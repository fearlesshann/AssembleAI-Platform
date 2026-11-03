<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue';
import ElButton from 'element-plus/es/components/button/index';
import type { RegistryManifest } from './registry-api';

const props = defineProps<{ initial: RegistryManifest; mode: 'create' | 'version' | 'edit'; busy: boolean; stale: boolean }>();
const emit = defineEmits<{ save: [manifest: RegistryManifest]; cancel: []; dirty: [value: boolean] }>();
const form = reactive({
  appId: props.initial.appId, name: props.initial.name, version: props.initial.version, category: props.initial.category,
  description: props.initial.description, frontendVersion: props.initial.frontendArtifact.version,
  sha256: props.initial.frontendArtifact.sha256, backendService: props.initial.backendService,
  backendVersion: props.initial.backendVersion, permissions: props.initial.permissions.join('\n'),
});
const original = JSON.stringify(form);
const error = ref('');
const entry = computed(() => `/apps/${form.appId || '应用标识'}/${form.frontendVersion || '前端版本'}/`);
watch(form, () => { emit('dirty', JSON.stringify(form) !== original); error.value = ''; }, { deep: true });
const slug = /^[a-z][a-z0-9-]{0,63}$/;
const version = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
function save() {
  if (props.busy || props.stale) return;
  const permissions = form.permissions.split(/[,，\n]/).map(value => value.trim()).filter(Boolean);
  if (!slug.test(form.appId) || !slug.test(form.backendService)) { error.value = '应用标识和后端服务使用小写字母开头，可包含数字和短横线，最长 64 位。'; return; }
  if ([form.version, form.frontendVersion, form.backendVersion].some(value => value.length > 64 || !version.test(value))) { error.value = '三个版本均需使用 1.0.0 这样的三段数字，不能有前导零。'; return; }
  if (!/^[a-f0-9]{64}$/.test(form.sha256)) { error.value = '发布物 SHA256 需要填写真实构建结果的 64 位小写摘要。'; return; }
  if (!permissions.length || permissions.length > 32 || new Set(permissions).size !== permissions.length || permissions.some(value => !/^[a-z][a-z0-9:._-]{0,127}$/.test(value))) { error.value = '填写 1–32 个不重复入口权限，例如 sample:use。'; return; }
  if (!form.name.trim() || !form.category.trim() || [form.name, form.category, form.description].some(value => /[\u0000-\u001f\u007f-\u009f]/.test(value))) { error.value = '应用名称、分类不能为空，文字字段不能包含控制字符。'; return; }
  emit('save', {
    schemaVersion: 1, appId: form.appId, name: form.name.trim(), version: form.version,
    category: form.category.trim(), description: form.description.trim(),
    frontendEntry: entry.value, frontendArtifact: { version: form.frontendVersion, sha256: form.sha256 },
    backendService: form.backendService, backendVersion: form.backendVersion, permissions, status: 'draft',
  });
}
</script>

<template>
  <form class="registry-form" aria-label="应用草稿" @submit.prevent="save">
    <div class="registry-section-heading"><div><h2>{{ mode === 'create' ? '注册应用' : mode === 'version' ? '新建版本' : '编辑草稿' }}</h2><p>草稿保存后提交审核，由另一位审核人发布。</p></div><ElButton :disabled="busy" @click="emit('cancel')">取消编辑</ElButton></div>
    <p v-if="stale" class="registry-error" role="alert">服务端内容或权限已变化。此草稿保留在当前页面，请取消编辑并重新读取后再保存。</p>
    <p v-if="error" class="registry-error" role="alert">{{ error }}</p>
    <fieldset :disabled="busy"><legend>基本信息</legend><div class="registry-field-grid">
      <label>应用标识<input v-model="form.appId" aria-label="应用标识" required maxlength="64" :readonly="mode !== 'create'" placeholder="production-reports" autocomplete="off" spellcheck="false" /></label>
      <label>应用名称<input v-model="form.name" aria-label="应用名称" required maxlength="128" placeholder="生产报表" autocomplete="off" /></label>
      <label>分类<input v-model="form.category" aria-label="分类" required maxlength="64" placeholder="生产运营" autocomplete="off" /></label>
      <label>发布版本<input v-model="form.version" aria-label="发布版本" required maxlength="64" :readonly="mode === 'edit'" placeholder="1.0.0" autocomplete="off" spellcheck="false" /></label>
      <label class="registry-field-wide">说明<input v-model="form.description" aria-label="说明" maxlength="2000" placeholder="向用户说明应用用途" autocomplete="off" /></label>
    </div></fieldset>
    <fieldset :disabled="busy"><legend>发布物与服务</legend><p class="registry-help">使用已部署发布物的版本和摘要。服务地址由部署配置管理。</p><div class="registry-field-grid">
      <label>前端版本<input v-model="form.frontendVersion" aria-label="前端版本" required maxlength="64" placeholder="1.0.0" autocomplete="off" spellcheck="false" /></label>
      <label>后端版本<input v-model="form.backendVersion" aria-label="后端版本" required maxlength="64" placeholder="1.0.0" autocomplete="off" spellcheck="false" /></label>
      <label class="registry-field-wide">发布物 SHA256<input v-model="form.sha256" aria-label="发布物 SHA256" required maxlength="64" autocomplete="off" spellcheck="false" /></label>
      <label class="registry-field-wide">后端服务<input v-model="form.backendService" aria-label="后端服务" required maxlength="64" placeholder="production-api" autocomplete="off" spellcheck="false" /></label>
      <label class="registry-field-wide">入口权限<textarea v-model="form.permissions" aria-label="入口权限" required rows="3" maxlength="4200" placeholder="sample:use" spellcheck="false" /><span class="registry-help">每行一个，或用逗号分隔；应用运行需具备全部入口权限。</span></label>
    </div><p class="registry-help registry-entry">前端入口 <code>{{ entry }}</code></p></fieldset>
    <div class="registry-form-footer"><span>保存为草稿，不会立即对租户开放。</span><ElButton native-type="submit" type="primary" :disabled="busy || stale">{{ busy ? '正在保存…' : '保存草稿' }}</ElButton></div>
  </form>
</template>
