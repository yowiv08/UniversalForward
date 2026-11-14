// Exercise native Chat configuration and paged connection results through the management bridge.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

(async () => {
  const output = path.resolve(__dirname, '../../artifacts/chat-completions/browser');
  fs.mkdirSync(output, { recursive: true });
  const preview = path.join(output, 'preview.html');
  fs.writeFileSync(preview, require('./page-source.cjs')());
  const browser = await chromium.launch({ headless: true, executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      window.calls = []; window.account = null;
      let rows = [], raw = '', version = 0;
      window.Router2API = { request: async (method, route, body) => {
        window.calls.push({ method, route, body: structuredClone(body) });
        if (route === 'appearance/settings') return { mode: 'light' };
        if (route === 'execution/settings') return { defaultHostAttempts: 1, execution: { setupTimeoutSeconds: 300, totalSetupTimeoutSeconds: 180, responseTimeoutSeconds: 600 } };
        if (route === 'network/proxies') return { subscriptions: [], nodes: [] };
        if (route === 'accounts') return { accounts: window.account ? [structuredClone(window.account)] : [] };
        if (route === 'accounts/save') {
          const previous = window.account;
          window.account = { ...structuredClone(body), id: 'chat-channel', keyRevision: (previous?.keyRevision || 0) + 1,
            totalKeyCount: body.keys.length, enabledKeyCount: body.keys.filter(key => key.enabled).length,
            keys: body.keys.map((key, index) => ({ ...key, id: key.id || 'key-' + index,
              secret: key.secret || previous?.keys.find(old => old.id === key.id)?.secret })) };
          return { account: structuredClone(window.account) };
        }
        if (route === 'tests/start') {
          version++;
          rows = body.models.map((model, index) => ({ model, keyId: 'key-0', keyName: 'test',
            endpoint: body.endpoint || '/v1/chat/completions', networkMode: 'direct', transport: 'http',
            jobId: 'job', rowId: index, state: 'succeeded', success: true, originalStatus: 200, mappedStatus: 200,
            durationMs: 20, firstEventMs: body.stream ? 8 : null, retries: 0, responseAvailable: true }));
          raw = body.stream ? 'data: {"choices":[{"delta":{"reasoning_content":"Thinking","content":"OK"},"finish_reason":"stop"}]}\n\ndata: [DONE]\n\n'
            : '{"choices":[{"message":{"role":"assistant","content":"OK","reasoning_content":"Thinking"},"finish_reason":"stop"}]}';
          return { id: 'job', state: 'Queued' };
        }
        if (route.startsWith('tests/status')) return { state: 'Completed', result: { total: rows.length, completed: rows.length, resultVersion: version } };
        if (route.startsWith('tests/results')) return { rows: structuredClone(rows), total: rows.length, nextOffset: rows.length, resultVersion: version };
        if (route.startsWith('tests/detail')) return { text: raw, done: true, nextOffset: raw.length, truncated: false, evicted: false };
        throw Error('Unexpected API ' + route);
      } };
    });
    await page.goto('file:///' + preview.replaceAll('\\', '/'));
    await page.locator('#add').click();
    assert.equal(await page.locator('#endpoints input:checked').count(), 3);
    await page.locator('#label').fill('Chat Completions 测试');
    await page.locator('#baseUrl').fill('https://upstream.example/v1/chat/completions');
    await page.locator('input[name=endpoints][value="/v1/responses"]').uncheck();
    await page.locator('input[name=endpoints][value="/v1/messages"]').uncheck();
    await page.locator('#responsesTransport').selectOption('websocket');
    await page.locator('#stripResponsesResourceState').check();
    await page.locator('#tab-keys').click();
    await page.locator('#keyBatch').fill('sk-demo-test'); await page.locator('#keyAdd').click();
    await page.locator('#tab-models').click();
    await page.locator('#models').fill('gpt-5.4');
    const model = page.locator('.protocol-row');
    await model.getByLabel('指定支持协议', { exact: true }).check();
    assert.equal(await model.getByLabel('Chat Completions', { exact: true }).isChecked(), true);
    assert.equal(await model.getByLabel('gpt-5.4 首选协议').inputValue(), 'chat');
    await model.getByLabel('强制原生', { exact: true }).check();
    await page.locator('#tab-reasoning').click();
    await page.locator('#reasoningProtocol').selectOption('chat');
    await page.locator('#reasoningMode .reasoning-mode-card:has(input[value="fixed"])').click();
    await page.locator('#reasoningFixed').fill('high');
    assert.match(await page.locator('#reasoningPreview').innerText(), /Chat Completions/);
    for (const protocol of ['messages', 'responses']) {
      await page.locator('#reasoningProtocol').selectOption(protocol);
      assert.equal(await page.locator('#reasoningMode input:checked').inputValue(), 'off');
    }
    await page.locator('#reasoningProtocol').selectOption('chat');
    assert.equal(await page.locator('#reasoningFixed').inputValue(), 'high');
    await page.locator('#tab-prompt').click();
    await page.locator('#systemPromptMode .reasoning-mode-card:has(input[value="append"])').click();
    await page.locator('#systemPromptText').fill('Keep conversation history.');
    await page.locator('#tab-headers').click();
    await page.locator('#headerPerEndpoint').click();
    await page.locator('[data-header-scope="/v1/chat/completions"]').click();
    assert.equal(await page.locator('#headerScopeTitle').innerText(), 'Chat Completions');
    assert.equal(await page.locator('#headerUseCommon').isChecked(), true);
    await page.locator('#headerUseCommon').uncheck();
    await page.locator('#headerJson').click();
    await page.locator('#headerOverride').fill(JSON.stringify({ 'X-Protocol': 'chat', Authorization: 'Bearer {api_key}' }));
    const save = async () => { await page.locator('#save').click(); await page.locator('#editor').waitFor({ state: 'hidden' }); };
    await save();
    const saved = await page.evaluate(() => window.calls.filter(call => call.route === 'accounts/save').at(-1).body);
    assert.deepEqual(saved.endpoints, ['/v1/chat/completions']);
    assert.deepEqual(saved.modelProtocols['gpt-5.4'], { protocols: ['chat'], preferredProtocol: 'chat', nativeOnly: true });
    assert.equal(saved.reasoningPolicy.defaults.chat.fixedEffort, 'high');
    assert.equal(saved.endpointHeaderOverrides['/v1/chat/completions'].headers['X-Protocol'], 'chat');
    assert.equal(saved.systemPromptPolicy.text, 'Keep conversation history.');
    // Reopen after saving, and check the added controls at desktop and narrow mobile sizes.
    await page.getByRole('button', { name: '编辑', exact: true }).click();
    assert.equal(await page.locator('#endpoints input:checked').count(), 1);
    for (const width of [1280, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      for (const tab of ['basic', 'models', 'headers', 'reasoning']) {
        await page.locator('#tab-' + tab).click();
        if (tab === 'basic') await page.locator('#endpoints').scrollIntoViewIfNeeded();
        if (tab === 'headers') {
          await page.locator('[data-header-scope="/v1/chat/completions"]').click();
          assert.equal(await page.locator('#headerScopeTitle').innerText(), 'Chat Completions');
        }
        if (tab === 'reasoning') await page.locator('#reasoningProtocol').selectOption('chat');
        assert.equal(await page.locator('#editor').evaluate(dialog => dialog.scrollWidth <= dialog.clientWidth), true, `${tab} at ${width}`);
        await page.screenshot({ path: path.join(output, `${tab}-${width}.png`) });
      }
    }
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.locator('#cancel').click();
    await page.getByRole('button', { name: '测试连接', exact: true }).click();
    assert.deepEqual(await page.locator('#testEndpoint option').evaluateAll(options => options.map(option => option.value)), ['', '/v1/chat/completions']);
    await page.locator('#testAll').click(); await page.locator('#confirmYes').click();
    await page.waitForFunction(() => document.querySelector('#testProgress').textContent.includes('已完成'));
    assert.equal(await page.locator('#testRows .result-endpoint').innerText(), '/v1/chat/completions');
    assert.equal(await page.evaluate(() => window.calls.filter(call => call.route === 'tests/start').at(-1).body.endpoint), null);
    await page.getByRole('button', { name: '详情', exact: true }).click();
    await page.locator('#testDetails').filter({ hasText: 'Thinking' }).waitFor();
    assert.match(await page.locator('#testDetailOverview').innerText(), /\/v1\/chat\/completions/);
    await page.locator('#testDetailClose').click();
    await page.locator('#testConfiguration summary').click();
    await page.locator('#testEndpoint').selectOption('/v1/chat/completions');
    await page.locator('#testStream').check();
    await page.locator('#testAll').click(); await page.locator('#confirmYes').click();
    await page.waitForFunction(() => window.calls.filter(call => call.route === 'tests/start').length === 2 && document.querySelector('#testProgress').textContent.includes('已完成'));
    await page.getByRole('button', { name: '详情', exact: true }).click();
    await page.locator('#testDetails').filter({ hasText: '[DONE]' }).waitFor();
    assert.match(await page.locator('#testDetails').innerText(), /reasoning_content/);
    assert.equal(await page.locator('#journalAdvanced select[name=endpoint] option').filter({ hasText: '/v1/chat/completions' }).count(), 1);
    assert.deepEqual(errors, []);
    console.log('Chat Completions UI passed: create/save/reopen, protocol defaults, independent headers and reasoning, JSON/SSE tests, desktop/mobile layout.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
