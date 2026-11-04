import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { fileURLToPath } from 'node:url';
import { createPlatformProxy, rejectPlatformUpgrade } from '../../tools/local-platform-target.mjs';
import { developmentFrontendArtifacts, loadFrontendArtifacts } from '../../tools/frontend-artifacts.mjs';

// Keep the browser's origin for BFF Origin/CSRF and OIDC callback checks.
// The fixed backend target is server-side configuration, never a browser parameter.
const proxyPlatform = createPlatformProxy(process.env.ASSEMBLEAI_PLATFORM_ORIGIN);

export default defineConfig({
  plugins: [vue(), {
    name: 'assembleai-local-platform',
    configureServer(server) {
      const configuration = process.env.ASSEMBLEAI_FRONTEND_ARTIFACTS;
      const artifacts = configuration !== undefined ? loadFrontendArtifacts(configuration)
        : developmentFrontendArtifacts(fileURLToPath(new URL('../sample/frontend/dist', import.meta.url)),
            fileURLToPath(new URL('../chatbi/frontend/dist', import.meta.url)));
      server.middlewares.use((req, res, next) => { if (!proxyPlatform(req, res) && !artifacts.handle(req, res)) next(); });
      server.httpServer?.prependListener('upgrade', rejectPlatformUpgrade);
    },
  }],
  server: {
    port: 5173, strictPort: true,
  },
});
