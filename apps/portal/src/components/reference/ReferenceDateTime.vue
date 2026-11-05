<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useAttrs, watch } from 'vue';
import { closeReferenceControl, releaseReferenceControl, setReferenceDateTime } from './reference-runtime.js';
import './reference-controls.css';
defineOptions({ inheritAttrs: false });
const props = withDefaults(defineProps<{ modelValue: string; label: string; disabled?: boolean; block?: boolean }>(), { block: true });
const emit = defineEmits<{ 'update:modelValue': [value: string]; change: [value: string] }>();
const attrs = useAttrs(); const root = ref<HTMLElement>();
const triggerAttrs = computed(() => Object.fromEntries(Object.entries(attrs).filter(([key]) => key !== 'class' && key !== 'style')));
async function sync() { await nextTick(); if (root.value) { setReferenceDateTime(root.value, props.modelValue); if (props.disabled) closeReferenceControl(root.value); } }
function changed(event: Event) { if (props.disabled) return; const value = (event as CustomEvent<{ value: string | null }>).detail.value ?? ''; emit('update:modelValue', value); emit('change', value); void sync(); }
onMounted(sync); watch(() => [props.modelValue, props.disabled], sync);
onBeforeUnmount(() => { if (root.value) releaseReferenceControl(root.value); });
</script>
<template>
  <div ref="root" class="reference-control ui-datetime" :class="[attrs.class, { 'ui-datetime--block': block }]" :style="attrs.style as any" data-reference-control data-ui-datetime :data-value="modelValue" @ui-change="changed">
    <button v-bind="triggerAttrs" type="button" class="ui-datetime__trigger" aria-haspopup="dialog" aria-expanded="false" :aria-label="label" :disabled="disabled">
      <span class="ui-datetime__value">{{ modelValue ? modelValue.slice(0,16).replace('T',' ') : '选择日期时间' }}</span>
      <svg class="ui-datetime__icon" width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true"><rect x="2.5" y="3.5" width="11" height="10" rx="2" stroke="currentColor" stroke-width="1.2" /><path d="M5 2v2.5M11 2v2.5M2.5 7h11" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" /></svg>
    </button>
    <div class="ui-datetime__panel" role="dialog" :aria-label="`${label}：选择日期与时间`" hidden></div>
  </div>
</template>
