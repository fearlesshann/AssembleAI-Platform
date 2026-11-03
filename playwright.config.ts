import { defineConfig } from '@playwright/test';

// These UI fixtures own their local sample deployment. Do not connect a test
// preview to a developer's real BFF or load unrelated deployment directories.
delete process.env.ASSEMBLEAI_FRONTEND_ARTIFACTS;
delete process.env.ASSEMBLEAI_PLATFORM_ORIGIN;

export default defineConfig({
  testDir: './tests/frontend/e2e', fullyParallel: false, workers: 1,
  outputDir: '.artifacts/playwright', reporter: 'list',
  use: { baseURL: 'http://127.0.0.1:4173', browserName: 'chromium', viewport: { width: 1366, height: 900 }, trace: 'retain-on-failure' },
  webServer: { command: `"${process.execPath}" tools/serve-frontend.mjs`, url: 'http://127.0.0.1:4173', reuseExistingServer: false,
    env: { PORT: '4173' } },
});
