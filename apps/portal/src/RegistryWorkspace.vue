<script setup lang="ts">
import { vReferenceContent } from './components/reference/reference-motion';
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { ReferenceButton, ReferenceSelect, ReferencePagination } from './components/reference';
import ReferenceDrawer from './components/reference/ReferenceDrawer.vue';
import ReferenceConfirm from './components/reference/ReferenceConfirm.vue';
import { useReferenceConfirm } from './components/reference/useReferenceConfirm';
import RegistryDraftForm from './RegistryDraftForm.vue';
import { registryApi, RegistryApiError, type RegistryWorkspace as Workspace, type TenantRegistryWorkspace,
  type RegistryRelease, type RegistryManifest, type RegistryAction, type RegistryAudit } from './registry-api';
import './registry.css';

const { confirmation: decision, requestConfirmation, settleConfirmation } = useReferenceConfirm();
const props = defineProps<{ tenantId: string | null; appId: string; refreshKey: number }>();
const emit = defineEmits<{ navigate: [path: string]; unauthorized: []; busy: [value: boolean]; dirty: [value: boolean] }>();
const workspace = ref<Workspace | null>(null);
const tenantWorkspace = ref<TenantRegistryWorkspace | null>(null);
const releases = ref<RegistryRelease[]>([]);
const tenantReleases = ref<RegistryRelease[]>([]);
const audits = ref<RegistryAudit[]>([]);
const auditHasMore = ref(false);
const selectedVersion = ref('');
const tenantVersion = ref('');
const page = ref(1); const pageSize = ref(10); const scopeFilter = ref('all');
const search = ref('');
const loading = ref(false);
const writing = ref(false);
const error = ref('');
const notice = ref('');
const missing = ref(false);
const dirty = ref(false);
const editor = ref<{ key: number; initial: RegistryManifest; mode: 'create' | 'version' | 'edit'; revision: number } | null>(null);
const forcedStale = ref(false);
const dialog = ref<HTMLDialogElement>();
const confirmation = ref<{ title: string; description: string; execute: (signal: AbortSignal) => Promise<unknown> } | null>(null);
let editorKey = 0;
let generation = 0;
let controller: AbortController | undefined;
let disposed = false;
const application = computed(() => workspace.value?.items.find(item => item.appId === props.appId));
const tenantApplication = computed(() => tenantWorkspace.value?.items.find(item => item.appId === props.appId));
const items = computed(() => {
  const list = new Map((tenantWorkspace.value?.items ?? []).map(item => [item.appId, { ...item, global: false, tenant: true }]));
  for (const item of workspace.value?.items ?? []) list.set(item.appId, { ...item, current: null, global: true, tenant: list.has(item.appId) });
  return [...list.values()].sort((a, b) => a.name.localeCompare(b.name, 'zh-CN')).filter(item => !search.value.trim()
    || `${item.appId} ${item.name} ${item.category}`.toLocaleLowerCase().includes(search.value.trim().toLocaleLowerCase()));
});
const filteredItems = computed(() => items.value.filter(item => scopeFilter.value === 'all' || (scopeFilter.value === 'global' ? item.global : item.tenant)));
const pagedItems = computed(() => filteredItems.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value));
watch([search, scopeFilter, pageSize], () => { page.value = 1; });
watch(() => filteredItems.value.length, count => { page.value = Math.min(page.value, Math.max(1, Math.ceil(count / pageSize.value))); });
const availableReleases = computed(() => application.value ? releases.value : tenantReleases.value);
const release = computed(() => availableReleases.value.find(item => item.manifest.version === selectedVersion.value));
const canEdit = computed(() => application.value?.canEdit === true);
const canReview = computed(() => application.value?.canReview === true);
const stale = computed(() => {
  const draft = editor.value;
  if (!draft) return false;
  if (forcedStale.value || !workspace.value) return true;
  if (draft.mode === 'create') return !workspace.value.canCreate;
  if (!canEdit.value) return true;
  if (draft.mode === 'version') return false;
  const latest = releases.value.find(item => item.manifest.version === draft.initial.version);
  return !latest || latest.revision !== draft.revision || latest.manifest.status !== 'draft';
});
const actions = computed<RegistryAction[]>(() => {
  if (!release.value) return [];
  const status = release.value.manifest.status;
  if (status === 'draft') return canEdit.value ? ['submit'] : [];
  if (!canReview.value) return [];
  if (status === 'pending_review') return release.value.submittedBy === workspace.value?.actorUserId ? ['reject'] : ['reject', 'publish'];
  if (status === 'published') return ['disable'];
  if (status === 'disabled') return ['enable'];
  return [];
});
const statusNames = { draft: '草稿', pending_review: '待审核', published: '已发布', disabled: '已停用' };
const actionNames: Record<RegistryAction, string> = { submit: '提交审核', reject: '驳回审核', publish: '发布版本', disable: '全局停用', enable: '恢复发布' };
const auditNames: Record<string, string> = { save_draft: '保存草稿', submit: '提交审核', reject: '驳回审核', publish: '发布版本', disable: '全局停用', enable: '恢复发布', set_tenant: '调整租户应用' };
const resultNames: Record<string, string> = { success: '成功', Forbidden: '权限拒绝', Conflict: '版本冲突', NotFound: '未找到', DeploymentNotAllowed: '部署不匹配', InvalidManifest: '清单无效' };
function setDirty(value: boolean) { dirty.value = value; emit('dirty', value); }
function clearEditor() { editor.value = null; forcedStale.value = false; setDirty(false); }
async function discard() { return !dirty.value || await ask('当前草稿尚未保存，确定放弃这些修改吗？'); }
async function cancelEditor() { if (await discard()) clearEditor(); }
function closeDetail() { if (editor.value) void cancelEditor(); else emit('navigate', '/manage/applications'); }
function begin(write = false) {
  controller?.abort(); controller = new AbortController(); generation++;
  writing.value = write; loading.value = !write; emit('busy', write);
  return { id: generation, signal: controller.signal };
}
const current = (id: number) => !disposed && id === generation;
function clearReadData() { workspace.value = null; tenantWorkspace.value = null; releases.value = []; tenantReleases.value = []; audits.value = []; auditHasMore.value = false; }
async function ask(description: string, title = '放弃修改？', confirmLabel = '放弃修改') {
  const context = generation; const draft = editor.value;
  const accepted = await requestConfirmation({ title, description, confirmLabel });
  return accepted && current(context) && draft === editor.value;
}
function errorText(failure: unknown) {
  const status = failure instanceof RegistryApiError ? failure.status : 503;
  if (status === 403) return '管理权限或当前租户已变化，请刷新页面后再试。';
  if (status === 404) return '应用不存在或当前账号无法管理此应用。';
  if (status === 409) return '此内容已被其他操作更新。请重新读取状态后再操作，不会自动覆盖。';
  if (status === 400 || status === 413 || status === 415) return '操作未通过校验，请检查表单、状态与部署信息后再试。';
  if (status === 429) return '操作较多，请稍后再试。';
  return '暂时无法确认应用状态，请检查连接后重新读取。';
}
async function readData(id: number, signal: AbortSignal) {
  const selected = props.tenantId; const appId = props.appId;
  const [global, tenant] = await Promise.all([registryApi.workspace(signal), selected ? registryApi.tenantWorkspace(selected, signal) : Promise.resolve(null)]);
  if (!current(id)) return;
  if (!Array.isArray(global.items) || (tenant && (!Array.isArray(tenant.items) || tenant.tenantId !== selected || tenant.actorUserId !== global.actorUserId))) throw new RegistryApiError(502);
  const globalApp = global.items.find(item => item.appId === appId);
  const tenantApp = tenant?.items.find(item => item.appId === appId);
  const [versions, tenantVersions, globalAudit, tenantAudit] = await Promise.all([
    globalApp ? registryApi.releases(appId, signal) : Promise.resolve({ items: [] }),
    tenantApp && selected ? registryApi.tenantReleases(selected, appId, signal) : Promise.resolve({ items: [] }),
    globalApp ? registryApi.audit(appId, signal) : Promise.resolve({ items: [], hasMore: false }),
    tenantApp && selected ? registryApi.tenantAudit(selected, appId, signal) : Promise.resolve({ items: [], hasMore: false }),
  ]);
  if (!current(id)) return;
  workspace.value = global; tenantWorkspace.value = tenant; releases.value = versions.items; tenantReleases.value = tenantVersions.items;
  audits.value = [...globalAudit.items, ...tenantAudit.items].sort((a, b) => b.occurredAt.localeCompare(a.occurredAt));
  auditHasMore.value = globalAudit.hasMore || tenantAudit.hasMore;
  if (!editor.value) forcedStale.value = false;
  missing.value = !!appId && !globalApp && !tenantApp;
  const choices = globalApp ? versions.items : tenantVersions.items;
  if (!choices.some(item => item.manifest.version === selectedVersion.value)) selectedVersion.value = choices[0]?.manifest.version ?? '';
  if (!tenantVersions.items.some(item => item.manifest.version === tenantVersion.value)) tenantVersion.value = tenantVersions.items.some(item => item.manifest.version === tenantApp?.current?.releaseVersion) ? tenantApp!.current!.releaseVersion : '';
}
async function refresh() {
  if (writing.value) return;
  if (confirmation.value) { dialog.value?.close(); confirmation.value = null; notice.value = '状态正在重新读取，请检查后再次确认操作。'; }
  const work = begin(); error.value = '';
  try { await readData(work.id, work.signal); }
  catch (failure) {
    if (!current(work.id) || work.signal.aborted) return;
    clearReadData(); error.value = errorText(failure);
    if (failure instanceof RegistryApiError && failure.status === 401) { clearEditor(); emit('unauthorized'); }
  } finally { if (current(work.id)) loading.value = false; }
}
async function mutate(operation: (signal: AbortSignal) => Promise<unknown>, success: string, destination?: string) {
  const work = begin(true); error.value = ''; notice.value = ''; let committed = false;
  try {
    await operation(work.signal);
    if (!current(work.id)) return;
    committed = true; clearEditor(); notice.value = success;
    if (!destination || destination === props.appId) await readData(work.id, work.signal);
  } catch (failure) {
    if (!current(work.id) || work.signal.aborted) return;
    if (failure instanceof RegistryApiError && failure.status === 401) { clearReadData(); clearEditor(); emit('unauthorized'); return; }
    if (!(failure instanceof RegistryApiError) || failure.status === 409 || failure.status >= 500) forcedStale.value = true;
    if ((failure instanceof RegistryApiError && failure.status === 403) || committed) clearReadData();
    error.value = committed ? '操作已提交，但最新状态读取失败。请重新读取，勿重复提交。' : errorText(failure);
  } finally {
    if (current(work.id)) {
      writing.value = false; loading.value = false; emit('busy', false);
      if (committed && destination && destination !== props.appId) emit('navigate', `/manage/applications/${destination}`);
    }
  }
}
async function edit(mode: 'create' | 'version' | 'edit') {
  if (writing.value || !await discard() || writing.value) return;
  if (mode === 'create' ? !workspace.value?.canCreate : !canEdit.value || !release.value) return;
  const initial: RegistryManifest = mode === 'create' ? { schemaVersion: 1, appId: '', name: '', category: '', description: '', version: '', frontendEntry: '', frontendArtifact: { version: '', sha256: '' }, backendService: '', backendVersion: '', permissions: [], status: 'draft' }
    : JSON.parse(JSON.stringify(release.value!.manifest)) as RegistryManifest;
  initial.status = 'draft'; if (mode === 'version') initial.version = '';
  editor.value = { key: ++editorKey, initial, mode, revision: mode === 'edit' ? release.value!.revision : 0 };
  forcedStale.value = false; setDirty(false); error.value = ''; notice.value = '';
}
function save(manifest: RegistryManifest) {
  if (!editor.value || stale.value || writing.value) return;
  const revision = editor.value.revision;
  selectedVersion.value = manifest.version;
  void mutate(signal => registryApi.save(manifest, revision, signal), '草稿已保存，可检查后提交审核。', manifest.appId);
}
async function navigate(event: MouseEvent, appId: string) {
  if (event.button || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
  event.preventDefault(); if (writing.value || !await discard() || writing.value) return;
  clearEditor(); emit('navigate', `/manage/applications/${appId}`);
}
async function confirm(title: string, description: string, execute: (signal: AbortSignal) => Promise<unknown>) {
  if (writing.value || loading.value || forcedStale.value) return;
  confirmation.value = { title, description, execute }; await nextTick(); dialog.value?.showModal();
}
function transition(action: RegistryAction) {
  const selected = release.value; if (!selected || !actions.value.includes(action)) return;
  const appId = props.appId;
  const description = action === 'disable' ? '此版本将对所有租户停用，阻止新调用；正在执行的调用按平台策略终止。'
    : action === 'publish' ? '发布后内容不可修改。该版本仍需租户管理员启用，才会向获授权用户开放。'
    : action === 'submit' ? '提交后草稿暂不可编辑，需要另一位审核人检查并发布。'
    : action === 'reject' ? '此版本将退回草稿，开发者可修改后重新提交。'
    : '恢复原先审核通过的发布内容；已失效的旧运行不会复活。';
  void confirm(actionNames[action], description, signal => registryApi.transition(appId, selected.manifest.version, action, selected.revision, signal));
}
function configureTenant(enabled: boolean) {
  const selected = tenantApplication.value; const tenantId = props.tenantId;
  const version = enabled ? tenantVersion.value : selected?.current?.releaseVersion;
  if (!selected || !tenantId || !version || enabled && !tenantReleases.value.some(item => item.manifest.version === version)) return;
  const revision = selected.current?.revision ?? 0;
  void confirm('租户变更', enabled
    ? `当前租户将启用版本 ${version}。新运行使用此版本，已有运行按排空期限结束；选择旧版即为回滚，失效运行不会恢复。`
    : '当前租户将停用此应用，阻止新调用并按平台策略终止在途调用。其他租户不受影响。',
  signal => registryApi.configureTenant(tenantId, selected.appId, version, enabled, revision, signal));
}
function executeConfirmation() {
  const command = confirmation.value; if (!command) return;
  dialog.value?.close(); confirmation.value = null;
  void mutate(command.execute, `${command.title}已完成，以下为最新状态。`);
}
function date(value: string | null) { return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'; }
watch(() => props.appId, () => { clearEditor(); clearReadData(); selectedVersion.value = ''; tenantVersion.value = ''; notice.value = ''; void refresh(); }, { immediate: true });
watch(() => props.refreshKey, () => { void refresh(); });
watch(() => props.tenantId, () => { clearEditor(); clearReadData(); tenantVersion.value = ''; void refresh(); });
onBeforeUnmount(() => { disposed = true; generation++; controller?.abort(); dialog.value?.close(); emit('busy', false); emit('dirty', false); });
</script>

<template>
  <section class="registry-workspace" aria-label="应用注册管理" :aria-busy="loading || writing">
    <div v-if="error && (!(appId || editor))" class="registry-error" role="alert"><span>{{ error }}</span><ReferenceButton :disabled="loading || writing" @click="refresh">重新读取状态</ReferenceButton></div>
    <p v-if="notice && (!(appId || editor))" class="registry-notice" role="status">{{ notice }}</p>
    <div class="registry-layout">
      <section class="data-grid registry-index" aria-label="可管理应用">
        <div class="data-grid__toolbar"><label class="data-grid__search"><svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden="true"><circle cx="6" cy="6" r="4.2" stroke="currentColor" stroke-width="1.2"/><path d="M9.2 9.2L12 12" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/></svg><input v-model="search" type="search" placeholder="搜索名称或标识" aria-label="搜索管理应用" :disabled="writing" /></label><div class="filter-row data-grid__filters"><button v-for="filter in [{ value: 'all', label: '全部' }, { value: 'global', label: '发布管理' }, { value: 'tenant', label: '租户管理' }]" :key="filter.value" type="button" class="filter-chip" :class="{ 'is-active': scopeFilter === filter.value }" :aria-pressed="scopeFilter === filter.value" @click="scopeFilter = filter.value">{{ filter.label }}</button></div><ReferenceButton v-if="workspace?.canCreate" class="registry-create" :disabled="loading || writing || forcedStale" @click="edit('create')">注册应用</ReferenceButton></div>
        <div class="admin-table-wrap data-grid__body"><div v-reference-content="loading" class="scroll-content data-grid__scroll"><div v-if="loading && !workspace" class="registry-loading" role="status">正在读取管理权限…<i v-for="n in 3" :key="n" /></div><div v-else-if="!filteredItems.length" class="empty-state data-grid__empty is-visible"><div class="empty-icon" aria-hidden="true">∅</div><div class="empty-title">{{ search ? '没有匹配的应用' : '当前没有可管理的应用' }}</div><div class="empty-desc">注册、审核与租户管理权限由管理员分配。</div></div>
          <table v-else class="data-grid__table" aria-label="可管理应用记录"><thead><tr><th scope="col">应用</th><th scope="col">应用标识</th><th scope="col">分类</th><th scope="col">管理范围</th><th scope="col" class="data-grid__actions">操作</th></tr></thead><tbody><tr v-for="item in pagedItems" :key="item.appId" :class="{ 'is-selected': appId === item.appId }"><td><div class="data-grid__person"><span class="avatar sm">{{ item.name.slice(0, 1) }}</span><a :href="`/manage/applications/${item.appId}`" :aria-current="appId === item.appId ? 'page' : undefined" @click="navigate($event, item.appId)">{{ item.name }}</a></div></td><td class="muted"><code>{{ item.appId }}</code></td><td class="muted">{{ item.category }}</td><td class="muted">{{ item.global ? '发布管理' : '' }}{{ item.global && item.tenant ? ' · ' : '' }}{{ item.tenant ? '租户管理' : '' }}</td><td class="data-grid__actions"><div class="data-grid__action-content"><a class="link-btn" :href="`/manage/applications/${item.appId}`" @click="navigate($event, item.appId)">查看</a></div></td></tr></tbody></table>
        </div></div><ReferencePagination :current-page="page" v-model:page-size="pageSize" :total="filteredItems.length" :disabled="writing" @change="page = $event" />
      </section>
      <ReferenceDrawer :open="!!(appId || editor)" :title="editor ? (editor.mode === 'create' ? '注册应用' : editor.mode === 'version' ? '新建版本' : '编辑草稿') : application?.name || tenantApplication?.name || '应用详情'" kicker="应用 · 发布管理" :disabled="writing" @close="closeDetail"><div class="registry-detail"><div v-if="error" class="registry-error" role="alert"><span>{{ error }}</span><ReferenceButton :disabled="loading || writing" @click="refresh">重新读取状态</ReferenceButton></div><p v-if="notice" class="registry-notice" role="status">{{ notice }}</p>
        <RegistryDraftForm v-if="editor" :key="editor.key" :initial="editor.initial" :mode="editor.mode" :busy="writing || loading" :stale="stale" @save="save" @dirty="setDirty" @cancel="cancelEditor" />
        <section v-else-if="!appId" class="registry-intro"><h2>管理应用发布与租户使用</h2><p>从列表选择应用，查看发布版本、当前租户设置和操作记录。</p><p v-if="workspace?.canCreate">注册应用后，先保存草稿，再提交独立审核。</p><p v-if="!tenantId">尚未选择租户。全局发布管理仍可使用；选择工作空间后可查看租户管理权限。</p></section>
        <section v-else-if="missing && !loading" class="registry-intro"><h2>应用不可管理</h2><p>应用不存在，或当前账号没有该应用的管理权限。</p></section>
        <section v-else-if="loading && !application && !tenantApplication" class="registry-loading" role="status">正在读取应用…<i v-for="n in 3" :key="n" /></section>
        <template v-else-if="application || tenantApplication">
          <header class="registry-app-heading"><div><p>{{ appId }}</p><span>{{ application?.category || tenantApplication?.category }}</span></div><ReferenceButton v-if="canEdit && release" :disabled="loading || writing || forcedStale" @click="edit('version')">新建版本</ReferenceButton></header>
          <section class="registry-section" aria-labelledby="registry-release-heading">
            <div class="registry-section-heading"><div><h3 id="registry-release-heading">{{ application ? '发布版本' : '已发布版本' }}</h3><p>{{ application ? '全局发布状态决定版本是否可以被租户启用。' : '你可查看并为当前租户选择已发布版本。' }}</p></div><label class="subwindow-field registry-version-picker"><span>查看发布版本</span><ReferenceSelect v-model="selectedVersion" label="查看发布版本" :disabled="loading || writing || forcedStale" :options="availableReleases.map(item => ({ value: item.manifest.version, label: `${item.manifest.version} · ${statusNames[item.manifest.status]}` }))" /></label></div>
            <template v-if="release"><div class="registry-release-summary"><span class="status-pill" :class="{ draft: release.manifest.status !== 'published' }" :data-status="release.manifest.status"><i class="sdot"></i>{{ statusNames[release.manifest.status] }}</span><span>v{{ release.manifest.version }}</span><span class="registry-muted">{{ release.manifest.description || '尚无版本说明' }}</span></div>
              <div class="registry-actions"><ReferenceButton v-if="canEdit && release.manifest.status === 'draft'" :disabled="loading || writing || forcedStale" @click="edit('edit')">编辑草稿</ReferenceButton><ReferenceButton v-for="action in actions" :key="action" :primary="action === 'publish' || action === 'submit'" :disabled="loading || writing || forcedStale" @click="transition(action)">{{ actionNames[action] }}</ReferenceButton></div>
              <p v-if="release.manifest.status === 'pending_review' && canReview && release.submittedBy === workspace?.actorUserId" class="registry-help">此版本由你提交，需要另一位审核人发布。</p>
              <dl class="registry-facts"><div><dt>前端版本</dt><dd>{{ release.manifest.frontendArtifact.version }}</dd></div><div><dt>后端服务 / 版本</dt><dd>{{ release.manifest.backendService }} / {{ release.manifest.backendVersion }}</dd></div><div><dt>首次发布</dt><dd>{{ date(release.publishedAt) }}</dd></div><div><dt>入口权限</dt><dd>{{ release.manifest.permissions.join('、') }}</dd></div></dl>
              <details class="registry-deployment"><summary>查看发布物与提交信息</summary><dl><div><dt>前端入口</dt><dd><code>{{ release.manifest.frontendEntry }}</code></dd></div><div><dt>SHA256</dt><dd><code>{{ release.manifest.frontendArtifact.sha256 }}</code></dd></div><div><dt>应用所有者</dt><dd><code>{{ release.ownerUserId }}</code></dd></div><div><dt>提交者</dt><dd><code>{{ release.submittedBy || '尚未提交' }}</code></dd></div></dl></details>
            </template><p v-else class="registry-empty">暂时没有可见的发布版本。</p>
          </section>
          <section class="registry-section" aria-labelledby="registry-tenant-heading"><div class="registry-section-heading"><div><h3 id="registry-tenant-heading">当前租户</h3><p>租户启用状态与全局发布状态分别管理。</p></div></div>
            <p v-if="!tenantId" class="registry-empty">请从工作空间选择租户后查看配置。</p>
            <p v-else-if="!tenantApplication" class="registry-empty">你没有当前租户中此应用的管理权限。</p>
            <template v-else><div class="registry-tenant-current"><strong>{{ !tenantApplication.current ? '尚未配置' : !tenantApplication.current.enabled ? '租户已停用' : tenantApplication.current.available ? '租户已启用' : '全局版本不可用' }}</strong><span v-if="tenantApplication.current">当前版本 {{ tenantApplication.current.releaseVersion }}</span></div><div class="registry-tenant-controls"><label class="subwindow-field">租户使用版本<ReferenceSelect v-model="tenantVersion" label="租户使用版本" :disabled="loading || writing || forcedStale" :options="[{ value: '', label: '选择已发布版本', disabled: true }, ...tenantReleases.map(item => ({ value: item.manifest.version, label: item.manifest.version }))]" /></label><ReferenceButton primary :disabled="loading || writing || forcedStale || !tenantVersion || (tenantApplication.current?.enabled && tenantApplication.current?.releaseVersion === tenantVersion)" @click="configureTenant(true)">{{ tenantApplication.current?.enabled ? '应用版本到当前租户' : '启用到当前租户' }}</ReferenceButton><ReferenceButton v-if="tenantApplication.current?.enabled" :disabled="loading || writing || forcedStale" @click="configureTenant(false)">停用当前租户应用</ReferenceButton></div><p class="registry-help">选择仍已发布的旧版本即可回滚。切换后的新运行使用所选版本，旧运行按平台排空策略结束。</p><p v-if="!tenantReleases.length" class="registry-help">没有可选的已发布版本，请等待发布审核完成。</p></template>
          </section>
          <section class="registry-section" aria-labelledby="registry-audit-heading"><div class="registry-section-heading"><div><h3 id="registry-audit-heading">操作记录</h3><p>记录本应用的操作主体、结果与关联编号。</p></div></div><p v-if="!audits.length" class="registry-empty">当前可见范围内暂无操作记录。</p><div v-else class="data-grid registry-audit-scroll"><div class="admin-table-wrap data-grid__body"><div class="scroll-content data-grid__scroll"><table class="data-grid__table" aria-label="注册操作记录" data-testid="registry-audit"><thead><tr><th>操作 / 时间</th><th>范围 / 版本</th><th>操作主体</th><th>结果 / 关联编号</th></tr></thead><tbody><tr v-for="item in audits" :key="item.eventId"><td>{{ auditNames[item.action] || item.action }}<small>{{ date(item.occurredAt) }}</small></td><td>{{ item.tenantId ? '当前租户' : '全局发布' }}<small>{{ item.releaseVersion }}</small></td><td><code>{{ item.actorUserId }}</code></td><td>{{ resultNames[item.result] || item.result }}<code>{{ item.requestId }}</code></td></tr></tbody></table></div></div></div><p v-if="auditHasMore" class="registry-help">仅显示每个可见范围最近 50 条记录，还有更早的操作未展示。</p></section>
        </template>
      </div><template v-if="editor" #footer><ReferenceButton :disabled="writing" @click="cancelEditor">取消编辑</ReferenceButton><ReferenceButton primary type="submit" form="registry-draft-form" :disabled="writing || loading || stale">{{ writing ? '正在保存…' : '保存草稿' }}</ReferenceButton></template></ReferenceDrawer>
    </div>
    <dialog ref="dialog" class="overlay is-open is-dialog registry-confirm" aria-labelledby="registry-confirm-title" @cancel="confirmation = null"><template v-if="confirmation"><div class="overlay-backdrop"></div><div class="dialog"><h2 class="dialog-title" id="registry-confirm-title">{{ confirmation.title }}</h2><p class="dialog-desc">{{ confirmation.description }}</p><div class="dialog-actions"><ReferenceButton @click="dialog?.close(); confirmation = null">取消</ReferenceButton><ReferenceButton primary @click="executeConfirmation">确认{{ confirmation.title }}</ReferenceButton></div></div></template></dialog>
    <ReferenceConfirm :request="decision" @resolve="settleConfirmation" />
  </section>
</template>
