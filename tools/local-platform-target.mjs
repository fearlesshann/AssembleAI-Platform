import { request } from 'node:http';

// Local development only. Validate the original text too: URL normalization must
// not turn alternative numeric hosts or escaped names into allowed loopback hosts.
export function localPlatformTarget(value) {
  if (value === undefined || value === '') return undefined;
  if (typeof value !== 'string' || !/^http:\/\/(localhost|127(?:\.(?:0|[1-9][0-9]{0,2})){3}|\[::1\])(?::([1-9][0-9]{0,4}))?\/?$/i.test(value))
    throw new Error('Invalid local platform origin. Use a plain HTTP loopback origin.');
  const target = new URL(value);
  if ((target.port && Number(target.port) > 65535)
    || (target.hostname.startsWith('127.') && target.hostname.split('.').some((part) => Number(part) > 255)))
    throw new Error('Invalid local platform origin. Use a plain HTTP loopback origin.');
  return target;
}

export function isPlatformRequest(path) {
  const pathname = (path ?? '').split('?')[0];
  return pathname === '/bff' || pathname.startsWith('/bff/')
    || pathname === '/api' || pathname.startsWith('/api/') || pathname === '/signin-oidc';
}

const hopHeaders = new Set(['connection', 'keep-alive', 'proxy-authenticate', 'proxy-authorization',
  'te', 'trailer', 'transfer-encoding', 'upgrade', 'expect']);

function safeHeaders(headers, outgoing) {
  const removed = new Set([...hopHeaders, ...(headers.connection ?? '').split(',').map((value) => value.trim().toLowerCase())]);
  return Object.fromEntries(Object.entries(headers).filter(([name]) => !removed.has(name)
    && (!outgoing || name !== 'forwarded' && !name.startsWith('x-forwarded-'))));
}

function failure(res, status, code) {
  if (res.destroyed || res.writableEnded) return;
  if (res.headersSent) { res.destroy(); return; }
  res.writeHead(status, { 'Content-Type': 'application/problem+json; charset=utf-8',
    'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
  res.end(JSON.stringify({ status, title: 'The local platform connection is unavailable.', code }));
}

/** Fixed-target, streaming middleware shared by Vite and built-output preview.
 * Returns true only for handled BFF/API paths. No request data selects the target.
 * These limits exceed the gateway's supported deadlines; this is not a deployment proxy.
 */
export function createPlatformProxy(platformOrigin) {
  let target;
  try { target = localPlatformTarget(platformOrigin); }
  catch { throw new Error('Invalid local platform origin. Use a plain HTTP loopback origin.'); }
  return (req, res) => {
    if (!isPlatformRequest(req.url)) return false;
    if (!target) { failure(res, 503, 'platform_not_configured'); req.resume(); return true; }
    if (req.headers.upgrade || req.method === 'CONNECT') {
      failure(res, 400, 'upgrade_not_supported'); req.resume(); return true;
    }
    const headers = safeHeaders(req.headers, true);
    headers.host = target.host;
    // Resolve localhost to a literal so a changed hosts/DNS entry cannot route outside loopback.
    const hostname = target.hostname === 'localhost' ? '127.0.0.1' : target.hostname.replace(/^\[|\]$/g, '');
    const upstream = request({ protocol: 'http:', hostname, port: target.port || 80,
      method: req.method, path: req.url, headers, agent: false });
    let response;
    let finished = false;
    let headerTimer;
    let connectTimer;
    const totalTimer = setTimeout(() => fail(), 660_000);
    const uploadTimer = setTimeout(() => fail(), 30_000);
    function cleanTimers() {
      clearTimeout(totalTimer); clearTimeout(uploadTimer); clearTimeout(headerTimer); clearTimeout(connectTimer);
    }
    function fail() {
      if (finished) return;
      finished = true; cleanTimers();
      req.unpipe(upstream); req.resume();
      upstream.destroy(); response?.destroy();
      failure(res, 502, 'platform_unavailable');
    }
    upstream.on('socket', (socket) => {
      connectTimer = setTimeout(fail, 3_000);
      socket.once('connect', () => clearTimeout(connectTimer));
    });
    upstream.on('finish', () => {
      clearTimeout(uploadTimer);
      // Covers auth/registry setup as well as the gateway's configurable first-header budget.
      if (!response && !finished) headerTimer = setTimeout(fail, 90_000);
    });
    upstream.setTimeout(130_000, fail);
    upstream.on('error', fail);
    upstream.on('upgrade', (_message, socket) => { socket.destroy(); fail(); });
    upstream.on('response', (incoming) => {
      response = incoming;
      clearTimeout(headerTimer);
      if (finished || res.destroyed) { incoming.destroy(); return; }
      const responseHeaders = safeHeaders(incoming.headers, false);
      responseHeaders['cache-control'] = 'no-store';
      responseHeaders['x-content-type-options'] = 'nosniff';
      res.writeHead(incoming.statusCode ?? 502, responseHeaders);
      res.flushHeaders();
      incoming.on('error', fail);
      incoming.on('aborted', fail);
      incoming.on('end', () => { finished = true; cleanTimers(); });
      incoming.pipe(res);
    });
    req.on('aborted', fail);
    req.on('error', fail);
    res.on('error', fail);
    res.on('close', () => {
      cleanTimers();
      if (!finished) { finished = true; upstream.destroy(); response?.destroy(); }
    });
    req.pipe(upstream);
    return true;
  };
}

export function rejectPlatformUpgrade(req, socket) {
  if (!isPlatformRequest(req.url)) return false;
  socket.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n');
  return true;
}
