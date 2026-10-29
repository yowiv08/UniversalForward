const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const source = require('./page-source.cjs')();
const raw = '{"code":"upstream_error","message":"保留错误中文😀","html":"<img src=x onerror=window.injected=true>"}';
const retainedBodies = {
  extensions: '{"reasoning_effort":"low"}',
  'incoming-headers': '{"User-Agent":"test-client"}',
  'attempt-1-request-headers': '{"Authorization":"Bearer header-secret"}',
  'attempt-2-response-headers': '{"Content-Type":"text/event-stream","x-request-id":"upstream-id"}',
  error: raw
};
const removedParts = ['incoming', 'received', 'attempt-1-request', 'attempt-2-response', 'attempt-1-websocket-events'];
const bodies = { ...retainedBodies, ...Object.fromEntries(removedParts.map(name => [name, 'legacy-payload-' + name])) };
const row = {
  id: 'log-one', started: Date.now(), channel: 'channel', channelLabel: 'Log channel', model: 'model',
  requestedModel: 'local-alias', sentModel: 'routed-model', reportedModel: 'upstream-version-20261022',
  endpoint: '/v1/responses', kind: 'forward', network: 'proxyPool', trace: 'trace-one', state: 'completed',
  status: 200, retries: 1, firstByteMs: 30, durationMs: 80, reasoningChanged: true,
  proxy: { id: 'node-one', subscriptionId: 'manual', address: 'socks5://127.0.0.1:1080', state: 'Active', available: true },
  receivedReasoning: { 'reasoning.effort': 'xhigh' }, extensionReasoning: { reasoning_effort: 'low' },
  sentReasoning: { 'reasoning.effort': 'low' },
  reportedReasoning: { 'reasoning.effort': 'max', 'usage.output_tokens_details.reasoning_tokens': 34 },
  tokenUsage: { inputTokens: 128692, outputTokens: 178, cacheReadTokens: 125712, cacheWriteTokens: 2977 }
};
const detail = { ...row, attempts: [
  { number: 1, transport: 'websocket', status: 101, url: 'wss://upstream.example/v1/responses', sentModel: 'first-routed-model', reportedModel: 'first-upstream-model', retryReason: '空响应',
    tokenUsage: { inputTokens: 1000, outputTokens: 0, cacheReadTokens: 900, cacheWriteTokens: null } },
  { number: 2, transport: 'websocket', status: 101, url: 'wss://upstream.example/v1/responses', sentModel: row.sentModel, reportedModel: row.reportedModel, tokenUsage: row.tokenUsage }
],
  parts: Object.entries(bodies).map(([name, body]) => ({ name, savedBytes: Buffer.byteLength(body), observedBytes: Buffer.byteLength(body), truncated: false })) };
const chunksByPart = Object.fromEntries(Object.entries(bodies).map(([name, body]) => [name,
  [...Buffer.from(body)].map((value, i) => ({ sequence: i, base64: Buffer.from([value]).toString('base64') }))]));
const mock = `<script>
window.calls=[];window.logSettings={enabled:true,maxRecords:1000,capacityBytes:67108864,bodyLimitBytes:4194304};
window.logRow=${JSON.stringify(row)};window.logDetail=${JSON.stringify(detail)};
window.Router2API={request:async(method,route,body)=>{
 window.calls.push({method,route,body});
 if (route === 'appearance/settings') return { mode: 'light' };
    if (route === 'execution/settings') return { defaultHostAttempts: 1, execution: window.catalog?.execution || { setupTimeoutSeconds: 300, totalSetupTimeoutSeconds: 180, responseTimeoutSeconds: 600 } };
    if(route==='accounts')return {accounts:[{id:'channel',label:'Log channel',baseUrl:'https://upstream.example',models:['model'],keys:[],enabled:true}]};
 if(route==='logs/status')return {available:true,storage:'memory',settings:window.logSettings};
 if(route.startsWith('logs?'))return {rows:window.deleted?[]:[window.logRow],page:1,pageSize:25,total:window.deleted?0:1};
 if(route.startsWith('logs/detail?'))return window.logDetail;
 if(route.startsWith('logs/body?')){const query=new URLSearchParams(route.split('?')[1]);const after=+query.get('after');const chunksByPart=${JSON.stringify(chunksByPart)};const chunks=chunksByPart[query.get('part')];if(!chunks)throw Error('Unexpected part: '+query.get('part'));const selected=chunks.filter(c=>c.sequence>after).slice(0,4);return {chunks:selected,next:selected.at(-1)?.sequence??after,done:selected.length<4};}
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
    await frame.locator('#cards .channel-menu-trigger').click();
    await frame.locator('#cards').getByRole('menuitem', { name: '日志', exact: true }).click();
    await frame.locator('#journalRows').getByRole('button', { name: '详情' }).waitFor();
    assert.equal(await frame.locator('#journalFilters [name=channel]').inputValue(), 'channel');
    const effort = frame.locator('#journalRows tr td').nth(3);
    assert.equal(await effort.locator('.record-effort').innerText(), '请求：low');
    assert.equal(await effort.locator('.record-effort-reported').innerText(), '上游：max · 34 tokens');
    assert.equal(await effort.locator('.record-effort-reported').getAttribute('title'), 'reasoning.effort="max"；usage.output_tokens_details.reasoning_tokens=34');
    assert.equal(await effort.locator('strong').count(), 0);
    assert.equal((await frame.locator('#journalRows').textContent()).includes('legacy-payload'), false);
    assert.match(await frame.locator('#journalRows .record-network').getAttribute('title'), /socks5:\/\/127\.0\.0\.1:1080.*node-one/);
    assert.equal(await frame.locator('#journalRows .record-model').innerText(), 'local-alias');
    assert.match(await frame.locator('#journalRows').innerText(), /响应：upstream-version-20261022/);
    const tokenMetrics = frame.locator('#journalRows .record-metrics > span').filter({ hasText: 'Token' });
    assert.deepEqual(await tokenMetrics.locator('strong').allTextContents(), ['128,692', '178']);
    await frame.locator('#journalRows').getByRole('button', { name: '详情' }).click();
    await frame.locator('#journalBody').filter({ hasText: 'upstream-id' }).waitFor();
    assert.equal(await frame.locator('#journalPart').inputValue(), 'attempt-2-response-headers');
    assert.deepEqual(await frame.locator('#journalPart option').evaluateAll(options => options.map(option => option.value)), Object.keys(retainedBodies));
    assert.match(await frame.locator('#journalPart option:checked').innerText(), /上游响应头/);
    assert.equal((await frame.locator('#journalMetadata').textContent()).includes('websocket-events'), false);
    const metadata = JSON.parse(await frame.locator('#journalMetadata').textContent());
    assert.deepEqual(metadata.parts.map(part => part.name), Object.keys(retainedBodies));
    assert.equal((await frame.locator('#journalDetail').textContent()).includes('插件收到的请求体'), false);
    assert.equal((await frame.locator('#journalDetail').textContent()).includes('legacy-payload'), false);
    await frame.locator('#journalPart').selectOption('error');
    await frame.locator('#journalBody').filter({ hasText: '保留错误中文' }).waitFor();
    assert.equal(await frame.locator('#journalBody').textContent(), raw);
    assert.equal(await frame.locator('#journalBody img').count(), 0);
    assert.match(await frame.locator('#journalOverview').innerText(), /WebSocket/);
    assert.match(await frame.locator('#journalOverview').innerText(), /101/);
    assert.match(await frame.locator('#journalOverview').innerText(), /实际代理.*socks5:\/\/127\.0\.0\.1:1080.*node-one/s);
    const modelGroup = frame.locator('#journalOverview .detail-group').filter({ has: frame.getByRole('heading', { name: '模型信息', exact: true }) });
    assert.deepEqual(await modelGroup.locator('dt').allTextContents(), ['本地请求模型', '实际发送模型', '上游响应模型']);
    assert.deepEqual(await modelGroup.locator('dd').allTextContents(), [row.requestedModel, row.sentModel, row.reportedModel]);
    const tokenGroup = frame.locator('#journalOverview .detail-group').filter({ has: frame.getByRole('heading', { name: 'Token 明细', exact: true }) });
    assert.deepEqual(await tokenGroup.locator('dt').allTextContents(), ['输入 Token', '输出 Token', '缓存读取', '缓存写入']);
    assert.deepEqual(await tokenGroup.locator('dd').allTextContents(), ['128,692', '178', '125,712', '2,977']);
    await tokenGroup.scrollIntoViewIfNeeded();
    await tokenGroup.screenshot({ path: path.resolve(__dirname, '../../artifacts/cpu-20261029/token-details.png') });
    await frame.locator('#journalDetail').screenshot({ path: path.resolve(__dirname, '../../artifacts/cpu-20261029/journal-details.png') });
    const firstAttempt = frame.locator('#journalOverview .detail-group').filter({ has: frame.getByRole('heading', { name: '上游尝试 1', exact: true }) });
    assert.match(await firstAttempt.innerText(), /实际发送模型\s+first-routed-model/);
    assert.match(await firstAttempt.innerText(), /上游响应模型\s+first-upstream-model/);
    assert.deepEqual((await firstAttempt.locator('dd').allTextContents()).slice(-4), ['1,000', '0', '900', '未返回']);
    assert.equal(await frame.locator('#journalPart option:checked').innerText(), 'error');
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
    assert.equal(exported.requestedModel, row.requestedModel);
    assert.equal(exported.sentModel, row.sentModel);
    assert.equal(exported.reportedModel, row.reportedModel);
    assert.equal(exported.attempts[0].reportedModel, 'first-upstream-model');
    assert.deepEqual(exported.tokenUsage, row.tokenUsage);
    assert.deepEqual(exported.attempts[0].tokenUsage, detail.attempts[0].tokenUsage);
    assert.deepEqual(exported.attempts[1].tokenUsage, row.tokenUsage);
    assert.deepEqual(exported.parts.map(part => part.name), Object.keys(retainedBodies));
    assert.deepEqual([...new Set(exported.bodyChunks.map(chunk => chunk.part))], Object.keys(retainedBodies));
    for (const [part, body] of Object.entries(retainedBodies))
      assert.equal(Buffer.concat(exported.bodyChunks.filter(chunk => chunk.part === part).map(chunk => Buffer.from(chunk.base64, 'base64'))).toString(), body);
    assert.equal(JSON.stringify(exported).includes('legacy-payload'), false);
    assert.equal(fs.readFileSync(exportPath, 'utf8').includes('插件收到的请求体'), false);
    const requestedParts = await frame.locator('body').evaluate(() => window.calls.filter(call => call.route.startsWith('logs/body?'))
      .map(call => new URLSearchParams(call.route.split('?')[1]).get('part')));
    assert.equal(requestedParts.some(part => removedParts.includes(part)), false);
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
    for (const [sent, reported, requestSummary, upstreamSummary] of [
      [{ 'reasoning_effort': 'high' }, { 'effort': 'max', 'usage.completion_tokens_details.reasoning_tokens': 0 }, '请求：high', '上游：max · 0 tokens'],
      [{ 'thinking.type': 'enabled', 'thinking.budget_tokens': 4096 }, { 'thinking.effort': 'high' }, '请求：模式 enabled · 预算 4096 tokens', '上游：high'],
      [{ 'output_config.effort': 'medium' }, { 'usage.output_tokens_details.reasoning_tokens': 21 }, '请求：medium', '上游：21 tokens'],
      [{}, {}, '请求：未提供', '上游：未返回']
    ]) {
      await frame.locator('body').evaluate((element, values) => {
        window.deleted = false;
        window.logRow = { ...window.logRow, sentReasoning: values.sent, reportedReasoning: values.reported };
        document.getElementById('journalFilters').requestSubmit();
      }, { sent, reported });
      await effort.locator('.record-effort').filter({ hasText: requestSummary }).waitFor();
      assert.equal(await effort.locator('.record-effort').innerText(), requestSummary);
      assert.equal(await effort.locator('.record-effort-reported').innerText(), upstreamSummary);
      if (Object.keys(reported).length) assert.equal(await effort.locator('.record-effort-reported').getAttribute('title'),
        Object.entries(reported).map(([key, value]) => `${key}=${JSON.stringify(value)}`).join('；'));
    }
    await frame.locator('body').evaluate(() => {
      window.deleted = false;
      const legacy = { ...window.logRow, model: 'legacy-model' };
      delete legacy.requestedModel; delete legacy.sentModel; delete legacy.reportedModel; delete legacy.tokenUsage;
      window.logRow = legacy;
      window.logDetail = { ...legacy, attempts: [{ number: 1, transport: 'http', status: 200 }], parts: [] };
      document.getElementById('journalFilters').requestSubmit();
    });
    await frame.locator('#journalRows .record-model').filter({ hasText: 'legacy-model' }).waitFor();
    assert.match(await frame.locator('#journalRows').innerText(), /响应：未返回/);
    await frame.locator('#journalRows').getByRole('button', { name: '详情' }).click();
    await modelGroup.waitFor();
    assert.deepEqual(await modelGroup.locator('dd').allTextContents(), ['legacy-model', '未提供', '未返回']);
    assert.deepEqual(await tokenGroup.locator('dd').allTextContents(), ['未返回', '未返回', '未返回', '未返回']);
    assert.equal(await frame.locator('#journalPart option').count(), 0);
    assert.equal(await frame.locator('#journalPart').isDisabled(), true);
    assert.equal(await frame.locator('#journalCopy').isDisabled(), true);
    assert.equal(await frame.locator('#journalBody').textContent(), '（没有可查看的原始内容）');
    await frame.locator('#journalDetailClose').click();
    for (const [usage, expected] of [
      [{ inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0 }, ['0', '0', '0', '0']],
      [{ inputTokens: 2048, outputTokens: null }, ['2,048', '未返回', '未返回', '未返回']],
      [null, ['未返回', '未返回', '未返回', '未返回']]
    ]) {
      await frame.locator('body').evaluate((element, tokenUsage) => {
        window.logRow = { ...window.logRow, tokenUsage };
        window.logDetail = { ...window.logDetail, tokenUsage, attempts: [{ number: 1, tokenUsage }] };
        document.getElementById('journalFilters').requestSubmit();
      }, usage);
      await tokenMetrics.locator('strong').first().filter({ hasText: expected[0] }).waitFor();
      assert.deepEqual(await tokenMetrics.locator('strong').allTextContents(), expected.slice(0, 2));
      await frame.locator('#journalRows').getByRole('button', { name: '详情' }).click();
      await tokenGroup.waitFor();
      assert.deepEqual(await tokenGroup.locator('dd').allTextContents(), expected);
      assert.deepEqual((await firstAttempt.locator('dd').allTextContents()).slice(-4), expected);
      await frame.locator('#journalDetailClose').click();
    }
    const longModel = '<img src=x onerror=window.injected=true>' + 'very-long-upstream-model-name-'.repeat(16);
    await frame.locator('body').evaluate((element, model) => {
      window.logRow = { ...window.logRow, requestedModel: model, sentModel: 'sent-model', reportedModel: model };
      window.logDetail = { ...window.logRow, attempts: [{ number: 1, sentModel: 'sent-model', reportedModel: model }], parts: [] };
      document.getElementById('journalFilters').requestSubmit();
    }, longModel);
    await frame.locator('#journalRows .record-model').filter({ hasText: longModel }).waitFor();
    assert.equal(await frame.locator('#journalRows img').count(), 0);
    await frame.locator('#journalRows').getByRole('button', { name: '详情' }).click();
    await modelGroup.waitFor();
    assert.deepEqual(await modelGroup.locator('dd').allTextContents(), [longModel, 'sent-model', longModel]);
    assert.equal(await frame.locator('#journalOverview img').count(), 0);
    assert.equal(await frame.locator('body').evaluate(() => !!window.injected), false);
    await page.setViewportSize({ width: 390, height: 844 });
    assert.equal(await frame.locator('#journal').evaluate(n => n.scrollWidth <= n.clientWidth + 1), true);
    assert.equal(await frame.locator('#journalDetail').evaluate(n => n.scrollWidth <= n.clientWidth + 1), true);
    await page.screenshot({ path: path.resolve(__dirname, '../../artifacts/journal-redesign-mobile.png'), fullPage: true });
    await frame.locator('#journalDetailClose').click();
    await frame.locator('#journalClose').click();
    assert.equal(await frame.locator('#journalBody').textContent(), '');
    assert.deepEqual(errors, []);
    await context.close();
    console.log('Journal UI passed: requested/sent/reported models, per-attempt identity, token details with grouping/zero/unknown values and export, legacy/missing model fallback, channel filtering, reasoning evidence, retained error/header chunk decoding, excluded legacy incoming/received/request/response/WS bodies, safe HTML and long models, real copy/export, confirmations, settings, mobile.');
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
