<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useAttrs, watch } from 'vue';
import { closeReferenceControl, releaseReferenceControl, setReferenceSelect } from './reference-runtime.js';
import './reference-controls.css';
defineOptions({ inheritAttrs: false });
export interface ReferenceOption { value: string; label: string; disabled?: boolean }
const props = withDefaults(defineProps<{ modelValue: string; options: ReferenceOption[]; label: string; disabled?: boolean; size?: 'default' | 'small'; placement?: 'top' | 'bottom'; block?: boolean; placeholder?: string }>(), { size: 'default', placement: 'bottom', block: true, placeholder: '请选择' });
const emit = defineEmits<{ 'update:modelValue': [value: string]; change: [value: string] }>();
const attrs = useAttrs(); const root = ref<HTMLElement>();
const triggerAttrs = computed(() => Object.fromEntries(Object.entries(attrs).filter(([key]) => key !== 'class' && key !== 'style')));
async function sync() { await nextTick(); if (root.value) { setReferenceSelect(root.value, props.modelValue); if (props.disabled) closeReferenceControl(root.value); } }
function openWithKeyboard() { if (root.value && !root.value.classList.contains('is-open')) root.value.querySelector<HTMLButtonElement>('.ui-select__trigger')?.click(); }
function changed(event: Event) {
  const value = (event as CustomEvent<{ value: string }>).detail.value;
  if (props.disabled || !props.options.some(option => option.value === value && !option.disabled)) return;
  emit('update:modelValue', value); emit('change', value);
  // The source runtime writes its label immediately. A controlled parent may
  // reject the change (for example cancelling a dirty-draft confirmation).
  void sync();
}
onMounted(sync); watch(() => [props.modelValue, props.options, props.disabled], sync, { deep: true });
onBeforeUnmount(() => { if (root.value) releaseReferenceControl(root.value); });
</script>
<template>
  <div ref="root" class="reference-control ui-select" :class="[attrs.class, { 'ui-select--block': block, 'ui-select--sm': size === 'small' }]" :style="attrs.style as any" data-reference-control data-ui-select :data-value="modelValue" :data-placement="placement" @ui-change="changed">
    <button v-bind="triggerAttrs" type="button" class="ui-select__trigger" role="combobox" aria-haspopup="listbox" aria-expanded="false" :aria-label="label" :disabled="disabled" @keydown.down.prevent="openWithKeyboard" @keydown.up.prevent="openWithKeyboard">
      <span class="ui-select__value">{{ options.find(option => option.value === modelValue)?.label ?? (modelValue || placeholder) }}</span>
      <svg class="ui-select__chev" width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden="true"><path d="M3 4.5L6 7.5L9 4.5" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round" /></svg>
    </button>
    <div class="ui-select__menu" role="listbox" :aria-label="label" hidden>
      <button v-for="option in options" :key="option.value" type="button" class="ui-select__option" :class="{ 'is-selected': option.value === modelValue }" role="option" :data-value="option.value" :aria-selected="option.value === modelValue" :disabled="disabled || option.disabled">{{ option.label }}</button>
    </div>
  </div>
</template>
