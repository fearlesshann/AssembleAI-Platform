import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseDatasets, createResultStream, contextIdentity } from '../../apps/chatbi/frontend/src/protocol.ts';

const dataset = { sourceId: 'manufacturing', datasetId: 'production', displayName: '每日生产（测试）', columns: [{ name: 'line', type: 'text' }, { name: 'produced', type: 'integer' }], maxRows: 100 };
const complete = () => ({ requestId: 'a'.repeat(32), modelRequestId: 'b'.repeat(32), queryRequestId: 'c'.repeat(32), sourceId: dataset.sourceId, datasetId: dataset.datasetId, datasetName: dataset.displayName, columns: ['line', 'metric0'], rowCount: 1, queryText: '{"columns":["line"],"metrics":[{"function":"sum","column":"produced"}],"limit":100}', scope: '当前授权工厂', summary: '已查询当前授权工厂。' });
const stage = (value) => ({ event: 'delta', data: { kind: 'stage', stage: value } });
const ready = () => { const stream = createResultStream(dataset); for (const value of ['planning', 'querying', 'receiving']) stream.accept(stage(value)); return stream; };

test('T11: catalog accepts only bounded public aliases and copies the trusted shape', () => {
  const original = structuredClone(dataset);
  const parsed = parseDatasets({ items: [original] });
  original.columns[0].name = 'changed';
  assert.equal(parsed[0].columns[0].name, 'line');
  assert.deepEqual(parseDatasets({ items: [] }), []);
  for (const value of [null, { items: [], password: 'hidden' }, { items: [dataset, dataset] }, { items: [{ ...dataset, connection: 'hidden' }] }, { items: [{ ...dataset, maxRows: 501 }] }, { items: [{ ...dataset, columns: [{ name: 'raw;sql', type: 'text' }] }] }]) assert.throws(() => parseDatasets(value));
});

test('T11: rows remain provisional until complete matches attribution and every row width', () => {
  const stream = ready();
  assert.equal(stream.accept({ event: 'delta', data: { kind: 'rows', rows: [['<img src=x onerror=alert(1)>', '9007199254740993.123456789']] } }).kind, 'rows');
  assert.throws(() => stream.finish());
  stream.accept({ event: 'complete', data: complete() });
  const result = stream.finish();
  assert.equal(result.rows[0][1], '9007199254740993.123456789');
  assert.equal(result.rows[0][0], '<img src=x onerror=alert(1)>');
  assert.equal(result.queryText, complete().queryText);
  assert.throws(() => stream.accept(stage('receiving')));
});

test('T11: empty completed result and nullable cells preserve their meaning', () => {
  const stream = ready(); stream.accept({ event: 'complete', data: { ...complete(), rowCount: 0 } });
  assert.deepEqual(stream.finish().rows, []);
  const nullable = ready(); nullable.accept({ event: 'delta', data: { kind: 'rows', rows: [[null, '0']] } }); nullable.accept({ event: 'complete', data: complete() });
  assert.deepEqual(nullable.finish().rows, [[null, '0']]);
});

test('T11: bad event kinds, order, payloads, limits and completion fail closed', () => {
  for (const event of [
    { event: 'unknown', data: {} }, { event: 'error', data: { code: 'private-detail' } },
    { event: 'delta', data: { kind: 'stage', stage: 'executing-sql' } },
    { event: 'delta', data: { kind: 'rows', rows: [[1, '2']] } },
    { event: 'delta', data: { kind: 'rows', rows: [['x'.repeat(4097)]] } },
    { event: 'delta', data: { kind: 'rows', rows: Array.from({ length: 101 }, () => ['1']) } },
    { event: 'delta', data: { kind: 'rows', rows: [Array(25).fill('1')] } },
    { event: 'delta', data: { kind: 'rows', rows: [Array(24).fill('x'.repeat(4096))] } },
    { event: 'delta', data: { kind: 'rows', rows: [], unknown: true } },
  ]) assert.throws(() => ready().accept(event));
  assert.throws(() => createResultStream(dataset).accept(stage('receiving')));
  for (const change of [{ rowCount: 0 }, { columns: ['line'] }, { columns: ['line', 'line'] }, { datasetId: 'other' }, { sourceId: 'other' }, { scope: '全部工厂' }, { queryText: null }, { queryText: 'x'.repeat(16385) }, { token: 'private' }]) {
    const stream = ready(); stream.accept({ event: 'delta', data: { kind: 'rows', rows: [['line-a', '300']] } });
    assert.throws(() => stream.accept({ event: 'complete', data: { ...complete(), ...change } }));
    assert.throws(() => stream.finish());
  }
});

test('T11: source display context invalidates identity changes but keeps theme changes separate', () => {
  const context = { appId: 'chatbi', releaseVersion: '0.1.0', mode: 'embedded', theme: 'light', tenantId: 'one', identity: { iss: 'issuer', sub: 'alice' } };
  assert.equal(contextIdentity(context), contextIdentity({ ...context, theme: 'dark' }));
  assert.notEqual(contextIdentity(context), contextIdentity({ ...context, tenantId: 'two' }));
  assert.notEqual(contextIdentity(context), contextIdentity({ ...context, identity: { iss: 'issuer', sub: 'bob' } }));
});
