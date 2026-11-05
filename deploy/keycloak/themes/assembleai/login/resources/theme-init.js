// Run before first paint; no authentication state is stored in browser storage.
(() => {
  let theme;
  try { theme = localStorage.getItem('assembleai.theme'); } catch { /* optional preference */ }
  if (theme !== 'light' && theme !== 'dark') theme = window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
  document.documentElement.dataset.theme = theme;
  // Match the supplied example: full by default, explicit system/reduced modes.
  const mode = new URLSearchParams(location.search).get('motion') || 'full';
  const media = window.matchMedia('(prefers-reduced-motion: reduce)');
  const applyMotion = () => { document.documentElement.dataset.motion = mode === 'reduced' || (mode === 'system' && media.matches) ? 'reduced' : 'full'; };
  applyMotion();
  media.addEventListener('change', applyMotion);
})();
