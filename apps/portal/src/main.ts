import { createApp } from 'vue';

// The template owns its styles and document-level interactions. Keep the
// engineering shell in a separate document so neither surface leaks styles.
if (location.pathname.startsWith('/workspace/')) {
  const [{ default: App }, { default: ElementPlus }] = await Promise.all([
    import('./App.vue'),
    import('element-plus'),
    import('element-plus/dist/index.css'),
    import('./theme.css'),
  ]);
  document.title = '集智 · 应用接入';
  createApp(App).use(ElementPlus).mount('#portal-root');
} else {
  const { default: TemplatePortal } = await import('./TemplatePortal');
  createApp(TemplatePortal).mount('#portal-root');
}
