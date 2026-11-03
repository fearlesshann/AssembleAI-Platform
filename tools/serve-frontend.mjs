import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { resolve, extname, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createPlatformProxy } from './local-platform-target.mjs';

const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.png': 'image/png', '.svg': 'image/svg+xml', '.ico': 'image/x-icon' };

/** Local-only production-build preview. Never exposes workspace source files. */
export function previewServer({ portalRoot, sampleRoot, platformOrigin }) {
  const proxyPlatform = createPlatformProxy(platformOrigin);
  const server = createServer(async (req, res) => {
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('Cache-Control', 'no-store');
    if (proxyPlatform(req, res)) return;
    if (req.method !== 'GET' && req.method !== 'HEAD') {
      res.writeHead(405, { Allow: 'GET, HEAD' }); res.end(); return;
    }
    let pathname;
    try { pathname = decodeURIComponent((req.url ?? '/').split('?')[0]); }
    catch { res.writeHead(400); res.end('Malformed URL'); return; }
    if (pathname.includes('\\') || pathname.includes('\0') || pathname.split('/').includes('..')) {
      res.writeHead(400); res.end('Invalid path'); return;
    }
    const prefix = '/apps/sample/0.1.0/';
    const isSample = pathname.startsWith(prefix);
    if (pathname.startsWith('/apps/') && !isSample) { res.writeHead(404); res.end('Unknown release'); return; }
    const root = resolve(isSample ? sampleRoot : portalRoot);
    const relative = isSample ? pathname.slice(prefix.length) : pathname.slice(1);
    let filename = resolve(root, relative || 'index.html');
    if (!filename.startsWith(root + sep)) { res.writeHead(403); res.end(); return; }
    try {
      if ((await stat(filename)).isDirectory()) filename = resolve(filename, 'index.html');
    } catch {
      // Only known application routes have an HTML fallback; missing assets are 404.
      if (!extname(relative) && (isSample || pathname === '/' || pathname.startsWith('/workspace/')
        || /^\/applications\/[a-z][a-z0-9-]{0,63}(?:\/run)?\/?$/.test(pathname)
        || pathname === '/reference-ui' || pathname.startsWith('/reference-ui/'))) filename = resolve(root, 'index.html');
    }
    try {
      const content = await readFile(filename);
      res.writeHead(200, { 'Content-Type': types[extname(filename)] ?? 'application/octet-stream' });
      res.end(req.method === 'HEAD' ? undefined : content);
    } catch { res.writeHead(404); res.end('Build output not found. Run npm run build.'); }
  });
  server.on('upgrade', (_req, socket) => socket.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n'));
  server.on('connect', (_req, socket) => socket.end('HTTP/1.1 405 Method Not Allowed\r\nConnection: close\r\nContent-Length: 0\r\n\r\n'));
  return server;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const server = previewServer({ portalRoot: 'apps/portal/dist', sampleRoot: 'apps/sample/frontend/dist',
    platformOrigin: process.env.ASSEMBLEAI_PLATFORM_ORIGIN });
  const port = Number(process.env.PORT ?? 4173);
  server.listen(port, '127.0.0.1', () => process.stdout.write(`AssembleAI preview: http://127.0.0.1:${port}\n`));
}
