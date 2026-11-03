export type ReleaseStatus = 'draft' | 'pending_review' | 'published' | 'disabled';
export interface RegistryManifest {
  schemaVersion: 1; appId: string; name: string; version: string; category: string; description: string;
  frontendEntry: string; frontendArtifact: { version: string; sha256: string };
  backendService: string; backendVersion: string; permissions: string[]; status: ReleaseStatus;
}
export interface RegistryRelease { manifest: RegistryManifest; revision: number; ownerUserId: string; submittedBy: string | null; publishedAt: string | null }
export interface RegistryApplication { appId: string; name: string; category: string; ownerUserId: string; canEdit: boolean; canReview: boolean }
export interface RegistryWorkspace { actorUserId: string; canCreate: boolean; canReview: boolean; items: RegistryApplication[] }
export interface TenantConfiguration { releaseVersion: string; enabled: boolean; available: boolean; revision: number }
export interface TenantRegistryApplication { appId: string; name: string; category: string; current: TenantConfiguration | null }
export interface TenantRegistryWorkspace { actorUserId: string; tenantId: string; items: TenantRegistryApplication[] }
export interface RegistryAudit { eventId: string; actorUserId: string; tenantId: string | null; appId: string; releaseVersion: string; action: string; result: string; requestId: string; occurredAt: string }
export interface AuditListing { items: RegistryAudit[]; hasMore: boolean }
export type RegistryAction = 'submit' | 'reject' | 'publish' | 'disable' | 'enable';
export class RegistryApiError extends Error {
  readonly status: number;
  constructor(status: number) { super('Registry request could not complete.'); this.status = status; }
}

/** Fixed same-origin control-plane requests. Capabilities are display hints; the API authorizes every operation. */
export function createRegistryApi(fetcher: typeof fetch = globalThis.fetch.bind(globalThis)) {
  async function request<T>(path: string, signal: AbortSignal, method = 'GET', body?: unknown, csrf?: string): Promise<T> {
    const controller = new AbortController();
    const abort = () => controller.abort(signal.reason);
    signal.addEventListener('abort', abort, { once: true });
    if (signal.aborted) abort();
    const timer = setTimeout(() => controller.abort(), 15_000);
    let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
    const cancelBody = () => { void reader?.cancel().catch(() => undefined); };
    controller.signal.addEventListener('abort', cancelBody, { once: true });
    try {
      controller.signal.throwIfAborted();
      const response = await fetcher(path, {
        method, signal: controller.signal, credentials: 'same-origin', cache: 'no-store', redirect: 'error',
        headers: { Accept: 'application/json', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), ...(csrf ? { 'X-CSRF-TOKEN': csrf } : {}) },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (!response.ok) { void response.body?.cancel().catch(() => undefined); throw new RegistryApiError(response.status); }
      if (response.headers.get('content-type')?.split(';', 1)[0].trim().toLowerCase() !== 'application/json' || !response.body) throw new RegistryApiError(502);
      reader = response.body.getReader();
      const decoder = new TextDecoder('utf-8', { fatal: true }); let text = ''; let bytes = 0;
      while (true) {
        controller.signal.throwIfAborted();
        const part = await reader.read();
        controller.signal.throwIfAborted();
        if (part.done) break;
        bytes += part.value.byteLength;
        if (bytes > 4 * 1024 * 1024) throw new RegistryApiError(502);
        text += decoder.decode(part.value, { stream: true });
      }
      return JSON.parse(text + decoder.decode()) as T;
    } catch (error) {
      if (signal.aborted) throw signal.reason;
      if (error instanceof RegistryApiError) throw error;
      throw new RegistryApiError(503);
    } finally {
      clearTimeout(timer); signal.removeEventListener('abort', abort); controller.abort();
      controller.signal.removeEventListener('abort', cancelBody);
      await reader?.cancel().catch(() => undefined); reader?.releaseLock();
    }
  }
  async function write<T>(path: string, signal: AbortSignal, method: 'PUT' | 'POST', body: unknown): Promise<T> {
    const csrf = await request<{ headerName: string; requestToken: string }>('/bff/csrf', signal);
    if (csrf?.headerName !== 'X-CSRF-TOKEN' || typeof csrf.requestToken !== 'string' || !csrf.requestToken || csrf.requestToken.length > 8192) throw new RegistryApiError(503);
    signal.throwIfAborted();
    return request<T>(path, signal, method, body, csrf.requestToken);
  }
  const root = '/api/platform/v1/registry';
  const application = (appId: string) => `${root}/apps/${encodeURIComponent(appId)}`;
  const tenant = (tenantId: string) => `/api/platform/v1/tenants/${encodeURIComponent(tenantId)}`;
  return {
    workspace: (signal: AbortSignal) => request<RegistryWorkspace>(`${root}/workspace`, signal),
    tenantWorkspace: (tenantId: string, signal: AbortSignal) => request<TenantRegistryWorkspace>(`${tenant(tenantId)}/registry/workspace`, signal),
    releases: (appId: string, signal: AbortSignal) => request<{ items: RegistryRelease[] }>(`${application(appId)}/releases`, signal),
    tenantReleases: (tenantId: string, appId: string, signal: AbortSignal) => request<{ items: RegistryRelease[] }>(`${tenant(tenantId)}/registry/apps/${encodeURIComponent(appId)}/releases`, signal),
    audit: (appId: string, signal: AbortSignal) => request<AuditListing>(`${application(appId)}/audit`, signal),
    tenantAudit: (tenantId: string, appId: string, signal: AbortSignal) => request<AuditListing>(`${tenant(tenantId)}/registry/apps/${encodeURIComponent(appId)}/audit`, signal),
    save: (manifest: RegistryManifest, expectedRevision: number, signal: AbortSignal) => write<RegistryRelease>(`${application(manifest.appId)}/releases/${encodeURIComponent(manifest.version)}`, signal, 'PUT', { expectedRevision, manifest }),
    transition: (appId: string, version: string, action: RegistryAction, expectedRevision: number, signal: AbortSignal) => write<RegistryRelease>(`${application(appId)}/releases/${encodeURIComponent(version)}/transitions`, signal, 'POST', { expectedRevision, action }),
    configureTenant: (tenantId: string, appId: string, releaseVersion: string, enabled: boolean, expectedRevision: number, signal: AbortSignal) => write<TenantConfiguration>(`${tenant(tenantId)}/applications/${encodeURIComponent(appId)}`, signal, 'PUT', { expectedRevision, releaseVersion, enabled }),
  };
}
export const registryApi = createRegistryApi();
