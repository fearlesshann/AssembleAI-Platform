import { createLifecycleQueue } from '@assembleai/app-sdk';

// One queue across component instances. A rejected transition can be retried,
// but a lifecycle that never settles cannot safely release the next mount.
const lifecycle = createLifecycleQueue();
let restartRequired = false;
export const portalRuntimeNeedsRestart = () => restartRequired;
export function restartPortalRuntime(reason: 'timeout' | 'cleanup') {
  if (restartRequired) return;
  restartRequired = true;
  const match = /^\/applications\/([a-z][a-z0-9-]{0,63})(?:\/run)?\/?$/.exec(location.pathname);
  location.replace(`${match ? `/applications/${match[1]}` : '/'}?runError=${reason}`);
}
export const portalRuntimeLifecycle = {
  run(transition: () => Promise<void>) {
    return lifecycle.run(async () => {
      if (restartRequired) return;
      let timer: number | undefined;
      try {
        await Promise.race([
          transition(),
          new Promise<never>((_, reject) => {
            timer = window.setTimeout(() => {
              // Stop this document before any new parcel can mount. A promise
              // timeout alone cannot cancel arbitrary JavaScript in an app.
              restartPortalRuntime('timeout');
              reject(new Error('Application lifecycle requires a new document.'));
            }, 15_000);
          }),
        ]);
      } finally { window.clearTimeout(timer); }
    });
  },
};
