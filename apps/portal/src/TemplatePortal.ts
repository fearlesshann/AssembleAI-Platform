import { defineComponent, h, onMounted, ref } from 'vue';
import templateDocument from '../../../references/ui-v1/index.html?raw';
import wheelScript from '../../../references/ui-v1/inertialWheelScroll.js?url';
import templateScript from '../../../references/ui-v1/app.js?url';
import '../../../references/ui-v1/app.css';

// This is trusted, vendored markup, never HTML supplied by an API or a user.
// Keep the reference canonical: its native DOM, CSS and scripts provide the
// requested exact appearance while Vue remains the portal entry point.
const markup = templateDocument.match(/<body>([\s\S]*?)<\/body>/i)![1]
  .replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '');

function loadClassicScript(src: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = src;
    script.async = false;
    script.onload = () => resolve();
    script.onerror = () => reject(new Error('Template script failed to load'));
    document.body.append(script);
  });
}

// Mount once per document. This plain TS component deliberately has no SFC
// hot-reload boundary: edits reload the document and its global listeners.
export default defineComponent({
  name: 'TemplatePortal',
  setup() {
    const failed = ref(false);
    onMounted(async () => {
      try {
        await loadClassicScript(wheelScript);
        await loadClassicScript(templateScript);
      } catch {
        failed.value = true;
      }
    });
    return () => [
      h('div', { style: { display: 'contents' }, innerHTML: markup }),
      failed.value ? h('div', {
        role: 'alert',
        style: { position: 'fixed', inset: '16px 16px auto', zIndex: 10000 },
      }, '页面加载失败，请刷新重试。') : null,
    ];
  },
});
