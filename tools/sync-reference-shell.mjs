import { readFile, writeFile } from 'node:fs/promises';
import postcss from 'postcss';

// Keep source declarations intact. Only scope selectors to the real portal;
// identity-provider login assets are generated separately from the same snapshot.
const source = await readFile(new URL('../references/ui-v1/app.css', import.meta.url), 'utf8');
const start = source.indexOf('/* —— Login screen');
const end = source.indexOf('/* —— Sidebar user menu');
if (start < 0 || end < start) throw new Error('Reference source anchors changed.');
const css = postcss.parse(source.slice(0, start) + source.slice(end));
css.walkAtRules('font-face', rule => rule.remove());
css.walkRules(rule => {
  if (rule.parent.type === 'atrule' && /keyframes$/.test(rule.parent.name)) return;
  const selectors = rule.selectors.filter(selector => selector !== ':root' && !/^html\[data-theme="(?:light|dark)"\]$/.test(selector));
  if (!selectors.length) { rule.remove(); return; }
  rule.selectors = selectors.flatMap(selector => {
    // The source universal rule also applies to the scope element itself.
    // Omitting it makes standalone application padding enlarge a 100% root.
    if (/^\*(?:::(?:before|after))?$/.test(selector)) return [`:where(.reference-ui)${selector.slice(1)}`, `:where(.reference-ui) ${selector}`];
    if (selector === 'html' || selector === 'body') return ':where(.reference-ui)';
    const html = /^(html(?:\[[^\]]+\]|\.[\w-]+)*)\s+(.+)$/.exec(selector);
    return html ? `${html[1]} :where(.reference-ui) ${html[2]}` : `:where(.reference-ui) ${selector}`;
  });
});
await writeFile(new URL('../apps/portal/src/reference-source.css', import.meta.url),
  '/* Generated from references/ui-v1/app.css by tools/sync-reference-shell.mjs. Do not restyle source components here. */\n' + css.toString());
