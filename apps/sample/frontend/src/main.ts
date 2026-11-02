import { createApp, reactive, type App } from 'vue';
import { SAMPLE_CONTEXT, type AppProps, type PlatformContext } from '@assembleai/app-sdk';
import Sample from './Sample.vue';
import './sample.css';

declare global { interface Window { __POWERED_BY_QIANKUN__?: boolean } }
let instance: App | undefined;
let current: PlatformContext | undefined;

export async function bootstrap() { /* No global side effects. */ }
export async function mount(props: AppProps = {}) {
  if (instance) throw new Error('Sample is already mounted');
  const root = (props.container ?? document).querySelector('#sample-root');
  if (!root) throw new Error('Sample mount container not found');
  current = reactive({ ...SAMPLE_CONTEXT, ...props.context });
  const candidate = createApp(Sample, { context: current });
  try {
    candidate.mount(root);
    instance = candidate;
  } catch (error) {
    try { candidate.unmount(); }
    finally { root.replaceChildren(); current = undefined; instance = undefined; }
    throw error;
  }
}
export async function unmount() {
  instance?.unmount();
  instance = undefined;
  current = undefined;
}
export async function update(props: AppProps) {
  if (current && props.context) Object.assign(current, props.context);
}
if (!window.__POWERED_BY_QIANKUN__) void mount();
