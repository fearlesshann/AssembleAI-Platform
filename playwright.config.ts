import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests/frontend/e2e', fullyParallel: false, workers: 1,
  outputDir: '.artifacts/playwright', reporter: 'list',
  use: { baseURL: 'http://127.0.0.1:4173', browserName: 'chromium', viewport: { width: 1366, height: 900 }, trace: 'retain-on-failure' },
  webServer: { command: `"${process.execPath}" tools/serve-frontend.mjs`, url: 'http://127.0.0.1:4173', reuseExistingServer: false },
});
