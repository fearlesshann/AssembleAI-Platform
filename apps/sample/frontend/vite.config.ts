import { defineConfig, type Plugin } from 'vite';
import vue from '@vitejs/plugin-vue';
import { fileURLToPath } from 'node:url';

const base = '/apps/sample/0.1.0/';
function htmlEntry(): Plugin {
  return {
    name: 'sample-html-entry',
    generateBundle() {
      this.emitFile({ type: 'asset', fileName: 'index.html', source: `<!doctype html>
<html lang="zh-CN"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>集智 · 接入验证应用</title><link rel="stylesheet" href="${base}sample.css"></head>
<body><div id="sample-root"></div><script src="${base}sample.umd.js"></script></body></html>` });
    },
  };
}
export default defineConfig({
  base, plugins: [vue(), htmlEntry()],
  build: {
    target: 'es2022', cssCodeSplit: false,
    lib: { entry: fileURLToPath(new URL('./src/main.ts', import.meta.url)), name: 'AssembleAISample', formats: ['umd'], fileName: () => 'sample.umd.js', cssFileName: 'sample' },
    rollupOptions: { output: { inlineDynamicImports: true } },
  },
});
