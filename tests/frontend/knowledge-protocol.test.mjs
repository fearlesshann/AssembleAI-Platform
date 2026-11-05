import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createAnswerStream, contextIdentity } from '../../apps/knowledge/frontend/src/protocol.ts';
import { parseDocuments, parseAccess, createKnowledgeLibrary } from '../../apps/knowledge/frontend/src/library.ts';

const tenant = '11111111-1111-4111-8111-111111111111';
const documentId = '22222222-2222-4222-8222-222222222222';
const organization = '33333333-3333-4333-8333-333333333333';
const chunkId = `${documentId.replaceAll('-', '')}-1-0-0`;
const profile = 'e5-small-dd476dd0-384-v1';
const result = () => ({ requestId: 'a'.repeat(32), modelRequestId: 'b'.repeat(32), searchRequestId: 'c'.repeat(32),
  answer: '停机后检查夹具。', refusal: false, profile, authorizationVersion: '9007199254740993', documents: [{ documentId, revision: 1 }],
  citations: [{ documentId, documentRevision: 1, chunkId, fileName: '制造业测试SOP.docx', locator: 'paragraph:2', quote: '停机后检查夹具。' }] });
const stage = (value) => ({ event: 'delta', data: { kind: 'stage', stage: value } });
const ready = () => { const stream = createAnswerStream(); for (const value of ['retrieving', 'answering', 'verifying']) stream.accept(stage(value)); return stream; };
const document = () => ({ tenantId: tenant, documentId, fileName: '制造业测试SOP.docx', mediaType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', sizeBytes: 256,
  sha256: 'd'.repeat(64), organizationId: organization, status: 'ready', revision: 1, createdBy: tenant, createdAt: '2026-11-05T00:00:00Z', failureCode: null, indexProfile: profile });

test('T12 V22: answer stays provisional and preserves authorization version exactly', () => {
  const stream = ready(); assert.throws(() => stream.finish());
  const original = result(); stream.accept({ event: 'complete', data: original }); original.citations[0].quote = 'changed';
  assert.equal(stream.finish().citations[0].quote, '停机后检查夹具。');
  assert.equal(stream.finish().authorizationVersion, '9007199254740993');
  assert.throws(() => stream.accept(stage('verifying')));
});
test('T12 V22: empty evidence can refuse without invoking a model', () => {
  const stream = createAnswerStream(); stream.accept(stage('retrieving')); stream.accept(stage('verifying'));
  stream.accept({ event: 'complete', data: { ...result(), modelRequestId: null, answer: '现有文档没有充分依据。', refusal: true, citations: [], documents: [] } });
  assert.equal(stream.finish().refusal, true);
});
test('T12 V22/V23: malformed completion, wrong revision, forged paths and unknown fields fail closed', () => {
  for (const patch of [
    { authorizationVersion: 9007199254740992 }, { authorizationVersion: '01' }, { authorizationVersion: '9223372036854775808' },
    { profile: 'other' }, { documents: [] }, { citations: [] }, { modelRequestId: null },
    { citations: [{ ...result().citations[0], documentRevision: 2 }] },
    { citations: [{ ...result().citations[0], chunkId: '../../private' }] },
    { citations: [result().citations[0], result().citations[0]] }, { secret: 'not-allowed' },
  ]) { const stream = ready(); assert.throws(() => stream.accept({ event: 'complete', data: { ...result(), ...patch } })); assert.throws(() => stream.finish()); }
  for (const value of ['verifying', 'unknown']) assert.throws(() => createAnswerStream().accept(stage(value)));
  const interrupted = ready(); assert.throws(() => interrupted.finish());
});
test('T12: document list is bounded, scoped and copied; extracted remains distinct from ready', () => {
  const item = document(); const parsed = parseDocuments({ items: [item], limit: 100 }, tenant); item.fileName = 'changed';
  assert.equal(parsed[0].fileName, '制造业测试SOP.docx');
  assert.equal(parseDocuments({ items: [{ ...document(), status: 'extracted', indexProfile: null }], limit: 100 }, tenant)[0].status, 'extracted');
  for (const value of [{ items: [document(), document()], limit: 100 }, { items: [{ ...document(), tenantId: organization }], limit: 100 },
    { items: [{ ...document(), status: 'deleted' }], limit: 100 }, { items: [{ ...document(), revision: Number.MAX_SAFE_INTEGER + 1 }], limit: 100 }]) assert.throws(() => parseDocuments(value, tenant));
});
test('T12: UI management hints keep each grant resource and organization together', () => {
  const access = parseAccess({ userId: tenant, tenantId: tenant, authorizationVersion: 1, grants: [
    { permission: 'knowledge:manage', resourceType: 'document', resourceId: documentId.replaceAll('-', ''), tenantWide: false, organizationIds: [organization] },
    { permission: 'knowledge:read', resourceType: 'knowledge', resourceId: 'documents', tenantWide: true, organizationIds: [] },
  ] }, tenant);
  assert.deepEqual(access.uploadScopes, []); assert.equal(access.canManage(document()), true);
  assert.equal(access.canManage({ ...document(), organizationId: null }), false);
  assert.equal(access.canManage({ ...document(), documentId: tenant }), false);
});
test('T12: BFF upload uses fixed tenant path, raw bytes and CSRF; oversize never fetches', async () => {
  const calls = [];
  const api = createKnowledgeLibrary(async (path, init) => {
    calls.push({ path, init });
    if (path === '/bff/csrf') return Response.json({ headerName: 'X-CSRF-TOKEN', requestToken: 'fixture-token' });
    return Response.json(document(), { status: 202 });
  });
  const signal = new AbortController().signal;
  await api.upload(tenant, new File(['test'], '测试.docx'), organization, signal);
  assert.equal(calls.length, 2); assert.equal(calls[1].init.method, 'POST');
  assert.equal(calls[1].init.headers['X-CSRF-TOKEN'], 'fixture-token');
  assert.equal(calls[1].init.headers['Content-Type'], 'application/octet-stream');
  assert.equal(calls[1].init.credentials, 'same-origin');
  assert.equal(calls[1].path, `/api/platform/v1/tenants/${tenant}/knowledge/documents?filename=${encodeURIComponent('测试.docx')}&organizationId=${organization}`);
  await assert.rejects(api.upload(tenant, new File([new Uint8Array(5 * 1024 * 1024 + 1)], 'large.pdf'), organization, signal));
  assert.equal(calls.length, 2);
});
test('T12 V25: theme changes do not invalidate identity while tenant changes do', () => {
  const context = { appId: 'knowledge', releaseVersion: '0.1.0', mode: 'embedded', theme: 'light', tenantId: tenant, identity: { iss: 'fixture', sub: 'alice' } };
  assert.equal(contextIdentity(context), contextIdentity({ ...context, theme: 'dark' }));
  assert.notEqual(contextIdentity(context), contextIdentity({ ...context, tenantId: organization }));
});
