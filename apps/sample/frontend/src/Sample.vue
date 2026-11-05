<script setup lang="ts">
import { ReferenceButton, ReferenceSelect, ReferencePagination } from '../../../portal/src/components/reference';
import '../../../portal/src/reference-tokens.css';
import '../../../portal/src/reference-source.css';

import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';
import { ApplicationApiError, type ApplicationApi, type PlatformContext } from '@assembleai/app-sdk';
import StandaloneAccess from './StandaloneAccess.vue';
const props = defineProps<{ context: PlatformContext; api?: ApplicationApi }>();
const count = ref(0);
const signals = ref(0);
const onSignal = () => { signals.value += 1; };
const standalone = shallowRef<{ api: ApplicationApi; releaseVersion: string }>();
const capability = computed(() => props.api ?? standalone.value?.api);
const releaseVersion = computed(() => standalone.value?.releaseVersion ?? props.context.releaseVersion);
const busy = ref(false);
const result = ref('');
const deltas = ref<string[]>([]);
const status = ref('');
let active: AbortController | undefined;
let generation = 0;
let disposed = false;
const display = (data: unknown) => JSON.stringify(data, null, 2).slice(0, 6000);
function stop() { generation++; active?.abort(); active = undefined; busy.value = false; }
function cancel() { stop(); status.value = '已取消，调用未完成。'; }
watch(capability, () => { stop(); result.value = ''; deltas.value = []; status.value = ''; }, { flush: 'sync' });
async function verify(stream: boolean) {
  const api = capability.value;
  if (!api || busy.value) return;
  stop(); const id = generation; const controller = new AbortController(); active = controller;
  busy.value = true; result.value = ''; deltas.value = []; status.value = '正在调用…';
  const current = () => !disposed && id === generation;
  try {
    if (stream) {
      let completed = false;
      for await (const event of api.stream<{ text?: string }>('events', { query: { count: '3', delay_ms: '150' }, signal: controller.signal })) {
        if (!current()) return;
        if (event.event === 'delta') { deltas.value.push(typeof event.data?.text === 'string' ? event.data.text : display(event.data)); status.value = '正在接收增量…'; }
        else { completed = true; result.value = display(event.data); }
      }
      if (!completed) throw new ApplicationApiError('stream_interrupted');
    } else {
      const data = await api.json('probe', { method: 'POST', body: { message: '验证调用' }, signal: controller.signal });
      if (!current()) return;
      result.value = display(data);
    }
    if (current()) status.value = stream ? '流式验证完成。' : 'JSON 验证完成。';
  } catch (error) {
    if (!current()) return;
    // The host and UMD artifact can contain separate SDK copies; do not rely on instanceof.
    const failure = error as Partial<ApplicationApiError> | null;
    if (failure?.code && ['cancelled', 'disposed'].includes(failure.code)) status.value = '已取消，调用未完成。';
    else if (failure?.code === 'expired') status.value = '本次运行已到期，请重新打开或连接应用。';
    else if (failure?.status === 401) status.value = '登录已失效，请重新登录平台。';
    else if (failure?.status === 403) status.value = '当前账号或租户无权执行此调用。';
    else status.value = '调用未完成，请确认平台连接后重试。';
  } finally { if (current()) { busy.value = false; active = undefined; } }
}
onMounted(() => window.addEventListener('assembleai:sample-ping', onSignal));
onBeforeUnmount(() => { disposed = true; stop(); window.removeEventListener('assembleai:sample-ping', onSignal); });
</script>

<template>
  <article class="reference-ui sample-app" :data-theme="context.theme" data-testid="sample-app">
    <header class="sample-header"><span class="sample-icon" aria-hidden="true">S</span><div><h2>应用已就绪</h2><p>这是用于验证平台接入能力的样例，不连接业务数据。</p></div><span class="sample-mode">{{ context.mode === 'embedded' ? '平台内运行' : '独立运行' }}</span></header>
    <dl class="sample-details"><div><dt>应用</dt><dd>{{ context.appId }}</dd></div><div><dt>发布版本</dt><dd>{{ releaseVersion }}</dd></div><div><dt>外观</dt><dd>{{ context.theme === 'dark' ? '暗色' : '浅色' }}</dd></div></dl>
    <StandaloneAccess v-if="context.mode === 'standalone'" @connection="standalone = $event" />
    <section class="sample-protected" aria-label="受保护调用验证" :aria-busy="busy">
      <h3>受保护调用验证</h3>
      <p v-if="!capability">当前仅验证界面交互；连接平台后才能调用受保护接口。</p>
      <p v-else>调用经过平台网关，校验当前会话、租户和应用权限。流式结果收到完整结束事件后才算完成。</p>
      <div class="sample-controls"><ReferenceButton :disabled="!capability || busy" @click="verify(false)">JSON 验证</ReferenceButton><ReferenceButton :disabled="!capability || busy" @click="verify(true)">流式验证</ReferenceButton><ReferenceButton v-if="busy" @click="cancel">取消调用</ReferenceButton></div>
      <p role="status" data-testid="sample-api-status">{{ status }}</p>
      <ol v-if="deltas.length" class="sample-deltas" aria-label="接收增量"><li v-for="(delta, index) in deltas" :key="index">{{ delta }}</li></ol>
      <pre v-if="result" class="sample-result" data-testid="sample-api-result">{{ result }}</pre>
    </section>
    <section class="sample-interaction"><h3>试一下交互</h3><p>退出应用再重新进入，计数应从零开始。</p><div class="sample-controls"><ReferenceButton @click="count++">增加计数</ReferenceButton><output aria-label="当前计数" data-testid="sample-count">{{ count }}</output></div></section>
    <footer>生命周期事件：<output data-testid="sample-signals">{{ signals }}</output><span>仅用于接入验证</span></footer>
  </article>
</template>
