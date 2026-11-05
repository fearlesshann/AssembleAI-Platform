<script setup lang="ts">
import './reference-controls.css';
withDefaults(defineProps<{ type?: 'button' | 'submit' | 'reset'; primary?: boolean; danger?: boolean; disabled?: boolean; variant?: 'pill' | 'icon' | 'link' }>(), { type: 'button', variant: 'pill' });
</script>
<template><button class="reference-control" :class="[`${variant}-btn`, { 'is-primary': primary, 'is-danger': danger }]" :type="type" :disabled="disabled"><slot /></button></template>
