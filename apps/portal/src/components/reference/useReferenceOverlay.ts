import { onBeforeUnmount } from 'vue';
import { referenceMotionOn } from './reference-motion';

/** app.js initOverlays, adapted to Vue leave hooks and native modal focus. */
export function useReferenceOverlay() {
  const timers = new Map<Element, ReturnType<typeof setTimeout>>();
  function clear(element: Element) { clearTimeout(timers.get(element)); timers.delete(element); }
  function enter(element: Element) {
    clear(element);
    const dialog = element as HTMLDialogElement;
    dialog.classList.remove('is-closing', 'is-open');
    dialog.showModal();
    void dialog.offsetWidth;
    dialog.classList.add('is-open');
    timers.set(element, setTimeout(() => {
      dialog.querySelector<HTMLElement>('.subwindow-body input:not(:disabled), .subwindow-body textarea:not(:disabled), .subwindow-body button:not(:disabled), .dialog-actions button:not(:disabled)')?.focus({ preventScroll: true });
      timers.delete(element);
    }, 20));
  }
  function leave(element: Element, done: () => void) {
    clear(element);
    element.classList.remove('is-open'); element.classList.add('is-closing');
    timers.set(element, setTimeout(() => {
      timers.delete(element);
      (element as HTMLDialogElement).close();
      element.classList.remove('is-closing');
      done();
    }, referenceMotionOn() ? 220 : 0));
  }
  onBeforeUnmount(() => { for (const [element, timer] of timers) { clearTimeout(timer); (element as HTMLDialogElement).close(); } timers.clear(); });
  return { enter, leave };
}
