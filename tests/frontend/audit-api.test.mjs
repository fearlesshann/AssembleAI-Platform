import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createAuditApi, auditQuery } from '../../apps/portal/src/audit-api.ts';
const filters = { scope: 'tenant', from: '2026-11-01T00:00:00.000Z', to: '2026-11-02T00:00:00.000Z', requestId: 'a:b+value', pageSize: 50 };
const json = value => new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } });
test('V25/V33: audit cursors preserve a fixed query without losing reserved characters', () => {
  const query = new URLSearchParams(auditQuery(filters, 'protected+/=cursor'));
  assert.equal(query.get('requestId'), filters.requestId); assert.equal(query.get('from'), filters.from);
  assert.equal(query.get('cursor'), 'protected+/=cursor'); assert.equal(query.get('scope'), 'tenant');
});
test('V12/V25: audit export obtains fresh CSRF, excludes pagination and never retries failure', async () => {
  const calls = [];
  const api = createAuditApi(async (path, init) => { calls.push({ path, init }); return path === '/bff/csrf' ? json({ headerName: 'X-CSRF-TOKEN', requestToken: 'fresh' }) : new Response('private database error', { status: 503 }); });
  await assert.rejects(api.export(filters, new AbortController().signal), e => e.status === 503 && !e.message.includes('private'));
  assert.equal(calls.length, 2); assert.equal(calls[1].init.headers['X-CSRF-TOKEN'], 'fresh');
  assert.equal(calls[1].init.method, 'POST'); assert.equal(calls[1].init.body, '{}');
  assert.equal(calls[1].init.credentials, 'same-origin'); assert.equal(calls[1].init.redirect, 'error');
  assert.equal(new URL(calls[1].path, 'http://fixture').searchParams.has('pageSize'), false);
});
test('V25: session abort during CSRF prevents an export request', async () => {
  const controller = new AbortController(); let count = 0;
  const api = createAuditApi(async () => { count++; controller.abort(); return json({ headerName: 'X-CSRF-TOKEN', requestToken: 'fresh' }); });
  await assert.rejects(api.export(filters, controller.signal)); assert.equal(count, 1);
});
test('V20/V33: audit refuses HTML, oversized or malformed metadata without surfacing it', async () => {
  for (const response of [new Response('private markup', { headers: { 'Content-Type': 'text/html' } }), json({ items: [], nextCursor: 12 }),
    json({ items: [{ id: 'bad', details: { secret: 'private' } }], nextCursor: null }), json({ items: [], nextCursor: null, padding: 'x'.repeat(4 * 1024 * 1024) })])
    await assert.rejects(createAuditApi(async () => response).events(filters, null, new AbortController().signal), e => !e.message.includes('private'));
});
test('V12: truncated export remains explicit instead of becoming a complete-data assertion', async () => {
  const api = createAuditApi(async path => path === '/bff/csrf' ? json({ headerName: 'X-CSRF-TOKEN', requestToken: 'fresh' }) : json({ items: [], truncated: true, exportedAt: filters.to }));
  const result = await api.export(filters, new AbortController().signal);
  assert.equal(result.truncated, true); assert.equal(result.exportedAt, filters.to);
});
