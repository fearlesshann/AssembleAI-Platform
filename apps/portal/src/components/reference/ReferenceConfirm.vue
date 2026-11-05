<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, useId, watch } from 'vue';
import ReferenceButton from './ReferenceButton.vue';
import type { ReferenceConfirmation } from './useReferenceConfirm';
import './reference-confirm.css';
const props = defineProps<{ request: ReferenceConfirmation | null; disabled?: boolean }>();
const emit = defineEmits<{ resolve: [accepted: boolean] }>();
const dialog = ref<HTMLDialogElement>();
const titleId = useId();
function resolve(accepted: boolean) { if (!props.disabled) emit('resolve', accepted); }
watch(() => props.request, async request => {
  await nextTick();
  if (request && !dialog.value?.open) dialog.value?.showModal();
  else if (!request && dialog.value?.open) dialog.value.close();
}, { immediate: true });
onBeforeUnmount(() => dialog.value?.close());
</script>
<template>
  <dialog ref="dialog" class="overlay is-open is-dialog reference-confirm-overlay" :aria-labelledby="titleId" @cancel.prevent="resolve(false)">
    <template v-if="request">
      <div class="overlay-backdrop" @click="resolve(false)"></div>
      <div class="dialog">
        <h2 :id="titleId" class="dialog-title">{{ request.title }}</h2>
        <p class="dialog-desc">{{ request.description }}</p>
        <div class="dialog-actions"><ReferenceButton :disabled="disabled" @click="resolve(false)">{{ request.cancelLabel ?? '取消' }}</ReferenceButton><ReferenceButton primary :disabled="disabled" @click="resolve(true)">{{ request.confirmLabel ?? '确认' }}</ReferenceButton></div>
      </div>
    </template>
  </dialog>
</template>
