const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const pageSource = require('./page-source.cjs');
const output = path.resolve(__dirname, '../../artifacts');
const bridge = `<script>(()=>{let n=0;const pending=new Map();window.addEventListener('message',e=>{if(e.data?.type!=='router2api-response')return;const p=pending.get(e.data.id);if(!p)return;pending.delete(e.data.id);e.data.ok?p.resolve(e.data.body):p.reject(new Error(e.data.error))});window.Router2API={request:(method,route,body)=>new Promise((resolve,reject)=>{const id=String(++n);pending.set(id,{resolve,reject});parent.postMessage({type:'router2api-request',id,method,route,body},'*')})}})();</script>`;

function hostMock() {
  window.calls = [];
  window.failDiscovery = false;
  window.emptyDiscovery = false;
  window.holdDiscovery = false;
  window.pendingDiscovery = [];
  window.failToggle = false;
  window.holdToggle = false;
  window.pendingToggle = [];
  window.accounts = [
    { id: 'primary', label: '主力推理渠道', baseUrl: 'https://api.example.test/v1', weight: 100, enabled: true,
      models: ['gpt-5', 'claude-sonnet'], endpoints: ['/v1/responses', '/v1/messages'],
      keyRevision: 1, keySelectionMode: 'roundRobin',
      keys: [{ id: 'key-a', name: '主 Key', masked: '••••1234', enabled: true }],
      headerOverride: {}, requestPolicy: { statusCodeMapping: { 429: 503 } } },
    { id: 'backup', label: 'Claude 工作区', baseUrl: 'https://claude.example.test', weight: 50, enabled: true,
      models: ['claude-opus', 'claude-sonnet'], endpoints: ['/v1/messages'],
      keyRevision: 2, keySelectionMode: 'priority',
      keys: [{ id: 'key-b', name: '工作区 Key', masked: '••••5678', enabled: true }] }
  ];
  let job = 0, cancelled = false, targets = [];
  window.addEventListener('message', event => {
    if (event.source !== document.querySelector('iframe').contentWindow || event.data?.type !== 'router2api-request') return;
    const { id, method, route, body } = event.data;
    window.calls.push({ method, route, body });
    let result, error;
    if (route === 'accounts') result = { accounts: window.accounts };
    else if (route === 'models/discover' || route === 'models/refresh') {
      if (window.failDiscovery) error = '上游返回 401，请检查 Key';
      else result = { models: window.emptyDiscovery ? [] : ['discovered-a', 'discovered-b'] };
    } else if (route === 'accounts/save') {
      result = { account: { ...body, id: body.id || 'new-channel', keys: body.keys.map((k, i) => ({ id: k.id || 'new-' + i, name: k.name, enabled: k.enabled, masked: '••••test' })) } };
      window.accounts = [...window.accounts.filter(a => a.id !== result.account.id), result.account];
    } else if (route === 'accounts/enabled') {
      if (window.failToggle) error = '渠道配置已变化，请刷新后重试';
      else {
        const account = window.accounts.find(a => a.id === body.id);
        result = { account: { ...account, enabled: body.enabled } };
        window.accounts = window.accounts.map(a => a.id === body.id ? result.account : a);
      }
    } else if (route === 'accounts/delete') {
      window.accounts = window.accounts.filter(a => a.id !== body.id); result = {};
    } else if (route === 'tests/start') {
      targets = body.models; cancelled = false; job++; result = { id: 'job-' + job, state: 0 };
    } else if (route === 'tests/cancel') { cancelled = true; result = { accepted: true }; }
    else if (route.startsWith('tests/status')) result = { state: cancelled ? 4 : 1, progress: { rows: [], total: targets.length, completed: 0 } };
    else error = 'Unexpected route: ' + route;
    const respond = () => event.source.postMessage({ type: 'router2api-response', id, ok: !error, body: result, error }, '*');
    if (window.holdDiscovery && route === 'models/discover') window.pendingDiscovery.push(respond);
    else if (window.holdToggle && route === 'accounts/enabled') window.pendingToggle.push(respond);
    else respond();
  });
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const source = pageSource().replace('<head>', '<head>' + bridge);
  const preview = path.join(output, 'ui-host-preview.html');
  fs.writeFileSync(preview, `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>html,body{margin:0;height:100%;}iframe{border:0;width:100%;height:100%;display:block}</style></head><body><iframe title="Plugin" sandbox="allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox"></iframe><script>(${hostMock})();document.querySelector('iframe').srcdoc=${JSON.stringify(source).replace(/</g, '\\u003c')};</script></body></html>`);
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    const errors = []; page.on('pageerror', e => errors.push(e.message));
    page.on('dialog', () => errors.push('Unexpected native browser dialog'));
    await page.goto('file:///' + preview.replaceAll('\\', '/'));
    const frame = page.frameLocator('iframe');
    await frame.locator('#cards .channel-card').first().waitFor();
    assert.equal(await frame.locator('#metricChannels').innerText(), '2');
    assert.equal(await frame.locator('#metricModels').innerText(), '3');
    await page.screenshot({ path: path.join(output, 'ui-overview-desktop.png') });
    const originalCardWidth = (await frame.locator('#cards .channel-card').first().boundingBox()).width;
    await frame.locator('#filter').fill('主力推理渠道');
    assert.equal(await frame.locator('#cards .channel-card').count(), 1);
    const singleCard = await frame.locator('#cards .channel-card').boundingBox();
    assert.ok(singleCard.width <= 480, 'Single channel must remain a compact card');
    assert.ok(Math.abs(singleCard.width - originalCardWidth) < 1, 'Filtering must not stretch cards');
    assert.equal(await frame.locator('.channel-endpoints .endpoint-tag').count(), 2);
    assert.match(await frame.locator('.channel-model-heading').innerText(), /已配置模型.*2 个/s);
    await page.screenshot({ path: path.join(output, 'ui-channel-card-desktop.png') });
    await frame.locator('#filter').fill('');
    const primary = frame.locator('.channel-card').filter({ hasText: '主力推理渠道' });
    await page.evaluate(() => { window.holdToggle = true; });
    await primary.getByRole('button', { name: '禁用渠道「主力推理渠道」', exact: true }).click();
    await primary.getByRole('button', { name: '禁用渠道「主力推理渠道」', exact: true }).filter({ hasText: '处理中' }).waitFor();
    assert.equal(await primary.getByRole('button', { name: '编辑', exact: true }).isDisabled(), true);
    await page.evaluate(() => { window.holdToggle = false; window.pendingToggle.splice(0).forEach(f => f()); });
    await primary.getByRole('button', { name: '启用渠道「主力推理渠道」', exact: true }).waitFor();
    assert.equal(await primary.locator('.badge').innerText(), '已禁用');
    assert.equal(await frame.locator('#metricEnabled').innerText(), '1');
    assert.equal(await frame.locator('#editor').isVisible(), false);
    const toggleCalls = await page.evaluate(() => window.calls.filter(c => c.route === 'accounts/enabled'));
    assert.equal(toggleCalls.length, 1);
    assert.deepEqual(toggleCalls[0].body, { id: 'primary', enabled: false });
    await frame.locator('#statusFilter').selectOption('enabled');
    assert.equal(await primary.count(), 0);
    await frame.locator('#statusFilter').selectOption('disabled');
    assert.equal(await primary.count(), 1);
    await page.screenshot({ path: path.join(output, 'ui-channel-disabled.png') });
    await page.evaluate(() => { window.failToggle = true; });
    await primary.getByRole('button', { name: '启用渠道「主力推理渠道」', exact: true }).click();
    await frame.locator('#message').filter({ hasText: '配置已变化' }).waitFor();
    assert.equal(await primary.locator('.badge').innerText(), '已禁用');
    await page.evaluate(() => { window.failToggle = false; });
    await primary.getByRole('button', { name: '启用渠道「主力推理渠道」', exact: true }).click();
    await frame.locator('#message').filter({ hasText: '已启用' }).waitFor();
    assert.equal(await primary.count(), 0);
    assert.equal(await frame.locator('#metricEnabled').innerText(), '2');
    await frame.locator('#statusFilter').selectOption('all');
    await frame.locator('#add').click();
    await frame.locator('#tab-policy').click();
    assert.equal(await frame.locator('#rateLimitRetryEnabled').isChecked(), false);
    assert.equal(await frame.locator('#emptyResponseRetryEnabled').isChecked(), false);
    assert.equal(await frame.locator('#responseRetrySettings').isVisible(), false);
    await frame.locator('#rateLimitRetryEnabled').check();
    assert.equal(await frame.locator('#responseRetrySettings').isVisible(), true);
    assert.match(await frame.locator('#responseRetrySettings').innerText(), /开始输出后实时透传/);
    assert.match(await frame.locator('#responseRetrySettings').innerText(), /不再重放请求/);
    assert.match(await frame.locator('#responseRetrySettings').innerText(), /保留上游原始状态码/);
    assert.match(await frame.locator('#panel-policy').innerText(), /到期会中断已开始的流/);
    assert.doesNotMatch(await frame.locator('#responseRetrySettings').innerText(), /启用后不实时输出/);
    assert.equal(await frame.locator('#responseMaxRetries').inputValue(), '3');
    assert.equal(await frame.locator('#responseRetryIntervalSeconds').inputValue(), '5');
    await frame.locator('#rateLimitRetryEnabled').uncheck();
    await frame.locator('#emptyResponseRetryEnabled').check();
    assert.equal(await frame.locator('#responseRetrySettings').isVisible(), true);
    await frame.locator('#rateLimitRetryEnabled').check();
    await frame.locator('#responseMaxRetries').fill('4');
    await frame.locator('#responseRetryIntervalSeconds').fill('8');
    await frame.locator('#save').click();
    assert.equal(await frame.locator('#tab-basic').getAttribute('aria-selected'), 'true');
    assert.match(await frame.locator('#formError').innerText(), /渠道名称/);
    await frame.locator('#label').fill('新建测试渠道');
    await frame.locator('#baseUrl').fill('https://draft.example.test/v1');
    await frame.locator('#tab-models').click();
    await frame.locator('#syncProtocols').click();
    await frame.locator('#protocolStatus').filter({ hasText: '请先填写' }).waitFor();
    await frame.locator('#discover').click();
    await frame.locator('#discoveryResponse').filter({ hasText: '请先在 Key 管理' }).waitFor();
    await frame.locator('#tab-keys').click();
    await frame.locator('#keyBatch').fill('draft-test-key');
    await frame.locator('#keyAdd').click();
    await frame.locator('#tab-headers').click();
    assert.equal(await frame.locator('#headerVisual').getAttribute('aria-pressed'), 'true');
    assert.equal(await frame.locator('#headerPass').count(), 0);
    await frame.locator('#templateCodex').click();
    await frame.locator('#headerJson').click();
    let headers = JSON.parse(await frame.locator('#headerOverride').inputValue());
    assert.equal(headers.Originator, 'codex_exec');
    assert.equal('Authorization' in headers, false);
    assert.equal('x-api-key' in headers, false);
    assert.equal(headers['User-Agent'], 'Codex Desktop/0.146.0-alpha.9.2 (Windows 10.0.26200; x86_64) unknown (Codex Desktop; 26.727.51351)');
    assert.equal(headers['X-Codex-Beta-Features'], 'remote_compaction_v2');
    assert.equal(headers['Session-Id'], '{session_id}');
    assert.equal(headers['Thread-Id'], '{thread_id}');
    assert.equal(headers['X-Client-Request-Id'], '{session_id}');
    assert.equal(headers['X-Codex-Window-Id'], '{window_id}');
    assert.equal(headers['X-Codex-Turn-Metadata'], '{codex_turn_metadata}');
    assert.equal('Host' in headers, false);
    await frame.locator('#templateClaude').click();
    await frame.locator('#confirmYes').click();
    headers = JSON.parse(await frame.locator('#headerOverride').inputValue());
    assert.equal(headers['x-app'], 'cli');
    assert.equal(headers['anthropic-version'], '2023-06-01');
    assert.equal('Authorization' in headers, false);
    assert.equal('x-api-key' in headers, false);
    assert.equal(headers['User-Agent'], 'claude-cli/2.1.161 (external, cli)');
    assert.equal(headers['x-claude-code-session-id'], '{session_id}');
    assert.equal(headers['anthropic-dangerous-direct-browser-access'], 'true');
    assert.ok(headers['anthropic-beta'].includes('claude-code-20250219'));
    assert.ok(headers['anthropic-beta'].includes('context-1m-2025-08-07'));
    await frame.locator('#headerOverride').fill('{broken');
    await frame.locator('#headerVisual').click();
    assert.equal(await frame.locator('#headerJson').getAttribute('aria-pressed'), 'true');
    assert.match(await frame.locator('#headerStatus').innerText(), /格式错误/);
    await frame.locator('#headerOverride').fill(JSON.stringify(headers));
    await frame.locator('#headerVisual').click();
    await frame.locator('#headerCopy').click();
    await frame.locator('#copyDialog').waitFor({ state: 'visible' });
    assert.deepEqual(JSON.parse(await frame.locator('#copyContent').inputValue()), headers);
    await frame.locator('#copyClose').click();
    await page.screenshot({ path: path.join(output, 'ui-headers-desktop.png') });

    await frame.locator('#tab-models').click();
    await frame.locator('#discover').click();
    await frame.locator('#discoveryStatus').filter({ hasText: '已获取 2' }).waitFor();
    const discovery = await page.evaluate(() => window.calls.filter(c => c.route === 'models/discover').at(-1).body);
    assert.equal(discovery.id, null);
    assert.equal(discovery.baseUrl, 'https://draft.example.test/v1');
    assert.equal(discovery.apiKey, 'draft-test-key');
    assert.deepEqual(discovery.headerOverride, headers);
    await frame.locator('#candidates').getByRole('button', { name: 'discovered-a', exact: true }).click();
    assert.equal(await frame.locator('#models').inputValue(), 'discovered-a');
    assert.equal(await frame.locator('#modelProtocols .protocol-row').count(), 1);
    await frame.locator('#syncProtocols').click();
    assert.match(await frame.locator('#protocolStatus').innerText(), /已同步 1/);
    await frame.locator('#modelProtocols input[type=checkbox]').first().check();
    await frame.locator('#modelProtocols').getByLabel('Anthropic Messages', { exact: true }).check();
    await frame.locator('#modelProtocols').getByLabel('discovered-a 首选协议').selectOption('messages');
    await page.evaluate(() => { window.failDiscovery = true; });
    await frame.locator('#discover').click();
    await frame.locator('#discoveryResponse').filter({ hasText: '上游返回 401' }).waitFor();
    await frame.locator('#copyDiscoveryResponse').click();
    await frame.locator('#copyDialog').waitFor({ state: 'visible' });
    assert.equal(await frame.locator('#copyTitle').textContent(), '复制响应详情');
    assert.equal(await frame.locator('#copyContent').inputValue(), '上游返回 401，请检查 Key');
    await frame.locator('#copyClose').click();
    await page.evaluate(() => { window.failDiscovery = false; window.emptyDiscovery = true; });
    await frame.locator('#discover').click();
    await frame.locator('#discoveryStatus').filter({ hasText: '空模型列表' }).waitFor();
    await page.evaluate(() => { window.emptyDiscovery = false; });
    assert.equal(await frame.locator('#models').inputValue(), 'discovered-a');

    await frame.locator('#tab-policy').click();
    assert.equal(await frame.locator('#mappingVisual').getAttribute('aria-pressed'), 'true');
    await frame.locator('#mappingAdd').click();
    assert.equal(await frame.locator('#mappingRows select').count(), 0);
    assert.equal(await frame.getByLabel('映射 1 原始状态码').getAttribute('inputmode'), 'numeric');
    await frame.locator('#mappingJson').click();
    assert.match(await frame.locator('#mappingStatus').innerText(), /请输入/);
    await frame.getByLabel('映射 1 原始状态码').fill('429');
    for (const invalid of ['abc', '199', '600', '503.0', '5e2']) {
      await frame.getByLabel('映射 1 目标状态码').fill(invalid);
      await frame.locator('#mappingJson').click();
      assert.match(await frame.locator('#mappingStatus').innerText(), /三位整数/);
    }
    await frame.getByLabel('映射 1 目标状态码').fill('503');
    await frame.locator('#mappingJson').click();
    assert.deepEqual(JSON.parse(await frame.locator('#statusCodeMapping').inputValue()), { 429: 503 });
    await frame.locator('#statusCodeMapping').fill('{"418":502,"429":503}');
    await frame.locator('#mappingVisual').click();
    assert.equal(await frame.locator('#mappingRows .mapping-row').count(), 2);
    await frame.locator('#mappingAdd').click();
    await frame.getByLabel('映射 3 原始状态码').fill('429');
    await frame.getByLabel('映射 3 目标状态码').fill('500');
    await frame.locator('#mappingJson').click();
    assert.match(await frame.locator('#mappingStatus').innerText(), /重复/);
    await frame.getByLabel('删除映射 3', { exact: true }).click();
    await frame.locator('#mappingJson').click();
    await frame.locator('#statusCodeMapping').fill('{"400":204}');
    await frame.locator('#mappingVisual').click();
    assert.match(await frame.locator('#mappingStatus').innerText(), /目标不能/);
    await frame.locator('#statusCodeMapping').fill('{"418":502,"429":503}');
    await frame.locator('#mappingVisual').click();
    await page.screenshot({ path: path.join(output, 'ui-mapping-desktop.png') });

    await frame.locator('#save').click();
    await frame.locator('#editor').waitFor({ state: 'hidden' });
    const saved = await page.evaluate(() => window.calls.filter(c => c.route === 'accounts/save').at(-1).body);
    assert.deepEqual(saved.requestPolicy.statusCodeMapping, { 418: 502, 429: 503 });
    assert.equal(saved.requestPolicy.rateLimitRetryEnabled, true);
    assert.equal(saved.requestPolicy.emptyResponseRetryEnabled, true);
    assert.equal(saved.requestPolicy.responseMaxRetries, 4);
    assert.equal(saved.requestPolicy.responseRetryIntervalSeconds, 8);
    assert.deepEqual(saved.headerOverride, headers);
    assert.equal(saved.modelProtocols['discovered-a'].preferredProtocol, 'messages');
    assert.equal(saved.keys[0].secret, 'draft-test-key');
    await frame.locator('#cards .channel-card').last().getByRole('button', { name: '编辑', exact: true }).click();
    await frame.locator('#tab-policy').click();
    assert.equal(await frame.locator('#rateLimitRetryEnabled').isChecked(), true);
    assert.equal(await frame.locator('#emptyResponseRetryEnabled').isChecked(), true);
    assert.equal(await frame.locator('#responseMaxRetries').inputValue(), '4');
    assert.equal(await frame.locator('#responseRetryIntervalSeconds').inputValue(), '8');
    await page.setViewportSize({ width: 390, height: 844 });
    assert.equal(await frame.locator('#editor').evaluate(d => d.scrollWidth <= d.clientWidth + 1), true);
    await page.screenshot({ path: path.join(output, 'ui-response-retry-mobile.png') });
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.screenshot({ path: path.join(output, 'ui-response-retry-desktop.png') });
    await frame.locator('#close').click();

    await frame.locator('#cards .channel-card').first().getByRole('button', { name: '测试连接', exact: true }).click();
    await frame.locator('#testAll').click();
    await frame.locator('#confirmDialog').waitFor({ state: 'visible' });
    await frame.locator('#confirmYes').click();
    await frame.locator('#testProgress').filter({ hasText: '测试中' }).waitFor();
    await frame.locator('#testCancel').click();
    await frame.locator('#testProgress').filter({ hasText: '已取消' }).waitFor();
    await frame.locator('#testClose').click();
    await frame.locator('#cards .channel-card').last().getByRole('button', { name: '删除', exact: true }).click();
    await frame.locator('#confirmNo').click();
    assert.equal(await frame.locator('#cards .channel-card').count(), 3);
    await frame.locator('#cards .channel-card').last().getByRole('button', { name: '删除', exact: true }).click();
    await frame.locator('#confirmYes').click();
    await page.waitForFunction(() => window.accounts.length === 2);

    await frame.locator('#cards .channel-card').first().getByRole('button', { name: '编辑', exact: true }).click();
    await frame.locator('#tab-models').click();
    await page.evaluate(() => { window.holdDiscovery = true; });
    await frame.locator('#discover').click();
    await page.waitForFunction(() => window.pendingDiscovery.length === 1);
    await frame.locator('#close').click();
    await frame.locator('#cards .channel-card').last().getByRole('button', { name: '编辑', exact: true }).click();
    await frame.locator('#tab-models').click();
    assert.equal(await frame.locator('#discover').isEnabled(), true);
    await page.evaluate(() => { window.holdDiscovery = false; window.pendingDiscovery.forEach(fn => fn()); window.pendingDiscovery = []; });
    await page.waitForTimeout(100);
    assert.equal(await frame.locator('#candidates button').count(), 0);
    assert.equal(await frame.locator('#discoveryStatus').textContent(), '');
    await frame.locator('#close').click();

    await page.setViewportSize({ width: 390, height: 844 });
    await frame.locator('#cards .channel-card').first().getByRole('button', { name: '编辑', exact: true }).click();
    for (const tab of ['basic', 'keys', 'models', 'headers', 'policy']) {
      await frame.locator('#tab-' + tab).click();
      const layout = await frame.locator('#editor').evaluate(d => {
        const footer = d.querySelector('.editor-footer').getBoundingClientRect();
        return { horizontal: d.scrollWidth <= d.clientWidth + 1, footer: footer.bottom <= innerHeight && footer.top >= 0 };
      });
      assert.deepEqual(layout, { horizontal: true, footer: true }, tab);
      assert.equal(await frame.locator('[role=tabpanel]:visible').count(), 1);
    }
    await page.screenshot({ path: path.join(output, 'ui-mapping-mobile.png') });
    await frame.locator('#tab-models').click();
    await page.screenshot({ path: path.join(output, 'ui-models-mobile.png') });
    await page.setViewportSize({ width: 390, height: 420 });
    const compactFooter = await frame.locator('.editor-footer').boundingBox();
    assert.ok(compactFooter.y + compactFooter.height <= 420);
    const labels = await frame.locator('label,button,h1,h2,h3,h4').allTextContents();
    assert.ok(labels.every(s => !/[（）]/.test(s)), 'Parenthetical UI labels remain');
    assert.deepEqual(errors, []);
    console.log('Sandboxed UI passed: templates, visual/JSON editors, draft discovery, feedback, protocol sync, confirmations, testing, responsive tabs.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
