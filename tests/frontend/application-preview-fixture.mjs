import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve, relative, isAbsolute } from 'node:path';
import { frontendArtifactDigest, createFrontendArtifacts } from '../../tools/frontend-artifacts.mjs';
import { previewServer } from '../../tools/serve-frontend.mjs';

// Invoked by the marked PostgreSQL fixture. Build outputs stay in its owned
// run directory; a second app uses the same source and independent build inputs.
const [mode, directoryArgument, platformOrigin, publicOrigin] = process.argv.slice(2);
const directory = resolve(directoryArgument);
const local = relative(resolve('.local-data/postgres-tests'), directory);
assert.ok(local && !local.startsWith('..') && !isAbsolute(local));
assert.ok(['prepare', 'serve'].includes(mode));
const configurationPath = resolve(directory, 'artifacts.json');

async function build(appId, version, root) {
  const child = spawn(process.execPath, [resolve('node_modules/vite/bin/vite.js'), 'build', '--outDir', root], {
    cwd: resolve('apps/sample/frontend'), windowsHide: true, stdio: 'inherit',
    env: { ...process.env, ASSEMBLEAI_APP_ID: appId, ASSEMBLEAI_FRONTEND_VERSION: version },
  });
  await new Promise((accept, reject) => {
    child.once('error', reject);
    child.once('exit', code => code === 0 ? accept() : reject(new Error('Application fixture build failed.')));
  });
}

if (mode === 'prepare') {
  await mkdir(directory, { recursive: true });
  const artifacts = [];
  for (const [appId, version] of [['sample', '0.1.0'], ['sample-clone', '0.2.0']]) {
    const root = resolve(directory, appId);
    await build(appId, version, root);
    artifacts.push({ appId, version, root, sha256: frontendArtifactDigest(root).sha256 });
  }
  await writeFile(configurationPath, JSON.stringify({ artifacts }), 'utf8');
} else {
  const publicAddress = new URL(publicOrigin);
  assert.equal(publicAddress.hostname, '127.0.0.1');
  assert.equal(publicAddress.protocol, 'http:');
  const frontendArtifacts = createFrontendArtifacts(JSON.parse(await readFile(configurationPath, 'utf8')));
  const server = previewServer({ portalRoot: resolve('apps/portal/dist'), platformOrigin, frontendArtifacts });
  server.listen(Number(publicAddress.port), '127.0.0.1', () => process.stdout.write('Application preview ready.\n'));
  process.once('SIGTERM', () => server.close(() => process.exit(0)));
}
