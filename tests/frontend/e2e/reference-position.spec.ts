import { test, expect, type Page } from '@playwright/test';
import { build } from 'esbuild';
import { compileScript, parse } from '@vue/compiler-sfc';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

async function harness(page: Page) {
  const output = await build({ stdin: { loader: 'ts', resolveDir: process.cwd(), contents: `
    import { createApp, h, ref } from 'vue';
    import Select from './apps/portal/src/components/reference/ReferenceSelect.vue';
    import DateTime from './apps/portal/src/components/reference/ReferenceDateTime.vue';
    import Drawer from './apps/portal/src/components/reference/ReferenceDrawer.vue';
    import './apps/portal/src/portal.css';
    const app = createApp({ setup() {
      const value=ref('a'), date=ref('2026-11-05T10:30'), drawer=ref(false), visible=ref(true);
      const controls = (prefix) => [h(Select,{label:prefix+'选择',modelValue:value.value,options:[{value:'a',label:'一号工厂'},{value:'b',label:'二号工厂'}],'onUpdate:modelValue':v=>value.value=v}),
        h(DateTime,{label:prefix+'时间',modelValue:date.value,'onUpdate:modelValue':v=>date.value=v})];
      window.unmountControls=()=>visible.value=false;
      return ()=>h('main',{class:'reference-ui'},[h('button',{id:'open-drawer',onClick:()=>drawer.value=true},'打开抽屉'),
        h('div',{class:'moving',style:'transform:translate(36px,18px);margin:100px 0 0 180px;width:600px'},
          h('div',{id:'scroller',style:'height:360px;overflow:auto;border:1px solid gray;padding:20px'},[
            h('div',{style:'height:70px'}),h('div',{id:'controls',style:'width:260px'},visible.value?controls('页面'):[]),h('div',{style:'height:900px'})])),
        h(Drawer,{open:drawer.value,title:'定位验证',onClose:()=>drawer.value=false},{default:()=>[h('div',{style:'height:100px'}),...controls('抽屉'),h('div',{style:'height:900px'})]})]);
    }});app.mount('#root');
  ` }, bundle: true, write: false, outdir: 'position-harness', format: 'iife', loader: { '.woff2': 'dataurl' }, define: { 'process.env.NODE_ENV': '"production"' }, plugins: [{ name: 'real-vue-components', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, async ({ path }) => { const { descriptor } = parse(await readFile(path, 'utf8'), { filename: path }); return { contents: compileScript(descriptor, { id: path, inlineTemplate: true }).content, loader: 'ts', resolveDir: resolve(path, '..') }; });
  } }] });
  await page.route('**/position-harness', route => route.fulfill({ contentType: 'text/html', body: `<html data-motion="full" data-theme="light"><style>${output.outputFiles.find(file=>file.path.endsWith('.css'))!.text}</style><div id="root"></div><script>${output.outputFiles.find(file=>file.path.endsWith('.js'))!.text}</script></html>` }));
  await page.goto('/position-harness');
}
async function aligned(page: Page, name: string, kind: 'select'|'datetime') {
  const trigger = page.getByRole(kind === 'select' ? 'combobox' : 'button', { name, exact: true });
  const panel = trigger.locator('..').locator(kind === 'select' ? '.ui-select__menu' : '.ui-datetime__panel');
  await expect.poll(async () => panel.evaluate(node => getComputedStyle(node).opacity)).toBe('1');
  await expect.poll(async () => {
    const t = (await trigger.boundingBox())!, p = (await panel.boundingBox())!;
    return Math.max(Math.abs(t.x-p.x), Math.min(Math.abs(p.y-t.y-t.height-4), Math.abs(t.y-p.y-p.height-4)));
  }, { message: `${name}: panel stays attached with the original 4px gap` }).toBeLessThanOrEqual(1.5);
}
for (const kind of ['select','datetime'] as const) test(`V37: ${kind} stays anchored through transformed ancestors, scrolling and layout movement`, async ({ page }) => {
  await harness(page); const name = '页面' + (kind === 'select' ? '选择' : '时间');
  await page.getByRole(kind === 'select' ? 'combobox' : 'button', { name, exact: true }).click();
  await aligned(page,name,kind);
  const colors = await page.locator(`.ui-${kind}.is-open .ui-${kind}__${kind==='select'?'menu':'panel'}`).evaluate(node=>[getComputedStyle(node).color,getComputedStyle(node.parentElement!).color]);
  expect(colors[0]).toBe(colors[1]);
  await page.locator('#scroller').evaluate(node=>node.scrollTop=65);
  await aligned(page,name,kind);
  await page.locator('.moving').evaluate(node=>(node as HTMLElement).style.transform='translate(120px,35px) scale(.92)');
  await aligned(page,name,kind);
  if(kind==='datetime') {
    const panel = page.locator('.ui-datetime.is-open .ui-datetime__panel');
    const hour = panel.getByRole('listbox',{name:'小时',exact:true});
    await hour.click(); await hour.press('ArrowDown');
    await expect(page.getByRole('button',{name,exact:true})).toContainText('11:30');
    await panel.getByRole('button',{name:'选择月份',exact:true}).click();
    await expect(panel.locator('[data-pick-month]')).toHaveCount(12); await aligned(page,name,kind);
    await page.keyboard.press('Escape');
  } else {
    await page.getByRole('option',{name:'二号工厂',exact:true}).click();
    await expect(page.getByRole('combobox',{name,exact:true})).toHaveText('二号工厂');
  }
  await page.keyboard.press('Escape');
  await expect(page.getByRole(kind === 'select' ? 'combobox' : 'button', { name, exact: true })).toHaveAttribute('aria-expanded','false');
});
for (const kind of ['select','datetime'] as const) test(`V37: ${kind} stays above the native drawer and follows its entry animation`, async ({page})=>{
  await harness(page); await page.locator('#open-drawer').click();
  const name = '抽屉' + (kind==='select'?'选择':'时间');
  const frames = await page.evaluate(async ({kind})=>{
    const trigger = document.querySelector(`.drawer .ui-${kind}__trigger`) as HTMLElement;
    trigger.click();
    const panel = trigger.parentElement!.querySelector(`.ui-${kind}__${kind==='select'?'menu':'panel'}`)!;
    return await new Promise<number[]>(resolve=>{const errors:number[]=[];function sample(){const t=trigger.getBoundingClientRect(), p=panel.getBoundingClientRect(); errors.push(Math.max(Math.abs(t.left-p.left),Math.abs(p.top-t.bottom-4)));if(errors.length<30)requestAnimationFrame(sample);else resolve(errors);}requestAnimationFrame(sample);});
  },{kind});
  expect(Math.max(...frames)).toBeLessThan(8); await aligned(page,name,kind);
  await page.locator('.subwindow-body').evaluate(node=>node.scrollTop=65); await aligned(page,name,kind);
  if(kind==='select') await page.getByRole('option',{name:'二号工厂',exact:true}).click();
  else { await page.locator('.drawer .ui-datetime__panel').getByRole('button',{name:'选择月份',exact:true}).click(); await page.keyboard.press('Escape'); await page.keyboard.press('Escape'); }
  await expect(page.locator('.reference-drawer-overlay')).toBeVisible();
  await page.keyboard.press('Escape'); await expect(page.locator('.reference-drawer-overlay')).toHaveCount(0);
  await expect(page.locator(':popover-open')).toHaveCount(0);
});
test('V37: floating controls clamp to a narrow viewport, close when their anchor scrolls away, and release on unmount',async({page})=>{
  await harness(page);
  await page.setViewportSize({width:390,height:844});
  await page.locator('.moving').evaluate(node=>Object.assign((node as HTMLElement).style,{transform:'none',margin:'100px 16px 0',width:'358px'}));
  await page.getByRole('button',{name:'页面时间',exact:true}).click();
  const panel=page.locator('.ui-datetime.is-open .ui-datetime__panel');
  await aligned(page,'页面时间','datetime');
  const bounds=(await panel.boundingBox())!; expect(bounds.x).toBeGreaterThanOrEqual(8);expect(bounds.x+bounds.width).toBeLessThanOrEqual(382);
  await page.locator('#scroller').evaluate(node=>node.scrollTop=300);
  await expect(page.locator(':popover-open')).toHaveCount(0);
  await page.locator('#scroller').evaluate(node=>node.scrollTop=0);
  await page.getByRole('combobox',{name:'页面选择',exact:true}).click();
  await expect(page.locator(':popover-open')).toHaveCount(1);
  const root=await page.locator('#controls > .ui-select').elementHandle();
  await page.evaluate(()=>(window as any).unmountControls());
  await expect(page.locator(':popover-open')).toHaveCount(0);
  expect(await root!.evaluate(node=>!!(node as any)._referenceStopPosition)).toBe(false);
});
test('V37: datetime follows outer scroll even with no transformed ancestor', async ({ page }) => {
  await harness(page); await page.locator('.moving').evaluate(node=>(node as HTMLElement).style.transform='none');
  await page.getByRole('button',{name:'页面时间',exact:true}).click(); await aligned(page,'页面时间','datetime');
  await page.locator('#scroller').evaluate(node=>node.scrollTop=65); await aligned(page,'页面时间','datetime');
});
