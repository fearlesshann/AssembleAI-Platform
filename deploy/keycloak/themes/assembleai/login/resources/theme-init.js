// Run before first paint; no authentication state is stored in browser storage.
(() => {
  let theme;
  try { theme = localStorage.getItem('assembleai.theme'); } catch { /* optional preference */ }
  if (theme !== 'light' && theme !== 'dark') theme = window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
  document.documentElement.dataset.theme = theme;
  document.documentElement.dataset.motion = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'reduced' : 'system';
})();
