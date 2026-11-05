<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import ElButton from 'element-plus/es/components/button/index';
import { auditApi, AuditApiError, auditQuery, auditSources, type AuditScope, type AuditSource, type AuditStatus, type AuditFilters, type AuditEvent, type AuditWorkspace } from './audit-api';
import './audit.css';
const props = defineProps<{ scope: AuditScope; tenantId: string; recordSource: AuditSource | ''; recordId: string; queryString: string; refreshToken: number }>();
const emit = defineEmits<{ navigate: [path: string]; busy: [value: boolean]; unauthorized: [] }>();
const names: Record<AuditSource, string> = { gateway: '应用网关调用', model: '模型调用', connector: '数据连接调用', 'knowledge-search': '知识检索', registry: '应用操作', identity: '身份操作', 'model-configuration': '模型配置', 'connector-configuration': '数据源配置', 'knowledge-document': '知识文档操作', 'audit-export': '审计导出' };
const statusNames: Record<AuditStatus, string> = { success: '成功', failure: '异常', unfinished: '未完成' };
const outcomeNames: Record<string, string> = { success: '成功', completed: '执行完成', transport_completed: '传输完成', running: '尚未完成', failed: '失败', denied: '拒绝', forbidden: '拒绝', revoked: '授权已撤回', cancelled: '已取消', timeout: '超时', upstream_error: '上游异常', transport_error: '传输异常', conflict: '冲突' };
const detailNames: Record<string, string> = { elapsedMilliseconds: '耗时（毫秒）', httpStatus: 'HTTP 状态', promptTokens: '输入 Token', completionTokens: '输出 Token', totalTokens: '总 Token', rowCount: '返回行数', hitCount: '检索命中数', revision: '修订', enabled: '启用', maxOutputTokens: '最大输出 Token', maxRows: '最大返回行数', timeoutSeconds: '超时（秒）', operation: '操作', datasetId: '数据集标识', documentId: '文档标识', profile: '检索配置', recordCount: '导出条数', truncated: '已截断', method: '方法' };
const workspace = ref<AuditWorkspace | null>(null); const items = ref<AuditEvent[]>([]); const detail = ref<AuditEvent | null>(null);
const loading = ref(false); const exporting = ref(false); const error = ref(''); const detailError = ref(''); const notice = ref(''); const denied = ref(false);
const cursors = ref<(string | null)[]>([null]); const pageIndex = ref(0); const nextCursor = ref<string | null>(null);
const fromInput = ref(''); const toInput = ref(''); const sourceInput = ref(''); const statusInput = ref(''); const appInput = ref(''); const requestInput = ref('');
const applied = ref<AuditFilters | null>(null);
const base = computed(() => props.scope === 'global' ? '/manage/audit/global' : '/manage/audit');
const canQuery = computed(() => props.scope === 'global' || !!props.tenantId);
let generation = 0; let controller: AbortController | undefined; let disposed = false; let filterContext = '';
const current = (id: number) => !disposed && generation === id;
function local(value: string) { const date = new Date(value); return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 19); }
function initial() { const to = new Date(Date.now() + 1000); return { scope: props.scope, from: new Date(to.getTime() - 86_400_000).toISOString(), to: to.toISOString(), pageSize: 50 } satisfies AuditFilters; }
function install(filters: AuditFilters) {
  applied.value = filters; fromInput.value = local(filters.from); toInput.value = local(filters.to); sourceInput.value = filters.source ?? ''; statusInput.value = filters.status ?? ''; appInput.value = filters.appId ?? ''; requestInput.value = filters.requestId ?? '';
}
function parseUrl(): AuditFilters {
  const query = new URLSearchParams(props.queryString); const filters = initial();
  for (const key of ['from', 'to'] as const) { const value = query.get(key); if (value) { if (!Number.isFinite(Date.parse(value))) throw new AuditApiError(400); filters[key] = value; } }
  const source = query.get('source') ?? ''; const status = query.get('status') ?? '';
  if (source && !auditSources.includes(source as AuditSource) || status && !['success', 'failure', 'unfinished'].includes(status)) throw new AuditApiError(400);
  return { ...filters, ...(source ? { source: source as AuditSource } : {}), ...(status ? { status: status as AuditStatus } : {}), ...(query.get('appId') ? { appId: query.get('appId')! } : {}), ...(query.get('requestId') ? { requestId: query.get('requestId')! } : {}) };
}
function resetPages() { cursors.value = [null]; pageIndex.value = 0; nextCursor.value = null; }
function begin(write = false) { controller?.abort(); controller = new AbortController(); generation++; loading.value = !write; exporting.value = write; emit('busy', write); return { id: generation, signal: controller.signal }; }
function clear() { items.value = []; detail.value = null; nextCursor.value = null; }
function message(failure: unknown) {
  const status = failure instanceof AuditApiError ? failure.status : 503;
  if (status === 403) return '当前范围没有查询或导出权限，请检查租户和资源授权。';
  if (status === 404) return '记录不存在，或已不在当前授权与筛选范围内。';
  if (status === 409 || status === 410) return '分页状态或权限已变化，请从第一页重新查询。';
  if (status === 400) return '筛选或分页条件无效。请选择不超过 31 天的时间范围，或重新查询第一页。';
  if (status === 429) return '请求较多，请稍后重新查询。';
  return '暂时无法读取审计记录，请稍后重试。';
}
function failure(errorValue: unknown) { clear(); error.value = message(errorValue); denied.value = errorValue instanceof AuditApiError && errorValue.status === 403; if (errorValue instanceof AuditApiError && errorValue.status === 401) { workspace.value = null; emit('unauthorized'); } }
function checkScope(records: AuditEvent[]) { if (records.some(row => row.tenantId !== (props.scope === 'global' ? null : props.tenantId))) throw new AuditApiError(502); }
async function load() {
  if (exporting.value) return;
  const work = begin(); clear(); error.value = ''; detailError.value = ''; denied.value = false;
  if (!canQuery.value) { workspace.value = null; loading.value = false; return; }
  try {
    if (!applied.value) install(parseUrl());
    const context = await auditApi.workspace(props.scope, work.signal);
    if (!current(work.id)) return;
    if (context.tenantId !== (props.scope === 'global' ? null : props.tenantId)) throw new AuditApiError(502);
    workspace.value = context;
    const page = await auditApi.events(applied.value!, cursors.value[pageIndex.value] ?? null, work.signal);
    if (!current(work.id)) return; checkScope(page.items); items.value = page.items; nextCursor.value = page.nextCursor;
    if (props.recordSource && props.recordId) {
      try {
        const value = await auditApi.detail(props.recordSource, props.recordId, applied.value!, work.signal);
        if (!current(work.id)) return; checkScope([value]); detail.value = value;
      } catch (errorValue) { if (!current(work.id) || work.signal.aborted) return; if (errorValue instanceof AuditApiError && errorValue.status === 404) detailError.value = message(errorValue); else throw errorValue; }
    }
  } catch (errorValue) { if (current(work.id) && !work.signal.aborted) failure(errorValue); }
  finally { if (current(work.id)) { loading.value = false; exporting.value = false; emit('busy', false); } }
}
function formFilters(): AuditFilters | null {
  const from = new Date(fromInput.value); const to = new Date(toInput.value);
  if (!Number.isFinite(from.getTime()) || !Number.isFinite(to.getTime()) || to <= from || to.getTime() - from.getTime() > 31 * 86_400_000) { error.value = '请选择开始早于结束、且不超过 31 天的时间范围。'; return null; }
  if (appInput.value.trim() && !/^[a-z][a-z0-9-]{0,63}$/.test(appInput.value.trim()) || requestInput.value.trim().length > 128) { error.value = '请检查应用标识或请求标识。'; return null; }
  return { scope: props.scope, from: from.toISOString(), to: to.toISOString(), pageSize: 50, ...(sourceInput.value ? { source: sourceInput.value as AuditSource } : {}), ...(statusInput.value ? { status: statusInput.value as AuditStatus } : {}), ...(appInput.value.trim() ? { appId: appInput.value.trim() } : {}), ...(requestInput.value.trim() ? { requestId: requestInput.value.trim() } : {}) };
}
function query() { if (exporting.value) return; const value = formFilters(); if (!value) return; resetPages(); install(value); notice.value = ''; emit('navigate', `${base.value}?${auditQuery(value)}`); }
function reset() { resetPages(); install(initial()); notice.value = ''; emit('navigate', `${base.value}?${auditQuery(applied.value!)}`); }
function retry() { resetPages(); notice.value = ''; void load(); }
function pageTo(direction: number) { if (loading.value || exporting.value) return; if (direction > 0) { if (!nextCursor.value) return; cursors.value = [...cursors.value.slice(0, pageIndex.value + 1), nextCursor.value]; pageIndex.value++; } else if (pageIndex.value) pageIndex.value--; void load(); }
function pathFor(row: AuditEvent) { return `${base.value}/${row.source}/${row.id}?${auditQuery(applied.value!)}`; }
function follow(event: MouseEvent, path: string) { if (event.button || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return; event.preventDefault(); if (!exporting.value) emit('navigate', path); }
function correlate(requestId: string) { if (!applied.value) return; const value: AuditFilters = { scope: props.scope, from: applied.value.from, to: applied.value.to, requestId, pageSize: 50 }; resetPages(); install(value); emit('navigate', `${base.value}?${auditQuery(value)}`); }
async function exportRecords() {
  if (!workspace.value?.canExport || !applied.value || loading.value || exporting.value) return;
  const work = begin(true); error.value = ''; notice.value = '';
  try {
    const value = await auditApi.export(applied.value, work.signal);
    if (!current(work.id)) return; checkScope(value.items);
    const url = URL.createObjectURL(new Blob([JSON.stringify(value, null, 2)], { type: 'application/json;charset=utf-8' }));
    const link = document.createElement('a'); link.href = url; link.download = `assembleai-audit-${value.exportedAt.slice(0, 10)}.json`; link.click(); window.setTimeout(() => URL.revokeObjectURL(url), 2000);
    notice.value = value.truncated ? `已导出前 ${value.items.length} 条，结果已截断。请缩小筛选范围后再导出。` : `已导出 ${value.items.length} 条记录，导出操作已记录审计。`;
  } catch (errorValue) {
    if (!current(work.id) || work.signal.aborted) return;
    failure(errorValue);
    if (errorValue instanceof AuditApiError && errorValue.status === 403)
      error.value = '当前范围没有导出权限，或权限已变化。没有下载文件，可能已记录导出审计；没有自动重试。';
    else if (!(errorValue instanceof AuditApiError) || errorValue.status >= 500)
      error.value = '导出未完成，可能已记录导出审计；没有自动重试，请重新查询状态。';
  } finally { if (current(work.id)) { exporting.value = false; loading.value = false; emit('busy', false); } }
}
function cancelExport() { if (!exporting.value) return; generation++; controller?.abort(); exporting.value = false; emit('busy', false); notice.value = '已取消等待导出，没有下载文件。服务端可能已记录审计，请重新查询状态。'; }
const time = (value: string) => new Date(value).toLocaleString('zh-CN', { hour12: false });
watch(() => [props.scope, props.tenantId, props.recordSource, props.recordId, props.queryString, props.refreshToken], () => {
  const key = JSON.stringify([props.scope, props.tenantId, props.queryString]);
  if (key !== filterContext) { filterContext = key; applied.value = null; workspace.value = null; resetPages(); notice.value = ''; }
  void load();
}, { immediate: true });
onBeforeUnmount(() => { disposed = true; generation++; controller?.abort(); emit('busy', false); });
</script>

<template>
  <section class="audit-workspace" aria-label="调用与审计工作区" :aria-busy="loading || exporting">
    <nav class="audit-scopes" aria-label="审计范围"><a href="/manage/audit" :aria-current="scope === 'tenant' ? 'page' : undefined" @click="follow($event, '/manage/audit')">当前租户</a><a v-if="scope === 'global' || workspace?.globalAvailable" href="/manage/audit/global" :aria-current="scope === 'global' ? 'page' : undefined" @click="follow($event, '/manage/audit/global')">平台操作</a><span>{{ scope === 'global' ? '仅全局身份与应用注册记录' : '仅当前租户中已授权的资源记录' }}</span></nav>
    <section v-if="!canQuery" class="audit-empty"><h2>选择工作租户</h2><p>选择租户后查询资源记录；全局管理员可进入平台操作范围。</p></section>
    <template v-else>
      <form class="audit-filters" aria-label="审计筛选" @submit.prevent="query"><fieldset :disabled="loading || exporting"><legend class="sr-only">筛选记录</legend>
        <label>开始时间<input v-model="fromInput" type="datetime-local" step="1" required /></label><label>结束时间<input v-model="toInput" type="datetime-local" step="1" required /></label>
        <label>记录类型<select v-model="sourceInput" aria-label="记录类型"><option value="">全部已授权类型</option><option v-for="source in workspace?.sources ?? []" :key="source" :value="source">{{ names[source] }}</option></select></label>
        <label>结果<select v-model="statusInput" aria-label="结果"><option value="">全部结果</option><option value="success">成功</option><option value="failure">异常</option><option value="unfinished">未完成</option></select></label>
        <label>应用标识<input v-model="appInput" maxlength="64" placeholder="精确应用标识" /></label><label>请求标识<input v-model="requestInput" maxlength="128" placeholder="请求或网关关联标识" /></label>
      </fieldset><div class="audit-filter-actions"><span>时间使用本地时区，单次最多查询 31 天。</span><ElButton :disabled="loading || exporting" @click="reset">清除筛选</ElButton><ElButton type="primary" native-type="submit" :disabled="loading || exporting">查询记录</ElButton></div></form>
      <div v-if="error" class="audit-feedback" role="alert"><span>{{ error }}</span><ElButton :disabled="loading || exporting" @click="retry">重新查询第一页</ElButton></div><p v-if="notice" class="audit-notice" role="status">{{ notice }}</p>
      <div class="audit-layout" :class="{ 'has-detail': recordId }"><div class="audit-grid">
        <div class="audit-toolbar"><strong>记录 <span v-if="!loading">{{ items.length }}</span></strong><span class="audit-secondary">{{ loading ? '正在查询…' : `第 ${pageIndex + 1} 页` }}</span><ElButton v-if="workspace?.canExport" :disabled="loading || exporting" @click="exportRecords">{{ exporting ? '正在导出…' : '导出记录' }}</ElButton><ElButton v-if="exporting" @click="cancelExport">取消导出</ElButton></div>
        <div v-if="loading" class="audit-loading" role="status" aria-label="正在查询审计记录"><div v-for="n in 5" :key="n"><i></i><i></i><i></i></div></div>
        <section v-else-if="!items.length" class="audit-empty"><h2>{{ denied ? '没有查询权限' : error ? '记录暂不可用' : '没有匹配的记录' }}</h2><p>{{ denied ? '需要此范围内的显式管理或审计权限。' : '请检查时间、记录类型和请求标识；这里只返回你获授权的记录。' }}</p></section>
        <div v-else class="audit-table-wrap" tabindex="0" aria-label="审计记录可横向滚动"><table class="audit-table" aria-label="审计记录"><thead><tr><th scope="col">时间</th><th scope="col">类型 / 资源</th><th scope="col">结果</th><th scope="col">请求标识</th><th scope="col"><span class="sr-only">详情</span></th></tr></thead><tbody><tr v-for="row in items" :key="`${row.source}:${row.id}`" :class="{ selected: recordSource === row.source && recordId === row.id }"><td><time>{{ time(row.occurredAt) }}</time></td><td><span>{{ names[row.source] }}</span><code>{{ row.resourceId }}</code></td><td><span class="audit-status" :data-status="row.status"><i aria-hidden="true"></i>{{ statusNames[row.status] }}</span></td><td><code>{{ row.requestId }}</code></td><td><a :href="pathFor(row)" @click="follow($event, pathFor(row))">查看详情<span class="sr-only"> {{ row.requestId }}</span></a></td></tr></tbody></table></div>
        <div class="audit-pagination"><span>按时间排序 · 每页最多 50 条</span><ElButton :disabled="loading || exporting || pageIndex === 0" @click="pageTo(-1)">上一页</ElButton><ElButton :disabled="loading || exporting || !nextCursor" @click="pageTo(1)">下一页</ElButton></div>
      </div>
      <aside v-if="recordId" class="audit-inspector" aria-label="审计记录详情"><header><h2>记录详情</h2><a :href="`${base}?${applied ? auditQuery(applied) : ''}`" @click="follow($event, `${base}?${applied ? auditQuery(applied) : ''}`)">关闭</a></header><p v-if="detailError" role="status">{{ detailError }}</p><p v-else-if="loading">正在读取详情…</p><template v-else-if="detail"><dl><div><dt>记录类型</dt><dd>{{ names[detail.source] }}</dd></div><div><dt>执行结果</dt><dd>{{ outcomeNames[detail.outcome] ?? detail.outcome }}</dd></div><div><dt>发生时间</dt><dd>{{ time(detail.occurredAt) }}</dd></div><div><dt>资源</dt><dd>{{ detail.resourceType }}<code>{{ detail.resourceId }}</code></dd></div><div><dt>操作主体</dt><dd><code>{{ detail.actorUserId }}</code></dd></div><div><dt>操作</dt><dd>{{ detail.action }}</dd></div><div><dt>请求标识</dt><dd><code>{{ detail.requestId }}</code></dd></div><div v-if="detail.appId"><dt>应用 / 版本</dt><dd>{{ detail.appId }}<code>{{ detail.releaseVersion }}</code></dd></div><div v-if="detail.runId"><dt>运行标识</dt><dd><code>{{ detail.runId }}</code></dd></div><div v-if="detail.gatewayRequestId"><dt>网关关联</dt><dd><code>{{ detail.gatewayRequestId }}</code></dd></div><div v-for="(value, key) in detail.details" :key="key"><dt>{{ detailNames[key] ?? key }}</dt><dd>{{ value ?? '未记录' }}</dd></div></dl><ElButton :disabled="exporting" @click="correlate(detail.requestId)">查看关联记录</ElButton><ElButton v-if="detail.gatewayRequestId && detail.gatewayRequestId !== detail.requestId" :disabled="exporting" @click="correlate(detail.gatewayRequestId)">查看网关关联</ElButton></template></aside></div>
      <p class="audit-note">成功仅表示对应操作或传输完成；未完成表示缺少最终记录，不代表仍在运行。关联查询仍受当前授权限制。<template v-if="workspace?.canExport"> 导出最多 1000 条，超出会明确标记。</template> 历史记录当前不自动清理。</p>
    </template>
  </section>
</template>
