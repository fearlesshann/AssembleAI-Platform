<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue';
import ReferenceButton from './ReferenceButton.vue';
import './reference-drawer.css';
const props = defineProps<{ open: boolean; title: string; kicker?: string; disabled?: boolean }>();
const emit = defineEmits<{ close: [] }>();
const dialog = ref<HTMLDialogElement>();
function close() { if (!props.disabled) emit('close'); }
function keydown(event: KeyboardEvent) {
  // A nested source select/date control owns its first Escape. Preserve the draft drawer.
  if (event.key === 'Escape' && dialog.value?.querySelector('[data-reference-control].is-open')) event.preventDefault();
}
watch(() => [props.open, props.title] as const, async ([open]) => {
  await nextTick();
  if (open) {
    if (!dialog.value?.open) dialog.value?.showModal();
    // The source overlay initially focuses its first editable/body action.
    dialog.value?.querySelector<HTMLElement>('.subwindow-body input:not(:disabled), .subwindow-body textarea:not(:disabled), .subwindow-body button:not(:disabled)')?.focus({ preventScroll: true });
  } else if (dialog.value?.open) dialog.value.close();
}, { immediate: true });
onBeforeUnmount(() => dialog.value?.close());
</script>
<template>
  <dialog ref="dialog" class="overlay is-open reference-drawer-overlay" :aria-label="title" @cancel.prevent="close" @keydown.capture="keydown">
    <div class="overlay-backdrop" @click="close"></div>
    <aside class="drawer subwindow">
      <header class="drawer-head"><div class="subwindow-identity"><span class="avatar">{{ title.replace(/^(编辑|新增|注册|建立)/, '').slice(0, 1) }}</span><div><div v-if="kicker" class="drawer-kicker">{{ kicker }}</div><h2 class="drawer-title">{{ title }}</h2></div></div><ReferenceButton variant="icon" :disabled="disabled" aria-label="关闭" data-close @click="close"><svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M4 4l8 8M12 4L4 12" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/></svg></ReferenceButton></header>
      <div class="subwindow-body"><slot /></div>
      <footer v-if="$slots.footer" class="subwindow-foot"><slot name="footer" /></footer>
    </aside>
  </dialog>
</template>
