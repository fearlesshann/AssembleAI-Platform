import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import { resolve, relative, isAbsolute } from 'node:path';
import { chromium, expect } from '@playwright/test';

// Actual built portal -> OIDC -> BFF -> PostgreSQL/provider. No route interception.
const configPath = resolve(process.argv[2]);
const local = relative(resolve('.local-data/postgres-tests'), configPath);
assert.ok(local && !local.startsWith('..') && !isAbsolute(local));
const config = JSON.parse(await readFile(configPath, 'utf8'));
const origin = new URL(config.origin);
assert.equal(origin.hostname, '127.0.0.1'); assert.equal(origin.protocol, 'http:');
const directory = resolve('.artifacts/resource-postgres', config.tenantId);
await mkdir(directory, { recursive: true });
const browser = await chromium.launch({ headless: true });
const failures = [];
const tenant = `/api/platform/v1/tenants/${config.tenantId}`;
const model = `${tenant}/models/assistant`;
const source = `${tenant}/connectors/factory-db`;
try {
  async function api(page, method, path, body) {
    return page.evaluate(async ({ method, path, body }) => {
      const headers = { Accept: 'application/json' };
      if (method !== 'GET') {
        const csrf = await fetch('/bff/csrf', { credentials: 'same-origin', cache: 'no-store' }).then(r => r.json());
        headers[csrf.headerName] = csrf.requestToken; headers['Content-Type'] = 'application/json';
      }
      const response = await fetch(path, { method, headers, credentials: 'same-origin', cache: 'no-store', body: body === undefined ? undefined : JSON.stringify(body) });
      const text = await response.text();
      return { status: response.status, body: text ? JSON.parse(text) : null };
    }, { method, path, body });
  }
  async function login(account, route) {
    const context = await browser.newContext({ baseURL: origin.origin, viewport: { width: 1366, height: 900 }, reducedMotion: 'reduce' });
    const page = await context.newPage(); page.on('pageerror', error => failures.push(error.message));
    await page.goto(route);
    await page.getByRole('link', { name: '企业账号登录', exact: true }).click();
    await page.getByRole('link', { name: account, exact: true }).click();
    await expect(page.getByRole('button', { name: '退出登录', exact: true })).toBeVisible();
    await page.getByLabel('当前租户', { exact: true }).selectOption(config.tenantId);
    await expect.poll(async () => (await api(page, 'GET', '/bff/tenant')).body.tenantId).toBe(config.tenantId);
    await page.goto(route);
    return page;
  }
  async function save(page, path) {
    const mutation = page.waitForResponse(response => response.request().method() === 'PUT' && new URL(response.url()).pathname === path);
    await page.getByRole('button', { name: '保存配置', exact: true }).click();
    assert.equal((await mutation).status(), 200);
    await expect(page.getByText('配置已保存，已重新读取最新状态。', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: '编辑配置', exact: true })).toBeEnabled();
  }
  const manager = await login('资源管理员', '/manage/models/assistant');
  await expect(manager.getByRole('heading', { name: '验收模型', exact: true })).toBeVisible();
  await manager.getByRole('button', { name: '编辑配置', exact: true }).click();
  await manager.getByLabel('启用当前租户', { exact: true }).check();
  await manager.getByLabel(/^最大输出 Token/).fill('512');
  await save(manager, `${model}/configuration`);
  assert.equal((await api(manager, 'GET', `${model}/configuration`)).body.maxOutputTokens, 512);
  await manager.getByLabel('测试内容', { exact: true }).fill('请回复模型连接正常。');
  await manager.getByLabel('测试输出上限', { exact: true }).fill('128');
  await manager.getByRole('button', { name: '发送测试', exact: true }).click();
  await expect(manager.getByText('模型连接正常。', { exact: true })).toBeVisible();
  const calls = (await api(manager, 'GET', `${model}/calls`)).body.items;
  assert.equal(calls.length, 1); assert.equal(calls[0].outcome, 'completed');
  await expect(manager.getByText(calls[0].requestId, { exact: true }).first()).toBeVisible();
  await manager.screenshot({ path: resolve(directory, 'model-light.png'), fullPage: true });
  await manager.reload();
  await expect(manager.getByRole('heading', { name: '验收模型', exact: true })).toBeVisible();
  await expect(manager.getByText('修订 1', { exact: true })).toBeVisible();
  assert.equal((await api(manager, 'GET', `${model}/configuration`)).body.maxOutputTokens, 512);

  // A second authenticated tab changes the durable revision while the first edits.
  await manager.getByRole('button', { name: '编辑配置', exact: true }).click();
  await manager.getByLabel(/^最大输出 Token/).fill('256');
  const original = (await api(manager, 'GET', `${model}/configuration`)).body;
  assert.equal((await api(manager, 'PUT', `${model}/configuration`, { enabled: true, maxOutputTokens: 384, expectedRevision: original.revision })).status, 200);
  const conflict = manager.waitForResponse(response => response.request().method() === 'PUT' && response.status() === 409);
  await manager.getByRole('button', { name: '保存配置', exact: true }).click(); await conflict;
  await expect(manager.getByLabel(/^最大输出 Token/)).toHaveValue('256');
  await expect(manager.getByRole('button', { name: '保存配置', exact: true })).toBeDisabled();
  manager.once('dialog', dialog => dialog.accept());
  await manager.getByRole('link', { name: '数据源管理', exact: true }).click();
  await manager.goto('/manage/data-sources/factory-db');
  await expect(manager.getByRole('heading', { name: '制造测试数据源', exact: true })).toBeVisible();
  await expect(manager.getByText('每日产量', { exact: true })).toBeVisible();
  await manager.getByRole('button', { name: '编辑配置', exact: true }).click();
  await manager.getByLabel('启用当前租户', { exact: true }).check();
  await manager.getByLabel(/^最大返回行数/).fill('25');
  await manager.getByLabel(/^查询超时（秒）/).fill('3');
  await save(manager, `${source}/configuration`);
  const configured = (await api(manager, 'GET', `${source}/configuration`)).body.configuration;
  assert.equal(configured.maxRows, 25); assert.equal(configured.timeoutSeconds, 3);
  await manager.getByRole('button', { name: '测试连接', exact: true }).click();
  await expect(manager.getByText('连接测试通过。', { exact: true })).toBeVisible();
  const sourceCalls = (await api(manager, 'GET', `${source}/calls`)).body.items;
  assert.equal(sourceCalls.length, 1); assert.equal(sourceCalls[0].outcome, 'completed');
  await manager.getByRole('button', { name: '切换暗色', exact: true }).click();
  await manager.screenshot({ path: resolve(directory, 'source-dark.png'), fullPage: true });
  await manager.setViewportSize({ width: 390, height: 844 });
  assert.equal(await manager.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await manager.screenshot({ path: resolve(directory, 'source-mobile.png'), fullPage: true });
  await manager.setViewportSize({ width: 1366, height: 900 });

  const limited = await login('仅配置管理员', '/manage/models/assistant');
  await expect(limited.getByRole('button', { name: '编辑配置', exact: true })).toBeVisible();
  await expect(limited.getByRole('button', { name: '发送测试', exact: true })).toHaveCount(0);
  assert.equal((await api(limited, 'POST', `${model}/chat`, { messages: [{ role: 'user', content: 'deny' }], maxOutputTokens: 10, stream: false })).status, 403);
  await limited.goto('/manage/data-sources/factory-db');
  await expect(limited.getByRole('button', { name: '编辑配置', exact: true })).toBeVisible();
  await expect(limited.getByRole('button', { name: '测试连接', exact: true })).toHaveCount(0);
  assert.equal((await api(limited, 'POST', `${source}/test`, {})).status, 403);
  for (const account of ['普通成员', '平台身份管理员']) {
    const denied = await login(account, '/manage/models/assistant');
    await expect(denied.getByRole('button', { name: '编辑配置', exact: true })).toHaveCount(0);
    await expect(denied.getByRole('link', { name: '模型管理', exact: true })).toHaveCount(0);
    await expect(denied.getByRole('link', { name: '数据源管理', exact: true })).toHaveCount(0);
    assert.deepEqual((await api(denied, 'GET', `${tenant}/models`)).body.items, []);
    assert.equal((await api(denied, 'GET', `${model}/configuration`)).status, 403);
    assert.equal((await api(denied, 'GET', `${source}/configuration`)).status, 403);
    await denied.context().close();
  }
  // Selecting another tenant clears the visible former-tenant resource and test output.
  await manager.getByLabel('当前租户', { exact: true }).selectOption(config.otherTenantId);
  await expect.poll(async () => (await api(manager, 'GET', '/bff/tenant')).body.tenantId).toBe(config.otherTenantId);
  await expect(manager.getByRole('heading', { name: '制造测试数据源', exact: true })).toHaveCount(0);
  assert.equal((await api(manager, 'GET', `${source}/configuration`)).status, 403);
  const storage = await limited.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }));
  assert.ok(!storage.includes('fixture-access-token') && !storage.includes('fixture-refresh-token') && !storage.includes('模型连接正常'));
  await limited.getByRole('button', { name: '退出登录', exact: true }).click();
  await expect(limited.getByRole('link', { name: '企业账号登录', exact: true })).toBeVisible();
  assert.equal((await limited.context().request.get(`${source}/configuration`)).status(), 401);
  assert.deepEqual(failures, []);
  process.stdout.write('PASS: built resource pages persist model/source config and real CAS, test local model/read-only source, display calls, enforce exact grants and identity-admin separation, tenant switch, themes/mobile, deep links and logout.\n');
} catch (error) {
  const page = browser.contexts()[0]?.pages()[0];
  if (page) {
    await page.screenshot({ path: resolve(directory, 'failure.png'), fullPage: true }).catch(() => {});
    process.stderr.write((await page.locator('body').innerText()).slice(0, 12000) + '\n');
  }
  throw error;
} finally { await browser.close(); }
