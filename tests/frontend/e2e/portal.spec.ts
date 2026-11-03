import { expect, test, type Page } from '@playwright/test';

const tenantA = '11111111-1111-4111-8111-111111111111';
const tenantB = '22222222-2222-4222-8222-222222222222';
const applications = [
  { appId: 'sample', name: '产线分析', releaseVersion: '1.0.0', category: '生产运营', description: '查看生产节拍与设备运行信息。' },
  { appId: 'quality', name: '质量知识检索', releaseVersion: '2.1.0', category: '质量管理', description: '检索已授权的质量文件与处置记录。' },
];

// Explicit UI fixtures. Real database / authorization evidence is recorded separately.
async function fixture(page: Page, signedIn = true, selected: string | null = tenantA) {
  const state = { signedIn, selected, applications: [...applications], catalogStatus: 200, runStatus: 200, runCalls: 0, csrfWrites: 0 };
  await page.route('**/bff/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/bff/session') return route.fulfill({ status: state.signedIn ? 200 : 401, json: state.signedIn ? { iss: 'https://identity.example.test', sub: 'fixture-user', name: '李工' } : { code: 'authentication_required' } });
    if (path === '/bff/csrf') return route.fulfill({ json: { requestToken: 'fixture-csrf', headerName: 'X-CSRF-TOKEN' } });
    if (route.request().method() === 'POST') {
      expect(route.request().headers()['x-csrf-token']).toBe('fixture-csrf');
      state.csrfWrites++;
      if (path === '/bff/logout') { state.signedIn = false; return route.fulfill({ status: 204 }); }
      if (path === '/bff/tenant') state.selected = route.request().postDataJSON().tenantId;
    }
    if (path === '/bff/tenant') return route.fulfill({ json: { tenantId: state.selected, authorizationVersion: 1 } });
    return route.fulfill({ status: 404, json: {} });
  });
  await page.route('**/api/platform/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/platform/v1/tenants') return route.fulfill({ json: { items: [{ tenantId: tenantA, name: '华东制造' }, { tenantId: tenantB, name: '华南制造' }] } });
    if (path.endsWith('/runs')) {
      state.runCalls++;
      expect(route.request().headers()['x-csrf-token']).toBe('fixture-csrf');
      if (state.runStatus !== 200) return route.fulfill({ status: state.runStatus, json: { code: 'run_access_denied' } });
      return route.fulfill({ json: {
        runId: '33333333-3333-4333-8333-333333333333', tenantId: state.selected,
        appId: 'sample', releaseVersion: '1.0.0', expiresAt: new Date(Date.now() + 900000).toISOString(),
        apiBase: '/api/apps/sample/releases/1.0.0/',
        manifest: { schemaVersion: 1, appId: 'sample', name: '产线分析', version: '1.0.0', frontendEntry: '/apps/sample/0.1.0/', frontendArtifact: { version: '0.1.0', sha256: 'a'.repeat(64) } },
      } });
    }
    if (path.includes('/catalog')) {
      if (state.catalogStatus !== 200) return route.fulfill({ status: state.catalogStatus, json: { code: 'catalog_unavailable' } });
      const items = path.includes(tenantB) ? [] : state.applications;
      if (path.endsWith('/catalog')) return route.fulfill({ json: { items, categories: [...new Set(items.map(app => app.category))] } });
      const app = items.find(item => path.endsWith('/' + item.appId));
      return route.fulfill({ status: app ? 200 : 404, json: app ?? { code: 'application_not_found' } });
    }
    return route.fulfill({ status: 404, json: {} });
  });
  return state;
}

test.beforeEach(async ({ page }) => { await page.emulateMedia({ colorScheme: 'dark', reducedMotion: 'reduce' }); });

test('default portal uses enterprise sign-in and never restores the reference demo identity', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('mfg-ai-auth', 'true'));
  await fixture(page, false);
  await page.goto('/');
  await expect(page.getByRole('heading', { name: '应用中心', exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: '企业账号登录' })).toHaveAttribute('href', /\/bff\/login\?returnUrl=/);
  await expect(page.getByLabel('密码', { exact: true })).toHaveCount(0);
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await expect(page.getByText('产线分析', { exact: true })).toHaveCount(0);
});

test('authorized catalog supports category, search, empty state and deep-linked details', async ({ page }) => {
  await fixture(page);
  await page.goto('/');
  await expect(page.getByText('产线分析', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('质量知识检索', { exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: '质量管理', exact: true }).click();
  await expect(page.getByText('产线分析', { exact: true })).toHaveCount(0);
  await page.getByRole('textbox', { name: '搜索应用' }).fill('不存在的应用');
  await expect(page.getByRole('heading', { name: '没有匹配的应用' })).toBeVisible();
  await page.getByRole('button', { name: '清除筛选' }).click();
  await page.goto('/applications/sample');
  await expect(page.getByRole('heading', { name: '产线分析', exact: true, level: 2 })).toBeVisible();
  await expect(page.getByRole('complementary', { name: '应用详情' }).getByText('查看生产节拍与设备运行信息。', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: '打开应用', exact: true })).toBeVisible();
});

test('a run is created with CSRF before loading the pinned frontend, and refresh never recreates it', async ({ page }) => {
  const state = await fixture(page);
  await page.goto('/applications/sample');
  await page.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(page.locator('#sample-root')).toBeVisible();
  expect(state.runCalls).toBe(1);
  await page.reload();
  await expect(page.getByText(/重新打开/).first()).toBeVisible();
  await expect(page.locator('#sample-root')).toHaveCount(0);
  expect(state.runCalls).toBe(1);
});

test('run denial cannot load resources or preserve the previous success', async ({ page }) => {
  const state = await fixture(page); state.runStatus = 403;
  let resourceRequests = 0;
  page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/apps/sample/')) resourceRequests++; });
  await page.goto('/applications/sample');
  await page.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(page.getByRole('alert').first()).toBeVisible();
  expect(state.runCalls).toBe(1);
  expect(resourceRequests).toBe(0);
});

test('catalog failure is a retryable error, not an empty authorized list', async ({ page }) => {
  const state = await fixture(page); state.catalogStatus = 503;
  await page.goto('/');
  await expect(page.getByRole('alert').first()).toBeVisible();
  await expect(page.getByText('暂无可用应用', { exact: true })).toHaveCount(0);
  state.catalogStatus = 200;
  await page.getByRole('button', { name: /重试/ }).first().click();
  await expect(page.getByText('产线分析', { exact: true }).first()).toBeVisible();
});

test('theme, narrow layout and logout preserve only appearance', async ({ page }) => {
  const state = await fixture(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await expect(page.getByText('产线分析', { exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: '切换暗色', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.getByRole('button', { name: '退出登录', exact: true }).click();
  await expect(page.getByRole('link', { name: '企业账号登录' })).toBeVisible();
  await expect(page.getByText('产线分析', { exact: true })).toHaveCount(0);
  expect(state.signedIn).toBe(false);
  expect(state.csrfWrites).toBe(1);
  expect(await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }))).not.toContain('fixture-csrf');
});

test('tenant choice is explicit and a late old-tenant response cannot restore its catalog', async ({ page }) => {
  const state = await fixture(page, true, null);
  await page.goto('/');
  await expect(page.getByRole('heading', { name: '选择租户', exact: true })).toBeVisible();
  expect(state.csrfWrites).toBe(0);
  await page.getByLabel('当前租户', { exact: true }).selectOption(tenantA);
  await expect(page.getByText('产线分析', { exact: true }).first()).toBeVisible();
  let release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  let entered!: () => void;
  const pending = new Promise<void>(resolve => { entered = resolve; });
  await page.route(`**/tenants/${tenantA}/catalog`, async route => {
    entered(); await gate;
    try { await route.fulfill({ json: { items: applications, categories: ['生产运营', '质量管理'] } }); } catch { /* Old request was cancelled. */ }
  });
  await page.getByRole('button', { name: '刷新应用', exact: true }).click();
  await pending;
  await page.getByLabel('当前租户', { exact: true }).selectOption(tenantB);
  await expect(page.getByRole('heading', { name: '暂无可用应用' })).toBeVisible();
  release();
  // Confirm the new selection's own settled state, then trigger another authoritative read.
  await page.getByRole('button', { name: '刷新应用', exact: true }).click();
  await expect(page.getByRole('heading', { name: '暂无可用应用' })).toBeVisible();
  await expect(page.getByText('产线分析', { exact: true })).toHaveCount(0);
  await expect(page.getByLabel('当前租户', { exact: true })).toHaveValue(tenantB);
});

test('focus revalidates session and a failed logout never claims success', async ({ page }) => {
  const state = await fixture(page);
  await page.goto('/');
  await expect(page.getByText('产线分析', { exact: true }).first()).toBeVisible();
  await page.route('**/bff/logout', route => route.fulfill({ status: 503, json: { code: 'session_unavailable' } }));
  await page.getByRole('button', { name: '退出登录', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('退出登录未完成');
  await expect(page.getByRole('link', { name: '企业账号登录' })).toHaveCount(0);
  await expect(page.getByText('产线分析', { exact: true })).toHaveCount(0);
  state.signedIn = false;
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(page.getByRole('link', { name: '企业账号登录' })).toBeVisible();
});

test('missing details, revoked catalog, loading and resource failure stay explicit', async ({ page }) => {
  const state = await fixture(page);
  await page.goto('/applications/unknown');
  await expect(page.getByRole('heading', { name: '应用不可用' })).toBeVisible();
  await expect(page.getByRole('button', { name: '打开应用', exact: true })).toHaveCount(0);
  await page.goto('/applications/sample');
  await page.route('**/apps/sample/0.1.0/**', route => route.fulfill({ status: 503, body: 'Unavailable' }));
  await page.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(page.getByRole('alert').first()).toContainText('应用加载失败');
  await page.getByRole('button', { name: '返回应用详情', exact: true }).click();
  await expect(page.getByRole('heading', { name: '产线分析', exact: true, level: 2 })).toBeVisible();
  state.catalogStatus = 403;
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(page.getByRole('alert').first()).toContainText('访问权限已变化');
  await expect(page.getByText('产线分析', { exact: true })).toHaveCount(0);
});

test('opening a second run in the same document survives single-spa history events', async ({ page }) => {
  const state = await fixture(page);
  await page.goto('/applications/sample');
  await page.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(page.getByTestId('sample-app')).toBeVisible();
  await page.getByRole('button', { name: '增加计数' }).click();
  await page.getByRole('link', { name: '← 返回应用详情', exact: true }).click();
  await page.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(page.getByTestId('sample-app')).toBeVisible();
  await expect(page.getByTestId('sample-count')).toHaveText('0');
  expect(state.runCalls).toBe(2);
  await page.goBack();
  await expect(page.getByRole('heading', { name: '产线分析', exact: true, level: 2 })).toBeVisible();
  await expect(page.getByTestId('sample-app')).toHaveCount(0);
});

test('a recovered identity with failed memberships shows an error instead of another sign-in', async ({ page }) => {
  const state = await fixture(page, false);
  await page.goto('/');
  await expect(page.getByRole('link', { name: '企业账号登录' })).toBeVisible();
  state.signedIn = true;
  await page.route('**/api/platform/v1/tenants', route => route.fulfill({ status: 503, json: { code: 'authorization_unavailable' } }));
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(page.getByRole('heading', { name: '暂时无法加载工作空间' })).toBeVisible();
  await expect(page.getByRole('link', { name: '企业账号登录' })).toHaveCount(0);
});

test('a hanging application lifecycle resets the document without creating another run', async ({ page }) => {
  const state = await fixture(page);
  await page.route('**/apps/sample/0.1.0/', route => route.fulfill({ contentType: 'text/html', body: '<div id="sample-root"></div><script src="/apps/sample/0.1.0/hang.js" entry></script>' }));
  await page.route('**/apps/sample/0.1.0/hang.js', route => route.fulfill({ contentType: 'text/javascript', body: 'window.assembleaiHangingFixture={bootstrap:function(){return Promise.resolve()},mount:function(){return new Promise(function(){})},unmount:function(){return Promise.resolve()}};' }));
  await page.goto('/applications/sample');
  await page.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(page).toHaveURL(/\/applications\/sample\?runError=timeout$/, { timeout: 22000 });
  await expect(page.getByRole('alert').first()).toContainText('未能及时');
  await expect(page.getByRole('heading', { name: '产线分析', exact: true, level: 2 })).toBeVisible();
  expect(state.runCalls).toBe(1);
});

test('a rejected unmount resets the document before another application can mount', async ({ page }) => {
  const state = await fixture(page);
  await page.route('**/apps/sample/0.1.0/', route => route.fulfill({ contentType: 'text/html', body: '<div id="sample-root"></div><script src="/apps/sample/0.1.0/cleanup.js" entry></script>' }));
  await page.route('**/apps/sample/0.1.0/cleanup.js', route => route.fulfill({ contentType: 'text/javascript', body: 'window.assembleaiCleanupFixture={bootstrap:function(){return Promise.resolve()},mount:function(props){props.container.querySelector("#sample-root").textContent="卸载回归应用已挂载";return Promise.resolve()},unmount:function(){return Promise.reject(new Error("fixture cleanup failed"))}};' }));
  await page.goto('/applications/sample');
  await page.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(page.getByText('卸载回归应用已挂载', { exact: true })).toBeVisible();
  await page.getByRole('link', { name: '← 返回应用详情', exact: true }).click();
  await expect(page).toHaveURL(/\/applications\/sample\?runError=cleanup$/);
  await expect(page.getByRole('alert')).toContainText('退出未完成');
  await expect(page.getByText('卸载回归应用已挂载', { exact: true })).toHaveCount(0);
  expect(state.runCalls).toBe(1);
  await page.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(page.getByText('卸载回归应用已挂载', { exact: true })).toBeVisible();
  expect(state.runCalls).toBe(2);
});

async function retainedApiFixture(page: Page, hangingMount: boolean) {
  await fixture(page);
  let gatewayCalls = 0;
  const reports: { code: string }[] = [];
  await page.exposeBinding('reportRetainedApi', (_source, result: { code: string }) => { reports.push(result); });
  await page.addInitScript(() => {
    document.addEventListener('assembleai:retained-api-result', (event) => {
      const report = (window as unknown as { reportRetainedApi: (result: { code: string }) => Promise<void> }).reportRetainedApi;
      void report((event as CustomEvent<{ code: string }>).detail);
    });
  });
  await page.route('**/api/apps/sample/releases/1.0.0/probe', route => { gatewayCalls++; return route.fulfill({ json: { ok: true } }); });
  await page.route('**/apps/sample/0.1.0/', route => route.fulfill({ contentType: 'text/html', body: '<div id="sample-root"></div><script src="/apps/sample/0.1.0/retained-api.js" entry></script>' }));
  await page.route('**/apps/sample/0.1.0/retained-api.js', route => route.fulfill({ contentType: 'text/javascript', body: `
    window.assembleaiRetainedApiFixture={
      bootstrap:function(){return Promise.resolve()},
      mount:function(props){
        var api=props.api;
        document.addEventListener('assembleai:probe-retained-api',function(){
          Promise.resolve().then(function(){return api.json('probe')})
            .then(function(){document.dispatchEvent(new CustomEvent('assembleai:retained-api-result',{detail:{code:'success'}}))})
            .catch(function(error){document.dispatchEvent(new CustomEvent('assembleai:retained-api-result',{detail:{code:error.code||'unexpected'}}))});
        });
        props.container.querySelector('#sample-root').textContent='能力回归应用已挂载';
        return ${hangingMount ? 'new Promise(function(){})' : 'Promise.resolve()'};
      },
      update:function(){return Promise.reject(new Error('fixture update failed'))},
      unmount:function(){return Promise.resolve()}
    };` }));
  await page.goto('/applications/sample');
  await page.getByRole('button', { name: '打开应用', exact: true }).click();
  await expect(page.getByText('能力回归应用已挂载', { exact: true })).toBeVisible();
  const probe = async () => {
    const before = reports.length;
    await page.evaluate(() => document.dispatchEvent(new CustomEvent('assembleai:probe-retained-api')));
    await expect.poll(() => reports.length).toBe(before + 1);
    return reports.at(-1);
  };
  expect(await probe()).toEqual({ code: 'success' });
  expect(gatewayCalls).toBe(1);
  return { probe, reports, calls: () => gatewayCalls };
}

test('T7: rejected theme update disposes the retained API without another gateway request', async ({ page }) => {
  const capability = await retainedApiFixture(page, false);
  await page.getByRole('button', { name: '切换暗色', exact: true }).click();
  await expect(page.getByRole('alert').first()).toContainText('应用外观更新失败');
  expect(await capability.probe()).toEqual({ code: 'disposed' });
  expect(capability.calls()).toBe(1);
});

test('T7: hanging mount disposes the retained API while document replacement is delayed', async ({ page }) => {
  const capability = await retainedApiFixture(page, true);
  let navigationStarted = false;
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  await page.route('**/applications/sample?runError=timeout', async route => {
    navigationStarted = true; await gate;
    try { await route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>Lifecycle test cleanup</title>' }); } catch { /* Test cleanup can close the original document. */ }
  });
  try {
    // Install the trigger before navigation starts; issuing a fresh evaluation
    // during pending document replacement can itself wait for that navigation.
    await page.evaluate(() => { window.setTimeout(() => document.dispatchEvent(new CustomEvent('assembleai:probe-retained-api')), 16_000); });
    await expect.poll(() => navigationStarted, { timeout: 22000 }).toBe(true);
    await expect.poll(() => capability.reports.length, { timeout: 6000 }).toBe(2);
    expect(capability.reports.at(-1)).toEqual({ code: 'disposed' });
    expect(capability.calls()).toBe(1);
  } finally {
    const replaced = page.waitForEvent('domcontentloaded', { timeout: 5000 });
    release(); await replaced;
  }
});
