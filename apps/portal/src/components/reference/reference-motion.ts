import type { ObjectDirective } from 'vue';
import { installGlobalInertialWheelScroll } from './reference-wheel';

/** Source app.js: full by default; ?motion=system/reduced are explicit opt-outs. */
export function referenceMotionOn() { return document.documentElement.dataset.motion !== 'reduced'; }

const runtimeKey = Symbol.for('assembleai.referenceMotion');
type Runtime = { roots: Set<HTMLElement>; stop(): void; destroy(): void };
type MotionDocument = Document & { [runtimeKey]?: Runtime };

function acquireMotion(root: HTMLElement) {
  const doc = root.ownerDocument as MotionDocument;
  let runtime = doc[runtimeKey];
  if (!runtime) {
    const view = doc.defaultView!;
    const media = view.matchMedia('(prefers-reduced-motion: reduce)');
    const mode = new URLSearchParams(view.location.search).get('motion') || doc.documentElement.dataset.motion || 'full';
    const applyPreference = () => { doc.documentElement.dataset.motion = mode === 'reduced' || (mode === 'system' && media.matches) ? 'reduced' : 'full'; };
    applyPreference();
    const roots = new Set<HTMLElement>();
    const listeners = new Map<string, EventListener>();
    // A document-lifetime listener must not be removed by a qiankun parcel's
    // sandbox when another mounted surface still owns it. Keep the reference
    // algorithm intact, and scope its event source to registered product roots.
    const facade = {
      get documentElement() { return doc.documentElement; },
      get scrollingElement() { return doc.scrollingElement; },
      defaultView: view,
      addEventListener(type: string, listener: EventListener, options: AddEventListenerOptions | boolean) {
        const wrapped: EventListener = event => {
          if (type !== 'wheel' || [...roots].some(element => event.target instanceof Node && element.contains(event.target))) listener(event);
        };
        listeners.set(type, wrapped);
        EventTarget.prototype.addEventListener.call(doc, type, wrapped, options);
      },
      removeEventListener(type: string, listener: EventListener, options: EventListenerOptions | boolean) {
        const wrapped = listeners.get(type);
        if (wrapped) EventTarget.prototype.removeEventListener.call(doc, type, wrapped, options);
      },
    } as unknown as Document;
    const controller = installGlobalInertialWheelScroll(facade, {
      shouldAnimate: referenceMotionOn,
      requestFrame: view.requestAnimationFrame.bind(view), cancelFrame: view.cancelAnimationFrame.bind(view),
    });
    const observer = new MutationObserver(() => { if (!referenceMotionOn()) controller.stop(); });
    observer.observe(doc.documentElement, { attributes: true, attributeFilter: ['data-motion'] });
    EventTarget.prototype.addEventListener.call(media, 'change', applyPreference);
    runtime = { roots, stop: controller.stop, destroy() {
      controller.destroy(); observer.disconnect();
      EventTarget.prototype.removeEventListener.call(media, 'change', applyPreference);
      delete doc[runtimeKey];
    } };
    doc[runtimeKey] = runtime;
  }
  runtime.roots.add(root);
  return () => {
    runtime!.stop(); runtime!.roots.delete(root);
    if (!runtime!.roots.size) runtime!.destroy();
  };
}

const disposers = new WeakMap<HTMLElement, () => void>();
export const vReferenceSurface: ObjectDirective<HTMLElement> = {
  mounted(element) { disposers.set(element, acquireMotion(element)); },
  beforeUnmount(element) { disposers.get(element)?.(); disposers.delete(element); },
};

function replay(element: HTMLElement, name: string) {
  element.classList.remove(name);
  if (!referenceMotionOn()) return;
  void element.offsetWidth;
  element.classList.add(name);
}
// Original view-enter and content-in keyframes/timings are in reference-source.css.
export const vReferenceView: ObjectDirective<HTMLElement, string> = {
  mounted(element) { replay(element, 'is-enter'); },
  updated(element, binding) { if (binding.value !== binding.oldValue) replay(element, 'is-enter'); },
};
export const vReferenceContent: ObjectDirective<HTMLElement, boolean> = {
  mounted(element, binding) { if (!binding.value) replay(element, 'content-fade-in'); },
  updated(element, binding) { if (binding.oldValue && !binding.value) replay(element, 'content-fade-in'); },
};

function userMenu(element: HTMLElement, open: boolean) {
  const panel = element.querySelector<HTMLElement>('.user-menu-pop');
  if (!panel) return;
  element.classList.remove('is-open');
  panel.hidden = !open;
  if (open) { void panel.offsetWidth; element.classList.add('is-open'); }
}
export const vReferenceMenu: ObjectDirective<HTMLElement, boolean> = {
  mounted(element, binding) { userMenu(element, binding.value); },
  updated(element, binding) { if (binding.value !== binding.oldValue) userMenu(element, binding.value); },
};
