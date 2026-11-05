<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import ElButton from 'element-plus/es/components/button/index';
import ElInput from 'element-plus/es/components/input/index';
import 'element-plus/es/components/button/style/css';
import 'element-plus/es/components/input/style/css';
import ApplicationRunner from './ApplicationRunner.vue';
import { portalApi, PortalApiError, type PortalApp, type PortalRun, type PortalSession, type PortalTenant, type Theme } from './portal-api';
const RegistryWorkspace = defineAsyncComponent(() => import('./RegistryWorkspace.vue'));
const IdentityWorkspace = defineAsyncComponent(() => import('./IdentityWorkspace.vue'));
import { identityApi, IdentityApiError, type IdentityWorkspaceData, type IdentitySection } from './identity-api';
const identityWorkspace = ref<IdentityWorkspaceData | null>(null);
const identityNames: Record<IdentitySection, string> = { users: '用户管理', tenants: '租户管理', members: '租户成员', organizations: '组织管理', roles: '角色权限' };

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
const managementBusy = ref(false);
const managementDirty = ref(false);
const managementRefresh = ref(0);
const managementInstance = ref(0);
const pageHeading = ref<HTMLElement>();
let generation = 0;
let controller: AbortController | undefined;
let expiryTimer: number | undefined;
let focusTimer: number | undefined;
let disposed = false;

const route = computed(() => {
  if (path.value === '/') return { kind: 'list', appId: '' };
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
const managing = computed(() => route.value.kind === 'management' || route.value.kind === 'identity');
const title = computed(() => route.value.kind === 'identity' ? identityNames[route.value.section!] : managing.value ? '应用管理' : '应用中心');
const breadcrumbScope = computed(() => {
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
const managementKey = computed(() => JSON.stringify([session.value?.iss, session.value?.sub, tenant.value, managementInstance.value]));
const loginHref = computed(() => `/bff/login?returnUrl=${encodeURIComponent(route.value.kind === 'missing' ? '/' : path.value)}`);
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
    await readManagementNavigation(id, signal);
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
async function selectTenant(event: Event) {
  const selected = (event.target as HTMLSelectElement).value;
  if (!selected || writing.value || selected === tenant.value) return;
  if (!discardDraft()) { (event.target as HTMLSelectElement).value = tenant.value; return; }
  const { id, signal } = begin('switch');
  identityWorkspace.value = null; clearData(); tenant.value = ''; message.value = ''; mode.value = 'ready';
  try {
    await portalApi.select(selected, signal);
    // Read the authoritative selection after the write; a second tab may also change it.
    const selection = await portalApi.selection(signal);
    if (!current(id)) return;
    if (!selection.tenantId || !tenants.value.some(item => item.tenantId === selection.tenantId)) throw new PortalApiError(403);
    tenant.value = selection.tenantId;
    await readManagementNavigation(id, signal);
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
  if (replace) history.replaceState({}, '', next); else history.pushState({}, '', next);
  path.value = location.pathname;
}
async function navigate(next: string) {
  const discardManagement = managing.value && managementDirty.value;
  if (writing.value || !discardDraft()) return;
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
  if (writing.value || !discardDraft()) return;
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
function historyChanged() {
  if (path.value === location.pathname) return;
  if (writing.value || !discardDraft()) { history.pushState({}, '', path.value); return; }
  path.value = location.pathname; run.value = null; void refresh();
}
function discardDraft() {
  if (!managementDirty.value) return true;
  if (!window.confirm('有尚未保存的草稿修改。放弃修改并继续？')) return false;
  managementDirty.value = false;
  return true;
}
function refreshPage() {
  if (writing.value || !discardDraft()) return;
  if (managing.value) managementInstance.value++;
  void refresh();
}
function beforeUnload(event: BeforeUnloadEvent) {
  if (managementDirty.value) { event.preventDefault(); event.returnValue = ''; }
}
function managementUnauthorized() { controller?.abort(); generation++; busy.value = ''; signedOut(); }
watch(title, value => { document.title = `集智 · ${value}`; }, { immediate: true });
watch(run, value => {
  window.clearTimeout(expiryTimer);
  if (value) expiryTimer = window.setTimeout(() => { run.value = null; message.value = '本次运行已到期，请重新打开应用。'; }, Math.max(0, Date.parse(value.expiresAt) - Date.now()));
});
onMounted(() => {
  void refresh();
  window.addEventListener('popstate', historyChanged);
  window.addEventListener('focus', revisit);
  window.addEventListener('pageshow', revisit);
  document.addEventListener('visibilitychange', revisit);
  window.addEventListener('beforeunload', beforeUnload);
});
onBeforeUnmount(() => {
  disposed = true; controller?.abort(); window.clearTimeout(expiryTimer); window.clearTimeout(focusTimer);
  window.removeEventListener('popstate', historyChanged);
  window.removeEventListener('focus', revisit);
  window.removeEventListener('pageshow', revisit);
  document.removeEventListener('visibilitychange', revisit);
  window.removeEventListener('beforeunload', beforeUnload);
});
</script>

<template>
  <div class="portal-shell">
    <a class="portal-skip" href="#portal-main">跳至主要内容</a>
    <aside class="portal-sidebar" aria-label="平台导航">
      <a class="portal-brand" href="/" @click="follow($event, '/')"><img src="/logo.png" width="28" height="28" alt="" /><span><strong>集智</strong><small>AssembleAI</small></span></a>
      <div class="portal-workspace"><span class="portal-label">工作空间</span><label for="portal-tenant" class="sr-only">当前租户</label><select id="portal-tenant" aria-label="当前租户" :value="tenant" :disabled="!session || writing || mode === 'boot'" @change="selectTenant"><option value="" disabled>选择租户</option><option v-for="item in tenants" :key="item.tenantId" :value="item.tenantId">{{ item.name }}</option></select></div>
      <nav aria-label="主导航"><a class="portal-nav-item" href="/" :aria-current="!managing ? 'page' : undefined" @click="follow($event, '/')"><svg width="17" height="17" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.4" aria-hidden="true"><rect x="2.5" y="2.5" width="5.5" height="5.5" rx="1" /><rect x="12" y="2.5" width="5.5" height="5.5" rx="1" /><rect x="2.5" y="12" width="5.5" height="5.5" rx="1" /><rect x="12" y="12" width="5.5" height="5.5" rx="1" /></svg>应用中心</a><a v-if="session" class="portal-nav-item" href="/manage/applications" :aria-current="route.kind === 'management' ? 'page' : undefined" @click="follow($event, '/manage/applications')"><svg width="17" height="17" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.4" aria-hidden="true"><path d="M4 3h12v14H4zM7 7h6M7 10h6M7 13h3" /></svg>应用管理</a><a v-for="link in identityNavigation" :key="link.section" class="portal-nav-item" :href="link.path" :aria-current="route.kind === 'identity' && route.section === link.section ? 'page' : undefined" @click="follow($event, link.path)"><svg width="17" height="17" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.4" aria-hidden="true"><circle cx="10" cy="6" r="3" /><path d="M4 17v-2a6 6 0 0 1 12 0v2M7 17h6" /></svg>{{ link.name }}</a></nav>
      <div class="portal-sidebar-bottom"><p>制造业 AI 应用平台</p><template v-if="session"><div class="portal-user"><span class="portal-avatar" aria-hidden="true">{{ (session.name || '用').slice(0, 1) }}</span><span class="portal-user-name">{{ session.name || '已登录用户' }}</span></div><ElButton text :disabled="writing" @click="logout">{{ busy === 'logout' ? '正在退出…' : '退出登录' }}</ElButton></template><span v-else class="portal-signed-out">企业应用，统一访问</span></div>
    </aside>
    <div class="portal-content">
      <header class="portal-topbar"><div class="portal-breadcrumb"><span>{{ session ? breadcrumbScope : '集智工作空间' }}</span><span aria-hidden="true">/</span><strong>{{ title }}</strong></div><ElButton text @click="toggleTheme" :aria-label="theme === 'light' ? '切换暗色' : '切换浅色'">{{ theme === 'light' ? '暗色外观' : '浅色外观' }}</ElButton></header>
      <main id="portal-main" tabindex="-1" class="portal-main">
        <div class="portal-page-heading"><div><h1 ref="pageHeading" tabindex="-1">{{ title }}</h1><p>{{ route.kind === 'identity' ? '管理企业身份、成员关系与访问范围。' : managing ? '维护应用发布与当前租户的使用版本。' : '找到适合当前工作的应用。' }}</p></div><ElButton v-if="session" :disabled="!!busy || writing" @click="refreshPage">{{ managing ? '刷新管理数据' : '刷新应用' }}</ElButton></div>
        <div v-if="message" class="portal-feedback" role="alert"><span>{{ message }}</span><ElButton v-if="mode === 'error' && session" @click="refresh()" :disabled="!!busy">重试</ElButton></div>
        <section v-if="mode === 'boot'" class="portal-skeleton" role="status" aria-label="正在加载应用中心"><span>正在加载工作空间…</span><div v-for="row in 3" :key="row" class="portal-skeleton-row"><i></i><div><b></b><em></em></div></div></section>
        <section v-else-if="mode === 'signed-out'" class="portal-entry"><div class="portal-entry-symbol" aria-hidden="true"><svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><path d="M12 3 4 6v6c0 4.2 4.6 7.6 8 9 3.4-1.4 8-4.8 8-9V6l-8-3Z" /><path d="m8.5 12 2.3 2.3 4.7-4.8" /></svg></div><h2>登录后，访问你的应用</h2><p>使用企业账号登录，查看当前租户为你开放的应用。</p><a :href="loginHref" class="portal-primary-link">企业账号登录 <span aria-hidden="true">→</span></a><p class="portal-entry-note">账号与访问权限由企业统一管理。</p></section>
        <section v-else-if="mode === 'error'" class="portal-state"><h2>暂时无法加载工作空间</h2><p>请检查连接后重试。已有登录状态不会被当作退出。</p><ElButton :disabled="!!busy" @click="refresh()">重新加载</ElButton></section>
        <template v-else-if="managing"><section v-if="busy === 'switch' || busy === 'logout'" class="portal-state" role="status">正在更新会话…</section><IdentityWorkspace v-else-if="route.kind === 'identity'" :key="managementKey" :section="route.section!" :entity-id="route.entityId || ''" :tenant-id="route.managementTenant || tenant || null" :explicit-tenant="!!route.managementTenant" :refresh-key="managementRefresh" @changed="refresh(true)" @navigate="navigate" @unauthorized="managementUnauthorized" @busy="managementBusy = $event" @dirty="managementDirty = $event" /><RegistryWorkspace v-else :key="managementKey" :tenant-id="tenant || null" :app-id="route.appId" :refresh-key="managementRefresh" @navigate="navigate" @unauthorized="managementUnauthorized" @busy="managementBusy = $event" @dirty="managementDirty = $event" /></template>
        <section v-else-if="!tenants.length" class="portal-state"><h2>尚未加入租户</h2><p>你的企业账号已登录。请联系管理员添加租户成员关系后刷新。</p><ElButton :disabled="!!busy" @click="refresh()">重新加载</ElButton></section>
        <section v-else-if="!tenant" class="portal-state"><h2>{{ busy === 'switch' ? '正在切换租户…' : '选择租户' }}</h2><p>从工作空间选择当前租户，查看你可以使用的应用。</p></section>
        <section v-else-if="route.kind === 'missing'" class="portal-state"><h2>页面不存在</h2><p>请返回应用列表继续访问。</p><ElButton @click="navigate('/')">应用列表</ElButton></section>
        <template v-else-if="route.kind === 'run'">
          <div class="portal-run-heading"><a :href="`/applications/${route.appId}`" @click="follow($event, `/applications/${route.appId}`)">← 返回应用详情</a><span>{{ detail?.name }}</span></div>
          <ApplicationRunner v-if="currentRun" :key="currentRun.runId" :run="currentRun" :theme="theme" @leave="navigate(`/applications/${route.appId}`)" />
          <section v-else class="portal-state"><h2>请重新打开应用</h2><p>{{ detailError || '运行不会在刷新页面后自动恢复。返回详情，重新检查访问权限并打开应用。' }}</p><ElButton @click="navigate(`/applications/${route.appId}`)">返回应用详情</ElButton></section>
        </template>
        <div v-else class="portal-catalog-layout" :class="{ 'has-detail': route.kind === 'detail' }">
          <section class="portal-catalog" aria-label="授权应用列表" :aria-busy="busy === 'load' || busy === 'switch'">
            <div class="portal-catalog-toolbar"><h2>我的应用 <span>{{ items.length }}</span></h2><ElInput v-model="search" aria-label="搜索应用" placeholder="搜索应用" clearable :disabled="busy === 'switch'" class="portal-search"><template #prefix><svg width="16" height="16" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.5" aria-hidden="true"><circle cx="8.5" cy="8.5" r="5.5" /><path d="m12.5 12.5 4 4" /></svg></template></ElInput></div>
            <div class="portal-categories" aria-label="应用分类"><button :aria-pressed="!category" @click="category = ''">全部应用</button><button v-for="item in categories" :key="item" :aria-pressed="category === item" @click="category = item">{{ item }}</button></div>
            <div v-if="busy === 'load' && !items.length" class="portal-skeleton" role="status"><span>正在加载应用…</span><div v-for="row in 3" :key="row" class="portal-skeleton-row"><i></i><div><b></b><em></em></div></div></div>
            <div v-else-if="!items.length" class="portal-state"><h3>暂无可用应用</h3><p>当前租户尚未为你开放应用。应用发布并获得授权后，会显示在这里。</p><ElButton :disabled="!!busy" @click="refresh()">刷新应用列表</ElButton></div>
            <div v-else-if="!shown.length" class="portal-state"><h3>没有匹配的应用</h3><p>试试其他关键词，或清除分类和搜索条件。</p><ElButton @click="search = ''; category = ''">清除筛选</ElButton></div>
            <ul v-else class="portal-app-list"><li v-for="app in shown" :key="app.appId" class="portal-app-row" :class="{ selected: detail?.appId === app.appId }"><div class="portal-app-icon" aria-hidden="true">{{ app.name.slice(0, 1) }}</div><div class="portal-app-info"><a :href="`/applications/${app.appId}`" @click="follow($event, `/applications/${app.appId}`)"><h3>{{ app.name }}</h3></a><p>{{ app.description }}</p><div class="portal-app-meta"><span>{{ app.category }}</span><span>v{{ app.releaseVersion }}</span></div></div><a class="portal-detail-link" :href="`/applications/${app.appId}`" @click="follow($event, `/applications/${app.appId}`)">查看详情 <span aria-hidden="true">↗</span></a></li></ul>
            <div v-if="items.length" class="portal-list-footer"><span aria-live="polite">显示 {{ shown.length }} 个应用</span><span>仅展示当前租户已授权的应用</span></div>
          </section>
          <aside v-if="route.kind === 'detail'" class="portal-inspector" aria-label="应用详情" :aria-busy="busy === 'load'"><div class="portal-inspector-top"><span>应用详情</span><a href="/" @click="follow($event, '/')" aria-label="应用列表">关闭 <span aria-hidden="true">×</span></a></div><template v-if="detail"><span class="portal-detail-category">{{ detail.category }}</span><h2>{{ detail.name }}</h2><p class="portal-detail-description">{{ detail.description }}</p><dl><div><dt>当前版本</dt><dd>{{ detail.releaseVersion }}</dd></div><div><dt>所属租户</dt><dd>{{ tenantName }}</dd></div><div><dt>应用标识</dt><dd class="portal-app-id">{{ detail.appId }}</dd></div></dl><ElButton type="primary" :disabled="!!busy" @click="openApplication">{{ busy === 'start' ? '正在打开…' : '打开应用' }}</ElButton><p class="portal-run-note">打开时将再次确认你的访问权限与应用状态。</p></template><div v-else-if="detailError" class="portal-detail-unavailable" role="status"><h2>应用不可用</h2><p>{{ detailError }}</p><ElButton @click="navigate('/')">应用列表</ElButton></div><p v-else role="status">正在加载详情…</p></aside>
        </div>
      </main>
    </div>
  </div>
</template>
