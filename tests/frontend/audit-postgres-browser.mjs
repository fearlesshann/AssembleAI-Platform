import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import { resolve, relative, isAbsolute } from 'node:path';
import { chromium, expect } from '@playwright/test';

// The built portal uses actual OIDC/BFF/PG. Business API routes are never intercepted.
const configPath = resolve(process.argv[2]);
const local = relative(resolve('.local-data/postgres-tests'), configPath);
assert.ok(local && !local.startsWith('..') && !isAbsolute(local));
const config = JSON.parse(await readFile(configPath, 'utf8'));
const origin = new URL(config.origin);
assert.equal(origin.hostname, '127.0.0.1'); assert.equal(origin.protocol, 'http:');
const directory = resolve('.artifacts/audit-postgres', config.tenantId);
await mkdir(directory, { recursive: true });
const browser = await chromium.launch({ headless: true });
const errors = [];
try {
  async function login(account, path, select = true) {
    const context = await browser.newContext({ baseURL: origin.origin, viewport: { width: 1366, height: 900 }, reducedMotion: 'reduce', acceptDownloads: true });
    const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
    await page.goto(path); await page.getByRole('link', { name: '企业账号登录', exact: true }).click();
    await page.getByRole('link', { name: account, exact: true }).click();
    await expect(page.getByRole('button', { name: '退出登录', exact: true })).toBeVisible();
    if (select) {
      await page.getByLabel('当前租户', { exact: true }).selectOption(config.tenantId);
      await expect.poll(async () => (await page.request.get('/bff/tenant')).json().then(x => x.tenantId)).toBe(config.tenantId);
    }
    await page.goto(path);
    await expect(page.getByRole('heading', { name: '调用与审计', exact: true })).toBeVisible();
    const workspace = page.getByRole('region', { name: '调用与审计工作区', exact: true });
    const before = await workspace.getAttribute('aria-busy');
    if (before === 'true') {
      const values = await page.getByRole('combobox', { name: '记录类型', exact: true }).locator('option').evaluateAll(nodes => nodes.map(node => node.value));
      process.stdout.write('Audit page pending: ' + JSON.stringify({ account, busy: before, sourceOptions: values }) + '\n');
    }
    await expect(workspace).toHaveAttribute('aria-busy', 'false');
    await expect(page.getByRole('table', { name: '审计记录', exact: true })).toBeVisible();
    return page;
  }
  async function query(page) {
    const response = page.waitForResponse(r => r.request().method() === 'GET' && new URL(r.url()).pathname === '/api/platform/v1/audit/events');
    await page.getByRole('button', { name: '查询记录', exact: true }).click();
    assert.equal((await response).status(), 200);
    await expect(page.getByRole('button', { name: '查询记录', exact: true })).toBeEnabled();
  }
  const manager = await login('审计管理员', '/manage/audit');
  await expect(manager.getByRole('table')).toBeVisible();
  await expect(manager.getByRole('button', { name: '下一页', exact: true })).toBeEnabled();
  const firstPage = await manager.getByRole('table').innerText();
  await manager.getByRole('button', { name: '下一页', exact: true }).click();
  await expect(manager.getByRole('button', { name: '上一页', exact: true })).toBeEnabled();
  assert.notEqual(await manager.getByRole('table').innerText(), firstPage);
  await manager.getByRole('button', { name: '上一页', exact: true }).click();
  await manager.getByRole('combobox', { name: '记录类型', exact: true }).selectOption('model');
  await manager.getByLabel('请求标识', { exact: true }).fill(config.correlation);
  await query(manager);
  await expect(manager.getByRole('link', { name: /^查看详情\s/ })).toHaveCount(1);
  await manager.getByRole('link', { name: /^查看详情\s/ }).click();
  await expect(manager).toHaveURL(new RegExp(`/manage/audit/model/${config.allowedId}(?:\\?|$)`));
  await expect(manager.getByRole('button', { name: '查看网关关联', exact: true })).toBeVisible();
  await manager.getByRole('button', { name: '查看网关关联', exact: true }).click();
  await expect(manager.getByRole('link', { name: /^查看详情\s/ })).toHaveCount(3);
  assert.ok(!(await manager.locator('body').innerText()).includes('private-marker'));
  assert.ok(!(await manager.locator('body').innerText()).includes(config.hiddenId));
  const download = manager.waitForEvent('download');
  await manager.getByRole('button', { name: '导出记录', exact: true }).click();
  const file = await download; const exported = JSON.parse(await readFile(await file.path(), 'utf8'));
  assert.equal(exported.items.length, 3); assert.equal(exported.truncated, false);
  assert.ok(!JSON.stringify(exported).includes('private-marker'));
  await manager.screenshot({ path: resolve(directory, 'audit-light.png'), fullPage: true });
  await manager.getByRole('button', { name: '切换暗色', exact: true }).click();
  await manager.screenshot({ path: resolve(directory, 'audit-dark.png'), fullPage: true });
  await manager.setViewportSize({ width: 390, height: 844 });
  assert.equal(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await manager.screenshot({ path: resolve(directory, 'audit-mobile.png'), fullPage: true });
  await manager.setViewportSize({ width: 1366, height: 900 });
  await manager.goto(`/manage/audit/model/${config.allowedId}`); await manager.reload();
  await expect(manager.getByRole('button', { name: '查看网关关联', exact: true })).toBeVisible();
  const limited = await login('模型审计员', '/manage/audit');
  await expect(limited.getByRole('button', { name: '导出记录', exact: true })).toHaveCount(0);
  const options = await limited.getByRole('combobox', { name: '记录类型', exact: true }).locator('option').evaluateAll(nodes => nodes.map(n => n.value));
  assert.ok(options.includes('model') && !options.includes('connector') && !options.includes('gateway'));
  const global = await login('平台身份管理员', '/manage/audit/global', false);
  await expect(global.getByRole('table')).toBeVisible();
  const globalOptions = await global.getByRole('combobox', { name: '记录类型', exact: true }).locator('option').evaluateAll(nodes => nodes.map(n => n.value));
  assert.ok(globalOptions.includes('identity') && !globalOptions.includes('model') && !globalOptions.includes('connector'));
  await expect(global.getByRole('button', { name: '导出记录', exact: true })).toHaveCount(0);
  await manager.getByLabel('当前租户', { exact: true }).selectOption(config.otherTenantId);
  await expect.poll(async () => (await manager.request.get('/bff/tenant')).json().then(x => x.tenantId)).toBe(config.otherTenantId);
  await expect(manager.getByRole('button', { name: '查看网关关联', exact: true })).toHaveCount(0);
  await expect(manager.getByRole('table')).toHaveCount(0);
  const storage = await limited.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }));
  assert.ok(!storage.includes(config.correlation) && !storage.includes('fixture-access-token'));
  await limited.getByRole('button', { name: '退出登录', exact: true }).click();
  await expect(limited.getByRole('link', { name: '企业账号登录', exact: true })).toBeVisible();
  assert.equal((await limited.request.get('/api/platform/v1/audit/workspace?scope=tenant')).status(), 401);
  assert.deepEqual(errors, []);
  process.stdout.write('PASS: actual unified audit pages, filters/pagination/detail/correlation/JSON export, global/resource separation, deep links, reference themes/390px and logout.\n');
} catch (error) {
  const page = browser.contexts()[0]?.pages()[0];
  if (page) {
    const layout = await page.evaluate(() => ({
      viewport: innerWidth, document: document.documentElement.scrollWidth,
      overflow: [...document.querySelectorAll('body *')].map(element => {
        const box = element.getBoundingClientRect(); const style = getComputedStyle(element);
        return { tag: element.tagName, class: typeof element.className === 'string' ? element.className : '', left: Math.round(box.left), right: Math.round(box.right), width: Math.round(box.width), overflowX: style.overflowX, position: style.position };
      }).filter(box => box.right > innerWidth + 1 || box.left < -1).slice(0, 40)
    })).catch(() => null);
    process.stderr.write('Failure layout: ' + JSON.stringify(layout) + '\n');
    await page.screenshot({ path: resolve(directory, 'failure.png'), fullPage: true }).catch(() => {});
    process.stderr.write((await page.locator('body').innerText()).slice(0, 12000) + '\n');
  }
  throw error;
} finally { await browser.close(); }
