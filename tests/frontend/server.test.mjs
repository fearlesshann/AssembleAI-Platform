import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { join, dirname, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { request } from 'node:http';
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
