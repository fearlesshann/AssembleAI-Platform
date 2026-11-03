import { createApp, h, reactive, shallowReactive, type App } from 'vue';
import { SAMPLE_CONTEXT, type AppProps, type PlatformContext } from '@assembleai/app-sdk';
import Sample from './Sample.vue';
import './sample.css';
import { BUILD_APP_ID, BUILD_FRONTEND_VERSION } from './build-context';

declare global { interface Window { __POWERED_BY_QIANKUN__?: boolean } }
let instance: App | undefined;
let current: PlatformContext | undefined;
let state: { context: PlatformContext; api?: AppProps['api'] } | undefined;

export async function bootstrap() { /* No global side effects. */ }
export async function mount(props: AppProps = {}) {
  if (instance) throw new Error('Sample is already mounted');
  const root = (props.container ?? document).querySelector('#sample-root');
  if (!root) throw new Error('Sample mount container not found');
  current = reactive({ ...SAMPLE_CONTEXT, appId: BUILD_APP_ID, releaseVersion: BUILD_FRONTEND_VERSION, ...props.context });
  const live = shallowReactive({ context: current, api: props.api });
  state = live;
  const candidate = createApp({ render: () => h(Sample, live) });
  try {
    candidate.mount(root);
    instance = candidate;
  } catch (error) {
    try { candidate.unmount(); }
    finally { root.replaceChildren(); current = undefined; instance = undefined; state = undefined; }
    throw error;
  }
}
export async function unmount() {
  instance?.unmount();
  instance = undefined;
  current = undefined;
  state = undefined;
}
export async function update(props: AppProps) {
  if (current && props.context) Object.assign(current, props.context);
  if (state && 'api' in props) state.api = props.api;
}
if (!window.__POWERED_BY_QIANKUN__) void mount();
