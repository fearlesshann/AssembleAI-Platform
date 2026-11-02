<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import { loadMicroApp, type MicroApp } from 'qiankun';
import { createLifecycleQueue, type Theme } from '@assembleai/app-sdk';

const theme = ref<Theme>('light');
try { if (localStorage.getItem('assembleai.theme') === 'dark') theme.value = 'dark'; } catch { /* Storage is optional. */ }
const active = ref(location.pathname.startsWith('/workspace/sample'));
const loading = ref(false);
const failure = ref('');
const search = ref('');
const host = ref<HTMLElement>();
let microApp: MicroApp | undefined;
let disposed = false;
let revision = 0;
const lifecycle = createLifecycleQueue();
const visible = computed(() => '接入验证应用 sample'.includes(search.value.trim().toLowerCase()));
const context = () => ({ appId: 'sample', releaseVersion: '0.1.0', theme: theme.value, mode: 'embedded' as const });

async function mountSample() {
  if (microApp || disposed) return;
  failure.value = '';
  try {
    await nextTick();
    if (!host.value || disposed) return;
    microApp = loadMicroApp({
      name: 'assembleai-sample', entry: '/apps/sample/0.1.0/', container: host.value,
      props: { context: context() },
    }, { sandbox: { experimentalStyleIsolation: true }, singular: false });
    await microApp.mountPromise;
  } catch {
    failure.value = '应用加载失败。请检查样例构建产物，然后重试。';
    const failed = microApp;
    microApp = undefined;
    try { await failed?.unmount(); } catch { /* A parcel that never mounted may reject unmount. */ }
    host.value?.replaceChildren();
  }
}

function retrySample() {
  // qiankun and its HTML loader cache rejected promises by parcel and resource URL.
  // A fresh document clears both layers while retaining the current route and theme.
  window.location.reload();
}

async function leaveSample() {
  if (loading.value) return;
  history.pushState({}, '', '/');
  await syncRoute();
}

async function openSample() {
  history.pushState({}, '', '/workspace/sample');
  await syncRoute();
}
async function syncRoute() {
  const request = ++revision;
  const showSample = location.pathname.startsWith('/workspace/sample');
  loading.value = true;
  try {
    await lifecycle.run(async () => {
      if (disposed || request !== revision) return;
      if (showSample) {
        active.value = true;
        await mountSample();
      } else {
        if (microApp) await microApp.unmount();
        microApp = undefined;
        active.value = false;
        failure.value = '';
      }
    });
  } catch { failure.value = '应用切换失败，请刷新页面重试。'; }
  finally { if (request === revision) loading.value = false; }
}
async function toggleTheme() {
  theme.value = theme.value === 'light' ? 'dark' : 'light';
  document.documentElement.dataset.theme = theme.value;
  try { localStorage.setItem('assembleai.theme', theme.value); } catch { /* optional */ }
  try {
    await lifecycle.run(async () => { if (microApp?.update) await microApp.update({ context: context() }); });
  } catch { failure.value = '应用外观更新失败，请重新打开应用。'; }
}
onMounted(() => {
  document.documentElement.dataset.theme = theme.value;
  window.addEventListener('popstate', syncRoute);
  if (active.value) void syncRoute();
});
onBeforeUnmount(() => {
  disposed = true;
  window.removeEventListener('popstate', syncRoute);
  void lifecycle.run(async () => { if (microApp) await microApp.unmount(); microApp = undefined; }).catch(() => undefined);
});
</script>

<template>
  <div class="shell">
    <aside class="sidebar" aria-label="平台导航">
      <a class="brand" href="/" @click.prevent="leaveSample"><span class="brand-mark" aria-hidden="true">集</span><strong>集智</strong><span class="brand-en">AssembleAI</span></a>
      <div class="workspace-label">研发工作区</div>
      <button class="nav-item" :aria-current="!active ? 'page' : undefined" @click="leaveSample" :disabled="loading"><span aria-hidden="true">▦</span> 应用接入</button>
      <div class="sidebar-bottom"><span class="environment-dot" aria-hidden="true"></span>本地开发环境<span class="version">v0.1</span></div>
    </aside>
    <div class="content">
      <header class="topbar"><div>工作区 <span aria-hidden="true">/</span> <strong>{{ active ? '接入验证应用' : '应用接入' }}</strong></div><el-button text @click="toggleTheme" :disabled="loading" :aria-label="theme === 'light' ? '切换暗色' : '切换浅色'">{{ theme === 'light' ? '暗色外观' : '浅色外观' }}</el-button></header>
      <main>
        <template v-if="!active">
          <div class="page-heading"><div><h1>应用接入</h1><p>验证独立应用在集智工作区中的运行体验。</p></div><span class="stage-label">工程验证</span></div>
          <div class="notice">当前为开发验证环境。身份认证、应用注册和业务数据将在后续任务中接入。</div>
          <div class="toolbar"><h2>验证应用 <span>1</span></h2><el-input v-model="search" placeholder="搜索应用" aria-label="搜索应用" clearable class="search-input" /></div>
          <div class="app-list" v-if="visible">
            <div class="app-row"><div class="app-symbol" aria-hidden="true">S</div><div class="app-info"><h3>接入验证应用</h3><p>独立运行 · 平台内加载 · 生命周期验证</p></div><span class="release">0.1.0</span><el-button type="primary" @click="openSample">打开应用</el-button></div>
          </div>
          <div v-else class="empty-state"><h3>没有匹配的应用</h3><p>试试“接入”或“sample”。</p><el-button @click="search = ''">清除搜索</el-button></div>
          <p class="footnote">此样例用于验证应用接入，不代表 ChatBI 或知识库已交付。</p>
        </template>
        <template v-else>
          <div class="page-heading"><div><h1>接入验证应用</h1><p>同一份前端发布物，在两种运行模式下使用。</p></div><div class="actions"><a class="standalone-link" href="/apps/sample/0.1.0/" target="_blank" rel="noopener">独立打开 ↗</a><el-button @click="leaveSample" :disabled="loading">返回应用列表</el-button></div></div>
          <div v-if="failure" class="error-state" role="alert"><p>{{ failure }}</p><el-button @click="retrySample" :disabled="loading">重新加载页面</el-button></div>
          <div v-if="loading" class="loading-state" role="status">正在切换应用…</div>
          <section ref="host" id="micro-app-host" aria-label="应用运行区" :aria-busy="loading" />
        </template>
      </main>
    </div>
  </div>
</template>
