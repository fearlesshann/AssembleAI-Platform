import type { IdentityGrant, IdentityOrganization, PermissionResource, PermissionReference } from './identity-api';

export const permissionCategories = [
  { id: 'applications', name: '应用访问' }, { id: 'models', name: '模型服务' }, { id: 'data', name: '业务数据' },
  { id: 'knowledge', name: '知识文档' }, { id: 'tools', name: '平台工具' }, { id: 'audit', name: '审计治理' }, { id: 'legacy', name: '已有自定义权限' },
];
export const resourceKey = (value: Pick<IdentityGrant, 'resourceType' | 'resourceId'>) => JSON.stringify([value.resourceType, value.resourceId]);
export const grantKey = (value: PermissionReference) => JSON.stringify([value.resourceType, value.resourceId, value.permission]);
export const scopeKey = (value: Pick<IdentityGrant, 'tenantWide' | 'organizationIds'>) => value.tenantWide ? 'tenant' : JSON.stringify([...value.organizationIds].sort());
export const copyGrants = (grants: IdentityGrant[]) => grants.map(grant => ({ ...grant, organizationIds: [...grant.organizationIds] }));

export function permissionResources(catalog: PermissionResource[], grants: IdentityGrant[]) {
  const result = catalog.map(resource => ({ ...resource, actions: [...resource.actions] }));
  for (const grant of grants) {
    let resource = result.find(item => resourceKey(item) === resourceKey(grant));
    if (!resource) {
      resource = { resourceType: grant.resourceType, resourceId: grant.resourceId, name: grant.resourceId, category: 'legacy', status: '未登记资源说明', legacy: true, actions: [] };
      result.push(resource);
    }
    if (!resource.actions.some(action => action.permission === grant.permission)) resource.actions.push({
      permission: grant.permission, name: grant.permission, description: '已有自定义权限，保留原配置。接入方尚未提供业务说明。', organizationScope: true, requires: [], legacy: true,
    });
  }
  return result;
}
export function grantGroups(grants: IdentityGrant[]) {
  const groups = new Map<string, { id: string; resourceType: string; resourceId: string; grants: IdentityGrant[]; tenantWide: boolean; organizationIds: string[] }>();
  for (const grant of grants) {
    const id = JSON.stringify([resourceKey(grant), scopeKey(grant)]);
    if (!groups.has(id)) groups.set(id, { id, resourceType: grant.resourceType, resourceId: grant.resourceId, grants: [], tenantWide: grant.tenantWide, organizationIds: [...grant.organizationIds] });
    groups.get(id)!.grants.push(grant);
  }
  return [...groups.values()];
}
export function scopeLabel(grant: Pick<IdentityGrant, 'tenantWide' | 'organizationIds'>, organizations: IdentityOrganization[]) {
  if (grant.tenantWide) return '整个租户';
  if (!grant.organizationIds.length) return '尚未选择组织';
  return grant.organizationIds.map(id => { const org = organizations.find(value => value.organizationId === id); return org ? org.name + (org.enabled ? '' : '（已停用）') : '未登记组织'; }).join('、');
}
export function grantChanges(before: IdentityGrant[], after: IdentityGrant[]) {
  const previous = new Map(before.map(grant => [grantKey(grant), grant]));
  const next = new Map(after.map(grant => [grantKey(grant), grant]));
  const changes: { kind: 'added' | 'removed' | 'scope'; before?: IdentityGrant; after?: IdentityGrant }[] = [];
  for (const [key, grant] of next) {
    const old = previous.get(key);
    if (!old) changes.push({ kind: 'added', after: grant });
    else if (scopeKey(old) !== scopeKey(grant)) changes.push({ kind: 'scope', before: old, after: grant });
  }
  for (const [key, grant] of previous) if (!next.has(key)) changes.push({ kind: 'removed', before: grant });
  return changes;
}
export function missingRequirements(grants: IdentityGrant[], resources: PermissionResource[]) {
  const missing: { grant: IdentityGrant; requirement: PermissionReference }[] = [];
  for (const grant of grants) {
    const action = resources.find(resource => resourceKey(resource) === resourceKey(grant))?.actions.find(item => item.permission === grant.permission);
    for (const requirement of action?.requires ?? []) {
      const actual = grants.find(item => grantKey(item) === grantKey(requirement));
      if (!actual || (!actual.tenantWide && (grant.tenantWide || grant.organizationIds.some(id => !actual.organizationIds.includes(id))))) missing.push({ grant, requirement });
    }
  }
  return missing;
}
/** No implicit unions: an existing exact action with a different scope needs an explicit edit. */
export function replaceGrantGroup(grants: IdentityGrant[], editingKeys: string[], additions: IdentityGrant[]) {
  const keep = grants.filter(grant => !editingKeys.includes(grantKey(grant)));
  if (additions.some(grant => keep.some(other => grantKey(other) === grantKey(grant) && scopeKey(other) !== scopeKey(grant)))) return { error: '其中一项能力已在其他范围授权，请从已授权清单编辑其范围。', grants };
  const result = copyGrants(keep);
  for (const grant of additions) if (!result.some(other => grantKey(other) === grantKey(grant))) result.push({ ...grant, organizationIds: [...grant.organizationIds] });
  if (result.length > 100) return { error: '每个角色最多分配100项权限，请先移除不需要的权限。', grants };
  return { error: '', grants: result };
}
