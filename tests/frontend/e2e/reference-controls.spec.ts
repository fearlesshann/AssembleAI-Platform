import { test, expect, type Page, type Locator } from '@playwright/test';
import { resolve } from 'node:path';
import { readFile } from 'node:fs/promises';
import { build } from 'esbuild';
import { compileScript, parse } from '@vue/compiler-sfc';
import { chooseReferenceOption, expectReferenceValue } from '../reference-controls-helpers.mjs';

const tenant = '11111111-1111-4111-8111-111111111111';
const actor = '22222222-2222-4222-8222-222222222222';
const row = { id: 'a'.repeat(32), source: 'model', tenantId: tenant, actorUserId: actor, resourceType: 'model', resourceId: 'assistant', action: 'chat', outcome: 'completed', status: 'success', requestId: 'request:example', occurredAt: '2026-11-02T03:00:00Z', appId: null, releaseVersion: null, runId: null, gatewayRequestId: null, details: {} };

test('V37: controlled selection reverts after cancellation and independently bundled apps share one listener runtime', async ({ page }) => {
  // Compile the real components into a test-only document. Two independent
  // script evaluations reproduce separate qiankun bundles sharing one DOM.
  const compiled = await build({ stdin: { resolveDir: process.cwd(), loader: 'ts', contents: `
    import { createApp, h, ref } from 'vue';
    import Select from './apps/portal/src/components/reference/ReferenceSelect.vue';
    import Confirm from './apps/portal/src/components/reference/ReferenceConfirm.vue';
    import { useReferenceConfirm } from './apps/portal/src/components/reference/useReferenceConfirm.ts';
    const target = window.harnessMount;
    const app = createApp({ setup() {
      const value = ref('alpha');
      const { confirmation, requestConfirmation, settleConfirmation } = useReferenceConfirm();
      return () => h('section', [h('button', {onClick: () => app.unmount()}, '卸载' + target), h(Select, { label: target, modelValue: value.value,
        options: [{value:'alpha',label:'华东制造'},{value:'beta',label:'华南制造'}],
        onChange: async proposed => { if (await requestConfirmation({title:'切换工作空间？',description:'放弃未保存的修改并切换。',confirmLabel:'确认切换'})) value.value = proposed; }
      }), h(Confirm, { request: confirmation.value, onResolve: settleConfirmation })]);
    }}); app.mount('#' + target);
  ` }, bundle: true, write: false, loader: { '.woff2': 'dataurl' }, outdir: 'controls-harness', format: 'iife', define: { 'process.env.NODE_ENV': '"production"' },
    plugins: [{ name: 'actual-vue-components', setup(builder) { builder.onLoad({ filter: /\.vue$/ }, async ({ path }) => {
      const { descriptor } = parse(await readFile(path, 'utf8'), { filename: path });
      return { contents: compileScript(descriptor, { id: path, inlineTemplate: true }).content, loader: 'ts', resolveDir: resolve(path, '..') };
    }); } }] });
  const js = compiled.outputFiles.find(file => file.path.endsWith('.js'))!.text;
  const css = compiled.outputFiles.find(file => file.path.endsWith('.css'))!.text;
  await page.route('**/controls-harness.js', route => route.fulfill({ contentType: 'text/javascript', body: js }));
  await page.route('**/controls-harness', route => route.fulfill({ contentType: 'text/html', body: `<style>${css}</style><div id="first"></div><div id="second"></div><script>window.harnessMount='first'</script><script src="/controls-harness.js"></script><script>window.harnessMount='second'</script><script src="/controls-harness.js"></script>` }));
  await page.goto('/controls-harness');
  await chooseReferenceOption(page, 'first', 'beta');
  const dialog = page.getByRole('dialog', { name: '切换工作空间？', exact: true });
  await dialog.getByRole('button', { name: '取消', exact: true }).click();
  await expectReferenceValue(page, 'first', 'alpha');
  await expect(page.getByRole('combobox', { name: 'first', exact: true })).toHaveText('华东制造');
  await page.getByRole('button', { name: '卸载first', exact: true }).click();
  await expect(page.getByRole('combobox', { name: 'first', exact: true })).toHaveCount(0);
  await chooseReferenceOption(page, 'second', 'beta');
  await dialog.getByRole('button', { name: '确认切换', exact: true }).click();
  await expectReferenceValue(page, 'second', 'beta');
  await page.getByRole('combobox', { name: 'second', exact: true }).click();
  await page.setViewportSize({ width: 390, height: 844 });
  const menu = page.getByRole('listbox', { name: 'second', exact: true });
  const box = await menu.boundingBox(); expect(box!.x).toBeGreaterThanOrEqual(0); expect(box!.x + box!.width).toBeLessThanOrEqual(390);
  await menu.getByRole('option', { name: '华东制造', exact: true }).click();
  await dialog.getByRole('button', { name: '确认切换', exact: true }).click();
  await expectReferenceValue(page, 'second', 'alpha');
});

async function audit(page: Page) {
  await page.route('**/bff/**', route => {
    const path = new URL(route.request().url()).pathname;
    return route.fulfill({ json: path === '/bff/session' ? { iss: 'https://controls.fixture', sub: 'reader', name: '界面验收' } : { tenantId: tenant } });
  });
  await page.route('**/api/platform/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/audit/workspace')) return route.fulfill({ json: { scope: 'tenant', tenantId: tenant, actorUserId: actor, sources: ['model', 'connector', 'gateway'], canExport: false, globalAvailable: false, policy: { maximumRangeDays: 31, defaultPageSize: 50, maximumPageSize: 100, maximumExportRecords: 1000, automaticRetention: false } } });
    if (path.endsWith('/audit/events')) return route.fulfill({ json: { items: [row], nextCursor: null } });
    if (path === '/api/platform/v1/tenants') return route.fulfill({ json: { items: [{ tenantId: tenant, name: '制造工作区' }] } });
    if (path.endsWith('/models') || path.endsWith('/connectors')) return route.fulfill({ json: { items: [] } });
    return route.fulfill({ status: 404, json: {} });
  });
  await page.goto('/manage/audit?from=2026-11-02T03%3A03%3A00Z&to=2026-11-03T03%3A03%3A00Z');
  await expect(page.getByRole('region', { name: '调用与审计工作区', exact: true })).toHaveAttribute('aria-busy', 'false');
  await expect(page.getByRole('table', { name: '审计记录', exact: true })).toBeVisible();
}
async function reference(page: Page) {
  await page.route('https://static.linear.app/fonts/**', route => route.request().url().includes('Italic') ? route.abort() : route.fulfill({ path: resolve('apps/portal/public/fonts/InterVariable.woff2'), contentType: 'font/woff2' }));
  await page.goto('/reference-ui/?motion=reduced');
  await page.getByLabel('账号 / 邮箱').fill('reference@example.test'); await page.getByLabel('密码').fill('only-a-reference-fixture');
  await page.getByRole('button', { name: '进入工作台', exact: true }).click();
  await page.getByRole('navigation', { name: '主导航', exact: true }).getByRole('button', { name: '管理中心', exact: true }).click();
  await expect(page.locator('#memberTable')).toBeVisible();
}
const surface = ['height', 'padding', 'border-radius', 'background-color', 'border-top-width', 'border-top-color', 'font-size', 'font-weight', 'font-family', 'font-feature-settings', 'line-height', 'letter-spacing', 'opacity', 'color'];
async function styles(locator: Locator, fields: string[]) { return locator.evaluate((node, fields) => { const css = getComputedStyle(node); return Object.fromEntries(fields.map(field => [field, css.getPropertyValue(field)])); }, fields); }
async function equalStyles(a: Locator, b: Locator, fields = surface) { expect(await styles(a, fields)).toEqual(await styles(b, fields)); }

for (const theme of ['light', 'dark']) test(`V36/V37: ${theme} select, datetime, input, button and pagination reuse the supplied components`, async ({ page, context }) => {
  const original = await context.newPage(); await reference(original); await audit(page);
  for (const target of [page, original]) await target.evaluate(async theme => { document.documentElement.dataset.theme = theme; document.documentElement.dataset.motion = 'reduced'; await document.fonts.ready; }, theme);
  await page.mouse.move(0, 0); await original.mouse.move(0, 0);
  await equalStyles(page.locator('.data-grid__pager .icon-btn').first(), original.locator('#memberPrev'));
  await equalStyles(page.locator('.data-grid__page-size .ui-select__trigger'), original.locator('#memberPageSize .ui-select__trigger'));
  await equalStyles(page.locator('.data-grid__page'), original.locator('#memberPageLabel'), ['font-size', 'font-weight', 'color', 'min-width', 'text-align']);
  await page.screenshot({ path: `.artifacts/reference-components/actual-table-${theme}.png`, fullPage: true });
  await original.screenshot({ path: `.artifacts/reference-components/original-table-${theme}.png`, fullPage: true });
  await original.locator('[data-member-edit]').first().click(); await expect(original.locator('#drawer-member')).toBeVisible();
  const select = page.getByRole('combobox', { name: '记录类型', exact: true });
  await equalStyles(select, original.locator('#memberFieldRole .ui-select__trigger'));
  await page.getByLabel('应用标识', { exact: true }).focus(); await original.locator('#memberFieldName').focus();
  await equalStyles(page.getByLabel('应用标识', { exact: true }), original.locator('#memberFieldName'));
  await select.click(); await original.locator('#memberFieldRole .ui-select__trigger').click();
  await equalStyles(page.locator('.ui-select.is-open .ui-select__menu'), original.locator('#memberFieldRole .ui-select__menu'), ['padding', 'border-radius', 'background-color', 'border-top-color', 'box-shadow']);
  await equalStyles(page.locator('.ui-select.is-open .ui-select__option').first(), original.locator('#memberFieldRole .ui-select__option').first(), ['min-height', 'padding', 'border-radius', 'font-size', 'font-weight']);
  await page.screenshot({ path: `.artifacts/reference-components/actual-select-${theme}.png`, fullPage: true });
  await original.screenshot({ path: `.artifacts/reference-components/original-select-${theme}.png`, fullPage: true });
  await page.keyboard.press('Escape'); await original.locator('#memberFieldRole .ui-select__trigger').click();
  const date = page.getByRole('button', { name: '开始时间', exact: true });
  await equalStyles(date, original.locator('#memberFieldActiveAt .ui-datetime__trigger'));
  const value = await date.locator('..').getAttribute('data-value');
  await original.evaluate(value => (window as any).__uiDateTime.set(document.getElementById('memberFieldActiveAt'), value, true), value);
  await date.click(); await original.locator('#memberFieldActiveAt .ui-datetime__trigger').click();
  const panel = page.locator('.ui-datetime.is-open .ui-datetime__panel'); const sourcePanel = original.locator('#memberFieldActiveAt .ui-datetime__panel');
  await equalStyles(panel, sourcePanel, ['width', 'padding', 'border-radius', 'background-color', 'box-shadow']);
  for (const selector of ['.ui-cal__head', '.ui-cal__title', '.ui-cal__day', '.ui-datetime__time', '.ui-wheel', '.ui-wheel__viewport', '.ui-wheel__item']) await equalStyles(panel.locator(selector).first(), sourcePanel.locator(selector).first(), ['height', 'padding', 'border-radius', 'background-color', 'font-size', 'font-weight', 'color']);
  await expect(panel.locator('.ui-cal__day')).toHaveCount(42); await expect(panel.locator('[data-wheel="hour"] .ui-wheel__item')).toHaveCount(24); await expect(panel.locator('[data-wheel="minute"] .ui-wheel__item')).toHaveCount(60);
  await page.screenshot({ path: `.artifacts/reference-components/actual-datetime-${theme}.png`, fullPage: true });
  await original.screenshot({ path: `.artifacts/reference-components/original-datetime-${theme}.png`, fullPage: true });
  await panel.getByRole('button', { name: '选择月份', exact: true }).click(); await expect(panel.locator('[data-pick-month]')).toHaveCount(12);
  await panel.getByRole('button', { name: '选择年份', exact: true }).click(); await expect(panel.locator('[data-pick-year]')).toHaveCount(12);
  await page.keyboard.press('Escape'); await expect(panel.locator('[data-pick-month]')).toHaveCount(12);
  await page.keyboard.press('Escape'); await expect(panel.locator('.ui-cal__day')).toHaveCount(42);
  const hour = panel.getByRole('listbox', { name: '小时', exact: true }); await hour.focus(); await hour.press('ArrowDown');
  await expect(panel.locator('[data-wheel="hour"] [aria-selected="true"]')).toHaveCount(1);
  await page.keyboard.press('Escape'); await expect(panel).toBeHidden(); await expect(date).toBeFocused();
  await expect(page.locator('.audit-workspace select, .audit-workspace input[type=datetime-local]')).toHaveCount(0);
  await page.screenshot({ path: `.artifacts/reference-components/controls-${theme}.png`, fullPage: true });
  await original.close();
});
