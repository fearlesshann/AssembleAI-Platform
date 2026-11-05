<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { vReferenceContent } from './components/reference/reference-motion';
import { ReferenceButton, ReferenceInput, ReferenceSelect } from './components/reference';
import type { IdentityGrant, IdentityMember, IdentityOrganization, IdentityRole, PermissionResource } from './identity-api';
import { copyGrants, grantChanges, grantGroups, grantKey, missingRequirements, permissionCategories, permissionResources, replaceGrantGroup, resourceKey, scopeLabel } from './role-permissions';
import './role-permissions.css';

const props = defineProps<{
  role?: IdentityRole; draft: { id: string; revision: number; name: string; enabled: boolean; grants: IdentityGrant[] } | null;
  resources: PermissionResource[]; organizations: IdentityOrganization[]; members: IdentityMember[];
  loading: boolean; writing: boolean; stale: boolean; catalogError: string; canManage: boolean;
}>();
const emit = defineEmits<{ edit: []; save: []; cancel: []; back: []; retry: []; dirty: [boolean]; grants: [IdentityGrant[]] }>();
const query = ref(''); const category = ref(''); const selectedResource = ref('');
const permissions = ref<string[]>([]); const scope = ref(''); const organizationIds = ref<string[]>([]); const organizationQuery = ref('');
const editingKeys = ref<string[]>([]); const composerTouched = ref(false); const message = ref(''); const review = ref(false);
const grants = computed(() => props.draft?.grants ?? props.role?.grants ?? []);
const baseline = computed(() => props.draft?.revision ? props.role?.grants ?? [] : []);
const resources = computed(() => permissionResources(props.resources, [...baseline.value, ...grants.value]));
const filtered = computed(() => resources.value.filter(resource => (!category.value || resource.category === category.value)
  && `${resource.name} ${resource.resourceId} ${resource.actions.map(action => `${action.name} ${action.description} ${action.permission}`).join(' ')}`.toLocaleLowerCase().includes(query.value.trim().toLocaleLowerCase())));
const categories = computed(() => permissionCategories.filter(item => resources.value.some(resource => resource.category === item.id)));
const selected = computed(() => resources.value.find(resource => resourceKey(resource) === selectedResource.value));
const groups = computed(() => grantGroups(grants.value));
const changes = computed(() => grantChanges(baseline.value, grants.value));
const assignedMembers = computed(() => props.members.filter(member => member.roleIds.includes(props.role?.roleId ?? props.draft?.id ?? '')));
const changed = computed(() => !!props.draft && (!props.draft.revision || props.draft.name.trim() !== props.role?.name || props.draft.enabled !== props.role?.enabled || changes.value.length > 0));
const locked = computed(() => props.loading || props.writing || props.stale || !props.canManage);
const availableOrganizations = computed(() => props.organizations.filter(org => `${org.name} ${org.kind}`.toLocaleLowerCase().includes(organizationQuery.value.trim().toLocaleLowerCase())));
const selectedActions = computed(() => selected.value?.actions.filter(action => permissions.value.includes(action.permission)) ?? []);
const tenantOnly = computed(() => selectedActions.value.some(action => !action.organizationScope));
const additions = computed<IdentityGrant[]>(() => selected.value ? permissions.value.map(permission => ({ permission,
  resourceType: selected.value!.resourceType, resourceId: selected.value!.resourceId, tenantWide: scope.value === 'tenant', organizationIds: scope.value === 'organizations' ? [...organizationIds.value] : [] })) : []);
const proposal = computed(() => replaceGrantGroup(grants.value, editingKeys.value, additions.value));
const dependencies = computed(() => missingRequirements(grants.value, resources.value));
const incompatibleScopes = computed(() => grants.value.filter(grant => !grant.tenantWide && resources.value.find(resource => resourceKey(resource) === resourceKey(grant))?.actions.some(action => action.permission === grant.permission && !action.organizationScope)));
const compositionError = computed(() => !permissions.value.length ? '选择要授予的能力。' : !scope.value ? '明确选择生效范围。'
  : scope.value === 'organizations' && tenantOnly.value ? '所选能力只支持整个租户，不支持按组织分配。'
  : scope.value === 'organizations' && !organizationIds.value.length ? '至少选择一个组织。'
  : organizationIds.value.length > 100 ? '一次最多选择100个组织。'
  : scope.value === 'organizations' && organizationIds.value.some(id => !props.organizations.find(org => org.organizationId === id)?.enabled) ? '已停用或不存在的组织不能继续授权，请取消选择。'
  : proposal.value.error);
const invalidGrants = computed(() => grants.value.some(grant => !grant.tenantWide && (!grant.organizationIds.length || grant.organizationIds.some(id => !props.organizations.find(org => org.organizationId === id)?.enabled))));
const oversized = computed(() => !!props.draft && new TextEncoder().encode(JSON.stringify({ expectedRevision: props.draft.revision, name: props.draft.name.trim(), enabled: props.draft.enabled, grants: grants.value })).length > 65536);
const ready = computed(() => changed.value && !!props.draft?.name.trim() && !locked.value && !invalidGrants.value && !oversized.value && !composerTouched.value);
const resourceName = (value: { resourceType: string; resourceId: string }) => resources.value.find(resource => resourceKey(resource) === resourceKey(value))?.name ?? value.resourceId;
const actionName = (value: { permission: string; resourceType: string; resourceId: string }) => resources.value.find(resource => resourceKey(resource) === resourceKey(value))?.actions.find(action => action.permission === value.permission)?.name ?? value.permission;
const scopeText = (value: Pick<IdentityGrant, 'tenantWide' | 'organizationIds'>) => scopeLabel(value, props.organizations);
function resetComposer() { permissions.value = []; scope.value = ''; organizationIds.value = []; editingKeys.value = []; composerTouched.value = false; message.value = ''; }
function select(resource: PermissionResource) {
  if (composerTouched.value) { message.value = '当前授权尚未加入清单，请先加入或取消本次配置，再切换资源。'; return; }
  resetComposer(); selectedResource.value = resourceKey(resource);
}
function editGroup(group: typeof groups.value[number]) {
  if (composerTouched.value) { message.value = '请先完成或取消当前配置。'; return; }
  selectedResource.value = resourceKey(group); category.value = ''; query.value = '';
  editingKeys.value = group.grants.map(grantKey); permissions.value = group.grants.map(grant => grant.permission);
  scope.value = group.tenantWide ? 'tenant' : 'organizations'; organizationIds.value = [...group.organizationIds];
  composerTouched.value = false; message.value = ''; review.value = false;
}
function touch() { composerTouched.value = true; message.value = ''; }
function changeScope(value: string) { scope.value = value; touch(); }
function apply() {
  if (locked.value || compositionError.value || !props.draft) return;
  emit('grants', proposal.value.grants); resetComposer();
}
function remove(group: typeof groups.value[number]) {
  if (locked.value) return;
  const keys = group.grants.map(grantKey);
  emit('grants', grants.value.filter(grant => !keys.includes(grantKey(grant))));
  if (editingKeys.value.some(key => keys.includes(key))) resetComposer();
}
function undoChange(change: typeof changes.value[number]) {
  if (locked.value) return;
  const key = grantKey((change.after ?? change.before)!);
  const next = grants.value.filter(grant => grantKey(grant) !== key);
  if (change.before) next.push(...copyGrants([change.before]));
  emit('grants', next); resetComposer();
}
function completeDependencies() {
  if (locked.value || !props.draft) return;
  const next = copyGrants(grants.value);
  for (const { grant, requirement } of dependencies.value) {
    // Never silently widen an existing grant or merge scopes from different capabilities.
    if (!next.some(item => grantKey(item) === grantKey(requirement))) next.push({ ...requirement, tenantWide: grant.tenantWide, organizationIds: [...grant.organizationIds] });
  }
  if (next.length > 100) { message.value = '补齐后超过100项权限，请先减少授权。'; return; }
  emit('grants', next);
}
const body = ref<HTMLElement>();
watch(review, async () => { await nextTick(); body.value?.querySelector<HTMLElement>('.role-config, .role-review')?.scrollTo({ top: 0 }); });
function inspect() { if (ready.value) { review.value = true; resetComposer(); } }
watch(() => props.draft, () => { resetComposer(); review.value = false; });
watch([changed, composerTouched], () => emit('dirty', changed.value || composerTouched.value));
watch(resources, () => { if (!selected.value && resources.value.length) selectedResource.value = resourceKey(resources.value[0]); }, { immediate: true });
</script>

<template>
  <section class="role-workspace" aria-label="角色授权工作区" :aria-busy="loading || writing">
    <header class="role-heading">
      <ReferenceButton variant="link" :disabled="writing" @click="emit('back')">← 角色列表</ReferenceButton>
      <div class="role-heading-title"><h2>{{ draft ? (draft.revision ? draft.name || '编辑角色' : '新增角色') : role?.name || '角色详情' }}</h2><span v-if="role && !draft" class="status-pill" :class="{ draft: !role.enabled }"><i class="sdot"/>{{ role.enabled ? '启用' : '停用' }}</span></div>
      <p>{{ grants.length }} 项权限 · {{ new Set(grants.map(resourceKey)).size }} 个资源 · {{ assignedMembers.length }} 名关联成员</p>
      <ReferenceButton v-if="role && !draft" primary :disabled="locked" @click="emit('edit')">编辑权限</ReferenceButton>
    </header>

    <template v-if="draft || role">
      <div v-if="draft" class="role-basics">
        <label class="subwindow-field">角色名称<ReferenceInput v-model="draft.name" :disabled="locked" maxlength="128" placeholder="例如：装配一厂质检员" /></label>
        <label class="subwindow-check"><input v-model="draft.enabled" type="checkbox" :disabled="locked" />启用角色</label>
        <p class="role-helper">角色决定能做什么；在成员管理中分配角色后生效。</p>
      </div>
      <div v-if="draft" class="filter-row role-tabs" aria-label="角色编辑步骤"><button type="button" class="filter-chip" :class="{ 'is-active': !review }" :aria-pressed="!review" @click="review = false">权限配置</button><button type="button" class="filter-chip" :class="{ 'is-active': review }" :aria-pressed="review" :disabled="!ready" @click="inspect">检查变更 <span>{{ changes.length }}</span></button></div>

      <div ref="body" class="role-body" :class="{ 'is-readonly': !draft, 'is-review': review }">
        <section v-if="draft && !review" v-reference-content="loading" class="role-config" aria-label="选择资源与能力">
          <div class="role-config-head"><h3>配置权限</h3><span class="role-helper">选择资源 → 勾选能力 → 确定范围</span></div>
          <div v-if="catalogError" class="role-feedback" role="alert">{{ catalogError }}<ReferenceButton :disabled="locked" @click="emit('retry')">重新读取</ReferenceButton></div>
          <div class="role-search"><label class="data-grid__search"><svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true"><circle cx="7" cy="7" r="4.5" stroke="currentColor" stroke-width="1.3"/><path d="m10.5 10.5 3 3" stroke="currentColor" stroke-width="1.3"/></svg><input v-model="query" type="search" aria-label="搜索资源或能力" placeholder="搜索资源名称或能力…" /></label></div>
          <div class="filter-row role-category-list" aria-label="资源分类"><button type="button" class="filter-chip" :class="{ 'is-active': !category }" :aria-pressed="!category" @click="category = ''">全部</button><button v-for="item in categories" :key="item.id" type="button" class="filter-chip" :class="{ 'is-active': category === item.id }" :aria-pressed="category === item.id" @click="category = item.id">{{ item.name }}</button></div>
          <div class="role-picker">
            <nav class="role-resource-list app-list" aria-label="可授权资源">
              <div class="scroll-content"><button v-for="resource in filtered" :key="resourceKey(resource)" type="button" class="app-row role-resource" :class="{ selected: selectedResource === resourceKey(resource) }" :aria-pressed="selectedResource === resourceKey(resource)" :aria-label="`${resource.name} · ${resource.resourceType}`" @click="select(resource)"><span class="app-icon" style="--ic:var(--accent)" aria-hidden="true">{{ resource.name.slice(0, 1) }}</span><span class="app-row-title">{{ resource.name }}</span><span v-if="grants.some(grant => resourceKey(grant) === resourceKey(resource))" class="role-assigned-dot" aria-label="已有授权">✓</span></button></div>
              <p v-if="!filtered.length" class="role-empty">没有匹配的资源，试试其他名称或分类。</p>
            </nav>
            <section v-if="selected" class="role-capabilities" aria-label="资源能力配置">
              <header><h3>{{ selected.name }}</h3><p class="role-helper">{{ selected.status }}</p></header>
              <fieldset :disabled="locked" class="role-capability-options"><legend class="sr-only">选择能力</legend>
                <div v-for="action in selected.actions" :key="action.permission" class="role-capability"><label class="subwindow-check"><input v-model="permissions" type="checkbox" :value="action.permission" :aria-label="action.name" @change="touch" /><strong>{{ action.name }}</strong><span v-if="grants.some(grant => resourceKey(grant) === selectedResource && grant.permission === action.permission)" class="role-helper">已授权</span></label><p class="role-helper">{{ action.description }}</p></div>
              </fieldset>
              <section class="role-scope" aria-label="生效范围">
                <label class="subwindow-field">生效范围<ReferenceSelect :model-value="scope" label="生效范围" :disabled="locked" :options="[{ value: '', label: '请选择生效范围' }, { value: 'tenant', label: '整个租户' }, { value: 'organizations', label: '指定组织', disabled: tenantOnly }]" @update:model-value="changeScope" /></label>
                <p class="role-helper">{{ tenantOnly ? '所选能力是租户级操作，不支持限定到工厂或部门。' : '按实际组织授予访问，不会自动包含其他组织。' }}</p>
                <template v-if="scope === 'organizations'">
                  <ReferenceInput v-model="organizationQuery" type="search" aria-label="搜索组织" placeholder="搜索工厂、部门…" />
                  <div class="role-organization-list"><label v-for="organization in availableOrganizations" :key="organization.organizationId" class="subwindow-check"><input v-model="organizationIds" type="checkbox" :value="organization.organizationId" :disabled="locked || !organization.enabled && !organizationIds.includes(organization.organizationId)" @change="touch" />{{ organization.name }}{{ organization.enabled ? '' : '（已停用）' }}</label><p v-if="!availableOrganizations.length" class="role-helper">没有可选组织，请调整搜索或先在组织管理中建立组织。</p></div>
                  <p class="role-helper">已选择 {{ organizationIds.length }} 个组织，搜索不会清除已有选择。</p>
                </template>
                <p v-if="scope === 'tenant'" class="role-helper">作用于整个租户；对于组织数据，也包括后续启用的组织。</p>
              </section>
              <details class="role-technical"><summary>技术标识</summary><code>{{ selected.resourceType }} / {{ selected.resourceId }}</code><code v-for="action in selected.actions" :key="action.permission">{{ action.permission }}</code></details>
              <div class="role-composer-foot"><p v-if="message" role="alert" class="role-validation">{{ message }}</p><p v-else class="role-helper">{{ compositionError || `本次将${editingKeys.length ? '更新' : '加入'} ${permissions.length} 项权限` }}</p><div><ReferenceButton :disabled="locked" @click="resetComposer">取消本次配置</ReferenceButton><ReferenceButton primary :disabled="locked || !!compositionError" @click="apply">{{ editingKeys.length ? '更新授权范围' : '加入授权清单' }}</ReferenceButton></div></div>
            </section>
          </div>
        </section>

        <section v-else-if="review" v-reference-content="false" class="role-review" aria-label="权限变更检查">
          <h3>确认这次调整</h3><p class="role-helper">{{ assignedMembers.length ? `保存后将作用于使用此角色的 ${assignedMembers.length} 名成员。成员的其他角色权限仍会叠加。` : '此角色尚未分配给成员；保存后可到租户成员管理中分配。' }}</p>
          <div class="role-change-counts"><span>新增 {{ changes.filter(change => change.kind === 'added').length }}</span><span>移除 {{ changes.filter(change => change.kind === 'removed').length }}</span><span>范围调整 {{ changes.filter(change => change.kind === 'scope').length }}</span></div>
          <div v-if="draft?.name.trim() !== role?.name" class="role-metadata-change">角色名称：{{ role?.name || '新角色' }} → {{ draft?.name.trim() }}</div>
          <div v-if="draft?.enabled !== role?.enabled" class="role-metadata-change">保存后状态：{{ draft?.enabled ? '启用' : '停用，成员不再通过此角色获得权限' }}</div>
          <div v-if="changes.length" class="data-grid role-change-grid"><div class="data-grid__body"><div class="data-grid__scroll"><table class="data-grid__table" aria-label="本次权限变更"><thead><tr><th>变更</th><th>资源与能力</th><th>生效范围</th><th class="data-grid__actions">操作</th></tr></thead><tbody><tr v-for="change in changes" :key="grantKey((change.after || change.before)!)"><td>{{ { added: '新增', removed: '移除', scope: '调整范围' }[change.kind] }}</td><td><strong>{{ resourceName((change.after || change.before)!) }}</strong><small>{{ actionName((change.after || change.before)!) }}</small></td><td><span v-if="change.before" :class="{ 'role-old-scope': !!change.after }">{{ scopeText(change.before) }}</span><span v-if="change.before && change.after" aria-hidden="true"> → </span><span v-if="change.after">{{ scopeText(change.after) }}</span></td><td class="data-grid__actions"><ReferenceButton variant="link" :disabled="locked" @click="undoChange(change)">撤销此项</ReferenceButton></td></tr></tbody></table></div></div></div>
          <p v-else class="role-helper">本次未更改业务权限。</p>
        </section>

        <section class="role-assignment-summary" aria-label="已授权清单">
          <header><h3>已授权清单</h3><span class="role-helper">{{ grants.length }} / 100 项</span></header>
          <div v-if="incompatibleScopes.length" class="role-feedback" role="status">{{ incompatibleScopes.length }} 项已有授权限定了组织，但对应能力只接受整个租户授权。请检查并明确调整范围；系统不会自动扩大。</div>
          <p v-if="!grants.length" class="role-empty">尚未授予业务权限。选择资源、能力与范围后，授权结果会显示在这里。</p>
          <article v-for="group in groups" :key="group.id" class="role-assignment"><header><h4>{{ resourceName(group) }}</h4><ReferenceButton v-if="draft" variant="link" :disabled="locked" :aria-label="`移除 ${resourceName(group)} ${scopeText(group)} 的授权`" @click="remove(group)">移除</ReferenceButton></header><ul><li v-for="grant in group.grants" :key="grantKey(grant)">{{ actionName(grant) }}</li></ul><div class="role-assignment-scope"><span class="role-helper">生效范围</span><p>{{ scopeText(group) }}</p><ReferenceButton v-if="draft" variant="link" :disabled="locked" :aria-label="`调整 ${resourceName(group)} ${scopeText(group)} 的范围`" @click="editGroup(group)">调整能力与范围</ReferenceButton></div></article>
          <section v-if="dependencies.length" class="role-dependencies"><h4>配套权限待检查</h4><p class="role-helper">仅凭此角色，还缺少以下权限或范围；成员的其他角色可能补足。</p><ul><li v-for="(item, index) in dependencies" :key="index">{{ resourceName(item.requirement) }} · {{ actionName(item.requirement) }}</li></ul><ReferenceButton v-if="draft" :disabled="locked || composerTouched" @click="completeDependencies">补齐未分配的配套权限</ReferenceButton><p v-if="draft" class="role-helper">沿用对应能力的范围；已有权限范围不会被自动扩大。</p></section>
          <section v-if="assignedMembers.length" class="role-members"><h4>关联成员 · {{ assignedMembers.length }}</h4><p v-for="member in assignedMembers.slice(0, 8)" :key="member.userId">{{ member.displayName }}<span v-if="!member.enabled || !member.userEnabled" class="role-helper"> · 已停用</span></p><p v-if="assignedMembers.length > 8" class="role-helper">另有 {{ assignedMembers.length - 8 }} 名成员；完整分配在租户成员管理中查看。</p></section>
        </section>
        <div v-if="!draft" class="role-activity"><slot name="activity" /></div>
      </div>
      <footer v-if="draft" class="role-savebar"><div><span v-if="stale" class="role-validation">配置已变化，请重新读取后编辑。</span><span v-else-if="invalidGrants" class="role-validation">授权包含停用或未选择的组织，请调整范围或移除对应授权。</span><span v-else-if="oversized" class="role-validation">当前授权配置过大，请减少权限或组织数量。</span><span v-else-if="composerTouched">当前配置尚未加入授权清单</span><span v-else>{{ changed ? `${changes.length} 项权限变更待保存` : '没有未保存的修改' }}</span></div><ReferenceButton :disabled="writing" @click="emit('cancel')">取消编辑</ReferenceButton><ReferenceButton v-if="!review" primary :disabled="!ready" @click="inspect">检查并保存</ReferenceButton><ReferenceButton v-else primary :disabled="!ready" @click="emit('save')">{{ writing ? '正在保存…' : '保存变更' }}</ReferenceButton></footer>
    </template>
    <p v-else class="role-empty">{{ loading ? '正在读取角色…' : '角色不存在或当前账号无法管理此角色。' }}</p>
  </section>
</template>
