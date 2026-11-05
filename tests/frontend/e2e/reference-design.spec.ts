import { fulfillFixtureLogin } from '../reference-controls-helpers.mjs';
import { expect, test, type Page } from '@playwright/test';
import { resolve } from 'node:path';

const tenant = '11111111-1111-4111-8111-111111111111';
const tokens = ['frame-bg', 'plate-bg', 'panel-bg', 'surface-bg', 'surface-border', 'surface-shadow',
  'text-frame', 'text-primary', 'text-secondary', 'text-tertiary', 'hairline', 'nav-selected', 'nav-hover', 'nav-hover-strong',
  'accent', 'accent-hover', 'control-bg', 'control-bg-hover', 'pill-bg', 'pill-inset', 'pill-inset-hover',
  'table-shell-bg', 'table-header-bg', 'table-row-bg', 'divider-row', 'search-bg', 'search-inset', 'font', 'mono'];
const app = { appId: 'sample', name: '产线分析', releaseVersion: '1.0.0', category: '生产运营', description: '查看当前授权范围的生产记录。' };

async function formal(page: Page, path = '/') {
  await page.route('**/bff/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/bff/login') return fulfillFixtureLogin(route);
    if (path === '/bff/session') return route.fulfill({ json: { iss: 'https://design.fixture', sub: 'reader', name: '界面验收' } });
    if (path === '/bff/tenant') return route.fulfill({ json: { tenantId: tenant } });
    return route.fulfill({ status: 404, json: {} });
  });
  await page.route('**/api/platform/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/platform/v1/tenants') return route.fulfill({ json: { items: [{ tenantId: tenant, name: '制造工作区' }] } });
    if (path.endsWith('/catalog')) return route.fulfill({ json: { items: [app], categories: ['生产运营'] } });
    if (path.endsWith('/catalog/sample')) return route.fulfill({ json: app });
    if (path.endsWith('/models')) return route.fulfill({ json: { items: [] } });
    if (path.endsWith('/connectors')) return route.fulfill({ json: { items: [{ sourceId: 'factory', displayName: '制造数据源', enabled: true,
      canManage: true, canTest: false, deploymentEnabled: true, maximumRows: 100, timeoutSeconds: 5, deploymentMaximumRows: 500,
      deploymentTimeoutSeconds: 10, datasets: [{ datasetId: 'production', displayName: '每日产量', columns: [{ name: 'line', type: 'text' }] }] }] } });
    if (path.endsWith('/connectors/factory/configuration')) return route.fulfill({ json: { configuration: { tenantId: tenant, sourceId: 'factory', enabled: true, maxRows: 100, timeoutSeconds: 5, revision: 1 } } });
    if (path.endsWith('/calls')) return route.fulfill({ json: { items: [] } });
    return route.fulfill({ status: 404, json: {} });
  });
  await page.goto(path);
  await expect(page.getByRole('button', { name: '用户菜单', exact: true })).toBeVisible();
  await expect(page.locator(path === '/' ? '.portal-app-row' : '.resource-dataset').first()).toBeVisible();
}

async function reference(page: Page) {
  // Keep the original CSS and DOM unchanged; resolve its exact 4.1 font offline.
  await page.route('https://static.linear.app/fonts/**', route => route.request().url().includes('Italic')
    ? route.abort() : route.fulfill({ path: resolve('apps/portal/public/fonts/InterVariable.woff2'), contentType: 'font/woff2' }));
  await page.goto('/reference-ui/?motion=reduced');
  await page.getByLabel('账号 / 邮箱').fill('design@example.test');
  await page.getByLabel('密码').fill('reference-only-fixture');
  await page.getByRole('button', { name: '进入工作台', exact: true }).click();
  await expect(page.locator('#appShell')).toBeVisible();
  await page.getByRole('navigation', { name: '主导航', exact: true }).getByRole('button', { name: '应用中心', exact: true }).click();
}

async function appearance(page: Page, theme: string) {
  await page.evaluate(theme => { document.documentElement.dataset.theme = theme; document.documentElement.dataset.motion = 'reduced'; }, theme);
  await page.evaluate(() => document.fonts.ready);
}
async function css(page: Page, selector: string, fields: string[]) {
  return page.locator(selector).first().evaluate((node, fields) => {
    const style = getComputedStyle(node);
    return Object.fromEntries(fields.map(field => [field, style.getPropertyValue(field).trim()]));
  }, fields);
}
async function same(referencePage: Page, actual: Page, referenceSelector: string, selector: string, fields: string[]) {
  expect(await css(actual, selector, fields), `${selector} follows ${referenceSelector}`).toEqual(await css(referencePage, referenceSelector, fields));
}

for (const theme of ['light', 'dark']) {
  test(`V11/V36/V37: ${theme} product shell matches the original reference runtime`, async ({ page, context }) => {
    const original = await context.newPage();
    await page.emulateMedia({ reducedMotion: 'reduce' }); await original.emulateMedia({ reducedMotion: 'reduce' });
    await reference(original); await formal(page); await appearance(original, theme); await appearance(page, theme);
    await original.mouse.move(0, 0); await page.mouse.move(0, 0);
    await same(original, page, 'html', 'html', tokens.map(token => `--${token}`));
    await same(original, page, '.sidebar', '.portal-sidebar', ['width', 'padding', 'background-color', 'gap']);
    await same(original, page, '.surface', '.portal-content', ['margin', 'border-radius', 'border-top-width', 'border-top-color', 'background-color', 'box-shadow']);
    await same(original, page, '.topbar', '.portal-topbar', ['height', 'padding', 'gap', 'border-bottom-color']);
    await same(original, page, '.nav-item.is-active', '.portal-nav-item[aria-current=page]', ['height', 'padding', 'gap', 'border-radius', 'font-family', 'font-size', 'font-weight', 'color']);
    await same(original, page, '#view-apps .list-title', '.portal-page-heading h1', ['font-size', 'font-weight', 'line-height', 'color']);
    await same(original, page, '#view-apps .list-meta', '.portal-heading-count', ['font-size', 'font-weight', 'line-height', 'color']);
    await same(original, page, '#view-apps .list-search', '.portal-search.list-search', ['height', 'padding', 'border-radius', 'background-color', 'box-shadow']);
    await same(original, page, '.workspace-mark', '.portal-brand img', ['width', 'height', 'padding', 'border-radius', 'background-color', 'filter']);
    await same(original, page, '#view-apps .pill-btn', '.portal-page-heading > .pill-btn', ['height', 'padding', 'border-radius', 'font-size', 'font-weight', 'background-color', 'box-shadow', 'color']);
    await same(original, page, '#view-apps .app-row', '.portal-app-row', ['height', 'padding', 'border-radius', 'gap']);
    await same(original, page, '#view-apps .app-icon', '.portal-app-icon', ['width', 'height', 'border-radius', 'font-size', 'font-weight']);
    await same(original, page, '.nav-item.is-active', '.portal-nav-item[aria-current=page]', ['background-color']);
    await original.locator('.nav-item.is-active').first().hover(); await page.locator('.portal-nav-item[aria-current=page]').hover();
    await same(original, page, '.nav-item.is-active', '.portal-nav-item[aria-current=page]', ['background-color', 'color']);
    await page.screenshot({ path: `.artifacts/reference-design/formal-${theme}.png`, fullPage: true });
    await original.screenshot({ path: `.artifacts/reference-design/reference-${theme}.png`, fullPage: true });
    expect(await page.evaluate(() => document.fonts.check('13px "Inter Variable"'))).toBe(true);
    await page.getByRole('link', { name: /查看详情/ }).click();
    await expect(page.getByRole('button', { name: '打开应用', exact: true })).toBeVisible();
    await same(original, page, '.props', '.portal-inspector', ['width', 'padding', 'background-color', 'border-left-width', 'border-left-color', 'opacity']);
    await original.close();
  });

  test(`V11/V36/V37: ${theme} management table and inputs reuse the original component geometry`, async ({ page, context }) => {
    const original = await context.newPage(); await reference(original);
    await original.getByRole('navigation', { name: '主导航', exact: true }).getByRole('button', { name: '管理中心', exact: true }).click();
    await formal(page, '/manage/data-sources/factory'); await appearance(original, theme); await appearance(page, theme);
    await original.mouse.move(0, 0); await page.mouse.move(0, 0);
    await same(original, page, '.data-grid', '.resource-table-wrap', ['border-radius', 'border-top-width', 'border-top-color', 'background-color']);
    // Compare ordinary data cells: the reference's selection column deliberately has 8px padding.
    await same(original, page, '.data-grid__table th:not(.data-grid__selection):not(.data-grid__actions)', '.resource-table-wrap th', ['height', 'padding', 'font-size', 'font-weight', 'background-color', 'color']);
    await same(original, page, '.data-grid__table td:not(.data-grid__selection):not(.data-grid__actions)', '.resource-table-wrap td', ['height', 'padding', 'font-size', 'font-weight', 'background-color', 'color']);
    await original.locator('[data-member-edit]').first().click();
    await page.getByRole('button', { name: '编辑配置', exact: true }).click();
    await original.locator('#memberFieldName').focus();
    await page.getByLabel('最大返回行数', { exact: true }).focus();
    await same(original, page, '.subwindow-field input', '.resource-form input[type=number]', ['height', 'padding', 'border-radius', 'font-size', 'font-weight', 'background-color', 'color']);
    await page.getByLabel('最大返回行数', { exact: true }).focus();
    await same(original, page, '#memberFieldName', '.resource-form input[type=number]', ['border-color', 'box-shadow', 'outline-style']);
    expect(await page.getByLabel('最大返回行数', { exact: true }).evaluate(node => getComputedStyle(node).boxShadow)).not.toBe('none');
    await page.screenshot({ path: `.artifacts/reference-components/actual-drawer-${theme}.png`, fullPage: true });
    await original.screenshot({ path: `.artifacts/reference-components/original-drawer-${theme}.png`, fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `.artifacts/reference-design/formal-management-${theme}-mobile.png`, fullPage: true });
    await page.setViewportSize({ width: 1366, height: 900 });
    await page.getByLabel('启用当前租户', { exact: true }).uncheck();
    await page.getByRole('button', { name: '保存配置', exact: true }).click();
    await expect(page.getByRole('dialog', { name: '确认停用？', exact: true })).toBeVisible();
    await original.locator('#drawer-member button[data-close]').first().click();
    await original.locator('[data-open=drawer-issue]').click();
    await original.locator('[data-open=dialog-archive]').click();
    await expect(original.locator('#dialog-archive')).toBeVisible();
    await same(original, page, '#dialog-archive .dialog', '.reference-confirm-overlay .dialog', ['width', 'padding', 'border-radius', 'background-color', 'box-shadow']);
    for (const selector of ['.dialog-title', '.dialog-desc', '.dialog-actions']) await same(original, page, '#dialog-archive ' + selector, '.reference-confirm-overlay ' + selector, ['margin', 'padding', 'font-size', 'font-weight', 'line-height', 'color', 'gap']);
    await page.screenshot({ path: `.artifacts/reference-components/actual-confirm-${theme}.png`, fullPage: true });
    await original.screenshot({ path: `.artifacts/reference-components/original-confirm-${theme}.png`, fullPage: true });
    await original.close();
  });
}
