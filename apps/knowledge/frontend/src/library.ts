import { UUID, fields, invalid, object, positive, text } from './protocol.ts';
export type DocumentStatus = 'queued' | 'processing' | 'extracted' | 'indexing' | 'ready' | 'failed';
export interface KnowledgeDocument {
  tenantId: string; documentId: string; fileName: string; mediaType: string; sizeBytes: number; sha256: string;
  organizationId: string | null; status: DocumentStatus; revision: number; createdBy: string; createdAt: string;
  failureCode: string | null; indexProfile?: string | null;
}
interface Grant { permission: string; resourceType: string; resourceId: string; tenantWide: boolean; organizationIds: string[] }
export interface LibraryAccess { uploadScopes: (string | null)[]; canManage(document: KnowledgeDocument): boolean }
export class LibraryError extends Error {
  readonly status: number; readonly code: string;
  constructor(status: number, code = 'knowledge_unavailable') { super('Knowledge library request failed.'); this.status = status; this.code = code; }
}
function document(value: unknown, tenant: string): KnowledgeDocument {
  const names = ['tenantId', 'documentId', 'fileName', 'mediaType', 'sizeBytes', 'sha256', 'organizationId', 'status', 'revision', 'createdBy', 'createdAt', 'failureCode'];
  if (object(value) && Object.hasOwn(value, 'indexProfile')) names.push('indexProfile');
  if (!fields(value, names) || value.tenantId !== tenant || typeof value.documentId !== 'string' || !UUID.test(value.documentId)
    || !text(value.fileName, 200) || !text(value.mediaType, 200) || !positive(value.sizeBytes) || value.sizeBytes > 5 * 1024 * 1024
    || typeof value.sha256 !== 'string' || !/^[a-f0-9]{64}$/.test(value.sha256)
    || value.organizationId !== null && (typeof value.organizationId !== 'string' || !UUID.test(value.organizationId))
    || !['queued', 'processing', 'extracted', 'indexing', 'ready', 'failed'].includes(value.status as string) || !positive(value.revision)
    || typeof value.createdBy !== 'string' || !UUID.test(value.createdBy) || typeof value.createdAt !== 'string' || !Number.isFinite(Date.parse(value.createdAt))
    || value.failureCode !== null && !text(value.failureCode, 100)
    || value.indexProfile !== undefined && value.indexProfile !== null && !text(value.indexProfile, 100)) return invalid();
  return { ...value } as unknown as KnowledgeDocument;
}
export function parseDocuments(value: unknown, tenant: string): KnowledgeDocument[] {
  if (!fields(value, ['items', 'limit']) || value.limit !== 100 || !Array.isArray(value.items) || value.items.length > 100) return invalid();
  const items = value.items.map(item => document(item, tenant));
  if (new Set(items.map(item => item.documentId)).size !== items.length) return invalid();
  return items;
}
/** UI hints only. Every read/write is independently authorized by the BFF. */
export function parseAccess(value: unknown, tenant: string): LibraryAccess {
  if (!fields(value, ['userId', 'tenantId', 'authorizationVersion', 'grants']) || value.tenantId !== tenant
    || typeof value.userId !== 'string' || !UUID.test(value.userId) || typeof value.authorizationVersion !== 'number' || value.authorizationVersion < 1
    || !Array.isArray(value.grants) || value.grants.length > 10000) return invalid();
  const grants: Grant[] = value.grants.map(grant => {
    if (!fields(grant, ['permission', 'resourceType', 'resourceId', 'tenantWide', 'organizationIds'])
      || !text(grant.permission, 128) || !text(grant.resourceType, 100) || !text(grant.resourceId, 256) || typeof grant.tenantWide !== 'boolean'
      || !Array.isArray(grant.organizationIds) || grant.organizationIds.length > 10000 || grant.organizationIds.some(id => typeof id !== 'string' || !UUID.test(id))) return invalid();
    return { ...grant, organizationIds: [...grant.organizationIds] } as Grant;
  });
  const manage = grants.filter(g => g.permission === 'knowledge:manage');
  const collection = manage.filter(g => g.resourceType === 'knowledge' && g.resourceId === 'documents');
  const uploadScopes = [...new Set(collection.flatMap(g => g.organizationIds))] as (string | null)[];
  if (collection.some(g => g.tenantWide)) uploadScopes.unshift(null);
  return { uploadScopes, canManage: doc => doc.tenantId === tenant && manage.some(g =>
    (g.resourceType === 'knowledge' && g.resourceId === 'documents' || g.resourceType === 'document' && g.resourceId === doc.documentId.replaceAll('-', ''))
    && (g.tenantWide || doc.organizationId !== null && g.organizationIds.includes(doc.organizationId))) };
}
export function createKnowledgeLibrary(fetcher: typeof fetch = globalThis.fetch.bind(globalThis)) {
  const root = (tenant: string) => { if (!UUID.test(tenant)) throw new LibraryError(400); return `/api/platform/v1/tenants/${tenant}/knowledge/documents`; };
  async function request(path: string, signal: AbortSignal, init: RequestInit = {}, download = false): Promise<unknown> {
    const controller = new AbortController(); const cancel = () => controller.abort(signal.reason);
    signal.addEventListener('abort', cancel, { once: true }); if (signal.aborted) cancel();
    const timer = setTimeout(() => controller.abort(), 35_000);
    try {
      controller.signal.throwIfAborted();
      const response = await fetcher(path, { ...init, credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: controller.signal,
        headers: { Accept: download ? 'application/octet-stream' : 'application/json', ...init.headers } });
      if (!response.ok) {
        let code = 'knowledge_unavailable';
        try { const failure = await response.json(); if (typeof failure?.code === 'string' && /^[a-z_]{1,80}$/.test(failure.code)) code = failure.code; } catch { /* No server error detail in UI. */ }
        throw new LibraryError(response.status, code);
      }
      if (response.status === 204) return undefined;
      const media = response.headers.get('content-type')?.split(';', 1)[0].toLowerCase().trim();
      if (media !== (download ? 'application/octet-stream' : 'application/json') || response.headers.get('content-encoding')) throw new LibraryError(502, 'invalid_response');
      const maximum = download ? 5 * 1024 * 1024 : 1024 * 1024;
      if (Number(response.headers.get('content-length') ?? '0') > maximum || !response.body) throw new LibraryError(502, 'invalid_response');
      const reader = response.body.getReader(); const chunks: Uint8Array[] = []; let length = 0;
      try {
        for (;;) { const part = await reader.read(); if (part.done) break; length += part.value.byteLength;
          if (length > maximum) throw new LibraryError(502, 'invalid_response'); chunks.push(part.value); }
      } finally { await reader.cancel().catch(() => {}); reader.releaseLock(); }
      const bytes = new Uint8Array(length); let offset = 0; for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
      return download ? new Blob([bytes], { type: 'application/octet-stream' }) : JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
    } catch (error) {
      if (signal.aborted) throw signal.reason;
      if (error instanceof LibraryError) throw error;
      throw new LibraryError(controller.signal.aborted ? 504 : 503);
    } finally { clearTimeout(timer); signal.removeEventListener('abort', cancel); controller.abort(); }
  }
  async function csrf(signal: AbortSignal): Promise<string> {
    const value = await request('/bff/csrf', signal);
    if (!object(value) || value.headerName !== 'X-CSRF-TOKEN' || !text(value.requestToken, 8192)) throw new LibraryError(502, 'invalid_response');
    return value.requestToken;
  }
  return {
    async list(tenant: string, signal: AbortSignal) { return parseDocuments(await request(root(tenant), signal), tenant); },
    async access(tenant: string, signal: AbortSignal) { root(tenant); return parseAccess(await request(`/api/platform/v1/tenants/${tenant}/access`, signal), tenant); },
    async upload(tenant: string, file: File, organization: string | null, signal: AbortSignal) {
      const path = root(tenant);
      if (!file.size || file.size > 5 * 1024 * 1024 || !text(file.name, 200) || /[\/\\\u0000-\u001f]/.test(file.name)
        || !/\.(pdf|docx|txt|md)$/i.test(file.name) || organization !== null && !UUID.test(organization)) throw new LibraryError(400, 'invalid_file');
      const key = await csrf(signal);
      return document(await request(`${path}?filename=${encodeURIComponent(file.name)}${organization === null ? '' : `&organizationId=${organization}`}`, signal,
        { method: 'POST', headers: { 'Content-Type': 'application/octet-stream', 'X-CSRF-TOKEN': key }, body: file }), tenant);
    },
    async remove(tenant: string, item: KnowledgeDocument, signal: AbortSignal) {
      if (item.tenantId !== tenant || !UUID.test(item.documentId) || !positive(item.revision)) throw new LibraryError(400);
      const path = `${root(tenant)}/${item.documentId}`; const key = await csrf(signal);
      await request(path, signal, { method: 'DELETE', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': key }, body: JSON.stringify({ expectedRevision: item.revision }) });
    },
    async download(tenant: string, documentId: string, signal: AbortSignal): Promise<Blob> {
      if (!UUID.test(documentId)) throw new LibraryError(400);
      return await request(`${root(tenant)}/${documentId}/content`, signal, {}, true) as Blob;
    },
  };
}
