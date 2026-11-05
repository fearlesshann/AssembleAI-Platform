import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, mkdir } from 'node:fs/promises';
import { chromium } from '@playwright/test';

// Standalone visual/authentication-boundary check. Uses an ephemeral port so it
// cannot disturb an active portal, native Keycloak, or Playwright webServer.
const assets = new URL('../apps/portal/public/reference-login/', import.meta.url);
const reference = new URL('../references/ui-v1/', import.meta.url);
const artifact = new URL('../.artifacts/reference-login/', import.meta.url);
await mkdir(artifact, { recursive: true });
const template = await readFile(new URL('login.html', assets), 'utf8');
const slots = { ASSET_BASE: '/assets', LOGIN_ACTION: '/identity-submit', USERNAME: '', ERROR: '', CSRF_TOKEN: 'form-token', FORGOT_HREF: '/reset', ERROR_HIDDEN: 'hidden', REMEMBER_CHECKED: '' };
const login = template.replace(/@@([A-Z_]+)@@/g, (_, key) => {
  assert.ok(Object.hasOwn(slots, key), `Unknown fixture slot ${key}`);
  return slots[key];
});
const requests = [];
const types = { css: 'text/css', js: 'text/javascript', png: 'image/png', html: 'text/html', woff2: 'font/woff2', ico: 'image/x-icon' };
const server = createServer(async (req, res) => {
  try {
    const pathname = new URL(req.url, 'http://localhost').pathname;
    if (pathname === '/identity-submit') {
      let body = ''; for await (const part of req) body += part;
      requests.push({ method: req.method, body });
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
      res.end('<p>Identity provider received the native POST.</p>'); return;
    }
    if (pathname === '/login') { res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' }); res.end(login); return; }
    const root = pathname.startsWith('/assets/') ? assets : reference;
    const relative = pathname.startsWith('/assets/') ? pathname.slice(8) : pathname.replace(/^\/reference\//, '');
    const file = new URL(relative || 'index.html', root);
    if (!file.href.startsWith(root.href)) { res.writeHead(404).end(); return; }
    const content = await readFile(file);
    res.writeHead(200, { 'content-type': `${types[file.pathname.split('.').pop()] ?? 'application/octet-stream'}; charset=utf-8` }); res.end(content);
  } catch { res.writeHead(404).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch();
try {
  const selectors = ['.login-screen', '.login-hero', '.login-hero-inner', '.login-brand-mark', '.login-brand-text', '.login-hero-title', '.login-hero-sub', '.login-proof', '.login-shot-deck', '.login-shot.is-front', '.login-card', '.login-card-inner', '.login-form-heading', '.login-form-title', '.login-form-desc', '.login-field', '.login-label', '#loginAccount', '#loginPassword', '.login-row', '.login-check', '#loginRemember', '#loginForgot', '#loginSubmit', '.login-security-note', '#loginThemeToggle'];
  const styles = ['display', 'position', 'backgroundColor', 'color', 'fontFamily', 'fontSize', 'fontWeight', 'lineHeight', 'letterSpacing', 'padding', 'gap', 'border', 'borderRadius', 'boxShadow', 'gridTemplateColumns', 'gridTemplateRows'];
  const measure = async page => page.evaluate(({ selectors, styles }) => Object.fromEntries(selectors.map(selector => {
    const el = document.querySelector(selector), css = getComputedStyle(el), rect = el.getBoundingClientRect();
    return [selector, { styles: Object.fromEntries(styles.map(style => [style, css[style]])), rect: [rect.x, rect.y, rect.width, rect.height].map(value => Math.round(value * 100) / 100) }];
  })), { selectors, styles });
  const settle = async pages => Promise.all(pages.map(page => page.evaluate(async () => {
    // Reading styles creates any newly triggered CSS transitions before waiting.
    document.querySelectorAll('#loginScreen *').forEach(element => getComputedStyle(element).color);
    await Promise.all(document.getAnimations().filter(animation => animation.effect.getTiming().iterations !== Infinity).map(animation => animation.finished.catch(() => {})));
  })));
  for (const theme of ['dark', 'light']) {
    for (const viewport of [{ width: 1440, height: 900 }, { width: 1366, height: 900 }, { width: 390, height: 844 }]) {
      const context = await browser.newContext({ viewport, colorScheme: theme, reducedMotion: 'reduce' });
      await context.addInitScript(({ theme }) => {
        localStorage.setItem('mfg-ai-theme', theme); localStorage.setItem('assembleai.theme', theme);
        document.documentElement?.setAttribute('data-motion', 'reduced');
        Math.random = () => 0.5;
      }, { theme });
      await context.route('https://static.linear.app/fonts/**', async route => route.fulfill({ body: await readFile(new URL('InterVariable.woff2', assets)), contentType: 'font/woff2' }));
      const original = await context.newPage(); const actual = await context.newPage();
      const errors = []; actual.on('pageerror', error => errors.push(error.message));
      await original.goto(`${origin}/reference/index.html`); await actual.goto(`${origin}/login`);
      await Promise.all([original.evaluate(() => document.fonts.ready), actual.evaluate(() => document.fonts.ready)]);
      // Business copy may differ. Normalize only those two text nodes in the
      // reference DOM so content wrapping cannot masquerade as a style mismatch.
      await original.evaluate(() => {
        document.documentElement.dataset.motion = 'reduced';
        document.querySelector('.login-form-desc').textContent = '使用企业账号登录，访问权限范围内的应用与数据。';
        document.querySelector('.login-hero-foot').textContent = '企业 AI 应用平台 · 工厂协作 · RBAC';
      });
      await settle([original, actual]);
      await assert.deepEqual(await measure(actual), await measure(original), `login ${theme} ${viewport.width}px geometry/styles`);
      for (const page of [original, actual]) await page.locator('#loginAccount').focus();
      await settle([original, actual]);
      await assert.deepEqual(await measure(actual), await measure(original), `login ${theme} ${viewport.width}px focus`);
      for (const page of [original, actual]) await page.locator('#loginRemember').check();
      await settle([original, actual]);
      await assert.deepEqual(await measure(actual), await measure(original), `login ${theme} ${viewport.width}px checkbox`);
      const canvasImage = async (page, selector) => page.locator(selector).evaluate(canvas => {
        const context = canvas.getContext('2d');
        return { width: canvas.width, height: canvas.height, painted: context.getImageData(0, 0, canvas.width, canvas.height).data.some(value => value !== 0), pixels: canvas.toDataURL() };
      });
      for (const selector of ['#loginWavesCanvas', '#loginEther', '#loginSubmitEffect canvas']) {
        const originalCanvas = await canvasImage(original, selector), actualCanvas = await canvasImage(actual, selector);
        assert.equal(actualCanvas.painted, true, `A canvas node alone does not prove ${selector} was rendered`);
        assert.deepEqual(actualCanvas, originalCanvas, `Seeded reduced-motion ${selector} pixels must match the original drawing`);
      }
      for (const [kind, page] of [['reference', original], ['actual', actual]]) await page.screenshot({ path: new URL(`${theme}-${viewport.width}-${kind}.png`, artifact).pathname.replace(/^\/(\w:)/, '$1') });
      assert.deepEqual(errors, [], 'No browser JavaScript failures');
      await context.close();
      console.log(`PASS ${theme} ${viewport.width}×${viewport.height}: original login geometry, styles, focus, checkbox and all three canvas pixels`);
    }
  }
  const context = await browser.newContext(); const page = await context.newPage();
  await page.goto(`${origin}/login`);
  await page.locator('#loginSubmit').click();
  assert.equal(requests.length, 0, 'Empty form is not posted');
  assert.equal(await page.locator('#loginAccount').getAttribute('aria-invalid'), 'true');
  await page.locator('#loginAccount').fill('alice'); await page.locator('#loginPassword').fill('temporary-test-value');
  await page.locator('#loginRemember').check();
  await Promise.all([page.waitForURL('**/identity-submit'), page.locator('#loginSubmit').click()]);
  assert.equal(requests.length, 1); assert.equal(requests[0].method, 'POST');
  assert.deepEqual(Object.fromEntries(new URLSearchParams(requests[0].body)), { username: 'alice', password: 'temporary-test-value', rememberMe: 'on', form_token: 'form-token' });
  assert.deepEqual(await page.evaluate(() => ({ local: Object.keys(localStorage), session: Object.keys(sessionStorage) })), { local: [], session: [] }, 'No credentials or fake auth state persisted');
  await context.close();
  console.log('PASS empty-field validation and native identity-provider POST; no credential/auth-state browser persistence');
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
