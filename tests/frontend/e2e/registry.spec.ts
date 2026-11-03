import { expect, test, type Page } from '@playwright/test';

const tenantA = '11111111-1111-4111-8111-111111111111';
const tenantB = '22222222-2222-4222-8222-222222222222';
const actor = '33333333-3333-4333-8333-333333333333';
function release(version = '1.0.0', status = 'draft') {
  return { manifest: { schemaVersion: 1, appId: 'sample', name: '生产报表', version, category: '生产运营', description: '原始说明', frontendEntry: '/apps/sample/0.1.0/', frontendArtifact: { version: '0.1.0', sha256: 'a'.repeat(64) }, backendService: 'sample', backendVersion: '0.1.0', permissions: ['sample:use'], status }, revision: 1, ownerUserId: actor, submittedBy: actor, publishedAt: null };
}
async function fixture(page: Page, tenantOnly = false, noTenant = false) {
  const state = { signedIn: true, selected: noTenant ? null as string | null : tenantA as string | null, canEdit: !tenantOnly, canReview: !tenantOnly,
    releases: [release()], saveStatus: 200, writes: [] as { path: string; body: Record<string, unknown> }[], current: { releaseVersion: '1.0.0', enabled: true, available: true, revision: 2 } };
  await page.route('**/bff/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/bff/session') return route.fulfill({ status: state.signedIn ? 200 : 401, json: { iss: 'https://identity.fixture', sub: 'registry-actor', name: '管理用户' } });
    if (path === '/bff/csrf') return route.fulfill({ json: { headerName: 'X-CSRF-TOKEN', requestToken: 'fixture-csrf' } });
    if (route.request().method() === 'POST') {
      expect(route.request().headers()['x-csrf-token']).toBe('fixture-csrf');
      if (path === '/bff/logout') { state.signedIn = false; return route.fulfill({ status: 204 }); }
      if (path === '/bff/tenant') state.selected = route.request().postDataJSON().tenantId;
    }
    return route.fulfill({ json: { tenantId: state.selected } });
  });
  await page.route('**/api/platform/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (route.request().method() !== 'GET') {
      expect(route.request().headers()['x-csrf-token']).toBe('fixture-csrf');
      const body = route.request().postDataJSON(); state.writes.push({ path, body });
      if (state.saveStatus !== 200) return route.fulfill({ status: state.saveStatus, json: { code: 'conflict' } });
      if (path.endsWith('/transitions')) {
        const value = state.releases.find(item => path.includes(`/releases/${item.manifest.version}/`))!;
        value.manifest.status = { submit: 'pending_review', publish: 'published', reject: 'draft', disable: 'disabled', enable: 'published' }[body.action as string] ?? 'draft'; value.revision++;
        return route.fulfill({ json: value });
      }
      if (path.includes('/releases/')) {
        const value = { ...release(body.manifest.version), manifest: body.manifest, revision: body.expectedRevision + 1 };
        state.releases = [...state.releases.filter(item => item.manifest.version !== value.manifest.version), value];
        return route.fulfill({ json: value });
      }
      state.current = { releaseVersion: body.releaseVersion, enabled: body.enabled, available: body.enabled, revision: body.expectedRevision + 1 };
      return route.fulfill({ json: state.current });
    }
    if (path === '/api/platform/v1/tenants') return route.fulfill({ json: { items: noTenant ? [] : [{ tenantId: tenantA, name: '华东制造' }, { tenantId: tenantB, name: '华南制造' }] } });
    const isTenant = path.includes('/tenants/');
    if (path.endsWith('/workspace')) return route.fulfill({ json: isTenant
      ? { actorUserId: actor, tenantId: state.selected, items: state.selected === tenantA ? [{ appId: 'sample', name: '生产报表', category: '生产运营', current: state.current }] : [] }
      : { actorUserId: actor, canCreate: !tenantOnly, canReview: state.canReview, items: !tenantOnly ? [{ appId: 'sample', name: '生产报表', category: '生产运营', ownerUserId: actor, canEdit: state.canEdit, canReview: state.canReview }] : [] } });
    if (path.endsWith('/audit')) return route.fulfill({ json: { items: [{ eventId: isTenant ? 'tenant-event' : 'global-event', actorUserId: actor, tenantId: isTenant ? state.selected : null, appId: 'sample', releaseVersion: '1.0.0', action: isTenant ? 'set_tenant' : 'save_draft', result: 'success', requestId: 'registry-request', occurredAt: '2026-11-03T08:00:00Z' }], hasMore: true } });
    if (path.endsWith('/releases')) return route.fulfill({ json: { items: isTenant ? state.releases.filter(item => item.manifest.status === 'published') : state.releases } });
    return route.fulfill({ status: 404, json: {} });
  });
  return state;
}

test('T8: global management needs no tenant and self-submission cannot be published by its author', async ({ page }) => {
  const state = await fixture(page, false, true);
  await page.goto('/manage/applications/sample');
  await expect(page.getByRole('button', { name: '注册应用', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: '编辑草稿', exact: true })).toBeVisible();
  await page.getByRole('button', { name: '提交审核', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: '确认提交审核', exact: true }).click();
  await expect(page.getByText('此版本由你提交，需要另一位审核人发布。')).toBeVisible();
  await expect(page.getByRole('button', { name: '发布版本', exact: true })).toHaveCount(0);
  expect(state.writes[0].body).toEqual({ expectedRevision: 1, action: 'submit' });
});

test('T8: conflicting draft saves retain input and never retry or overwrite automatically', async ({ page }) => {
  const state = await fixture(page); state.saveStatus = 409;
  await page.goto('/manage/applications/sample');
  await page.getByRole('button', { name: '编辑草稿', exact: true }).click();
  await page.getByLabel('说明', { exact: true }).fill('未保存的重要修改');
  await page.getByRole('button', { name: '保存草稿', exact: true }).click();
  await expect(page.getByRole('alert').first()).toContainText('不会自动覆盖');
  await expect(page.getByLabel('说明', { exact: true })).toHaveValue('未保存的重要修改');
  await expect(page.getByRole('button', { name: '保存草稿', exact: true })).toBeDisabled();
  expect(state.writes).toHaveLength(1);
});

test('T8: confirmed discard on the same management route removes the draft', async ({ page }) => {
  const state = await fixture(page);
  await page.goto('/manage/applications');
  await page.getByRole('button', { name: '注册应用', exact: true }).click();
  await page.getByLabel('应用名称', { exact: true }).fill('应当放弃的新应用草稿');
  let confirmations = 0;
  page.on('dialog', async dialog => { confirmations++; await dialog.accept(); });
  await page.getByRole('link', { name: '应用管理', exact: true }).click();
  await expect(page.getByRole('form', { name: '应用草稿', exact: true })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: '管理应用发布与租户使用', exact: true })).toBeVisible();
  expect(confirmations).toBe(1);
  expect(state.writes).toHaveLength(0);
});

test('T8: quiet focus refresh preserves dirty input while changed revisions make the draft stale', async ({ page }) => {
  const state = await fixture(page);
  await page.goto('/manage/applications/sample');
  await page.getByRole('button', { name: '编辑草稿', exact: true }).click();
  await page.getByLabel('应用名称', { exact: true }).fill('我正在编辑的名称');
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(page.getByRole('button', { name: '保存草稿', exact: true })).toBeEnabled();
  await expect(page.getByLabel('应用名称', { exact: true })).toHaveValue('我正在编辑的名称');
  state.releases[0].revision++;
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(page.getByRole('alert')).toContainText('此草稿保留');
  await expect(page.getByRole('button', { name: '保存草稿', exact: true })).toBeDisabled();
  await expect(page.getByLabel('应用名称', { exact: true })).toHaveValue('我正在编辑的名称');
  expect(state.writes).toHaveLength(0);
});

test('T8: tenant-only management hides global controls and can disable an unavailable current version', async ({ page }) => {
  const state = await fixture(page, true); state.releases = [release('2.0.0', 'published')]; state.current.available = false;
  await page.goto('/manage/applications/sample');
  await expect(page.getByRole('button', { name: '注册应用', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: '全局停用', exact: true })).toHaveCount(0);
  await expect(page.getByText('全局版本不可用', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: '停用当前租户应用', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: '确认租户变更', exact: true }).click();
  await expect(page.getByText('租户已停用', { exact: true })).toBeVisible();
  expect(state.writes[0].body).toEqual({ expectedRevision: 2, releaseVersion: '1.0.0', enabled: false });
  await page.getByLabel('租户使用版本', { exact: true }).selectOption('2.0.0');
  await page.getByRole('button', { name: '启用到当前租户', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: '确认租户变更', exact: true }).click();
  await expect(page.getByText('租户已启用', { exact: true })).toBeVisible();
  expect(state.writes[1].body).toEqual({ expectedRevision: 3, releaseVersion: '2.0.0', enabled: true });
  await expect(page.getByTestId('registry-audit')).toContainText('调整租户应用');
});

test('T8: a late prior-tenant management response cannot restore the old list and logout clears it', async ({ page }) => {
  await fixture(page, true);
  await page.goto('/manage/applications/sample');
  await expect(page.getByRole('heading', { name: '生产报表', exact: true })).toBeVisible();
  let releaseGate!: () => void; let entered!: () => void;
  const gate = new Promise<void>(resolve => { releaseGate = resolve; });
  const pending = new Promise<void>(resolve => { entered = resolve; });
  await page.route(`**/tenants/${tenantA}/registry/workspace`, async route => {
    entered(); await gate;
    try { await route.fulfill({ json: { actorUserId: actor, tenantId: tenantA, items: [{ appId: 'secret', name: '旧租户迟到数据', category: '', current: null }] } }); } catch { /* Cancelled old request. */ }
  });
  await page.evaluate(() => window.dispatchEvent(new Event('focus'))); await pending;
  await page.getByRole('combobox', { name: '当前租户', exact: true }).selectOption(tenantB);
  await expect(page.getByRole('heading', { name: '应用不可管理', exact: true })).toBeVisible();
  releaseGate();
  await expect(page.getByText('旧租户迟到数据', { exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: '退出登录', exact: true }).click();
  await expect(page.getByRole('link', { name: '企业账号登录', exact: true })).toBeVisible();
  await expect(page.getByRole('region', { name: '应用注册管理' })).toHaveCount(0);
});

test('T8: refresh dismisses a pending confirmation and both themes fit a narrow viewport', async ({ page }) => {
  const state = await fixture(page); state.releases = [release('1.0.0', 'published')];
  await page.goto('/manage/applications/sample');
  await page.getByRole('button', { name: '全局停用', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(page.getByRole('dialog')).not.toBeVisible();
  expect(state.writes).toHaveLength(0);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
  await page.getByRole('button', { name: '切换暗色', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
  await expect(page.getByTestId('registry-audit')).toContainText(actor);
  await expect(page.getByTestId('registry-audit')).toContainText('registry-request');
});

test('T8: failed shell identity refresh explicitly reports clearing an unsaved management draft', async ({ page }) => {
  await fixture(page);
  await page.goto('/manage/applications/sample');
  await page.getByRole('button', { name: '编辑草稿', exact: true }).click();
  await page.getByLabel('说明', { exact: true }).fill('尚未保存的内容');
  await page.route('**/api/platform/v1/tenants', route => route.fulfill({ status: 503, json: { code: 'authorization_unavailable' } }));
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(page.getByRole('alert')).toContainText('未保存的草稿修改已清除');
  await expect(page.getByLabel('说明', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('link', { name: '企业账号登录', exact: true })).toHaveCount(0);
});
