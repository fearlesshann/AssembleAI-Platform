import { chooseReferenceOption, closeReferenceDrawer, expectFixtureLogin, fulfillFixtureLogin, toggleReferenceTheme } from '../reference-controls-helpers.mjs';
import { test, expect, type Page } from '@playwright/test';
const tenant = '11111111-1111-1111-1111-111111111111'; const other = '22222222-2222-2222-2222-222222222222';
async function fixture(page: Page) {
  const state = { selected: tenant, signedIn: true, status: 200, readFailure: false, modelFailure: false, writes: [] as any[], tests: [] as string[],
    model: { modelId: 'assistant.v1', displayName: '生产助手模型', enabled: true, canManage: true, canInvoke: true, deploymentEnabled: true, maximumOutputTokens: 256, deploymentMaximumOutputTokens: 2048, callTimeoutSeconds: 120 },
    source: { sourceId: 'factory_db', displayName: '制造测试数据源', enabled: true, canManage: true, canTest: true, deploymentEnabled: true, maximumRows: 100, timeoutSeconds: 5, deploymentMaximumRows: 500, deploymentTimeoutSeconds: 20, datasets: [{ datasetId: 'daily-production', displayName: '每日产量', columns: [{ name: 'production_count', type: 'integer' }, { name: 'line_name', type: 'text' }] }] },
    modelConfig: { tenantId: tenant, modelId: 'assistant.v1', enabled: true, maxOutputTokens: 256, revision: 1 } as any,
    sourceConfig: { tenantId: tenant, sourceId: 'factory_db', enabled: true, maxRows: 100, timeoutSeconds: 5, revision: 1 } as any,
    calls: [] as any[] };
  await page.route('**/bff/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/bff/login') return fulfillFixtureLogin(route);
    if (path === '/bff/session') return route.fulfill({ status: state.signedIn ? 200 : 401, json: { iss: 'https://identity.fixture', sub: 'resource-user', name: '资源管理用户' } });
    if (path === '/bff/csrf') return route.fulfill({ json: { headerName: 'X-CSRF-TOKEN', requestToken: 'fresh' } });
    if (path === '/bff/logout') { state.signedIn = false; return route.fulfill({ status: 204 }); }
    if (route.request().method() === 'POST') state.selected = route.request().postDataJSON().tenantId;
    return route.fulfill({ json: { tenantId: state.selected } });
  });
  await page.route('**/api/platform/v1/**', route => {
    const path = new URL(route.request().url()).pathname; const method = route.request().method();
    if (path === '/api/platform/v1/tenants') return route.fulfill({ json: { items: [{ tenantId: tenant, name: '华东制造' }, { tenantId: other, name: '华南制造' }] } });
    if (path.endsWith('/identity-management/workspace')) return route.fulfill({ status: 403, json: {} });
    if (path.endsWith('/catalog')) return route.fulfill({ json: { items: [], categories: [] } });
    const isModel = path.includes('/models');
    if (state.modelFailure && isModel) return route.fulfill({ status: 503, json: {} });
    if (path.endsWith('/models') || path.endsWith('/connectors')) return route.fulfill({ json: { items: state.selected === tenant ? [isModel ? state.model : state.source] : [] } });
    if (method === 'PUT') {
      expect(route.request().headers()['x-csrf-token']).toBe('fresh'); const body = route.request().postDataJSON(); state.writes.push(body);
      if (state.status !== 200) return route.fulfill({ status: state.status, json: { detail: 'private database' } });
      const value = { tenantId: tenant, ...(isModel ? { modelId: 'assistant.v1' } : { sourceId: 'factory_db' }), ...(isModel ? state.modelConfig : state.sourceConfig), ...body, revision: body.expectedRevision + 1 };
      if (isModel) { state.modelConfig = value; state.model.enabled = body.enabled; state.model.maximumOutputTokens = body.maxOutputTokens; }
      else { state.sourceConfig = value; state.source.enabled = body.enabled; state.source.maximumRows = body.maxRows; state.source.timeoutSeconds = body.timeoutSeconds; }
      return route.fulfill({ json: isModel ? value : { configuration: value } });
    }
    if (method === 'POST') {
      state.tests.push(path); state.calls = [{ callId: 'call-1', tenantId: tenant, userId: 'user-1', requestId: 'request-123', outcome: 'completed', elapsedMilliseconds: 28, startedAt: '2026-11-05T08:00:00Z', modelId: isModel ? 'assistant.v1' : undefined, sourceId: isModel ? undefined : 'factory_db', operation: isModel ? undefined : 'test', usage: isModel ? { promptTokens: 7, completionTokens: 9, totalTokens: 16 } : null }];
      return route.fulfill({ json: isModel ? { requestId: 'request-123', content: '<script>safe text</script>', finishReason: 'stop', usage: { promptTokens: 7, completionTokens: 9, totalTokens: 16 } } : { requestId: 'request-123', status: 'ready' } });
    }
    if (state.readFailure) return route.fulfill({ status: 503, json: {} });
    if (path.endsWith('/configuration')) return route.fulfill({ contentType: 'application/json', body: JSON.stringify(isModel ? state.modelConfig : { configuration: state.sourceConfig }) });
    if (path.endsWith('/calls')) return route.fulfill({ json: { items: state.calls } });
    return route.fulfill({ status: 404, json: {} });
  });
  return state;
}
test('T13: model config saves CAS and single invocation refreshes metadata without rendering HTML', async ({ page }) => {
  const state = await fixture(page); await page.goto('/manage/models/assistant.v1');
  await page.getByRole('button', { name: '编辑配置', exact: true }).click();
  await page.getByLabel('最大输出 Token', { exact: true }).fill('512'); await page.getByRole('button', { name: '保存配置', exact: true }).click();
  await expect(page.getByText('配置已保存，已重新读取最新状态。', { exact: true })).toBeVisible(); expect(state.writes[0].expectedRevision).toBe(1);
  await page.getByLabel('测试内容', { exact: true }).fill('请确认连接。'); await page.getByRole('button', { name: '发送测试', exact: true }).click();
  await expect(page.getByText('单次调用已完成。', { exact: true })).toBeVisible(); await expect(page.locator('.resource-output')).toHaveText('<script>safe text</script>');
  await expect(page.getByRole('table', { name: '最近调用记录' })).toContainText('request-123'); expect(state.tests).toHaveLength(1);
});
test('T13: source datasets, configuration, connection test, themes and narrow viewport', async ({ page }) => {
  const state = await fixture(page); await page.goto('/manage/data-sources/factory_db');
  await expect(page.getByRole('heading', { name: '每日产量', exact: true })).toBeVisible(); await expect(page.getByText('production_count', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: '编辑配置', exact: true }).click(); await page.getByLabel('最大返回行数', { exact: true }).fill('200');
  await page.getByLabel('查询超时（秒）', { exact: true }).fill('8'); await page.getByRole('button', { name: '保存配置', exact: true }).click();
  await expect(page.getByText('配置已保存，已重新读取最新状态。', { exact: true })).toBeVisible(); expect(state.writes[0].maxRows).toBe(200);
  await page.getByRole('button', { name: '测试连接', exact: true }).click(); await expect(page.getByText('连接测试通过。', { exact: true })).toBeVisible();
  await page.screenshot({ path: '.artifacts/t13-resources/source-light.png', fullPage: true });
  await toggleReferenceTheme(page); await page.screenshot({ path: '.artifacts/t13-resources/source-dark.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 }); expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
  await page.screenshot({ path: '.artifacts/t13-resources/source-mobile.png', fullPage: true });
});
test('T13: CAS conflict keeps input and blocks duplicate save; cancelled navigation keeps draft', async ({ page }) => {
  const state = await fixture(page); state.status = 409; await page.goto('/manage/models/assistant.v1');
  await page.getByRole('button', { name: '编辑配置', exact: true }).click(); await page.getByLabel('最大输出 Token', { exact: true }).fill('512');
  await page.getByRole('button', { name: '取消编辑', exact: true }).click(); await page.getByRole('dialog', { name: '放弃修改？', exact: true }).getByRole('button', { name: '取消', exact: true }).click();
  await expect(page.getByLabel('最大输出 Token', { exact: true })).toHaveValue('512'); await page.getByRole('button', { name: '保存配置', exact: true }).click();
  await expect(page.getByRole('alert').first()).toContainText('不会自动覆盖'); await expect(page.getByRole('button', { name: '保存配置', exact: true })).toBeDisabled(); expect(state.writes).toHaveLength(1);
  await page.getByRole('button', { name: '取消编辑', exact: true }).click(); await page.getByRole('dialog', { name: '放弃修改？', exact: true }).getByRole('button', { name: '放弃修改', exact: true }).click(); await closeReferenceDrawer(page); await page.getByRole('link', { name: '数据源管理', exact: true }).click(); await expect(page.getByLabel('最大输出 Token', { exact: true })).toHaveCount(0);
});
test('T13: management does not imply test authority, and invoke-only cannot load tenant configuration', async ({ page }) => {
  const state = await fixture(page); state.model.canInvoke = false; state.source.canTest = false;
  await page.goto('/manage/models/assistant.v1'); await expect(page.getByRole('button', { name: '编辑配置', exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: '发送测试', exact: true })).toHaveCount(0);
  await page.goto('/manage/data-sources/factory_db'); await expect(page.getByRole('button', { name: '测试连接', exact: true })).toHaveCount(0);
  state.model.canManage = false; state.model.canInvoke = true;
  await page.goto('/manage/models/assistant.v1'); await expect(page.getByRole('button', { name: '编辑配置', exact: true })).toHaveCount(0); await expect(page.getByRole('button', { name: '发送测试', exact: true })).toBeVisible();
});
test('T13: null configuration creates revision zero and checkbox first click is retained', async ({ page }) => {
  const state = await fixture(page); state.modelConfig = null; state.model.enabled = false;
  await page.goto('/manage/models/assistant.v1'); await page.getByRole('button', { name: '编辑配置', exact: true }).click();
  await page.getByLabel('启用当前租户', { exact: true }).check(); await expect(page.getByLabel('启用当前租户', { exact: true })).toBeChecked();
  await page.getByRole('button', { name: '保存配置', exact: true }).click(); await expect(page.getByText('配置已保存，已重新读取最新状态。', { exact: true })).toBeVisible(); expect(state.writes[0].expectedRevision).toBe(0);
});
test('T13: committed write plus failed readback never resubmits and recovery reads authoritative state', async ({ page }) => {
  const state = await fixture(page); await page.goto('/manage/models/assistant.v1'); await page.getByRole('button', { name: '编辑配置', exact: true }).click();
  await page.getByLabel('最大输出 Token', { exact: true }).fill('512'); state.readFailure = true;
  await page.getByRole('button', { name: '保存配置', exact: true }).click(); await expect(page.getByRole('alert')).toContainText('配置已提交'); expect(state.writes).toHaveLength(1);
  state.readFailure = false; await page.getByRole('button', { name: '重新读取状态', exact: true }).click();
  await expect(page.getByText('512 Token / 次', { exact: true })).toBeVisible(); expect(state.writes).toHaveLength(1);
});
test('T13: cancellation clears busy state and ignores a late test response without retry', async ({ page }) => {
  const state = await fixture(page); let release!: () => void; const gate = new Promise<void>(resolve => { release = resolve; }); let sent = 0;
  await page.route('**/models/assistant.v1/chat', async route => { sent++; await gate; await route.fulfill({ json: { content: 'late model output', requestId: 'late', usage: null } }).catch(() => {}); });
  await page.goto('/manage/models/assistant.v1'); await page.getByLabel('测试内容', { exact: true }).fill('请求测试'); await page.getByRole('button', { name: '发送测试', exact: true }).click();
  await expect.poll(() => sent).toBe(1); await page.getByRole('button', { name: '取消测试', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('已停止等待'); await expect(page.getByRole('button', { name: '发送测试', exact: true })).toBeDisabled(); release();
  await closeReferenceDrawer(page); await page.getByRole('link', { name: '数据源管理', exact: true }).click(); await expect(page.getByRole('heading', { name: '数据源管理', exact: true })).toBeVisible();
  expect(sent).toBe(1); expect(state.tests).toHaveLength(0); await expect(page.getByText('late model output', { exact: true })).toHaveCount(0);
});
test('T13: empty selection, missing resource and session loss clear previous data', async ({ page }) => {
  const state = await fixture(page); await page.goto('/manage/models/absent-model'); await expect(page.getByRole('heading', { name: '资源不可用', exact: true })).toBeVisible();
  await chooseReferenceOption(page, '当前租户', other); await expect(page.getByText('当前租户暂无已授权资源', { exact: true })).toBeVisible();
  await chooseReferenceOption(page, '当前租户', tenant); await page.goto('/manage/models/assistant.v1'); await expect(page.getByRole('heading', { name: '生产助手模型', exact: true })).toBeVisible();
  state.signedIn = false; await page.evaluate(() => window.dispatchEvent(new Event('focus'))); await expectFixtureLogin(page);
  await expect(page.getByRole('heading', { name: '生产助手模型', exact: true })).toHaveCount(0);
});
test('T13: delayed old tenant configuration cannot restore data after switching', async ({ page }) => {
  const state = await fixture(page); let release!: () => void; const gate = new Promise<void>(resolve => { release = resolve; }); let reading = false;
  await page.route(`**/tenants/${tenant}/models/assistant.v1/configuration`, async route => { reading = true; await gate; await route.fulfill({ json: state.modelConfig }).catch(() => {}); });
  await page.goto('/manage/models/assistant.v1'); await expect.poll(() => reading).toBe(true); await chooseReferenceOption(page, '当前租户', other); release();
  await expect(page.getByText('当前租户暂无已授权资源', { exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: '生产助手模型', exact: true })).toHaveCount(0); await expect(page.getByRole('button', { name: '编辑配置', exact: true })).toHaveCount(0);
});
test('T13: isolated module errors do not break other navigation, switching tenant clears resource access', async ({ page }) => {
  const state = await fixture(page); state.modelFailure = true; await page.goto('/manage/data-sources/factory_db');
  await expect(page.getByRole('heading', { name: '制造测试数据源', exact: true })).toBeVisible(); await expect(page.getByRole('link', { name: '模型管理', exact: true })).toHaveCount(0);
  await chooseReferenceOption(page, '当前租户', other); await expect(page.getByRole('link', { name: '数据源管理', exact: true })).toHaveCount(0); await expect(page.getByRole('heading', { name: '制造测试数据源', exact: true })).toHaveCount(0);
});
