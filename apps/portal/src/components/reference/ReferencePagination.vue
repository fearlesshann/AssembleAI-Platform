<script setup lang="ts">
import { computed } from 'vue';
import ReferenceSelect from './ReferenceSelect.vue';
import './reference-controls.css';
const props = withDefaults(defineProps<{ currentPage: number; pageSize: number; total?: number; hasNext?: boolean; disabled?: boolean; pageSizes?: number[] }>(), { pageSizes: () => [10,25,50] });
const emit = defineEmits<{ 'update:pageSize': [value: number]; change: [page: number] }>();
const pages = computed(() => props.total === undefined ? undefined : Math.max(1, Math.ceil(props.total / props.pageSize)));
const next = computed(() => pages.value === undefined ? !!props.hasNext : props.currentPage < pages.value);
const sizes = computed(() => [...new Set([...props.pageSizes, props.pageSize])].sort((a,b) => a-b).map(value => ({ value: String(value), label: String(value) })));
</script>
<template>
  <div class="reference-control data-grid__footer">
    <span class="data-grid__count"><slot name="count">{{ total === undefined ? '按时间排序' : `共 ${total} 条` }}</slot></span>
    <div class="data-grid__pager">
      <div class="data-grid__page-size"><span>每页</span><ReferenceSelect :model-value="String(pageSize)" :options="sizes" label="每页条数" size="small" placement="top" :block="false" :disabled="disabled || pageSizes.length < 2" @update:model-value="emit('update:pageSize', Number($event))" /></div>
      <button class="icon-btn" type="button" aria-label="上一页" :disabled="disabled || currentPage <= 1" @click="emit('change', currentPage - 1)"><svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden="true"><path d="M8.5 3L5 7l3.5 4" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round" /></svg></button>
      <span class="data-grid__page">{{ currentPage }} / {{ pages ?? (hasNext ? '…' : currentPage) }}</span>
      <button class="icon-btn" type="button" aria-label="下一页" :disabled="disabled || !next" @click="emit('change', currentPage + 1)"><svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden="true"><path d="M5.5 3L9 7l-3.5 4" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round" /></svg></button>
    </div>
  </div>
</template>
