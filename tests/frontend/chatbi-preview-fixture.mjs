import { cp, mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { frontendArtifactDigest } from '../../tools/frontend-artifacts.mjs';
import { previewServer } from '../../tools/serve-frontend.mjs';

const [action, directory, platformOrigin, publicOrigin] = process.argv.slice(2);
if (!directory) throw new Error('A launcher-owned fixture directory is required');
const interactive = process.env.ASSEMBLEAI_INTERACTIVE_DEMO === '1';
const portalRoot = interactive ? join(resolve(directory), 'portal') : resolve('apps/portal/dist');
async function addExperienceNotice(root) {
  const filename = join(root, 'index.html');
  const html = await readFile(filename, 'utf8');
  const body = /<body(?:\s[^>]*)?>/i;
  if (!body.test(html)) throw new Error('The experience requires an HTML body');
  const notice = '<aside role="note" data-local-experience="true" style="position:sticky;top:0;z-index:2147483647;box-sizing:border-box;padding:8px 16px;background:#fff4d5;color:#604500;border-bottom:1px solid #e7ce85;text-align:center;font:13px/1.5 system-ui,sans-serif">本机体验 · 制造业测试数据 · 仅支持三个示例问题（固定规划）· 未接真实模型</aside>';
  await writeFile(filename, html.replace(body, (tag) => `${tag}\n${notice}\n`));
}
if (action === 'prepare') {
  const root = join(resolve(directory), 'chatbi');
  await mkdir(root, { recursive: true });
  await cp(resolve('apps/chatbi/frontend/dist'), root, { recursive: true });
  if (interactive) {
    await addExperienceNotice(root);
    await cp(resolve('apps/portal/dist'), portalRoot, { recursive: true });
    await addExperienceNotice(portalRoot);
  }
  const { sha256 } = frontendArtifactDigest(root);
  await writeFile(join(directory, 'artifacts.json'), JSON.stringify({ artifacts: [{ appId: 'chatbi', version: '0.1.0', root, sha256 }] }));
} else if (action === 'serve') {
  const server = previewServer({ portalRoot, platformOrigin,
    frontendArtifactsPath: join(directory, 'artifacts.json') });
  server.listen(Number(new URL(publicOrigin).port), '127.0.0.1');
} else throw new Error('Unknown fixture action');
