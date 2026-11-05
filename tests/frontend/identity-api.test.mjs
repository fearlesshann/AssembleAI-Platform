import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createIdentityApi } from '../../apps/portal/src/identity-api.ts';
const json = value => new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } });

test('T13: identity writes use fresh CSRF, exact CAS and no retry on conflicts', async () => {
  const calls = [];
  const api = createIdentityApi(async (path, init) => {
    calls.push({ path, init });
    return path === '/bff/csrf' ? json({ headerName: 'X-CSRF-TOKEN', requestToken: 'fresh' }) : new Response('private SQL', { status: 409 });
  });
  await assert.rejects(api.save('members', 'user', 'tenant', { expectedRevision: 4, enabled: false, administrator: false, roleIds: [] }, new AbortController().signal), { status: 409 });
  assert.equal(calls.length, 2);
  assert.equal(calls[1].path, '/api/platform/v1/identity-management/tenants/tenant/members/user');
  assert.equal(calls[1].init.headers['X-CSRF-TOKEN'], 'fresh');
  assert.equal(calls[1].init.credentials, 'same-origin');
  assert.equal(calls[1].init.redirect, 'error');
  assert.equal(JSON.parse(calls[1].init.body).expectedRevision, 4);
});
test('T13: cancelled CSRF prevents changes; malformed responses fail closed', async () => {
  const controller = new AbortController(); let calls = 0;
  const api = createIdentityApi(async () => { calls++; controller.abort(); return json({ headerName: 'X-CSRF-TOKEN', requestToken: 'fresh' }); });
  await assert.rejects(api.save('tenants', 'tenant', null, {}, controller.signal));
  assert.equal(calls, 1);
  const reader = createIdentityApi(async () => new Response('<html>login</html>', { headers: { 'Content-Type': 'text/html' } }));
  await assert.rejects(reader.workspace(new AbortController().signal), { status: 502 });
});

test('T13: only the allowlisted capacity code is read from a bounded 409 problem', async () => {
  const api = createIdentityApi(async () => new Response(JSON.stringify({ code: 'identity_capacity_reached', title: 'private SQL', detail: 'private database connection' }), { status: 409, headers: { 'Content-Type': 'application/problem+json; charset=utf-8' } }));
  await assert.rejects(api.workspace(new AbortController().signal), failure => {
    assert.equal(failure.status, 409);
    assert.equal(failure.code, 'identity_capacity_reached');
    assert.equal(failure.message, 'Identity management request could not complete.');
    assert.equal(JSON.stringify(failure).includes('private'), false);
    return true;
  });
});

test('T13: problem JSON over 4 KiB is cancelled and falls back to its safe HTTP status', async () => {
  let cancelled = false;
  const bytes = new TextEncoder().encode(JSON.stringify({ code: 'identity_capacity_reached', detail: '私'.repeat(1800) }));
  const body = new ReadableStream({ start(controller) { controller.enqueue(bytes.subarray(0, 4096)); controller.enqueue(bytes.subarray(4096)); }, cancel() { cancelled = true; } });
  const api = createIdentityApi(async () => new Response(body, { status: 409, headers: { 'Content-Type': 'application/problem+json' } }));
  await assert.rejects(api.workspace(new AbortController().signal), { status: 409, code: '' });
  assert.equal(cancelled, true);
});

test('T13: malformed, unexpected or non-409 error bodies never expose server text or arbitrary codes', async () => {
  for (const [status, contentType, body] of [
    [409, 'application/problem+json', '{private SQL'],
    [409, 'application/json', 'null'],
    [409, 'application/json', JSON.stringify({ code: 'private connection string' })],
    [409, 'text/html', JSON.stringify({ code: 'identity_capacity_reached' })],
    [503, 'application/problem+json', JSON.stringify({ code: 'identity_capacity_reached', detail: 'private SQL' })],
  ]) {
    const api = createIdentityApi(async () => new Response(body, { status, headers: { 'Content-Type': contentType } }));
    await assert.rejects(api.workspace(new AbortController().signal), failure => {
      assert.equal(failure.status, status); assert.equal(failure.code, '');
      assert.equal(failure.message, 'Identity management request could not complete.'); return true;
    });
  }
});
