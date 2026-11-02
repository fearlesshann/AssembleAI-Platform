<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import type { PlatformContext } from '@assembleai/app-sdk';
defineProps<{ context: PlatformContext }>();
const count = ref(0);
const signals = ref(0);
const onSignal = () => { signals.value += 1; };
onMounted(() => window.addEventListener('assembleai:sample-ping', onSignal));
onBeforeUnmount(() => window.removeEventListener('assembleai:sample-ping', onSignal));
</script>

<template>
  <article class="sample-app" :data-theme="context.theme" data-testid="sample-app">
    <header class="sample-header"><span class="sample-icon" aria-hidden="true">S</span><div><h2>应用已就绪</h2><p>这是用于验证接入能力的本地样例，不连接业务数据。</p></div><span class="sample-mode">{{ context.mode === 'embedded' ? '平台内运行' : '独立运行' }}</span></header>
    <dl class="sample-details"><div><dt>应用</dt><dd>{{ context.appId }}</dd></div><div><dt>发布版本</dt><dd>{{ context.releaseVersion }}</dd></div><div><dt>外观</dt><dd>{{ context.theme === 'dark' ? '暗色' : '浅色' }}</dd></div></dl>
    <section class="sample-interaction"><h3>试一下交互</h3><p>退出应用再重新进入，计数应从零开始。</p><div class="sample-controls"><button @click="count++">增加计数</button><output aria-label="当前计数" data-testid="sample-count">{{ count }}</output></div></section>
    <footer>生命周期事件：<output data-testid="sample-signals">{{ signals }}</output><span>仅用于接入验证</span></footer>
  </article>
</template>
