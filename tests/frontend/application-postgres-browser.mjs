import { chooseReferenceOption, expectOidcAccountPicker, logoutFromUserMenu } from './reference-controls-helpers.mjs';
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { chromium, expect } from '@playwright/test';

// No route interception: browser -> native preview -> BFF/YARP -> FastAPI ->
// platform credential validator -> PostgreSQL is the actual execution path.
const [base, tenantId, userId] = process.argv.slice(2);
const origin = new URL(base);
assert.equal(origin.protocol, 'http:');
assert.equal(origin.hostname, '127.0.0.1');
assert.match(tenantId, /^[a-f0-9-]{36}$/);
assert.match(userId, /^[a-f0-9-]{36}$/);
const directory = resolve('.artifacts/application-postgres', tenantId);
await mkdir(directory, { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  const context = await browser.newContext({ baseURL: origin.origin, viewport: { width: 1366, height: 900 }, reducedMotion: 'reduce' });
  const page = await context.newPage();
  const failures = [];
  const appRequests = [];
  let embeddedRuns = 0;
  context.on('page', target => target.on('pageerror', error => failures.push(error.message)));
  page.on('pageerror', error => failures.push(error.message));
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname.endsWith('/runs')) embeddedRuns++;
  });
  context.on('request', request => {
    if (new URL(request.url()).pathname.startsWith('/api/apps/')) appRequests.push(request);
  });
  await page.goto('/bff/login?returnUrl=' + encodeURIComponent('/'));
  await chooseReferenceOption(page, '当前租户', tenantId);
  await page.locator('a[href="/applications/sample"]').first().click();
  await page.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(page.getByTestId('sample-app')).toBeVisible();
  await expect(page.getByTestId('sample-app')).toContainText('平台内运行');

  async function verifyJson(target, appId, releaseVersion) {
    await target.getByRole('button', { name: 'JSON 验证', exact: true }).click();
    await expect(target.getByTestId('sample-api-status')).toHaveText('JSON 验证完成。');
    const value = JSON.parse(await target.getByTestId('sample-api-result').textContent());
    assert.equal(value.status, 'verified');
    assert.equal(value.tenantId, tenantId);
    assert.equal(value.userId, userId);
    assert.equal(value.appId, appId);
    assert.equal(value.releaseVersion, releaseVersion);
    assert.equal(value.message, '验证调用');
    assert.match(value.requestId, /^[a-f0-9]{32}$/);
    assert.ok(value.authorizationVersion > 0);
  }

  await verifyJson(page, 'sample', '1.0.0');
  await page.getByRole('button', { name: '流式验证', exact: true }).click();
  await expect(page.getByTestId('sample-api-status')).toHaveText('流式验证完成。');
  assert.equal(await page.getByRole('list', { name: '接收增量' }).locator('li').count(), 3);
  await page.screenshot({ path: resolve(directory, 'embedded-json-sse.png'), fullPage: true });
  // Cancel synchronously when the first real increment reaches the DOM. The
  // three-part stream lasts only 300ms; Playwright actionability stabilization
  // can legitimately finish after the cancel button has already disappeared.
  await page.evaluate(() => {
    const root = document.querySelector('[data-testid="sample-app"]');
    if (!root) throw new Error('Missing application for cancellation.');
    const observer = new MutationObserver(() => {
      const increments = root.querySelectorAll('ol li').length;
      const cancel = [...root.querySelectorAll('button')].find(button => button.textContent === '取消调用');
      if (increments > 0 && cancel) {
        observer.disconnect(); root.setAttribute('data-fixture-cancelled-after-delta', String(increments)); cancel.click();
      }
    });
    observer.observe(root, { childList: true, subtree: true });
  });
  await page.getByRole('button', { name: '流式验证', exact: true }).click();
  await expect(page.getByTestId('sample-api-status')).toHaveText('已取消，调用未完成。');
  await expect(page.getByTestId('sample-app')).toHaveAttribute('data-fixture-cancelled-after-delta', '1');
  await page.getByRole('button', { name: '增加计数', exact: true }).click();
  await expect(page.getByTestId('sample-count')).toHaveText('1');

  // Re-entering uses a fresh component; the second registered app has its own
  // real build/digest but calls the same independently versioned backend.
  await page.getByRole('link', { name: '← 返回应用详情', exact: true }).click();
  await expect(page.getByTestId('sample-app')).toHaveCount(0);
  await page.locator('a[href="/applications/sample-clone"]').first().click();
  await page.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(page.getByTestId('sample-app')).toContainText('sample-clone');
  await verifyJson(page, 'sample-clone', '2.0.0');
  await page.getByRole('link', { name: '← 返回应用详情', exact: true }).click();
  await page.locator('a[href="/applications/sample"]').first().click();
  await page.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(page.getByTestId('sample-count')).toHaveText('0');
  await verifyJson(page, 'sample', '1.0.0');
  const runsBeforeReload = embeddedRuns;
  await page.reload();
  await expect(page.getByRole('heading', { name: '请重新打开应用', exact: true })).toBeVisible();
  assert.equal(embeddedRuns, runsBeforeReload, 'Embedded refresh must not recreate the previous run.');
  await page.getByRole('button', { name: '返回应用详情', exact: true }).click();
  await page.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(page.getByTestId('sample-count')).toHaveText('0');

  const standalone = await context.newPage();
  let standaloneRuns = 0;
  standalone.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname.endsWith('/runs')) standaloneRuns++;
  });
  await standalone.goto('/apps/sample/0.1.0/reports/line-a');
  await expect(standalone.getByTestId('sample-app')).toContainText('独立运行');
  const connect = standalone.getByRole('button', { name: '连接平台', exact: true });
  await expect(connect).toBeEnabled();
  assert.equal(standaloneRuns, 0);
  await connect.click();
  await expect(standalone.getByRole('button', { name: '已连接平台', exact: true })).toBeVisible();
  await verifyJson(standalone, 'sample', '1.0.0');
  assert.equal(standaloneRuns, 1);
  await standalone.screenshot({ path: resolve(directory, 'standalone-deep-link.png'), fullPage: true });
  await standalone.reload();
  await expect(standalone.getByRole('button', { name: '连接平台', exact: true })).toBeEnabled();
  assert.equal(standaloneRuns, 1, 'Refreshing cannot silently create another run.');
  await expect(standalone.getByRole('button', { name: 'JSON 验证', exact: true })).toBeDisabled();
  assert.equal((await context.request.get('/apps/sample/9.9.9/')).status(), 404);
  assert.equal((await context.request.get('/apps/sample/0.1.0/missing.js')).status(), 404);

  // Same artifacts also work for the second app outside qiankun.
  await standalone.goto('/apps/sample-clone/0.2.0/details');
  await standalone.getByRole('button', { name: '连接平台', exact: true }).click();
  await verifyJson(standalone, 'sample-clone', '2.0.0');
  for (const request of appRequests) {
    const headers = await request.allHeaders();
    assert.equal(headers.authorization, undefined, 'Internal credentials must never be sent by browser JavaScript.');
  }
  for (const target of [page, standalone]) {
    const storage = await target.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }));
    assert.ok(!storage.includes('fixture-access-token') && !storage.includes('fixture-refresh-token') && !storage.includes('eyJ'));
  }
  await logoutFromUserMenu(page);
  await expectOidcAccountPicker(page);
  await standalone.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(standalone.getByRole('link', { name: '使用企业账号登录' })).toBeVisible();
  assert.equal(await standalone.getByTestId('sample-api-result').count(), 0);
  assert.equal((await context.request.get('/bff/session')).status(), 401);
  assert.deepEqual(failures, []);
  await context.close();
  process.stdout.write('PASS: real browser + OIDC/PG + YARP + Python: embedded/standalone, two registered artifacts, JSON/SSE/cancel, deep links, remount, no credential leakage, logout cleanup.\n');
} finally { await browser.close(); }
