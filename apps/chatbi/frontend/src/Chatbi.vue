<script setup lang="ts">
import { vReferenceSurface } from '../../../portal/src/components/reference/reference-motion';
import { ReferenceButton, ReferenceSelect, ReferencePagination } from '../../../portal/src/components/reference';
import '../../../portal/src/reference-tokens.css';
import '../../../portal/src/reference-source.css';

import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue';
import type { ApplicationApi, PlatformContext } from '@assembleai/app-sdk';
import StandaloneAccess from './StandaloneAccess.vue';
import { contextIdentity, createResultStream, parseDatasets, type AnalysisResult, type Dataset, type Stage } from './protocol';

const props = defineProps<{ context: PlatformContext; api?: ApplicationApi }>();
const standalone = shallowRef<{ api: ApplicationApi; releaseVersion: string }>();
const offeredCapability = computed(() => props.api ?? standalone.value?.api);
const invalidatedCapability = shallowRef<ApplicationApi>();
const capability = computed(() => offeredCapability.value === invalidatedCapability.value ? undefined : offeredCapability.value);
const localTheme = ref<'light' | 'dark'>('light');
const theme = computed(() => props.context.mode === 'standalone' ? localTheme.value : props.context.theme);
const datasets = shallowRef<Dataset[]>([]);
const datasetId = ref('');
const dataset = computed(() => datasets.value.find((item) => item.datasetId === datasetId.value));
const question = ref('');
const catalogLoading = ref(false);
const catalogMessage = ref('');
const busy = ref(false);
const phase = ref<Stage>();
const status = ref('');
const error = ref(false);
const result = shallowRef<AnalysisResult>();
const evidenceOpen = ref(false);
const resultPage = ref(1); const resultPageSize = ref(10);
const shownRows = computed(() => (result.value?.rows ?? []).slice((resultPage.value - 1) * resultPageSize.value, resultPage.value * resultPageSize.value));
watch([result, resultPageSize], () => { resultPage.value = 1; });
const examples = ['2026年11月1日至2日的产量合计是多少？', '2026年11月2日的产量是多少？', '按产线汇总产量'];
const stages: { value: Stage; label: string }[] = [{ value: 'planning', label: '理解问题' }, { value: 'querying', label: '查询授权数据' }, { value: 'receiving', label: '核对结果' }];
let catalogController: AbortController | undefined;
let controller: AbortController | undefined;
let catalogGeneration = 0;
let generation = 0;
let disposed = false;
const canAsk = computed(() => !!capability.value && !!dataset.value && !busy.value && !catalogLoading.value
  && question.value.trim().length > 0 && [...question.value.trim()].length <= 1000);
function stop() { generation++; controller?.abort(); controller = undefined; busy.value = false; phase.value = undefined; result.value = undefined; evidenceOpen.value = false; }
function cancel() { stop(); error.value = false; status.value = '已取消分析，未保留本次结果。'; }
function clearAnalysis() { stop(); status.value = ''; error.value = false; }
function failureMessage(failure: unknown): string {
  const item = failure as { code?: string; status?: number } | undefined;
  if (item?.code === 'expired') return '本次运行已到期，请重新打开或连接 ChatBI。';
  if (item?.status === 401) return '登录已失效，请重新登录平台。';
  if (item?.status === 403) return '当前账号或租户无权查询此数据集，请刷新数据集或联系管理员。';
  if (item?.code === 'cancelled' || item?.code === 'disposed') return '分析已停止，未保留本次结果。';
  if (item?.code === 'timeout') return '分析超时，请缩小问题范围后重试。';
  return '分析未完成，未保留不完整结果。请检查问题范围或稍后重试。';
}
async function loadCatalog() {
  clearAnalysis(); catalogGeneration++; catalogController?.abort(); catalogController = undefined;
  const id = catalogGeneration; const api = capability.value;
  datasets.value = []; datasetId.value = ''; catalogMessage.value = ''; catalogLoading.value = false;
  if (!api || disposed) return;
  const work = new AbortController(); catalogController = work; catalogLoading.value = true;
  try {
    const next = parseDatasets(await api.json('datasets', { signal: work.signal }));
    if (disposed || id !== catalogGeneration || work.signal.aborted) return;
    datasets.value = next; datasetId.value = next[0]?.datasetId ?? '';
    if (!next.length) catalogMessage.value = '当前没有可分析的数据集。请联系管理员确认数据访问权限。';
  } catch (failure) {
    if (!disposed && id === catalogGeneration && !work.signal.aborted) catalogMessage.value = failureMessage(failure);
  } finally { if (!disposed && id === catalogGeneration) { catalogLoading.value = false; catalogController = undefined; } }
}
async function ask() {
  const api = capability.value; const selected = dataset.value;
  if (!api || !selected || !canAsk.value) return;
  clearAnalysis(); const id = generation; const work = new AbortController(); controller = work; busy.value = true;
  const current = () => !disposed && id === generation && !work.signal.aborted;
  status.value = '正在准备分析…';
  try {
    const stream = createResultStream(selected);
    for await (const event of api.stream('ask', { method: 'POST', body: { datasetId: selected.datasetId, question: question.value.trim() }, signal: work.signal })) {
      if (!current()) return;
      const change = stream.accept(event);
      if (change.kind === 'stage') { phase.value = change.stage; status.value = `${stages.find((stage) => stage.value === change.stage)!.label}…`; }
    }
    if (!current()) return;
    result.value = stream.finish(); phase.value = undefined;
    status.value = result.value.rowCount ? '分析完成。结果仅包含当前授权工厂的数据。' : '查询完成，当前条件没有匹配数据。';
  } catch (failure) {
    if (!current()) return;
    result.value = undefined; error.value = true; phase.value = undefined; status.value = failureMessage(failure);
  } finally { if (current()) { controller = undefined; busy.value = false; } work.abort(); }
}
function chooseExample(value: string) { clearAnalysis(); question.value = value; }
watch(datasetId, clearAnalysis, { flush: 'sync' });
watch(() => contextIdentity(props.context), () => {
  // A context update cannot rebind an old SDK capability to a different identity.
  invalidatedCapability.value = offeredCapability.value; clearAnalysis(); datasets.value = []; datasetId.value = '';
}, { flush: 'sync' });
watch(capability, () => { void loadCatalog(); }, { immediate: true, flush: 'sync' });
onBeforeUnmount(() => { disposed = true; catalogGeneration++; catalogController?.abort(); stop(); });
</script>

<template>
  <article v-reference-surface class="reference-ui chatbi-app" :data-theme="theme" :data-mode="context.mode" data-testid="chatbi-app">
    <header class="chatbi-header">
      <div><h1>ChatBI <span>数据分析</span></h1><p>用自然语言查询数据，并查看每次结果的查询依据。</p></div>
      <div class="chatbi-header-actions"><span class="chatbi-mode">{{ context.mode === 'embedded' ? '平台内运行' : '独立运行' }}</span><ReferenceButton v-if="context.mode === 'standalone'" type="button" @click="localTheme = theme === 'light' ? 'dark' : 'light'">{{ theme === 'light' ? '切换暗色' : '切换浅色' }}</ReferenceButton></div>
    </header>
    <StandaloneAccess v-if="context.mode === 'standalone'" @connection="standalone = $event" />
    <div class="chatbi-workspace">
      <form class="chatbi-compose" aria-label="数据分析问题" @submit.prevent="ask">
        <div class="chatbi-section-heading"><h2>分析范围</h2><ReferenceButton v-if="capability" type="button" variant="link" :disabled="catalogLoading || busy" @click="loadCatalog">刷新数据集</ReferenceButton></div>
        <div class="subwindow-field"><span>数据集</span><ReferenceSelect id="chatbi-dataset" v-model="datasetId" data-testid="chatbi-dataset" label="分析数据集" :disabled="!capability || catalogLoading || !datasets.length" :options="[{ value: '', label: catalogLoading ? '正在读取数据集…' : '选择数据集', disabled: true }, ...datasets.map(item => ({ value: item.datasetId, label: item.displayName }))]" /></div>
        <p v-if="!capability" class="chatbi-hint">连接平台后，可选择当前账号有权访问的数据集。</p>
        <p v-else-if="catalogMessage" class="chatbi-hint" role="status">{{ catalogMessage }}</p>
        <p v-else-if="dataset" class="chatbi-hint">查询范围：当前授权工厂 · 最多 {{ Math.min(100, dataset.maxRows) }} 行</p>
        <div v-if="catalogLoading" class="chatbi-loading" aria-hidden="true"><span></span><span></span></div>
        <label class="chatbi-question-label" for="chatbi-question">你想了解什么？</label>
        <div class="composer-wrap"><div class="composer"><textarea id="chatbi-question" v-model="question" data-testid="chatbi-question" aria-label="分析问题" aria-describedby="chatbi-question-help" rows="5" maxlength="1000" :disabled="!dataset || busy" placeholder="例如：按产线汇总这两天的产量"></textarea>
        <p id="chatbi-question-help" class="chatbi-hint">写明时间范围和指标，问题限 1000 字。</p>
        <div class="composer-bar chatbi-actions"><ReferenceButton primary type="submit" data-testid="chatbi-submit" :disabled="!canAsk">{{ busy ? '正在分析…' : '开始分析' }}</ReferenceButton><ReferenceButton v-if="busy" type="button" data-testid="chatbi-cancel" @click="cancel">取消分析</ReferenceButton></div></div></div>
        <section class="chatbi-examples" aria-label="制造业测试问题"><h3>制造业测试问题</h3><p>以下问题用于测试数据集，可选中后修改。</p><ReferenceButton variant="link" v-for="example in examples" :key="example" type="button" :disabled="!dataset || busy" @click="chooseExample(example)">{{ example }} <span aria-hidden="true">↗</span></ReferenceButton></section>
      </form>
      <section class="chatbi-output" aria-label="分析结果" :aria-busy="busy">
        <div class="chatbi-section-heading"><h2>分析结果</h2><span v-if="result" class="chatbi-result-count">{{ result.rowCount }} 行</span></div>
        <p v-if="status" data-testid="chatbi-status" :role="error ? 'alert' : 'status'" class="chatbi-status" :class="{ 'chatbi-error': error }">{{ status }}</p>
        <ol v-if="busy" class="chatbi-stages" aria-label="分析进度"><li v-for="(stage, index) in stages" :key="stage.value" :class="{ active: phase === stage.value, passed: phase && stages.findIndex(item => item.value === phase) > index }" :aria-current="phase === stage.value ? 'step' : undefined"><span aria-hidden="true">{{ index + 1 }}</span>{{ stage.label }}</li></ol>
        <div v-if="busy" class="chatbi-progress-note"><p>正在完成查询和权限核验。</p><p>完整结果准备好后会显示在这里。</p></div>
        <template v-else-if="result">
          <div data-testid="chatbi-result" class="chatbi-result">
            <p class="chatbi-summary">{{ result.summary }}</p>
            <div v-if="result.rowCount" class="data-grid chatbi-table-wrap" tabindex="0" role="region" aria-label="查询结果表，可横向滚动">
              <div class="data-grid__toolbar"><span class="list-meta">{{ result.datasetName }} · {{ result.scope }}</span></div><div class="admin-table-wrap data-grid__body"><div class="scroll-content data-grid__scroll"><table class="data-grid__table" :aria-label="`${result.datasetName} · ${result.scope}`"><thead><tr><th v-for="column in result.columns" :key="column" scope="col">{{ column }}</th></tr></thead><tbody><tr v-for="(row, rowIndex) in shownRows" :key="rowIndex"><td v-for="(cell, columnIndex) in row" :key="columnIndex"><span v-if="cell === null" class="chatbi-null" aria-label="空值">—</span><template v-else>{{ cell }}</template></td></tr></tbody></table></div></div><ReferencePagination :current-page="resultPage" v-model:page-size="resultPageSize" :total="result.rows.length" @change="resultPage = $event" />
            </div>
            <p v-else class="chatbi-empty-result">没有匹配数据。可调整日期或筛选条件后重新分析。</p>
            <details class="chatbi-evidence" :open="evidenceOpen" @toggle="evidenceOpen = ($event.target as HTMLDetailsElement).open"><summary>查看查询依据</summary><dl><div><dt>数据集</dt><dd>{{ result.datasetName }}</dd></div><div><dt>数据范围</dt><dd>{{ result.scope }}</dd></div><div><dt>查询编号</dt><dd>{{ result.queryRequestId }}</dd></div></dl><h3>执行的结构化查询</h3><pre data-testid="chatbi-evidence">{{ result.queryText }}</pre></details>
          </div>
        </template>
        <div v-else-if="!error" class="empty-state is-visible chatbi-empty"><div class="empty-icon" aria-hidden="true">∅</div><h3 class="empty-title">从一个数据问题开始</h3><p class="empty-desc">选择数据集，描述你要查看的指标与时间范围。分析完成后，在这里核对数据和查询依据。</p></div>
      </section>
    </div>
    <footer class="chatbi-footer">结果受当前账号与租户权限约束；更换数据集或分析范围后，请重新查询。</footer>
  </article>
</template>
