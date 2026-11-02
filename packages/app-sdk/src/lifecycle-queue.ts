/** Serialize mount/unmount work; a rejected transition must not poison retries. */
export function createLifecycleQueue() {
  let pending: Promise<void> = Promise.resolve();
  return {
    run(transition: () => Promise<void>): Promise<void> {
      const result = pending.then(transition);
      pending = result.catch(() => undefined);
      return result;
    },
  };
}
