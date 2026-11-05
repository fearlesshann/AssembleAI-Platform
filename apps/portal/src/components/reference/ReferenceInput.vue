<script setup lang="ts" generic="T extends string | number = string">
import './reference-controls.css';
const props = withDefaults(defineProps<{ modelValue?: T; type?: string }>(), { type: 'text' });
const emit = defineEmits<{ 'update:modelValue': [value: T]; input: [event: Event]; change: [event: Event] }>();
function input(event: Event) { const target = event.target as HTMLInputElement; emit('update:modelValue', (props.type === 'number' && target.value !== '' ? target.valueAsNumber : target.value) as T); emit('input', event); }
</script>
<template><input class="reference-control ui-input" :type="type" :value="modelValue" @input="input" @change="emit('change', $event)" /></template>
