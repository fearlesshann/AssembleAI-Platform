import { expect, test, type Page } from '@playwright/test';
import type { AuditEvent, AuditSource } from '../../../apps/portal/src/audit-api';

const tenant = '11111111-1111-4111-8111-111111111111';
const other = '22222222-2222-4222-8222-222222222222';
const actor = '33333333-3333-4333-8333-333333333333';
const firstId = 'a'.repeat(32); const secondId = 'b'.repeat(32);
const related = 'flow:shared-1';
const fixed = 'from=2026-11-01T00%3A00%3A00.000Z&to=2026-11-02T00%3A00%3A00.000Z';

function row(id: string, source: AuditSource, tenantId: string | null, requestId: string): AuditEvent {
  return { id, source, tenantId, actorUserId: actor, resourceType: source === 'model' ? 'model' : 'application',
    resourceId: source === 'model' ? 'assistant.v1' : 'chatbi', action: 'invoke', outcome: 'completed', status: 'success',
    requestId, occurredAt: '2026-11-01T08:00:00.000Z', appId: 'chatbi', releaseVersion: '1.0.0', runId: '44444444-4444-4444-8444-444444444444',
    gatewayRequestId: null, details: { elapsedMilliseconds: '25' } };
}

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>(done => { resolve = done; });
  return { promise, resolve };
}
function hold() { return { entered: deferred(), release: deferred(), finished: deferred() }; }

async function fixture(page: Page) {
  const state = {
    selected: tenant as string | null, globalAvailable: false, allowed: true, canExport: true,
    readStatus: 200, exportStatus: 200, csrf: 0, requests: [] as { path: string; query: URLSearchParams; selected: string | null }[],
    exports: [] as { query: URLSearchParams; csrf: string; body: unknown }[],
    eventHold: null as ReturnType<typeof hold> | null, exportHold: null as ReturnType<typeof hold> | null,
  };
  const records = (scope: string | null, selected: string | null) => scope === 'global'
    ? [row('c'.repeat(32), 'identity', null, 'global-identity')]
    : selected === other ? [row('d'.repeat(32), 'connector', other, 'other-tenant-request')]
      : [row(firstId, 'gateway', tenant, related),
        { ...row(secondId, 'model', tenant, 'model-request'), gatewayRequestId: related, status: 'failure' as const,
          outcome: 'upstream_error', details: { elapsedMilliseconds: '41', httpStatus: '503' } }];
  await page.route('**/bff/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/bff/session') return route.fulfill({ json: { iss: 'https://audit.fixture', sub: 'auditor', name: '审计用户' } });
    if (path === '/bff/csrf') return route.fulfill({ json: { headerName: 'X-CSRF-TOKEN', requestToken: `fresh-${++state.csrf}` } });
    if (path === '/bff/tenant') {
      if (route.request().method() === 'POST') state.selected = route.request().postDataJSON().tenantId;
      return route.fulfill({ json: { tenantId: state.selected } });
    }
    return route.fulfill({ status: 404, json: {} });
  });
  await page.route('**/api/platform/v1/**', async route => {
    const url = new URL(route.request().url()); const path = url.pathname; const query = url.searchParams;
    if (path === '/api/platform/v1/tenants') return route.fulfill({ json: { items: state.selected === null ? [] : [{ tenantId: tenant, name: '华东制造' }, { tenantId: other, name: '华南制造' }] } });
    if (path.endsWith('/identity-management/workspace')) return route.fulfill({ status: 403, json: {} });
    if (path.endsWith('/models') || path.endsWith('/connectors') || path.endsWith('/catalog')) return route.fulfill({ json: { items: [], categories: [] } });
    if (!path.startsWith('/api/platform/v1/audit/')) return route.fulfill({ status: 404, json: {} });
    const selected = state.selected; const global = query.get('scope') === 'global';
    state.requests.push({ path, query, selected });
    if (global && !state.globalAvailable) return route.fulfill({ status: 403, json: { detail: 'private authorization internals' } });
    const sources: AuditSource[] = !state.allowed ? [] : global ? ['identity', 'registry'] : ['gateway', 'model', 'connector'];
    if (path.endsWith('/workspace')) return route.fulfill({ json: { scope: global ? 'global' : 'tenant', tenantId: global ? null : selected,
      actorUserId: actor, sources, canExport: state.canExport && state.allowed, globalAvailable: state.globalAvailable,
      policy: { maximumRangeDays: 31, defaultPageSize: 50, maximumPageSize: 100, maximumExportRecords: 1000, automaticRetention: false } } });
    const all = state.allowed ? records(query.get('scope'), selected) : [];
    const filtered = all.filter(item => (!query.get('source') || item.source === query.get('source'))
      && (!query.get('status') || item.status === query.get('status')) && (!query.get('appId') || item.appId === query.get('appId'))
      && (!query.get('requestId') || [item.requestId, item.gatewayRequestId].includes(query.get('requestId'))));
    if (path.endsWith('/export')) {
      state.exports.push({ query, csrf: route.request().headers()['x-csrf-token'], body: route.request().postDataJSON() });
      const gate = state.exportHold; state.exportHold = null;
      if (gate) { gate.entered.resolve(); await gate.release.promise; }
      try { return await route.fulfill(state.exportStatus !== 200
        ? { status: state.exportStatus, json: { detail: 'private export failure' } }
        : { json: { items: filtered, truncated: true, exportedAt: '2026-11-02T09:00:00.000Z' } }); }
      finally { gate?.finished.resolve(); }
    }
    if (path.endsWith('/events')) {
      const gate = state.eventHold; state.eventHold = null;
      const status = state.readStatus;
      const paginated = !query.get('requestId') && !query.get('source') && filtered.length > 1;
      const response = { items: paginated ? [filtered[query.has('cursor') ? 1 : 0]] : filtered,
        nextCursor: paginated && !query.has('cursor') ? 'protected+/=page2' : null };
      if (gate) { gate.entered.resolve(); await gate.release.promise; }
      try { return await route.fulfill(status !== 200 ? { status, json: { detail: 'private query failure' } } : { json: response }); }
      finally { gate?.finished.resolve(); }
    }
    const item = filtered.find(item => path.endsWith(`/events/${item.source}/${item.id}`));
    return route.fulfill(item ? { json: item } : { status: 404, json: {} });
  });
  return state;
}
const queries = (state: Awaited<ReturnType<typeof fixture>>) => state.requests.filter(request => request.path.endsWith('/events'));
const auditTable = (page: Page) => page.getByRole('table', { name: '审计记录', exact: true });
async function ready(page: Page) { await expect(auditTable(page)).toBeVisible(); await expect(page.getByRole('button', { name: '查询记录', exact: true })).toBeEnabled(); }

test('V25/V33: audit filters and cursor pages persist; details can correlate across sources', async ({ page }) => {
  const state = await fixture(page); await page.goto(`/manage/audit?${fixed}`); await ready(page);
  await expect(auditTable(page)).toContainText(related);
  await page.getByRole('button', { name: '下一页', exact: true }).click(); await expect(auditTable(page)).toContainText('model-request');
  expect(queries(state).at(-1)!.query.get('cursor')).toBe('protected+/=page2');
  expect(queries(state).at(-1)!.query.get('from')).toBe('2026-11-01T00:00:00.000Z');
  await page.getByRole('button', { name: '上一页', exact: true }).click(); await expect(auditTable(page)).toContainText(related);
  await page.getByRole('combobox', { name: '记录类型', exact: true }).selectOption('model');
  await page.getByRole('combobox', { name: '结果', exact: true }).selectOption('failure');
  await page.getByLabel('应用标识', { exact: true }).fill('chatbi');
  await page.getByLabel('请求标识', { exact: true }).fill('model-request');
  await page.getByRole('button', { name: '查询记录', exact: true }).click(); await expect(auditTable(page)).toContainText('model-request');
  expect(Object.fromEntries(queries(state).at(-1)!.query)).toMatchObject({ source: 'model', status: 'failure', appId: 'chatbi', requestId: 'model-request' });
  expect(queries(state).at(-1)!.query.has('cursor')).toBe(false);
  const count = queries(state).length;
  await page.getByRole('button', { name: '查询记录', exact: true }).click();
  await expect.poll(() => queries(state).length).toBeGreaterThan(count); await ready(page);
  await page.getByRole('link', { name: '查看详情 model-request', exact: true }).click();
  const detail = page.getByRole('complementary', { name: '审计记录详情', exact: true });
  await expect(detail).toContainText('上游异常'); await expect(detail).toContainText(actor); await expect(detail).toContainText('503');
  await detail.getByRole('button', { name: '查看网关关联', exact: true }).click();
  await expect(auditTable(page)).toContainText(related); await expect(auditTable(page)).toContainText('model-request');
  const query = queries(state).at(-1)!.query;
  expect(query.get('requestId')).toBe(related); expect(query.has('source')).toBe(false); expect(query.has('status')).toBe(false); expect(query.has('appId')).toBe(false);
  await expect(detail).toHaveCount(0); await expect(page.getByRole('combobox', { name: '记录类型', exact: true })).toHaveValue('');
  await page.getByRole('button', { name: '切换暗色', exact: true }).click();
  await page.setViewportSize({ width: 390, height: 844 });
  const layout = await page.evaluate(() => ({ width: document.documentElement.scrollWidth, viewport: innerWidth,
    outside: [...document.querySelectorAll('body *')].map(node => ({ tag: node.tagName, className: node.className,
      width: node.getBoundingClientRect().width, right: node.getBoundingClientRect().right, position: getComputedStyle(node).position,
      overflow: getComputedStyle(node).overflow, display: getComputedStyle(node).display })).filter(node => node.right > innerWidth + 1) }));
  expect(layout.width, JSON.stringify(layout.outside)).toBeLessThanOrEqual(layout.viewport);
  const scroll = page.getByLabel('审计记录可横向滚动', { exact: true });
  expect(await scroll.evaluate(node => node.scrollWidth > node.clientWidth)).toBe(true);
  // Capture from the top before focus scrolls the viewport down to the table.
  await page.screenshot({ path: '.artifacts/t13-audit-ui/audit-mobile-dark.png', fullPage: true });
  await scroll.focus(); await expect(scroll).toBeFocused();
});

test('V12/V25: export downloads explicitly truncated JSON with fresh CSRF and blocks repeated writes', async ({ page }) => {
  const state = await fixture(page); await page.goto(`/manage/audit?${fixed}&requestId=${encodeURIComponent(related)}`); await ready(page);
  const gate = hold(); state.exportHold = gate;
  const downloaded = page.waitForEvent('download');
  await page.getByRole('button', { name: '导出记录', exact: true }).click(); await gate.entered.promise;
  await expect(page.getByRole('button', { name: '正在导出…', exact: true })).toBeDisabled();
  await expect(page.getByLabel('当前租户', { exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: '查询记录', exact: true })).toBeDisabled();
  gate.release.resolve(); const download = await downloaded;
  expect(download.suggestedFilename()).toBe('assembleai-audit-2026-11-02.json');
  const stream = await download.createReadStream(); expect(stream).not.toBeNull();
  const chunks: Buffer[] = []; for await (const chunk of stream!) chunks.push(Buffer.from(chunk));
  const exported = JSON.parse(Buffer.concat(chunks).toString('utf8'));
  expect(exported.truncated).toBe(true); expect(exported.items).toHaveLength(2);
  expect(exported.items.every((item: AuditEvent) => item.tenantId === tenant)).toBe(true);
  await expect(page.getByRole('status').filter({ hasText: '结果已截断' })).toBeVisible();
  expect(state.exports).toHaveLength(1); expect(state.exports[0].csrf).toBe('fresh-1'); expect(state.exports[0].body).toEqual({});
  expect(state.exports[0].query.has('cursor')).toBe(false); expect(state.exports[0].query.has('pageSize')).toBe(false);
  await expect(page.getByLabel('当前租户', { exact: true })).toBeEnabled();
});

test('V25/V33: a revoked query clears previously visible rows and details until an explicit retry', async ({ page }) => {
  const state = await fixture(page); await page.goto(`/manage/audit/gateway/${firstId}?${fixed}`); await ready(page);
  const detail = page.getByRole('complementary', { name: '审计记录详情', exact: true }); await expect(detail).toContainText(actor);
  await page.clock.install(); state.readStatus = 403;
  await page.getByRole('button', { name: '刷新管理数据', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('当前范围没有查询或导出权限');
  await expect(auditTable(page)).toHaveCount(0); await expect(detail).not.toContainText(actor);
  await expect(page.getByRole('heading', { name: '没有查询权限', exact: true })).toBeVisible();
  const count = queries(state).length; await page.clock.runFor(30_000); expect(queries(state)).toHaveLength(count);
  await expect(page.locator('body')).not.toContainText('private query failure');
  state.readStatus = 200; await page.getByRole('button', { name: '重新查询第一页', exact: true }).click(); await ready(page);
});

for (const status of [403, 503]) test(`V12/V25: export ${status} clears data and preserves commit uncertainty without repeating the write`, async ({ page }) => {
  const state = await fixture(page); await page.goto(`/manage/audit/gateway/${firstId}?${fixed}`); await ready(page);
  await expect(page.getByRole('complementary', { name: '审计记录详情', exact: true })).toContainText(actor);
  await page.clock.install(); state.exportStatus = status;
  await page.getByRole('button', { name: '导出记录', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('可能已记录导出审计；没有自动重试');
  await expect(auditTable(page)).toHaveCount(0);
  await expect(page.getByRole('complementary', { name: '审计记录详情', exact: true })).not.toContainText(actor);
  await page.clock.runFor(30_000); expect(state.exports).toHaveLength(1);
  await expect(page.locator('body')).not.toContainText('private export failure');
  await expect(page.getByLabel('当前租户', { exact: true })).toBeEnabled();
});

test('V25: a delayed response from the old tenant cannot repopulate the new tenant audit view', async ({ page }) => {
  const state = await fixture(page); await page.goto(`/manage/audit?${fixed}`); await ready(page);
  const gate = hold(); state.eventHold = gate;
  await page.getByRole('button', { name: '下一页', exact: true }).click(); await gate.entered.promise;
  await page.getByLabel('当前租户', { exact: true }).selectOption(other);
  await expect(auditTable(page)).toContainText('other-tenant-request');
  gate.release.resolve(); await gate.finished.promise;
  await expect(auditTable(page)).toContainText('other-tenant-request'); await expect(auditTable(page)).not.toContainText('model-request');
  await expect(page.getByLabel('当前租户', { exact: true })).toHaveValue(other);
  expect(queries(state).at(-1)!.selected).toBe(other); expect(queries(state).at(-1)!.query.has('cursor')).toBe(false);
});

test('V25/V33: a global administrator can enter platform audit without a tenant membership', async ({ page }) => {
  const state = await fixture(page); state.selected = null; state.globalAvailable = true;
  await page.goto('/'); const navigation = page.getByRole('navigation', { name: '主导航', exact: true });
  await expect(navigation.getByRole('link', { name: '调用与审计', exact: true })).toHaveAttribute('href', '/manage/audit/global');
  await navigation.getByRole('link', { name: '调用与审计', exact: true }).click(); await ready(page);
  await expect(auditTable(page)).toContainText('global-identity');
  await expect(page.getByRole('navigation', { name: '审计范围', exact: true }).getByRole('link', { name: '平台操作', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(page.getByRole('combobox', { name: '记录类型', exact: true }).locator('option')).toHaveText(['全部已授权类型', '身份操作', '应用操作']);
  expect(queries(state).at(-1)!.query.get('scope')).toBe('global'); expect(queries(state).at(-1)!.selected).toBeNull();
});

test('V25/V33: an actor without audit resources sees an honest empty state and no export or global entry', async ({ page }) => {
  const state = await fixture(page); state.allowed = false; await page.goto(`/manage/audit?${fixed}`);
  await expect(page.getByRole('heading', { name: '没有匹配的记录', exact: true })).toBeVisible();
  await expect(page.getByRole('navigation', { name: '主导航', exact: true }).getByRole('link', { name: '调用与审计', exact: true })).toHaveCount(0);
  await expect(page.getByRole('link', { name: '平台操作', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: '导出记录', exact: true })).toHaveCount(0);
  await expect(auditTable(page)).toHaveCount(0);
  await page.goto(`/manage/audit/global?${fixed}`);
  await expect(page.getByRole('heading', { name: '没有查询权限', exact: true })).toBeVisible();
  await expect(page.locator('body')).not.toContainText('global-identity');
});
