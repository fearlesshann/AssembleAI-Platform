import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173, strictPort: true,
    proxy: { '/apps/sample/': 'http://127.0.0.1:4173' },
  },
});
