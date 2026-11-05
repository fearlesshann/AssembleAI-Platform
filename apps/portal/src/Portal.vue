<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { ReferenceButton, ReferenceSelect } from './components/reference';
import ReferenceConfirm from './components/reference/ReferenceConfirm.vue';
import { useReferenceConfirm } from './components/reference/useReferenceConfirm';
const { confirmation, requestConfirmation, settleConfirmation } = useReferenceConfirm();
import ApplicationRunner from './ApplicationRunner.vue';
import { portalApi, PortalApiError, type PortalApp, type PortalRun, type PortalSession, type PortalTenant, type Theme } from './portal-api';
const RegistryWorkspace = defineAsyncComponent(() => import('./RegistryWorkspace.vue'));
const IdentityWorkspace = defineAsyncComponent(() => import('./IdentityWorkspace.vue'));
const ResourceWorkspace = defineAsyncComponent(() => import('./ResourceWorkspace.vue'));
const AuditWorkspace = defineAsyncComponent(() => import('./AuditWorkspace.vue'));
import { identityApi, IdentityApiError, type IdentityWorkspaceData, type IdentitySection } from './identity-api';
import { resourceApi, ResourceApiError } from './resource-api';
import { auditApi, AuditApiError, auditSources, type AuditSource, type AuditScope } from './audit-api';
const identityWorkspace = ref<IdentityWorkspaceData | null>(null);
const identityNames: Record<IdentitySection, string> = { users: '用户管理', tenants: '租户管理', members: '租户成员', organizations: '组织管理', roles: '角色权限' };
const resourceNames = { models: '模型管理', 'data-sources': '数据源管理' };
const resourceNavigation = ref<(keyof typeof resourceNames)[]>([]);
const auditNavigation = ref('');

const theme = ref<Theme>(document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light');
const session = ref<PortalSession | null>(null);
const tenants = ref<PortalTenant[]>([]);
const tenant = ref('');
const items = ref<PortalApp[]>([]);
const categories = ref<string[]>([]);
const search = ref('');
const category = ref('');
const detail = ref<PortalApp | null>(null);
const run = ref<PortalRun | null>(null);
const mode = ref<'boot' | 'ready' | 'signed-out' | 'error'>('boot');
const busy = ref('');
const message = ref('');
const detailError = ref('');
const path = ref(location.pathname);
const routeQuery = ref(location.search);
const managementBusy = ref(false);
const managementDirty = ref(false);
const managementRefresh = ref(0);
const managementInstance = ref(0);
const pageHeading = ref<HTMLElement>();
const sidebarCollapsed = ref(false);
const userMenuOpen = ref(false);
const tenantPicker = ref<HTMLElement>();
let generation = 0;
let controller: AbortController | undefined;
let expiryTimer: number | undefined;
let focusTimer: number | undefined;
let disposed = false;

const route = computed(() => {
  if (path.value === '/') return { kind: 'list', appId: '' };
  const audit = new RegExp(`^/manage/audit(?:/(global))?(?:/(${auditSources.join('|')})/([0-9a-f]{32}))?/?$`).exec(path.value);
  if (audit) return { kind: 'audit', appId: '', auditScope: (audit[1] ? 'global' : 'tenant') as AuditScope, recordSource: (audit[2] ?? '') as AuditSource | '', recordId: audit[3] ?? '' };
  const resource = /^\/manage\/(models|data-sources)(?:\/([a-z][a-z0-9._-]{0,63}))?\/?$/.exec(path.value);
  if (resource) return { kind: 'resource', appId: '', resourceSection: resource[1] as keyof typeof resourceNames, resourceId: resource[2] ?? '' };
  const identity = /^\/manage\/(users|tenants|members|organizations|roles)(?:\/([0-9a-f-]{36}))?\/?$/i.exec(path.value);
  const scoped = /^\/manage\/tenants\/([0-9a-f-]{36})\/(members|organizations|roles)(?:\/([0-9a-f-]{36}))?\/?$/i.exec(path.value);
  if (scoped) return { kind: 'identity', appId: '', section: scoped[2] as IdentitySection, entityId: scoped[3] ?? '', managementTenant: scoped[1] };
  if (identity) return { kind: 'identity', appId: '', section: identity[1] as IdentitySection, entityId: identity[2] ?? '', managementTenant: '' };
  const management = /^\/manage\/applications(?:\/([a-z][a-z0-9-]{0,63}))?\/?$/.exec(path.value);
  if (management) return { kind: 'management', appId: management[1] ?? '' };
  const match = /^\/applications\/([a-z][a-z0-9-]{0,63})(\/run)?\/?$/.exec(path.value);
  return match ? { kind: match[2] ? 'run' : 'detail', appId: match[1] } : { kind: 'missing', appId: '' };
});
const tenantName = computed(() => tenants.value.find(item => item.tenantId === tenant.value)?.name ?? '选择租户');
const shown = computed(() => {
  const query = search.value.trim().toLocaleLowerCase();
  return items.value.filter(item => (!category.value || item.category === category.value)
    && (!query || `${item.name} ${item.description} ${item.appId}`.toLocaleLowerCase().includes(query)));
});
const writing = computed(() => managementBusy.value || ['switch', 'start', 'logout'].includes(busy.value));
const managing = computed(() => ['management', 'identity', 'resource', 'audit'].includes(route.value.kind));
const title = computed(() => route.value.kind === 'audit' ? '调用与审计' : route.value.kind === 'resource' ? resourceNames[route.value.resourceSection!] : route.value.kind === 'identity' ? identityNames[route.value.section!] : managing.value ? '应用管理' : '应用中心');
const breadcrumbScope = computed(() => {
  if (route.value.kind === 'audit') return route.value.auditScope === 'global' ? '平台操作' : tenantName.value;
  if (route.value.kind === 'resource') return tenantName.value;
  if (route.value.kind === 'identity') {
    if (route.value.section === 'users' || route.value.section === 'tenants') return '平台管理';
    const managedId = route.value.managementTenant || tenant.value;
    const managed = identityWorkspace.value?.tenants.find(item => item.tenantId === managedId);
    return managed ? `管理租户：${managed.name}` : '租户管理';
  }
  return managing.value && !tenant.value ? '平台管理' : tenantName.value;
});
const identityNavigationTenant = computed(() => {
  const scope = route.value.managementTenant || tenant.value;
  return identityWorkspace.value?.tenants.find(x => x.tenantId === scope)?.tenantId || (identityWorkspace.value?.platformAdministrator ? identityWorkspace.value.tenants[0]?.tenantId : '') || '';
});
const identityNavigation = computed(() => {
  const links: { name: string; path: string; section: IdentitySection }[] = [];
  if (identityWorkspace.value?.platformAdministrator) for (const section of ['users', 'tenants'] as const) links.push({ name: identityNames[section], path: `/manage/${section}`, section });
  if (identityNavigationTenant.value) for (const section of ['members', 'organizations', 'roles'] as const) links.push({ name: identityNames[section], path: `/manage/tenants/${identityNavigationTenant.value}/${section}`, section });
  return links;
});
async function readManagementNavigation(id: number, signal: AbortSignal) {
  try {
    const value = await identityApi.workspace(signal);
    if (current(id)) identityWorkspace.value = typeof value.platformAdministrator === 'boolean' && Array.isArray(value.tenants) ? value : null;
  } catch (failure) {
    if (!current(id)) return;
    identityWorkspace.value = null;
    if (failure instanceof IdentityApiError && failure.status === 401) throw new PortalApiError(401);
  }
}
async function readResourceNavigation(id: number, signal: AbortSignal, selected: string) {
  if (!selected) { if (current(id)) resourceNavigation.value = []; return; }
  const results = await Promise.allSettled([resourceApi.models(selected, signal), resourceApi.sources(selected, signal)]);
  if (!current(id)) return;
  const available: (keyof typeof resourceNames)[] = [];
  for (const [index, result] of results.entries()) {
    if (result.status === 'fulfilled') {
      if (Array.isArray(result.value?.items) && result.value.items.length) available.push(index === 0 ? 'models' : 'data-sources');
    } else if (result.reason instanceof ResourceApiError && result.reason.status === 401) throw new PortalApiError(401);
  }
  resourceNavigation.value = available;
}
async function readAuditNavigation(id: number, signal: AbortSignal, selected: string) {
  try {
    const value = await auditApi.workspace(selected ? 'tenant' : 'global', signal);
    if (!current(id)) return;
    auditNavigation.value = value.sources.length ? (selected ? '/manage/audit' : '/manage/audit/global') : value.globalAvailable ? '/manage/audit/global' : '';
  } catch (failure) {
    if (!current(id)) return;
    auditNavigation.value = '';
    if (failure instanceof AuditApiError && failure.status === 401) throw new PortalApiError(401);
  }
}
const managementKey = computed(() => JSON.stringify([session.value?.iss, session.value?.sub, tenant.value, managementInstance.value]));
const loginHref = computed(() => `/bff/login?returnUrl=${encodeURIComponent(route.value.kind === 'missing' ? '/' : path.value + routeQuery.value)}&prompt=login`);
const currentRun = computed(() => route.value.kind === 'run' && run.value?.appId === route.value.appId ? run.value : null);

function begin(action: string) {
  controller?.abort();
  controller = new AbortController();
  const id = ++generation;
  busy.value = action;
  return { id, signal: controller.signal };
}
const current = (id: number) => !disposed && generation === id;
function clearData() {
  auditNavigation.value = '';
  resourceNavigation.value = [];
  items.value = []; categories.value = []; detail.value = null; run.value = null;
  detailError.value = ''; search.value = ''; category.value = '';
}
function signedOut() {
  managementDirty.value = false; managementBusy.value = false;
  identityWorkspace.value = null; clearData(); session.value = null; tenants.value = []; tenant.value = ''; mode.value = 'signed-out'; message.value = '';
}
function errorMessage(error: unknown, action = '加载') {
  const status = error instanceof PortalApiError ? error.status : 503;
  if (status === 403) return '访问权限已变化，请重新选择租户或联系管理员。';
  if (status === 404) return '此应用暂不可用，可能已停用或调整了访问权限。';
  if (status === 409) return '应用版本已更新，请刷新详情后重新打开。';
  if (status === 429) return '当前请求较多，请稍后再试。';
  return `${action}暂时失败，请稍后重试。`;
}
function handleError(error: unknown, id: number, action: string) {
  if (!current(id)) return;
  run.value = null;
  if (error instanceof PortalApiError && error.status === 401) { signedOut(); return; }
  message.value = errorMessage(error, action);
}
async function loadCatalog(id: number, signal: AbortSignal, selected: string) {
  const catalog = await portalApi.catalog(selected, signal);
  if (!current(id)) return;
  items.value = catalog.items;
  categories.value = catalog.categories;
  if (category.value && !categories.value.includes(category.value)) category.value = '';
  if (run.value && !items.value.some(item => item.appId === run.value?.appId)) run.value = null;
  detail.value = null; detailError.value = '';
  if (route.value.appId) {
    try {
      const result = await portalApi.detail(selected, route.value.appId, signal);
      if (current(id)) detail.value = result;
    } catch (error) {
      if (!current(id)) return;
      if (error instanceof PortalApiError && error.status === 404) {
        detailError.value = errorMessage(error); run.value = null;
      } else throw error;
    }
  }
}
async function refresh(quiet = false) {
  if (quiet && (writing.value || mode.value === 'boot')) return;
  const { id, signal } = begin('load');
  message.value = '';
  if (!quiet) { items.value = []; categories.value = []; detail.value = null; run.value = null; }
  try {
    const identity = await portalApi.session(signal);
    if (!current(id)) return;
    if (session.value && (identity.iss !== session.value.iss || identity.sub !== session.value.sub)) {
      identityWorkspace.value = null; clearData(); tenants.value = []; tenant.value = '';
    }
    session.value = identity;
    const [memberships, selection] = await Promise.all([
      portalApi.tenants(signal),
      portalApi.selection(signal).catch(error => {
        if (error instanceof PortalApiError && error.status === 403) return { tenantId: null };
        throw error;
      }),
    ]);
    if (!current(id)) return;
    tenants.value = memberships.items;
    const selected = selection.tenantId && tenants.value.some(item => item.tenantId === selection.tenantId) ? selection.tenantId : '';
    if (selected !== tenant.value) clearData();
    tenant.value = selected;
    await Promise.all([readManagementNavigation(id, signal), readResourceNavigation(id, signal, selected), readAuditNavigation(id, signal, selected)]);
    if (!current(id)) return;
    mode.value = 'ready';
    if (selected && !managing.value) await loadCatalog(id, signal, selected);
    if (managing.value && current(id)) managementRefresh.value++;
    if (current(id) && new URLSearchParams(location.search).get('runError') === 'timeout')
      message.value = '上次应用未能及时完成加载或退出，页面已恢复，请重新打开。';
    if (current(id) && new URLSearchParams(location.search).get('runError') === 'cleanup')
      message.value = '上次应用退出未完成，页面已恢复，请重新打开。';
  } catch (error) {
    if (!current(id)) return;
    const lostDraft = managing.value && managementDirty.value;
    clearData();
    handleError(error, id, '加载');
    if (lostDraft) message.value += ' 未保存的草稿修改已清除，请重新加载后再编辑。';
    if (!(error instanceof PortalApiError && error.status === 401)) mode.value = 'error';
  } finally { if (current(id)) busy.value = ''; }
}
async function selectTenant(event: Event | string) {
  const selected = typeof event === 'string' ? event : (event.target as HTMLSelectElement).value;
  if (!selected || writing.value || selected === tenant.value) return;
  if (!await discardDraft()) { if (typeof event !== 'string') (event.target as HTMLSelectElement).value = tenant.value; return; }
  const { id, signal } = begin('switch');
  identityWorkspace.value = null; clearData(); tenant.value = ''; message.value = ''; mode.value = 'ready';
  try {
    await portalApi.select(selected, signal);
    // Read the authoritative selection after the write; a second tab may also change it.
    const selection = await portalApi.selection(signal);
    if (!current(id)) return;
    if (!selection.tenantId || !tenants.value.some(item => item.tenantId === selection.tenantId)) throw new PortalApiError(403);
    tenant.value = selection.tenantId;
    await Promise.all([readManagementNavigation(id, signal), readResourceNavigation(id, signal, tenant.value), readAuditNavigation(id, signal, tenant.value)]);
    if (!current(id)) return;
    if (route.value.kind === 'identity' && route.value.managementTenant) setPath(`/manage/${route.value.section}`);
    if (!managing.value) await loadCatalog(id, signal, tenant.value);
  } catch (error) { handleError(error, id, '切换租户'); }
  finally { if (current(id)) busy.value = ''; }
}
function setPath(next: string, replace = false) {
  // single-spa synthesizes popstate for pushState after its first mount. Publish
  // our path first so the listener does not mistake our own navigation for Back.
  path.value = new URL(next, location.origin).pathname;
  routeQuery.value = new URL(next, location.origin).search;
  if (replace) history.replaceState({}, '', next); else history.pushState({}, '', next);
  path.value = location.pathname;
  routeQuery.value = location.search;
}
async function navigate(next: string) {
  const discardManagement = managing.value && managementDirty.value;
  if (writing.value || !await discardDraft()) return;
  // The sidebar may navigate to this same URL. A confirmed discard must also
  // discard the child editor instead of only clearing the parent's dirty flag.
  if (discardManagement) managementInstance.value++;
  run.value = null;
  setPath(next);
  await refresh();
  await nextTick();
  pageHeading.value?.focus();
}
function follow(event: MouseEvent, next: string) {
  if (event.button || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
  event.preventDefault(); void navigate(next);
}
async function openApplication() {
  if (!detail.value || !tenant.value || busy.value) return;
  const target = detail.value;
  const selected = tenant.value;
  const { id, signal } = begin('start');
  message.value = ''; run.value = null;
  try {
    const started = await portalApi.start(selected, target, signal);
    if (!current(id)) return;
    if (started.tenantId !== selected || started.appId !== target.appId || started.releaseVersion !== target.releaseVersion
      || !Number.isFinite(Date.parse(started.expiresAt)) || Date.parse(started.expiresAt) <= Date.now()) throw new PortalApiError(503);
    run.value = started;
    setPath(`/applications/${encodeURIComponent(target.appId)}/run`);
  } catch (error) { handleError(error, id, '打开应用'); }
  finally { if (current(id)) busy.value = ''; }
}
async function logout() {
  if (writing.value || !await discardDraft()) return;
  const { id, signal } = begin('logout');
  clearData(); message.value = '';
  try {
    await portalApi.logout(signal);
    if (current(id)) { signedOut(); setPath('/', true); }
  } catch (error) {
    handleError(error, id, '退出登录');
    if (mode.value !== 'signed-out') { mode.value = 'error'; message.value = '退出登录未完成，请重试。'; }
  } finally { if (current(id)) busy.value = ''; }
}
function openWorkspace() {
  if (sidebarCollapsed.value) { sidebarCollapsed.value = false; return; }
  tenantPicker.value?.querySelector<HTMLButtonElement>('button')?.click();
}
function focusNavigation(event: FocusEvent) {
  if (window.matchMedia('(max-width: 760px)').matches && event.target instanceof HTMLElement && event.target.matches('.nav-item'))
    event.target.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'auto' });
}
function toggleTheme() {
  theme.value = theme.value === 'light' ? 'dark' : 'light';
  document.documentElement.dataset.theme = theme.value;
  try { localStorage.setItem('assembleai.theme', theme.value); } catch { /* Display preference is optional. */ }
}
function revisit() {
  if (document.visibilityState === 'hidden' || disposed || mode.value === 'boot' || writing.value) return;
  window.clearTimeout(focusTimer);
  focusTimer = window.setTimeout(() => void refresh(true), 120);
}
async function historyChanged() {
  if (path.value === location.pathname && routeQuery.value === location.search) return;
  const destination = location.pathname + location.search;
  const previous = path.value + routeQuery.value;
  if (writing.value) { history.pushState({}, '', previous); return; }
  // Keep the current URL while the source dialog is open, including cancellation.
  if (managementDirty.value) history.replaceState({}, '', previous);
  if (!await discardDraft()) return;
  setPath(destination, true); run.value = null; void refresh();
}
async function discardDraft() {
  if (!managementDirty.value) return true;
  const before = generation;
  if (!await requestConfirmation({ title: '放弃修改？', description: '有尚未保存的草稿修改。放弃修改并继续？', confirmLabel: '放弃修改' })
    || !current(before) || writing.value) return false;
  managementDirty.value = false;
  return true;
}
async function refreshPage() {
  if (writing.value || !await discardDraft()) return;
  if (managing.value) managementInstance.value++;
  void refresh();
}
function beforeUnload(event: BeforeUnloadEvent) {
  if (managementDirty.value) { event.preventDefault(); event.returnValue = ''; }
}
function managementUnauthorized() { controller?.abort(); generation++; busy.value = ''; signedOut(); }
watch(mode, value => { if (value === 'signed-out') { userMenuOpen.value = false; window.location.replace(loginHref.value); } });
function closeUserMenu(event: Event) { if (!(event.target instanceof Element) || !event.target.closest('.user-menu')) userMenuOpen.value = false; }
function keydown(event: KeyboardEvent) { if (event.key === 'Escape') userMenuOpen.value = false; }
watch(title, value => { document.title = `集智 · ${value}`; }, { immediate: true });
watch(run, value => {
  window.clearTimeout(expiryTimer);
  if (value) expiryTimer = window.setTimeout(() => { run.value = null; message.value = '本次运行已到期，请重新打开应用。'; }, Math.max(0, Date.parse(value.expiresAt) - Date.now()));
});
onMounted(() => {
  document.addEventListener('pointerdown', closeUserMenu);
  document.addEventListener('keydown', keydown);
  void refresh();
  window.addEventListener('popstate', historyChanged);
  window.addEventListener('focus', revisit);
  window.addEventListener('pageshow', revisit);
  document.addEventListener('visibilitychange', revisit);
  window.addEventListener('beforeunload', beforeUnload);
});
onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', closeUserMenu);
  document.removeEventListener('keydown', keydown);
  disposed = true; controller?.abort(); window.clearTimeout(expiryTimer); window.clearTimeout(focusTimer);
  window.removeEventListener('popstate', historyChanged);
  window.removeEventListener('focus', revisit);
  window.removeEventListener('pageshow', revisit);
  document.removeEventListener('visibilitychange', revisit);
  window.removeEventListener('beforeunload', beforeUnload);
});
</script>

<template>
  <div class="reference-ui portal-root">
    <a class="portal-skip" href="#portal-main">跳至主要内容</a>
    <div class="page"><div class="frame portal-shell" :class="{ 'is-sidebar-collapsed': sidebarCollapsed }">
      <aside class="sidebar portal-sidebar" id="portal-sidebar" aria-label="平台导航" @focusin="focusNavigation">
        <button class="workspace portal-brand" type="button" aria-label="集智工作区" @click="openWorkspace">
          <img class="workspace-mark" src="/logo.png" width="22" height="22" alt="" />
          <span class="workspace-name">集智</span><span class="workspace-chevron" aria-hidden="true"><svg width="12" height="12" viewBox="0 0 12 12" fill="none"><path d="M3 4.5L6 7.5L9 4.5" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round" /></svg></span>
        </button>
        <div class="sidebar-scroll"><div class="scroll-content">
          <div ref="tenantPicker" class="nav-section portal-workspace"><span class="nav-section-label">工作空间</span><ReferenceSelect label="当前租户" size="small" :model-value="tenant" :options="[{ value: '', label: '选择租户', disabled: true }, ...tenants.map(item => ({value:item.tenantId,label:item.name}))]" :disabled="!session || writing || mode === 'boot'" @change="selectTenant" /></div>
          <div class="nav-section"><nav class="nav-list" aria-label="主导航"><a class="nav-item portal-nav-item" href="/" :class="{ 'is-active': !managing }" :aria-current="!managing ? 'page' : undefined" @click="follow($event, '/')"><span class="nav-icon" aria-hidden="true"><svg width="16" height="16" viewBox="0 0 16 16" fill="none"><rect x="1.5" y="1.5" width="5" height="5" rx="1.2" stroke="currentColor" stroke-width="1.3"/><rect x="9.5" y="1.5" width="5" height="5" rx="1.2" stroke="currentColor" stroke-width="1.3"/><rect x="1.5" y="9.5" width="5" height="5" rx="1.2" stroke="currentColor" stroke-width="1.3"/><rect x="9.5" y="9.5" width="5" height="5" rx="1.2" stroke="currentColor" stroke-width="1.3"/></svg></span><span class="nav-label">应用中心</span></a><a v-if="session" class="nav-item portal-nav-item" href="/manage/applications" :class="{ 'is-active': route.kind === 'management' }" :aria-current="route.kind === 'management' ? 'page' : undefined" @click="follow($event, '/manage/applications')"><span class="nav-icon" aria-hidden="true"><svg width="17" height="17" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.4" aria-hidden="true"><path d="M4 3h12v14H4zM7 7h6M7 10h6M7 13h3" /></svg></span><span class="nav-label">应用管理</span></a><a v-for="link in identityNavigation" :key="link.section" class="nav-item portal-nav-item" :href="link.path" :class="{ 'is-active': route.kind === 'identity' && route.section === link.section }" :aria-current="route.kind === 'identity' && route.section === link.section ? 'page' : undefined" @click="follow($event, link.path)"><span class="nav-icon" aria-hidden="true"><svg width="17" height="17" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.4" aria-hidden="true"><circle cx="10" cy="6" r="3" /><path d="M4 17v-2a6 6 0 0 1 12 0v2M7 17h6" /></svg></span><span class="nav-label">{{ link.name }}</span></a>
        <a v-for="section in resourceNavigation" :key="section" class="nav-item portal-nav-item" :href="`/manage/${section}`" :class="{ 'is-active': route.kind === 'resource' && route.resourceSection === section }" :aria-current="route.kind === 'resource' && route.resourceSection === section ? 'page' : undefined" @click="follow($event, `/manage/${section}`)"><span class="nav-icon" aria-hidden="true"><svg width="17" height="17" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.4" aria-hidden="true"><rect x="3" y="3" width="14" height="14" rx="2" /><path d="M3 8h14M8 8v9" /></svg></span><span class="nav-label">{{ resourceNames[section] }}</span></a>
        <a v-if="auditNavigation" class="nav-item portal-nav-item" :href="auditNavigation" :class="{ 'is-active': route.kind === 'audit' }" :aria-current="route.kind === 'audit' ? 'page' : undefined" @click="follow($event, auditNavigation)"><span class="nav-icon" aria-hidden="true"><svg width="16" height="16" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.4" aria-hidden="true"><path d="M4 3h12v14H4zM7 7h6M7 10h6M7 13h4" /></svg></span><span class="nav-label">调用与审计</span></a>
</nav></div>
        </div></div>
        <div class="sidebar-footer portal-sidebar-bottom">
          <button class="sidebar-collapse" type="button" :aria-expanded="!sidebarCollapsed" aria-controls="portal-sidebar" :title="sidebarCollapsed ? '展开侧边栏' : '收起侧边栏'" @click="sidebarCollapsed = !sidebarCollapsed"><span class="sidebar-collapse-icon" aria-hidden="true"><svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M9.5 3.5L6 8l3.5 4.5M3.5 2.5v11" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round" /></svg></span><span class="sidebar-collapse-label">{{ sidebarCollapsed ? '展开' : '收起' }}</span></button>
          <div v-if="session" class="user-menu" :class="{ 'is-open': userMenuOpen }"><button class="user-row portal-user" type="button" aria-label="用户菜单" aria-haspopup="menu" :aria-expanded="userMenuOpen" @click="userMenuOpen = !userMenuOpen"><span class="avatar portal-avatar" aria-hidden="true">{{ (session.name || '用').slice(0,1) }}</span><span class="user-name">{{ session.name || '已登录用户' }}</span></button><div class="user-menu-pop" role="menu" :hidden="!userMenuOpen"><button class="user-menu-item" type="button" role="menuitem" :disabled="writing" @click="logout">{{ busy === 'logout' ? '正在退出…' : '退出登录' }}</button></div></div>
        </div>
      </aside>
      <div class="surface portal-content">
        <header class="topbar portal-topbar"><div class="breadcrumb portal-breadcrumb"><span class="crumb-id">{{ session ? breadcrumbScope : '集智工作空间' }}</span><span class="crumb-title">{{ title }}</span></div><div class="topbar-actions"><button class="theme-toggle" type="button" @click="toggleTheme" :aria-label="theme === 'light' ? '切换暗色' : '切换浅色'" title="切换主题"><svg class="theme-icon-sun" width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true"><circle cx="8" cy="8" r="3.25" stroke="currentColor" stroke-width="1.3"/><path d="M8 1.5v1.4M8 13.1v1.4M1.5 8h1.4M13.1 8h1.4M3.05 3.05l.99.99M11.96 11.96l.99.99M12.95 3.05l-.99.99M4.04 11.96l-.99.99" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/></svg><svg class="theme-icon-moon" width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M12.6 9.35A5.25 5.25 0 0 1 6.65 3.4 5.5 5.5 0 1 0 12.6 9.35z" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/></svg></button></div></header>
        <main id="portal-main" tabindex="-1" class="portal-main list-view" :class="{ 'is-management': managing }">
          <div class="list-toolbar portal-page-heading"><h1 class="list-title" ref="pageHeading" tabindex="-1">{{ title }}</h1><span v-if="mode === 'ready' && !!tenant && !managing" class="list-meta portal-heading-count">{{ items.length }} 个应用</span><div v-if="mode === 'ready' && !!tenant && !managing" class="list-search portal-search"><svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true"><circle cx="7" cy="7" r="4.5" stroke="currentColor" stroke-width="1.3"/><path d="M10.5 10.5L14 14" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/></svg><input v-model="search" type="search" aria-label="搜索应用" placeholder="搜索应用…" :disabled="busy === 'switch'" /></div><ReferenceButton v-if="session" :disabled="!!busy || writing" @click="refreshPage">{{ managing ? '刷新管理数据' : '刷新应用' }}</ReferenceButton></div>
        <div v-if="message" class="portal-feedback" role="alert"><span>{{ message }}</span><ReferenceButton v-if="mode === 'error' && session" @click="refresh()" :disabled="!!busy">重试</ReferenceButton></div>
        <section v-if="mode === 'boot'" class="portal-skeleton" role="status" aria-label="正在加载应用中心"><span>正在加载工作空间…</span><div v-for="row in 3" :key="row" class="portal-skeleton-row"><i></i><div><b></b><em></em></div></div></section>
        <section v-else-if="mode === 'signed-out'" class="portal-state" role="status"><h2>正在进入登录页…</h2><a :href="loginHref" class="pill-btn">企业账号登录</a></section>
        <section v-else-if="mode === 'error'" class="portal-state"><h2>暂时无法加载工作空间</h2><p>请检查连接后重试。已有登录状态不会被当作退出。</p><ReferenceButton :disabled="!!busy" @click="refresh()">重新加载</ReferenceButton></section>
        <template v-else-if="managing"><section v-if="busy === 'switch' || busy === 'logout'" class="portal-state" role="status">正在更新会话…</section><IdentityWorkspace v-else-if="route.kind === 'identity'" :key="managementKey" :section="route.section!" :entity-id="route.entityId || ''" :tenant-id="route.managementTenant || tenant || null" :explicit-tenant="!!route.managementTenant" :refresh-key="managementRefresh" @changed="refresh(true)" @navigate="navigate" @unauthorized="managementUnauthorized" @busy="managementBusy = $event" @dirty="managementDirty = $event" /><AuditWorkspace v-else-if="route.kind === 'audit'" :key="managementKey" :scope="route.auditScope!" :tenant-id="tenant" :record-source="route.recordSource || ''" :record-id="route.recordId || ''" :query-string="routeQuery" :refresh-token="managementRefresh" @navigate="navigate" @unauthorized="managementUnauthorized" @busy="managementBusy = $event" /><ResourceWorkspace v-else-if="route.kind === 'resource'" :key="managementKey" :section="route.resourceSection!" :resource-id="route.resourceId || ''" :tenant-id="tenant" :refresh-token="managementRefresh" @navigate="navigate" @unauthorized="managementUnauthorized" @busy="managementBusy = $event" @dirty="managementDirty = $event" /><RegistryWorkspace v-else :key="managementKey" :tenant-id="tenant || null" :app-id="route.appId" :refresh-key="managementRefresh" @navigate="navigate" @unauthorized="managementUnauthorized" @busy="managementBusy = $event" @dirty="managementDirty = $event" /></template>
        <section v-else-if="!tenants.length" class="portal-state"><h2>尚未加入租户</h2><p>你的企业账号已登录。请联系管理员添加租户成员关系后刷新。</p><ReferenceButton :disabled="!!busy" @click="refresh()">重新加载</ReferenceButton></section>
        <section v-else-if="!tenant" class="portal-state"><h2>{{ busy === 'switch' ? '正在切换租户…' : '选择租户' }}</h2><p>从工作空间选择当前租户，查看你可以使用的应用。</p></section>
        <section v-else-if="route.kind === 'missing'" class="portal-state"><h2>页面不存在</h2><p>请返回应用列表继续访问。</p><ReferenceButton @click="navigate('/')">应用列表</ReferenceButton></section>
        <template v-else-if="route.kind === 'run'">
          <div class="portal-run-heading"><a :href="`/applications/${route.appId}`" @click="follow($event, `/applications/${route.appId}`)">← 返回应用详情</a><span>{{ detail?.name }}</span></div>
          <ApplicationRunner v-if="currentRun" :key="currentRun.runId" :run="currentRun" :theme="theme" @leave="navigate(`/applications/${route.appId}`)" />
          <section v-else class="portal-state"><h2>请重新打开应用</h2><p>{{ detailError || '运行不会在刷新页面后自动恢复。返回详情，重新检查访问权限并打开应用。' }}</p><ReferenceButton @click="navigate(`/applications/${route.appId}`)">返回应用详情</ReferenceButton></section>
        </template>
        <div v-else class="portal-catalog-layout" :class="{ 'has-detail': route.kind === 'detail' }">
          <section class="portal-catalog list-view" aria-label="应用列表"><h2 class="sr-only">我的应用</h2>
            <div class="filter-row portal-categories" aria-label="应用分类"><button class="filter-chip" :class="{ 'is-active': !category }" type="button" :aria-pressed="!category" @click="category = ''">全部应用</button><button v-for="item in categories" :key="item" class="filter-chip" :class="{ 'is-active': category === item }" type="button" :aria-pressed="category === item" @click="category = item">{{ item }}</button></div>
            <div class="app-list" :class="{ 'is-loading': busy === 'load' }"><div v-if="busy === 'load'" class="skeleton-list" role="status" aria-label="正在读取应用"><div v-for="row in 5" :key="row" class="skeleton-row"><span class="sk sk-avatar"/><span class="sk sk-line grow"/><span class="sk sk-line short"/></div></div><div v-else-if="!shown.length" class="empty-state is-visible" role="status"><div class="empty-icon" aria-hidden="true">∅</div><h2 class="empty-title">{{ items.length ? '没有匹配的应用' : '当前租户暂无可用应用' }}</h2><p class="empty-desc">{{ items.length ? '试试调整关键词或清除筛选。' : '应用发布并获授权后，会显示在这里。' }}</p><ReferenceButton v-if="items.length" @click="search = ''; category = ''">清除筛选</ReferenceButton></div>
            <ul v-else class="scroll-content portal-app-list"><li v-for="app in shown" :key="app.appId" class="app-row portal-app-row" :class="{ selected: route.appId === app.appId }"><span class="app-icon portal-app-icon" style="--ic:var(--accent)" aria-hidden="true">{{ app.name.slice(0,1) }}</span><a class="app-row-title" :href="`/applications/${app.appId}`" @click="follow($event, `/applications/${app.appId}`)">{{ app.name }}</a><span class="app-row-meta">{{ app.category }} · v{{ app.releaseVersion }}</span><a class="pill-btn portal-detail-link" :href="`/applications/${app.appId}`" @click="follow($event, `/applications/${app.appId}`)">查看详情</a></li></ul></div>
            <div v-if="items.length" class="portal-list-footer"><span aria-live="polite">显示 {{ shown.length }} 个应用</span><span>仅展示当前租户已授权的应用</span></div>
          </section>
          <aside v-if="route.kind === 'detail'" class="props portal-inspector" aria-label="应用详情" :aria-busy="busy === 'load'"><div class="scroll-content"><div class="portal-inspector-top"><span class="props-label">应用详情</span><a class="icon-btn" href="/" @click="follow($event, '/')" aria-label="应用列表">×</a></div><template v-if="detail"><div class="props-block"><span class="props-label">{{ detail.category }}</span><h2>{{ detail.name }}</h2><p class="portal-detail-description">{{ detail.description }}</p></div><div class="props-block"><dl><div><dt>当前版本</dt><dd>{{ detail.releaseVersion }}</dd></div><div><dt>所属租户</dt><dd>{{ tenantName }}</dd></div><div><dt>应用标识</dt><dd><code>{{ detail.appId }}</code></dd></div></dl></div><ReferenceButton primary :disabled="!!busy" @click="openApplication">{{ busy === 'start' ? '正在打开…' : '打开应用' }}</ReferenceButton><p class="portal-run-note">打开时将再次确认你的访问权限与应用状态。</p></template><div v-else-if="detailError" role="status"><h2>应用不可用</h2><p>{{ detailError }}</p><ReferenceButton @click="navigate('/')">应用列表</ReferenceButton></div><p v-else role="status">正在加载详情…</p></div></aside>
        </div>
        </main>
      </div>
    </div></div>
    <ReferenceConfirm :request="confirmation" @resolve="settleConfirmation" />
  </div>
</template>
