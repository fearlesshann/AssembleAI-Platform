<script setup lang="ts">
import { ReferenceButton, ReferenceSelect, ReferencePagination } from '../../../portal/src/components/reference';
import '../../../portal/src/reference-tokens.css';
import '../../../portal/src/reference-source.css';

import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';
import type { ApplicationApi, PlatformContext } from '@assembleai/app-sdk';
import StandaloneAccess from './StandaloneAccess.vue';
import ReferenceConfirm from '../../../portal/src/components/reference/ReferenceConfirm.vue';
import { BUILD_APP_ID, BUILD_FRONTEND_VERSION } from './build-context';
import { createStandalonePlatform, type StandaloneSnapshot } from './standalone';
import { createKnowledgeLibrary, type KnowledgeDocument, type LibraryAccess } from './library';
import { contextIdentity, createAnswerStream, fields, type Answer, type Stage } from './protocol';

const props = defineProps<{ context: PlatformContext; api?: ApplicationApi }>();
const standalone = shallowRef<{ api: ApplicationApi; releaseVersion: string }>();
const offered = computed(() => props.api ?? standalone.value?.api);
const invalidated = shallowRef<ApplicationApi>();
const api = computed(() => offered.value === invalidated.value ? undefined : offered.value);
const platform = createStandalonePlatform(BUILD_APP_ID, BUILD_FRONTEND_VERSION);
const library = createKnowledgeLibrary();
const snapshot = shallowRef<StandaloneSnapshot>();
const documents = shallowRef<KnowledgeDocument[]>([]);
const access = shallowRef<LibraryAccess>();
const documentPage = ref(1); const documentPageSize = ref(10);
const shownDocuments = computed(() => documents.value.slice((documentPage.value - 1) * documentPageSize.value, documentPage.value * documentPageSize.value));
watch(documentPageSize, () => { documentPage.value = 1; });
watch(() => documents.value.length, count => { documentPage.value = Math.min(documentPage.value, Math.max(1, Math.ceil(count / documentPageSize.value))); });
const loading = ref(false); const writing = ref(false); const libraryMessage = ref('');
const libraryError = ref(false); const uploadScope = ref(''); const file = shallowRef<File>();
const fileInput = ref<HTMLInputElement>(); const confirmation = shallowRef<KnowledgeDocument>();
const question = ref(''); const busy = ref(false); const phase = ref<Stage>();
const message = ref(''); const error = ref(false); const answer = shallowRef<Answer>();
const checking = ref(false); const localTheme = ref<'light' | 'dark'>('light');
const theme = computed(() => props.context.mode === 'standalone' ? localTheme.value : props.context.theme);
const questionLength = computed(() => [...question.value.trim()].length);
const canAsk = computed(() => !!api.value && !!snapshot.value?.tenantId && !busy.value && !loading.value && !checking.value
  && questionLength.value > 0 && questionLength.value <= 1000);
const stages: { value: Stage; label: string }[] = [
  { value: 'retrieving', label: '检索文档' }, { value: 'answering', label: '整理答案' }, { value: 'verifying', label: '核对引用' },
];
const statuses: Record<string, string> = { queued: '等待处理', processing: '正在解析', extracted: '已解析', indexing: '正在索引', ready: '可问答', failed: '处理失败' };
const failureNames: Record<string, string> = {
  ocr_required: '该 PDF 没有可提取的文字，请上传文本层 PDF。', encrypted_document: '请先解除文档加密后重新上传。',
  unsupported_document: '此文档格式不受支持。', empty_document: '文档中没有可提取的文字。',
  extracted_text_too_large: '文档文字过多，请拆分后上传。', document_too_complex: '文档结构过于复杂，请简化后上传。',
  invalid_document: '无法读取文档，请检查文件是否完整。', parser_timeout: '解析超时，请拆分文档后重试。',
  parser_unavailable: '解析服务暂时不可用。', embedding_unavailable: '索引服务暂时不可用。',
  index_too_large: '文档片段过多，请拆分后上传。', index_profile_mismatch: '索引配置不匹配，请联系管理员。',
};
let disposed = false; let generation = 0; let libraryGeneration = 0;
let controller: AbortController | undefined; let libraryController: AbortController | undefined;
let mutation: AbortController | undefined; let referenceController: AbortController | undefined;
let poll: ReturnType<typeof setTimeout> | undefined;
let polls = 0;
function same(left: StandaloneSnapshot | undefined, right: StandaloneSnapshot | undefined) {
  return !!left && !!right && left.session.iss === right.session.iss && left.session.sub === right.session.sub && left.tenantId === right.tenantId;
}
function stopAnswer() { generation++; controller?.abort(); controller = undefined; referenceController?.abort(); referenceController = undefined;
  answer.value = undefined; busy.value = false; checking.value = false; phase.value = undefined; }
function reset() {
  libraryGeneration++; libraryController?.abort(); mutation?.abort(); clearTimeout(poll); poll = undefined;
  stopAnswer(); documents.value = []; access.value = undefined; snapshot.value = undefined; confirmation.value = undefined;
  file.value = undefined; if (fileInput.value) fileInput.value.value = ''; loading.value = false; writing.value = false;
  polls = 0;
  message.value = ''; error.value = false; libraryMessage.value = ''; libraryError.value = false;
}
function failureMessage(failure: unknown): string {
  const value = failure as { status?: number; code?: string } | undefined;
  if (value?.status === 401) return '登录已失效，请重新登录平台。';
  if (value?.status === 403 || value?.code === 'authorization_revoked') return '访问权限已改变，请刷新文档或重新连接平台。';
  if (value?.status === 404) return '文档已移除或当前账号无权访问，请刷新文档。';
  if (value?.status === 409) return '文档已被更新，请刷新后重新确认。';
  if (value?.code === 'expired') return '本次运行已到期，请重新打开或连接知识库。';
  if (value?.code === 'invalid_file') return '请选择不超过 5 MiB 的 PDF、DOCX、TXT 或 Markdown 文件。';
  if (value?.code === 'timeout' || value?.status === 504) return '处理超时，请稍后重试。';
  return '本次操作未完成，请稍后重试。';
}
function cancel() { stopAnswer(); error.value = false; message.value = '已取消提问，未保留本次答案。'; }
function scopeLabel(value: string | null) { return value === null ? '全租户' : `组织 ${value.slice(0, 8)}`; }
function locatorLabel(value: string) {
  const location = /^(page|paragraph|lines):([0-9]+)(?:-([0-9]+))?$/.exec(value);
  if (!location) return value;
  const unit = location[1] === 'page' ? '页' : location[1] === 'paragraph' ? '段' : '行';
  return `第 ${location[2]}${location[3] && location[3] !== location[2] ? `–${location[3]}` : ''} ${unit}`;
}
function chooseFile(event: Event) { file.value = (event.target as HTMLInputElement).files?.[0]; }
function schedulePoll() {
  clearTimeout(poll); poll = undefined;
  if (!disposed && polls < 60 && documents.value.some(doc => ['queued', 'processing', 'extracted', 'indexing'].includes(doc.status)))
    poll = setTimeout(() => { if (!writing.value && !busy.value && !document.hidden) { polls++; void refresh(false); } else schedulePoll(); }, 5000);
}
async function assertIdentity(expected: StandaloneSnapshot, signal: AbortSignal) {
  const next = await platform.inspect(signal);
  if (!same(expected, next)) { invalidated.value = offered.value; reset(); throw new Error('Identity changed'); }
  return next;
}
async function refresh(explicit = true) {
  const capability = api.value;
  if (!capability || disposed || writing.value) return;
  libraryController?.abort(); const work = new AbortController(); libraryController = work; const id = ++libraryGeneration;
  const current = () => !disposed && id === libraryGeneration && !work.signal.aborted;
  const previous = snapshot.value; loading.value = true; confirmation.value = undefined;
  if (explicit) { polls = 0; stopAnswer(); libraryMessage.value = ''; libraryError.value = false; }
  try {
    const next = await platform.inspect(work.signal); if (!current()) return;
    if (previous && !same(previous, next)) { invalidated.value = capability; reset(); return; }
    if (!next.tenantId) throw new Error('No selected tenant');
    const [items, grants] = await Promise.all([library.list(next.tenantId, work.signal), library.access(next.tenantId, work.signal)]);
    await assertIdentity(next, work.signal); if (!current()) return;
    snapshot.value = next; documents.value = items; access.value = grants;
    const scopes = grants.uploadScopes.map(scope => scope ?? 'tenant');
    if (!scopes.includes(uploadScope.value)) uploadScope.value = scopes[0] ?? '';
    if (answer.value && answer.value.documents.some(ref => !items.some(doc => doc.documentId === ref.documentId && doc.revision === ref.revision && doc.status === 'ready'))) {
      // The document list is bounded: a missing reference triggers a server check, never an inferred grant.
      await checkReferences();
    }
    return true;
  } catch (failure) {
    if (current()) { stopAnswer(); documents.value = []; access.value = undefined; libraryError.value = true; libraryMessage.value = failureMessage(failure); }
  } finally { if (current()) { loading.value = false; libraryController = undefined; schedulePoll(); } work.abort(); }
}
async function mutate(action: (tenant: string, signal: AbortSignal) => Promise<void>, success: string) {
  const expected = snapshot.value; if (!expected?.tenantId || writing.value || !api.value) return;
  stopAnswer(); libraryController?.abort(); libraryGeneration++; loading.value = false; clearTimeout(poll);
  const work = new AbortController(); mutation = work; writing.value = true; libraryError.value = false; libraryMessage.value = '';
  let committed = false;
  try {
    await assertIdentity(expected, work.signal); if (work.signal.aborted) return;
    await action(expected.tenantId, work.signal); committed = true;
    await assertIdentity(expected, work.signal); if (work.signal.aborted || disposed) return;
    confirmation.value = undefined; file.value = undefined; if (fileInput.value) fileInput.value.value = '';
    libraryMessage.value = success;
  } catch (failure) {
    if (!work.signal.aborted && !disposed) { libraryError.value = true; libraryMessage.value = committed ? '操作已提交，当前状态无法确认，请刷新文档。' : `${failureMessage(failure)} 如状态不确定，请先刷新文档。`; }
  } finally {
    if (!work.signal.aborted && !disposed) {
      writing.value = false; mutation = undefined;
      const refreshed = await refresh(false);
      if (committed && !refreshed && !disposed && same(expected, snapshot.value)) {
        libraryError.value = true; libraryMessage.value = '操作已提交，但最新文档列表读取失败，请刷新文档确认结果。';
      }
    }
    work.abort();
  }
}
async function upload() {
  const selected = file.value; const scope = uploadScope.value;
  if (!selected || !access.value?.uploadScopes.some(value => (value ?? 'tenant') === scope)) return;
  await mutate(async (tenant, signal) => { await library.upload(tenant, selected, scope === 'tenant' ? null : scope, signal); }, '文档已上传，处理完成后即可用于问答。');
}
async function remove() {
  const item = confirmation.value; if (!item || !access.value?.canManage(item)) return;
  await mutate(async (tenant, signal) => library.remove(tenant, item, signal), '文档已删除，后续问答将不再使用它。');
}
async function download(documentId: string, fileName: string) {
  const expected = snapshot.value; if (!expected?.tenantId || writing.value) return;
  const work = new AbortController(); mutation = work; writing.value = true; libraryError.value = false;
  try {
    await assertIdentity(expected, work.signal);
    const blob = await library.download(expected.tenantId, documentId, work.signal);
    await assertIdentity(expected, work.signal); if (work.signal.aborted || disposed) return;
    const url = URL.createObjectURL(blob); const link = document.createElement('a'); link.href = url; link.download = fileName;
    link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
  } catch (failure) { if (!work.signal.aborted && !disposed) { libraryError.value = true; libraryMessage.value = failureMessage(failure); } }
  finally { if (!work.signal.aborted && !disposed) { writing.value = false; mutation = undefined; } work.abort(); }
}
async function checkReferences() {
  const existing = answer.value; const capability = api.value; const expected = snapshot.value;
  if (!existing || !capability || !expected?.tenantId || checking.value || disposed) return;
  const work = new AbortController(); referenceController = work; checking.value = true; answer.value = undefined;
  const id = generation;
  try {
    await assertIdentity(expected, work.signal);
    const response = await capability.json('references', { method: 'POST', signal: work.signal, body: {
      profile: existing.profile, authorizationVersion: existing.authorizationVersion, documents: existing.documents,
    } });
    if (!fields(response, ['valid']) || response.valid !== true) throw new Error('References changed');
    await assertIdentity(expected, work.signal);
    if (!disposed && id === generation && !work.signal.aborted) answer.value = existing;
  } catch {
    if (!disposed && id === generation && !work.signal.aborted) { answer.value = undefined; error.value = true; message.value = '文档或访问状态已改变，旧答案已清除，请重新提问。'; }
  } finally { if (!disposed && id === generation) { checking.value = false; referenceController = undefined; } work.abort(); }
}
async function ask() {
  const capability = api.value; const expected = snapshot.value; if (!capability || !expected?.tenantId || !canAsk.value) return;
  stopAnswer(); const id = generation; const work = new AbortController(); controller = work; busy.value = true; error.value = false;
  const current = () => !disposed && id === generation && !work.signal.aborted;
  message.value = '正在准备检索…';
  try {
    await assertIdentity(expected, work.signal); const stream = createAnswerStream();
    for await (const event of capability.stream('ask', { method: 'POST', body: { question: question.value.trim() }, signal: work.signal })) {
      if (!current()) return; const change = stream.accept(event);
      if (change.kind === 'stage') { phase.value = change.stage; message.value = `${stages.find(stage => stage.value === change.stage)!.label}…`; }
    }
    await assertIdentity(expected, work.signal); if (!current()) return;
    answer.value = stream.finish(); phase.value = undefined;
    message.value = answer.value.refusal ? '当前文档不足以支持回答。' : '回答完成，可通过下方引用核对原文。';
  } catch (failure) {
    if (current()) { answer.value = undefined; phase.value = undefined; error.value = true; message.value = `回答未完成。${failureMessage(failure)}`; }
  } finally { if (current()) { busy.value = false; controller = undefined; schedulePoll(); } work.abort(); }
}
function focus() {
  if (!api.value || disposed || writing.value) return;
  // Hide prior answers during the current permission check; no background model request.
  if (answer.value) void checkReferences();
  if (!busy.value && !loading.value) void refresh(false);
}
watch(() => contextIdentity(props.context), () => { invalidated.value = offered.value; reset(); }, { flush: 'sync' });
watch(api, () => { reset(); if (api.value) void refresh(); }, { immediate: true, flush: 'sync' });
onMounted(() => window.addEventListener('focus', focus));
onBeforeUnmount(() => { disposed = true; reset(); window.removeEventListener('focus', focus); });
</script>

<template>
  <article class="reference-ui knowledge-app" :data-theme="theme" :data-mode="context.mode" data-testid="knowledge-app">
    <header class="knowledge-header">
      <div><h1>企业知识库</h1><p>从授权文档中查找答案，回到原文核对依据。</p></div>
      <div class="knowledge-header-actions"><span>{{ context.mode === 'embedded' ? '平台内运行' : '独立运行' }}</span><ReferenceButton v-if="context.mode === 'standalone'" type="button" @click="localTheme = theme === 'light' ? 'dark' : 'light'">{{ theme === 'light' ? '切换暗色' : '切换浅色' }}</ReferenceButton></div>
    </header>
    <StandaloneAccess v-if="context.mode === 'standalone'" @connection="standalone = $event" />
    <div class="knowledge-workspace">
      <section class="knowledge-library" aria-label="文档库" :aria-busy="loading || writing">
        <div class="knowledge-section-heading"><h2>文档库 <span v-if="snapshot">{{ documents.length }}</span></h2><ReferenceButton variant="link" type="button" :disabled="!api || loading || writing" @click="refresh()">刷新文档</ReferenceButton></div>
        <form v-if="access?.uploadScopes.length" class="knowledge-upload" aria-label="上传文档" @submit.prevent="upload">
          <label for="knowledge-file" class="subwindow-field">添加文档</label><div class="knowledge-file-picker"><ReferenceButton :disabled="writing || loading" @click="fileInput?.click()">选择文件</ReferenceButton><span class="subwindow-hint">{{ file?.name ?? '尚未选择文件' }}</span></div><input class="knowledge-file-input" id="knowledge-file" ref="fileInput" type="file" accept=".pdf,.docx,.txt,.md" :disabled="writing || loading" @change="chooseFile" />
          <p class="knowledge-hint">PDF / DOCX / TXT / Markdown，单份最多 5 MiB。PDF 需包含可提取文字。</p>
          <div class="knowledge-upload-actions"><div class="subwindow-field"><span>所属范围</span><ReferenceSelect id="knowledge-scope" v-model="uploadScope" label="所属范围" :disabled="writing || loading" :options="access.uploadScopes.map(scope => ({ value: scope ?? 'tenant', label: scopeLabel(scope) }))" /></div><ReferenceButton type="submit" :disabled="!file || writing || loading">{{ writing ? '正在处理…' : '上传文档' }}</ReferenceButton></div>
        </form>
        <p v-if="libraryMessage" class="knowledge-notice" :class="{ 'knowledge-error': libraryError }" :role="libraryError ? 'alert' : 'status'">{{ libraryMessage }}</p>
        <div v-if="loading && !documents.length" class="knowledge-skeleton" aria-label="正在读取文档"><span></span><span></span><span></span></div>
        <div v-else-if="documents.length" class="data-grid knowledge-document-grid" data-testid="knowledge-documents"><div class="admin-table-wrap data-grid__body"><div class="scroll-content data-grid__scroll"><table class="data-grid__table" aria-label="授权文档"><thead><tr><th>文档</th><th>状态</th><th>所属范围</th><th>大小</th><th class="data-grid__actions">操作</th></tr></thead><tbody><tr v-for="item in shownDocuments" :key="item.documentId"><td><div class="data-grid__person"><span class="avatar sm">{{ item.fileName.split('.').pop()?.slice(0, 1).toUpperCase() }}</span><span :title="item.fileName">{{ item.fileName }}</span></div><p v-if="item.status === 'failed'" class="knowledge-failure">{{ failureNames[item.failureCode ?? ''] ?? '文档处理失败，请联系管理员检查或重新上传。' }}</p><p v-else-if="item.status === 'extracted'" class="knowledge-hint">文字已提取，尚未可用于问答。</p></td><td><span class="status-pill" :class="{ draft: item.status !== 'ready' }"><i class="sdot"></i>{{ statuses[item.status] }}</span></td><td class="muted" :title="item.organizationId ?? undefined">{{ scopeLabel(item.organizationId) }}</td><td class="muted tabular">{{ Math.max(1, Math.ceil(item.sizeBytes / 1024)) }} KB</td><td class="data-grid__actions"><div class="data-grid__action-content"><ReferenceButton variant="link" :disabled="writing" @click="download(item.documentId, item.fileName)">下载原文</ReferenceButton><ReferenceButton v-if="access?.canManage(item)" variant="link" :disabled="writing || loading" @click="confirmation = item">删除</ReferenceButton></div></td></tr></tbody></table></div></div><ReferencePagination :current-page="documentPage" v-model:page-size="documentPageSize" :total="documents.length" :disabled="writing" @change="documentPage = $event" /></div>
        <div v-else-if="!libraryError" class="empty-state is-visible knowledge-library-empty"><div class="empty-icon" aria-hidden="true">∅</div><h3 class="empty-title">{{ api ? '还没有可见文档' : '连接后查看文档' }}</h3><p class="empty-desc">{{ api ? '上传一份操作规程或质量说明，处理完成后即可提问。没有上传权限时，请联系管理员。' : '文档列表与问答范围由当前账号和租户权限决定。' }}</p></div>
        <p v-if="documents.length" class="knowledge-hint knowledge-list-limit">此列表展示最近 100 份文档中的可见项目；问答检索覆盖全部授权文档。</p>
      </section>
      <section class="knowledge-answer-area" aria-label="知识问答">
        <form class="knowledge-question" aria-label="文档提问" @submit.prevent="ask">
          <label for="knowledge-question">你想从文档中了解什么？</label>
          <div class="composer-wrap"><div class="composer"><textarea id="knowledge-question" v-model="question" rows="3" maxlength="2000" :disabled="!api || busy || checking" :aria-invalid="questionLength > 1000" placeholder="例如：设备点检发现异常时，应先执行哪些步骤？" aria-describedby="knowledge-question-help"></textarea>
          <div class="composer-bar knowledge-question-actions"><p id="knowledge-question-help" class="knowledge-hint">仅检索当前授权文档 · {{ questionLength > 1000 ? `已超出 ${questionLength - 1000} 字` : `${questionLength} / 1000 字` }}</p><div><ReferenceButton v-if="busy" type="button" @click="cancel">取消提问</ReferenceButton><ReferenceButton type="submit" primary :disabled="!canAsk">{{ busy ? '正在查找…' : '查找答案' }}</ReferenceButton></div></div></div></div>
        </form>
        <section class="knowledge-output" aria-label="答案与来源" :aria-busy="busy || checking">
          <div class="knowledge-section-heading"><h2>答案与来源</h2><span v-if="answer && !answer.refusal">{{ answer.citations.length }} 处引用</span></div>
          <p v-if="message" data-testid="knowledge-status" class="knowledge-notice" :class="{ 'knowledge-error': error }" :role="error ? 'alert' : 'status'">{{ message }}</p>
          <ol v-if="busy" class="knowledge-stages" aria-label="问答进度"><li v-for="stage in stages" :key="stage.value" :aria-current="phase === stage.value ? 'step' : undefined">{{ stage.label }}</li></ol>
          <p v-if="checking" class="knowledge-hint" role="status">正在复核文档与访问权限…</p>
          <div v-else-if="answer" data-testid="knowledge-answer">
            <p class="knowledge-answer-text">{{ answer.answer }}</p>
            <ol v-if="answer.citations.length" class="knowledge-citations" aria-label="原文引用">
              <li v-for="(citation, index) in answer.citations" :key="citation.chunkId"><div class="knowledge-citation-heading"><span class="avatar sm knowledge-citation-number">{{ index + 1 }}</span><h3>{{ citation.fileName }}</h3><span>{{ locatorLabel(citation.locator) }}</span></div><blockquote>{{ citation.quote }}</blockquote><ReferenceButton type="button" variant="link" :disabled="writing" @click="download(citation.documentId, citation.fileName)">下载原文核对</ReferenceButton></li>
            </ol>
          </div>
          <div v-else-if="!busy && !error && !checking" class="empty-state is-visible knowledge-empty"><div class="empty-icon" aria-hidden="true">∅</div><h3 class="empty-title">每个答案，都有出处可查</h3><p class="empty-desc">提出具体问题，查看相关说明与原文位置。文档没有充分依据时，会明确说明。</p></div>
          <div v-else-if="busy" class="knowledge-skeleton" aria-hidden="true"><span></span><span></span><span></span></div>
        </section>
      </section>
    </div>
    <footer class="knowledge-footer">处理完成的文档才能用于问答。答案供查阅参考，关键操作请核对现行规程与原文。</footer>
    <ReferenceConfirm :request="confirmation ? { title: '确认删除文档', description: '删除后，新的问答将不再引用这份文档。', confirmLabel: writing ? '正在删除…' : '确认删除' } : null" :disabled="writing" @resolve="$event ? remove() : (confirmation = undefined)" />
  </article>
</template>
