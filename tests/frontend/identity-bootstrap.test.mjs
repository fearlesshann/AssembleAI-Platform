import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bootstrapInput } from '../../tools/bootstrap-identity-admin.mjs';

test('T13: bootstrap carries Unicode and quotes as ASCII data without SQL or psql injection', () => {
  const name = "制造管理员 ' \\gexec";
  const subject = "account'); SELECT current_user; --";
  const input = bootstrapInput({ 'user-id': '11111111-1111-4111-8111-111111111111',
    issuer: 'https://identity.example/realm', subject, 'display-name': name });
  assert.match(input, /^[\x00-\x7f]+$/);
  assert.equal(input.includes(subject), false);
  assert.equal(input.includes(name), false);
  const values = [...input.matchAll(/decode\('([A-Za-z0-9+/=]+)'/g)].map(match => Buffer.from(match[1], 'base64').toString('utf8'));
  assert.equal(values[2], subject);
  assert.equal(values[3], name);
  assert.equal(input.split('\\gset').length, 2);
});

test('T13: invalid bootstrap identity fails before starting a database process', () => {
  const options = { 'user-id': '11111111-1111-4111-8111-111111111111', issuer: 'https://identity.example/realm', subject: 'account', 'display-name': '管理员' };
  for (const change of [{ 'user-id': '00000000-0000-0000-0000-000000000000' }, { subject: 'account\n\\gexec' },
    { issuer: 'https://user:secret@identity.example/' }, { 'display-name': ' ' }])
    assert.throws(() => bootstrapInput({ ...options, ...change }));
});
