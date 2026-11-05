import { cp, mkdir, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { frontendArtifactDigest } from '../../tools/frontend-artifacts.mjs';
import { previewServer } from '../../tools/serve-frontend.mjs';

const [action, directory, platformOrigin, publicOrigin] = process.argv.slice(2);
if (!directory) throw new Error('A launcher-owned fixture directory is required');
const interactive = process.env.ASSEMBLEAI_INTERACTIVE_DEMO === '1';
const portalRoot = interactive ? join(resolve(directory), 'portal') : resolve('apps/portal/dist');
if (action === 'prepare') {
  const root = join(resolve(directory), 'chatbi');
  await mkdir(root, { recursive: true });
  await cp(resolve('apps/chatbi/frontend/dist'), root, { recursive: true });
  if (interactive) {
    await cp(resolve('apps/portal/dist'), portalRoot, { recursive: true });
    // Identity, tenant/dataset names and the application description label the
    // synthetic fixture. Keep both application and portal DOM/CSS unchanged.
  }
  const { sha256 } = frontendArtifactDigest(root);
  await writeFile(join(directory, 'artifacts.json'), JSON.stringify({ artifacts: [{ appId: 'chatbi', version: '0.1.0', root, sha256 }] }));
} else if (action === 'serve') {
  const server = previewServer({ portalRoot, platformOrigin,
    frontendArtifactsPath: join(directory, 'artifacts.json') });
  server.listen(Number(new URL(publicOrigin).port), '127.0.0.1');
} else throw new Error('Unknown fixture action');
