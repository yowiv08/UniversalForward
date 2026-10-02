const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const source = require('./page-source.cjs')();
const raw = '{"Authorization":"Bearer raw-secret","input":"中文😀","html":"<img src=x onerror=window.injected=true>"}';
const bytes = Buffer.from(raw);
const row = {
  id: 'log-one', started: Date.now(), channel: 'channel', channelLabel: 'Log channel', model: 'model',
  endpoint: '/v1/responses', kind: 'forward', network: 'proxyPool', trace: 'trace-one', state: 'completed',
  status: 200, retries: 1, firstByteMs: 30, durationMs: 80, reasoningChanged: true,
  receivedReasoning: { 'reasoning.effort': 'xhigh' }, extensionReasoning: { reasoning_effort: 'low' },
  sentReasoning: { 'reasoning.effort': 'low' },
  reportedReasoning: { 'reasoning.effort': 'max', 'usage.output_tokens_details.reasoning_tokens': 34 }
};
const detail = { ...row, attempts: [{ number: 1, retryReason: 'HTTP 502' }],
  parts: [{ name: 'attempt-1-response', savedBytes: bytes.length, observedBytes: bytes.length, truncated: false }] };
const chunks = [...bytes].map((value, i) => ({ sequence: i, base64: Buffer.from([value]).toString('base64') }));
const mock = `<script>
window.calls=[];window.logSettings={enabled:true,maxRecords:1000,capacityBytes:67108864,bodyLimitBytes:4194304};
window.Router2API={request:async(method,route,body)=>{
 window.calls.push({method,route,body});
 if(route==='accounts')return {accounts:[{id:'channel',label:'Log channel',baseUrl:'https://upstream.example',models:['model'],keys:[],enabled:true}]};
 if(route==='logs/status')return {available:true,storage:'memory',settings:window.logSettings};
 if(route.startsWith('logs?'))return {rows:window.deleted?[]:[${JSON.stringify(row)}],page:1,pageSize:25,total:window.deleted?0:1};
 if(route.startsWith('logs/detail?'))return ${JSON.stringify(detail)};
 if(route.startsWith('logs/body?')){const after=+new URLSearchParams(route.split('?')[1]).get('after');const chunks=${JSON.stringify(chunks)};const selected=chunks.filter(c=>c.sequence>after).slice(0,4);return {chunks:selected,next:selected.at(-1)?.sequence??after,done:selected.length<4};}
 if(route==='logs/settings'){window.logSettings=body;return{success:true};}
 if(route==='logs/delete'||route==='logs/clear'){window.deleted=true;return{success:true};}
 throw Error('Unexpected route: '+route);
}};<\/script>`;
const html = `<html><head><style>html,body{margin:0}iframe{display:block;border:0;width:100%;height:100vh}</style></head><body><iframe sandbox="allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox"></iframe><script>document.querySelector('iframe').srcdoc=${JSON.stringify(source.replace('<head>', '<head>' + mock)).replaceAll('<', '\\u003c')}<\/script></body></html>`;

(async () => {
  const server = http.createServer((_, res) => { res.setHeader('Content-Type', 'text/html; charset=utf-8'); res.end(html); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ headless: true });
  try {
    const origin = `http://127.0.0.1:${server.address().port}`;
    const context = await browser.newContext({ acceptDownloads: true });
    await context.grantPermissions(['clipboard-read'], { origin });
    const page = await context.newPage();
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(origin);
    const frame = page.frameLocator('iframe');
    await frame.locator('#cards').getByRole('button', { name: '日志', exact: true }).click();
    await frame.locator('#journalRows').getByRole('button', { name: '详情' }).waitFor();
    assert.equal(await frame.locator('#journalFilters [name=channel]').inputValue(), 'channel');
    const effort = frame.locator('#journalRows tr td').nth(3);
    assert.deepEqual(await effort.locator('div').allTextContents(), [
      '请求：reasoning.effort="low"',
      '上游：reasoning.effort="max"；usage.output_tokens_details.reasoning_tokens=34'
    ]);
    assert.equal(await effort.locator('strong').count(), 0);
    assert.equal((await frame.locator('#journalRows').textContent()).includes('raw-secret'), false);
    await frame.locator('#journalRows').getByRole('button', { name: '详情' }).click();
    await frame.locator('#journalBody').filter({ hasText: 'raw-secret' }).waitFor();
    assert.equal(await frame.locator('#journalBody').textContent(), raw);
    assert.equal(await frame.locator('#journalBody img').count(), 0);
    await page.screenshot({ path: path.resolve(__dirname, '../../artifacts/journal-redesign-desktop.png'), fullPage: true });
    await frame.locator('#journalCopy').click();
    await frame.locator('#journalStatus').filter({ hasText: '已复制原文' }).waitFor();
    assert.equal(await page.evaluate(() => navigator.clipboard.readText()), raw);
    await frame.locator('#journalJson').click();
    assert.deepEqual(JSON.parse(await frame.locator('#journalBody').textContent()), JSON.parse(raw));
    const downloadPromise = new Promise(resolve => context.on('page', popup => popup.on('download', resolve)));
    await frame.locator('#journalExport').click();
    const download = await Promise.race([downloadPromise, new Promise((_, reject) => setTimeout(() => reject(Error('Export download timeout')), 10000))]);
    const exportPath = path.resolve(__dirname, '../../artifacts/journal-export-test.json');
    await download.saveAs(exportPath);
    const exported = JSON.parse(fs.readFileSync(exportPath, 'utf8'));
    assert.equal(exported.format, 'universalforward-request-log-v1');
    assert.equal(Buffer.concat(exported.bodyChunks.map(c => Buffer.from(c.base64, 'base64'))).toString(), raw);
    await page.bringToFront();
    await frame.locator('#journalDelete').click();
    await frame.locator('#confirmNo').click();
    assert.equal(await frame.locator('#journalRows tr').count(), 1);
    await frame.locator('#journalDelete').click();
    await frame.locator('#confirmYes').click();
    await frame.locator('#journalPage').filter({ hasText: '共 0 条' }).waitFor();
    await frame.locator('summary').filter({ hasText: '日志存储设置' }).click();
    await frame.locator('#journalSettings [name=maxRecords]').fill('14');
    await frame.locator('#journalSettings button').click();
    await frame.locator('#journalStatus').filter({ hasText: '日志设置已生效，仅限本次运行' }).waitFor();
    await page.setViewportSize({ width: 390, height: 844 });
    assert.equal(await frame.locator('#journal').evaluate(n => n.scrollWidth <= n.clientWidth + 1), true);
    await page.screenshot({ path: path.resolve(__dirname, '../../artifacts/journal-redesign-mobile.png'), fullPage: true });
    await frame.locator('#journalClose').click();
    assert.equal(await frame.locator('#journalBody').textContent(), '');
    assert.deepEqual(errors, []);
    await context.close();
    console.log('Journal UI passed: channel filtering, reasoning evidence, chunk decoding, raw secrets, safe HTML, real copy/export, confirmations, settings, mobile.');
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
