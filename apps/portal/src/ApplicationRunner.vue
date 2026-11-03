<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { loadMicroApp, type MicroApp } from 'qiankun';
import ElButton from 'element-plus/es/components/button/index';
import type { PortalRun, Theme } from './portal-api';
import { portalRuntimeLifecycle, portalRuntimeNeedsRestart, restartPortalRuntime } from './portal-runtime';

const props = defineProps<{ run: PortalRun; theme: Theme }>();
const emit = defineEmits<{ leave: [] }>();
const host = ref<HTMLElement>();
const loading = ref(true);
const failure = ref('');
const resources = new AbortController();
let parcel: MicroApp | undefined;
let enteredMount = false;
let disposed = false;
let loadTimer: number | undefined;
let leaseTimer: number | undefined;
const context = () => ({ appId: props.run.appId, releaseVersion: props.run.releaseVersion, theme: props.theme, mode: 'embedded' as const });

async function cleanup() {
  if (portalRuntimeNeedsRestart()) return;
  const current = parcel;
  parcel = undefined;
  try { await current?.unmount(); }
  catch {
    // Once mounting began, failed unmount can skip qiankun sandbox cleanup.
    // Do not release another parcel into that document. A pure resource-load
    // failure before beforeMount never activated this application's sandbox.
    if (enteredMount) restartPortalRuntime('cleanup');
  }
  host.value?.replaceChildren();
}

onMounted(() => {
  void portalRuntimeLifecycle.run(async () => {
    if (disposed) return;
    const run = props.run;
    const artifact = run.manifest?.frontendArtifact;
    const appId = /^[a-z][a-z0-9-]{0,63}$/;
    const version = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
    const remaining = Date.parse(run.expiresAt) - Date.now();
    const entry = `/apps/${run.appId}/${artifact?.version}/`;
    if (!appId.test(run.appId) || !version.test(run.releaseVersion) || !artifact || !version.test(artifact.version)
      || run.manifest.appId !== run.appId || run.manifest.version !== run.releaseVersion
      || run.manifest.frontendEntry !== entry || !Number.isFinite(remaining) || remaining <= 0 || remaining > 3_601_000) {
      failure.value = '本次运行已失效，请返回应用详情重新打开。'; loading.value = false; return;
    }
    leaseTimer = window.setTimeout(() => {
      if (disposed) return;
      failure.value = '本次运行已到期，请返回应用详情重新打开。';
      resources.abort();
      void portalRuntimeLifecycle.run(cleanup);
    }, remaining);
    loadTimer = window.setTimeout(() => resources.abort(), 15_000);
    try {
      await nextTick();
      if (!host.value || disposed) return;
      parcel = loadMicroApp({
        name: `assembleai-${run.appId}-${run.runId}`, entry, container: host.value,
        props: { context: context() },
      }, {
        sandbox: { experimentalStyleIsolation: true }, singular: false,
        fetch: (input, init) => {
          const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url, location.origin);
          if (url.origin !== location.origin || !url.pathname.startsWith(entry)) return Promise.reject(new Error('Application resource is outside its release.'));
          return fetch(url, { ...init, credentials: 'same-origin', redirect: 'error', signal: resources.signal });
        },
      }, { beforeMount: async () => { enteredMount = true; } });
      void parcel.loadPromise.catch(() => undefined);
      void parcel.bootstrapPromise.catch(() => undefined);
      await parcel.mountPromise;
      if (disposed || resources.signal.aborted) await cleanup();
    } catch {
      if (!disposed && !failure.value) failure.value = '应用加载失败，请返回详情；也可以重新加载页面后再试。';
      await cleanup();
    } finally {
      window.clearTimeout(loadTimer);
      loading.value = false;
    }
  }).catch(() => { if (!disposed) { loading.value = false; failure.value = '应用切换失败，请重新加载页面后再试。'; } });
});

watch(() => props.theme, () => {
  void portalRuntimeLifecycle.run(async () => {
    if (!disposed && parcel?.update) await parcel.update({ context: context() });
  }).catch(() => { if (!disposed) failure.value = '应用外观更新失败，请返回详情重新打开。'; });
});

onBeforeUnmount(() => {
  disposed = true;
  resources.abort();
  window.clearTimeout(loadTimer);
  window.clearTimeout(leaseTimer);
  void portalRuntimeLifecycle.run(cleanup).catch(() => undefined);
});
</script>

<template>
  <section aria-label="应用运行区" :aria-busy="loading" class="application-runner">
    <div v-if="failure" class="portal-state" role="alert">
      <h2>暂时无法继续运行</h2><p>{{ failure }}</p>
      <ElButton @click="emit('leave')">返回应用详情</ElButton>
    </div>
    <p v-else-if="loading" role="status">正在打开应用…</p>
    <div ref="host" v-show="!failure" class="portal-runtime-host" />
  </section>
</template>
