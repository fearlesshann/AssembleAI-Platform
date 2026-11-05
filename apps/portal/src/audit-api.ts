export type AuditScope = 'tenant' | 'global';
export type AuditStatus = 'success' | 'failure' | 'unfinished';
export const auditSources = ['gateway', 'model', 'connector', 'knowledge-search', 'registry', 'identity', 'model-configuration', 'connector-configuration', 'knowledge-document', 'audit-export'] as const;
export type AuditSource = typeof auditSources[number];
export interface AuditFilters { scope: AuditScope; from: string; to: string; source?: AuditSource; status?: AuditStatus; appId?: string; requestId?: string; pageSize?: number }
export interface AuditEvent { id: string; source: AuditSource; tenantId: string | null; actorUserId: string; resourceType: string; resourceId: string; action: string; outcome: string; status: AuditStatus; requestId: string; occurredAt: string; appId: string | null; releaseVersion: string | null; runId: string | null; gatewayRequestId: string | null; details: Record<string, string | null> }
export interface AuditWorkspace { scope: AuditScope; tenantId: string | null; actorUserId: string; sources: AuditSource[]; canExport: boolean; globalAvailable: boolean; policy: { maximumRangeDays: number; defaultPageSize: number; maximumPageSize: number; maximumExportRecords: number; automaticRetention: boolean } }
export interface AuditPage { items: AuditEvent[]; nextCursor: string | null }
export interface AuditExport { items: AuditEvent[]; truncated: boolean; exportedAt: string }
export class AuditApiError extends Error { readonly status: number; constructor(status: number) { super('Audit request could not complete.'); this.status = status; } }
export function auditQuery(filters: AuditFilters, cursor?: string | null): string {
  const query = new URLSearchParams({ scope: filters.scope, from: filters.from, to: filters.to });
  for (const key of ['source', 'status', 'appId', 'requestId'] as const) if (filters[key]) query.set(key, filters[key]!);
  if (filters.pageSize) query.set('pageSize', String(filters.pageSize));
  if (cursor) query.set('cursor', cursor);
  return query.toString();
}
function event(value: unknown): value is AuditEvent {
  if (!value || typeof value !== 'object') return false;
  const row = value as AuditEvent;
  return typeof row.id === 'string' && /^[0-9a-f]{32}$/.test(row.id) && auditSources.includes(row.source)
    && [null, 'string'].includes(row.tenantId === null ? null : typeof row.tenantId)
    && ['actorUserId', 'resourceType', 'resourceId', 'action', 'outcome', 'requestId', 'occurredAt'].every(key => typeof row[key as keyof AuditEvent] === 'string')
    && Number.isFinite(Date.parse(row.occurredAt)) && ['success', 'failure', 'unfinished'].includes(row.status)
    && ['appId', 'releaseVersion', 'runId', 'gatewayRequestId'].every(key => row[key as keyof AuditEvent] === null || typeof row[key as keyof AuditEvent] === 'string')
    && !!row.details && typeof row.details === 'object' && !Array.isArray(row.details)
    && Object.entries(row.details).length <= 32 && Object.values(row.details).every(value => value === null || typeof value === 'string');
}
export function createAuditApi(fetcher: typeof fetch = globalThis.fetch.bind(globalThis)) {
  async function request<T>(path: string, signal: AbortSignal, method = 'GET', body?: unknown, csrf?: string): Promise<T> {
    const controller = new AbortController(); const abort = () => controller.abort(signal.reason);
    signal.addEventListener('abort', abort, { once: true }); if (signal.aborted) abort();
    const timer = setTimeout(() => controller.abort(), 20_000);
    let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
    const cancel = () => { void reader?.cancel().catch(() => undefined); };
    controller.signal.addEventListener('abort', cancel, { once: true });
    try {
      controller.signal.throwIfAborted();
      const response = await fetcher(path, { method, signal: controller.signal, credentials: 'same-origin', cache: 'no-store', redirect: 'error',
        headers: { Accept: 'application/json', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), ...(csrf ? { 'X-CSRF-TOKEN': csrf } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
      if (!response.ok || response.headers.get('content-type')?.split(';', 1)[0].trim().toLowerCase() !== 'application/json' || !response.body) {
        void response.body?.cancel().catch(() => undefined); throw new AuditApiError(response.ok ? 502 : response.status);
      }
      reader = response.body.getReader(); const decoder = new TextDecoder('utf-8', { fatal: true }); let text = ''; let bytes = 0;
      while (true) {
        controller.signal.throwIfAborted(); const part = await reader.read(); controller.signal.throwIfAborted(); if (part.done) break;
        bytes += part.value.byteLength; if (bytes > 4 * 1024 * 1024) throw new AuditApiError(502); text += decoder.decode(part.value, { stream: true });
      }
      return JSON.parse(text + decoder.decode()) as T;
    } catch (failure) { if (signal.aborted) throw signal.reason; if (failure instanceof AuditApiError) throw failure; throw new AuditApiError(503); }
    finally { clearTimeout(timer); signal.removeEventListener('abort', abort); controller.abort(); controller.signal.removeEventListener('abort', cancel); await reader?.cancel().catch(() => undefined); reader?.releaseLock(); }
  }
  const root = '/api/platform/v1/audit';
  return {
    workspace: async (scope: AuditScope, signal: AbortSignal) => {
      const data = await request<AuditWorkspace>(`${root}/workspace?scope=${scope}`, signal);
      if (!data || data.scope !== scope || !Array.isArray(data.sources) || !data.sources.every(source => auditSources.includes(source))
        || typeof data.canExport !== 'boolean' || typeof data.globalAvailable !== 'boolean' || !data.policy
        || data.policy.maximumRangeDays !== 31 || data.policy.maximumPageSize !== 100 || data.policy.maximumExportRecords !== 1000) throw new AuditApiError(502);
      return data;
    },
    events: async (filters: AuditFilters, cursor: string | null, signal: AbortSignal) => {
      const data = await request<AuditPage>(`${root}/events?${auditQuery(filters, cursor)}`, signal);
      if (!data || !Array.isArray(data.items) || data.items.length > 100 || !data.items.every(event)
        || !(data.nextCursor === null || typeof data.nextCursor === 'string' && data.nextCursor.length <= 8192)) throw new AuditApiError(502);
      return data;
    },
    detail: async (source: AuditSource, id: string, filters: AuditFilters, signal: AbortSignal) => {
      const data = await request<AuditEvent>(`${root}/events/${encodeURIComponent(source)}/${encodeURIComponent(id)}?${auditQuery(filters)}`, signal);
      if (!event(data) || data.source !== source || data.id !== id) throw new AuditApiError(502); return data;
    },
    export: async (filters: AuditFilters, signal: AbortSignal) => {
      const csrf = await request<{ headerName: string; requestToken: string }>('/bff/csrf', signal);
      if (csrf?.headerName !== 'X-CSRF-TOKEN' || typeof csrf.requestToken !== 'string' || !csrf.requestToken || csrf.requestToken.length > 8192) throw new AuditApiError(503);
      const exportFilters = { ...filters }; delete exportFilters.pageSize;
      const data = await request<AuditExport>(`${root}/export?${auditQuery(exportFilters)}`, signal, 'POST', {}, csrf.requestToken);
      if (!data || !Array.isArray(data.items) || data.items.length > 1000 || !data.items.every(event) || typeof data.truncated !== 'boolean'
        || typeof data.exportedAt !== 'string' || !Number.isFinite(Date.parse(data.exportedAt))) throw new AuditApiError(502); return data;
    },
  };
}
export const auditApi = createAuditApi();
