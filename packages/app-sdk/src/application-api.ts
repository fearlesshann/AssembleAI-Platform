/** Browser bindings are request intent, never authorization. The BFF validates every call. */
export interface ApplicationBinding {
  runId: string;
  tenantId: string;
  appId: string;
  releaseVersion: string;
  expiresAt: string;
}
export interface ApplicationRequest {
  method?: 'GET' | 'POST';
  query?: Readonly<Record<string, string | number | boolean>>;
  body?: unknown;
  signal?: AbortSignal;
}
export interface ApplicationEvent<T = unknown> { event: 'delta' | 'complete'; data: T }
export interface ApplicationApi {
  json<T = unknown>(operation: string, options?: ApplicationRequest): Promise<T>;
  stream<T = unknown>(operation: string, options?: ApplicationRequest): AsyncIterable<ApplicationEvent<T>>;
}
export interface ApplicationClient { readonly api: ApplicationApi; dispose(): void }
export type ApplicationErrorCode = 'invalid_request' | 'invalid_binding' | 'payload_too_large' | 'invalid_response'
  | 'request_failed' | 'unavailable' | 'cancelled' | 'disposed' | 'expired' | 'timeout'
  | 'stream_error' | 'stream_interrupted' | 'stream_protocol_error';
export class ApplicationApiError extends Error {
  readonly code: ApplicationErrorCode;
  readonly status?: number;
  constructor(code: ApplicationErrorCode, status?: number) {
    super('The protected application request could not complete.');
    this.name = 'ApplicationApiError'; this.code = code; this.status = status;
  }
}

const JSON_LIMIT = 1024 * 1024;
const FRAME_LIMIT = 64 * 1024;
const encoder = new TextEncoder();
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const appId = /^[a-z][a-z0-9-]{0,63}$/;
const version = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
function fail(code: ApplicationErrorCode): never { throw new ApplicationApiError(code); }

function mediaType(response: Response, expected: string) {
  if (response.headers.get('content-type')?.split(';', 1)[0].trim().toLowerCase() !== expected) fail('invalid_response');
}

async function readJson<T>(response: Response, signal: AbortSignal): Promise<T> {
  mediaType(response, 'application/json');
  if (!response.body) fail('invalid_response');
  const reader = response.body.getReader();
  const cancel = () => { void reader.cancel().catch(() => undefined); };
  signal.addEventListener('abort', cancel, { once: true });
  let size = 0; let content = '';
  const decoder = new TextDecoder('utf-8', { fatal: true });
  try {
    while (true) {
      signal.throwIfAborted();
      const item = await reader.read();
      signal.throwIfAborted();
      if (item.done) break;
      size += item.value.byteLength;
      if (size > JSON_LIMIT) fail('invalid_response');
      content += decoder.decode(item.value, { stream: true });
    }
    content += decoder.decode();
    return JSON.parse(content) as T;
  } catch (error) {
    if (signal.aborted) throw signal.reason;
    if (error instanceof ApplicationApiError) throw error;
    fail('invalid_response');
  } finally {
    signal.removeEventListener('abort', cancel);
    await reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
}

/** The returned capability cannot retarget a run or override identity headers. It is not a JS security sandbox. */
export function createApplicationClient(binding: ApplicationBinding, dependencies: { fetch?: typeof fetch; clock?: () => number } = {}): ApplicationClient {
  const run = { ...binding };
  const clock = dependencies.clock ?? Date.now;
  const fetcher = dependencies.fetch ?? globalThis.fetch.bind(globalThis);
  const expires = Date.parse(run.expiresAt);
  const remaining = expires - clock();
  if (!uuid.test(run.runId) || !uuid.test(run.tenantId) || !appId.test(run.appId) || typeof run.releaseVersion !== 'string' || run.releaseVersion.length > 64 || !version.test(run.releaseVersion)
    || !Number.isFinite(remaining) || remaining <= 0 || remaining > 3_601_000) fail('invalid_binding');
  const base = `/api/apps/${run.appId}/releases/${run.releaseVersion}/`;
  const lifetime = new AbortController();
  const lease = setTimeout(() => lifetime.abort(new ApplicationApiError('expired')), remaining);
  const dispose = () => { clearTimeout(lease); lifetime.abort(new ApplicationApiError('disposed')); };

  function requestScope(options: ApplicationRequest, streaming: boolean) {
    if (clock() >= expires && !lifetime.signal.aborted) lifetime.abort(new ApplicationApiError('expired'));
    lifetime.signal.throwIfAborted();
    const controller = new AbortController();
    const end = () => controller.abort(lifetime.signal.reason);
    const cancel = () => controller.abort(new ApplicationApiError('cancelled'));
    lifetime.signal.addEventListener('abort', end, { once: true });
    options.signal?.addEventListener('abort', cancel, { once: true });
    if (options.signal?.aborted) cancel();
    const timer = setTimeout(() => controller.abort(new ApplicationApiError('timeout')), streaming ? 600_000 : 15_000);
    return {
      signal: controller.signal,
      close() {
        clearTimeout(timer); lifetime.signal.removeEventListener('abort', end);
        options.signal?.removeEventListener('abort', cancel); controller.abort();
      },
    };
  }

  async function response(operation: string, options: ApplicationRequest, signal: AbortSignal, streaming: boolean) {
    if (typeof operation !== 'string' || operation.length > 512 || !/^[A-Za-z0-9_-]+(?:\/[A-Za-z0-9_-]+)*$/.test(operation)) fail('invalid_request');
    const method = options.method ?? 'GET';
    if (method !== 'GET' && method !== 'POST') fail('invalid_request');
    if (method === 'GET' && options.body !== undefined) fail('invalid_request');
    let body: string | undefined;
    if (method === 'POST') {
      try { body = JSON.stringify(options.body); } catch { fail('invalid_request'); }
      if (body === undefined) fail('invalid_request');
      if (encoder.encode(body).byteLength > JSON_LIMIT) fail('payload_too_large');
    }
    const query = new URLSearchParams();
    for (const [key, value] of Object.entries(options.query ?? {})) {
      if (!['string', 'number', 'boolean'].includes(typeof value) || (typeof value === 'number' && !Number.isFinite(value))) fail('invalid_request');
      query.append(key, String(value));
    }
    const search = query.toString();
    if (search.length > 2048) fail('invalid_request');
    const headers: Record<string, string> = {
      Accept: streaming ? 'text/event-stream' : 'application/json', 'X-Run-ID': run.runId, 'X-Tenant-ID': run.tenantId,
    };
    if (method === 'POST') {
      signal.throwIfAborted();
      const csrfResponse = await fetcher('/bff/csrf', { method: 'GET', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal, headers: { Accept: 'application/json' } });
      if (!csrfResponse.ok) throw new ApplicationApiError('request_failed', csrfResponse.status);
      const csrf = await readJson<{ headerName: string; requestToken: string }>(csrfResponse, signal);
      if (csrf?.headerName !== 'X-CSRF-TOKEN' || typeof csrf.requestToken !== 'string' || !csrf.requestToken || csrf.requestToken.length > 8192) fail('invalid_response');
      headers['X-CSRF-TOKEN'] = csrf.requestToken; headers['Content-Type'] = 'application/json';
    }
    signal.throwIfAborted();
    const result = await fetcher(`${base}${operation}${search ? `?${search}` : ''}`, { method, body, headers, signal, credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
    if (!result.ok) { void result.body?.cancel().catch(() => undefined); throw new ApplicationApiError('request_failed', result.status); }
    return result;
  }

  function safeError(error: unknown, signal: AbortSignal): never {
    if (signal.aborted && signal.reason instanceof ApplicationApiError) throw signal.reason;
    if (error instanceof ApplicationApiError) throw error;
    fail('unavailable');
  }

  const api: ApplicationApi = Object.freeze({
    async json<T = unknown>(operation: string, options: ApplicationRequest = {}): Promise<T> {
      const scope = requestScope(options, false);
      try { return await readJson<T>(await response(operation, options, scope.signal, false), scope.signal); }
      catch (error) { return safeError(error, scope.signal); }
      finally { scope.close(); }
    },
    async *stream<T = unknown>(operation: string, options: ApplicationRequest = {}): AsyncIterable<ApplicationEvent<T>> {
      const scope = requestScope(options, true);
      let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
      const cancel = () => { void reader?.cancel().catch(() => undefined); };
      try {
        const result = await response(operation, options, scope.signal, true);
        mediaType(result, 'text/event-stream');
        if (!result.body) fail('invalid_response');
        reader = result.body.getReader();
        scope.signal.addEventListener('abort', cancel, { once: true });
        let line: number[] = []; let frameSize = 0; let event = ''; let data: string[] = [];
        let previousCR = false; let firstLine = true;
        const decoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });
        function endLine(): ApplicationEvent<T> | undefined {
          let text: string;
          try { text = decoder.decode(Uint8Array.from(line)); } catch { return fail('stream_protocol_error'); }
          line = [];
          if (firstLine) { text = text.replace(/^\uFEFF/, ''); firstLine = false; }
          if (text === '') {
            frameSize = 0;
            if (!event && !data.length) return;
            if (!['delta', 'complete', 'error'].includes(event) || !data.length) fail('stream_protocol_error');
            let value: T;
            try { value = JSON.parse(data.join('\n')) as T; } catch { return fail('stream_protocol_error'); }
            if (event === 'error') fail('stream_error');
            const item = { event: event as 'delta' | 'complete', data: value };
            event = ''; data = []; return item;
          }
          if (text.startsWith(':')) return;
          const colon = text.indexOf(':');
          const field = colon < 0 ? text : text.slice(0, colon);
          let value = colon < 0 ? '' : text.slice(colon + 1);
          if (value.startsWith(' ')) value = value.slice(1);
          if (field === 'event') event = value;
          if (field === 'data') data.push(value);
        }
        while (true) {
          scope.signal.throwIfAborted();
          const chunk = await reader.read();
          scope.signal.throwIfAborted();
          if (chunk.done) fail('stream_interrupted');
          for (const byte of chunk.value) {
            // A consumer can cancel between next() calls while this chunk still contains events.
            scope.signal.throwIfAborted();
            if (++frameSize > FRAME_LIMIT) fail('stream_protocol_error');
            if (previousCR && byte === 10) { previousCR = false; continue; }
            previousCR = byte === 13;
            if (byte === 10 || byte === 13) {
              const item = endLine();
              if (item) {
                if (item.event === 'complete') { await reader.cancel().catch(() => undefined); scope.close(); yield item; return; }
                yield item;
              }
            } else line.push(byte);
          }
        }
      } catch (error) { safeError(error, scope.signal); }
      finally {
        scope.signal.removeEventListener('abort', cancel);
        await reader?.cancel().catch(() => undefined); reader?.releaseLock(); scope.close();
      }
    },
  });
  return Object.freeze({ api, dispose });
}
