import { chooseReferenceOption, expectReferenceValue, toggleReferenceTheme } from './reference-controls-helpers.mjs';
import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import { resolve, relative, isAbsolute } from 'node:path';
import { chromium, expect } from '@playwright/test';

const configPath = resolve(process.argv[2]);
const local = relative(resolve('.local-data/postgres-tests'), configPath);
assert.ok(local && !local.startsWith('..') && !isAbsolute(local));
const config = JSON.parse(await readFile(configPath, 'utf8'));
const origin = new URL(config.origin);
assert.equal(origin.hostname, '127.0.0.1'); assert.equal(origin.protocol, 'http:');
const artifactDirectory = resolve('.artifacts/registry-postgres', config.tenantId);
await mkdir(artifactDirectory, { recursive: true });
const browser = await chromium.launch({ headless: true });
const errors = [];
const contexts = [];
const route = `/manage/applications/${config.appId}`;
try {
  async function login(account) {
    const context = await browser.newContext({ baseURL: origin.origin, viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce' });
    contexts.push(context);
    const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
    await page.goto('/bff/login?returnUrl=' + encodeURIComponent('/manage/applications'));
    await page.getByRole('link', { name: account, exact: true }).click();
    await page.waitForURL(/\/manage\/applications\/?$/);
    return page;
  }
  async function confirm(page, action) {
    await page.getByRole('button', { name: action, exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: `确认${action}`, exact: true }).click();
  }
  async function form(page, manifest, fresh) {
    if (fresh) await page.getByLabel('应用标识', { exact: true }).fill(manifest.appId);
    await page.getByLabel('应用名称', { exact: true }).fill(manifest.name);
    await page.getByLabel('分类', { exact: true }).fill(manifest.category);
    await page.getByLabel('说明', { exact: true }).fill(manifest.description);
    await page.getByLabel('发布版本', { exact: true }).fill(manifest.version);
    await page.getByLabel('前端版本', { exact: true }).fill(manifest.frontendArtifact.version);
    await page.getByLabel('发布物 SHA256', { exact: true }).fill(manifest.frontendArtifact.sha256);
    await page.getByLabel('后端服务', { exact: true }).fill(manifest.backendService);
    await page.getByLabel('后端版本', { exact: true }).fill(manifest.backendVersion);
    await page.getByLabel('入口权限', { exact: true }).fill(manifest.permissions.join('\n'));
    const saved = page.waitForResponse(response => response.request().method() === 'PUT' && new URL(response.url()).pathname.endsWith(`/releases/${manifest.version}`));
    await page.getByRole('button', { name: '保存草稿', exact: true }).click();
    assert.equal((await saved).status(), 200);
    await expectReferenceValue(page, '查看发布版本', manifest.version);
  }
  async function show(page, version = '1.0.0') {
    await page.goto(route);
    await chooseReferenceOption(page, '查看发布版本', version);
  }
  async function api(page, method, path, body) {
    return page.evaluate(async ({ method, path, body }) => {
      const headers = { Accept: 'application/json' };
      if (method !== 'GET') {
        const csrf = await fetch('/bff/csrf', { credentials: 'same-origin', cache: 'no-store' }).then(response => response.json());
        headers[csrf.headerName] = csrf.requestToken;
        if (body !== undefined) headers['Content-Type'] = 'application/json';
      }
      const response = await fetch(path, { method, headers, credentials: 'same-origin', cache: 'no-store', body: body === undefined ? undefined : JSON.stringify(body) });
      return { status: response.status, body: await response.json() };
    }, { method, path, body });
  }
  const owner = await login('研发账号');
  // Owner has no tenant membership. Global release management still works.
  await owner.getByRole('button', { name: '注册应用', exact: true }).click();
  await form(owner, config.manifests[0], true);
  await owner.getByRole('button', { name: '编辑草稿', exact: true }).click();
  await owner.getByLabel('说明', { exact: true }).fill('真实注册审核与租户发布验收');
  await owner.getByRole('button', { name: '保存草稿', exact: true }).click();
  await expect(owner.getByRole('button', { name: '提交审核', exact: true })).toBeEnabled();
  await confirm(owner, '提交审核');
  const releasePath = `/api/platform/v1/registry/apps/${config.appId}/releases/1.0.0`;
  await expect.poll(async () => (await api(owner, 'GET', releasePath)).body.manifest.status).toBe('pending_review');
  const pending = await api(owner, 'GET', releasePath);
  assert.equal((await api(owner, 'POST', releasePath + '/transitions', { expectedRevision: pending.body.revision, action: 'publish' })).status, 403, 'Even a developer/reviewer cannot approve their own submission.');
  const reviewer = await login('审核账号');
  await show(reviewer);
  await confirm(reviewer, '发布版本');
  await expect.poll(async () => (await api(owner, 'GET', releasePath)).body.manifest.status).toBe('published');
  await show(owner);
  await owner.getByRole('button', { name: '新建版本', exact: true }).click();
  await form(owner, config.manifests[1], false);
  await confirm(owner, '提交审核');
  await show(reviewer, '2.0.0');
  await confirm(reviewer, '发布版本');
  await expect.poll(async () => (await api(owner, 'GET', releasePath.replace('1.0.0', '2.0.0'))).body.manifest.status).toBe('published');
  await reviewer.screenshot({ path: resolve(artifactDirectory, 'published-releases.png'), fullPage: true });

  const admin = await login('租户管理员');
  await chooseReferenceOption(admin, '当前租户', config.tenantId);
  // A forced full navigation can cancel the asynchronous POST initiated by the
  // selector. Wait for the server-owned selection, as the disabled UI does.
  await expect.poll(async () => (await api(admin, 'GET', '/bff/tenant')).body.tenantId).toBe(config.tenantId);
  await show(admin);
  const tenantPath = `/api/platform/v1/tenants/${config.tenantId}/applications/${config.appId}`;
  async function tenantChange(version, enabled = true) {
    await chooseReferenceOption(admin, '租户使用版本', version);
    await admin.getByRole('button', { name: enabled ? /^(启用到当前租户|应用版本到当前租户)$/ : '停用当前租户应用' }).click();
    await admin.getByRole('dialog').getByRole('button', { name: '确认租户变更', exact: true }).click();
    await expect.poll(async () => {
      const response = await api(admin, 'GET', tenantPath);
      return { status: response.status, version: response.body.releaseVersion, enabled: response.body.enabled };
    }).toEqual({ status: 200, version, enabled });
  }
  await tenantChange('1.0.0');
  assert.equal((await api(admin, 'GET', releasePath)).status, 403, 'Tenant manager must not become a global release operator.');
  async function run(version, expected = 200) {
    const response = await api(admin, 'POST', `${tenantPath}/releases/${version}/runs`);
    assert.equal(response.status, expected);
    return response.body;
  }
  async function probe(binding, expected = 200) {
    const status = await admin.evaluate(async binding => {
      const csrf = await fetch('/bff/csrf').then(value => value.json());
      const response = await fetch(`/api/apps/${binding.appId}/releases/${binding.releaseVersion}/probe`, {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf.requestToken, 'X-Run-ID': binding.runId, 'X-Tenant-ID': binding.tenantId },
        body: JSON.stringify({ message: 'lifecycle probe' }),
      });
      await response.text(); return response.status;
    }, binding);
    assert.equal(status, expected);
  }
  async function stream(binding) {
    await admin.evaluate(binding => {
      window.registryStream = { chunks: 0, done: false, completed: false, status: null };
      const state = window.registryStream;
      void (async () => {
        try {
          const response = await fetch(`/api/apps/${binding.appId}/releases/${binding.releaseVersion}/events`, {
            headers: { 'X-Run-ID': binding.runId, 'X-Tenant-ID': binding.tenantId }, credentials: 'same-origin', cache: 'no-store',
          });
          state.status = response.status;
          const reader = response.body.getReader(); const decoder = new TextDecoder(); let text = '';
          try { while (true) { const next = await reader.read(); if (next.done) break; text += decoder.decode(next.value, { stream: true }); state.chunks++; state.completed = text.includes('event: complete'); } }
          finally { await reader.cancel().catch(() => undefined); reader.releaseLock(); }
        } catch { /* Revocation may terminate the transport after committed headers. */ }
        finally { state.done = true; }
      })();
    }, binding);
    await expect.poll(() => admin.evaluate(() => window.registryStream.chunks)).toBeGreaterThan(0);
    assert.equal(await admin.evaluate(() => window.registryStream.status), 200);
  }
  async function ended() {
    await expect.poll(() => admin.evaluate(() => window.registryStream.done), { timeout: 12000 }).toBe(true);
    assert.equal(await admin.evaluate(() => window.registryStream.completed), false, 'A stopped stream must not claim normal completion.');
  }
  const first = await run('1.0.0'); await probe(first); await stream(first);
  await tenantChange('2.0.0');
  // Switching starts draining instead of cancelling the already admitted stream
  // immediately. SQL and monotonic deadline rules are exercised by the gateway.
  assert.equal(await admin.evaluate(() => window.registryStream.done), false);
  await run('1.0.0', 403); await probe(await run('2.0.0'));
  await ended(); await probe(first, 403);
  await tenantChange('1.0.0'); await probe(first, 403);
  const restored = await run('1.0.0'); await probe(restored); await stream(restored);
  await show(reviewer); await confirm(reviewer, '全局停用');
  await ended(); await probe(restored, 403);
  await confirm(reviewer, '恢复发布');
  await expect.poll(async () => (await api(owner, 'GET', releasePath)).body.manifest.status).toBe('published');
  await probe(restored, 403); const enabled = await run('1.0.0'); await stream(enabled);
  await tenantChange('1.0.0', false); await ended(); await probe(enabled, 403);
  await tenantChange('1.0.0'); await probe(enabled, 403); await probe(await run('1.0.0'));
  await admin.screenshot({ path: resolve(artifactDirectory, 'tenant-version-management.png'), fullPage: true });
  await toggleReferenceTheme(admin);
  await expect(admin.locator('html')).toHaveAttribute('data-theme', 'dark');
  await admin.screenshot({ path: resolve(artifactDirectory, 'tenant-dark.png'), fullPage: true });
  await show(reviewer);
  const audit = reviewer.getByTestId('registry-audit'); await expect(audit).toBeVisible();
  await expect(audit).toContainText(config.ownerId); await expect(audit).toContainText(config.reviewerId);
  await show(admin);
  await expect(admin.getByTestId('registry-audit')).toContainText(config.adminId);
  await admin.setViewportSize({ width: 390, height: 844 });
  assert.equal(await admin.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
  await admin.screenshot({ path: resolve(artifactDirectory, 'tenant-mobile.png'), fullPage: true });
  for (const page of [owner, reviewer, admin]) {
    const data = await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }));
    assert.ok(!data.includes('fixture-access-token') && !data.includes('fixture-refresh-token') && !data.includes('requestToken'));
  }
  assert.deepEqual(errors, []);
  process.stdout.write('PASS: registry browser: real independent OIDC accounts, draft/edit/review/publish, selected-tenant enable/switch/rollback/disable, old-run drain/revocation and visible attributable audit.\n');
} catch (error) {
  for (const [index, context] of contexts.entries()) {
    const page = context.pages()[0];
    if (page) {
      await page.screenshot({ path: resolve(artifactDirectory, `failure-${index}.png`), fullPage: true }).catch(() => undefined);
      const diagnostic = await page.locator('body').innerText().catch(() => 'Unavailable page');
      process.stderr.write(`Registry fixture page ${index}: ${diagnostic.slice(0, 1800)}\n`);
    }
  }
  throw error;
} finally { await Promise.allSettled(contexts.map(context => context.close())); await browser.close(); }
