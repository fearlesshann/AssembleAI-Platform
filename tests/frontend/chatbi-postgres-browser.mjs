import { chooseReferenceOption, expectOidcAccountPicker, logoutFromUserMenu, toggleReferenceTheme } from './reference-controls-helpers.mjs';
import assert from 'node:assert/strict';
import { readFile, mkdir } from 'node:fs/promises';
import { resolve, relative, isAbsolute } from 'node:path';
import { chromium, expect } from '@playwright/test';

// Real OIDC -> BFF -> YARP -> independent ChatBI -> model/connector -> PostgreSQL.
// This runner never intercepts routes or supplies browser authorization credentials.
const [configurationPath] = process.argv.slice(2);
const configuration = JSON.parse(await readFile(configurationPath, 'utf8'));
const { publicOrigin, issuer, userId, tenantId, appId, outputDirectory } = configuration;
const origin = new URL(publicOrigin);
assert.equal(origin.protocol, 'http:'); assert.equal(origin.hostname, '127.0.0.1');
assert.equal(appId, 'chatbi'); assert.match(tenantId, /^[a-f0-9-]{36}$/); assert.match(userId, /^[a-f0-9-]{36}$/);
assert.equal(new URL(issuer).hostname, '127.0.0.1');
const directory = resolve(outputDirectory);
const artifactPath = relative(resolve('.artifacts'), directory);
const disposablePath = relative(resolve('.local-data/postgres-tests'), directory);
assert.ok([artifactPath, disposablePath].some(path => path && !path.startsWith('..') && !isAbsolute(path)), 'Browser output must stay in the owned acceptance directories.');
await mkdir(directory, { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  const context = await browser.newContext({ baseURL: origin.origin, viewport: { width: 1366, height: 900 }, reducedMotion: 'reduce' });
  const failures = [];
  const appRequests = [];
  context.on('page', page => page.on('pageerror', () => failures.push('Browser script failed.')));
  context.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/apps/chatbi/')) appRequests.push(request); });
  const portal = await context.newPage();
  await portal.goto('/bff/login?returnUrl=' + encodeURIComponent('/'));
  await chooseReferenceOption(portal, '当前租户', tenantId);
  const session = await context.request.get('/bff/session');
  assert.equal(session.status(), 200); assert.equal((await session.json()).iss, issuer);

  const standalone = await context.newPage(); let standaloneRuns = 0;
  standalone.on('request', request => { if (request.method() === 'POST' && new URL(request.url()).pathname.endsWith('/runs')) standaloneRuns++; });
  await standalone.goto('/apps/chatbi/0.1.0/');
  await expect(standalone.getByTestId('chatbi-app')).toContainText('独立运行');
  await expect(standalone.getByRole('button', { name: '连接平台', exact: true })).toBeEnabled();
  assert.equal(standaloneRuns, 0, 'Independent page inspection cannot silently create a run.');
  await standalone.getByRole('button', { name: '连接平台', exact: true }).click();
  await expect(standalone.getByRole('button', { name: '已连接平台', exact: true })).toBeVisible();

  async function analyze(page) {
    await expect(page.getByTestId('chatbi-dataset')).toBeEnabled();
    await page.getByTestId('chatbi-question').fill('2026年11月1日至2日的产量合计是多少？');
    await page.getByTestId('chatbi-submit').click();
    await expect(page.getByTestId('chatbi-result')).toBeVisible({ timeout: 160_000 });
    await expect(page.getByTestId('chatbi-status')).toContainText('分析完成');
    await expect(page.getByTestId('chatbi-result').getByRole('cell', { name: '300', exact: true })).toHaveCount(1);
    assert.equal(await page.getByTestId('chatbi-result').locator('tbody tr').count(), 1);
    await page.getByText('查看查询依据', { exact: true }).click();
    await expect(page.getByTestId('chatbi-evidence')).toContainText('sum');
    assert.ok(!await page.getByTestId('chatbi-result').locator('img,script,iframe').count());
  }
  await analyze(standalone); assert.equal(standaloneRuns, 1);
  await standalone.screenshot({ path: resolve(directory, 'standalone-light.png'), fullPage: true });
  await toggleReferenceTheme(standalone);
  await expect(standalone.getByTestId('chatbi-app')).toHaveAttribute('data-theme', 'dark');
  await standalone.screenshot({ path: resolve(directory, 'standalone-dark.png'), fullPage: true });
  await standalone.setViewportSize({ width: 390, height: 844 });
  assert.equal(await standalone.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await standalone.screenshot({ path: resolve(directory, 'standalone-mobile-dark.png'), fullPage: true });
  await standalone.getByRole('button', { name: '切换浅色', exact: true }).click();
  await standalone.screenshot({ path: resolve(directory, 'standalone-mobile-light.png'), fullPage: true });

  await portal.locator('a[href="/applications/chatbi"]').first().click();
  await portal.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(portal.getByTestId('chatbi-app')).toContainText('平台内运行');
  await analyze(portal);
  await portal.screenshot({ path: resolve(directory, 'embedded-light.png'), fullPage: true });
  await toggleReferenceTheme(portal);
  await expect(portal.getByTestId('chatbi-app')).toHaveAttribute('data-theme', 'dark');
  await expect(portal.getByTestId('chatbi-result')).toBeVisible();
  await portal.screenshot({ path: resolve(directory, 'embedded-dark.png'), fullPage: true });
  await portal.getByRole('link', { name: '← 返回应用详情', exact: true }).click();
  await expect(portal.getByTestId('chatbi-app')).toHaveCount(0);
  for (const request of appRequests) {
    const headers = await request.allHeaders();
    assert.equal(headers.authorization, undefined); assert.equal(headers['x-model-delegation'], undefined); assert.equal(headers['x-connector-delegation'], undefined);
    assert.equal(headers['x-tenant-id'], tenantId);
  }
  assert.equal(appRequests.filter(request => request.method() === 'POST' && new URL(request.url()).pathname.endsWith('/ask')).length, 2,
    'Acceptance must make exactly one explicit question per run mode.');
  await logoutFromUserMenu(portal);
  await expectOidcAccountPicker(portal);
  await standalone.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(standalone.getByRole('link', { name: '使用企业账号登录', exact: true })).toBeVisible();
  await expect(standalone.getByTestId('chatbi-result')).toHaveCount(0);
  for (const page of [portal, standalone]) {
    const storage = await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }));
    assert.ok(!storage.includes('fixture-access-token') && !storage.includes('fixture-refresh-token') && !storage.includes('eyJ'));
  }
  assert.deepEqual(failures, []);
  await context.close();
  process.stdout.write('PASS: ChatBI real browser OIDC/BFF/YARP/Python/PostgreSQL: explicit standalone and embedded sum baseline, light/dark/mobile, source evidence and logout cleanup.\n');
} finally { await browser.close(); }
