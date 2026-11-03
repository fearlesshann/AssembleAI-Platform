import { createHash } from 'node:crypto';
import { closeSync, existsSync, fstatSync, lstatSync, openSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const limits = Object.freeze({ artifacts: 128, files: 2000, fileBytes: 16 * 1024 * 1024,
  artifactBytes: 64 * 1024 * 1024, totalBytes: 256 * 1024 * 1024, configBytes: 1024 * 1024, depth: 16 });
const types = Object.freeze({ '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.json': 'application/json; charset=utf-8', '.png': 'image/png',
  '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.webp': 'image/webp', '.gif': 'image/gif', '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon', '.woff': 'font/woff', '.woff2': 'font/woff2', '.ttf': 'font/ttf', '.eot': 'application/vnd.ms-fontobject',
  '.txt': 'text/plain; charset=utf-8', '.wasm': 'application/wasm' });
const forbiddenNames = new Set(['src', 'source', 'sources', 'node_modules', 'test', 'tests', 'coverage',
  'package.json', 'package-lock.json', 'npm-shrinkwrap.json', 'tsconfig.json', 'jsconfig.json']);
const slug = /^[a-z][a-z0-9-]{0,63}$/;
const versionPattern = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
const fail = reason => { throw new Error(`Frontend artifact ${reason}`); };
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

export function frontendArtifactEntry(appId, version) {
  if (typeof appId !== 'string' || !slug.test(appId)) fail('appId must be a lowercase slug');
  if (typeof version !== 'string' || version.length > 64 || !versionPattern.test(version)) fail('version must be stable x.y.z');
  return `/apps/${appId}/${version}/`;
}

function strictObject(value, keys) {
  if (!value || typeof value !== 'object' || Array.isArray(value)
    || ![Object.prototype, null].includes(Object.getPrototypeOf(value))
    || Object.keys(value).length !== keys.length || keys.some(key => !Object.hasOwn(value, key))) fail('configuration has unexpected fields');
}

function portableName(name) {
  return name.length <= 128 && /^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(name) && !name.endsWith('.')
    && !/^(?:con|prn|aux|nul|com[0-9]|lpt[0-9])(?:\.|$)/i.test(name) && !forbiddenNames.has(name.toLowerCase());
}

function checkDirectoryAncestors(root) {
  let current = root;
  while (true) {
    const info = lstatSync(current);
    if (info.isSymbolicLink() || !info.isDirectory()) fail('root must contain only real directories');
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }
}

function scan(root) {
  if (typeof root !== 'string' || !root.trim() || root.includes('\0')) fail('root must be a local directory');
  const absolute = resolve(root);
  const files = new Map();
  const names = new Set();
  let totalBytes = 0;
  let entries = 0;
  try {
    checkDirectoryAncestors(absolute);
    const walk = (directory, relative, depth) => {
      if (depth > limits.depth) fail('directory nesting exceeds the limit');
      for (const name of readdirSync(directory)) {
        if (++entries > limits.files || !portableName(name)) fail('contains forbidden paths or too many files');
        const path = relative ? `${relative}/${name}` : name;
        if (path.length > 512 || names.has(path.toLowerCase())) fail('contains ambiguous paths');
        names.add(path.toLowerCase());
        const filename = join(directory, name);
        const info = lstatSync(filename);
        if (info.isSymbolicLink()) fail('symbolic links and junctions are forbidden');
        if (info.isDirectory()) { walk(filename, path, depth + 1); continue; }
        if (!info.isFile() || !Object.hasOwn(types, extname(name))) fail('contains non-public build files');
        if (info.size > limits.fileBytes || totalBytes + info.size > limits.artifactBytes) fail('size exceeds the limit');
        const descriptor = openSync(filename, 'r');
        let bytes;
        try {
          const opened = fstatSync(descriptor);
          if (!opened.isFile() || opened.ino !== info.ino || opened.dev !== info.dev || opened.size !== info.size)
            fail('changed while being validated');
          bytes = readFileSync(descriptor);
          const after = fstatSync(descriptor);
          if (after.size !== info.size || after.mtimeMs !== info.mtimeMs || bytes.length !== info.size)
            fail('changed while being validated');
        } finally { closeSync(descriptor); }
        totalBytes += bytes.length;
        files.set(path, bytes);
      }
    };
    walk(absolute, '', 0);
  } catch (error) {
    if (error instanceof Error && error.message.startsWith('Frontend artifact ')) throw error;
    fail('directory could not be validated');
  }
  if (!files.has('index.html')) fail('requires index.html');
  // v1: ordinal-sorted '/' paths; UTF-8 domain + compact JSON [{path,size,sha256}].
  // Limits: 128 releases, 2000 entries/release, 16 MiB/file, 64 MiB/release, 256 MiB/config.
  const records = [...files.keys()].sort().map(path => ({ path, size: files.get(path).length, sha256: sha256(files.get(path)) }));
  const digest = sha256('assembleai-frontend-artifact-v1\n' + JSON.stringify(records));
  return { files, sha256: digest, fileCount: files.size, totalBytes };
}

export function frontendArtifactDigest(root) {
  const { sha256, fileCount, totalBytes } = scan(root);
  return Object.freeze({ sha256, fileCount, totalBytes });
}

function snapshot(releases) {
  const byEntry = new Map(releases.map(release => [release.entry, release]));
  const artifacts = Object.freeze(releases.map(({ files: _files, ...metadata }) => Object.freeze(metadata)));
  return Object.freeze({ artifacts, handle(req, res) {
    const raw = (req.url ?? '/').split('?')[0];
    // Capture encoded /apps aliases too, so a downstream development server cannot decode them.
    let decoded;
    try { decoded = decodeURIComponent(raw); } catch { decoded = raw; }
    if (!/^\/apps(?:\/|$|%|\\)/i.test(raw) && !/^\/apps(?:\/|$)/i.test(decoded)) return false;
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('Cache-Control', 'no-store');
    const end = (status, text) => { res.writeHead(status); res.end(req.method === 'HEAD' ? undefined : text); return true; };
    if (req.method !== 'GET' && req.method !== 'HEAD') { res.setHeader('Allow', 'GET, HEAD'); return end(405); }
    if (raw.includes('%') || raw.includes('\\') || /[\u0000-\u0020\u007f:#]/.test(raw)) return end(400, 'Invalid artifact path');
    const parts = raw.split('/');
    if (parts.some(part => part === '.' || part === '..')) return end(400, 'Invalid artifact path');
    const entry = parts.length >= 5 ? `/apps/${parts[2]}/${parts[3]}/` : '';
    const release = byEntry.get(entry);
    if (!release) return end(404, 'Unknown release');
    const relative = parts.slice(4).join('/');
    const segments = relative.replace(/\/$/, '').split('/');
    if (relative && segments.some(part => !portableName(part))) return end(404, 'Artifact not found');
    let key = relative || 'index.html';
    if (relative.endsWith('/')) key += 'index.html';
    if (!release.files.has(key) && !extname(relative)) key = 'index.html';
    const bytes = release.files.get(key);
    if (!bytes) return end(404, 'Artifact not found');
    res.writeHead(200, { 'Content-Type': types[extname(key)], 'Content-Length': bytes.length });
    res.end(req.method === 'HEAD' ? undefined : bytes);
    return true;
  } });
}

export function createFrontendArtifacts(configuration, { baseDirectory = process.cwd() } = {}) {
  strictObject(configuration, ['artifacts']);
  if (!Array.isArray(configuration.artifacts) || configuration.artifacts.length > limits.artifacts) fail('configuration has too many releases');
  const entries = new Set();
  let totalBytes = 0;
  const releases = configuration.artifacts.map(artifact => {
    strictObject(artifact, ['appId', 'version', 'root', 'sha256']);
    const entry = frontendArtifactEntry(artifact.appId, artifact.version);
    if (entries.has(entry)) fail('configuration has duplicate releases');
    entries.add(entry);
    if (typeof artifact.root !== 'string' || !artifact.root.trim() || artifact.root.includes('\0')) fail('root must be a local directory');
    if (typeof artifact.sha256 !== 'string' || !/^[a-f0-9]{64}$/.test(artifact.sha256)) fail('sha256 must be lowercase hex');
    const content = scan(resolve(baseDirectory, artifact.root));
    if (content.sha256 !== artifact.sha256) fail('sha256 does not match the release bytes');
    totalBytes += content.totalBytes;
    if (totalBytes > limits.totalBytes) fail('configuration size exceeds the limit');
    return { appId: artifact.appId, version: artifact.version, entry, ...content };
  });
  return snapshot(releases);
}

export function loadFrontendArtifacts(configurationPath) {
  if (typeof configurationPath !== 'string' || !configurationPath.trim() || configurationPath.includes('\0')) fail('configuration path is invalid');
  const absolute = resolve(configurationPath);
  let configuration;
  try {
    const info = lstatSync(absolute);
    if (info.isSymbolicLink() || !info.isFile() || info.size > limits.configBytes) fail('configuration file is invalid');
    // Windows PowerShell 5 writes UTF-8 BOM; only the initial marker is optional.
    configuration = JSON.parse(readFileSync(absolute, 'utf8').replace(/^\uFEFF/, ''));
  } catch (error) {
    if (error instanceof Error && error.message.startsWith('Frontend artifact ')) throw error;
    fail('configuration file could not be read');
  }
  return createFrontendArtifacts(configuration, { baseDirectory: dirname(absolute) });
}

/** Unconfigured local PoC compatibility only. Deployment uses loadFrontendArtifacts. */
export function developmentFrontendArtifacts(sampleRoot) {
  if (!sampleRoot || !existsSync(sampleRoot)) return snapshot([]);
  return snapshot([{ appId: 'sample', version: '0.1.0', entry: '/apps/sample/0.1.0/', ...scan(sampleRoot) }]);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    const [appId, version, directory, ...extra] = process.argv.slice(2);
    if (extra.length || !directory) fail('usage: node tools/frontend-artifacts.mjs <appId> <version> <root>');
    const entry = frontendArtifactEntry(appId, version);
    const root = resolve(directory);
    const { sha256 } = frontendArtifactDigest(root);
    process.stdout.write(JSON.stringify({ appId, version, root, sha256, registry: { appId, version, entry, sha256 } }, null, 2) + '\n');
  } catch (error) {
    process.stderr.write((error instanceof Error ? error.message : 'Frontend artifact validation failed') + '\n');
    process.exitCode = 1;
  }
}
