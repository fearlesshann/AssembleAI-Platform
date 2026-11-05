<script setup lang="ts">
import { useId } from 'vue';
import { useReferenceOverlay } from './useReferenceOverlay';
import ReferenceButton from './ReferenceButton.vue';
import type { ReferenceConfirmation } from './useReferenceConfirm';
import './reference-confirm.css';
const props = defineProps<{ request: ReferenceConfirmation | null; disabled?: boolean }>();
const emit = defineEmits<{ resolve: [accepted: boolean] }>();
const { enter, leave } = useReferenceOverlay();
const titleId = useId();
function resolve(accepted: boolean) { if (props.request && !props.disabled) emit('resolve', accepted); }
</script>
<template>
  <Transition appear :css="false" @enter="enter" @leave="leave">
  <dialog v-if="request" class="overlay is-dialog reference-confirm-overlay" :aria-labelledby="titleId" @cancel.prevent="resolve(false)">
      <div class="overlay-backdrop" @click="resolve(false)"></div>
      <div class="dialog">
        <h2 :id="titleId" class="dialog-title">{{ request.title }}</h2>
        <p class="dialog-desc">{{ request.description }}</p>
        <div class="dialog-actions"><ReferenceButton :disabled="disabled" @click="resolve(false)">{{ request.cancelLabel ?? '取消' }}</ReferenceButton><ReferenceButton primary :disabled="disabled" @click="resolve(true)">{{ request.confirmLabel ?? '确认' }}</ReferenceButton></div>
      </div>
  </dialog>
  </Transition>
</template>
