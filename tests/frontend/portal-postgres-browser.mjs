import { chooseReferenceOption, expectOidcAccountPicker, logoutFromUserMenu, toggleReferenceTheme } from './reference-controls-helpers.mjs';
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { chromium } from '@playwright/test';

// Invoked only by the marked, disposable PostgreSQL acceptance fixture. No
// mocked API routes: discovery, login, tenant selection and catalog are real HTTP.
const [base, tenantId, appId] = process.argv.slice(2);
const origin = new URL(base);
assert.equal(origin.protocol, 'http:');
assert.equal(origin.hostname, '127.0.0.1');
assert.match(tenantId, /^[a-f0-9-]{36}$/);
assert.match(appId, /^[a-z][a-z0-9-]{0,63}$/);
const directory = resolve('.artifacts/portal-postgres', tenantId);
await mkdir(directory, { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  const context = await browser.newContext({ baseURL: origin.origin, viewport: { width: 1366, height: 900 }, reducedMotion: 'reduce', colorScheme: 'dark' });
  const page = await context.newPage();
  const failures = [];
  page.on('pageerror', () => failures.push('Browser script failed.'));
  const signedIn = page.waitForResponse(response => new URL(response.url()).pathname === '/bff/session' && response.status() === 200);
  await page.goto('/bff/login?returnUrl=%2F');
  await signedIn;
  assert.equal(await page.locator('html').getAttribute('data-theme'), 'light');
  await page.getByRole('heading', { name: '选择租户', exact: true }).waitFor();
  const listing = page.waitForResponse(response => new URL(response.url()).pathname === `/api/platform/v1/tenants/${tenantId}/catalog` && response.status() === 200);
  await chooseReferenceOption(page, '当前租户', tenantId);
  const catalog = await (await listing).json();
  assert.equal(catalog.items.length, 2);
  const app = catalog.items.find(item => item.appId === appId);
  assert.ok(app, 'Real catalog must contain the authorized release.');
  await page.getByText(app.name, { exact: true }).first().waitFor();
  await page.screenshot({ path: resolve(directory, 'catalog-light.png'), fullPage: true });
  await page.getByRole('searchbox', { name: '搜索应用' }).fill(appId);
  await page.locator(`a[href="/applications/${appId}"]`).first().click();
  await page.getByRole('heading', { name: app.name, exact: true, level: 2 }).waitFor();
  assert.equal(await page.getByRole('button', { name: '打开应用', exact: true }).isEnabled(), true);
  await toggleReferenceTheme(page);
  await page.screenshot({ path: resolve(directory, 'detail-dark.png'), fullPage: true });
  await page.reload();
  await page.getByRole('heading', { name: app.name, exact: true, level: 2 }).waitFor();
  assert.equal(await page.locator('html').getAttribute('data-theme'), 'dark');
  await page.setViewportSize({ width: 390, height: 844 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: resolve(directory, 'detail-mobile.png'), fullPage: true });
  await logoutFromUserMenu(page);
  await expectOidcAccountPicker(page);
  assert.equal(await page.getByText(app.name, { exact: true }).count(), 0);
  assert.equal((await context.request.get('/bff/session')).status(), 401);
  const storage = await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }));
  assert.ok(!storage.includes('fixture-access-token') && !storage.includes('fixture-refresh-token'));
  assert.deepEqual(failures, []);
  await context.close();
  process.stdout.write('PASS: browser -> real OIDC/BFF -> PostgreSQL catalog, tenant selection, detail, theme, narrow layout and logout.\n');
} finally { await browser.close(); }
