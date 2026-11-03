import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { createServer, request } from 'node:http';
import { mkdtemp, mkdir, writeFile, rm, symlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { frontendArtifactDigest, frontendArtifactEntry, createFrontendArtifacts, loadFrontendArtifacts } from '../../tools/frontend-artifacts.mjs';
import { previewServer } from '../../tools/serve-frontend.mjs';

async function fixture(t) {
  const directory = await mkdtemp(join(tmpdir(), 'assembleai-artifacts-'));
  const root = join(directory, 'release');
  await mkdir(join(root, 'assets'), { recursive: true });
  await writeFile(join(root, 'index.html'), '<h1>Released application</h1>');
  await writeFile(join(root, 'assets', 'entry.js'), 'window.release = "1.2.3";');
  t.after(async () => {
    assert.equal(dirname(resolve(directory)), resolve(tmpdir()));
    assert.ok(resolve(directory).startsWith(resolve(tmpdir(), 'assembleai-artifacts-')));
    await rm(directory, { recursive: true, force: true });
  });
  const artifact = () => ({ appId: 'new-quality-app', version: '1.2.3', root, sha256: frontendArtifactDigest(root).sha256 });
  return { directory, root, artifact };
}
async function listen(t, server) {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
  return `http://127.0.0.1:${server.address().port}`;
}
function rawStatus(base, path) {
  return new Promise((resolve, reject) => {
    const call = request(base, { path }, response => { response.resume(); resolve(response.statusCode); });
    call.on('error', reject); call.end();
  });
}

test('T7: deterministic domain-separated directory digest binds paths and bytes', async t => {
  const f = await fixture(t);
  const a = frontendArtifactDigest(f.root);
  assert.equal(a.fileCount, 2);
  const files = [
    ['assets/entry.js', 'window.release = "1.2.3";'],
    ['index.html', '<h1>Released application</h1>'],
  ].map(([path, contents]) => ({ path, size: Buffer.byteLength(contents), sha256: createHash('sha256').update(contents).digest('hex') }));
  assert.equal(a.sha256, createHash('sha256').update('assembleai-frontend-artifact-v1\n').update(JSON.stringify(files), 'utf8').digest('hex'));
  assert.deepEqual(frontendArtifactDigest(f.root), a);
  await writeFile(join(f.root, 'assets', 'entry.js'), 'window.release = "changed";');
  assert.notEqual(frontendArtifactDigest(f.root).sha256, a.sha256);
  assert.throws(() => createFrontendArtifacts({ artifacts: [{ ...f.artifact(), sha256: a.sha256 }] }), /artifact/i);
});

test('T7: configured releases and independent deep links work without platform code changes', async t => {
  const f = await fixture(t);
  const snapshot = createFrontendArtifacts({ artifacts: [f.artifact()] });
  const base = await listen(t, previewServer({ portalRoot: f.root, sampleRoot: 'ignored', frontendArtifacts: snapshot }));
  assert.match(await (await fetch(base + '/apps/new-quality-app/1.2.3/')).text(), /Released application/);
  assert.match(await (await fetch(base + '/apps/new-quality-app/1.2.3/details/line-a')).text(), /Released application/);
  const script = await fetch(base + '/apps/new-quality-app/1.2.3/assets/entry.js?cache=v1');
  assert.match(script.headers.get('content-type'), /javascript/);
  assert.equal(script.headers.get('x-content-type-options'), 'nosniff');
  assert.equal((await fetch(base + '/apps/new-quality-app/1.2.3/missing.js')).status, 404);
  assert.equal((await fetch(base + '/apps/new-quality-app/9.0.0/')).status, 404);
  assert.equal((await fetch(base + '/apps/sample/0.1.0/')).status, 404);
  assert.equal((await fetch(base + '/apps/new-quality-app/1.2.3/', { method: 'POST' })).status, 405);
  const head = await fetch(base + '/apps/new-quality-app/1.2.3/assets/entry.js', { method: 'HEAD' });
  assert.equal(head.status, 200); assert.equal(await head.text(), '');
  assert.equal(snapshot.artifacts[0].entry, '/apps/new-quality-app/1.2.3/');
  assert.ok(!JSON.stringify(snapshot.artifacts).includes(f.root));
});

test('T7: served bytes remain the validated snapshot after disk mutation or deletion', async t => {
  const f = await fixture(t);
  const snapshot = createFrontendArtifacts({ artifacts: [f.artifact()] });
  const base = await listen(t, createServer((req, res) => { if (!snapshot.handle(req, res)) { res.writeHead(404); res.end(); } }));
  await writeFile(join(f.root, 'assets', 'entry.js'), 'secret-after-validation');
  await rm(join(f.root, 'index.html'));
  assert.equal(await (await fetch(base + '/apps/new-quality-app/1.2.3/assets/entry.js')).text(), 'window.release = "1.2.3";');
  assert.match(await (await fetch(base + '/apps/new-quality-app/1.2.3/')).text(), /Released application/);
});

test('T7: raw path encodings, Windows paths and hidden/source requests cannot escape an artifact', async t => {
  const f = await fixture(t);
  const snapshot = createFrontendArtifacts({ artifacts: [f.artifact()] });
  const base = await listen(t, createServer((req, res) => { if (!snapshot.handle(req, res)) { res.writeHead(404); res.end(); } }));
  for (const path of ['/apps/new-quality-app/1.2.3/../secret.txt', '/apps/new-quality-app/1.2.3/%2e%2e/secret.txt',
    '/apps/new-quality-app/1.2.3/%252e%252e/secret.txt', '/apps/new-quality-app/1.2.3/assets%2fentry.js',
    '/apps/new-quality-app/1.2.3/assets\\entry.js', '/apps/new-quality-app/1.2.3/index.html:secret',
    '/apps/new-quality-app/1.2.3/%00', '/apps/new-quality-app/1.2.3/.env', '/apps/new-quality-app/1.2.3/src/main.ts'])
    assert.ok([400, 404].includes(await rawStatus(base, path)), path);
});

test('T7: publishing rejects source maps, source trees, links and unbounded files', async t => {
  const f = await fixture(t);
  for (const name of ['entry.js.map', '.env', 'main.ts', 'package.json']) {
    await writeFile(join(f.root, name), 'private');
    assert.throws(() => frontendArtifactDigest(f.root), /artifact/i);
    await rm(join(f.root, name));
  }
  const outside = join(f.directory, 'outside');
  await mkdir(outside);
  await writeFile(join(outside, 'secret.js'), 'private');
  await symlink(outside, join(f.root, 'linked'), 'junction');
  assert.throws(() => frontendArtifactDigest(f.root), /artifact/i);
  await rm(join(f.root, 'linked'));
  await writeFile(join(f.root, 'large.js'), Buffer.alloc(16 * 1024 * 1024 + 1));
  assert.throws(() => frontendArtifactDigest(f.root), /artifact/i);
});

test('T7: deployment config is strict, bound to its own directory and cannot add implicit sample access', async t => {
  const f = await fixture(t);
  const path = join(f.directory, 'artifacts.json');
  await writeFile(path, JSON.stringify({ artifacts: [{ ...f.artifact(), root: 'release' }] }));
  const snapshot = loadFrontendArtifacts(path);
  assert.equal(snapshot.artifacts[0].appId, 'new-quality-app');
  await writeFile(path, '\uFEFF' + JSON.stringify({ artifacts: [{ ...f.artifact(), root: 'release' }] }));
  assert.equal(loadFrontendArtifacts(path).artifacts[0].appId, 'new-quality-app');
  for (const config of [null, { artifacts: [], extra: true }, { artifacts: [f.artifact(), f.artifact()] },
    { artifacts: [{ ...f.artifact(), appId: '../sample' }] }, { artifacts: [{ ...f.artifact(), version: '01.2.3' }] },
    { artifacts: [{ ...f.artifact(), entry: 'https://evil.test/' }] }, { artifacts: [{ ...f.artifact(), sha256: 'a'.repeat(64) }] }])
    assert.throws(() => createFrontendArtifacts(config), /artifact/i);
  const empty = createFrontendArtifacts({ artifacts: [] });
  const base = await listen(t, previewServer({ portalRoot: f.root, sampleRoot: f.root, frontendArtifacts: empty }));
  assert.equal((await fetch(base + '/apps/sample/0.1.0/')).status, 404);
});

test('T7: CLI produces real deployment and Registry metadata without changing the release', async t => {
  const f = await fixture(t);
  const before = frontendArtifactDigest(f.root);
  const output = JSON.parse(execFileSync(process.execPath,
    [fileURLToPath(new URL('../../tools/frontend-artifacts.mjs', import.meta.url)), 'new-quality-app', '1.2.3', f.root], { encoding: 'utf8' }));
  assert.deepEqual(output, { ...f.artifact(), registry: {
    appId: 'new-quality-app', version: '1.2.3', entry: '/apps/new-quality-app/1.2.3/', sha256: before.sha256,
  } });
  assert.deepEqual(frontendArtifactDigest(f.root), before);
});

test('T7: root junctions, source directories, Windows aliases and excessive release counts are rejected', async t => {
  const f = await fixture(t);
  await symlink(f.root, join(f.directory, 'root-link'), 'junction');
  assert.throws(() => frontendArtifactDigest(join(f.directory, 'root-link')), /artifact/i);
  await mkdir(join(f.root, 'src'));
  await writeFile(join(f.root, 'src', 'private.js'), 'private');
  assert.throws(() => frontendArtifactDigest(f.root), /artifact/i);
  await rm(join(f.root, 'src', 'private.js'));
  await rm(join(f.root, 'src'), { recursive: true });
  assert.throws(() => createFrontendArtifacts({ artifacts: Array.from({ length: 129 }, () => f.artifact()) }), /artifact/i);
  const snapshot = createFrontendArtifacts({ artifacts: [f.artifact()] });
  const base = await listen(t, createServer((req, res) => { if (!snapshot.handle(req, res)) { res.writeHead(404); res.end(); } }));
  for (const path of ['/apps/new-quality-app/1.2.3/con', '/apps/new-quality-app/1.2.3/NUL.txt',
    '/apps/new-quality-app/1.2.3/index.html.', '/%61pps/new-quality-app/1.2.3/index.html', '/apps%2fnew-quality-app/1.2.3/index.html'])
    assert.ok([400, 404].includes(await rawStatus(base, path)), path);
});

test('T7: artifact identities use the same stable x.y.z versions as the Registry', () => {
  for (const version of ['1.0.0-beta', '1.0.0+build', '01.0.0', 'v1.0.0', '1.0', '1.0.0.0', '1'.repeat(61) + '.0.0'])
    assert.throws(() => frontendArtifactEntry('sample', version), /artifact/i, version);
  assert.equal(frontendArtifactEntry('sample', '1.20.300'), '/apps/sample/1.20.300/');
});
