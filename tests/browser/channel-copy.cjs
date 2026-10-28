const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const { bridge, assertContrast } = require('./settings-fixture.cjs');
const source = require('./page-source.cjs')().replace('<head>', '<head>' + bridge);
const output = path.resolve(__dirname, '../../artifacts/v0.3.7/ui');

function copyHost() {
  const rule = (mode, mappings = [], defaultEffort = null, fixedEffort = null) => ({ mode, mappings, defaultEffort, fixedEffort });
  window.copyState = { calls: [], mode: 'light', failSave: false, holdSave: false, nextId: 1, accounts: [{
    id: 'source-channel', label: '生产渠道', baseUrl: 'https://copy.example/v1', enabled: true, state: 'CoolingDown', weight: 73,
    networkMode: 'proxyPool', proxySubscriptionIds: ['subscription-a'], proxyId: 'proxy-a',
    responsesTransport: 'websocket', stripResponsesResourceState: true,
    keySelectionMode: 'priority', keyRevision: 17,
    keys: [
      { id: 'source-key-a', name: '主力', secret: 'sk-copy-source-0001', enabled: true },
      { id: 'source-key-b', name: '备用', secret: 'sk-copy-source-0002', enabled: false },
      { id: 'source-key-c', name: '0003', secret: 'sk-copy-source-0003', enabled: true }
    ],
    models: ['gpt-model', 'claude-model'], endpoints: ['/v1/responses', '/v1/messages'],
    modelProtocols: {
      'gpt-model': { protocols: ['responses', 'messages'], preferredProtocol: 'responses', nativeOnly: true },
      'claude-model': { protocols: ['messages'], preferredProtocol: 'messages', nativeOnly: false }
    },
    reasoningPolicy: { defaults: { responses: rule('map', [{ from: 'low', to: 'high' }], 'medium') },
      models: { 'claude-model': { messages: rule('fixed', [], null, 'max') } } },
    systemPromptPolicy: { mode: 'append', text: '  保留完整提示词。\n先给结论。  ' },
    headerOverrideMode: 'perEndpoint', headerOverride: { 'X-Shared': 'common', 'X-Key': '{api_key}' },
    endpointHeaderOverrides: {
      '/v1/responses': { useCommon: false, headers: { 'X-Responses': 'custom' } },
      '/v1/messages': { useCommon: true, headers: { 'X-Dormant': 'preserve' } }
    },
    requestPolicy: { maxRetries: 2, headerTimeoutSeconds: 45, totalTimeoutSeconds: 240, streamIdleTimeoutSeconds: 30,
      rateLimitRetryEnabled: true, emptyResponseRetryEnabled: true, responseMaxRetries: 4, responseRetryIntervalSeconds: 7,
      retryStatusCodes: '429,500-503,520', statusCodeMapping: { '502': 503 } },
    extraParams: { apiKeyHeader: 'X-Api-Key', apiKeyPrefix: '', custom: { nested: ['preserve'] } },
    availableModels: ['cached-model-not-configured'], modelsUpdatedAt: '2026-10-25T00:00:00Z'
  }] };
  addEventListener('message', event => {
    if (event.source !== document.querySelector('iframe').contentWindow || event.data?.type !== 'router2api-request') return;
    const { id, method, route, body } = event.data, state = window.copyState;
    state.calls.push({ method, route, body });
    const execute = () => {
      let result;
      try {
        if (route === 'accounts') result = { accounts: state.accounts };
        else if (route === 'appearance/settings') {
          if (method === 'POST') state.mode = body.mode;
          result = { mode: state.mode };
        } else if (route === 'execution/settings') result = { defaultHostAttempts: 3,
          execution: { setupTimeoutSeconds: 300, totalSetupTimeoutSeconds: 180, responseTimeoutSeconds: 600 } };
        else if (route === 'network/proxies') result = {
          subscriptions: [{ id: 'subscription-a', name: '订阅 A', enabled: true }],
          nodes: [{ id: 'proxy-a', subscriptionId: 'subscription-a', address: 'http://proxy.example:8080', available: true, state: 'Active' }]
        };
        else if (route === 'models/discover') result = { models: ['discovered-model'] };
        else if (route === 'accounts/save') {
          if (state.failSave) throw Error('模拟保存失败');
          if (body.id || body.keyRevision !== 0 || body.deletedKeyIds.length || body.keys.some(key => key.id || !key.secret))
            throw Error('副本必须作为全新渠道提交完整密钥');
          const channelId = 'copy-' + state.nextId++;
          const account = { ...body, id: channelId, state: 'Active', keyRevision: 1,
            keys: body.keys.map((key, index) => ({ ...key, id: channelId + '-key-' + index })) };
          delete account.deletedKeyIds;
          state.accounts.push(account); result = { account };
        } else throw Error('Unexpected route: ' + route);
        event.source.postMessage({ type: 'router2api-response', id, ok: true, body: result }, '*');
      } catch (error) { event.source.postMessage({ type: 'router2api-response', id, ok: false, error: error.message }, '*'); }
    };
    if (state.holdSave && route === 'accounts/save') state.releaseSave = execute; else execute();
  });
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>html,body{margin:0}iframe{border:0;width:100%;height:100vh;display:block}</style></head><body><iframe sandbox="allow-scripts allow-forms"></iframe><script>(${copyHost})();document.querySelector('iframe').srcdoc=${JSON.stringify(source).replaceAll('<', '\\u003c')};</script></body></html>`;
  const server = http.createServer((_, response) => { response.setHeader('Content-Type', 'text/html; charset=utf-8'); response.end(html); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1040 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    const frame = page.frameLocator('iframe'), field = id => frame.locator('#' + id);
    const menu = frame.locator('.channel-menu:not([hidden])');
    const sourceCard = () => frame.locator('.channel-card').first();
    const openCopy = async () => {
      await sourceCard().locator('.channel-menu-trigger').click();
      await menu.getByRole('menuitem', { name: '复制', exact: true }).click();
      await field('editor').waitFor();
    };
    const savedCalls = () => page.evaluate(() => copyState.calls.filter(call => call.route === 'accounts/save'));
    const unchangedSource = async () => assert.deepEqual(await page.evaluate(() => copyState.accounts[0]), original);
    await sourceCard().waitFor();
    const original = await page.evaluate(() => copyState.accounts[0]);

    // The menu remains navigable and copying is a local, cancellable draft.
    await sourceCard().locator('.channel-menu-trigger').click();
    assert.deepEqual(await menu.getByRole('menuitem').allTextContents(), ['日志', '复制', '删除']);
    await page.keyboard.press('ArrowDown');
    assert.equal(await menu.getByRole('menuitem', { name: '复制', exact: true }).evaluate(node => node === document.activeElement), true);
    await page.keyboard.press('Enter');
    assert.equal(await field('formTitle').innerText(), '复制渠道 · 生产渠道');
    assert.equal(await field('label').inputValue(), '生产渠道 - 副本');
    assert.equal(await frame.locator('#accountForm [name=id]').inputValue(), '');
    assert.equal(await frame.locator('#accountForm [name=enabled]').isChecked(), false);
    assert.match(await field('formDescription').innerText(), /默认禁用.*保存后创建/);
    assert.equal(await field('baseUrl').inputValue(), original.baseUrl);
    assert.equal(await field('weight').inputValue(), '73');
    assert.equal(await field('proxyScope').inputValue(), 'node');
    assert.equal(await field('proxyNode').inputValue(), 'proxy-a');
    assert.equal(await field('responsesTransport').inputValue(), 'websocket');
    assert.equal(await field('stripResponsesResourceState').isChecked(), true);
    await field('tab-keys').click();
    assert.deepEqual(await frame.locator('#keyRows .key-secret-cell input').evaluateAll(nodes => nodes.map(node => node.value)), original.keys.map(key => key.secret));
    assert.deepEqual(await frame.locator('#keyRows .key-enabled input').evaluateAll(nodes => nodes.map(node => node.checked)), [true, false, true]);
    assert.equal(await field('keySelectionMode').inputValue(), 'priority');
    await field('tab-models').click();
    assert.equal(await field('models').inputValue(), original.models.join('\n'));
    assert.equal(await field('candidates').innerText(), '');
    assert.equal(await field('refreshModels').isDisabled(), true);
    await field('cancel').click();
    assert.equal(await sourceCard().locator('.channel-menu-trigger').evaluate(node => node === document.activeElement), true);
    assert.equal((await savedCalls()).length, 0);
    await unchangedSource();

    // Nested edits and discarded key removals must not mutate the source or leak into a later copy.
    await openCopy();
    await field('tab-models').click();
    await frame.getByLabel('gpt-model 首选协议', { exact: true }).selectOption('messages');
    await field('tab-headers').click(); await field('headerJson').click();
    await field('headerOverride').fill('{"X-Draft":"changed"}');
    await field('tab-reasoning').click(); await field('reasoningDefault').fill('low');
    await field('tab-policy').click(); await field('maxRetries').fill('5');
    await field('tab-keys').click();
    await frame.locator('#keyRows .key-row').nth(1).getByRole('button', { name: '删除 Key' }).click();
    await field('confirmYes').click();
    await field('cancel').click();
    await sourceCard().getByRole('button', { name: '编辑', exact: true }).click();
    assert.equal(await field('formDescription').innerText(), '配置渠道连接与转发规则');
    await field('tab-models').click();
    assert.equal(await frame.getByLabel('gpt-model 首选协议', { exact: true }).inputValue(), 'responses');
    await field('tab-policy').click(); assert.equal(await field('maxRetries').inputValue(), '2');
    await field('tab-keys').click(); assert.equal(await frame.locator('#keyRows .key-row').count(), 3);
    await field('cancel').click(); await unchangedSource();

    // Draft model discovery sends the new draft's connection and full selected key, never a source ID.
    await openCopy(); await field('baseUrl').fill('https://draft.example/v1');
    await field('tab-models').click();
    await field('discoveryKey').selectOption({ label: '0003' }); await field('discover').click();
    await field('discoveryStatus').filter({ hasText: '已获取' }).waitFor();
    const discovery = await page.evaluate(() => copyState.calls.filter(call => call.route === 'models/discover').at(-1).body);
    assert.equal(discovery.id, null); assert.equal(discovery.keyId, undefined);
    assert.equal(discovery.baseUrl, 'https://draft.example/v1'); assert.equal(discovery.apiKey, original.keys[2].secret);
    assert.deepEqual(discovery.headerOverride, original.headerOverride); assert.deepEqual(discovery.extraParams, original.extraParams);
    assert.equal(await field('refreshModels').isDisabled(), true);
    await field('cancel').click(); assert.equal((await savedCalls()).length, 0);

    // Save all configuration through the existing create endpoint, with independent IDs and failure recovery.
    await openCopy(); await page.evaluate(() => { copyState.failSave = true; });
    await field('save').click(); await field('formError').filter({ hasText: '模拟保存失败' }).waitFor();
    assert.equal(await field('editor').isVisible(), true);
    assert.equal(await field('label').inputValue(), '生产渠道 - 副本');
    assert.equal(await page.evaluate(() => copyState.accounts.length), 1);
    const submitted = (await savedCalls()).at(-1).body;
    assert.equal(submitted.id, null); assert.equal(submitted.keyRevision, 0); assert.deepEqual(submitted.deletedKeyIds, []);
    assert.equal(submitted.enabled, false);
    assert.deepEqual(submitted.keys, original.keys.map(key => ({ ...key, id: '' })));
    for (const name of ['baseUrl', 'weight', 'networkMode', 'proxySubscriptionIds', 'proxyId', 'responsesTransport',
      'stripResponsesResourceState', 'keySelectionMode', 'models', 'endpoints', 'modelProtocols', 'reasoningPolicy', 'systemPromptPolicy',
      'headerOverride', 'headerOverrideMode', 'endpointHeaderOverrides', 'requestPolicy', 'extraParams'])
      assert.deepEqual(submitted[name], original[name], 'Preserve ' + name);
    for (const name of ['state', 'availableModels', 'modelsUpdatedAt']) assert.equal(name in submitted, false);
    await page.evaluate(() => { copyState.failSave = false; copyState.holdSave = true; });
    await field('save').click();
    await page.waitForFunction(() => typeof copyState.releaseSave === 'function');
    assert.equal(await field('save').isDisabled(), true);
    await field('save').evaluate(button => button.form.requestSubmit());
    assert.equal((await savedCalls()).length, 2, 'Repeated submit while saving must not create a second copy');
    await page.evaluate(() => { copyState.holdSave = false; copyState.releaseSave(); });
    await field('editor').waitFor({ state: 'hidden' }); await frame.locator('.channel-card').nth(1).waitFor();
    const created = await page.evaluate(() => copyState.accounts[1]);
    assert.notEqual(created.id, original.id);
    assert.equal(new Set(created.keys.map(key => key.id)).size, 3);
    assert.equal(created.keys.some(key => original.keys.some(old => old.id === key.id)), false);
    assert.equal(created.enabled, false);
    assert.equal(await frame.locator('.channel-card').nth(1).getAttribute('data-enabled'), 'false');
    await unchangedSource();

    await openCopy(); assert.equal(await field('label').inputValue(), '生产渠道 - 副本 2'); await field('cancel').click();
    await frame.locator('.channel-card').nth(1).getByRole('button', { name: '编辑', exact: true }).click();
    await field('label').fill('修改副本'); await field('tab-keys').click();
    await frame.locator('#keyRows .key-secret-cell input').first().fill('sk-copy-changed');
    await field('cancel').click(); await unchangedSource();

    // Name collision checks include filtered-out channels; truncate without splitting a surrogate pair.
    await page.evaluate(() => {
      copyState.accounts[0].label = 'x'.repeat(114) + '😀yz';
      copyState.accounts.push({ ...structuredClone(copyState.accounts[0]), id: 'name-collision', label: 'x'.repeat(114) + ' - 副本', enabled: false });
    });
    await field('reload').click();
    await frame.getByRole('button', { name: /^编辑渠道「x/ }).first().waitFor();
    await field('statusFilter').selectOption('enabled');
    await openCopy();
    const longName = await field('label').inputValue();
    assert.equal(longName, 'x'.repeat(113) + ' - 副本 2');
    assert.ok(longName.length <= 120 && longName.isWellFormed());
    await field('cancel').click();
    await page.evaluate(() => { copyState.accounts[0].label = '生产渠道'; });
    await field('reload').click(); await frame.getByRole('button', { name: '编辑渠道「生产渠道」', exact: true }).waitFor();
    await field('statusFilter').selectOption('all');

    // Both views use the same menu, with readable colors and reachable controls inside a real opaque sandbox.
    for (const mode of ['light', 'dark']) {
      await frame.locator(`.workspace-header [data-appearance-mode=${mode}]`).click();
      await field('appearanceStatus').waitFor({ state: 'hidden' });
      for (const view of ['Grid', 'List']) {
        await field('view' + view).click();
        await sourceCard().locator('.channel-menu-trigger').click();
        await menu.getByRole('menuitem', { name: '复制', exact: true }).hover();
        await assertContrast(frame, '.channel-menu:not([hidden])');
        await page.screenshot({ path: path.join(output, `copy-menu-${mode}-${view.toLowerCase()}.png`) });
        await menu.getByRole('menuitem', { name: '复制', exact: true }).click();
        await assertContrast(frame, '#editor');
        if (view === 'Grid') await page.screenshot({ path: path.join(output, `copy-draft-${mode}.png`) });
        await field('cancel').click();
      }
    }
    for (const width of [390, 320]) {
      await page.setViewportSize({ width, height: 844 }); await field('viewGrid').click();
      await sourceCard().locator('.channel-menu-trigger').click();
      const bounds = await menu.boundingBox();
      assert.ok(bounds.x >= 0 && bounds.y >= 0 && bounds.x + bounds.width <= width && bounds.y + bounds.height <= 844);
      await menu.getByRole('menuitem', { name: '复制', exact: true }).click();
      assert.equal(await field('editor').evaluate(node => node.scrollWidth <= node.clientWidth), true);
      await assertContrast(frame, '#editor');
      await page.screenshot({ path: path.join(output, `copy-draft-dark-${width}.png`) });
      await field('tab-keys').click();
      assert.equal(await field('editor').evaluate(node => node.scrollWidth <= node.clientWidth), true);
      await page.screenshot({ path: path.join(output, `copy-keys-dark-${width}.png`) });
      await field('cancel').click();
    }
    assert.deepEqual(errors, []);
    assert.equal(await page.evaluate(() => copyState.calls.some(call => call.route === 'models/refresh' || call.route.startsWith('logs') || call.route.startsWith('tests/'))), false);
    console.log('Channel copy passed: independent draft, full configuration and keys, new IDs, cancellation, discovery, save failure and duplicate submission, unique bounded names, card/list menus, keyboard, contrast and sandbox mobile layouts.');
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
