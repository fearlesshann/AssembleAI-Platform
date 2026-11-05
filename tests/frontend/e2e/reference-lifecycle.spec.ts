import { expect, test } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { resolve, extname } from 'node:path';
import { build, type Plugin } from 'esbuild';
import { compileScript, parse } from '@vue/compiler-sfc';
import { chooseReferenceOption, expectReferenceValue } from '../reference-controls-helpers.mjs';

const vue: Plugin = { name: 'real-vue-source', setup(builder) { builder.onLoad({ filter: /\.vue$/ }, async ({ path }) => {
  const { descriptor } = parse(await readFile(path, 'utf8'), { filename: path });
  return { contents: compileScript(descriptor, { id: path, inlineTemplate: true }).content, loader: 'ts', resolveDir: resolve(path, '..') };
}); } };
async function bundle(contents: string, globalName: string) {
  return build({ stdin: { contents, resolveDir: process.cwd(), loader: 'ts' }, bundle: true, write: false, outdir: 'reference-lifecycle',
    format: 'iife', globalName, loader: { '.woff2': 'dataurl' }, define: { 'process.env.NODE_ENV': '"production"' }, plugins: [vue] });
}

test('V19/V37: a qiankun parcel can install, unmount and remount the shared original controls', async ({ page }) => {
  const parcel = await bundle(`import {createApp,h,ref} from 'vue';
    import Select from './apps/portal/src/components/reference/ReferenceSelect.vue';
    import DateTime from './apps/portal/src/components/reference/ReferenceDateTime.vue';
    let app;
    export async function bootstrap(){}
    export async function mount(props){ app=createApp({setup(){const value=ref('alpha'),date=ref('2026-11-02T09:15');return ()=>h('section',{style:'width:180px;margin-left:auto'},[
      h(Select,{label:'微应用选项',modelValue:value.value,options:[{value:'alpha',label:'第一项'},{value:'beta',label:'第二项'}],'onUpdate:modelValue':v=>value.value=v}),
      h(DateTime,{label:'微应用日期',modelValue:date.value,'onUpdate:modelValue':v=>date.value=v})]);}});app.mount(props.container.querySelector('#control-root'));}
    export async function unmount(){app.unmount();app=null;}`, 'ReferenceControlParcel');
  const host = await bundle(`import {loadMicroApp} from 'qiankun';let parcel;
    document.getElementById('mount').onclick=async()=>{parcel=loadMicroApp({name:'source-controls',entry:'/parcel/',container:'#host'},{sandbox:true,singular:false});await parcel.mountPromise;document.getElementById('status').textContent='已挂载';};
    document.getElementById('unmount').onclick=async()=>{await parcel.unmount();document.getElementById('status').textContent='已卸载';};`, 'ReferenceControlHost');
  await page.route('**/lifecycle-host', route => route.fulfill({ contentType: 'text/html; charset=utf-8', body: '<button id="mount">挂载控件</button><button id="unmount">卸载控件</button><p id="status"></p><div id="host"></div><script src="/host.js"></script>' }));
  await page.route('**/host.js', route => route.fulfill({ contentType: 'text/javascript', body: host.outputFiles.find(file => file.path.endsWith('.js'))!.text }));
  await page.route('**/parcel/', route => route.fulfill({ contentType: 'text/html; charset=utf-8', body: '<link rel="stylesheet" href="/parcel.css"><div id="control-root"></div><script src="/parcel.js" entry></script>' }));
  await page.route('**/parcel.js', route => route.fulfill({ contentType: 'text/javascript', body: parcel.outputFiles.find(file => file.path.endsWith('.js'))!.text + '\nwindow.ReferenceControlParcel = ReferenceControlParcel;'  }));
  await page.route('**/parcel.css', route => route.fulfill({ contentType: 'text/css', body: parcel.outputFiles.find(file => file.path.endsWith('.css'))!.text }));
  await page.goto('/lifecycle-host');
  for (let cycle = 0; cycle < 2; cycle++) {
    await page.setViewportSize({ width: 1366, height: 900 });
    await page.getByRole('button', { name: '挂载控件', exact: true }).click();
    await expect(page.locator('#status')).toHaveText('已挂载');
    await chooseReferenceOption(page, '微应用选项', 'beta'); await expectReferenceValue(page, '微应用选项', 'beta');
    await page.getByRole('button', { name: '微应用日期', exact: true }).click();
    await page.setViewportSize({ width: 390, height: 844 });
    const panel = page.getByRole('dialog', { name: '微应用日期：选择日期与时间', exact: true });
    await expect(panel).toBeVisible();
    await expect.poll(async () => { const box = await panel.boundingBox(); return box!.x + box!.width; }).toBeLessThanOrEqual(390);
    await page.getByRole('button', { name: '卸载控件', exact: true }).click();
    await expect(page.locator('#status')).toHaveText('已卸载'); await expect(page.getByRole('combobox')).toHaveCount(0);
  }
});

for (const app of ['sample', 'chatbi', 'knowledge']) test(`V13/V37: ${app} standalone artifact supplies the exact source font without a portal`, async ({ page }) => {
  const prefix = `/apps/${app}/0.1.0/`; const fontRequests: string[] = [];
  page.on('request', request => { if (request.resourceType() === 'font') fontRequests.push(request.url()); });
  // Only this single release's dist is served. No portal files or external host.
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/bff/session') return route.fulfill({ status: 401, json: { code: 'authentication_required' } });
    if (url.hostname !== 'standalone.test' || !url.pathname.startsWith(prefix)) return route.fulfill({ status: 404, body: 'Not part of this release' });
    const name = url.pathname.slice(prefix.length) || 'index.html';
    if (!/^[a-zA-Z0-9_.-]+$/.test(name)) return route.fulfill({ status: 404, body: '' });
    const contentType = ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2' } as Record<string, string>)[extname(name)];
    try { return await route.fulfill({ contentType, body: await readFile(resolve(`apps/${app}/frontend/dist`, name)) }); }
    catch { return route.fulfill({ status: 404, body: '' }); }
  });
  await page.goto('http://standalone.test' + prefix);
  expect(await page.evaluate(async () => { const faces = await document.fonts.load('13px "Inter Variable"'); await document.fonts.ready; return faces.length > 0 && document.fonts.check('13px "Inter Variable"'); })).toBe(true);
  expect(fontRequests.filter(url => !url.startsWith('data:') && !url.startsWith('http://standalone.test' + prefix))).toEqual([]);
});
