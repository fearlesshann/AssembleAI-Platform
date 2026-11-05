import { chooseReferenceOption, expectOidcAccountPicker, logoutFromUserMenu, toggleReferenceTheme } from './reference-controls-helpers.mjs';
import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import { resolve, relative, isAbsolute } from 'node:path';
import { chromium, expect } from '@playwright/test';

// Real built portal -> OIDC -> BFF -> PostgreSQL. Never intercept an API route.
const configPath = resolve(process.argv[2]);
const local = relative(resolve('.local-data/postgres-tests'), configPath);
assert.ok(local && !local.startsWith('..') && !isAbsolute(local));
const config = JSON.parse(await readFile(configPath, 'utf8'));
const origin = new URL(config.origin);
assert.equal(origin.hostname, '127.0.0.1'); assert.equal(origin.protocol, 'http:');
const directory = resolve('.artifacts/identity-postgres', config.tenantId);
await mkdir(directory, { recursive: true });
const browser = await chromium.launch({ headless: true });
const failures = [];
const base = '/api/platform/v1/identity-management';
const tenant = `${base}/tenants/${config.tenantId}`;
try {
  async function login(account, route) {
    const context = await browser.newContext({ baseURL: origin.origin, viewport: { width: 1366, height: 900 }, reducedMotion: 'reduce' });
    const page = await context.newPage(); page.on('pageerror', error => failures.push(error.message));
    await page.goto('/bff/login?returnUrl=' + encodeURIComponent(route));
    await page.getByRole('link', { name: account, exact: true }).click();
    await expect(page.getByRole('button', { name: '用户菜单', exact: true })).toBeVisible();
    return page;
  }
  async function api(page, method, path, body) {
    return page.evaluate(async ({ method, path, body }) => {
      const headers = { Accept: 'application/json' };
      if (method !== 'GET') {
        const csrf = await fetch('/bff/csrf', { credentials: 'same-origin', cache: 'no-store' }).then(r => r.json());
        headers[csrf.headerName] = csrf.requestToken; headers['Content-Type'] = 'application/json';
      }
      const response = await fetch(path, { method, headers, credentials: 'same-origin', cache: 'no-store', body: body === undefined ? undefined : JSON.stringify(body) });
      return { status: response.status, body: await response.json() };
    }, { method, path, body });
  }
  async function save(page, name) {
    const mutation = page.waitForResponse(response => response.request().method() === 'PUT' && new URL(response.url()).pathname.startsWith(base));
    const roleEditor = page.getByRole('button', { name: '检查并保存', exact: true });
    if (await roleEditor.count()) await roleEditor.click();
    await page.getByRole('button', { name: '保存变更', exact: true }).click();
    assert.equal((await mutation).status(), 200);
    await expect(page.getByRole('heading', { name, exact: true, level: 2 })).toBeVisible();
    await expect(page.getByRole('button', { name: /^(编辑资料|编辑权限)$/ })).toBeEnabled();
  }
  const root = await login('平台管理员', '/manage/users');
  await root.getByRole('button', { name: '新增用户', exact: true }).click();
  await root.getByLabel('显示名称', { exact: true }).fill('浏览器新成员');
  await root.getByLabel('身份服务颁发者', { exact: true }).fill(config.issuer);
  await root.getByLabel('账号主体', { exact: true }).fill(`browser-${config.tenantId}`);
  await save(root, '浏览器新成员');
  const created = (await api(root, 'GET', `${base}/workspace`)).body.users.find(user => user.displayName === '浏览器新成员');
  assert.ok(created && !created.platformAdministrator);
  await root.screenshot({ path: resolve(directory, 'users-light.png'), fullPage: true });
  await root.goto(`/manage/tenants/${config.tenantId}`);
  await root.getByRole('button', { name: '编辑资料', exact: true }).click();
  await root.getByLabel('租户名称', { exact: true }).fill('身份验收工厂');
  await save(root, '身份验收工厂');
  await root.getByRole('link', { name: '管理租户成员' }).click();
  await root.getByRole('button', { name: '新增成员', exact: true }).click();
  await chooseReferenceOption(root, '用户 UUID', created.userId);
  await save(root, '浏览器新成员');

  const admin = await login('租户管理员', '/manage/organizations');
  await chooseReferenceOption(admin, '当前租户', config.tenantId);
  await expect.poll(async () => (await api(admin, 'GET', '/bff/tenant')).body.tenantId).toBe(config.tenantId);
  await admin.goto('/manage/organizations');
  await admin.getByRole('button', { name: '新增组织', exact: true }).click();
  await admin.getByLabel('组织名称', { exact: true }).fill('装配一厂');
  await admin.getByLabel('组织类型').fill('factory');
  await save(admin, '装配一厂');
  const org = (await api(admin, 'GET', tenant)).body.organizations.find(org => org.name === '装配一厂');
  assert.ok(org);
  await admin.goto('/manage/roles');
  await admin.getByRole('button', { name: '新增角色', exact: true }).click();
  await admin.getByLabel('角色名称', { exact: true }).fill('装配生产只读');
  await admin.getByRole('button', { name: '企业知识文档 · knowledge', exact: true }).click();
  await admin.getByRole('checkbox', { name: '阅读与问答', exact: true }).check();
  await chooseReferenceOption(admin, '生效范围', 'organizations');
  await admin.getByLabel('装配一厂', { exact: true }).check();
  await admin.getByRole('button', { name: '加入授权清单', exact: true }).click();
  await save(admin, '装配生产只读');
  const role = (await api(admin, 'GET', tenant)).body.roles.find(role => role.name === '装配生产只读');
  assert.equal(role.grants[0].permission, 'knowledge:read');
  assert.deepEqual(role.grants[0].organizationIds, [org.organizationId]);
  assert.equal(role.grants[0].tenantWide, false);
  await toggleReferenceTheme(admin);
  await admin.screenshot({ path: resolve(directory, 'roles-dark.png'), fullPage: true });
  await admin.goto(`/manage/members/${created.userId}`);
  await admin.getByRole('button', { name: '编辑资料', exact: true }).click();
  await expect(admin.getByLabel('租户管理员', { exact: true })).toBeDisabled();
  await admin.getByLabel('装配生产只读', { exact: true }).check();
  await save(admin, '浏览器新成员');
  assert.deepEqual((await api(admin, 'GET', tenant)).body.members.find(member => member.userId === created.userId).roleIds, [role.roleId]);
  await admin.reload();
  await expect(admin.getByRole('heading', { name: '浏览器新成员', exact: true, level: 2 })).toBeVisible();
  await admin.setViewportSize({ width: 390, height: 844 });
  assert.equal(await admin.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await admin.screenshot({ path: resolve(directory, 'members-mobile.png'), fullPage: true });
  await admin.setViewportSize({ width: 1366, height: 900 });
  await admin.goto(`/manage/organizations/${org.organizationId}`);
  await admin.getByRole('button', { name: '编辑资料', exact: true }).click();
  await admin.getByLabel('组织名称', { exact: true }).fill('未保存的工厂名称');
  const changed = await api(root, 'PUT', `${tenant}/organizations/${org.organizationId}`, { expectedRevision: 1, name: '已提交的工厂名称', kind: 'factory', enabled: true });
  assert.equal(changed.status, 200);
  const conflict = admin.waitForResponse(response => response.request().method() === 'PUT' && response.status() === 409);
  await admin.getByRole('button', { name: '保存变更', exact: true }).click(); await conflict;
  await expect(admin.getByRole('alert')).toContainText('记录已被更新');
  await expect(admin.getByLabel('组织名称', { exact: true })).toHaveValue('未保存的工厂名称');
  await expect(admin.getByRole('button', { name: '保存变更', exact: true })).toBeDisabled();
  const ordinary = await login('普通成员', '/manage/organizations');
  await chooseReferenceOption(ordinary, '当前租户', config.tenantId);
  await expect.poll(async () => (await api(ordinary, 'GET', '/bff/tenant')).body.tenantId).toBe(config.tenantId);
  await ordinary.goto('/manage/organizations');
  await expect(ordinary.getByRole('alert')).toBeVisible();
  await expect(ordinary.getByRole('button', { name: '新增组织', exact: true })).toHaveCount(0);
  assert.equal((await api(ordinary, 'GET', tenant)).status, 403);
  assert.equal((await api(admin, 'GET', `${base}/tenants/${config.otherTenantId}`)).status, 403);
  // No credentials or sensitive management state may be persisted in browser storage.
  const storage = await root.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }));
  assert.ok(!storage.includes('fixture-access-token') && !storage.includes('fixture-refresh-token') && !storage.includes(created.userId));
  await logoutFromUserMenu(root);
  await expectOidcAccountPicker(root);
  assert.equal((await root.context().request.get(`${base}/workspace`)).status(), 401);
  assert.deepEqual(failures, []);
  process.stdout.write('PASS: built identity pages create users, edit tenants, create scoped roles/organizations, assign members, preserve real CAS conflict input, deny ordinary/cross-tenant access, themes/mobile and logout.\n');
} finally { await browser.close(); }
