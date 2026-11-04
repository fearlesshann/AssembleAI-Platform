import { createApp, h, reactive, shallowReactive, type App } from 'vue';
import type { AppProps, PlatformContext } from '@assembleai/app-sdk';
import Chatbi from './Chatbi.vue';
import './chatbi.css';
import { BUILD_APP_ID, BUILD_FRONTEND_VERSION } from './build-context';

declare global { interface Window { __POWERED_BY_QIANKUN__?: boolean } }
let instance: App | undefined;
let current: PlatformContext | undefined;
let state: { context: PlatformContext; api?: AppProps['api'] } | undefined;
export async function bootstrap() { /* Mount owns every resource. */ }
export async function mount(props: AppProps = {}) {
  if (instance) throw new Error('ChatBI is already mounted');
  const root = (props.container ?? document).querySelector('#chatbi-root');
  if (!root) throw new Error('ChatBI mount container not found');
  current = reactive({ appId: BUILD_APP_ID, releaseVersion: BUILD_FRONTEND_VERSION, theme: 'light', mode: 'standalone', ...props.context } as PlatformContext);
  const live = shallowReactive({ context: current, api: props.api }); state = live;
  const candidate = createApp({ render: () => h(Chatbi, live) });
  try { candidate.mount(root); instance = candidate; }
  catch (error) {
    try { candidate.unmount(); }
    finally { root.replaceChildren(); current = undefined; instance = undefined; state = undefined; }
    throw error;
  }
}
export async function unmount() { instance?.unmount(); instance = undefined; current = undefined; state = undefined; }
export async function update(props: AppProps) {
  if (current && props.context) Object.assign(current, props.context);
  if (state && 'api' in props) state.api = props.api;
}
if (!window.__POWERED_BY_QIANKUN__) void mount();
