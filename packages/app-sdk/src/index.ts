/** Display context only. Backend authorization must verify every request. */
export { createLifecycleQueue } from './lifecycle-queue';
export { createApplicationClient, ApplicationApiError } from './application-api';
export type { ApplicationApi, ApplicationBinding, ApplicationClient, ApplicationRequest, ApplicationEvent, ApplicationErrorCode } from './application-api';
import type { ApplicationApi } from './application-api';
export type Theme = 'light' | 'dark';
export interface PlatformContext {
  appId: string;
  releaseVersion: string;
  theme: Theme;
  mode: 'embedded' | 'standalone';
}
export interface AppProps {
  container?: Element;
  context?: PlatformContext;
  api?: ApplicationApi;
}
export const SAMPLE_CONTEXT: PlatformContext = {
  appId: 'sample', releaseVersion: '0.1.0', theme: 'light', mode: 'standalone',
};
