<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import ElButton from 'element-plus/es/components/button/index';
import { identityApi, IdentityApiError, type IdentitySection, type IdentityWorkspaceData, type IdentityTenantData, type IdentityGrant, type IdentityAudit } from './identity-api';
import './identity.css';

const props = defineProps<{ section: IdentitySection; entityId: string; tenantId: string | null; explicitTenant: boolean; refreshKey: number }>();
const emit = defineEmits<{ navigate: [path: string]; unauthorized: []; busy: [value: boolean]; dirty: [value: boolean]; changed: [] }>();
const labels = { users: '用户', tenants: '租户', members: '成员', organizations: '组织', roles: '角色' };
const workspace = ref<IdentityWorkspaceData | null>(null);
const tenantData = ref<IdentityTenantData | null>(null);
const search = ref(''); const loading = ref(false); const writing = ref(false); const error = ref(''); const notice = ref('');
const denied = ref(false); const dirty = ref(false); const stale = ref(false);
const editor = ref<{ id: string; revision: number; name: string; issuer: string; subject: string; enabled: boolean; administrator: boolean; kind: string; roleIds: string[]; grants: IdentityGrant[] } | null>(null);
const formHeading = ref<HTMLElement>();
let generation = 0; let controller: AbortController | undefined; let disposed = false;
const globalSection = computed(() => props.section === 'users' || props.section === 'tenants');
const canManage = computed(() => !!workspace.value && (globalSection.value ? workspace.value.platformAdministrator : !!tenantData.value));
const rows = computed(() => {
  if (props.section === 'users') return (workspace.value?.users ?? []).map(x => ({ id: x.userId, name: x.displayName, enabled: x.enabled, revision: x.revision }));
  if (props.section === 'tenants') return (workspace.value?.tenants ?? []).map(x => ({ id: x.tenantId, name: x.name, enabled: x.enabled, revision: x.revision }));
  if (props.section === 'members') return (tenantData.value?.members ?? []).map(x => ({ id: x.userId, name: x.displayName, enabled: x.enabled && x.userEnabled, revision: x.revision }));
  if (props.section === 'organizations') return (tenantData.value?.organizations ?? []).map(x => ({ id: x.organizationId, name: x.name, enabled: x.enabled, revision: x.revision }));
  return (tenantData.value?.roles ?? []).map(x => ({ id: x.roleId, name: x.name, enabled: x.enabled, revision: x.revision }));
});
const capacityReached = computed(() => rows.value.length >= 500);
const shown = computed(() => rows.value.filter(x => `${x.name} ${x.id}`.toLocaleLowerCase().includes(search.value.trim().toLocaleLowerCase())));
const audit = computed(() => globalSection.value ? workspace.value?.audit : tenantData.value?.audit);
const selected = computed(() => rows.value.find(x => x.id === props.entityId));
const selectedUser = computed(() => workspace.value?.users.find(x => x.userId === props.entityId));
const selectedMember = computed(() => tenantData.value?.members.find(x => x.userId === props.entityId));
const selectedOrganization = computed(() => tenantData.value?.organizations.find(x => x.organizationId === props.entityId));
const selectedRole = computed(() => tenantData.value?.roles.find(x => x.roleId === props.entityId));
const pathFor = (id = '') => `${!globalSection.value && props.explicitTenant ? `/manage/tenants/${props.tenantId}` : '/manage'}/${props.section}${id ? `/${id}` : ''}`;
const tenantPath = (section: string) => `/manage/tenants/${props.entityId}/${section}`;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
function inputChanged(event: Event) { if ((event.target as HTMLInputElement).type !== 'checkbox') setDirty(true); }
function setDirty(value: boolean) { dirty.value = value; emit('dirty', value); }
function clearEditor() { editor.value = null; stale.value = false; setDirty(false); }
function clearData() { workspace.value = null; tenantData.value = null; }
function begin(write = false) {
  controller?.abort(); controller = new AbortController(); const id = ++generation;
  writing.value = write; loading.value = !write; emit('busy', write);
  return { id, signal: controller.signal };
}
const current = (id: number) => !disposed && id === generation;
function errorText(failure: unknown) {
  const status = failure instanceof IdentityApiError ? failure.status : 503;
  if (failure instanceof IdentityApiError && failure.code === 'identity_capacity_reached') return '记录数量或资料/授权范围达到管理容量，请减少本次内容或联系部署管理员；已有记录可编辑。';
  if (status === 403) return '管理权限或当前租户已变化，请重新读取状态。';
  if (status === 404) return '记录不存在或当前账号无法管理此记录。';
  if (status === 409) return '记录已被更新或当前操作会影响管理权限。此输入保留，不会自动覆盖；请重新读取后再编辑。';
  if ([400, 413, 415].includes(status)) return '变更未通过校验，请检查必填项、成员、角色与资源范围。';
  return '暂时无法确认管理状态，请检查连接后重新读取。';
}
async function read(id: number, signal: AbortSignal) {
  const result = await identityApi.workspace(signal);
  if (!current(id)) return;
  if (!Array.isArray(result.users) || !Array.isArray(result.tenants) || typeof result.platformAdministrator !== 'boolean') throw new IdentityApiError(502);
  if (globalSection.value && !result.platformAdministrator) throw new IdentityApiError(403);
  const data = !globalSection.value && props.tenantId ? await identityApi.tenant(props.tenantId, signal) : null;
  if (!current(id)) return;
  if (data && (data.tenant?.tenantId !== props.tenantId || !Array.isArray(data.members) || !Array.isArray(data.roles) || !Array.isArray(data.organizations))) throw new IdentityApiError(502);
  workspace.value = result; tenantData.value = data; denied.value = false;
  if (editor.value?.revision && rows.value.find(x => x.id === editor.value?.id)?.revision !== editor.value.revision) stale.value = true;
}
async function refresh() {
  if (writing.value) return;
  const work = begin(); error.value = '';
  try { await read(work.id, work.signal); }
  catch (failure) {
    if (!current(work.id) || work.signal.aborted) return;
    clearData(); stale.value = !!editor.value; error.value = errorText(failure);
    if (failure instanceof IdentityApiError && [401, 403].includes(failure.status)) {
      clearEditor(); denied.value = true;
      if (failure.status === 401) emit('unauthorized');
    }
  } finally { if (current(work.id)) loading.value = false; }
}
async function edit(create = false) {
  if (loading.value || writing.value || !canManage.value || (create && capacityReached.value) || (!create && !selected.value)) return;
  if (dirty.value && !window.confirm('有尚未保存的修改。放弃修改并继续？')) return;
  const user = create ? null : selectedUser.value; const member = create ? null : selectedMember.value;
  const organization = create ? null : selectedOrganization.value; const role = create ? null : selectedRole.value;
  editor.value = { id: create ? (props.section === 'members' ? '' : crypto.randomUUID()) : props.entityId, revision: create ? 0 : selected.value!.revision,
    name: create ? '' : selected.value!.name, issuer: user?.issuer ?? '', subject: user?.subject ?? '',
    enabled: create ? true : (props.section === 'members' ? !!member?.enabled : selected.value!.enabled),
    administrator: props.section === 'users' ? !!user?.platformAdministrator : !!member?.administrator,
    kind: organization?.kind ?? 'factory', roleIds: [...(member?.roleIds ?? [])], grants: JSON.parse(JSON.stringify(role?.grants ?? [])) as IdentityGrant[] };
  stale.value = false; setDirty(false); error.value = ''; notice.value = '';
  await nextTick(); formHeading.value?.focus();
}
function addGrant() { editor.value?.grants.push({ permission: '', resourceType: 'application', resourceId: '', tenantWide: false, organizationIds: [] }); setDirty(true); }
function scopeChanged(grant: IdentityGrant) { if (grant.tenantWide) grant.organizationIds = []; setDirty(true); }
function validation() {
  const value = editor.value; if (!value) return false;
  if (!uuid.test(value.id) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value.id)) { error.value = '请输入有效的用户 UUID。'; return false; }
  if (props.section !== 'members' && !value.name.trim()) { error.value = '名称不能为空。'; return false; }
  if (props.section === 'users' && (!value.issuer.trim() || !value.subject.trim())) { error.value = '请填写身份服务颁发者和账号主体。'; return false; }
  if (props.section === 'roles' && value.grants.some(g => !/^[a-z][a-z0-9:._-]{0,127}$/.test(g.permission) || !/^[a-z][a-z0-9._-]{0,63}$/.test(g.resourceType) || !g.resourceId || g.resourceId.trim() !== g.resourceId || g.resourceId.includes('*') || (!g.tenantWide && !g.organizationIds.length))) {
    error.value = '每条权限需填写有效权限名、资源类型、精确资源标识，并选择整个租户或至少一个组织。'; return false;
  }
  return true;
}
async function save() {
  if (!editor.value || (!editor.value.revision && capacityReached.value) || writing.value || loading.value || stale.value || !canManage.value || !validation()) return;
  const value = editor.value;
  if (!value.enabled && value.revision && !window.confirm('停用后将影响该记录关联的访问权限。确认保存？')) return;
  const body = props.section === 'users' ? { expectedRevision: value.revision, issuer: value.issuer.trim(), subject: value.subject.trim(), displayName: value.name.trim(), enabled: value.enabled, platformAdministrator: value.administrator }
    : props.section === 'tenants' ? { expectedRevision: value.revision, name: value.name.trim(), enabled: value.enabled }
    : props.section === 'members' ? { expectedRevision: value.revision, enabled: value.enabled, administrator: value.administrator, roleIds: value.roleIds }
    : props.section === 'organizations' ? { expectedRevision: value.revision, name: value.name.trim(), kind: value.kind.trim(), enabled: value.enabled }
    : { expectedRevision: value.revision, name: value.name.trim(), enabled: value.enabled, grants: value.grants };
  const work = begin(true); error.value = ''; notice.value = ''; let committed = false;
  try {
    await identityApi.save(props.section, value.id, props.tenantId, body, work.signal);
    if (!current(work.id)) return;
    committed = true; clearEditor();
    await read(work.id, work.signal);
    if (current(work.id)) notice.value = '变更已保存，已重新读取最新状态。';
  } catch (failure) {
    if (!current(work.id) || work.signal.aborted) return;
    if (failure instanceof IdentityApiError && [401, 403].includes(failure.status)) { clearData(); clearEditor(); denied.value = true; if (failure.status === 401) emit('unauthorized'); }
    else if (!(failure instanceof IdentityApiError) || (failure.status === 409 && failure.code !== 'identity_capacity_reached') || failure.status >= 500) stale.value = true;
    if (committed) clearData();
    error.value = committed ? '变更已提交，但最新状态读取失败。请重新读取，勿重复提交。' : errorText(failure);
  } finally {
    if (current(work.id)) {
      writing.value = false; loading.value = false; emit('busy', false);
      if (committed && !error.value) { emit('changed'); if (value.id !== props.entityId) emit('navigate', pathFor(value.id)); }
    }
  }
}
function follow(event: MouseEvent, path: string) {
  if (event.button || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
  event.preventDefault(); if (!writing.value) emit('navigate', path);
}
function cancel() { if (!dirty.value || window.confirm('有尚未保存的修改。放弃修改并继续？')) clearEditor(); }
function reread() { if (!dirty.value || window.confirm('重新读取会放弃当前未保存修改。继续？')) { clearEditor(); void refresh(); } }
const auditTypes: Record<string, string> = { user: '用户', users: '用户', tenant: '租户', tenants: '租户', member: '成员', members: '成员', organization: '组织', organizations: '组织', role: '角色', roles: '角色' };
const auditActions: Record<string, string> = { create: '新增', update: '更新', save: '保存', enable: '启用', disable: '停用', delete: '删除' };
const auditResults: Record<string, string> = { success: '成功', denied: '权限拒绝', conflict: '修订冲突', failed: '失败' };
function auditTargetName(item: IdentityAudit) {
  const type = item.entityType.replace(/s$/, '');
  if (type === 'user') return workspace.value?.users.find(x => x.userId === item.entityId)?.displayName;
  if (type === 'tenant') return workspace.value?.tenants.find(x => x.tenantId === item.entityId)?.name;
  if (type === 'member') return tenantData.value?.members.find(x => x.userId === item.entityId)?.displayName;
  if (type === 'organization') return tenantData.value?.organizations.find(x => x.organizationId === item.entityId)?.name;
  if (type === 'role') return tenantData.value?.roles.find(x => x.roleId === item.entityId)?.name;
  return undefined;
}
const date = (value: string) => new Date(value).toLocaleString('zh-CN', { hour12: false });
watch(() => [props.section, props.entityId, props.tenantId], () => { clearEditor(); clearData(); search.value = ''; notice.value = ''; void refresh(); }, { immediate: true });
watch(() => props.refreshKey, () => { void refresh(); });
onBeforeUnmount(() => { disposed = true; generation++; controller?.abort(); emit('dirty', false); emit('busy', false); });
</script>

<template>
  <section class="identity-workspace" aria-label="身份与访问管理" :aria-busy="loading || writing">
    <div v-if="error" class="identity-error" role="alert"><span>{{ error }}</span><ElButton :disabled="loading || writing" @click="reread">重新读取状态</ElButton></div>
    <p v-if="notice" class="identity-notice" role="status">{{ notice }}</p>
    <p v-if="stale && editor && !error" class="identity-error" role="alert">记录已变化，此输入保留。请重新读取后再编辑。</p>
    <section v-if="denied" class="identity-empty"><h2>没有管理权限</h2><p>此页面需要对应的平台或租户管理权限。联系管理员后重新读取。</p></section>
    <section v-else-if="!globalSection && !tenantId" class="identity-empty"><h2>选择管理租户</h2><p>从工作空间选择租户，或从租户管理进入对应的成员、组织与角色。</p></section>
    <div v-else class="identity-layout">
      <aside class="identity-index" :aria-label="`${labels[section]}列表`">
        <div class="identity-index-heading"><h2>{{ labels[section] }} <span>{{ rows.length }}</span></h2><ElButton v-if="canManage" size="small" :disabled="loading || writing || capacityReached" @click="edit(true)">新增{{ labels[section] }}</ElButton></div>
        <p v-if="capacityReached" class="identity-help" role="status">已达到当前管理容量（500条），现有记录仍可编辑；扩容需调整部署方案。</p>
        <label class="sr-only" for="identity-search">搜索{{ labels[section] }}</label><input id="identity-search" v-model="search" type="search" :placeholder="`搜索${labels[section]}名称或标识`" :disabled="writing" />
        <p v-if="loading && !workspace" class="identity-empty" role="status">正在读取管理数据…</p>
        <p v-else-if="!shown.length" class="identity-empty">{{ search ? '没有匹配的记录。请调整搜索条件。' : `暂无${labels[section]}。使用新增操作建立第一条记录。` }}</p>
        <ul v-else class="identity-list"><li v-for="row in shown" :key="row.id"><a :href="pathFor(row.id)" :aria-current="entityId === row.id ? 'page' : undefined" @click="follow($event, pathFor(row.id))"><strong>{{ row.name }}</strong><span>{{ row.enabled ? '已启用' : '已停用' }}</span></a></li></ul>
        <p class="identity-help">{{ globalSection ? '平台范围' : tenantData?.tenant.name }} · 仅展示当前账号可管理的记录。</p>
      </aside>
      <div class="identity-detail">
        <form v-if="editor" class="identity-form" aria-label="身份管理表单" @submit.prevent="save" @input="inputChanged" @change="setDirty(true)">
          <header class="identity-heading"><div><h2 ref="formHeading" tabindex="-1">{{ editor.revision ? '编辑' : '新增' }}{{ labels[section] }}</h2><p>{{ section === 'users' ? '绑定企业身份账号，登录凭据仍由身份中心管理。' : '保存后立即按新状态检查访问权限。' }}</p></div></header>
          <fieldset :disabled="writing || loading || stale || !canManage"><legend class="sr-only">{{ labels[section] }}资料</legend>
            <div class="identity-fields">
              <label v-if="section !== 'members'">{{ section === 'users' ? '显示名称' : `${labels[section]}名称` }}<input v-model="editor.name" required :maxlength="section === 'roles' ? 128 : 256" /></label>
              <template v-if="section === 'users'"><label>身份服务颁发者<input v-model="editor.issuer" required maxlength="2048" :readonly="editor.revision > 0" placeholder="https://identity.example/realms/company" /></label><label>账号主体<input v-model="editor.subject" required maxlength="512" :readonly="editor.revision > 0" /></label></template>
              <label v-if="section === 'members'">用户 UUID<input :list="workspace?.platformAdministrator ? 'identity-known-users' : undefined" v-model="editor.id" required :readonly="editor.revision > 0" maxlength="36" :pattern="uuid.source" /></label>
              <datalist v-if="workspace?.platformAdministrator" id="identity-known-users"><option v-for="user in workspace.users" :key="user.userId" :value="user.userId">{{ user.displayName }}{{ user.enabled ? '' : '（已停用）' }}</option></datalist>
              <label v-if="section === 'organizations'">组织类型<input v-model="editor.kind" required maxlength="64" pattern="[a-z][a-z0-9._-]{0,63}" /><small>例如 factory（工厂）、line（产线）。组织之间不继承权限。</small></label>
            </div>
            <div class="identity-switches"><label><input v-model="editor.enabled" type="checkbox" />{{ section === 'members' ? '启用成员关系' : '启用记录' }}</label><label v-if="section === 'users' || section === 'members'"><input v-model="editor.administrator" type="checkbox" :disabled="!workspace?.platformAdministrator" />{{ section === 'users' ? '平台管理员' : '租户管理员' }}</label></div>
            <p v-if="section === 'members'" class="identity-help">使用平台已登记用户的 UUID。只有平台管理员可以调整租户管理员身份。</p>
            <fieldset v-if="section === 'members'" class="identity-subsection"><legend>分配角色</legend><p v-if="!tenantData?.roles.length" class="identity-help">尚无角色。先创建角色，再为成员分配。</p><div class="identity-choices"><label v-for="role in tenantData?.roles" :key="role.roleId"><input v-model="editor.roleIds" type="checkbox" :value="role.roleId" :disabled="!role.enabled && !editor.roleIds.includes(role.roleId)" />{{ role.name }}{{ role.enabled ? '' : '（已停用）' }}</label></div></fieldset>
            <section v-if="section === 'roles'" class="identity-subsection"><div class="identity-index-heading"><h3>权限与资源范围</h3><ElButton size="small" @click="addGrant">添加权限</ElButton></div><p class="identity-help">每条权限绑定一个精确资源；整个租户与指定组织二选一。</p><p v-if="!editor.grants.length" class="identity-help">该角色当前不授予任何业务权限。</p>
              <fieldset v-for="(grant, index) in editor.grants" :key="index" class="identity-grant"><legend>权限 {{ index + 1 }}</legend><div class="identity-fields"><label>权限名称<input v-model="grant.permission" required maxlength="128" pattern="[a-z][a-z0-9:._-]{0,127}" placeholder="chatbi:use" /></label><label>资源类型<input v-model="grant.resourceType" required maxlength="64" pattern="[a-z][a-z0-9._-]{0,63}" /></label><label>资源标识<input v-model="grant.resourceId" required maxlength="256" placeholder="chatbi" /></label></div><label class="identity-check"><input v-model="grant.tenantWide" type="checkbox" @change="scopeChanged(grant)" />整个租户</label><div v-if="!grant.tenantWide" class="identity-choices"><label v-for="organization in tenantData?.organizations" :key="organization.organizationId"><input v-model="grant.organizationIds" type="checkbox" :value="organization.organizationId" :disabled="!organization.enabled && !grant.organizationIds.includes(organization.organizationId)" />{{ organization.name }}{{ organization.enabled ? '' : '（已停用）' }}</label><p v-if="!tenantData?.organizations.some(x => x.enabled)" class="identity-help">暂无启用的组织，请先建立组织。</p></div><ElButton text @click="editor.grants.splice(index, 1); setDirty(true)">移除权限 {{ index + 1 }}</ElButton></fieldset>
            </section>
          </fieldset>
          <footer class="identity-form-footer"><span>{{ stale ? '请重新读取后编辑' : dirty ? '有未保存的修改' : '填写资料后保存' }}</span><div><ElButton :disabled="writing" @click="cancel">取消编辑</ElButton><ElButton native-type="submit" type="primary" :disabled="writing || loading || stale || !canManage || (!editor.revision && capacityReached)">{{ writing ? '正在保存…' : '保存变更' }}</ElButton></div></footer>
        </form>
        <section v-else-if="selected" class="identity-record">
          <header class="identity-heading"><div><h2>{{ selected.name }}</h2><p>{{ selected.enabled ? '已启用' : '已停用' }}</p></div><ElButton :disabled="loading || writing || !canManage" @click="edit()">编辑资料</ElButton></header>
          <dl class="identity-facts"><div><dt>{{ labels[section] }}标识</dt><dd><code>{{ selected.id }}</code></dd></div><template v-if="section === 'users'"><div><dt>身份服务颁发者</dt><dd>{{ selectedUser?.issuer }}</dd></div><div><dt>账号主体</dt><dd>{{ selectedUser?.subject }}</dd></div><div><dt>管理身份</dt><dd>{{ selectedUser?.platformAdministrator ? '平台管理员' : '普通用户' }}</dd></div></template><template v-if="section === 'members'"><div><dt>管理身份</dt><dd>{{ selectedMember?.administrator ? '租户管理员' : '租户成员' }}</dd></div><div><dt>账号状态</dt><dd>{{ selectedMember?.userEnabled ? '账号已启用' : '账号已停用，成员启用后仍无法访问' }}</dd></div><div><dt>已分配角色</dt><dd>{{ selectedMember?.roleIds.map(id => tenantData?.roles.find(x => x.roleId === id)?.name ?? id).join('、') || '尚未分配角色' }}</dd></div></template><div v-if="section === 'organizations'"><dt>组织类型</dt><dd>{{ selectedOrganization?.kind }}</dd></div></dl>
          <div v-if="section === 'tenants'" class="identity-related"><h3>租户内管理</h3><a v-for="sectionName in ['members', 'organizations', 'roles']" :key="sectionName" :href="tenantPath(sectionName)" @click="follow($event, tenantPath(sectionName))">{{ { members: '管理租户成员', organizations: '管理组织', roles: '管理角色权限' }[sectionName] }} <span aria-hidden="true">→</span></a></div>
          <section v-if="section === 'roles'" class="identity-subsection"><h3>权限与资源范围</h3><p v-if="!selectedRole?.grants.length" class="identity-help">此角色不授予业务权限。</p><dl v-for="(grant, index) in selectedRole?.grants" :key="index" class="identity-grant-summary"><dt>{{ grant.permission }}</dt><dd>{{ grant.resourceType }} / {{ grant.resourceId }}</dd><dd>{{ grant.tenantWide ? '整个租户' : grant.organizationIds.map(id => tenantData?.organizations.find(x => x.organizationId === id)?.name ?? id).join('、') }}</dd></dl></section>
        </section>
        <section v-else-if="entityId && !loading && workspace" class="identity-empty"><h2>记录不可管理</h2><p>记录不存在或当前账号无法管理此记录。</p></section>
        <section v-else class="identity-empty"><h2>{{ loading ? '正在读取资料…' : `管理${labels[section]}` }}</h2><p>从列表选择记录，查看资料与访问配置，或新增{{ labels[section] }}。</p></section>
        <section v-if="audit && !editor" class="identity-audit"><h3>最近操作</h3><p v-if="!audit.items.length" class="identity-help">当前范围暂无可见操作记录。</p><ol v-else><li v-for="item in audit.items" :key="item.eventId"><span>{{ auditTypes[item.entityType] ?? item.entityType }} · {{ auditActions[item.action] ?? item.action }} · {{ auditResults[item.result] ?? item.result }}</span><time>{{ date(item.occurredAt) }}</time><div class="identity-audit-target"><span>目标{{ auditTargetName(item) ? `：${auditTargetName(item)}` : '' }}</span><code>{{ item.entityId }}</code></div><div class="identity-audit-meta"><span>操作人</span><code>{{ item.actorUserId }}</code></div><div class="identity-audit-meta"><span>关联编号</span><code>{{ item.requestId }}</code></div></li></ol><p v-if="audit.hasMore" class="identity-help">显示最近 50 条操作，还有更早记录。</p></section>
      </div>
    </div>
  </section>
</template>
