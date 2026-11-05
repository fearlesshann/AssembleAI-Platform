import { chooseReferenceOption, expectOidcAccountPicker, expectReferenceValue, logoutFromUserMenu, referenceSelect, toggleReferenceTheme } from './reference-controls-helpers.mjs';
import assert from 'node:assert/strict';
import { readFile, mkdir } from 'node:fs/promises';
import { resolve, relative, isAbsolute, basename } from 'node:path';
import { chromium, expect } from '@playwright/test';

// Real OIDC/BFF/platform/Python/native dependencies. No route interception.
const [configurationPath] = process.argv.slice(2);
const { publicOrigin, issuer, userId, tenantId, appId, outputDirectory, uploadFile, factoryId } = JSON.parse(await readFile(configurationPath, 'utf8'));
const origin = new URL(publicOrigin);
assert.equal(origin.protocol, 'http:'); assert.equal(origin.hostname, '127.0.0.1');
assert.equal(appId, 'knowledge'); assert.match(tenantId, /^[a-f0-9-]{36}$/); assert.match(userId, /^[a-f0-9-]{36}$/);
assert.equal(new URL(issuer).hostname, '127.0.0.1');
const directory = resolve(outputDirectory);
assert.ok([relative(resolve('.artifacts'), directory), relative(resolve('.local-data/postgres-tests'), directory)]
  .some(path => path && !path.startsWith('..') && !isAbsolute(path)), 'Output must stay in acceptance directories.');
await mkdir(directory, { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  const context = await browser.newContext({ baseURL: origin.origin, viewport: { width: 1366, height: 900 }, reducedMotion: 'reduce', acceptDownloads: true });
  const failures = [], appRequests = [], uploads = [];
  const bootstrapRequests = new WeakMap(), bootstrapTimings = [];
  const bootstrapPaths = new Set(['/bff/session', '/bff/tenant', '/bff/csrf', '/api/platform/v1/tenants']);
  context.on('page', page => page.on('pageerror', () => failures.push('Browser script failed.')));
  context.on('request', request => {
    const url = new URL(request.url());
    if (bootstrapPaths.has(url.pathname)) bootstrapRequests.set(request, { method: request.method(), path: url.pathname, started: performance.now() });
    if (url.pathname.startsWith('/api/apps/knowledge/')) appRequests.push(request);
    if (request.method() === 'POST' && url.pathname.endsWith('/knowledge/documents')) uploads.push(request);
  });
  function recordBootstrap(request, status) {
    const value = bootstrapRequests.get(request);
    if (value) {
      bootstrapTimings.push({ method: value.method, path: value.path, status, elapsedMs: Math.round(performance.now() - value.started) });
      if (bootstrapTimings.length > 24) bootstrapTimings.shift();
      bootstrapRequests.delete(request);
    }
  }
  context.on('response', response => recordBootstrap(response.request(), response.status()));
  context.on('requestfailed', request => recordBootstrap(request, 'failed'));
  const portal = await context.newPage();
  let standalone, standaloneRuns = 0;
  try {
    await portal.goto('/bff/login?returnUrl=' + encodeURIComponent('/'));
    const portalTenant = portal.getByRole('combobox', { name: '当前租户', exact: true });
    await expect(portalTenant).toBeEnabled();
    if (await referenceSelect(portal, '当前租户').getAttribute('data-value') !== tenantId) {
      // An option click does not await the component's
      // CSRF -> tenant write -> authoritative read -> catalog transaction.
      const tenantWrite = portal.waitForResponse(response => response.request().method() === 'POST'
        && new URL(response.url()).pathname === '/bff/tenant');
      await chooseReferenceOption(portal, '当前租户', tenantId);
      const committed = await tenantWrite;
      assert.equal(committed.status(), 200, 'Tenant selection must commit before opening the independent application.');
      assert.ok((await committed.json()).tenantId === tenantId, 'Tenant selection response must match the selected workspace.');
    }
    await expectReferenceValue(portal, '当前租户', tenantId);
    await expect(portalTenant).toBeEnabled();
    const selected = await context.request.get('/bff/tenant');
    assert.equal(selected.status(), 200);
    assert.ok((await selected.json()).tenantId === tenantId, 'Authoritative tenant read must match the committed workspace.');
    const session = await context.request.get('/bff/session');
    assert.equal(session.status(), 200); assert.equal((await session.json()).iss, issuer);

    standalone = await context.newPage();
    standalone.on('request', request => { if (request.method() === 'POST' && new URL(request.url()).pathname.endsWith('/runs')) standaloneRuns++; });
    await standalone.goto('/apps/knowledge/0.1.0/');
    await expect(standalone.getByTestId('knowledge-app')).toContainText('独立运行');
    await expectReferenceValue(standalone, '独立运行租户', tenantId);
    await expect(standalone.getByRole('region', { name: '独立运行平台连接' })).toHaveAttribute('aria-busy', 'false');
    await expect(standalone.getByRole('button', { name: '连接平台', exact: true })).toBeEnabled();
  } catch (error) {
    async function state(page, selector) {
      if (!page || page.isClosed()) return { present: false };
      return page.evaluate(({ selector, tenantId }) => {
        const select = document.querySelector(selector);
        const connection = document.querySelector('.knowledge-access');
        const button = connection?.querySelector('.knowledge-controls > button');
        const selectedValue = select?.closest('.ui-select')?.getAttribute('data-value');
        return { present: true, tenantControl: !!select, tenantPresent: !!selectedValue,
          selectedExpectedTenant: selectedValue === tenantId, tenantDisabled: select?.disabled ?? null,
          connectionBusy: connection?.getAttribute('aria-busy') ?? null, connectionDisabled: button?.disabled ?? null };
      }, { selector, tenantId }).catch(() => ({ present: true, unavailable: true }));
    }
    process.stderr.write(`Knowledge bootstrap diagnostics: ${JSON.stringify({ http: bootstrapTimings,
      portal: await state(portal, '[role="combobox"][aria-label="当前租户"]'), standalone: await state(standalone, '[role="combobox"][aria-label="独立运行租户"]') })}\n`);
    throw error;
  }
  assert.equal(standaloneRuns, 0);
  await standalone.getByRole('button', { name: '连接平台', exact: true }).click();
  await expect(standalone.getByTestId('knowledge-documents')).toContainText('制造业测试-轴承维护.docx');
  await expect(standalone.getByTestId('knowledge-documents')).not.toContainText('restricted');
  assert.equal(standaloneRuns, 1);

  async function answer(page) {
    await page.getByLabel('你想从文档中了解什么？').fill('轴承温度过高时，应该先采取什么措施？');
    await page.getByRole('button', { name: '查找答案', exact: true }).click();
    await expect(page.getByTestId('knowledge-answer')).toBeVisible({ timeout: 180_000 });
    await expect(page.getByTestId('knowledge-answer')).toContainText('停机');
    await expect(page.getByTestId('knowledge-answer')).toContainText('制造业测试-轴承维护.docx');
    await expect(page.getByTestId('knowledge-answer').locator('blockquote')).toContainText('轴承温度过高时');
    assert.equal(await page.getByTestId('knowledge-answer').locator('img,script,iframe').count(), 0);
    const downloadPromise = page.waitForEvent('download');
    await page.getByTestId('knowledge-answer').getByRole('button', { name: '下载原文核对', exact: true }).first().click();
    const download = await downloadPromise;
    assert.equal(download.suggestedFilename(), '制造业测试-轴承维护.docx');
    const path = await download.path(); assert.ok(path);
    const bytes = await readFile(path);
    assert.equal(bytes.subarray(0, 2).toString(), 'PK');
    await download.delete();
  }
  await answer(standalone);
  await standalone.screenshot({ path: resolve(directory, 'standalone-light.png'), fullPage: true });
  await toggleReferenceTheme(standalone);
  await expect(standalone.getByTestId('knowledge-app')).toHaveAttribute('data-theme', 'dark');
  await expect(standalone.getByTestId('knowledge-answer')).toBeVisible();
  await standalone.screenshot({ path: resolve(directory, 'standalone-dark.png'), fullPage: true });
  await standalone.setViewportSize({ width: 390, height: 844 });
  assert.equal(await standalone.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await standalone.screenshot({ path: resolve(directory, 'standalone-mobile-dark.png'), fullPage: true });
  await standalone.getByRole('button', { name: '切换浅色', exact: true }).click();
  await standalone.screenshot({ path: resolve(directory, 'standalone-mobile-light.png'), fullPage: true });

  // A real upload from the rendered form must become ready through the broker/CPU indexer.
  await standalone.getByLabel('添加文档').setInputFiles(uploadFile);
  await chooseReferenceOption(standalone, '所属范围', factoryId);
  await standalone.getByRole('button', { name: '上传文档', exact: true }).click();
  const uploaded = standalone.getByTestId('knowledge-documents').locator('tbody tr').filter({ hasText: basename(uploadFile) });
  await expect(uploaded).toContainText('可问答', { timeout: 150_000 });
  assert.equal(uploads.length, 1);
  const uploadHeaders = await uploads[0].allHeaders();
  assert.equal(uploadHeaders['content-type'], 'application/octet-stream'); assert.ok(uploadHeaders['x-csrf-token']);
  assert.equal(new URL(uploads[0].url()).searchParams.get('organizationId'), factoryId);
  // Chromium can omit File/Blob bytes from CDP postDataEntries. Verify the
  // actual persisted original through the authenticated download UI instead.
  const uploadedDownloadPromise = standalone.waitForEvent('download');
  await uploaded.getByRole('button', { name: '下载原文', exact: true }).click();
  const uploadedDownload = await uploadedDownloadPromise;
  try {
    assert.equal(uploadedDownload.suggestedFilename(), basename(uploadFile));
    const downloadedPath = await uploadedDownload.path(); assert.ok(downloadedPath);
    const downloadedBytes = await readFile(downloadedPath);
    const expectedBytes = await readFile(uploadFile);
    assert.ok(downloadedBytes.equals(expectedBytes), 'Downloaded original must exactly match the uploaded document bytes.');
  } finally { await uploadedDownload.delete(); }
  await uploaded.getByRole('button', { name: '删除', exact: true }).click();
  const deletion = standalone.getByRole('dialog', { name: '确认删除文档', exact: true });
  await expect(deletion).toBeVisible();
  await deletion.getByRole('button', { name: '确认删除', exact: true }).click();
  await expect(uploaded).toHaveCount(0);
  await answer(standalone); // Retain a current answer to verify cross-tab logout cleanup below.

  await portal.locator('a[href="/applications/knowledge"]').first().click();
  await portal.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(portal.getByTestId('knowledge-app')).toContainText('平台内运行');
  await answer(portal);
  await portal.screenshot({ path: resolve(directory, 'embedded-light.png'), fullPage: true });
  await toggleReferenceTheme(portal);
  await expect(portal.getByTestId('knowledge-app')).toHaveAttribute('data-theme', 'dark');
  await expect(portal.getByTestId('knowledge-answer')).toBeVisible();
  await portal.screenshot({ path: resolve(directory, 'embedded-dark.png'), fullPage: true });
  await portal.getByRole('link', { name: '← 返回应用详情', exact: true }).click();
  await expect(portal.getByTestId('knowledge-app')).toHaveCount(0);

  for (const request of appRequests) {
    const headers = await request.allHeaders();
    assert.equal(headers.authorization, undefined); assert.equal(headers['x-model-delegation'], undefined); assert.equal(headers['x-knowledge-delegation'], undefined);
    assert.equal(headers['x-tenant-id'], tenantId);
  }
  assert.equal(appRequests.filter(request => request.method() === 'POST' && new URL(request.url()).pathname.endsWith('/ask')).length, 3);
  await logoutFromUserMenu(portal);
  await expectOidcAccountPicker(portal);
  await standalone.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(standalone.getByTestId('knowledge-answer')).toHaveCount(0);
  await expect(standalone.getByRole('link', { name: '使用企业账号登录', exact: true })).toBeVisible();
  for (const page of [portal, standalone]) {
    const storage = await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }));
    assert.ok(!storage.includes('fixture-access-token') && !storage.includes('fixture-refresh-token') && !storage.includes('eyJ'));
  }
  assert.deepEqual(failures, []);
  await context.close();
  process.stdout.write('PASS: Knowledge browser actual OIDC, upload/index/delete, original DOCX download, independent/embedded answers, light/dark/mobile and logout cleanup.\n');
} finally { await browser.close(); }
