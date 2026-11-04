import type { ApplicationBinding } from '@assembleai/app-sdk';

export interface StandaloneSession { iss: string; sub: string; name: string | null }
export interface StandaloneTenant { tenantId: string; name: string }
export interface StandaloneSnapshot { session: StandaloneSession; tenants: StandaloneTenant[]; tenantId: string | null }
export interface StandaloneRun extends ApplicationBinding {
  apiBase: string;
  manifest: { appId: string; version: string; frontendEntry: string; frontendArtifact: { version: string } };
}
export class StandaloneError extends Error {
  readonly status: number;
  readonly code: string;
  constructor(status: number, code = 'unavailable') { super('Platform access is unavailable.'); this.status = status; this.code = code; }
}
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const version = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;

/** Fixed same-origin platform bootstrap. A browser context never supplies authorization. */
export function createStandalonePlatform(appId: string, frontendVersion: string, fetcher: typeof fetch = globalThis.fetch.bind(globalThis)) {
  if (!/^[a-z][a-z0-9-]{0,63}$/.test(appId) || typeof frontendVersion !== 'string' || frontendVersion.length > 64 || !version.test(frontendVersion)) throw new StandaloneError(400, 'invalid_response');
  async function request<T>(path: string, signal: AbortSignal, method = 'GET', body?: unknown, csrf?: string): Promise<T> {
    const controller = new AbortController();
    const cancel = () => controller.abort(signal.reason);
    signal.addEventListener('abort', cancel, { once: true });
    if (signal.aborted) cancel();
    const timer = setTimeout(() => controller.abort(), 15_000);
    try {
      controller.signal.throwIfAborted();
      const response = await fetcher(path, {
        method, credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: controller.signal,
        headers: { Accept: 'application/json', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), ...(csrf ? { 'X-CSRF-TOKEN': csrf } : {}) },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (!response.ok) throw new StandaloneError(response.status);
      if (response.headers.get('content-type')?.split(';', 1)[0].trim().toLowerCase() !== 'application/json') throw new StandaloneError(502, 'invalid_response');
      return await response.json() as T;
    } catch (error) {
      if (signal.aborted) throw signal.reason;
      if (error instanceof StandaloneError) throw error;
      throw new StandaloneError(503);
    } finally { clearTimeout(timer); signal.removeEventListener('abort', cancel); controller.abort(); }
  }
  async function write<T>(path: string, signal: AbortSignal, body?: unknown): Promise<T> {
    const csrf = await request<{ headerName: string; requestToken: string }>('/bff/csrf', signal);
    if (csrf?.headerName !== 'X-CSRF-TOKEN' || typeof csrf.requestToken !== 'string' || !csrf.requestToken || csrf.requestToken.length > 8192) throw new StandaloneError(502, 'invalid_response');
    return request(path, signal, 'POST', body, csrf.requestToken);
  }
  return {
    async inspect(signal: AbortSignal): Promise<StandaloneSnapshot> {
      const session = await request<StandaloneSession>('/bff/session', signal);
      if (!session || typeof session.iss !== 'string' || !session.iss || typeof session.sub !== 'string' || !session.sub) throw new StandaloneError(502, 'invalid_response');
      const [tenants, selection] = await Promise.all([
        request<{ items: StandaloneTenant[] }>('/api/platform/v1/tenants', signal),
        request<{ tenantId: string | null }>('/bff/tenant', signal).catch((error) => {
          if (error instanceof StandaloneError && error.status === 403) return { tenantId: null };
          throw error;
        }),
      ]);
      if (!Array.isArray(tenants?.items) || tenants.items.some((item) => !item || !uuid.test(item.tenantId) || typeof item.name !== 'string')) throw new StandaloneError(502, 'invalid_response');
      const selected = selection?.tenantId;
      return { session, tenants: tenants.items, tenantId: selected && tenants.items.some((tenant) => tenant.tenantId === selected) ? selected : null };
    },
    async select(tenantId: string, signal: AbortSignal): Promise<void> {
      if (!uuid.test(tenantId)) throw new StandaloneError(400, 'invalid_response');
      const selected = await write<{ tenantId: string }>('/bff/tenant', signal, { tenantId });
      if (selected?.tenantId !== tenantId) throw new StandaloneError(502, 'invalid_response');
    },
    async connect(tenantId: string, signal: AbortSignal): Promise<StandaloneRun> {
      if (!uuid.test(tenantId)) throw new StandaloneError(400, 'invalid_response');
      const detail = await request<{ appId: string; releaseVersion: string }>(`/api/platform/v1/tenants/${tenantId}/catalog/${appId}`, signal);
      if (detail?.appId !== appId || typeof detail.releaseVersion !== 'string' || detail.releaseVersion.length > 64 || !version.test(detail.releaseVersion)) throw new StandaloneError(502, 'invalid_response');
      const run = await write<StandaloneRun>(`/api/platform/v1/tenants/${tenantId}/applications/${appId}/releases/${detail.releaseVersion}/runs`, signal);
      if (run?.tenantId !== tenantId || run.appId !== appId || run.releaseVersion !== detail.releaseVersion || !uuid.test(run.runId)
        || run.manifest?.appId !== appId || run.manifest.version !== run.releaseVersion || run.apiBase !== `/api/apps/${appId}/releases/${run.releaseVersion}/`) throw new StandaloneError(502, 'invalid_response');
      if (run.manifest.frontendEntry !== `/apps/${appId}/${frontendVersion}/` || run.manifest.frontendArtifact?.version !== frontendVersion) throw new StandaloneError(409, 'version_changed');
      return run;
    },
  };
}
