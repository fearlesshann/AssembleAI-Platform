import { expect, test, type Page } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { build } from 'esbuild';
import { compileScript, parse } from '@vue/compiler-sfc';

async function formal(page: Page, motion = '') {
  const tenantId = '11111111-1111-4111-8111-111111111111';
  await page.route('**/bff/**', route => route.fulfill({ json: route.request().url().includes('/session')
    ? { iss: 'https://motion.fixture', sub: 'reader', name: '动效验收' } : { tenantId } }));
  await page.route('**/api/platform/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/tenants')) return route.fulfill({ json: { items: [{ tenantId, name: '制造工作区' }] } });
    if (path.endsWith('/catalog')) return route.fulfill({ json: { items: Array.from({ length: 60 }, (_, i) => ({ appId: `app-${i}`, name: `制造应用 ${i}`, releaseVersion: '1.0.0', category: '制造', description: '测试数据' })), categories: ['制造'] } });
    return route.fulfill({ status: 404, json: {} });
  });
  await page.goto('/' + (motion ? '?motion=' + motion : ''));
  await expect(page.locator('.portal-app-row')).toHaveCount(60);
}

async function reference(page: Page) {
  await page.route('https://static.linear.app/fonts/**', route => route.abort());
  await page.goto('/reference-ui/?motion=full');
  await page.getByLabel('账号 / 邮箱').fill('motion@example.test');
  await page.getByLabel('密码').fill('reference-only');
  await page.getByRole('button', { name: '进入工作台', exact: true }).click();
  await expect(page.locator('#appShell')).toBeVisible();
  await page.getByRole('navigation', { name: '主导航', exact: true }).getByRole('button', { name: '应用中心', exact: true }).click();
  await expect(page.locator('#appList')).not.toHaveClass(/is-loading/);
}

async function wheelTrace(page: Page, selector: string) {
  await page.locator(selector).evaluate(node => {
    const element = node as HTMLElement; element.scrollTop = 0;
    const trace: { t: number; y: number }[] = [];
    (window as any).wheelTrace = trace;
    element.addEventListener('wheel', event => {
      (window as any).wheelPrevented = event.defaultPrevented;
      const start = performance.now();
      function sample() { trace.push({ t: performance.now() - start, y: element.scrollTop }); if (performance.now() - start < 950) requestAnimationFrame(sample); }
      sample();
    }, { once: true });
  });
  await page.locator(selector).hover();
  await page.mouse.wheel(0, 120);
  await expect.poll(() => page.evaluate(() => (window as any).wheelTrace.at(-1)?.t ?? 0)).toBeGreaterThan(940);
  return page.evaluate(() => ({ prevented: (window as any).wheelPrevented, trace: (window as any).wheelTrace as { t: number; y: number }[] }));
}

test('V37: real mouse notches use the original continuous decaying scroll in the product list', async ({ page, context }) => {
  // The reference explicitly defaults to full motion even on Windows with OS animations off.
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await formal(page);
  const original = await context.newPage(); await reference(original);
  await original.locator('#appList .scroll-content').evaluate(node => { node.innerHTML = node.innerHTML.repeat(8); });
  const baseline = await wheelTrace(original, '#appList');
  const actual = await wheelTrace(page, '.app-list');
  for (const sample of [baseline, actual]) {
    expect(sample.prevented).toBe(true);
    expect(sample.trace[0].y).toBe(0);
    expect(new Set(sample.trace.map(point => point.y)).size).toBeGreaterThan(8);
    expect(sample.trace.at(-1)!.y).toBeGreaterThan(25);
    expect(sample.trace.at(-1)!.y).toBeLessThan(70);
    expect(sample.trace.find(point => point.t >= 250)!.y).toBeGreaterThan(sample.trace.find(point => point.t >= 50)!.y);
  }
  expect(Math.abs(actual.trace.at(-1)!.y - baseline.trace.at(-1)!.y)).toBeLessThan(8);
  await expect(page.locator('html')).not.toHaveAttribute('data-wheel-scrolling');
});

test('V37: wheel ownership preserves precision, zoom, horizontal and time-wheel input; pointer stops inertia', async ({ page }) => {
  await formal(page, 'full');
  const result = await page.locator('.app-list').evaluate(async node => {
    const element = node as HTMLElement;
    function wheel(init: WheelEventInit, target: Element = element) {
      const event = new WheelEvent('wheel', { bubbles: true, cancelable: true, deltaY: 120, ...init });
      target.dispatchEvent(event); return event.defaultPrevented;
    }
    const bypass = [wheel({ deltaY: 5 }), wheel({ ctrlKey: true }), wheel({ deltaX: 240 })];
    const time = document.createElement('div'); time.dataset.wheelScroll = ''; element.append(time);
    bypass.push(wheel({}, time)); time.remove();
    const owned = wheel({});
    await new Promise(resolve => setTimeout(resolve, 100));
    element.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    const stopped = element.scrollTop;
    await new Promise(resolve => setTimeout(resolve, 120));
    return { bypass, owned, stopped, after: element.scrollTop };
  });
  expect(result.bypass).toEqual([false, false, false, false]); expect(result.owned).toBe(true);
  expect(result.stopped).toBeGreaterThan(0); expect(result.after).toBe(result.stopped);
});

test('V37: nested scroll boundaries, line/page units and reversals retain the reference algorithm', async ({ page }) => {
  await formal(page, 'full');
  const result = await page.locator('.app-list').evaluate(async node => {
    const outer = node as HTMLElement;
    const inner = document.createElement('div'); inner.style.cssText = 'height:80px;overflow:auto;overscroll-behavior:contain';
    inner.innerHTML = '<div style="height:1600px">嵌套滚动测试</div>'; outer.prepend(inner);
    const wait = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
    function wheel(deltaY: number, deltaMode = 0) { const event = new WheelEvent('wheel', { bubbles: true, cancelable: true, deltaY, deltaMode }); inner.dispatchEvent(event); return event.defaultPrevented; }
    inner.scrollTop = inner.scrollHeight - inner.clientHeight;
    const contained = wheel(120); await wait(100); const outerContained = outer.scrollTop;
    inner.style.overscrollBehavior = 'auto'; const chained = wheel(120); await wait(100); const outerChained = outer.scrollTop;
    inner.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    inner.scrollTop = 600; const line = wheel(3, 1); await wait(100); const forward = inner.scrollTop;
    const reversed = wheel(-1, 2); await wait(250); const backward = inner.scrollTop;
    inner.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    inner.remove(); return { contained, outerContained, chained, outerChained, line, forward, reversed, backward };
  });
  expect(result.contained).toBe(false); expect(result.outerContained).toBe(0);
  expect(result.chained).toBe(true); expect(result.outerChained).toBeGreaterThan(0);
  expect(result.line).toBe(true); expect(result.forward).toBeGreaterThan(600);
  expect(result.reversed).toBe(true); expect(result.backward).toBeLessThan(result.forward);
});

for (const mode of ['system', 'reduced']) test(`V37: ${mode} respects reduced motion for CSS and wheel`, async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' }); await formal(page, mode);
  await expect(page.locator('html')).toHaveAttribute('data-motion', 'reduced');
  const prevented = await page.locator('.app-list').evaluate(node => { const event = new WheelEvent('wheel', { bubbles: true, cancelable: true, deltaY: 120 }); node.dispatchEvent(event); return event.defaultPrevented; });
  expect(prevented).toBe(false);
  expect(await page.locator('.sidebar').evaluate(node => getComputedStyle(node).transitionDuration)).toBe('0s');
});

test('V37: navigation, refreshed content and user menu actually animate in full mode', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' }); await formal(page);
  await page.evaluate(() => { (window as any).motionEvents = []; document.addEventListener('animationstart', event => (window as any).motionEvents.push((event as AnimationEvent).animationName)); });
  await page.getByRole('button', { name: '刷新应用', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as any).motionEvents)).toContain('content-in');
  await page.getByRole('link', { name: '应用管理', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as any).motionEvents)).toContain('view-enter');
  const transitions = await page.getByRole('button', { name: '用户菜单', exact: true }).evaluate(async node => {
    (node as HTMLElement).click(); await new Promise(requestAnimationFrame);
    return document.querySelector('.user-menu-pop')!.getAnimations().map(animation => animation.effect!.getTiming().duration);
  });
  expect(transitions).toContain(160);
});

test('V37: source drawer and confirmation animate both directions before removal', async ({ page, context }) => {
  const compiled = await build({ stdin: { resolveDir: process.cwd(), loader: 'ts', contents: `
    import {createApp,h,ref} from 'vue';
    import Drawer from './apps/portal/src/components/reference/ReferenceDrawer.vue';
    import Confirm from './apps/portal/src/components/reference/ReferenceConfirm.vue';
    import './apps/portal/src/portal.css';
    createApp({setup(){const open=ref(false),request=ref(null);return()=>h('div',{class:'reference-ui'},[
      h('button',{id:'drawer-open',onClick:()=>open.value=true},'打开抽屉'),
      h('button',{id:'confirm-open',onClick:()=>request.value={title:'确认操作',description:'保留原动画'}},'打开确认'),
      h(Drawer,{open:open.value,title:'动效抽屉',onClose:()=>open.value=false},()=>h('input',{class:'ui-input'})),
      h(Confirm,{request:request.value,onResolve:()=>request.value=null})]);}}).mount('#root');
  ` }, bundle: true, write: false, outdir: 'motion-harness', loader: { '.woff2': 'dataurl' }, format: 'iife', define: { 'process.env.NODE_ENV': '"production"' },
    plugins: [{ name: 'actual-vue-components', setup(builder) { builder.onLoad({ filter: /\.vue$/ }, async ({ path }) => {
      const { descriptor } = parse(await readFile(path, 'utf8'), { filename: path });
      return { contents: compileScript(descriptor, { id: path, inlineTemplate: true }).content, loader: 'ts', resolveDir: resolve(path, '..') };
    }); } }] });
  await page.route('**/motion-harness', route => route.fulfill({ contentType: 'text/html', body: `<html data-motion="full"><style>${compiled.outputFiles.find(file => file.path.endsWith('.css'))!.text}</style><div id="root"></div><script>${compiled.outputFiles.find(file => file.path.endsWith('.js'))!.text}</script></html>` }));
  await page.goto('/motion-harness');
  const original = await context.newPage(); await reference(original);
  for (const kind of ['drawer', 'confirm']) {
    const target = kind === 'drawer' ? '.reference-drawer-overlay' : '.reference-confirm-overlay';
    const child = kind === 'drawer' ? '.drawer' : '.dialog';
    const referenceId = kind === 'drawer' ? 'drawer-member' : 'dialog-invite';
    const baseline = await original.evaluate(({ referenceId, child }) => {
      (window as any).__openOverlay(referenceId);
      return document.querySelector('#' + referenceId + ' ' + child)!.getAnimations().map(a => a.effect!.getTiming().duration).sort();
    }, { referenceId, child });
    const enter = await page.evaluate(async ({ kind, target, child }) => {
      (document.querySelector('#' + kind + '-open') as HTMLElement).click();
      await new Promise(requestAnimationFrame); await new Promise(requestAnimationFrame);
      return document.querySelector(target + ' ' + child)!.getAnimations().map(a => a.effect!.getTiming().duration).sort();
    }, { kind, target, child });
    expect(enter).toEqual(baseline); expect(enter).toEqual([160, 220]);
    await expect.poll(() => page.locator(target + ' ' + child).evaluate(node => getComputedStyle(node).opacity)).toBe('1');
    const leaving = await page.locator(target).evaluate(async (node, child) => {
      (node.querySelector('button') as HTMLElement).click(); await new Promise(requestAnimationFrame);
      return { open: (node as HTMLDialogElement).open, closing: node.classList.contains('is-closing'), transitions: node.querySelector(child)!.getAnimations().length };
    }, child);
    expect(leaving).toEqual({ open: true, closing: true, transitions: 2 });
    await expect(page.locator(target)).not.toBeVisible();
    await original.evaluate(id => (window as any).__closeOverlay(document.getElementById(id)), referenceId);
  }
});
