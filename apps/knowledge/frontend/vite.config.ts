import { defineConfig, type Plugin } from 'vite';
import vue from '@vitejs/plugin-vue';
import { fileURLToPath } from 'node:url';
import { frontendArtifactEntry } from '../../../tools/frontend-artifacts.mjs';

const appId = process.env.ASSEMBLEAI_APP_ID ?? 'knowledge';
const version = process.env.ASSEMBLEAI_FRONTEND_VERSION ?? '0.1.0';
const base = frontendArtifactEntry(appId, version);
function htmlEntry(): Plugin {
  return {
    name: 'knowledge-html-entry',
    generateBundle() {
      this.emitFile({ type: 'asset', fileName: 'index.html', source: `<!doctype html>
<html lang="zh-CN"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>集智 · 企业知识库</title><link rel="stylesheet" href="${base}knowledge.css"></head>
<body style="margin:0"><div id="knowledge-root"></div><script src="${base}knowledge.umd.js"></script></body></html>` });
    },
  };
}
export default defineConfig({
  base, plugins: [vue(), htmlEntry()],
  define: { 'process.env.NODE_ENV': JSON.stringify('production'),
    __ASSEMBLEAI_APP_ID__: JSON.stringify(appId), __ASSEMBLEAI_FRONTEND_VERSION__: JSON.stringify(version) },
  build: {
    target: 'es2022', cssCodeSplit: false,
    lib: { entry: fileURLToPath(new URL('./src/main.ts', import.meta.url)), name: `AssembleAI_${appId.replaceAll('-', '_')}`,
      formats: ['umd'], fileName: () => 'knowledge.umd.js', cssFileName: 'knowledge' },
    rollupOptions: { output: { inlineDynamicImports: true } },
  },
});
