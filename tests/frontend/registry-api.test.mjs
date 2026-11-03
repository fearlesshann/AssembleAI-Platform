import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRegistryApi } from '../../apps/portal/src/registry-api.ts';
const json = value => new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } });

test('T8: PUT draft and tenant changes use fresh CSRF and exact revision without retry', async () => {
  const calls = [];
  const api = createRegistryApi(async (path, init) => {
    calls.push({ path, init });
    if (path === '/bff/csrf') return json({ headerName: 'X-CSRF-TOKEN', requestToken: 'fresh' });
    return new Response('private SQL must not reach errors', { status: 409 });
  });
  await assert.rejects(api.configureTenant('tenant', 'sample', '1.0.0', true, 7, new AbortController().signal), { status: 409 });
  assert.equal(calls.length, 2);
  assert.equal(calls[1].init.method, 'PUT');
  assert.equal(calls[1].init.headers['X-CSRF-TOKEN'], 'fresh');
  assert.deepEqual(JSON.parse(calls[1].init.body), { expectedRevision: 7, releaseVersion: '1.0.0', enabled: true });
  assert.equal(calls[1].init.credentials, 'same-origin');
  assert.equal(calls[1].init.redirect, 'error');
});
test('T8: cancellation during CSRF prevents the write and reads carry no browser identity headers', async () => {
  const controller = new AbortController(); let calls = 0;
  const api = createRegistryApi(async () => { calls++; controller.abort(); return json({ headerName: 'X-CSRF-TOKEN', requestToken: 'fresh' }); });
  await assert.rejects(api.transition('sample', '1.0.0', 'publish', 2, controller.signal));
  assert.equal(calls, 1);
  const reader = createRegistryApi(async (_path, init) => { assert.deepEqual(init.headers, { Accept: 'application/json' }); return json({ items: [] }); });
  assert.deepEqual(await reader.audit('sample', new AbortController().signal), { items: [] });
});
