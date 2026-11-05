import { cp, mkdir, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { frontendArtifactDigest } from '../../tools/frontend-artifacts.mjs';
import { previewServer } from '../../tools/serve-frontend.mjs';

const [action, directory, platformOrigin, publicOrigin] = process.argv.slice(2);
if (!directory) throw new Error('A launcher-owned fixture directory is required');
if (action === 'prepare') {
  const root = join(resolve(directory), 'knowledge');
  await mkdir(root, { recursive: true });
  await cp(resolve('apps/knowledge/frontend/dist'), root, { recursive: true });
  const { sha256 } = frontendArtifactDigest(root);
  await writeFile(join(directory, 'artifacts.json'), JSON.stringify({ artifacts: [{ appId: 'knowledge', version: '0.1.0', root, sha256 }] }));
} else if (action === 'serve') {
  const server = previewServer({ portalRoot: resolve('apps/portal/dist'), platformOrigin,
    frontendArtifactsPath: join(directory, 'artifacts.json') });
  server.listen(Number(new URL(publicOrigin).port), '127.0.0.1');
} else throw new Error('Unknown fixture action');
