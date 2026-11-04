export interface Dataset { sourceId: string; datasetId: string; displayName: string; columns: { name: string; type: string }[]; maxRows: number }
export type Stage = 'planning' | 'querying' | 'receiving';
export interface AnalysisResult {
  requestId: string; modelRequestId: string; queryRequestId: string; sourceId: string; datasetId: string;
  datasetName: string; columns: string[]; rowCount: number; queryText: string; scope: string; summary: string;
  rows: (string | null)[][];
}
const alias = /^[a-z][a-z0-9._-]{0,63}$/;
const requestId = /^[a-f0-9]{32}$/;
const encoder = new TextEncoder();
const bytes = (value: unknown) => encoder.encode(JSON.stringify(value)).length;
function invalid(): never { throw new Error('ChatBI response is invalid or incomplete.'); }
function object(value: unknown): value is Record<string, unknown> { return value !== null && typeof value === 'object' && !Array.isArray(value); }
function fields(value: unknown, names: string[]): value is Record<string, unknown> {
  return object(value) && Object.keys(value).length === names.length && names.every((name) => Object.hasOwn(value, name));
}
function text(value: unknown, maximum: number, empty = false): value is string {
  return typeof value === 'string' && (empty || value.length > 0) && !/[\uD800-\uDFFF]/u.test(value)
    && encoder.encode(value).length <= maximum;
}
function id(value: unknown): value is string { return typeof value === 'string' && alias.test(value); }
export function parseDatasets(value: unknown): Dataset[] {
  if (!fields(value, ['items']) || !Array.isArray(value.items) || value.items.length > 100) return invalid();
  const seen = new Set<string>();
  return value.items.map((item) => {
    if (!fields(item, ['sourceId', 'datasetId', 'displayName', 'columns', 'maxRows']) || !id(item.sourceId) || !id(item.datasetId)
      || seen.has(item.datasetId) || !text(item.displayName, 800) || !Number.isInteger(item.maxRows)
      || (item.maxRows as number) < 1 || (item.maxRows as number) > 500 || !Array.isArray(item.columns)
      || item.columns.length < 1 || item.columns.length > 32) return invalid();
    seen.add(item.datasetId);
    const columns = new Set<string>();
    return { sourceId: item.sourceId, datasetId: item.datasetId, displayName: item.displayName, maxRows: item.maxRows as number,
      columns: item.columns.map((column) => {
        if (!fields(column, ['name', 'type']) || !id(column.name) || columns.has(column.name)
          || !['text', 'integer', 'decimal', 'date'].includes(column.type as string)) return invalid();
        columns.add(column.name); return { name: column.name, type: column.type as string };
      }) };
  });
}

/** Streaming rows never leave this buffer before a verified complete event. */
export function createResultStream(dataset: Dataset) {
  let rows: (string | null)[][] = [];
  let result: AnalysisResult | undefined;
  let stage = -1; let count = 0; let totalBytes = 0; let failed = false;
  const stages: Stage[] = ['planning', 'querying', 'receiving'];
  return {
    accept(event: unknown): { kind: 'stage'; stage: Stage } | { kind: 'rows' } | { kind: 'complete' } {
      try {
        if (failed || result || !fields(event, ['event', 'data']) || ++count > 128 || bytes(event) > 32768) return invalid();
        totalBytes += bytes(event); if (totalBytes > 1024 * 1024) return invalid();
        const data = event.data;
        if (event.event === 'delta' && fields(data, ['kind', 'stage']) && data.kind === 'stage') {
          if (stage >= 2 || data.stage !== stages[stage + 1]) return invalid();
          stage++; return { kind: 'stage', stage: stages[stage] };
        }
        if (event.event === 'delta' && fields(data, ['kind', 'rows']) && data.kind === 'rows' && stage === 2) {
          if (!Array.isArray(data.rows) || rows.length + data.rows.length > Math.min(100, dataset.maxRows)) return invalid();
          for (const row of data.rows) {
            if (!Array.isArray(row) || row.length < 1 || row.length > 24
              || row.some((cell) => cell !== null && !text(cell, 4096, true))) return invalid();
            rows.push([...row] as (string | null)[]);
          }
          return { kind: 'rows' };
        }
        if (event.event !== 'complete' || stage !== 2 || !fields(data, ['requestId', 'modelRequestId', 'queryRequestId', 'sourceId',
          'datasetId', 'datasetName', 'columns', 'rowCount', 'queryText', 'scope', 'summary'])
          || ![data.requestId, data.modelRequestId, data.queryRequestId].every((value) => typeof value === 'string' && requestId.test(value))
          || data.sourceId !== dataset.sourceId || data.datasetId !== dataset.datasetId || data.datasetName !== dataset.displayName
          || !Array.isArray(data.columns) || data.columns.length < 1 || data.columns.length > 24
          || new Set(data.columns).size !== data.columns.length
          || data.columns.some((name) => !id(name) || !dataset.columns.some((column) => column.name === name) && !/^metric[0-7]$/.test(name))
          || !Number.isInteger(data.rowCount) || data.rowCount !== rows.length
          || rows.some((row) => row.length !== (data.columns as unknown[]).length)
          || !text(data.queryText, 16384) || !text(data.summary, 4000) || data.scope !== '当前授权工厂') return invalid();
        result = { ...(data as unknown as Omit<AnalysisResult, 'rows'>), columns: [...data.columns] as string[], rows: rows.map((row) => [...row]) };
        rows = []; return { kind: 'complete' };
      } catch { failed = true; rows = []; result = undefined; return invalid(); }
    },
    finish(): AnalysisResult { if (failed || !result) return invalid(); return result; },
  };
}

/** Display context is not authorization; changes invalidate locally cached results. */
export function contextIdentity(context: unknown): string {
  if (!object(context)) return '';
  const names = ['appId', 'releaseVersion', 'mode', 'tenantId', 'userId', 'issuer', 'subject', 'iss', 'sub', 'authorizationVersion'];
  const identity = object(context.identity) ? context.identity : {};
  return JSON.stringify([names.map((name) => context[name] ?? null), ['issuer', 'subject', 'iss', 'sub', 'userId'].map((name) => identity[name] ?? null)]);
}
