export function installGlobalInertialWheelScroll(root: Document, options: {
  shouldAnimate: () => boolean;
  requestFrame?: typeof requestAnimationFrame;
  cancelFrame?: typeof cancelAnimationFrame;
}): { destroy(): void; stop(): void };
