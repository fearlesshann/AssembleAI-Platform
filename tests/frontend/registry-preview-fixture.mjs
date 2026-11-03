import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve, relative, isAbsolute } from 'node:path';
import { frontendArtifactDigest, createFrontendArtifacts } from '../../tools/frontend-artifacts.mjs';
import { previewServer } from '../../tools/serve-frontend.mjs';

const [mode, directoryArgument, ...arguments_] = process.argv.slice(2);
const directory = resolve(directoryArgument);
const local = relative(resolve('.local-data/postgres-tests'), directory);
assert.ok(local && !local.startsWith('..') && !isAbsolute(local));
const configurationPath = resolve(directory, 'artifacts.json');
if (mode === 'prepare') {
  const [appId] = arguments_;
  assert.match(appId, /^registry-browser-[a-f0-9]{32}$/);
  await mkdir(directory, { recursive: true });
  const artifacts = [];
  for (const version of ['1.0.0', '2.0.0']) {
    const root = resolve(directory, version);
    const child = spawn(process.execPath, [resolve('node_modules/vite/bin/vite.js'), 'build', '--outDir', root], {
      cwd: resolve('apps/sample/frontend'), windowsHide: true, stdio: 'inherit',
      env: { ...process.env, ASSEMBLEAI_APP_ID: appId, ASSEMBLEAI_FRONTEND_VERSION: version },
    });
    await new Promise((accept, reject) => { child.once('error', reject); child.once('exit', code => code === 0 ? accept() : reject(new Error('Registry fixture artifact build failed.'))); });
    artifacts.push({ appId, version, root, sha256: frontendArtifactDigest(root).sha256 });
  }
  await writeFile(configurationPath, JSON.stringify({ artifacts }), 'utf8');
} else {
  assert.equal(mode, 'serve');
  const [platformOrigin, publicOrigin] = arguments_;
  const address = new URL(publicOrigin);
  assert.equal(address.protocol, 'http:'); assert.equal(address.hostname, '127.0.0.1');
  const frontendArtifacts = createFrontendArtifacts(JSON.parse(await readFile(configurationPath, 'utf8')));
  const server = previewServer({ portalRoot: resolve('apps/portal/dist'), platformOrigin, frontendArtifacts });
  server.listen(Number(address.port), '127.0.0.1', () => process.stdout.write('Registry preview ready.\n'));
  process.once('SIGTERM', () => server.close(() => process.exit(0)));
}
