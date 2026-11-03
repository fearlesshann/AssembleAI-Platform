import { test } from 'node:test';
import assert from 'node:assert/strict';

const sdk = await import('../../packages/app-sdk/src/application-api.ts');
const binding = () => ({ runId: '11111111-1111-4111-8111-111111111111', tenantId: '22222222-2222-4222-8222-222222222222', appId: 'sample', releaseVersion: '1.2.3', expiresAt: new Date(Date.now() + 60000).toISOString() });
const json = (value) => new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } });
const csrf = () => json({ headerName: 'X-CSRF-TOKEN', requestToken: 'csrf-value' });
const eventResponse = (text, split = false, keepOpen = false, cancelled = () => {}) => new Response(new ReadableStream({
  start(controller) {
    const bytes = new TextEncoder().encode(text);
    for (const chunk of split ? [...bytes].map((b) => Uint8Array.of(b)) : [bytes]) controller.enqueue(chunk);
    if (!keepOpen) controller.close();
  }, cancel: cancelled,
}), { headers: { 'Content-Type': 'text/event-stream; charset=utf-8' } });
const collect = async (stream) => { const result = []; for await (const event of stream) result.push(event); return result; };
const make = (fetch, run = binding()) => {
  assert.equal(typeof sdk.createApplicationClient, 'function', 'SDK must expose a controlled application client');
  return sdk.createApplicationClient(run, { fetch });
};

test('T7: fixed release/run/tenant and CSRF survive caller overrides and binding mutation', async () => {
  const requests = [];
  const run = binding();
  const client = make(async (path, init) => { requests.push({ path, init }); return path === '/bff/csrf' ? csrf() : json({ accepted: true }); }, run);
  try {
    run.tenantId = 'changed'; run.appId = 'changed';
    assert.deepEqual(await client.api.json('probe', { method: 'POST', body: { message: '验证调用' }, headers: { 'X-Tenant-ID': 'attacker', Authorization: 'token' }, baseUrl: 'https://evil.example' }), { accepted: true });
    assert.equal(requests.length, 2);
    const { path, init } = requests[1];
    assert.equal(path, '/api/apps/sample/releases/1.2.3/probe');
    assert.equal(init.headers['X-Tenant-ID'], '22222222-2222-4222-8222-222222222222');
    assert.equal(init.headers['X-Run-ID'], run.runId);
    assert.equal(init.headers['X-CSRF-TOKEN'], 'csrf-value');
    assert.equal(init.headers.Authorization, undefined);
    assert.equal(init.credentials, 'same-origin'); assert.equal(init.redirect, 'error');
    assert.equal(Object.isFrozen(client.api), true);
  } finally { client.dispose(); }
});

test('T7: reject URL/path escapes and invalid payloads before any network request', async () => {
  let calls = 0; const client = make(async () => { calls++; return json({}); });
  try {
    for (const operation of ['https://evil.example', '../probe', '/probe', 'probe?x=1', 'probe#x', 'a/%2e%2e/b', 'a//b', 'a\\b']) {
      await assert.rejects(client.api.json(operation), { code: 'invalid_request' });
    }
    await assert.rejects(client.api.json('probe', { body: {} }), { code: 'invalid_request' });
    await assert.rejects(client.api.json('probe', { method: 'DELETE' }), { code: 'invalid_request' });
    await assert.rejects(client.api.json('probe', { method: 'POST', body: '字'.repeat(400000) }), { code: 'payload_too_large' });
    assert.equal(calls, 0);
  } finally { client.dispose(); }
});

test('T7: fresh CSRF is required and failed writes are never retried', async () => {
  let calls = 0; const client = make(async () => { calls++; return json({ headerName: 'Bad', requestToken: 'secret' }); });
  try { await assert.rejects(client.api.json('probe', { method: 'POST', body: {} }), { code: 'invalid_response' }); assert.equal(calls, 1); }
  finally { client.dispose(); }
});

test('T7: JSON response size/content are bounded and errors do not expose response text', async () => {
  for (const [response, code] of [[new Response('private stack trace', { status: 500 }), 'request_failed'], [new Response('[]', { headers: { 'Content-Type': 'text/html' } }), 'invalid_response'], [json('x'.repeat(1048576)), 'invalid_response']]) {
    const client = make(async () => response);
    try {
      await assert.rejects(client.api.json('probe'), (error) => error.code === code && !error.message.includes('private'));
    } finally { client.dispose(); }
  }
});

test('T7: stream decodes UTF-8 split bytes/CRLF and completes without waiting for transport EOF', async () => {
  let cancelled = false;
  const client = make(async () => eventResponse('\ufeff: heartbeat\r\nevent: delta\r\ndata: {"text":"你好"}\r\n\r\nevent: complete\r\ndata: {"ok":true}\r\n\r\n', true, true, () => { cancelled = true; }));
  try {
    assert.deepEqual(await collect(client.api.stream('events', { query: { count: '3' } })), [{ event: 'delta', data: { text: '你好' } }, { event: 'complete', data: { ok: true } }]);
    assert.equal(cancelled, true);
  } finally { client.dispose(); }
});

test('T7: stream error, malformed event, oversize frame and missing complete all fail closed', async () => {
  for (const [body, code] of [
    ['event: delta\ndata: {"text":"partial"}\n\n', 'stream_interrupted'],
    ['event: error\ndata: {"message":"private stack trace"}\n\n', 'stream_error'],
    ['event: mystery\ndata: {}\n\n', 'stream_protocol_error'],
    ['event: delta\ndata: not-json\n\n', 'stream_protocol_error'],
    [`event: delta\ndata: "${'a'.repeat(65536)}"\n\n`, 'stream_protocol_error'],
  ]) {
    const client = make(async () => eventResponse(body));
    try { await assert.rejects(collect(client.api.stream('events')), (e) => e.code === code && !e.message.includes('private')); }
    finally { client.dispose(); }
  }
});

test('T7: stopping iteration cancels the reader and disposable lifetime cannot be revived', async () => {
  let cancelled = false; let signal;
  const client = make(async (_path, init) => { signal = init.signal; return eventResponse('event: delta\ndata: {}\n\n', false, true, () => { cancelled = true; }); });
  for await (const event of client.api.stream('events')) { assert.equal(event.event, 'delta'); break; }
  assert.equal(cancelled, true); assert.equal(signal.aborted, true);
  client.dispose();
  await assert.rejects(client.api.json('probe'), { code: 'disposed' });
});

test('T7: dispose aborts in-flight fetch and caller cancellation does not dispose other requests', async () => {
  const client = make(async (_path, init) => new Promise((_resolve, reject) => { init.signal.addEventListener('abort', () => reject(init.signal.reason), { once: true }); }));
  const controller = new AbortController();
  const first = client.api.json('probe', { signal: controller.signal });
  const second = client.api.json('probe');
  controller.abort(); await assert.rejects(first, { code: 'cancelled' });
  client.dispose(); await assert.rejects(second, { code: 'disposed' });
});

const standalone = await import('../../apps/sample/frontend/src/standalone.ts');
test('T7: standalone initialization reads session/selection without implicitly selecting or starting a run', async () => {
  assert.equal(typeof standalone.createStandalonePlatform, 'function');
  const paths = [];
  const platform = standalone.createStandalonePlatform('sample', '0.1.0', async (path, init) => {
    paths.push(path); assert.equal(init.method, 'GET');
    return json(path === '/bff/session' ? { iss: 'https://id.example', sub: 'subject', name: 'Operator' }
      : path === '/bff/tenant' ? { tenantId: null } : { items: [{ tenantId: binding().tenantId, name: 'Factory' }] });
  });
  assert.equal((await platform.inspect(new AbortController().signal)).tenantId, null);
  assert.deepEqual(paths.sort(), ['/api/platform/v1/tenants', '/bff/session', '/bff/tenant']);
});

test('T7: standalone connect rejects a run for a different artifact or selected tenant', async () => {
  assert.equal(typeof standalone.createStandalonePlatform, 'function');
  for (const change of ['artifact', 'tenant']) {
    const platform = standalone.createStandalonePlatform('sample', '0.1.0', async (path) => {
      if (path === '/bff/csrf') return csrf();
      if (path.endsWith('/catalog/sample')) return json({ appId: 'sample', releaseVersion: '1.2.3' });
      const run = { ...binding(), apiBase: '/api/apps/sample/releases/1.2.3/', manifest: { appId: 'sample', version: '1.2.3', frontendEntry: '/apps/sample/0.1.0/', frontendArtifact: { version: '0.1.0' } } };
      if (change === 'artifact') run.manifest.frontendEntry = '/apps/sample/0.2.0/';
      else run.tenantId = '33333333-3333-4333-8333-333333333333';
      return json(run);
    });
    await assert.rejects(platform.connect(binding().tenantId, new AbortController().signal), (error) => error.code === (change === 'artifact' ? 'version_changed' : 'invalid_response'));
  }
});

test('T7: SSE bounds count CRLF wire bytes as part of the frame', async () => {
  const client = make(async () => eventResponse(`${':\r\n'.repeat(22000)}event: complete\r\ndata: {}\r\n\r\n`));
  try { await assert.rejects(collect(client.api.stream('events')), { code: 'stream_protocol_error' }); }
  finally { client.dispose(); }
});

test('T7: invalid UTF-8 cannot be replaced silently in JSON or SSE', async () => {
  const bytes = new Uint8Array([...new TextEncoder().encode('event: delta\ndata: "'), 0xc3, 0x28, ...new TextEncoder().encode('"\n\nevent: complete\ndata: {}\n\n')]);
  const client = make(async () => new Response(bytes, { headers: { 'Content-Type': 'text/event-stream' } }));
  try { await assert.rejects(collect(client.api.stream('events')), { code: 'stream_protocol_error' }); }
  finally { client.dispose(); }
  const invalidJson = make(async () => new Response(Uint8Array.of(34, 0xc3, 0x28, 34), { headers: { 'Content-Type': 'application/json' } }));
  try { await assert.rejects(invalidJson.api.json('probe'), { code: 'invalid_response' }); }
  finally { invalidJson.dispose(); }
});

test('T7: cancellation during CSRF retrieval prevents the POST and untrusted query is only data', async () => {
  const controller = new AbortController(); let calls = 0;
  const cancelled = make(async () => { calls++; controller.abort(); return csrf(); });
  try { await assert.rejects(cancelled.api.json('probe', { method: 'POST', body: {}, signal: controller.signal }), { code: 'cancelled' }); assert.equal(calls, 1); }
  finally { cancelled.dispose(); }
  let target;
  const query = make(async (path) => { target = path; return json({ ok: true }); });
  try { await query.api.json('probe', { query: { next: 'https://evil.example/../?a=1#fragment' } }); assert.equal(target, '/api/apps/sample/releases/1.2.3/probe?next=https%3A%2F%2Fevil.example%2F..%2F%3Fa%3D1%23fragment'); }
  finally { query.dispose(); }
});

test('T7: disposal interrupts an idle stream reader and an expired lease cannot send a request', async () => {
  let ready; const started = new Promise((resolve) => { ready = resolve; });
  const client = make(async () => { ready(); return eventResponse('', false, true); });
  const iterator = client.api.stream('events')[Symbol.asyncIterator]();
  const pending = iterator.next(); await started; await Promise.resolve(); client.dispose();
  await assert.rejects(pending, { code: 'disposed' });
  let now = Date.now(); let calls = 0;
  const run = binding();
  const expiring = sdk.createApplicationClient(run, { clock: () => now, fetch: async () => { calls++; return json({}); } });
  try { now = Date.parse(run.expiresAt) + 1; await assert.rejects(expiring.api.json('probe'), { code: 'expired' }); assert.equal(calls, 0); }
  finally { expiring.dispose(); }
});

test('T7: cancellation after a delta cannot emit complete from the already buffered chunk', async () => {
  for (const reason of ['cancelled', 'disposed']) {
    const controller = new AbortController();
    const client = make(async () => eventResponse('event: delta\ndata: {"text":"partial"}\n\nevent: complete\ndata: {}\n\n'));
    const stream = client.api.stream('events', { signal: controller.signal })[Symbol.asyncIterator]();
    try {
      assert.equal((await stream.next()).value.event, 'delta');
      if (reason === 'cancelled') controller.abort(); else client.dispose();
      await assert.rejects(stream.next(), { code: reason });
    } finally { await stream.return?.(); client.dispose(); }
  }
});

test('T7: SDK and standalone versions follow the platform type and 64-character bound', () => {
  const overlong = `${'1'.repeat(63)}.0.0`;
  let client;
  try { assert.throws(() => { client = sdk.createApplicationClient({ ...binding(), releaseVersion: overlong }); }, { code: 'invalid_binding' }); }
  finally { client?.dispose(); }
  assert.throws(() => standalone.createStandalonePlatform('sample', overlong), { code: 'invalid_response' });
  for (const invalid of [null, undefined, 123]) {
    assert.throws(() => sdk.createApplicationClient({ ...binding(), releaseVersion: invalid }), { code: 'invalid_binding' });
    assert.throws(() => standalone.createStandalonePlatform('sample', invalid), { code: 'invalid_response' });
  }
});
