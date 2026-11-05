export type Stage = 'retrieving' | 'answering' | 'verifying';
export interface DocumentReference { documentId: string; revision: number }
export interface Citation { documentId: string; documentRevision: number; chunkId: string; fileName: string; locator: string; quote: string }
export interface Answer {
  requestId: string; modelRequestId: string | null; searchRequestId: string;
  answer: string; refusal: boolean; citations: Citation[]; profile: string;
  authorizationVersion: string; documents: DocumentReference[];
}
export const PROFILE = 'e5-small-dd476dd0-384-v1';
export const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const requestId = /^[0-9a-f]{32}$/;
const encoder = new TextEncoder();
export function invalid(): never { throw new Error('Knowledge response is invalid or incomplete.'); }
export function object(value: unknown): value is Record<string, unknown> { return !!value && typeof value === 'object' && !Array.isArray(value); }
export function fields(value: unknown, names: string[]): value is Record<string, unknown> {
  return object(value) && Object.keys(value).length === names.length && names.every(name => Object.hasOwn(value, name));
}
export function text(value: unknown, maximum: number): value is string {
  return typeof value === 'string' && value.trim().length > 0 && [...value].length <= maximum
    && !/[\uD800-\uDFFF]/u.test(value) && !value.includes('\0');
}
export function positive(value: unknown): value is number { return typeof value === 'number' && Number.isSafeInteger(value) && value > 0; }
export function parseAnswer(value: unknown): Answer {
  if (!fields(value, ['requestId', 'modelRequestId', 'searchRequestId', 'answer', 'refusal', 'citations', 'profile', 'authorizationVersion', 'documents'])
    || ![value.requestId, value.searchRequestId].every(id => typeof id === 'string' && requestId.test(id))
    || value.modelRequestId !== null && (typeof value.modelRequestId !== 'string' || !requestId.test(value.modelRequestId))
    || !text(value.answer, 4000) || typeof value.refusal !== 'boolean' || value.profile !== PROFILE
    || typeof value.authorizationVersion !== 'string' || !/^[1-9][0-9]{0,18}$/.test(value.authorizationVersion)
    || BigInt(value.authorizationVersion) > 9223372036854775807n
    || !Array.isArray(value.documents) || value.documents.length > 5 || !Array.isArray(value.citations) || value.citations.length > 5) return invalid();
  const documents = new Map<string, number>();
  for (const doc of value.documents) {
    if (!fields(doc, ['documentId', 'revision']) || typeof doc.documentId !== 'string' || !UUID.test(doc.documentId)
      || !positive(doc.revision) || documents.has(doc.documentId)) return invalid();
    documents.set(doc.documentId, doc.revision);
  }
  const chunks = new Set<string>();
  const citations = value.citations.map(cite => {
    if (!fields(cite, ['documentId', 'documentRevision', 'chunkId', 'fileName', 'locator', 'quote'])
      || typeof cite.documentId !== 'string' || !positive(cite.documentRevision) || documents.get(cite.documentId) !== cite.documentRevision
      || !text(cite.chunkId, 128) || !new RegExp(`^${cite.documentId.replaceAll('-', '')}-${cite.documentRevision}-(0|[1-9][0-9]{0,4})-(0|[1-9][0-9]{0,4})$`).test(cite.chunkId)
      || chunks.has(cite.chunkId) || !text(cite.fileName, 200) || !text(cite.locator, 200) || !text(cite.quote, 600)) return invalid();
    chunks.add(cite.chunkId); return { ...cite } as unknown as Citation;
  });
  if (value.refusal ? citations.length !== 0 : citations.length === 0 || value.modelRequestId === null) return invalid();
  return { ...value, citations, documents: [...documents].map(([documentId, revision]) => ({ documentId, revision })) } as Answer;
}
/** No answer or citation leaves this buffer before a complete, ordered stream. */
export function createAnswerStream() {
  let stage = 0; let events = 0; let result: Answer | undefined; let failed = false;
  return {
    accept(event: unknown): { kind: 'stage'; stage: Stage } | { kind: 'complete' } {
      try {
        if (failed || result || ++events > 4 || !fields(event, ['event', 'data']) || encoder.encode(JSON.stringify(event)).length > 65536) return invalid();
        const data = event.data;
        if (event.event === 'delta' && fields(data, ['kind', 'stage']) && data.kind === 'stage') {
          const next = ['retrieving', 'answering', 'verifying'].indexOf(data.stage as string) + 1;
          if (next <= stage || next < 1 || stage === 0 && next !== 1 || next !== stage + 1 && !(stage === 1 && next === 3)) return invalid();
          stage = next; return { kind: 'stage', stage: data.stage as Stage };
        }
        if (event.event !== 'complete' || stage !== 3) return invalid();
        result = parseAnswer(data); return { kind: 'complete' };
      } catch { failed = true; result = undefined; return invalid(); }
    },
    finish(): Answer { if (failed || !result) return invalid(); return result; },
  };
}
export function contextIdentity(context: unknown): string {
  if (!object(context)) return '';
  const names = ['appId', 'releaseVersion', 'mode', 'tenantId', 'userId', 'issuer', 'subject', 'iss', 'sub', 'authorizationVersion'];
  const identity = object(context.identity) ? context.identity : {};
  return JSON.stringify([names.map(name => context[name] ?? null), ['issuer', 'subject', 'iss', 'sub', 'userId'].map(name => identity[name] ?? null)]);
}
