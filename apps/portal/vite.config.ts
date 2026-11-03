import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { createPlatformProxy, rejectPlatformUpgrade } from '../../tools/local-platform-target.mjs';

// Keep the browser's origin for BFF Origin/CSRF and OIDC callback checks.
// The fixed backend target is server-side configuration, never a browser parameter.
const proxyPlatform = createPlatformProxy(process.env.ASSEMBLEAI_PLATFORM_ORIGIN);

export default defineConfig({
  plugins: [vue(), {
    name: 'assembleai-local-platform',
    configureServer(server) {
      server.middlewares.use((req, res, next) => { if (!proxyPlatform(req, res)) next(); });
      server.httpServer?.prependListener('upgrade', rejectPlatformUpgrade);
    },
  }],
  server: {
    port: 5173, strictPort: true,
    proxy: { '/apps/sample/': 'http://127.0.0.1:4173' },
  },
});
