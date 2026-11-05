export type IdentitySection = 'users' | 'tenants' | 'members' | 'organizations' | 'roles';
export interface IdentityTenant { tenantId: string; name: string; enabled: boolean; revision: number }
export interface IdentityUser { userId: string; issuer: string; subject: string; displayName: string; enabled: boolean; platformAdministrator: boolean; revision: number }
export interface IdentityMember { userId: string; displayName: string; enabled: boolean; userEnabled: boolean; administrator: boolean; roleIds: string[]; revision: number }
export interface IdentityOrganization { organizationId: string; name: string; kind: string; enabled: boolean; revision: number }
export interface IdentityGrant { permission: string; resourceType: string; resourceId: string; tenantWide: boolean; organizationIds: string[] }
export interface IdentityRole { roleId: string; name: string; enabled: boolean; revision: number; grants: IdentityGrant[] }
export interface PermissionReference { permission: string; resourceType: string; resourceId: string }
export interface PermissionAction { permission: string; name: string; description: string; organizationScope: boolean; requires: PermissionReference[]; legacy?: boolean }
export interface PermissionResource { resourceType: string; resourceId: string; name: string; category: string; status: string; actions: PermissionAction[]; legacy?: boolean }
export interface PermissionCatalog { tenantId: string; resources: PermissionResource[] }
export interface IdentityAudit { eventId: string; actorUserId: string; tenantId: string | null; entityType: string; entityId: string; action: string; result: string; requestId: string; occurredAt: string }
export interface IdentityWorkspaceData { actorUserId: string; platformAdministrator: boolean; tenants: IdentityTenant[]; users: IdentityUser[]; audit: { items: IdentityAudit[]; hasMore: boolean } }
export interface IdentityTenantData { actorUserId: string; platformAdministrator: boolean; tenant: IdentityTenant; members: IdentityMember[]; organizations: IdentityOrganization[]; roles: IdentityRole[]; audit: { items: IdentityAudit[]; hasMore: boolean } }
export class IdentityApiError extends Error {
  readonly status: number;
  readonly code: string;
  constructor(status: number, code = '') {
    super('Identity management request could not complete.'); this.status = status;
    this.code = status === 409 && code === 'identity_capacity_reached' ? code : '';
  }
}
export function createIdentityApi(fetcher: typeof fetch = globalThis.fetch.bind(globalThis)) {
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
      if (!response.ok) {
        let code = '';
        const mediaType = response.headers.get('content-type')?.split(';', 1)[0].trim().toLowerCase();
        // Only one public business code changes UI behavior. Never retain server
        // titles/details, and never parse an oversized or non-JSON problem body.
        if (response.status === 409 && response.body && ['application/problem+json', 'application/json'].includes(mediaType ?? '')
          && !(Number(response.headers.get('content-length')) > 4096)) {
          try {
            reader = response.body.getReader();
            const decoder = new TextDecoder('utf-8', { fatal: true }); let text = ''; let bytes = 0;
            while (true) {
              controller.signal.throwIfAborted();
              const part = await reader.read();
              controller.signal.throwIfAborted();
              if (part.done) break;
              bytes += part.value.byteLength;
              if (bytes > 4096) throw new Error('Problem exceeds the bounded public-code envelope.');
              text += decoder.decode(part.value, { stream: true });
            }
            const problem: unknown = JSON.parse(text + decoder.decode());
            if (problem && typeof problem === 'object' && !Array.isArray(problem)
              && 'code' in problem && problem.code === 'identity_capacity_reached') code = problem.code;
          } catch { /* Keep the HTTP status; malformed/private problem content is never surfaced. */ }
        }
        if (!reader) void response.body?.cancel().catch(() => undefined);
        throw new IdentityApiError(response.status, code);
      }
      if (response.headers.get('content-type')?.split(';', 1)[0].trim().toLowerCase() !== 'application/json' || !response.body) throw new IdentityApiError(502);
      reader = response.body.getReader();
      const decoder = new TextDecoder('utf-8', { fatal: true }); let text = ''; let bytes = 0;
      while (true) {
        controller.signal.throwIfAborted();
        const part = await reader.read();
        controller.signal.throwIfAborted();
        if (part.done) break;
        bytes += part.value.byteLength;
        if (bytes > 4 * 1024 * 1024) throw new IdentityApiError(502);
        text += decoder.decode(part.value, { stream: true });
      }
      return JSON.parse(text + decoder.decode()) as T;
    } catch (error) {
      if (signal.aborted) throw signal.reason;
      if (error instanceof IdentityApiError) throw error;
      throw new IdentityApiError(503);
    } finally {
      clearTimeout(timer); signal.removeEventListener('abort', abort); controller.abort();
      controller.signal.removeEventListener('abort', cancelBody);
      await reader?.cancel().catch(() => undefined); reader?.releaseLock();
    }
  }
  async function write<T>(path: string, signal: AbortSignal, method: 'PUT' | 'POST', body: unknown): Promise<T> {
    const csrf = await request<{ headerName: string; requestToken: string }>('/bff/csrf', signal);
    if (csrf?.headerName !== 'X-CSRF-TOKEN' || typeof csrf.requestToken !== 'string' || !csrf.requestToken || csrf.requestToken.length > 8192) throw new IdentityApiError(503);
    signal.throwIfAborted();
    return request<T>(path, signal, method, body, csrf.requestToken);
  }
  const root = '/api/platform/v1/identity-management';
  return {
    workspace: (signal: AbortSignal) => request<IdentityWorkspaceData>(`${root}/workspace`, signal),
    tenant: (tenantId: string, signal: AbortSignal) => request<IdentityTenantData>(`${root}/tenants/${encodeURIComponent(tenantId)}`, signal),
    permissionCatalog: (tenantId: string, signal: AbortSignal) => request<PermissionCatalog>(`${root}/tenants/${encodeURIComponent(tenantId)}/permission-catalog`, signal),
    save: (section: IdentitySection, id: string, tenantId: string | null, body: unknown, signal: AbortSignal) => {
      const scope = section === 'users' || section === 'tenants' ? root : `${root}/tenants/${encodeURIComponent(tenantId ?? '')}`;
      return write<{ revision: number }>(`${scope}/${section}/${encodeURIComponent(id)}`, signal, 'PUT', body);
    },
  };
}
export const identityApi = createIdentityApi();
