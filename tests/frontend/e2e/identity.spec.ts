import { chooseReferenceOption, closeReferenceDrawer, expectReferenceValue, fulfillFixtureLogin, logoutFromUserMenu, toggleReferenceTheme } from '../reference-controls-helpers.mjs';
import { expect, test, type Page } from '@playwright/test';
const tenant = '11111111-1111-4111-8111-111111111111';
const user = '33333333-3333-4333-8333-333333333333';
const roleId = '44444444-4444-4444-8444-444444444444';
async function fixture(page: Page, administrator = true) {
  const state = { selected: administrator ? null as string | null : tenant, signedIn: true, status: 200, errorCode: 'identity_conflict', writes: [] as any[],
    workspace: { actorUserId: user, platformAdministrator: administrator,
      audit: { items: [], hasMore: false },
      tenants: [{ tenantId: tenant, name: '华东制造', enabled: true, revision: 1 }],
      users: administrator ? [{ userId: user, issuer: 'https://identity.fixture', subject: 'admin', displayName: '管理用户', enabled: true, platformAdministrator: true, revision: 1 }] : [] },
    detail: { actorUserId: user, platformAdministrator: administrator,
      tenant: { tenantId: tenant, name: '华东制造', enabled: true, revision: 1 },
      members: [{ userId: user, displayName: '管理用户', enabled: true, userEnabled: true, administrator: true, roleIds: [roleId], revision: 1 }],
      organizations: [{ organizationId: '55555555-5555-4555-8555-555555555555', name: '一号工厂', kind: 'factory', enabled: true, revision: 1 }],
      roles: [{ roleId, name: '生产查看', enabled: true, revision: 1, grants: [{ permission: 'chatbi:use', resourceType: 'application', resourceId: 'chatbi', tenantWide: true, organizationIds: [] as string[] }] }],
      audit: { items: [] as any[], hasMore: false } } };
  await page.route('**/bff/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/bff/login') return fulfillFixtureLogin(route);
    if (path === '/bff/session') return route.fulfill({ status: state.signedIn ? 200 : 401, json: { iss: 'https://identity.fixture', sub: 'admin', name: '管理用户' } });
    if (path === '/bff/csrf') return route.fulfill({ json: { headerName: 'X-CSRF-TOKEN', requestToken: 'fresh' } });
    if (path === '/bff/logout') { state.signedIn = false; return route.fulfill({ status: 204 }); }
    if (route.request().method() === 'POST') state.selected = route.request().postDataJSON().tenantId;
    return route.fulfill({ json: { tenantId: state.selected } });
  });
  await page.route('**/api/platform/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/platform/v1/tenants') return route.fulfill({ json: { items: administrator ? [] : state.workspace.tenants } });
    if (path.endsWith('/identity-management/workspace')) return route.fulfill({ json: state.workspace });
    if (route.request().method() === 'PUT') {
      expect(route.request().headers()['x-csrf-token']).toBe('fresh');
      const body = route.request().postDataJSON(); state.writes.push({ path, body });
      if (state.status !== 200) return route.fulfill({ status: state.status, json: { code: state.errorCode } });
      const section = path.split('/').at(-2)!; const id = path.split('/').at(-1)!;
      const collection = section === 'users' ? state.workspace.users : section === 'tenants' ? state.workspace.tenants : (state.detail as any)[section];
      const idKey = { users: 'userId', tenants: 'tenantId', members: 'userId', organizations: 'organizationId', roles: 'roleId' }[section]!;
      const entity = collection.find((item: any) => item[idKey] === id);
      const updated = { ...entity, ...body, [idKey]: id, revision: body.expectedRevision + 1 };
      if (entity) Object.assign(entity, updated); else collection.push(updated);
      state.detail.audit.items.unshift({ eventId: String(state.writes.length), actorUserId: user, tenantId: tenant, entityType: section, entityId: id, action: 'save', result: 'success', requestId: 'identity-request', occurredAt: '2026-11-05T08:00:00Z' });
      return route.fulfill({ json: { revision: updated.revision } });
    }
    if (path.endsWith(`/identity-management/tenants/${tenant}`)) return route.fulfill({ json: state.detail });
    return route.fulfill({ status: 404, json: {} });
  });
  return state;
}
test('T13: global administrator manages users and tenants without a membership selection', async ({ page }) => {
  const state = await fixture(page);
  await page.goto(`/manage/users/${user}`);
  await page.getByRole('button', { name: '编辑资料', exact: true }).click();
  await page.getByLabel('显示名称', { exact: true }).fill('更新后的管理用户');
  await page.getByRole('button', { name: '保存变更', exact: true }).click();
  await expect(page.getByRole('heading', { name: '更新后的管理用户', exact: true })).toBeVisible();
  expect(state.writes[0].body.expectedRevision).toBe(1);
  await closeReferenceDrawer(page);
  await page.getByRole('link', { name: '租户管理', exact: true }).click();
  await page.getByRole('link', { name: '华东制造', exact: true }).click();
  await page.getByRole('link', { name: '管理租户成员', exact: true }).click();
  await expect(page.getByRole('heading', { name: '租户成员', exact: true })).toBeVisible();
  expect(state.selected).toBeNull();
});
test('T13: tenant administrator edits role resource scopes and member assignments without global controls', async ({ page }) => {
  const state = await fixture(page, false);
  await page.goto(`/manage/roles/${roleId}`);
  await expect(page.getByRole('link', { name: '用户管理', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: '编辑资料', exact: true }).click();
  await page.getByLabel('角色名称', { exact: true }).fill('本厂生产查看');
  await page.getByLabel('整个租户', { exact: true }).uncheck();
  await page.getByLabel('一号工厂', { exact: true }).check();
  await page.getByRole('button', { name: '保存变更', exact: true }).click();
  await expect(page.getByRole('heading', { name: '本厂生产查看', exact: true })).toBeVisible();
  await expect(page.getByText('角色 · 保存 · 成功', { exact: true })).toBeVisible();
  await expect(page.getByRole('table', { name: '最近身份操作', exact: true }).locator('td').filter({ hasText: '本厂生产查看' })).toHaveAttribute('title', roleId);
  await expect(page.getByRole('table', { name: '最近身份操作', exact: true }).locator('td').filter({ hasText: '本厂生产查看' })).toContainText('本厂生产查看');
  await page.screenshot({ path: '.artifacts/t13-identity-ui/role-light.png', fullPage: true });
  await toggleReferenceTheme(page);
  await page.screenshot({ path: '.artifacts/t13-identity-ui/role-dark.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  await closeReferenceDrawer(page);
  const navigation = page.getByRole('navigation', { name: '主导航' });
  expect((await navigation.boundingBox())!.height).toBeLessThan(80);
  expect(await navigation.evaluate(element => element.scrollWidth > element.clientWidth)).toBe(true);
  await page.getByRole('link', { name: '应用中心', exact: true }).focus();
  await page.getByRole('link', { name: '角色权限', exact: true }).focus();
  const focused = await page.getByRole('link', { name: '角色权限', exact: true }).boundingBox();
  expect(focused!.x).toBeGreaterThanOrEqual(0);
  await expect.poll(async () => { const box = await page.getByRole('link', { name: '角色权限', exact: true }).boundingBox(); return box!.x + box!.width; }).toBeLessThanOrEqual(390);
  expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
  await page.screenshot({ path: '.artifacts/t13-identity-ui/role-mobile.png', fullPage: true });
  await page.setViewportSize({ width: 1366, height: 900 });
  expect(state.writes[0].body.grants[0].tenantWide).toBe(false);
  expect(state.writes[0].body.grants[0].organizationIds).toHaveLength(1);
  await page.getByRole('link', { name: '租户成员', exact: true }).click();
  await page.getByRole('link', { name: '管理用户', exact: true }).click();
  await page.getByRole('button', { name: '编辑资料', exact: true }).click();
  await expect(page.getByLabel('租户管理员', { exact: true })).toBeDisabled();
});
test('T13: conflict preserves edits, blocks repeated saves and confirmed navigation discards', async ({ page }) => {
  const state = await fixture(page); state.status = 409;
  await page.goto(`/manage/users/${user}`);
  await page.getByRole('button', { name: '编辑资料', exact: true }).click();
  await page.getByLabel('显示名称', { exact: true }).fill('保留我的输入');
  await page.getByRole('button', { name: '保存变更', exact: true }).click();
  await expect(page.getByRole('alert').first()).toContainText('不会自动覆盖');
  await expect(page.getByLabel('显示名称', { exact: true })).toHaveValue('保留我的输入');
  await expect(page.getByRole('button', { name: '保存变更', exact: true })).toBeDisabled();
  expect(state.writes).toHaveLength(1);
  await page.getByRole('button', { name: '取消编辑', exact: true }).click();
  await page.getByRole('dialog', { name: '放弃修改？', exact: true }).getByRole('button', { name: '放弃修改', exact: true }).click();
  await closeReferenceDrawer(page);
  await closeReferenceDrawer(page);
  await page.getByRole('link', { name: '租户管理', exact: true }).click();
  await expect(page.getByLabel('显示名称', { exact: true })).toHaveCount(0);
});
test('T13: organization create/readback, responsive themes and logout clear identity data', async ({ page }) => {
  const state = await fixture(page, false);
  await page.goto('/manage/organizations');
  await page.getByRole('button', { name: '新增组织', exact: true }).click();
  await page.getByLabel('组织名称', { exact: true }).fill('二号工厂');
  await page.getByRole('button', { name: '保存变更', exact: true }).click();
  await expect(page.getByRole('heading', { name: '二号工厂', exact: true })).toBeVisible();
  expect(state.writes[0].body.expectedRevision).toBe(0);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
  await toggleReferenceTheme(page);
  expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
  await logoutFromUserMenu(page);
  await expect(page.getByText('二号工厂', { exact: true })).toHaveCount(0);
});
test('T13: a new grant starts without scope and requires an explicit organization or tenant choice', async ({ page }) => {
  const state = await fixture(page, false);
  await page.goto('/manage/roles');
  await page.getByRole('button', { name: '新增角色', exact: true }).click();
  await page.getByLabel('角色名称', { exact: true }).fill('质检查看');
  await page.getByRole('button', { name: '添加权限', exact: true }).click();
  await page.getByLabel('权限名称', { exact: true }).fill('quality:read');
  await page.getByLabel('资源标识', { exact: true }).fill('quality-report');
  await expect(page.getByLabel('整个租户', { exact: true })).not.toBeChecked();
  await page.getByRole('button', { name: '保存变更', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('选择整个租户或至少一个组织');
  expect(state.writes).toHaveLength(0);
  await page.getByLabel('一号工厂', { exact: true }).check();
  await page.getByRole('button', { name: '保存变更', exact: true }).click();
  await expect(page.getByRole('heading', { name: '质检查看', exact: true })).toBeVisible();
  expect(state.writes[0].body.grants[0].tenantWide).toBe(false);
});
test('T13: successful write with failed readback is never presented as safe to resubmit', async ({ page }) => {
  const state = await fixture(page);
  await page.goto(`/manage/users/${user}`);
  await page.getByRole('button', { name: '编辑资料', exact: true }).click();
  await page.getByLabel('显示名称', { exact: true }).fill('已提交的名字');
  let failReadback = true;
  await page.route('**/identity-management/workspace', route => state.writes.length && failReadback
    ? route.fulfill({ status: 503, json: { code: 'identity_unavailable' } }) : route.fulfill({ json: state.workspace }));
  await page.getByRole('button', { name: '保存变更', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('变更已提交，但最新状态读取失败');
  await expect(page.getByRole('button', { name: '保存变更', exact: true })).toHaveCount(0);
  failReadback = false;
  await page.getByRole('button', { name: '重新读取状态', exact: true }).click();
  await expect(page.getByRole('heading', { name: '已提交的名字', exact: true })).toBeVisible();
  expect(state.writes).toHaveLength(1);
});
test('T13: empty, unavailable and permission-denied states can recover without stale records', async ({ page }) => {
  const state = await fixture(page, false); state.detail.organizations = [];
  await page.goto('/manage/organizations');
  await expect(page.getByText('暂无组织', { exact: true })).toBeVisible();
  let status = 503;
  await page.route(`**/identity-management/tenants/${tenant}`, route => status === 200 ? route.fulfill({ json: state.detail }) : route.fulfill({ status, json: {} }));
  await page.getByRole('button', { name: '刷新管理数据', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('暂时无法确认管理状态');
  status = 403;
  await page.getByRole('button', { name: '重新读取状态', exact: true }).click();
  await expect(page.getByRole('heading', { name: '没有管理权限', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: '新增组织', exact: true })).toHaveCount(0);
  status = 200;
  await page.getByRole('button', { name: '重新读取状态', exact: true }).click();
  await expect(page.getByRole('button', { name: '新增组织', exact: true })).toBeEnabled();
});
test('T13: tenant switch clears old identity records and ignores a late response', async ({ page }) => {
  const state = await fixture(page, false);
  const second = '22222222-2222-4222-8222-222222222222';
  state.workspace.tenants.push({ tenantId: second, name: '华南制造', enabled: true, revision: 1 });
  await page.route(`**/identity-management/tenants/${second}`, route => route.fulfill({ json: { ...state.detail, tenant: state.workspace.tenants[1], members: [], organizations: [], roles: [] } }));
  await page.goto(`/manage/tenants/${tenant}/organizations`);
  await expect(page.getByRole('link', { name: '一号工厂', exact: true })).toBeVisible();
  let entered!: () => void; let release!: () => void;
  const pending = new Promise<void>(resolve => { entered = resolve; });
  const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route(`**/identity-management/tenants/${tenant}`, async route => {
    entered(); await gate;
    try { await route.fulfill({ json: state.detail }); } catch { /* The prior tenant request has been cancelled. */ }
  });
  await page.evaluate(() => window.dispatchEvent(new Event('focus'))); await pending;
  await chooseReferenceOption(page, '当前租户', second);
  await expect(page.getByText('暂无组织', { exact: true })).toBeVisible();
  release();
  await expect(page.getByRole('link', { name: '一号工厂', exact: true })).toHaveCount(0);
  await expectReferenceValue(page, '当前租户', second);
});
test('T13: an assigned disabled organization can be removed without silently widening scope', async ({ page }) => {
  const state = await fixture(page, false);
  state.detail.organizations[0].enabled = false;
  state.detail.roles[0].grants[0].tenantWide = false;
  state.detail.roles[0].grants[0].organizationIds = [state.detail.organizations[0].organizationId];
  await page.goto(`/manage/roles/${roleId}`);
  await page.getByRole('button', { name: '编辑资料', exact: true }).click();
  const organization = page.getByLabel('一号工厂（已停用）', { exact: true });
  await expect(organization).toBeEnabled();
  await organization.uncheck();
  await expect(organization).toBeDisabled();
  await expect(page.getByLabel('整个租户', { exact: true })).not.toBeChecked();
  await page.getByRole('button', { name: '移除权限 1', exact: true }).click();
  await page.getByRole('button', { name: '保存变更', exact: true }).click();
  await expect(page.getByRole('heading', { name: '生产查看', exact: true })).toBeVisible();
  expect(state.writes[0].body.grants).toEqual([]);
});
test('T13: capacity includes disabled records and blocks creation while existing records remain editable', async ({ page }) => {
  const state = await fixture(page, false);
  state.detail.organizations = Array.from({ length: 500 }, (_, index) => ({ organizationId: `55555555-5555-4555-8555-${String(index + 1).padStart(12, '0')}`, name: `工厂 ${index + 1}`, kind: 'factory', enabled: index === 0, revision: 1 }));
  await page.goto(`/manage/organizations/${state.detail.organizations[0].organizationId}`);
  await expect(page.getByRole('button', { name: '新增组织', exact: true })).toBeDisabled();
  await expect(page.getByText('已达到当前管理容量（500条），现有记录仍可编辑；扩容需调整部署方案。')).toBeVisible();
  await closeReferenceDrawer(page);
  await page.getByLabel('搜索组织', { exact: true }).fill('工厂 1');
  await expect(page.getByRole('button', { name: '新增组织', exact: true })).toBeDisabled();
  await page.getByRole('link', { name: '工厂 1', exact: true }).click();
  await page.getByRole('button', { name: '编辑资料', exact: true }).click();
  await page.getByLabel('组织名称', { exact: true }).fill('现有工厂更名');
  await page.getByRole('button', { name: '保存变更', exact: true }).click();
  await expect(page.getByRole('heading', { name: '现有工厂更名', exact: true })).toBeVisible();
  expect(state.writes).toHaveLength(1);
  expect(state.writes[0].body.expectedRevision).toBe(1);
});
test('T13: management breadcrumb follows the explicit target without changing the business tenant selection', async ({ page }) => {
  const state = await fixture(page);
  const second = '22222222-2222-4222-8222-222222222222';
  state.selected = tenant;
  state.workspace.tenants.push({ tenantId: second, name: '华南制造', enabled: true, revision: 1 });
  await page.route('**/api/platform/v1/tenants', route => route.fulfill({ json: { items: [state.workspace.tenants[0]] } }));
  await page.route(`**/identity-management/tenants/${second}`, route => route.fulfill({ json: { ...state.detail, tenant: state.workspace.tenants[1] } }));
  await page.goto(`/manage/tenants/${second}/organizations`);
  await expect(page.locator('.portal-breadcrumb')).toContainText('管理租户：华南制造');
  await expect(page.locator('.portal-breadcrumb')).not.toContainText('华东制造');
  await expectReferenceValue(page, '当前租户', tenant);
  await page.getByRole('link', { name: '用户管理', exact: true }).click();
  await expect(page.locator('.portal-breadcrumb')).toContainText('平台管理');
  await expectReferenceValue(page, '当前租户', tenant);
});
test('T13: an explicit aggregate-capacity rejection preserves editable input and only retries after a manual save', async ({ page }) => {
  const state = await fixture(page, false); state.status = 409; state.errorCode = 'identity_capacity_reached';
  await page.goto(`/manage/roles/${roleId}`);
  await page.getByRole('button', { name: '编辑资料', exact: true }).click();
  await page.getByLabel('角色名称', { exact: true }).fill('保留完整角色输入');
  await page.getByRole('button', { name: '保存变更', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('记录数量或资料/授权范围达到管理容量');
  await expect(page.getByRole('alert')).not.toContainText('记录已被更新');
  await expect(page.getByLabel('角色名称', { exact: true })).toHaveValue('保留完整角色输入');
  await expect(page.getByLabel('角色名称', { exact: true })).toBeEnabled();
  await expect(page.getByRole('button', { name: '保存变更', exact: true })).toBeEnabled();
  expect(state.detail.roles).toHaveLength(1); expect(state.writes).toHaveLength(1);
  await page.getByRole('button', { name: '移除权限 1', exact: true }).click();
  expect(state.writes).toHaveLength(1);
  state.status = 200;
  await page.getByRole('button', { name: '保存变更', exact: true }).click();
  await expect(page.getByRole('heading', { name: '保留完整角色输入', exact: true })).toBeVisible();
  expect(state.writes).toHaveLength(2);
  expect(state.writes[1].body).toMatchObject({ expectedRevision: 1, grants: [] });
});


test('V37: a short desktop viewport keeps the final authorized navigation reachable', async ({ page }) => {
  await fixture(page, false); await page.setViewportSize({ width: 1366, height: 260 });
  await page.goto('/manage/organizations'); await expect(page.getByText('一号工厂', { exact: true })).toBeVisible();
  const sidebar = page.locator('.portal-sidebar .sidebar-scroll');
  expect(await sidebar.evaluate(node => node.scrollHeight > node.clientHeight)).toBe(true);
  const roles = page.getByRole('link', { name: '角色权限', exact: true });
  await roles.focus(); await roles.click();
  await expect(page).toHaveURL(new RegExp(`/manage/tenants/${tenant}/roles$`));
  await expect(page.getByRole('link', { name: '生产查看', exact: true })).toBeVisible();
  const rect = await roles.boundingBox(); expect(rect!.y).toBeGreaterThanOrEqual(0); expect(rect!.y + rect!.height).toBeLessThanOrEqual(260);
});
