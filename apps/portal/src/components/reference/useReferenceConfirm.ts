import { onBeforeUnmount, shallowRef } from 'vue';

export interface ReferenceConfirmation {
  title: string;
  description: string;
  confirmLabel?: string;
  cancelLabel?: string;
}

/** Keeps the source dialog declarative and resolves pending decisions on unmount. */
export function useReferenceConfirm() {
  const confirmation = shallowRef<ReferenceConfirmation | null>(null);
  let resolvePending: ((accepted: boolean) => void) | undefined;
  let disposed = false;
  function settleConfirmation(accepted: boolean) {
    const resolve = resolvePending;
    resolvePending = undefined;
    confirmation.value = null;
    resolve?.(accepted);
  }
  function requestConfirmation(request: ReferenceConfirmation): Promise<boolean> {
    settleConfirmation(false);
    if (disposed) return Promise.resolve(false);
    confirmation.value = request;
    return new Promise(resolve => { resolvePending = resolve; });
  }
  onBeforeUnmount(() => { disposed = true; settleConfirmation(false); });
  return { confirmation, requestConfirmation, settleConfirmation };
}
