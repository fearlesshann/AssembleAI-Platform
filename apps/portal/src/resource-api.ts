export type ResourceSection = 'models' | 'data-sources';
export interface ModelResource { modelId: string; displayName: string; enabled: boolean; canManage: boolean; canInvoke: boolean; deploymentEnabled: boolean; maximumOutputTokens: number; deploymentMaximumOutputTokens: number; callTimeoutSeconds: number }
export interface SourceResource { sourceId: string; displayName: string; enabled: boolean; canManage: boolean; canTest: boolean; deploymentEnabled: boolean; maximumRows: number; timeoutSeconds: number; deploymentMaximumRows: number; deploymentTimeoutSeconds: number; datasets: { datasetId: string; displayName: string; columns: { name: string; type: string }[] }[] }
export interface ResourceConfiguration { tenantId: string; modelId?: string; sourceId?: string; enabled: boolean; maxOutputTokens?: number; maxRows?: number; timeoutSeconds?: number; revision: number }
export interface ResourceCall { callId: string; tenantId: string; userId: string; requestId: string; outcome: string; elapsedMilliseconds: number; startedAt: string; modelId?: string; sourceId?: string; datasetId?: string | null; operation?: string; rowCount?: number | null; usage?: { promptTokens: number; completionTokens: number; totalTokens: number } | null; source?: { appId: string; releaseVersion: string; runId: string; gatewayRequestId: string } | null }
export interface ModelTestResult { requestId: string; content: string; finishReason: string | null; usage: { promptTokens: number; completionTokens: number; totalTokens: number } | null }
export class ResourceApiError extends Error { readonly status: number; constructor(status: number) { super('Resource request could not complete.'); this.status = status; } }
const positive = (value: unknown, maximum = Number.MAX_SAFE_INTEGER) => typeof value === 'number' && Number.isSafeInteger(value) && value >= 1 && value <= maximum;
const identifier = (value: unknown) => typeof value === 'string' && /^[a-z][a-z0-9._-]{0,63}$/.test(value);
function validResource(section: ResourceSection, value: unknown): boolean {
  if (!value || typeof value !== 'object') return false;
  const row = value as Record<string, unknown>;
  if (typeof row.displayName !== 'string' || !row.displayName || !['enabled', 'canManage', 'deploymentEnabled'].every(key => typeof row[key] === 'boolean')) return false;
  if (section === 'models') return identifier(row.modelId) && typeof row.canInvoke === 'boolean'
    && positive(row.deploymentMaximumOutputTokens, 32768) && positive(row.maximumOutputTokens, row.deploymentMaximumOutputTokens as number) && positive(row.callTimeoutSeconds, 600);
  return identifier(row.sourceId) && typeof row.canTest === 'boolean' && positive(row.deploymentMaximumRows, 500)
    && positive(row.maximumRows, row.deploymentMaximumRows as number) && positive(row.deploymentTimeoutSeconds, 30)
    && positive(row.timeoutSeconds, row.deploymentTimeoutSeconds as number) && Array.isArray(row.datasets)
    && row.datasets.every(dataset => dataset && identifier(dataset.datasetId) && typeof dataset.displayName === 'string' && Array.isArray(dataset.columns)
      && dataset.columns.every((column: { name?: unknown; type?: unknown } | null) => column && identifier(column.name) && typeof column.type === 'string'));
}
export function createResourceApi(fetcher: typeof fetch = globalThis.fetch.bind(globalThis)) {
  async function request<T>(path: string, signal: AbortSignal, method = 'GET', body?: unknown, csrf?: string, milliseconds = 15_000): Promise<T> {
    const controller = new AbortController(); const abort = () => controller.abort(signal.reason);
    signal.addEventListener('abort', abort, { once: true }); if (signal.aborted) abort();
    const timer = setTimeout(() => controller.abort(), milliseconds);
    let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
    const cancel = () => { void reader?.cancel().catch(() => undefined); };
    controller.signal.addEventListener('abort', cancel, { once: true });
    try {
      controller.signal.throwIfAborted();
      const response = await fetcher(path, { method, signal: controller.signal, credentials: 'same-origin', cache: 'no-store', redirect: 'error',
        headers: { Accept: 'application/json', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), ...(csrf ? { 'X-CSRF-TOKEN': csrf } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
      if (!response.ok || response.headers.get('content-type')?.split(';', 1)[0].trim().toLowerCase() !== 'application/json' || !response.body) {
        void response.body?.cancel().catch(() => undefined); throw new ResourceApiError(response.ok ? 502 : response.status);
      }
      reader = response.body.getReader(); const decoder = new TextDecoder('utf-8', { fatal: true }); let text = ''; let bytes = 0;
      while (true) { controller.signal.throwIfAborted(); const part = await reader.read(); controller.signal.throwIfAborted(); if (part.done) break;
        bytes += part.value.byteLength; if (bytes > 4 * 1024 * 1024) throw new ResourceApiError(502); text += decoder.decode(part.value, { stream: true }); }
      return JSON.parse(text + decoder.decode()) as T;
    } catch (failure) { if (signal.aborted) throw signal.reason; if (failure instanceof ResourceApiError) throw failure; throw new ResourceApiError(503); }
    finally { clearTimeout(timer); signal.removeEventListener('abort', abort); controller.abort(); controller.signal.removeEventListener('abort', cancel); await reader?.cancel().catch(() => undefined); reader?.releaseLock(); }
  }
  async function write<T>(path: string, signal: AbortSignal, method: string, body: unknown, milliseconds = 15_000): Promise<T> {
    const csrf = await request<{ headerName: string; requestToken: string }>('/bff/csrf', signal);
    if (csrf?.headerName !== 'X-CSRF-TOKEN' || typeof csrf.requestToken !== 'string' || !csrf.requestToken || csrf.requestToken.length > 8192) throw new ResourceApiError(503);
    signal.throwIfAborted(); return request<T>(path, signal, method, body, csrf.requestToken, milliseconds);
  }
  const root = (section: ResourceSection, tenant: string, id = '') => `/api/platform/v1/tenants/${encodeURIComponent(tenant)}/${section === 'models' ? 'models' : 'connectors'}${id ? `/${encodeURIComponent(id)}` : ''}`;
  async function list<T>(section: ResourceSection, tenant: string, signal: AbortSignal): Promise<{ items: T[] }> {
    const data = await request<{ items: T[] }>(root(section, tenant), signal);
    if (!data || !Array.isArray(data.items) || !data.items.every(item => validResource(section, item))) throw new ResourceApiError(502);
    return data;
  }
  return {
    models: (tenant: string, signal: AbortSignal) => list<ModelResource>('models', tenant, signal),
    sources: (tenant: string, signal: AbortSignal) => list<SourceResource>('data-sources', tenant, signal),
    configuration: async (section: ResourceSection, tenant: string, id: string, signal: AbortSignal): Promise<ResourceConfiguration | null> => {
      const data = await request<ResourceConfiguration | { configuration: ResourceConfiguration | null } | null>(`${root(section, tenant, id)}/configuration`, signal);
      if (section === 'models') return data as ResourceConfiguration | null;
      if (!data || !('configuration' in data)) throw new ResourceApiError(502); return data.configuration;
    },
    save: (section: ResourceSection, tenant: string, id: string, body: unknown, signal: AbortSignal) => write<unknown>(`${root(section, tenant, id)}/configuration`, signal, 'PUT', body),
    calls: async (section: ResourceSection, tenant: string, id: string, signal: AbortSignal) => {
      const data = await request<{ items: ResourceCall[] }>(`${root(section, tenant, id)}/calls`, signal);
      if (!data || !Array.isArray(data.items) || data.items.length > 50) throw new ResourceApiError(502); return data;
    },
    testModel: (tenant: string, id: string, content: string, maxOutputTokens: number, timeoutSeconds: number, signal: AbortSignal) => write<ModelTestResult>(`${root('models', tenant, id)}/chat`, signal, 'POST', { messages: [{ role: 'user', content }], maxOutputTokens, stream: false }, (Math.min(600, Math.max(1, timeoutSeconds)) + 5) * 1000),
    testSource: (tenant: string, id: string, signal: AbortSignal) => write<{ requestId: string; status: string }>(`${root('data-sources', tenant, id)}/test`, signal, 'POST', {}, 35_000),
  };
}
export const resourceApi = createResourceApi();
