<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { ReferenceButton, ReferencePagination, ReferenceInput } from './components/reference';
import ReferenceDrawer from './components/reference/ReferenceDrawer.vue';
import ReferenceConfirm from './components/reference/ReferenceConfirm.vue';
import { useReferenceConfirm } from './components/reference/useReferenceConfirm';
import { resourceApi, ResourceApiError, type ResourceSection, type ModelResource, type SourceResource, type ResourceConfiguration, type ResourceCall, type ModelTestResult } from './resource-api';
import './resource-workspace.css';
const { confirmation: decision, requestConfirmation, settleConfirmation } = useReferenceConfirm();
const props = defineProps<{ section: ResourceSection; resourceId: string; tenantId: string; refreshToken: number }>();
const emit = defineEmits<{ navigate: [path: string]; busy: [value: boolean]; dirty: [value: boolean]; unauthorized: [] }>();
const models = ref<ModelResource[]>([]); const sources = ref<SourceResource[]>([]); const configuration = ref<ResourceConfiguration | null>(null); const calls = ref<ResourceCall[]>([]);
const statusFilter = ref('all'); const page = ref(1); const pageSize = ref(10); const sortAscending = ref(true);
const search = ref(''); const loading = ref(false); const writing = ref(false); const testing = ref(false); const loaded = ref(false); const denied = ref(false); const error = ref(''); const notice = ref('');
const dirty = ref(false); const stale = ref(false); const editor = ref<{ enabled: boolean; limit: number; timeout: number; revision: number } | null>(null);
const prompt = ref(''); const testLimit = ref(128); const testResult = ref<ModelTestResult | null>(null); const testRequestId = ref(''); const testNotice = ref(''); const testUncertain = ref(false);
let generation = 0; let controller: AbortController | undefined; let disposed = false;
const isModel = computed(() => props.section === 'models'); const label = computed(() => isModel.value ? '模型' : '数据源');
const model = computed(() => models.value.find(x => x.modelId === props.resourceId)); const source = computed(() => sources.value.find(x => x.sourceId === props.resourceId));
const selected = computed(() => isModel.value ? model.value : source.value);
const rows = computed(() => isModel.value ? models.value.map(x => ({ id: x.modelId, ...x })) : sources.value.map(x => ({ id: x.sourceId, ...x })));
const shown = computed(() => rows.value.filter(x => `${x.displayName} ${x.id}`.toLocaleLowerCase().includes(search.value.trim().toLocaleLowerCase()) && (statusFilter.value === 'all' || x.enabled === (statusFilter.value === 'enabled'))).sort((a, b) => (sortAscending.value ? 1 : -1) * a.displayName.localeCompare(b.displayName, 'zh-CN')));
const pagedRows = computed(() => shown.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value));
watch([search, statusFilter, pageSize], () => { page.value = 1; });
watch(() => shown.value.length, count => { page.value = Math.min(page.value, Math.max(1, Math.ceil(count / pageSize.value))); });
const canTest = computed(() => isModel.value ? model.value?.canInvoke === true : source.value?.canTest === true);
const testDisabled = computed(() => writing.value || loading.value || !!editor.value || !selected.value?.enabled || testUncertain.value);
const maximum = computed(() => isModel.value ? model.value?.deploymentMaximumOutputTokens ?? 1 : source.value?.deploymentMaximumRows ?? 1);
const pathFor = (id: string) => `/manage/${props.section}/${encodeURIComponent(id)}`;
function setDirty(value: boolean) { dirty.value = value; emit('dirty', value); }
function clearEditor() { editor.value = null; stale.value = false; setDirty(false); }
function clearTest() { testResult.value = null; testRequestId.value = ''; testNotice.value = ''; }
function clearData() { models.value = []; sources.value = []; configuration.value = null; calls.value = []; loaded.value = false; clearTest(); }
function begin(write = false) { controller?.abort(); controller = new AbortController(); const id = ++generation; loading.value = !write; writing.value = write; emit('busy', write); return { id, signal: controller.signal }; }
const current = (id: number) => !disposed && id === generation;
async function ask(description: string, title = '放弃修改？', confirmLabel = '放弃修改') {
  const context = generation; const draft = editor.value;
  const accepted = await requestConfirmation({ title, description, confirmLabel });
  return accepted && current(context) && draft === editor.value;
}
function errorText(failure: unknown) {
  const status = failure instanceof ResourceApiError ? failure.status : 503;
  if (status === 403) return '当前租户或资源权限已变化，请重新读取状态。';
  if (status === 404) return '此资源不存在或已从部署清单移除。';
  if (status === 409) return '配置已被更新，此输入保留，不会自动覆盖；请重新读取后再编辑。';
  if (status === 429) return '调用过于频繁，请稍后重新读取后再试。';
  if ([400, 413, 415].includes(status)) return '请求未通过校验，请检查输入和允许的上限。';
  return '请求结果暂时无法确认，请检查连接并重新读取状态；操作不会自动重试。';
}
function checkFailure(failure: unknown) {
  if (failure instanceof ResourceApiError && [401, 403].includes(failure.status)) { clearData(); clearEditor(); denied.value = true; if (failure.status === 401) emit('unauthorized'); }
}
async function read(id: number, signal: AbortSignal) {
  if (!props.tenantId) { clearData(); return; }
  const section = props.section; const tenant = props.tenantId; const resource = props.resourceId;
  const data = section === 'models' ? await resourceApi.models(tenant, signal) : await resourceApi.sources(tenant, signal);
  if (!current(id)) return;
  const item = data.items.find(x => ('modelId' in x ? x.modelId : x.sourceId) === resource);
  let config: ResourceConfiguration | null = null; let history: ResourceCall[] = [];
  if (item?.canManage) {
    const responses = await Promise.all([resourceApi.configuration(section, tenant, resource, signal), resourceApi.calls(section, tenant, resource, signal)]);
    config = responses[0]; history = responses[1].items;
    if (config && (config.tenantId !== tenant || (section === 'models' ? config.modelId : config.sourceId) !== resource || !Number.isSafeInteger(config.revision) || config.revision < 1 || typeof config.enabled !== 'boolean')) throw new ResourceApiError(502);
    if (history.some(x => x.tenantId !== tenant || (section === 'models' ? x.modelId : x.sourceId) !== resource)) throw new ResourceApiError(502);
  }
  if (!current(id)) return;
  if (section === 'models') models.value = data.items as ModelResource[]; else sources.value = data.items as SourceResource[];
  configuration.value = config; calls.value = history; loaded.value = true; denied.value = false;
  if (!item?.canManage) clearEditor();
  else if (editor.value && editor.value.revision !== (config?.revision ?? 0)) stale.value = true;
  if (!canTest.value || !item?.enabled) clearTest();
  if (model.value && testLimit.value > model.value.maximumOutputTokens) testLimit.value = model.value.maximumOutputTokens;
}
async function refresh() {
  if (writing.value) return;
  const work = begin(); error.value = '';
  try { await read(work.id, work.signal); }
  catch (failure) { if (!current(work.id) || work.signal.aborted) return; clearData(); stale.value = !!editor.value; error.value = errorText(failure); checkFailure(failure); }
  finally { if (current(work.id)) loading.value = false; }
}
async function edit() {
  if (writing.value || loading.value || !selected.value?.canManage) return;
  const value = configuration.value;
  editor.value = { enabled: value?.enabled ?? false, limit: value ? (isModel.value ? value.maxOutputTokens! : value.maxRows!) : maximum.value,
    timeout: value?.timeoutSeconds ?? source.value?.deploymentTimeoutSeconds ?? 1, revision: value?.revision ?? 0 };
  stale.value = false; setDirty(false); error.value = ''; notice.value = ''; clearTest();
}
function inputChanged(event: Event) { if ((event.target as HTMLInputElement).type !== 'checkbox') setDirty(true); }
async function save() {
  const value = editor.value;
  if (!value || writing.value || loading.value || stale.value || !selected.value?.canManage) return;
  if (!Number.isSafeInteger(value.limit) || value.limit < 1 || value.limit > maximum.value || (!isModel.value && (!Number.isSafeInteger(value.timeout) || value.timeout < 1 || value.timeout > source.value!.deploymentTimeoutSeconds))) { error.value = '请填写允许范围内的整数。'; return; }
  if (configuration.value?.enabled && !value.enabled && !await ask('停用后会阻止当前租户使用此资源。确认保存？', '确认停用？', '确认保存')) return;
  if (value !== editor.value || writing.value || loading.value || stale.value || !selected.value?.canManage) return;
  const body = { enabled: value.enabled, expectedRevision: value.revision, ...(isModel.value ? { maxOutputTokens: value.limit } : { maxRows: value.limit, timeoutSeconds: value.timeout }) };
  const work = begin(true); error.value = ''; notice.value = ''; let committed = false;
  try {
    await resourceApi.save(props.section, props.tenantId, props.resourceId, body, work.signal);
    if (!current(work.id)) return;
    committed = true; clearEditor(); await read(work.id, work.signal);
    if (current(work.id)) notice.value = '配置已保存，已重新读取最新状态。';
  } catch (failure) {
    if (!current(work.id) || work.signal.aborted) return;
    if (committed) clearData();
    if (!(failure instanceof ResourceApiError) || failure.status === 409 || failure.status >= 500) stale.value = true;
    checkFailure(failure); error.value = committed ? '配置已提交，但最新状态读取失败。请重新读取，勿重复提交。' : errorText(failure);
  } finally { if (current(work.id)) { writing.value = false; loading.value = false; emit('busy', false); } }
}
async function testResource() {
  if (testDisabled.value || !canTest.value) return;
  if (isModel.value && (!prompt.value.trim() || prompt.value.length > 4000 || !Number.isSafeInteger(testLimit.value) || testLimit.value < 1 || testLimit.value > model.value!.maximumOutputTokens)) { error.value = '请填写测试内容和允许范围内的输出上限。'; return; }
  const work = begin(true); testing.value = true; clearTest(); error.value = ''; notice.value = ''; let completed = false;
  try {
    if (isModel.value) {
      const result = await resourceApi.testModel(props.tenantId, props.resourceId, prompt.value, testLimit.value, model.value!.callTimeoutSeconds, work.signal);
      if (!current(work.id)) return;
      if (!result || typeof result.content !== 'string' || typeof result.requestId !== 'string') throw new ResourceApiError(502);
      testResult.value = result; testRequestId.value = result.requestId;
    } else {
      const result = await resourceApi.testSource(props.tenantId, props.resourceId, work.signal);
      if (!current(work.id)) return;
      if (result?.status !== 'ready' || typeof result.requestId !== 'string') throw new ResourceApiError(502);
      testRequestId.value = result.requestId;
    }
    completed = true; testNotice.value = isModel.value ? '单次调用已完成。' : '连接测试通过。';
    await read(work.id, work.signal);
  } catch (failure) {
    if (!current(work.id) || work.signal.aborted) return;
    testUncertain.value = true; checkFailure(failure);
    error.value = completed ? '测试已完成，但最新调用状态读取失败。请重新读取，勿重复发送。' : errorText(failure);
  } finally { if (current(work.id)) { testing.value = false; writing.value = false; loading.value = false; emit('busy', false); } }
}
function cancelTest() {
  if (!testing.value) return;
  generation++; controller?.abort(); testing.value = false; writing.value = false; loading.value = false; emit('busy', false);
  clearTest(); testUncertain.value = true;
  error.value = '已停止等待测试结果。服务端可能已执行，请重新读取调用记录后再决定是否重发。';
}
async function cancel() { if (!dirty.value || await ask('有尚未保存的修改。放弃修改并继续？')) clearEditor(); }
async function reread() { if (!dirty.value || await ask('重新读取会放弃当前未保存修改。继续？', '重新读取状态？', '放弃并重新读取')) { clearEditor(); testUncertain.value = false; clearTest(); void refresh(); } }
function follow(event: MouseEvent, path: string) { if (event.button || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return; event.preventDefault(); if (!writing.value) emit('navigate', path); }
const outcomes: Record<string, string> = { completed: '已完成', success: '成功', started: '尚未完成', admitted: '尚未完成', running: '尚未完成', failed: '失败', upstream_error: '上游异常', timeout: '超时', revoked: '权限已撤回', cancelled: '已取消' };
const date = (value: string) => new Date(value).toLocaleString('zh-CN', { hour12: false });
watch(() => [props.section, props.resourceId, props.tenantId], () => { clearEditor(); clearData(); search.value = ''; statusFilter.value = 'all'; page.value = 1; prompt.value = ''; testLimit.value = 128; testUncertain.value = false; notice.value = ''; void refresh(); }, { immediate: true });
watch(() => props.refreshToken, () => { void refresh(); });
onBeforeUnmount(() => { disposed = true; generation++; controller?.abort(); emit('dirty', false); emit('busy', false); });
</script>

<template>
  <section class="resource-workspace" :aria-label="`${label}资源管理`" :aria-busy="loading || writing">
    <div v-if="error && (!resourceId || denied || !tenantId)" class="resource-error" role="alert"><span>{{ error }}</span><ReferenceButton :disabled="loading || writing" @click="reread">重新读取状态</ReferenceButton></div>
    <p v-if="notice && (!resourceId || denied || !tenantId)" class="resource-notice" role="status">{{ notice }}</p>
    <section v-if="!tenantId" class="empty-state is-visible resource-empty"><div class="empty-icon" aria-hidden="true">∅</div><h2 class="empty-title">选择工作租户</h2><p class="empty-desc">从工作空间选择租户，查看当前账号有权使用或管理的资源。</p></section>
    <section v-else-if="denied" class="empty-state is-visible resource-empty"><div class="empty-icon" aria-hidden="true">∅</div><h2 class="empty-title">没有资源访问权限</h2><p class="empty-desc">需要当前租户内对应资源的授权。身份管理员资格不会自动授予资源权限。</p></section>
    <div v-else class="resource-layout">
      <section class="data-grid resource-index" :aria-label="`${label}列表`">
        <div class="data-grid__toolbar"><label class="data-grid__search"><svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden="true"><circle cx="6" cy="6" r="4.2" stroke="currentColor" stroke-width="1.2"/><path d="M9.2 9.2L12 12" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/></svg><input id="resource-search" v-model="search" type="search" :aria-label="`搜索${label}`" :placeholder="`搜索${label}名称或标识`" /></label><div class="filter-row data-grid__filters"><button v-for="filter in [{ value: 'all', label: '全部' }, { value: 'enabled', label: '启用' }, { value: 'disabled', label: '停用' }]" :key="filter.value" class="filter-chip" :class="{ 'is-active': statusFilter === filter.value }" type="button" :aria-pressed="statusFilter === filter.value" @click="statusFilter = filter.value">{{ filter.label }}</button></div></div>
        <div class="admin-table-wrap data-grid__body"><div class="scroll-content data-grid__scroll"><p v-if="loading && !loaded" class="resource-empty" role="status">正在读取资源…</p><p v-else-if="!loaded" class="resource-empty">资源暂未载入，请重新读取状态。</p><div v-else-if="!shown.length" class="empty-state data-grid__empty is-visible"><div class="empty-icon" aria-hidden="true">∅</div><div class="empty-title">{{ search ? '没有匹配的资源' : '当前租户暂无已授权资源' }}</div><div class="empty-desc">请调整搜索条件，或联系管理员检查资源部署和权限分配。</div></div>
          <table v-else class="data-grid__table" :aria-label="`${label}记录`"><thead><tr><th scope="col"><button class="data-grid__sort" :class="sortAscending ? 'is-asc' : 'is-desc'" type="button" @click="sortAscending = !sortAscending"><span>{{ label }}</span><span class="data-grid__sort-icons" aria-hidden="true"><i class="up"></i><i class="down"></i></span></button></th><th scope="col">标识</th><th scope="col">状态</th><th scope="col">权限</th><th scope="col" class="data-grid__actions">操作</th></tr></thead><tbody><tr v-for="row in pagedRows" :key="row.id" :class="{ 'is-selected': resourceId === row.id }"><td><div class="data-grid__person"><span class="avatar sm">{{ row.displayName.slice(0, 1) }}</span><a :href="pathFor(row.id)" :aria-current="resourceId === row.id ? 'page' : undefined" @click="follow($event, pathFor(row.id))">{{ row.displayName }}</a></div></td><td class="muted"><code>{{ row.id }}</code></td><td><span class="status-pill" :class="{ draft: !row.enabled }"><i class="sdot"></i>{{ row.enabled ? '启用' : '停用' }}</span></td><td class="muted">{{ row.canManage ? '可管理' : '可使用' }}</td><td class="data-grid__actions"><div class="data-grid__action-content"><a class="link-btn" :href="pathFor(row.id)" @click="follow($event, pathFor(row.id))">查看</a></div></td></tr></tbody></table>
        </div></div><ReferencePagination :current-page="page" v-model:page-size="pageSize" :total="shown.length" :disabled="writing" @change="page = $event" />
      </section>
      <ReferenceDrawer v-if="resourceId" open :title="editor ? (editor.revision ? '编辑租户配置' : '建立租户配置') : selected?.displayName ?? '资源详情'" :kicker="`${label} · 租户资源`" :disabled="writing" @close="editor ? cancel() : emit('navigate', `/manage/${section}`)"><div class="resource-detail"><div v-if="error" class="resource-error" role="alert"><span>{{ error }}</span><ReferenceButton :disabled="loading || writing" @click="reread">重新读取状态</ReferenceButton></div><p v-if="notice" class="resource-notice" role="status">{{ notice }}</p><p v-if="stale && editor && !error" class="resource-error" role="alert">配置已变化，此输入保留。请重新读取后再编辑。</p>
        <template v-if="selected">
          <header class="resource-heading"><div><p><code>{{ resourceId }}</code> · {{ selected.enabled ? '当前租户可用' : '当前租户未启用' }}</p></div><ReferenceButton v-if="selected.canManage && !editor" :disabled="loading || writing" @click="edit">编辑配置</ReferenceButton></header>
          <p v-if="!selected.deploymentEnabled" class="resource-notice">此资源已在部署侧停用，租户配置无法恢复使用。请联系部署管理员。</p>
          <form v-if="editor" id="resource-configuration-form" class="resource-form" aria-label="资源配置" @submit.prevent="save" @input="inputChanged" @change="setDirty(true)">

            <fieldset :disabled="writing || loading || stale || !selected.canManage"><legend class="sr-only">租户配置</legend>
              <label class="subwindow-check resource-check"><input v-model="editor.enabled" type="checkbox" />启用当前租户</label>
              <div class="resource-fields"><label class="subwindow-field">{{ isModel ? '最大输出 Token' : '最大返回行数' }}<ReferenceInput v-model="editor.limit" :aria-label="isModel ? '最大输出 Token' : '最大返回行数'" aria-describedby="resource-limit-help" type="number" min="1" :max="maximum" step="1" required /><small id="resource-limit-help">部署允许范围：1–{{ maximum }}</small></label>
                <label v-if="source" class="subwindow-field">查询超时（秒）<ReferenceInput v-model="editor.timeout" aria-label="查询超时（秒）" aria-describedby="resource-timeout-help" type="number" min="1" :max="source.deploymentTimeoutSeconds" step="1" required /><small id="resource-timeout-help">部署允许范围：1–{{ source.deploymentTimeoutSeconds }} 秒</small></label></div>
            </fieldset>
            <p class="subwindow-hint">{{ dirty ? '有未保存修改' : '按当前修订保存' }} · 修订 {{ editor.revision }}</p>
          </form>
          <dl v-else class="resource-facts"><div><dt>当前生效上限</dt><dd>{{ model ? `${model.maximumOutputTokens} Token / 次` : `${source?.maximumRows} 行 / 次` }}</dd></div><div v-if="source"><dt>当前查询超时</dt><dd>{{ source.timeoutSeconds }} 秒</dd></div><div><dt>部署允许上限</dt><dd>{{ model ? `${model.deploymentMaximumOutputTokens} Token / 次` : `${source?.deploymentMaximumRows} 行 · ${source?.deploymentTimeoutSeconds} 秒` }}</dd></div><div v-if="selected.canManage"><dt>租户配置</dt><dd>{{ configuration ? `修订 ${configuration.revision}` : '尚未配置' }}</dd></div><div><dt>操作权限</dt><dd>{{ selected.canManage ? '管理配置与记录' : '查看已授权资源' }} · {{ canTest ? '允许测试' : '无测试权限' }}</dd></div></dl>
          <p class="resource-help">{{ model ? '模型、服务地址和凭据由部署管理员维护。租户配置在部署允许范围内生效。' : '连接、物理表和字段映射由部署管理员维护。这里只展示已授权的数据集与逻辑字段。' }}</p>
          <section v-if="source" class="resource-section" aria-label="受控数据集"><h3>数据集 <span>{{ source.datasets.length }}</span></h3><p v-if="!source.datasets.length" class="resource-help">暂无可查看的数据集。</p><article v-for="dataset in source.datasets" :key="dataset.datasetId" class="resource-dataset"><h4>{{ dataset.displayName }}</h4><code>{{ dataset.datasetId }}</code><div class="data-grid resource-table-wrap" tabindex="0" :aria-label="`${dataset.displayName}字段`"><div class="admin-table-wrap data-grid__body"><div class="scroll-content data-grid__scroll"><table class="data-grid__table"><thead><tr><th scope="col">字段</th><th scope="col">类型</th></tr></thead><tbody><tr v-for="column in dataset.columns" :key="column.name"><td><code>{{ column.name }}</code></td><td>{{ column.type }}</td></tr></tbody></table></div></div></div></article></section>
          <section class="resource-section" :aria-label="model ? '模型调用验证' : '连接验证'"><h3>{{ model ? '调用验证' : '连接验证' }}</h3>
            <template v-if="canTest"><p class="resource-help">{{ model ? '发送后将实际调用模型，并使用当前租户的模型额度。测试内容与回答仅在本页显示。' : '测试当前连接与受控数据集的只读访问能力，不返回业务记录。' }}</p>
              <form v-if="model" class="resource-form" @submit.prevent="testResource"><label class="subwindow-field">测试内容<div class="composer-wrap"><div class="composer"><textarea v-model="prompt" rows="3" maxlength="4000" required :disabled="testDisabled" placeholder="输入一条简短的验证消息" /></div></div></label><label class="subwindow-field resource-test-limit">测试输出上限<ReferenceInput v-model="testLimit" type="number" min="1" :max="model.maximumOutputTokens" step="1" required :disabled="testDisabled" /></label><ReferenceButton type="submit" :disabled="testDisabled">{{ writing && !editor ? '正在调用…' : '发送测试' }}</ReferenceButton></form>
              <ReferenceButton v-else :disabled="testDisabled" @click="testResource">{{ writing && !editor ? '正在测试…' : '测试连接' }}</ReferenceButton>
              <p v-if="!selected.enabled" class="resource-help">启用部署资源及当前租户配置后，才能测试。</p><p v-else-if="editor" class="resource-help">请先保存或取消配置编辑，再进行测试。</p>
            </template><p v-else class="resource-help">当前账号没有{{ model ? '模型调用' : '连接测试' }}权限。配置管理权限不会自动授予测试权限。</p>
            <ReferenceButton v-if="testing" @click="cancelTest">取消测试</ReferenceButton>
            <p v-if="testNotice" role="status" class="resource-notice">{{ testNotice }}</p><pre v-if="testResult" class="resource-output">{{ testResult.content }}</pre><p v-if="testRequestId" class="resource-help">请求标识：<code>{{ testRequestId }}</code><span v-if="testResult?.usage"> · 总 Token {{ testResult.usage.totalTokens }}</span></p>
          </section>
          <section v-if="selected.canManage" class="resource-section" aria-label="调用记录"><h3>最近调用</h3><p class="resource-help">仅显示此租户、此资源最近 50 条调用元数据，不包含输入、回答或业务数据。已完成表示服务端执行完成，不保证响应已送达；尚未完成也可能是中断后未能写入最终状态。{{ calls.length === 50 ? '已达到显示上限，可能还有更早记录。' : '' }}</p><p v-if="!calls.length" class="resource-empty">暂无调用记录。</p><div v-else class="data-grid resource-table-wrap" tabindex="0" aria-label="调用记录可横向滚动"><div class="admin-table-wrap data-grid__body"><div class="scroll-content data-grid__scroll"><table class="data-grid__table" aria-label="最近调用记录"><thead><tr><th scope="col">时间与请求</th><th scope="col">结果</th><th scope="col">耗时 / 用量</th><th scope="col">调用来源</th></tr></thead><tbody><tr v-for="call in calls" :key="call.callId"><td><time>{{ date(call.startedAt) }}</time><code>{{ call.requestId }}</code></td><td>{{ outcomes[call.outcome] ?? '未知状态' }}<small v-if="call.operation">{{ call.operation === 'test' ? '连接测试' : '数据查询' }}</small></td><td>{{ call.elapsedMilliseconds }} ms<small v-if="call.usage">{{ call.usage.totalTokens }} Token</small><small v-if="call.rowCount != null">{{ call.rowCount }} 行</small></td><td>{{ call.source?.appId ?? '平台直接调用' }}<small v-if="call.source">v{{ call.source.releaseVersion }}</small><code>{{ call.userId }}</code></td></tr></tbody></table></div></div></div></section>
        </template>
        <section v-else-if="loaded && resourceId" class="empty-state is-visible resource-empty"><div class="empty-icon" aria-hidden="true">∅</div><h2 class="empty-title">资源不可用</h2><p class="empty-desc">此资源未部署或不在当前租户的授权范围内，请从列表重新选择。</p></section>
        <section v-else-if="!editor" class="empty-state is-visible resource-empty"><div class="empty-icon" aria-hidden="true">∅</div><h2 class="empty-title">选择{{ label }}查看详情</h2><p class="empty-desc">查看当前状态、租户配置{{ isModel ? '和调用记录。' : '、受控数据集和调用记录。' }}</p></section>
        <section v-if="editor && !selected" class="empty-state is-visible resource-empty"><div class="empty-icon" aria-hidden="true">∅</div><h2 class="empty-title">配置暂不可确认</h2><p class="empty-desc">当前输入已保留，重新读取状态后再编辑。</p><pre class="resource-output">启用：{{ editor.enabled ? '是' : '否' }}
上限：{{ editor.limit }}{{ isModel ? ' Token' : ' 行' }}
{{ isModel ? '' : `超时：${editor.timeout} 秒` }}</pre></section>
      </div><template v-if="editor && selected" #footer><ReferenceButton :disabled="writing" @click="cancel">取消编辑</ReferenceButton><ReferenceButton primary type="submit" form="resource-configuration-form" :disabled="writing || loading || stale || !selected?.canManage">{{ writing ? '正在保存…' : '保存配置' }}</ReferenceButton></template></ReferenceDrawer>
    </div>
    <ReferenceConfirm :request="decision" @resolve="settleConfirmation" />
  </section>
</template>
