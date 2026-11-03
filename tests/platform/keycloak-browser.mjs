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
  return config;
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
    const context = await browser.newContext({ baseURL: config.platformOrigin });
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
    await page.locator('#username').waitFor({ state: 'visible' });
    await page.locator('#password').waitFor({ state: 'visible' });
    await mkdir(config.artifactDirectory, { recursive: true });
    await page.screenshot({ path: path.join(config.artifactDirectory, 'keycloak-login.png'), fullPage: true });

    stage = 'reject an incorrect Keycloak password';
    await page.locator('#username').fill(config.username);
    await page.locator('#password').fill(`${config.password}-incorrect`);
    await Promise.all([
      page.waitForNavigation({ waitUntil: 'domcontentloaded' }),
      page.locator('#kc-login').click()
    ]);
    assert.equal(new URL(page.url()).origin, new URL(config.authority).origin);
    await page.locator('#password').waitFor({ state: 'visible' });
    await assertSession(context, config, 401);

    stage = 'complete real Keycloak authorization-code login';
    await page.locator('#username').fill(config.username);
    await page.locator('#password').fill(config.password);
    await Promise.all([
      page.waitForURL(url => url.origin === config.platformOrigin && url.pathname === '/bff/session',
        { waitUntil: 'domcontentloaded' }),
      page.locator('#kc-login').click()
    ]);
    await assertSession(context, config);

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
  console.log('PASS: real Keycloak browser login, password rejection, HttpOnly session, token isolation, CSRF/Origin, local logout, revoked Cookie replay and SSO re-entry.');
}

await run().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
