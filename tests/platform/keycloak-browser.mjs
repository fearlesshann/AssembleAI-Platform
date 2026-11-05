import assert from 'node:assert/strict';
import { readFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from '@playwright/test';

// Explicit opt-in integration test. The launcher owns the real Keycloak and BFF
// processes; this script only owns its browser contexts. Never print fixture data.
const workspace = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const tokenField = /\b(?:access_token|refresh_token|id_token)\b/i;
const jwtValue = /\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b/;

function within(directory, candidate) {
  const relative = path.relative(directory, candidate);
  return relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative));
}

function loopbackUrl(value) {
  assert.equal(typeof value, 'string');
  const url = new URL(value);
  assert.ok(url.protocol === 'http:' || url.protocol === 'https:');
  assert.ok(['localhost', '127.0.0.1', '[::1]'].includes(url.hostname));
  assert.ok(!url.username && !url.password && !url.search && !url.hash);
  return url;
}

async function readConfiguration() {
  const configPath = process.env.ASSEMBLEAI_KEYCLOAK_TEST_CONFIG;
  assert.ok(configPath && path.isAbsolute(configPath));
  assert.ok(['.local-data', '.artifacts'].some(directory => within(path.join(workspace, directory), configPath)));
  const config = JSON.parse(await readFile(configPath, 'utf8'));
  const platform = loopbackUrl(config.platformOrigin);
  const authority = loopbackUrl(config.authority);
  assert.equal(config.platformOrigin, platform.origin);
  assert.ok(authority.pathname.startsWith('/realms/') && authority.pathname.length > '/realms/'.length);
  assert.notEqual(platform.origin, authority.origin);
  for (const name of ['username', 'password', 'subject'])
    assert.ok(typeof config[name] === 'string' && config[name].length > 0);
  assert.ok(typeof config.artifactDirectory === 'string' && path.isAbsolute(config.artifactDirectory));
  assert.ok(within(path.join(workspace, '.artifacts'), config.artifactDirectory));
  assert.ok(typeof config.resetMailFile === 'string' && path.isAbsolute(config.resetMailFile));
  assert.ok(within(path.join(workspace, '.local-data/keycloak-tests'), config.resetMailFile));
  return config;
}

async function assertReferenceTheme(page, artifactDirectory, name) {
  await page.locator('#loginScreen .login-hero').waitFor({ state: 'visible' });
  assert.equal(await page.locator('.login-shot-deck .login-shot').count(), 3);
  assert.equal(await page.locator('#loginThemeToggle').count(), 1);
  await page.evaluate(() => document.fonts.ready);
  await page.evaluate(async () => {
    document.querySelectorAll('#loginScreen *').forEach(element => getComputedStyle(element).opacity);
    await Promise.all(document.getAnimations().filter(animation => animation.effect.getTiming().iterations !== Infinity).map(animation => animation.finished.catch(() => {})));
  });
  const geometry = await page.evaluate(() => ({
    columns: getComputedStyle(document.querySelector('.login-screen')).gridTemplateColumns.split(' ').length,
    inputs: [...document.querySelectorAll('.login-input')].map(input => ({ height: input.getBoundingClientRect().height, radius: getComputedStyle(input).borderRadius })),
    button: document.querySelector('#loginSubmit') ? { height: document.querySelector('#loginSubmit').getBoundingClientRect().height, radius: getComputedStyle(document.querySelector('#loginSubmit')).borderRadius } : null,
    canvas: (() => { const canvas = document.querySelector('#loginWavesCanvas'); return { width: canvas.width, painted: canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data.some(value => value !== 0) }; })(),
  }));
  assert.equal(geometry.columns, 2);
  assert.ok(geometry.canvas.width > 300 && geometry.canvas.painted, 'Original login waves must be drawn, not merely mounted as an empty canvas');
  for (const input of geometry.inputs) assert.deepEqual(input, { height: 48, radius: '4px' });
  if (geometry.button) assert.deepEqual(geometry.button, { height: 48, radius: '12px' });
  await page.screenshot({ path: path.join(artifactDirectory, name), fullPage: true });
}

async function resetMailUrl(config) {
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    try {
      const mail = JSON.parse(await readFile(config.resetMailFile, 'utf8'));
      assert.ok(mail.resetUrl.startsWith(config.authority + '/login-actions/action-token?'));
      return mail.resetUrl;
    } catch { await new Promise(resolve => setTimeout(resolve, 100)); }
  }
  throw new Error('Private reset mail was not delivered');
}

function noTokens(text) {
  assert.ok(!tokenField.test(text), 'A browser-visible platform response contained an OIDC token field.');
  assert.ok(!jwtValue.test(text), 'A browser-visible platform response contained a JWT.');
}

async function inspectApiResponse(response, expectedStatus) {
  assert.equal(response.status(), expectedStatus);
  const headers = response.headers();
  assert.ok(headers['cache-control']?.includes('no-store'));
  noTokens(JSON.stringify(headers));
  const body = await response.text();
  noTokens(body);
  return body;
}

async function assertSession(context, config, expectedStatus = 200) {
  const response = await context.request.get('/bff/session', { maxRedirects: 0 });
  const body = await inspectApiResponse(response, expectedStatus);
  if (expectedStatus === 200) {
    const session = JSON.parse(body);
    assert.equal(session.iss, config.authority);
    assert.equal(session.sub, config.subject);
    assert.deepEqual(Object.keys(session).sort(), ['iss', 'name', 'sub']);
  }
}

async function csrfToken(context) {
  const response = await context.request.get('/bff/csrf');
  const value = JSON.parse(await inspectApiResponse(response, 200));
  assert.equal(value.headerName, 'X-CSRF-TOKEN');
  assert.ok(typeof value.requestToken === 'string' && value.requestToken.length > 0);
  return value.requestToken;
}

async function browserLogout(page, token) {
  const result = await page.evaluate(async requestToken => {
    const response = await fetch('/bff/logout', {
      method: 'POST',
      credentials: 'same-origin',
      headers: requestToken ? { 'X-CSRF-TOKEN': requestToken } : {}
    });
    // Consume the stream before navigating again. Keeping only the status lets
    // Chromium discard the body before the response observer can inspect it.
    return {
      status: response.status,
      headers: Object.fromEntries(response.headers.entries()),
      body: await response.text()
    };
  }, token);
  assert.ok(result.headers['cache-control']?.includes('no-store'));
  noTokens(JSON.stringify(result.headers));
  noTokens(result.body);
  return result.status;
}

async function run() {
  let stage = 'read isolated test configuration';
  let browser;
  const contexts = [];
  try {
    const config = await readConfiguration();
    stage = 'launch Chromium';
    browser = await chromium.launch();
    const context = await browser.newContext({ baseURL: config.platformOrigin, viewport: { width: 1366, height: 900 }, colorScheme: 'dark' });
    contexts.push(context);
    context.setDefaultTimeout(20_000);
    context.setDefaultNavigationTimeout(30_000);
    const responseChecks = [];
    const responseFailures = [];
    let browserCalledTokenEndpoint = false;
    context.on('request', request => {
      const url = new URL(request.url());
      if (url.pathname.endsWith('/protocol/openid-connect/token')) browserCalledTokenEndpoint = true;
    });
    context.on('response', response => {
      const url = new URL(response.url());
      if (url.origin !== config.platformOrigin) return;
      if (!url.pathname.startsWith('/bff/') && url.pathname !== '/signin-oidc') return;
      // Diagnostics use only allowlisted routes, numeric statuses and fixed
      // categories. Never include the URL query, headers, body or exception.
      const route = ['/bff/login', '/bff/session', '/bff/logout', '/bff/csrf', '/signin-oidc']
        .includes(url.pathname) ? url.pathname : '/bff/*';
      const failure = (check, part) => responseFailures.push({ check, part, route, status: response.status() });
      const inspectTokens = (text, part) => {
        if (tokenField.test(text)) failure('token-field', part);
        if (jwtValue.test(text)) failure('jwt', part);
      };
      responseChecks.push((async () => {
        let headers;
        try { headers = await response.allHeaders(); }
        catch { failure('headers-read', 'headers'); }
        if (headers) {
          if (!headers['cache-control']?.includes('no-store')) failure('missing-no-store', 'headers');
          inspectTokens(JSON.stringify(headers), 'headers');
        }
        // HTTP 204/205 have no response content; Chromium rejects getResponseBody
        // for 204 rather than returning an empty string. Redirect bodies are also
        // unavailable. All of these responses still receive the header checks.
        const status = response.status();
        if (status !== 204 && status !== 205 && (status < 300 || status >= 400)) {
          let body;
          try { body = await response.text(); }
          catch { failure('body-read', 'body'); }
          if (body !== undefined) inspectTokens(body, 'body');
        }
      })().catch(() => { failure('response-inspection', 'unknown'); }));
    });
    const page = await context.newPage();

    stage = 'reject anonymous session requests';
    await assertSession(context, config, 401);
    stage = 'open the real Keycloak login form';
    await page.goto('/bff/login?returnUrl=/bff/session');
    assert.equal(new URL(page.url()).origin, new URL(config.authority).origin);
    await page.locator('#loginAccount').waitFor({ state: 'visible' });
    await page.locator('#loginPassword').waitFor({ state: 'visible' });
    await mkdir(config.artifactDirectory, { recursive: true });
    await assertReferenceTheme(page, config.artifactDirectory, 'keycloak-login.png');
    await page.locator('#loginRemember').check();

    stage = 'reject an incorrect Keycloak password';
    await page.locator('#loginAccount').fill(config.username);
    await page.locator('#loginPassword').fill(`${config.password}-incorrect`);
    await Promise.all([
      page.waitForNavigation({ waitUntil: 'domcontentloaded' }),
      page.locator('#loginSubmit').click()
    ]);
    assert.equal(new URL(page.url()).origin, new URL(config.authority).origin);
    await page.locator('#loginPassword').waitFor({ state: 'visible' });
    await page.locator('#loginFormError').waitFor({ state: 'visible' });
    assert.equal(await page.locator('#loginRemember').isChecked(), true);
    await assertReferenceTheme(page, config.artifactDirectory, 'keycloak-login-error.png');
    await assertSession(context, config, 401);

    stage = 'complete real Keycloak authorization-code login';
    await page.locator('#loginAccount').fill(config.username);
    await page.locator('#loginPassword').fill(config.password);
    await Promise.all([
      page.waitForURL(url => url.origin === config.platformOrigin && url.pathname === '/bff/session',
        { waitUntil: 'domcontentloaded' }),
      page.locator('#loginSubmit').click()
    ]);
    await assertSession(context, config);
    stage = 'verify the persistent identity-provider remember-me Cookie';
    // Keycloak scopes its Cookie to /realms/<realm>/, unlike the BFF's root
    // Cookie. Query that path instead of incorrectly filtering at origin /.
    const identityCookies = await context.cookies();
    const rememberedIdentity = identityCookies.find(cookie => cookie.name === 'KEYCLOAK_REMEMBER_ME');
    if (!rememberedIdentity || rememberedIdentity.expires <= Date.now() / 1000) {
      console.error('FAIL: remember-me Cookie metadata ' + JSON.stringify(identityCookies.map(({ name, path: cookiePath, expires, httpOnly }) => ({ name, path: cookiePath, expires, httpOnly }))));
    }
    assert.ok(rememberedIdentity && rememberedIdentity.expires > Date.now() / 1000, 'Remember me is persisted by the actual identity provider');

    stage = 'verify browser session and token boundaries';
    const sessionCookies = (await context.cookies(config.platformOrigin))
      .filter(cookie => cookie.name === 'assembleai-session');
    assert.equal(sessionCookies.length, 1);
    const oldCookie = sessionCookies[0];
    assert.equal(oldCookie.httpOnly, true);
    assert.equal(oldCookie.sameSite, 'Lax');
    assert.equal(oldCookie.path, '/');
    assert.equal(oldCookie.expires, -1);
    const browserState = await page.evaluate(() => ({
      cookie: document.cookie,
      local: Object.fromEntries(Object.entries(localStorage)),
      session: Object.fromEntries(Object.entries(sessionStorage))
    }));
    assert.ok(!browserState.cookie.includes('assembleai-session='));
    noTokens(JSON.stringify(browserState));
    await inspectApiResponse(await context.request.get('/bff/logout', { maxRedirects: 0 }), 405);
    await inspectApiResponse(await context.request.get('/bff/login', { maxRedirects: 0 }), 409);

    stage = 'reject logout without CSRF or with a foreign Origin';
    const token = await csrfToken(context);
    assert.ok(!(await page.evaluate(() => document.cookie)).includes('assembleai-csrf='));
    assert.equal(await browserLogout(page), 403);
    await assertSession(context, config);
    await inspectApiResponse(await context.request.post('/bff/logout', {
      headers: { Origin: 'https://foreign-origin.example', 'X-CSRF-TOKEN': token },
      maxRedirects: 0
    }), 403);
    await assertSession(context, config);

    stage = 'revoke the local session with browser Origin and CSRF';
    assert.equal(await browserLogout(page, token), 204);
    await assertSession(context, config, 401);
    const replay = await browser.newContext({ baseURL: config.platformOrigin });
    contexts.push(replay);
    await replay.addCookies([oldCookie]);
    await assertSession(replay, config, 401);

    stage = 'preserve Keycloak SSO while issuing a new local session';
    // The real realm requires S256, including when .NET uses PAR. An accepted
    // code exchange exercises that constraint without assuming URL parameters.
    await page.goto('/bff/login?returnUrl=/bff/session');
    await page.waitForURL(url => url.origin === config.platformOrigin && url.pathname === '/bff/session',
      { waitUntil: 'domcontentloaded' });
    await assertSession(context, config);
    const newCookie = (await context.cookies(config.platformOrigin)).find(cookie => cookie.name === 'assembleai-session');
    assert.ok(newCookie && newCookie.value !== oldCookie.value);
    await assertSession(replay, config, 401);
    assert.equal(await browserLogout(page, await csrfToken(context)), 204);
    await assertSession(context, config, 401);
    const fresh = await browser.newContext({ baseURL: config.platformOrigin });
    contexts.push(fresh);
    await assertSession(fresh, config, 401);

    stage = 'honor explicit prompt=login despite the remembered Keycloak SSO session';
    await page.goto('/bff/login?returnUrl=/bff/session&prompt=login');
    await page.screenshot({ path: path.join(config.artifactDirectory, 'keycloak-forced-login-response.png'), fullPage: true });
    assert.equal(new URL(page.url()).origin, new URL(config.authority).origin);
    stage = 'show the reference password field during Keycloak reauthentication';
    await page.locator('#loginPassword').waitFor({ state: 'visible' });
    await assertReferenceTheme(page, config.artifactDirectory, 'keycloak-forced-login.png');
    stage = 'submit credentials during explicit Keycloak reauthentication';
    // Keycloak may hide the already established username during reauthentication.
    if (await page.locator('#loginAccount').count()) await page.locator('#loginAccount').fill(config.username);
    await page.locator('#loginPassword').fill(config.password);
    await Promise.all([
      page.waitForURL(url => url.origin === config.platformOrigin && url.pathname === '/bff/session', { waitUntil: 'domcontentloaded' }),
      page.locator('#loginSubmit').click()
    ]);
    await assertSession(context, config);
    assert.equal(await browserLogout(page, await csrfToken(context)), 204);

    stage = 'open the themed real Keycloak password reset';
    const resetContext = await browser.newContext({ baseURL: config.platformOrigin, viewport: { width: 1366, height: 900 }, colorScheme: 'light' });
    contexts.push(resetContext);
    const resetPage = await resetContext.newPage();
    await resetPage.goto('/bff/login?returnUrl=/bff/session');
    await Promise.all([resetPage.waitForNavigation({ waitUntil: 'domcontentloaded' }), resetPage.locator('#loginForgot').click()]);
    assert.equal(await resetPage.locator('.login-form-title').innerText(), '找回密码');
    assert.equal(await resetPage.locator('input[type="password"]').count(), 0);
    await assertReferenceTheme(resetPage, config.artifactDirectory, 'keycloak-reset-request.png');

    stage = 'deliver the real Keycloak reset email to the private SMTP sink';
    await resetPage.locator('#loginAccount').fill(config.username);
    await Promise.all([resetPage.waitForNavigation({ waitUntil: 'domcontentloaded' }), resetPage.locator('#loginSubmit').click()]);
    await resetPage.screenshot({ path: path.join(config.artifactDirectory, 'keycloak-reset-email-response.png'), fullPage: true });
    // Keycloak 26 forks back to the real login flow with emailSentMessage.
    // Delivery is proven by the private SMTP capture, not by that generic UI
    // response (which intentionally also covers unknown accounts).
    assert.equal(await resetPage.locator('.login-form-title').innerText(), '登录工作台');
    await resetPage.locator('#loginFormError').waitFor({ state: 'visible' });
    await assertReferenceTheme(resetPage, config.artifactDirectory, 'keycloak-reset-email-sent.png');

    stage = 'open the real emailed password-reset required action';
    await resetPage.goto(await resetMailUrl(config));
    await resetPage.locator('[name="password-new"]').waitFor({ state: 'visible' });
    await resetPage.locator('[name="password-confirm"]').waitFor({ state: 'visible' });
    await assertReferenceTheme(resetPage, config.artifactDirectory, 'keycloak-update-password.png');

    stage = 'reject mismatched passwords in the real required action';
    const replacementPassword = `${config.password}-updated`;
    await resetPage.locator('[name="password-new"]').fill(replacementPassword);
    await resetPage.locator('[name="password-confirm"]').fill(`${replacementPassword}-mismatch`);
    await Promise.all([resetPage.waitForNavigation({ waitUntil: 'domcontentloaded' }), resetPage.locator('#loginSubmit').click()]);
    await resetPage.locator('#loginFormError').waitFor({ state: 'visible' });
    await assertReferenceTheme(resetPage, config.artifactDirectory, 'keycloak-update-password-error.png');

    stage = 'complete the real password reset and authorization-code callback';
    await resetPage.locator('[name="password-new"]').fill(replacementPassword);
    await resetPage.locator('[name="password-confirm"]').fill(replacementPassword);
    await Promise.all([
      resetPage.waitForURL(url => url.origin === config.platformOrigin && url.pathname === '/bff/session', { waitUntil: 'domcontentloaded' }),
      resetPage.locator('#loginSubmit').click()
    ]);
    await assertSession(resetContext, config);
    assert.equal(await browserLogout(resetPage, await csrfToken(resetContext)), 204);

    stage = 'verify the new password works in a fresh Keycloak session';
    const changedContext = await browser.newContext({ baseURL: config.platformOrigin });
    contexts.push(changedContext);
    const changedPage = await changedContext.newPage();
    await changedPage.goto('/bff/login?returnUrl=/bff/session');
    await changedPage.locator('#loginAccount').fill(config.username);
    await changedPage.locator('#loginPassword').fill(replacementPassword);
    await Promise.all([
      changedPage.waitForURL(url => url.origin === config.platformOrigin && url.pathname === '/bff/session', { waitUntil: 'domcontentloaded' }),
      changedPage.locator('#loginSubmit').click()
    ]);
    await assertSession(changedContext, config);
    assert.equal(await browserLogout(changedPage, await csrfToken(changedContext)), 204);

    stage = 'render a genuine identity-provider protocol error in the reference shell';
    const invalidCallback = new URL(config.authority + '/protocol/openid-connect/auth');
    invalidCallback.search = new URLSearchParams({ client_id: 'assembleai-platform', response_type: 'code', scope: 'openid', redirect_uri: config.platformOrigin + '/unregistered-callback' }).toString();
    await resetPage.goto(invalidCallback.href);
    assert.equal(await resetPage.locator('.login-form-title').innerText(), '无法完成登录');
    await resetPage.locator('#loginFormError').waitFor({ state: 'visible' });
    await assertReferenceTheme(resetPage, config.artifactDirectory, 'keycloak-protocol-error.png');

    stage = 'verify browser-visible platform responses contain no OIDC tokens';
    for (let index = 0; index < responseChecks.length; index++) await responseChecks[index];
    if (responseFailures.length > 0) {
      const counts = {};
      for (const failure of responseFailures) counts[failure.check] = (counts[failure.check] ?? 0) + 1;
      console.error(`FAIL: platform response checks ${JSON.stringify({ counts, responses: responseFailures })}`);
    }
    if (browserCalledTokenEndpoint) console.error('FAIL: browser called the OIDC token endpoint.');
    assert.equal(responseFailures.length, 0);
    assert.equal(browserCalledTokenEndpoint, false);
  } catch {
    // Playwright errors can embed form values, headers or callback query strings.
    // Export only our fixed stage label, without the underlying error or stack.
    throw new Error(`FAIL: Keycloak browser verification could not ${stage}.`);
  } finally {
    for (const context of contexts.reverse()) await context.close().catch(() => {});
    await browser?.close().catch(() => {});
  }
  console.log('PASS: real Keycloak reference login/error/reset/required-action shell, remember-me Cookie, prompt=login, SMTP password reset, new-password login, HttpOnly session, token isolation, CSRF/Origin, local logout, revoked Cookie replay and SSO re-entry.');
}

await run().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
