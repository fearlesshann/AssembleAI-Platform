export type { Theme } from '@assembleai/app-sdk';

export interface PortalSession { iss: string; sub: string; name: string | null }
export interface PortalTenant { tenantId: string; name: string }
export interface PortalApp {
  appId: string;
  name: string;
  releaseVersion: string;
  category: string;
  description: string;
}
export interface PortalCatalog { items: PortalApp[]; categories: string[] }
export interface PortalRun {
  runId: string;
  tenantId: string;
  appId: string;
  releaseVersion: string;
  manifest: {
    appId: string;
    name: string;
    version: string;
    frontendEntry: string;
    frontendArtifact: { version: string; sha256: string };
  };
  expiresAt: string;
  apiBase: string;
}

export class PortalApiError extends Error {
  constructor(public readonly status: number, public readonly code = '') {
    super('The platform request failed.');
  }
}

/** Browser requests carry only the existing same-origin session, never OIDC tokens. */
async function request<T>(path: string, signal: AbortSignal, method = 'GET', body?: unknown, headers: Record<string, string> = {}): Promise<T> {
  const deadline = new AbortController();
  const abort = () => deadline.abort(signal.reason);
  signal.addEventListener('abort', abort, { once: true });
  if (signal.aborted) abort();
  const timer = window.setTimeout(() => deadline.abort(), 15_000);
  try {
    const response = await fetch(path, {
      method, signal: deadline.signal, credentials: 'same-origin', cache: 'no-store', redirect: 'error',
      headers: { Accept: 'application/json', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), ...headers },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) throw new PortalApiError(response.status);
    if (response.status === 204) return undefined as T;
    if (!response.headers.get('content-type')?.toLowerCase().includes('application/json')) throw new PortalApiError(502);
    return await response.json() as T;
  } catch (error) {
    if (signal.aborted || error instanceof PortalApiError) throw error;
    throw new PortalApiError(503);
  } finally {
    window.clearTimeout(timer);
    signal.removeEventListener('abort', abort);
  }
}

async function write<T>(path: string, signal: AbortSignal, body?: unknown): Promise<T> {
  const csrf = await request<{ requestToken: string; headerName: string }>('/bff/csrf', signal);
  if (csrf.headerName !== 'X-CSRF-TOKEN' || typeof csrf.requestToken !== 'string' || !csrf.requestToken) throw new PortalApiError(503);
  return request<T>(path, signal, 'POST', body, { 'X-CSRF-TOKEN': csrf.requestToken });
}

const catalogPath = (tenant: string) => `/api/platform/v1/tenants/${encodeURIComponent(tenant)}/catalog`;
export const portalApi = {
  session: (signal: AbortSignal) => request<PortalSession>('/bff/session', signal),
  tenants: (signal: AbortSignal) => request<{ items: PortalTenant[] }>('/api/platform/v1/tenants', signal),
  selection: (signal: AbortSignal) => request<{ tenantId: string | null; authorizationVersion: number | null }>('/bff/tenant', signal),
  select: (tenantId: string, signal: AbortSignal) => write<{ tenantId: string }>('/bff/tenant', signal, { tenantId }),
  catalog: (tenant: string, signal: AbortSignal) => request<PortalCatalog>(catalogPath(tenant), signal),
  detail: (tenant: string, app: string, signal: AbortSignal) => request<PortalApp>(`${catalogPath(tenant)}/${encodeURIComponent(app)}`, signal),
  start: (tenant: string, app: PortalApp, signal: AbortSignal) => write<PortalRun>(
    `/api/platform/v1/tenants/${encodeURIComponent(tenant)}/applications/${encodeURIComponent(app.appId)}/releases/${encodeURIComponent(app.releaseVersion)}/runs`, signal),
  logout: (signal: AbortSignal) => write<void>('/bff/logout', signal),
};
