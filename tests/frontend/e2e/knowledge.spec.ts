import { test, expect, type Page } from '@playwright/test';

const tenant = '11111111-1111-4111-8111-111111111111';
const documentId = '22222222-2222-4222-8222-222222222222';
const organization = '33333333-3333-4333-8333-333333333333';
const profile = 'e5-small-dd476dd0-384-v1';
const chunkId = `${documentId.replaceAll('-', '')}-1-0-0`;
const fixtureDocument = { tenantId: tenant, documentId, fileName: '制造业测试 · 设备点检规程.docx', mediaType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', sizeBytes: 1024,
  sha256: 'd'.repeat(64), organizationId: organization, status: 'ready', revision: 1, createdBy: tenant, createdAt: '2026-11-05T00:00:00Z', failureCode: null, indexProfile: profile };
const answer = { requestId: 'a'.repeat(32), modelRequestId: 'b'.repeat(32), searchRequestId: 'c'.repeat(32), answer: '先停机，再检查夹具。\n确认异常后，通知设备维护人员。', refusal: false,
  profile, authorizationVersion: '9007199254740993', documents: [{ documentId, revision: 1 }],
  citations: [{ documentId, documentRevision: 1, chunkId, fileName: fixtureDocument.fileName, locator: 'paragraph:2', quote: '测试条目：停机后检查夹具。<img src=x onerror=alert(1)>' }] };
type FixtureWindow = Window & { AssembleAI_knowledge: { mount(value: unknown): Promise<void>; update(value: unknown): Promise<void>; unmount(): Promise<void> };
  knowledgeFixture: { release?: () => void; mode: string; asks: number; checks: number; failCheck: boolean; replace(): Promise<void> } };

async function routes(page: Page) {
  await page.route('**/bff/session', route => route.fulfill({ json: { iss: 'https://fixture.test', sub: 'alice', name: 'Fixture Alice' } }));
  await page.route('**/bff/tenant', route => route.fulfill({ json: { tenantId: tenant } }));
  await page.route('**/bff/csrf', route => route.fulfill({ json: { headerName: 'X-CSRF-TOKEN', requestToken: 'fixture-csrf' } }));
  await page.route('**/api/platform/v1/tenants', route => route.fulfill({ json: { items: [{ tenantId: tenant, name: '制造业测试租户' }] } }));
  await page.route(`**/api/platform/v1/tenants/${tenant}/access`, route => route.fulfill({ json: { userId: tenant, tenantId: tenant, authorizationVersion: 1,
    grants: [{ permission: 'knowledge:manage', resourceType: 'knowledge', resourceId: 'documents', tenantWide: false, organizationIds: [organization] }] } }));
  await page.route(`**/api/platform/v1/tenants/${tenant}/knowledge/documents`, route => route.fulfill({ json: { items: [fixtureDocument], limit: 100 } }));
}
async function embedded(page: Page) {
  await routes(page); await page.addInitScript(() => { (window as Window & { __POWERED_BY_QIANKUN__: boolean }).__POWERED_BY_QIANKUN__ = true; });
  await page.goto('/apps/knowledge/0.1.0/');
  await page.evaluate(async ({ answer, tenant }) => {
    const target = window as unknown as FixtureWindow;
    const state = { mode: 'complete', asks: 0, checks: 0, failCheck: false, release: undefined as (() => void) | undefined, replace: async () => {} };
    const makeApi = () => ({
      async json(path: string) { if (path !== 'references') throw new Error('Unexpected operation'); state.checks++; if (state.failCheck) throw new Error('Revoked'); return { valid: true }; },
      async *stream() {
        state.asks++; for (const stage of ['retrieving', 'answering', 'verifying']) yield { event: 'delta', data: { kind: 'stage', stage } };
        // Ignores AbortSignal deliberately: the component must reject late completion itself.
        await new Promise<void>(resolve => { state.release = resolve; });
        if (state.mode !== 'partial') yield { event: 'complete', data: state.mode === 'invalid' ? { ...answer, documents: [] } : answer };
      },
    });
    state.replace = () => target.AssembleAI_knowledge.update({ api: makeApi() }); target.knowledgeFixture = state;
    await target.AssembleAI_knowledge.mount({ context: { appId: 'knowledge', releaseVersion: '0.1.0', theme: 'light', mode: 'embedded', tenantId: tenant, identity: { iss: 'fixture', sub: 'alice' } }, api: makeApi() });
  }, { answer, tenant });
  await expect(page.getByTestId('knowledge-documents')).toContainText(fixtureDocument.fileName);
}
async function start(page: Page) {
  await page.getByLabel('你想从文档中了解什么？').fill('点检发现夹具异常，先执行哪些步骤？');
  await page.getByRole('button', { name: '查找答案', exact: true }).click();
  await expect(page.getByTestId('knowledge-status')).toContainText('核对引用');
  await expect.poll(() => page.evaluate(() => !!(window as unknown as FixtureWindow).knowledgeFixture.release)).toBe(true);
}
async function release(page: Page, mode = 'complete') {
  await page.evaluate(value => { const state = (window as unknown as FixtureWindow).knowledgeFixture; state.mode = value; state.release?.(); state.release = undefined; }, mode);
}

test('T12 V31: question input preserves supplementary Unicode and refuses more than 1000 scalars', async ({ page }) => {
  await embedded(page);
  const input = page.getByLabel('你想从文档中了解什么？');
  const legal = '😀'.repeat(1000);
  await input.focus(); await page.keyboard.insertText(legal);
  await expect(input).toHaveValue(legal);
  await expect(page.getByRole('button', { name: '查找答案', exact: true })).toBeEnabled();
  await input.fill('a'.repeat(1001));
  await expect(page.getByRole('button', { name: '查找答案', exact: true })).toBeDisabled();
  await expect(page.locator('#knowledge-question-help')).toContainText('已超出 1 字');
  expect(await page.evaluate(() => (window as unknown as FixtureWindow).knowledgeFixture.asks)).toBe(0);
});
test('T12 V22: full answer and original quotations render only after completion; themes preserve result', async ({ page }) => {
  await embedded(page); await start(page); await expect(page.getByTestId('knowledge-answer')).toHaveCount(0); await release(page);
  await expect(page.getByTestId('knowledge-answer')).toContainText(answer.answer);
  await expect(page.getByTestId('knowledge-answer')).toContainText(answer.citations[0].quote);
  await expect(page.getByTestId('knowledge-answer').locator('img')).toHaveCount(0);
  await page.screenshot({ path: '.artifacts/knowledge-light.png', fullPage: true });
  await page.evaluate(() => (window as unknown as FixtureWindow).AssembleAI_knowledge.update({ context: { theme: 'dark' } }));
  await expect(page.getByTestId('knowledge-app')).toHaveAttribute('data-theme', 'dark');
  await expect(page.getByTestId('knowledge-answer')).toBeVisible();
  await page.screenshot({ path: '.artifacts/knowledge-dark.png', fullPage: true });
});
test('T12 V18/V25: cancel and capability replacement discard late completion', async ({ page }) => {
  await embedded(page); await start(page); await page.getByRole('button', { name: '取消提问' }).click(); await release(page);
  await expect(page.getByTestId('knowledge-status')).toContainText('已取消'); await expect(page.getByTestId('knowledge-answer')).toHaveCount(0);
  await start(page); await page.evaluate(() => (window as unknown as FixtureWindow).knowledgeFixture.replace()); await release(page);
  await expect(page.getByTestId('knowledge-answer')).toHaveCount(0);
  await expect(page.getByTestId('knowledge-documents')).toBeVisible();
  await start(page); await page.evaluate(() => (window as unknown as FixtureWindow).AssembleAI_knowledge.update({ context: { tenantId: 'other' } })); await release(page);
  await expect(page.getByTestId('knowledge-answer')).toHaveCount(0); await expect(page.getByTestId('knowledge-documents')).toHaveCount(0);
  await page.evaluate(() => (window as unknown as FixtureWindow).AssembleAI_knowledge.unmount());
  await expect(page.getByTestId('knowledge-app')).toHaveCount(0);
});
test('T12 V22: interrupted or forged reference streams never display answers', async ({ page }) => {
  await embedded(page);
  for (const mode of ['partial', 'invalid']) { await start(page); await release(page, mode); await expect(page.getByTestId('knowledge-status')).toContainText('回答未完成'); await expect(page.getByTestId('knowledge-answer')).toHaveCount(0); }
});
test('T12 V23: focus rechecks every answer reference without another model call', async ({ page }) => {
  await embedded(page); await start(page); await release(page); await expect(page.getByTestId('knowledge-answer')).toBeVisible();
  await page.evaluate(() => { (window as unknown as FixtureWindow).knowledgeFixture.failCheck = true; window.dispatchEvent(new Event('focus')); });
  await expect(page.getByTestId('knowledge-answer')).toHaveCount(0); await expect(page.getByTestId('knowledge-status')).toContainText('旧答案已清除');
  expect(await page.evaluate(() => (window as unknown as FixtureWindow).knowledgeFixture.asks)).toBe(1);
  expect(await page.evaluate(() => (window as unknown as FixtureWindow).knowledgeFixture.checks)).toBe(1);
});
test('T12: file upload is raw and scoped; deletion requires explicit revision-bound confirmation', async ({ page }) => {
  let uploads = 0; let deletes = 0; let current = fixtureDocument;
  await embedded(page);
  await page.route(`**/knowledge/documents?*`, async route => {
    const request = route.request(); expect(request.method()).toBe('POST'); expect(request.headers()['x-csrf-token']).toBe('fixture-csrf');
    expect(request.headers()['content-type']).toBe('application/octet-stream'); expect(request.url()).toContain(`organizationId=${organization}`);
    expect(request.postDataBuffer()?.toString()).toBe('synthetic document'); uploads++;
    current = { ...fixtureDocument, status: 'extracted', indexProfile: '' }; await route.fulfill({ status: 202, json: { ...current, indexProfile: null } });
  });
  await page.route(`**/api/platform/v1/tenants/${tenant}/knowledge/documents`, route => route.fulfill({ json: { items: deletes ? [] : [{ ...current, indexProfile: current.indexProfile || null }], limit: 100 } }));
  await page.route(`**/knowledge/documents/${documentId}`, async route => {
    expect(route.request().method()).toBe('DELETE'); expect(route.request().postDataJSON()).toEqual({ expectedRevision: 1 }); deletes++;
    await route.fulfill({ status: 204 });
  });
  await page.getByLabel('添加文档').setInputFiles({ name: '测试.docx', mimeType: 'application/octet-stream', buffer: Buffer.from('synthetic document') });
  await page.getByRole('button', { name: '上传文档', exact: true }).click(); await expect(page.getByText('文档已上传，处理完成后即可用于问答。')).toBeVisible();
  await expect(page.getByTestId('knowledge-documents')).toContainText('已解析'); expect(uploads).toBe(1);
  await page.getByRole('button', { name: '删除', exact: true }).click(); expect(deletes).toBe(0);
  await page.getByRole('button', { name: '确认删除', exact: true }).click(); await expect(page.getByTestId('knowledge-documents')).toHaveCount(0); expect(deletes).toBe(1);
});
test('T12 V11: mobile workspace, focus and both themes have no horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 }); await page.emulateMedia({ reducedMotion: 'reduce' });
  await embedded(page); await start(page); await release(page); await expect(page.getByTestId('knowledge-answer')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: '.artifacts/knowledge-mobile-light.png', fullPage: true });
  await page.evaluate(() => (window as unknown as FixtureWindow).AssembleAI_knowledge.update({ context: { theme: 'dark' } }));
  await expect(page.getByTestId('knowledge-app')).toHaveAttribute('data-theme', 'dark');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: '.artifacts/knowledge-mobile-dark.png', fullPage: true });
});
test('T12: a committed upload remains acknowledged when the follow-up read fails', async ({ page }) => {
  await embedded(page); let uploaded = false;
  await page.route('**/knowledge/documents?*', async route => { uploaded = true; await route.fulfill({ status: 202, json: fixtureDocument }); });
  await page.route(`**/api/platform/v1/tenants/${tenant}/knowledge/documents`, route => uploaded
    ? route.fulfill({ status: 503, json: { code: 'knowledge_unavailable' } }) : route.fulfill({ json: { items: [fixtureDocument], limit: 100 } }));
  await page.getByLabel('添加文档').setInputFiles({ name: '测试.docx', mimeType: 'application/octet-stream', buffer: Buffer.from('synthetic') });
  await page.getByRole('button', { name: '上传文档', exact: true }).click();
  await expect(page.getByText('操作已提交，但最新文档列表读取失败，请刷新文档确认结果。')).toBeVisible();
  await expect(page.getByTestId('knowledge-documents')).toHaveCount(0);
});
test('T12 V2: standalone requires explicit connection before accessing library or asking', async ({ page }) => {
  let runs = 0; let asks = 0;
  await routes(page);
  await page.route(`**/catalog/knowledge`, route => route.fulfill({ json: { appId: 'knowledge', releaseVersion: '0.1.0' } }));
  await page.route('**/applications/knowledge/releases/0.1.0/runs', route => { runs++; return route.fulfill({ json: {
    tenantId: tenant, appId: 'knowledge', releaseVersion: '0.1.0', runId: documentId, expiresAt: new Date(Date.now() + 60000).toISOString(),
    apiBase: '/api/apps/knowledge/releases/0.1.0/', manifest: { appId: 'knowledge', version: '0.1.0', frontendEntry: '/apps/knowledge/0.1.0/', frontendArtifact: { version: '0.1.0' } },
  } }); });
  await page.route('**/api/apps/knowledge/releases/0.1.0/ask', route => { asks++; return route.fulfill({ status: 503 }); });
  await page.goto('/apps/knowledge/0.1.0/'); await expect(page.getByRole('button', { name: '连接平台', exact: true })).toBeEnabled();
  expect(runs).toBe(0); await expect(page.getByTestId('knowledge-documents')).toHaveCount(0);
  await page.getByRole('button', { name: '连接平台', exact: true }).click(); await expect(page.getByTestId('knowledge-documents')).toContainText(fixtureDocument.fileName);
  expect(runs).toBe(1); expect(asks).toBe(0);
});
