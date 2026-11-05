import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createResourceApi } from '../../apps/portal/src/resource-api.ts';
const json = value => new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } });
test('T13: resource writes acquire fresh CSRF and never retry CAS failures', async () => {
  const calls = []; const api = createResourceApi(async (path, init) => { calls.push({ path, init }); return path === '/bff/csrf' ? json({ headerName: 'X-CSRF-TOKEN', requestToken: 'fresh' }) : new Response('private', { status: 409 }); });
  await assert.rejects(api.save('models', 'tenant', 'local.v1', { enabled: true, maxOutputTokens: 256, expectedRevision: 7 }, new AbortController().signal), { status: 409 });
  assert.equal(calls.length, 2); assert.equal(calls[1].path, '/api/platform/v1/tenants/tenant/models/local.v1/configuration');
  assert.equal(calls[1].init.headers['X-CSRF-TOKEN'], 'fresh'); assert.equal(calls[1].init.credentials, 'same-origin'); assert.equal(calls[1].init.redirect, 'error');
  assert.equal(JSON.parse(calls[1].init.body).expectedRevision, 7);
});
test('T13: model and connector absent configuration normalize to null', async () => {
  const api = createResourceApi(async path => json(path.includes('/models/') ? null : { configuration: null }));
  for (const section of ['models', 'data-sources']) assert.equal(await api.configuration(section, 'tenant', 'source', new AbortController().signal), null);
});
test('T13: aborted CSRF blocks resource side effects and invalid transport stays safe', async () => {
  const controller = new AbortController(); let calls = 0;
  const api = createResourceApi(async () => { calls++; controller.abort(); return json({ headerName: 'X-CSRF-TOKEN', requestToken: 'fresh' }); });
  await assert.rejects(api.testSource('tenant', 'source', controller.signal)); assert.equal(calls, 1);
  for (const response of [new Response('<html>private</html>', { headers: { 'Content-Type': 'text/html' } }), new Response('private SQL', { status: 503 })]) {
    await assert.rejects(createResourceApi(async () => response).models('tenant', new AbortController().signal), e => !e.message.includes('private'));
  }
});
test('T13: large or malformed successful JSON is rejected without surfacing content', async () => {
  for (const body of ['invalid private', JSON.stringify({ items: 'invalid' }), JSON.stringify({ items: [], padding: 'x'.repeat(4 * 1024 * 1024) })]) {
    await assert.rejects(createResourceApi(async () => new Response(body, { headers: { 'Content-Type': 'application/json' } })).models('tenant', new AbortController().signal));
  }
});
test('T13: malformed resource capabilities and limits fail closed before UI uses them', async () => {
  const model = { modelId: 'local.v1', displayName: 'Local model', enabled: true, canManage: true, canInvoke: true, deploymentEnabled: true, maximumOutputTokens: 128, deploymentMaximumOutputTokens: 256, callTimeoutSeconds: 120 };
  for (const change of [{ canInvoke: 'true' }, { callTimeoutSeconds: null }, { callTimeoutSeconds: 601 }, { maximumOutputTokens: 257 }, { deploymentMaximumOutputTokens: 1.5 }]) {
    const api = createResourceApi(async () => json({ items: [{ ...model, ...change }] }));
    await assert.rejects(api.models('tenant', new AbortController().signal), { status: 502 });
  }
  const good = await createResourceApi(async () => json({ items: [model] })).models('tenant', new AbortController().signal);
  assert.equal(good.items[0].callTimeoutSeconds, 120);
});
