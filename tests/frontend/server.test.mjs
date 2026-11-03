import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { join, dirname, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { createServer, request } from 'node:http';
import { previewServer } from '../../tools/serve-frontend.mjs';

test('V2/V19: release assets, deep links and source isolation', async (t) => {
  const root = await mkdtemp(join(tmpdir(), 'assembleai-preview-'));
  const portalRoot = join(root, 'portal');
  const sampleRoot = join(root, 'sample');
  await Promise.all([mkdir(portalRoot), mkdir(sampleRoot)]);
  await Promise.all([
    writeFile(join(portalRoot, 'index.html'), '<h1>Portal</h1>'),
    writeFile(join(sampleRoot, 'index.html'), '<h1>Sample</h1>'),
    writeFile(join(sampleRoot, 'sample.css'), '.sample{color:red}'),
    writeFile(join(root, 'secret.txt'), 'private'),
  ]);
  const server = previewServer({ portalRoot, sampleRoot });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => {
    await new Promise((resolve) => server.close(resolve));
    assert.equal(dirname(resolve(root)), resolve(tmpdir()));
    await rm(root, { recursive: true, force: true });
  });
  const base = `http://127.0.0.1:${server.address().port}`;
  assert.match(await (await fetch(`${base}/workspace/sample/details`)).text(), /Portal/);
  assert.match(await (await fetch(`${base}/applications/quality`)).text(), /Portal/);
  assert.match(await (await fetch(`${base}/applications/quality/run`)).text(), /Portal/);
  assert.match(await (await fetch(`${base}/manage/applications`)).text(), /Portal/);
  assert.match(await (await fetch(`${base}/manage/applications/quality`)).text(), /Portal/);
  assert.equal((await fetch(`${base}/manage/applications/quality/missing.js`)).status, 404);
  assert.match(await (await fetch(`${base}/reference-ui/`)).text(), /Portal/);
  assert.match(await (await fetch(`${base}/apps/sample/0.1.0/details`)).text(), /Sample/);
  const css = await fetch(`${base}/apps/sample/0.1.0/sample.css`);
  assert.match(css.headers.get('content-type'), /text\/css/);
  assert.equal((await fetch(`${base}/apps/sample/0.1.0/missing.js`)).status, 404);
  assert.equal((await fetch(`${base}/apps/sample/9.0.0/`)).status, 404);
  assert.equal((await fetch(`${base}/package.json`)).status, 404);
  assert.equal((await fetch(`${base}/`, { method: 'POST' })).status, 405);
  const traversalStatus = await new Promise((resolve, reject) => {
    const req = request(`${base}/`, { path: '/%2e%2e/secret.txt' }, (res) => { res.resume(); resolve(res.statusCode); });
    req.on('error', reject); req.end();
  });
  assert.equal(traversalStatus, 400);
});

async function listen(server, t) {
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => {
    server.closeAllConnections();
    await new Promise((resolve) => server.close(resolve));
  });
  return `http://127.0.0.1:${server.address().port}`;
}

function preview(platformOrigin) {
  return previewServer({ portalRoot: 'apps/portal/dist', sampleRoot: 'apps/sample/frontend/dist', platformOrigin });
}

test('T6: absent platform configuration is safe JSON and external targets are rejected', async (t) => {
  for (const value of ['https://127.0.0.1:5100', 'http://example.com', 'http://10.0.0.1',
    'http://127.0.0.1/private', 'http://user@127.0.0.1', 'http://127.0.0.1?secret=x',
    'http://127.0.0.1#x', 'http://127.1', 'http://2130706433', 'http://127.0.0.1:0',
    'http://127.0.0.1:65536']) assert.throws(() => preview(value), /platform origin/i);
  for (const value of ['http://127.0.0.1:5100', 'http://127.0.0.2:5100/', 'http://localhost:5100', 'http://[::1]:5100'])
    assert.doesNotThrow(() => preview(value));
  const base = await listen(preview(), t);
  for (const path of ['/bff/session', '/api/platform/v1/tenants', '/signin-oidc']) {
    const response = await fetch(base + path);
    assert.equal(response.status, 503);
    assert.equal(response.headers.get('cache-control'), 'no-store');
    assert.equal(response.headers.get('x-content-type-options'), 'nosniff');
    assert.equal((await response.json()).code, 'platform_not_configured');
  }
});

test('T6: same-origin proxy preserves BFF POST, redirects and cookies without forwarding identity hints', async (t) => {
  let received;
  let redirectFollowed = false;
  const backend = createServer(async (req, res) => {
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    received = { url: req.url, method: req.method, headers: req.headers, body: Buffer.concat(chunks).toString() };
    if (req.url === '/landing') redirectFollowed = true;
    res.writeHead(302, { Location: '/landing', 'Set-Cookie': ['ticket=opaque; HttpOnly; Path=/', 'correlation=nonce; HttpOnly; Path=/'],
      'Cache-Control': 'public, max-age=3600', 'X-Content-Type-Options': 'invalid' });
    res.end();
  });
  const target = await listen(backend, t);
  const base = await listen(preview(target), t);
  const response = await fetch(base + '/bff/tenant?flow=select', {
    method: 'POST', redirect: 'manual',
    headers: { Origin: base, Cookie: 'ticket=opaque', 'X-CSRF-TOKEN': 'csrf-test', 'Content-Type': 'application/json',
      Forwarded: 'for=evil', 'X-Forwarded-Host': 'evil.test', 'X-Forwarded-Proto': 'https', 'X-Forwarded-For': '203.0.113.7' },
    body: '{"tenantId":"00000000-0000-4000-8000-000000000001"}',
  });
  assert.equal(response.status, 302);
  assert.equal(response.headers.get('location'), '/landing');
  assert.deepEqual(response.headers.getSetCookie(), ['ticket=opaque; HttpOnly; Path=/', 'correlation=nonce; HttpOnly; Path=/']);
  assert.equal(response.headers.get('cache-control'), 'no-store');
  assert.equal(response.headers.get('x-content-type-options'), 'nosniff');
  assert.equal(received.url, '/bff/tenant?flow=select');
  assert.equal(received.method, 'POST');
  assert.equal(received.headers.origin, base);
  assert.equal(received.headers.cookie, 'ticket=opaque');
  assert.equal(received.headers['x-csrf-token'], 'csrf-test');
  assert.equal(received.headers.host, new URL(target).host);
  assert.equal(received.headers.forwarded, undefined);
  assert.equal(received.headers['x-forwarded-for'], undefined);
  assert.equal(received.headers['x-forwarded-host'], undefined);
  assert.equal(received.headers['x-forwarded-proto'], undefined);
  assert.equal(received.body, '{"tenantId":"00000000-0000-4000-8000-000000000001"}');
  assert.equal(redirectFollowed, false);
  const unknownApi = await fetch(base + '/api/unknown', { redirect: 'manual' });
  assert.equal(unknownApi.status, 302);
  assert.equal(received.url, '/api/unknown');
});

test('T6: proxy streams the first SSE segment and cancels the upstream on browser disconnect', { timeout: 10000 }, async (t) => {
  let stopped;
  const cancelled = new Promise((resolve) => { stopped = resolve; });
  const backend = createServer((req, res) => {
    res.writeHead(200, { 'Content-Type': 'text/event-stream' });
    res.write('event: delta\ndata: 首段\n\n');
    res.on('close', () => { if (!res.writableEnded) stopped(); });
  });
  const target = await listen(backend, t);
  const base = await listen(preview(target), t);
  const controller = new AbortController();
  const response = await fetch(base + '/api/apps/sample/releases/0.1.0/stream', { signal: controller.signal });
  assert.equal(response.status, 200);
  const reader = response.body.getReader();
  assert.match(new TextDecoder().decode((await reader.read()).value), /data: 首段/);
  controller.abort();
  await cancelled;
});

test('T6: unavailable upstream returns a bounded safe failure, never an exception or target', async (t) => {
  const backend = createServer();
  const target = await listen(backend, t);
  await new Promise((resolve) => backend.close(resolve));
  const base = await listen(preview(target), t);
  const response = await fetch(base + '/api/unknown?secret=not-for-errors');
  assert.equal(response.status, 502);
  assert.equal(response.headers.get('cache-control'), 'no-store');
  const text = await response.text();
  assert.equal(JSON.parse(text).code, 'platform_unavailable');
  assert.doesNotMatch(text, /ECONN|127\.0\.0\.1|not-for-errors|stack/i);
});

test('T6: browser protocol upgrades never reach the platform', { timeout: 10000 }, async (t) => {
  let reached = false;
  const backend = createServer((_req, res) => { reached = true; res.end(); });
  backend.on('upgrade', (_req, socket) => { reached = true; socket.destroy(); });
  const target = await listen(backend, t);
  const base = await listen(preview(target), t);
  const status = await new Promise((resolve, reject) => {
    const connection = request(base + '/api/apps/sample/stream', {
      headers: { Connection: 'Upgrade', Upgrade: 'websocket' },
    }, (response) => { response.resume(); resolve(response.statusCode); });
    connection.on('error', reject);
    connection.on('upgrade', (_response, socket) => { socket.destroy(); reject(new Error('Unexpected upgrade')); });
    connection.end();
  });
  assert.equal(status, 400);
  assert.equal(reached, false);
});

test('T7: preview library ignores inherited deployment config and rejects conflicting explicit sources', () => {
  const prior = process.env.ASSEMBLEAI_FRONTEND_ARTIFACTS;
  process.env.ASSEMBLEAI_FRONTEND_ARTIFACTS = 'not-a-file-do-not-use-for-fixed-preview-tests.json';
  try {
    assert.doesNotThrow(() => preview());
    assert.throws(() => previewServer({ portalRoot: 'apps/portal/dist', sampleRoot: 'apps/sample/frontend/dist',
      frontendArtifactsPath: process.env.ASSEMBLEAI_FRONTEND_ARTIFACTS }), /artifact/i);
    assert.throws(() => previewServer({ portalRoot: 'apps/portal/dist', frontendArtifacts: {}, frontendArtifactsPath: 'unused' }), /one frontend artifact/i);
  } finally {
    if (prior === undefined) delete process.env.ASSEMBLEAI_FRONTEND_ARTIFACTS;
    else process.env.ASSEMBLEAI_FRONTEND_ARTIFACTS = prior;
  }
});
