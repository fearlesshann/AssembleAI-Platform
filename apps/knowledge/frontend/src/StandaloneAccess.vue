<script setup lang="ts">
import { ReferenceButton, ReferenceSelect, ReferencePagination } from '../../../portal/src/components/reference';

import { onBeforeUnmount, onMounted, ref, shallowRef } from 'vue';
import { createApplicationClient, type ApplicationApi, type ApplicationClient } from '@assembleai/app-sdk';
import { BUILD_APP_ID, BUILD_FRONTEND_VERSION } from './build-context';
import { createStandalonePlatform, StandaloneError, type StandaloneSnapshot } from './standalone';

const emit = defineEmits<{ connection: [value: { api: ApplicationApi; releaseVersion: string } | undefined] }>();
const platform = createStandalonePlatform(BUILD_APP_ID, BUILD_FRONTEND_VERSION);
const snapshot = shallowRef<StandaloneSnapshot>();
const selected = ref('');
const phase = ref<'loading' | 'login' | 'ready' | 'unavailable' | 'updated'>('loading');
const busy = ref(false);
const connected = ref(false);
const message = ref('正在检查平台会话…');
const loginHref = `/bff/login?returnUrl=${encodeURIComponent(`/apps/${BUILD_APP_ID}/${BUILD_FRONTEND_VERSION}/`)}`;
const detailHref = `/applications/${BUILD_APP_ID}`;
let active: AbortController | undefined;
let client: ApplicationClient | undefined;
let expiration: ReturnType<typeof setTimeout> | undefined;
let generation = 0;
let disposed = false;
function disconnect() {
  client?.dispose(); client = undefined; connected.value = false;
  clearTimeout(expiration); emit('connection', undefined);
}
function begin() { active?.abort(); active = new AbortController(); busy.value = true; return { signal: active.signal, id: ++generation }; }
function current(id: number) { return !disposed && generation === id; }
function failure(error: unknown) {
  disconnect();
  if (error instanceof StandaloneError && error.status === 401) { snapshot.value = undefined; phase.value = 'login'; message.value = '登录企业账号后，选择租户并连接平台。'; }
  else if (error instanceof StandaloneError && error.code === 'version_changed') { phase.value = 'updated'; message.value = '应用版本已更新，请从平台应用详情打开当前版本。'; }
  else { phase.value = 'unavailable'; message.value = '平台暂时不可用，请稍后重试连接。'; }
}
async function inspect() {
  const work = begin();
  try {
    const next = await platform.inspect(work.signal);
    if (!current(work.id)) return;
    const previous = snapshot.value;
    const changed = previous && (previous.session.iss !== next.session.iss || previous.session.sub !== next.session.sub || previous.tenantId !== next.tenantId);
    if (changed) disconnect();
    snapshot.value = next; selected.value = next.tenantId ?? ''; phase.value = 'ready';
    message.value = changed ? '账号或租户已改变，请重新连接平台。' : connected.value ? '平台已连接，可以查阅文档并开始提问。' : '确认当前租户后，连接平台以查阅当前授权文档。';
  } catch (error) { if (current(work.id) && !work.signal.aborted) failure(error); }
  finally { if (current(work.id)) busy.value = false; }
}
async function selectTenant() {
  disconnect(); const work = begin();
  try {
    await platform.select(selected.value, work.signal);
    const next = await platform.inspect(work.signal);
    if (!current(work.id)) return;
    snapshot.value = next; selected.value = next.tenantId ?? ''; phase.value = 'ready'; message.value = '租户已选择，请连接平台。';
  } catch (error) { if (current(work.id) && !work.signal.aborted) failure(error); }
  finally { if (current(work.id)) busy.value = false; }
}
async function connect() {
  const expected = snapshot.value;
  if (!expected?.tenantId) return;
  disconnect(); const work = begin();
  try {
    const next = await platform.inspect(work.signal);
    if (!current(work.id)) return;
    snapshot.value = next; selected.value = next.tenantId ?? '';
    if (next.session.iss !== expected.session.iss || next.session.sub !== expected.session.sub || next.tenantId !== expected.tenantId) {
      message.value = '账号或租户已改变，请确认后重新连接平台。'; return;
    }
    const run = await platform.connect(expected.tenantId, work.signal);
    if (!current(work.id)) return;
    client = createApplicationClient(run); connected.value = true;
    emit('connection', { api: client.api, releaseVersion: run.releaseVersion });
    message.value = '平台已连接，可以查阅文档并开始提问。';
    expiration = setTimeout(() => { disconnect(); message.value = '本次运行已到期，请重新连接平台。'; }, Math.max(0, Date.parse(run.expiresAt) - Date.now()));
  } catch (error) { if (current(work.id) && !work.signal.aborted) failure(error); }
  finally { if (current(work.id)) busy.value = false; }
}
const focus = () => { if (!busy.value) void inspect(); };
onMounted(() => { void inspect(); window.addEventListener('focus', focus); });
onBeforeUnmount(() => { disposed = true; generation++; active?.abort(); disconnect(); window.removeEventListener('focus', focus); });
</script>

<template>
  <section class="knowledge-access" aria-label="独立运行平台连接" :aria-busy="busy">
    <h3>平台连接</h3><p role="status">{{ message }}</p>
    <a v-if="phase === 'login'" :href="loginHref">使用企业账号登录</a>
    <a v-else-if="phase === 'updated'" :href="detailHref">前往平台应用详情</a>
    <div v-else-if="phase === 'ready'" class="knowledge-controls">
      <div class="subwindow-field"><span>当前租户</span><ReferenceSelect v-model="selected" label="独立运行租户" :disabled="busy" @change="selectTenant" :options="[{ value: '', label: '选择租户', disabled: true }, ...(snapshot?.tenants ?? []).map(tenant => ({ value: tenant.tenantId, label: tenant.name }))]" /></div>
      <ReferenceButton :disabled="busy || !snapshot?.tenantId || connected" @click="connect">{{ connected ? '已连接平台' : '连接平台' }}</ReferenceButton>
    </div>
    <ReferenceButton v-else-if="phase === 'unavailable'" :disabled="busy" @click="inspect">重试平台连接</ReferenceButton>
  </section>
</template>
