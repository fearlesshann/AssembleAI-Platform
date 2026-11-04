import { expect, test, type Page } from '@playwright/test';

// UI protocol fixtures only. Root's marked PostgreSQL harness verifies the real chain.
const catalog = { items: [{ sourceId: 'manufacturing', datasetId: 'production', displayName: '每日生产（测试）', columns: [{ name: 'line', type: 'text' }, { name: 'produced', type: 'integer' }], maxRows: 100 },
  { sourceId: 'manufacturing', datasetId: 'quality', displayName: '质量记录（测试）', columns: [{ name: 'line', type: 'text' }, { name: 'produced', type: 'integer' }], maxRows: 100 }] };
const metadata = { requestId: 'a'.repeat(32), modelRequestId: 'b'.repeat(32), queryRequestId: 'c'.repeat(32), sourceId: 'manufacturing', datasetId: 'production', datasetName: '每日生产（测试）', columns: ['line', 'metric0'], rowCount: 1, queryText: '{"columns":["line"],"metrics":[{"function":"sum","column":"produced"}],"limit":100}', scope: '当前授权工厂', summary: '已完成当前授权工厂的测试查询。' };
const row = ['<img src=x onerror=alert(1)>', '9007199254740993.123456789'];

async function embedded(page: Page) {
  await page.addInitScript(() => { (window as unknown as { __POWERED_BY_QIANKUN__: boolean }).__POWERED_BY_QIANKUN__ = true; });
  await page.goto('/apps/chatbi/0.1.0/');
  await page.evaluate(async ({ catalog, metadata, row }) => {
    const target = window as unknown as {
      AssembleAI_chatbi: { mount(value: unknown): Promise<void>; update(value: unknown): Promise<void>; unmount(): Promise<void> };
      chatbiFixture: { release?: () => void; mode: string; asks: number; catalogs: number; replace: () => Promise<void> };
    };
    const state = { mode: 'complete', asks: 0, catalogs: 0, release: undefined as (() => void) | undefined, replace: async () => {} };
    const makeApi = () => ({
      async json() { state.catalogs++; return catalog; },
      async *stream() {
        state.asks++;
        for (const stage of ['planning', 'querying', 'receiving']) yield { event: 'delta', data: { kind: 'stage', stage } };
        yield { event: 'delta', data: { kind: 'rows', rows: [row] } };
        // Deliberately ignores AbortSignal to exercise the UI's generation guard.
        await new Promise<void>((resolve) => { state.release = resolve; });
        if (state.mode !== 'partial') yield { event: 'complete', data: state.mode === 'mismatch' ? { ...metadata, rowCount: 2 } : metadata };
      },
    });
    state.replace = () => target.AssembleAI_chatbi.update({ api: makeApi() });
    target.chatbiFixture = state;
    await target.AssembleAI_chatbi.mount({ context: { appId: 'chatbi', releaseVersion: '0.1.0', theme: 'light', mode: 'embedded', tenantId: 'tenant-a', identity: { iss: 'fixture', sub: 'alice' } }, api: makeApi() });
  }, { catalog, metadata, row });
  await expect(page.getByTestId('chatbi-dataset')).toHaveValue('production');
}
async function start(page: Page) {
  await page.getByTestId('chatbi-question').fill('按产线汇总产量');
  await page.getByTestId('chatbi-submit').click();
  await expect(page.getByTestId('chatbi-status')).toContainText('核对结果');
  await expect.poll(() => page.evaluate(() => !!(window as unknown as { chatbiFixture: { release?: unknown } }).chatbiFixture.release)).toBe(true);
}
async function release(page: Page, mode = 'complete') {
  await page.evaluate((value) => { const state = (window as unknown as { chatbiFixture: { mode: string; release?: () => void } }).chatbiFixture; state.mode = value; state.release?.(); state.release = undefined; }, mode);
}

test('T11: result commits only at complete, preserves decimal text and escapes query/data', async ({ page }) => {
  await embedded(page); await start(page);
  await expect(page.getByTestId('chatbi-result')).toHaveCount(0);
  await expect(page.getByText(row[1], { exact: true })).toHaveCount(0);
  await release(page);
  await expect(page.getByTestId('chatbi-result')).toContainText(row[1]);
  await expect(page.getByTestId('chatbi-result').locator('img')).toHaveCount(0);
  await expect(page.getByTestId('chatbi-result')).toContainText(row[0]);
  await page.getByText('查看查询依据', { exact: true }).click();
  await expect(page.getByTestId('chatbi-evidence')).toHaveText(metadata.queryText);
  await page.screenshot({ path: '.artifacts/chatbi-result-light.png', fullPage: true });
  await page.evaluate(async () => { await (window as unknown as { AssembleAI_chatbi: { update(value: unknown): Promise<void> } }).AssembleAI_chatbi.update({ context: { theme: 'dark' } }); });
  await expect(page.getByTestId('chatbi-app')).toHaveAttribute('data-theme', 'dark');
  await expect(page.getByTestId('chatbi-result')).toBeVisible();
  await page.screenshot({ path: '.artifacts/chatbi-result-dark.png', fullPage: true });
  await page.getByTestId('chatbi-dataset').selectOption('quality');
  await expect(page.getByTestId('chatbi-result')).toHaveCount(0);
});

test('T11: cancel, identity change, capability replacement and unmount reject late results', async ({ page }) => {
  await embedded(page); await start(page);
  await page.getByTestId('chatbi-cancel').click(); await release(page);
  await expect(page.getByTestId('chatbi-status')).toContainText('已取消');
  await expect(page.getByTestId('chatbi-result')).toHaveCount(0);
  await start(page);
  await page.evaluate(() => (window as unknown as { chatbiFixture: { replace(): Promise<void> } }).chatbiFixture.replace());
  await release(page);
  await expect(page.getByTestId('chatbi-result')).toHaveCount(0);
  expect(await page.evaluate(() => (window as unknown as { chatbiFixture: { catalogs: number; asks: number } }).chatbiFixture.asks)).toBe(2);
  await expect.poll(() => page.evaluate(() => (window as unknown as { chatbiFixture: { catalogs: number } }).chatbiFixture.catalogs)).toBe(2);
  await start(page);
  await page.evaluate(() => (window as unknown as { AssembleAI_chatbi: { update(value: unknown): Promise<void> } }).AssembleAI_chatbi.update({ context: { tenantId: 'tenant-b', identity: { iss: 'fixture', sub: 'bob' } } }));
  await release(page); await expect(page.getByTestId('chatbi-result')).toHaveCount(0);
  await expect(page.getByTestId('chatbi-dataset')).toBeDisabled();
  await page.evaluate(() => (window as unknown as { chatbiFixture: { replace(): Promise<void> } }).chatbiFixture.replace());
  await start(page);
  await page.evaluate(() => (window as unknown as { AssembleAI_chatbi: { unmount(): Promise<void> } }).AssembleAI_chatbi.unmount());
  await release(page); await expect(page.getByTestId('chatbi-app')).toHaveCount(0);
});

test('T11: interrupted and mismatched streams never display provisional rows', async ({ page }) => {
  await embedded(page);
  for (const mode of ['partial', 'mismatch']) {
    await start(page); await release(page, mode);
    await expect(page.getByTestId('chatbi-status')).toContainText('分析未完成');
    await expect(page.getByTestId('chatbi-result')).toHaveCount(0);
    await expect(page.getByText(row[1], { exact: true })).toHaveCount(0);
  }
});

test('T11: compact workspace keeps keyboard focus and both themes without page overflow', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await embedded(page);
  await page.getByTestId('chatbi-dataset').focus(); await page.keyboard.press('Tab');
  await expect(page.getByTestId('chatbi-question')).toBeFocused();
  await start(page); await release(page);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: '.artifacts/chatbi-mobile-light.png', fullPage: true });
  await page.evaluate(() => (window as unknown as { AssembleAI_chatbi: { update(value: unknown): Promise<void> } }).AssembleAI_chatbi.update({ context: { theme: 'dark' } }));
  await expect(page.getByTestId('chatbi-app')).toHaveAttribute('data-theme', 'dark');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: '.artifacts/chatbi-mobile-dark.png', fullPage: true });
});

test('T11: standalone inspects session but requires explicit connection before catalog or ask', async ({ page }) => {
  const tenant = '22222222-2222-4222-8222-222222222222'; let runs = 0; let asks = 0;
  await page.route('**/bff/session', (route) => route.fulfill({ json: { iss: 'https://fixture.test', sub: 'alice', name: 'Fixture Alice' } }));
  await page.route('**/bff/tenant', (route) => route.fulfill({ json: { tenantId: tenant } }));
  await page.route('**/bff/csrf', (route) => route.fulfill({ json: { headerName: 'X-CSRF-TOKEN', requestToken: 'ui-fixture-only' } }));
  await page.route('**/api/platform/v1/tenants', (route) => route.fulfill({ json: { items: [{ tenantId: tenant, name: '制造业测试租户' }] } }));
  await page.route(`**/api/platform/v1/tenants/${tenant}/catalog/chatbi`, (route) => route.fulfill({ json: { appId: 'chatbi', releaseVersion: '0.1.0' } }));
  await page.route('**/applications/chatbi/releases/0.1.0/runs', (route) => { runs++; return route.fulfill({ json: { tenantId: tenant, appId: 'chatbi', releaseVersion: '0.1.0', runId: '11111111-1111-4111-8111-111111111111', expiresAt: new Date(Date.now() + 60_000).toISOString(), apiBase: '/api/apps/chatbi/releases/0.1.0/', manifest: { appId: 'chatbi', version: '0.1.0', frontendEntry: '/apps/chatbi/0.1.0/', frontendArtifact: { version: '0.1.0' } } } }); });
  await page.route('**/api/apps/chatbi/releases/0.1.0/datasets', (route) => route.fulfill({ json: catalog }));
  await page.route('**/api/apps/chatbi/releases/0.1.0/ask', (route) => { asks++; return route.fulfill({ status: 503 }); });
  await page.goto('/apps/chatbi/0.1.0/');
  await expect(page.getByRole('button', { name: '连接平台', exact: true })).toBeEnabled();
  expect(runs).toBe(0); expect(asks).toBe(0);
  await page.getByRole('button', { name: '连接平台', exact: true }).click();
  await expect(page.getByTestId('chatbi-dataset')).toHaveValue('production');
  expect(runs).toBe(1); expect(asks).toBe(0);
  await page.getByRole('button', { name: '切换暗色', exact: true }).click();
  await expect(page.getByTestId('chatbi-app')).toHaveAttribute('data-theme', 'dark');
});
