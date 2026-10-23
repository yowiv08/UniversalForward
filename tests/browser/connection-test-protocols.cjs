// Verify protocol defaults and displayed results through the management bridge, without a real upstream.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const html = require('./page-source.cjs')();

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      const account = {
        id: 'mixed', label: '混合模型渠道', baseUrl: 'https://example.test/v1', enabled: true, weight: 100,
        endpoints: ['/v1/responses', '/v1/messages'], models: ['anthropic/claude-sonnet-4-6', 'gpt-5.4'],
        keys: [{ id: 'test', name: 'test', secret: 'sk-demo-test', enabled: true }], keyRevision: 1,
        keySelectionMode: 'roundRobin', enabledKeyCount: 1, totalKeyCount: 1,
        modelProtocols: {}, responsesTransport: 'websocket'
      };
      window.calls = [];
      let rows = [], job = 0;
      window.Router2API = { request: async (method, route, body) => {
        window.calls.push({ method, route, body });
        if (route === 'accounts') return { accounts: [structuredClone(account)] };
        if (route === 'accounts/save') {
          account.modelProtocols = structuredClone(body.modelProtocols);
          account.endpoints = body.endpoints;
          return { account: structuredClone(account) };
        }
        if (route === 'tests/start') {
          job++;
          rows = body.models.map(model => {
            // Fixtures represent backend-selected endpoints; this suite tests their UI presentation.
            const endpoint = body.endpoint || (model === 'gpt-5.4' ? '/v1/responses' : '/v1/messages');
            return { model, keyId: 'test', keyName: 'test', endpoint, networkMode: 'direct', success: true,
              originalStatus: 200, mappedStatus: 200, durationMs: 20, retries: 0,
              transport: endpoint === '/v1/responses' ? 'websocket' : 'http',
              response: endpoint === '/v1/messages' ? '{"content":[{"type":"text","text":"OK"}]}' : '{"status":"completed","output":[{"type":"message"}]}' };
          });
          return { id: 'job-' + job, state: 0 };
        }
        if (route.startsWith('tests/status')) return { state: 2, result: { rows } };
        throw Error('Unexpected API ' + route);
      } };
    });
    const output = path.resolve(__dirname, '../../artifacts/connection-test-protocols');
    fs.mkdirSync(output, { recursive: true });
    const preview = path.join(output, 'preview.html');
    fs.writeFileSync(preview, html);
    await page.goto('file:///' + preview.replaceAll('\\', '/'));
    await page.getByRole('button', { name: '编辑', exact: true }).click();
    await page.locator('#tab-models').click();
    const claude = page.locator('.protocol-row').filter({ has: page.locator('strong', { hasText: 'anthropic/claude-sonnet-4-6' }) });
    const gpt = page.locator('.protocol-row').filter({ has: page.locator('strong', { hasText: 'gpt-5.4' }) });
    await claude.getByLabel('指定支持协议', { exact: true }).check();
    assert.equal(await claude.getByLabel('Anthropic Messages', { exact: true }).isChecked(), true);
    assert.equal(await claude.getByLabel('OpenAI Responses', { exact: true }).isChecked(), false);
    assert.equal(await claude.getByLabel('anthropic/claude-sonnet-4-6 首选协议').inputValue(), 'messages');
    await gpt.getByLabel('指定支持协议', { exact: true }).check();
    assert.equal(await gpt.getByLabel('gpt-5.4 首选协议').inputValue(), 'responses');
    // Changing channel endpoints updates defaults for unconfigured models and preserves explicit choices.
    await gpt.getByLabel('指定支持协议', { exact: true }).uncheck();
    await page.locator('#tab-basic').click();
    await page.locator('input[name=endpoints][value="/v1/responses"]').uncheck();
    await page.locator('#tab-models').click();
    await gpt.getByLabel('指定支持协议', { exact: true }).check();
    assert.equal(await gpt.getByLabel('gpt-5.4 首选协议').inputValue(), 'messages');
    await gpt.getByLabel('指定支持协议', { exact: true }).uncheck();
    await page.locator('#tab-basic').click();
    await page.locator('input[name=endpoints][value="/v1/responses"]').check();
    await page.locator('#tab-models').click();
    assert.equal(await claude.getByLabel('anthropic/claude-sonnet-4-6 首选协议').inputValue(), 'messages');
    await claude.getByLabel('OpenAI Responses', { exact: true }).check();
    await claude.getByLabel('anthropic/claude-sonnet-4-6 首选协议').selectOption('responses');
    await page.locator('#save').click();
    await page.waitForFunction(() => !document.querySelector('#editor').open);
    const saved = await page.evaluate(() => window.calls.filter(call => call.route === 'accounts/save').at(-1).body);
    assert.equal(saved.modelProtocols['anthropic/claude-sonnet-4-6'].preferredProtocol, 'responses');
    assert.deepEqual(saved.modelProtocols['anthropic/claude-sonnet-4-6'].protocols, ['messages', 'responses']);
    assert.equal('gpt-5.4' in saved.modelProtocols, false);
    await page.getByRole('button', { name: '编辑', exact: true }).click();
    await page.locator('#tab-models').click();
    assert.equal(await claude.getByLabel('anthropic/claude-sonnet-4-6 首选协议').inputValue(), 'responses');
    await claude.getByLabel('anthropic/claude-sonnet-4-6 首选协议').selectOption('messages');
    await page.locator('#save').click();
    await page.waitForFunction(() => !document.querySelector('#editor').open);
    await page.getByRole('button', { name: '测试连接', exact: true }).click();
    assert.equal(await page.locator('#testEndpoint').inputValue(), '');
    assert.match(await page.locator('.test-endpoint-hint').innerText(), /Claude.*Messages/);
    await page.locator('#testAll').click();
    await page.locator('#confirmYes').click();
    await page.waitForFunction(() => document.querySelector('#testProgress').textContent.includes('已完成'));
    const starts = await page.evaluate(() => window.calls.filter(call => call.route === 'tests/start').map(call => call.body));
    assert.equal(starts[0].endpoint, null);
    const claudeResult = page.locator('#testRows tr').filter({ hasText: 'anthropic/claude-sonnet-4-6' });
    const gptResult = page.locator('#testRows tr').filter({ hasText: 'gpt-5.4' });
    assert.equal(await claudeResult.locator('.result-endpoint').innerText(), '/v1/messages');
    assert.equal(await gptResult.locator('.result-endpoint').innerText(), '/v1/responses');
    for (const viewport of [{ width: 1280, height: 660 }, { width: 390, height: 844 }, { width: 320, height: 844 }]) {
      await page.setViewportSize(viewport);
      assert.equal(await page.locator('#testDialog').evaluate(dialog => dialog.scrollWidth <= dialog.clientWidth), true);
      await claudeResult.getByRole('button', { name: '详情', exact: true }).click();
      assert.match(await page.locator('#testDetailOverview').innerText(), /测试端点\s+\/v1\/messages/);
      assert.match(await page.locator('#testDetails').innerText(), /测试端点：\/v1\/messages/);
      assert.equal(await page.locator('#testDetailDialog').evaluate(dialog => dialog.scrollWidth <= dialog.clientWidth), true);
      await page.screenshot({ path: path.join(output, 'details-' + viewport.width + '.png') });
      await page.locator('#testDetailClose').click();
    }
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.locator('#testConfiguration summary').click();
    await page.locator('#testEndpoint').selectOption('/v1/responses');
    await claudeResult.getByRole('button', { name: '测试', exact: true }).click();
    await page.locator('#confirmYes').click();
    await page.waitForFunction(() => document.querySelector('#testRows .result-endpoint').textContent === '/v1/responses');
    assert.equal(await page.evaluate(() => window.calls.filter(call => call.route === 'tests/start').at(-1).body.endpoint), '/v1/responses');
    assert.deepEqual(errors, []);
    console.log('Connection-test protocols passed: editor defaults and persistence, automatic mixed-model results, explicit endpoint, desktop/mobile details.');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
