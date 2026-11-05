import { test } from 'node:test';
import assert from 'node:assert/strict';
import { grantChanges, grantGroups, grantKey, missingRequirements, permissionResources, replaceGrantGroup } from '../../apps/portal/src/role-permissions.ts';
const grant = (permission, ids = ['factory-a']) => ({ permission, resourceType: 'knowledge', resourceId: 'documents', tenantWide: false, organizationIds: ids });

test('V38: grouping and no-op comparisons ignore organization order without widening any scope', () => {
  const read = grant('knowledge:read', ['factory-a', 'factory-b']);
  const manage = grant('knowledge:manage', ['factory-a']);
  assert.equal(grantGroups([read, manage]).length, 2);
  assert.deepEqual(grantChanges([read], [{ ...read, organizationIds: ['factory-b', 'factory-a'] }]), []);
  assert.ok(replaceGrantGroup([manage], [], [grant('knowledge:manage', ['factory-b'])]).error);
  const replacement = replaceGrantGroup([read, manage], [grantKey(manage)], [{ ...manage, tenantWide: true, organizationIds: [] }]);
  assert.equal(replacement.error, '');
  assert.deepEqual(replacement.grants[0], read);
  assert.equal(grantChanges([read, manage], replacement.grants)[0].kind, 'scope');
  assert.deepEqual(manage.organizationIds, ['factory-a']);
});
test('V38: unknown existing grants survive catalog enrichment and unrelated edits exactly', () => {
  const legacy = { ...grant('custom:read'), resourceType: 'machine', resourceId: 'machine-7' };
  const resources = permissionResources([], [legacy]);
  assert.equal(resources[0].legacy, true); assert.equal(resources[0].actions[0].legacy, true);
  const result = replaceGrantGroup([legacy], [], [grant('knowledge:read')]);
  assert.deepEqual(result.grants[0], legacy);
  result.grants[0].organizationIds.push('different');
  assert.deepEqual(legacy.organizationIds, ['factory-a']);
});
test('V38: dependency coverage requires every organization and tenant-wide callers require tenant-wide dependencies', () => {
  const read = grant('knowledge:read', ['factory-a', 'factory-b']);
  const requirement = { permission: 'tool:invoke', resourceType: 'tool', resourceId: 'query' };
  const resources = [{ resourceType: 'knowledge', resourceId: 'documents', actions: [{ permission: read.permission, requires: [requirement] }] }];
  const tool = { ...requirement, tenantWide: false, organizationIds: ['factory-a'] };
  assert.equal(missingRequirements([read, tool], resources).length, 1);
  assert.equal(missingRequirements([read, { ...tool, organizationIds: ['factory-a', 'factory-b'] }], resources).length, 0);
  assert.equal(missingRequirements([{ ...read, tenantWide: true, organizationIds: [] }, tool], resources).length, 1);
  assert.equal(missingRequirements([read, { ...tool, tenantWide: true, organizationIds: [] }], resources).length, 0);
});
test('V38: review distinguishes remove, add and scope change and the 100-grant limit never truncates', () => {
  const before = [grant('knowledge:read'), grant('knowledge:manage')];
  const after = [{ ...before[0], tenantWide: true, organizationIds: [] }, grant('custom:new')];
  assert.deepEqual(grantChanges(before, after).map(change => change.kind), ['scope', 'added', 'removed']);
  const hundred = Array.from({ length: 100 }, (_, index) => grant(`permission:${index}`));
  const result = replaceGrantGroup(hundred, [], [grant('extra')]);
  assert.ok(result.error); assert.equal(result.grants, hundred);
});
