const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const source = require('./page-source.cjs')();
const mock = `<script>
window.calls=[];window.holdCatalog=false;window.pendingCatalog=[];window.failCatalog=false;
window.catalog={subscriptions:[{id:'sub-one',name:'主订阅',enabled:true},{id:'sub-disabled',name:'已禁用订阅',enabled:false},{id:'manual',name:'手动节点',enabled:true}],
 nodes:[{id:'node-one',subscriptionId:'sub-one',address:'socks5://127.0.0.1:1101',state:'Active',available:true},
 {id:'node-manual',subscriptionId:'manual',address:'http://127.0.0.1:1102',state:'Active',available:true},
 {id:'node-disabled',subscriptionId:'sub-disabled',address:'http://127.0.0.1:1103',state:'Disabled',available:false}],
 execution:{setupTimeoutSeconds:180,totalSetupTimeoutSeconds:180,responseTimeoutSeconds:600}};
window.accounts=[{id:'channel',label:'代理渠道',baseUrl:'https://upstream.example',models:['model'],endpoints:['/v1/responses'],
 networkMode:'proxyPool',proxySubscriptionIds:['sub-one'],proxyId:'node-one',enabled:true,weight:100,keyRevision:1,
 keys:[{id:'key',name:'Key',enabled:true,masked:'••••test'}]}];
window.Router2API={request:async(method,route,body)=>{
 window.calls.push({method,route,body});
 if(route==='accounts')return {accounts:window.accounts};
 if(route==='network/proxies'){
   if(window.failCatalog)throw Error('代理目录暂不可用');
   const result=structuredClone(window.catalog);
   if(window.holdCatalog)return new Promise(resolve=>window.pendingCatalog.push(()=>resolve(result)));
   return result;
 }
 if(route==='accounts/save'){
   window.accounts=[{...body,id:'channel',keyRevision:1,keys:body.keys.map(k=>({...k,id:k.id||'key',masked:'••••test'}))}];
   return {account:window.accounts[0]};
 }
 if(route==='models/discover'||route==='models/refresh')return {models:['model']};
 if(route==='tests/start')return {id:'test-job'};
 if(route.startsWith('tests/status'))return {state:'Completed',result:{rows:[{model:'model',success:true,networkMode:'proxyPool',transport:'websocket',
   originalStatus:200,mappedStatus:200,durationMs:10,retries:0,proxy:window.catalog.nodes[0],response:'{"ok":true}'}]}};
 throw Error('Unexpected route: '+route);
}};<\/script>`;
const html = `<html><head><style>html,body{margin:0}iframe{display:block;border:0;width:100%;height:100vh}</style></head><body><iframe sandbox="allow-scripts allow-forms"></iframe><script>document.querySelector('iframe').srcdoc=${JSON.stringify(source.replace('<head>', '<head>' + mock)).replaceAll('<', '\\u003c')}<\/script></body></html>`;

(async () => {
  const server = http.createServer((_, response) => { response.setHeader('Content-Type', 'text/html; charset=utf-8'); response.end(html); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport: { width: 1200, height: 950 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    const frame = page.frameLocator('iframe');
    const edit = () => frame.locator('#cards').getByRole('button', { name: '编辑', exact: true }).click();
    const state = () => page.frames().find(item => item.parentFrame());
    const lastBody = route => state().evaluate(route => window.calls.filter(call => call.route === route).at(-1)?.body, route);
    await edit();
    await frame.locator('#proxyCatalogStatus').filter({ hasText: '目录中有 3 个节点' }).waitFor();
    assert.equal(await frame.locator('#proxyScope').inputValue(), 'node');
    assert.equal(await frame.locator('#proxyNode').inputValue(), 'node-one');
    assert.equal(await frame.locator('#proxySubscriptions input[value="sub-one"]').isChecked(), true);
    await frame.locator('#tab-policy').click();
    assert.match(await frame.locator('#hostTimeoutHint').innerText(), /建立响应总预算 180 秒.*读取预算 600 秒/);
    await frame.locator('#save').click();
    await frame.locator('#editor').waitFor({ state: 'hidden' });
    let saved = await lastBody('accounts/save');
    assert.equal(saved.proxyId, 'node-one');
    assert.deepEqual(saved.proxySubscriptionIds, ['sub-one']);

    await edit();
    await frame.locator('#proxyCatalogStatus').filter({ hasText: '目录中有' }).waitFor();
    await frame.locator('#proxyScope').selectOption('manual');
    await frame.locator('#tab-models').click();
    await frame.locator('#refreshModels').click();
    await frame.locator('#discoveryResponse').filter({ hasText: '代理选择尚未保存' }).waitFor();
    assert.equal(await state().evaluate(() => window.calls.filter(c => c.route === 'models/refresh').length), 0);
    await frame.locator('#discover').click();
    await frame.locator('#discoveryStatus').filter({ hasText: '已获取 1' }).waitFor();
    const discovery = await lastBody('models/discover');
    assert.equal(discovery.networkMode, 'proxyPool');
    assert.deepEqual(discovery.proxySubscriptionIds, ['manual']);
    assert.equal(discovery.proxyId, null);
    await frame.locator('#save').click();
    await frame.locator('#editor').waitFor({ state: 'hidden' });
    saved = await lastBody('accounts/save');
    assert.deepEqual(saved.proxySubscriptionIds, ['manual']);
    assert.equal(saved.proxyId, null);
    await edit();
    await frame.locator('#proxyCatalogStatus').filter({ hasText: '目录中有' }).waitFor();
    assert.equal(await frame.locator('#proxyScope').inputValue(), 'manual');

    await frame.locator('#proxyScope').selectOption('subscriptions');
    await frame.locator('#proxySubscriptions input[value="manual"]').uncheck();
    await frame.locator('#save').click();
    await frame.locator('#formError').filter({ hasText: '至少一个订阅来源' }).waitFor();
    await frame.locator('#proxySubscriptions input[value="sub-one"]').check();
    await frame.locator('#proxySubscriptions input[value="manual"]').check();
    await frame.locator('#save').click();
    await frame.locator('#editor').waitFor({ state: 'hidden' });
    saved = await lastBody('accounts/save');
    assert.deepEqual(saved.proxySubscriptionIds, ['sub-one', 'manual']);

    await edit();
    await frame.locator('#proxyCatalogStatus').filter({ hasText: '目录中有' }).waitFor();
    await frame.locator('#proxyScope').selectOption('node');
    await frame.locator('#proxyNode').selectOption('node-manual');
    await state().evaluate(() => { window.catalog.nodes = window.catalog.nodes.filter(node => node.id !== 'node-manual'); });
    await frame.locator('#refreshProxies').click();
    await frame.locator('#proxySelectionHint').filter({ hasText: '已保留当前节点选择' }).waitFor();
    assert.equal(await frame.locator('#proxyNode').inputValue(), 'node-manual');
    assert.match(await frame.locator('#proxyNode option:checked').innerText(), /已删除/);
    await state().evaluate(() => { window.failCatalog = true; });
    await frame.locator('#refreshProxies').click();
    await frame.locator('#proxyCatalogStatus').filter({ hasText: '无法读取代理目录' }).waitFor();
    assert.equal(await frame.locator('#proxyNode').inputValue(), 'node-manual');
    await frame.locator('#save').click();
    await frame.locator('#editor').waitFor({ state: 'hidden' });
    assert.equal((await lastBody('accounts/save')).proxyId, 'node-manual');

    await state().evaluate(() => { window.failCatalog = false; window.holdCatalog = true; });
    await edit();
    await state().waitForFunction(() => window.pendingCatalog.length === 1);
    await frame.locator('#close').click();
    await state().evaluate(() => { window.holdCatalog = false; window.accounts[0].proxyId = 'node-one'; window.accounts[0].proxySubscriptionIds = []; });
    await frame.locator('#reload').click();
    await edit();
    await frame.locator('#proxyCatalogStatus').filter({ hasText: '目录中有' }).waitFor();
    await state().evaluate(() => window.pendingCatalog.splice(0).forEach(resolve => resolve()));
    assert.equal(await frame.locator('#proxyNode').inputValue(), 'node-one', 'Old editor response must not replace a new editor draft');
    await frame.locator('#proxyScope').selectOption('all');
    await frame.locator('#save').click();
    await frame.locator('#editor').waitFor({ state: 'hidden' });
    saved = await lastBody('accounts/save');
    assert.deepEqual(saved.proxySubscriptionIds, []); assert.equal(saved.proxyId, null);

    await edit();
    await frame.locator('#proxyCatalogStatus').filter({ hasText: '目录中有' }).waitFor();
    await frame.locator('#proxyScope').selectOption('node');
    await frame.locator('#proxyNode').selectOption('node-one');
    await page.setViewportSize({ width: 390, height: 844 });
    assert.equal(await frame.locator('#editor').evaluate(element => element.scrollWidth <= element.clientWidth + 1), true);
    assert.equal(await frame.locator('#proxySettings').evaluate(element => element.scrollWidth <= element.clientWidth + 1), true);
    const output = path.resolve(__dirname, '../../artifacts'); fs.mkdirSync(output, { recursive: true });
    await page.screenshot({ path: path.join(output, 'proxy-selection-mobile.png') });
    await page.setViewportSize({ width: 1200, height: 950 });
    await page.screenshot({ path: path.join(output, 'proxy-selection-desktop.png') });
    await frame.locator('#save').click();
    await frame.locator('#editor').waitFor({ state: 'hidden' });
    await frame.locator('#cards').getByRole('button', { name: '测试连接', exact: true }).click();
    await frame.locator('#testAll').click(); await frame.locator('#confirmYes').click();
    await frame.locator('#testProgress').filter({ hasText: '已完成' }).waitFor();
    await frame.locator('#testRows').getByRole('button', { name: '详情', exact: true }).click();
    assert.match(await frame.locator('#testDetailOverview').innerText(), /实际代理.*socks5:\/\/127\.0\.0\.1:1101.*代理 ID.*node-one/s);
    assert.match(await frame.locator('#testDetails').textContent(), /实际代理：socks5:\/\/127\.0\.0\.1:1101 · node-one/);
    assert.deepEqual(errors, []);
    console.log('Proxy selection UI passed: saved scopes, manual-only, draft discovery, unsaved refresh guard, missing nodes, catalog failures, stale responses, actual proxy details and mobile layout.');
  } finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
