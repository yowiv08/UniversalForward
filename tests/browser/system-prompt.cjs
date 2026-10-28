const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const { bridge, assertContrast } = require('./settings-fixture.cjs');
const source = require('./page-source.cjs')().replace('<head>', '<head>' + bridge);
const output = path.resolve(__dirname, '../../artifacts/v0.3.8/ui');

function promptHost() {
  window.promptState = { calls: [], mode: 'light', failSave: false, nextId: 1, accounts: [
    { id: 'primary', label: '系统提示词渠道', baseUrl: 'https://prompt.example/v1', enabled: true, weight: 100,
      models: ['gpt-model', 'claude-model'], endpoints: ['/v1/responses', '/v1/messages'], keyRevision: 1,
      keys: [{ id: 'key-a', name: '0001', secret: 'sk-test-0001', enabled: true }],
      systemPromptPolicy: { mode: 'append', text: '  请用简体中文回答。\n先给结论，再解释原因。  ' } },
    { id: 'legacy', label: '原有渠道', baseUrl: 'https://legacy.example/v1', enabled: false, weight: 100,
      models: ['gpt-model'], endpoints: ['/v1/responses'], keyRevision: 1,
      keys: [{ id: 'key-b', name: '0002', secret: 'sk-test-0002', enabled: true }] }
  ] };
  addEventListener('message', event => {
    if (event.source !== document.querySelector('iframe').contentWindow || event.data?.type !== 'router2api-request') return;
    const { id, method, route, body } = event.data, state = window.promptState;
    state.calls.push({ method, route, body });
    let result, error;
    if (route === 'accounts') result = { accounts: state.accounts };
    else if (route === 'appearance/settings') {
      if (method === 'POST') state.mode = body.mode;
      result = { mode: state.mode };
    } else if (route === 'execution/settings') result = { defaultHostAttempts: 1,
      execution: { setupTimeoutSeconds: 300, totalSetupTimeoutSeconds: 180, responseTimeoutSeconds: 600 } };
    else if (route === 'network/proxies') result = { subscriptions: [], nodes: [] };
    else if (route === 'accounts/save') {
      if (state.failSave) error = '模拟渠道保存失败';
      else {
        const previous = state.accounts.find(account => account.id === body.id), accountId = body.id || 'copy-' + state.nextId++;
        const account = { ...previous, ...body, id: accountId, keyRevision: (previous?.keyRevision || 0) + 1,
          keys: body.keys.map((key, index) => ({ ...key, id: key.id || accountId + '-key-' + index,
            secret: key.secret || previous?.keys.find(old => old.id === key.id)?.secret || '' })) };
        const index = state.accounts.findIndex(old => old.id === accountId);
        if (index < 0) state.accounts.push(account); else state.accounts[index] = account;
        result = { account };
      }
    } else error = 'Unexpected route: ' + route;
    event.source.postMessage({ type: 'router2api-response', id, ok: !error, body: result, error }, '*');
  });
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>html,body{margin:0}iframe{display:block;border:0;width:100%;height:100vh}</style></head><body><iframe title="UniversalForward" sandbox="allow-scripts allow-forms"></iframe><script>(${promptHost})();document.querySelector('iframe').srcdoc=${JSON.stringify(source).replaceAll('<', '\\u003c')};</script></body></html>`;
  const server = http.createServer((_, response) => { response.setHeader('Content-Type', 'text/html; charset=utf-8'); response.end(html); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1040 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    page.on('dialog', () => errors.push('Unexpected native dialog'));
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    const frame = page.frameLocator('iframe'), field = id => frame.locator('#' + id);
    const primaryCard = () => frame.locator('.channel-card').first();
    const selectedMode = () => frame.locator('#systemPromptMode input:checked').inputValue();
    const selectMode = mode => frame.locator(`#systemPromptMode .reasoning-mode-card:has(input[value=${mode}])`).click();
    const saveCalls = () => page.evaluate(() => promptState.calls.filter(call => call.route === 'accounts/save'));
    const open = async () => { await primaryCard().getByRole('button', { name: '编辑', exact: true }).click(); await field('tab-prompt').click(); };
    const save = async () => { await field('save').click(); await field('editor').waitFor({ state: 'hidden' }); };
    const openCopy = async () => {
      await primaryCard().locator('.channel-menu-trigger').click();
      await frame.locator('.channel-menu:not([hidden])').getByRole('menuitem', { name: '复制', exact: true }).click();
      await field('tab-prompt').click();
    };
    await primaryCard().waitFor();
    const original = await page.evaluate(() => structuredClone(promptState.accounts[0]));

    // Old and new channels default to no rewrite; entering or cancelling a draft has no write.
    await frame.locator('.channel-card').nth(1).getByRole('button', { name: '编辑', exact: true }).click();
    await field('tab-prompt').click();
    assert.equal(await selectedMode(), 'off');
    assert.equal(await field('systemPromptText').inputValue(), '');
    assert.equal(await field('systemPromptText').isEditable(), false);
    assert.equal(await field('systemPromptDetails').evaluate(node => node.open), false);
    await field('tab-prompt').focus(); await page.keyboard.press('ArrowLeft');
    assert.equal(await field('tab-reasoning').getAttribute('aria-selected'), 'true');
    await page.keyboard.press('ArrowRight');
    assert.equal(await field('tab-prompt').getAttribute('aria-selected'), 'true');
    assert.equal(await frame.locator('[role=tabpanel]:visible').count(), 1);
    await field('cancel').click();
    await field('add').click(); await field('tab-prompt').click();
    assert.equal(await selectedMode(), 'off'); assert.equal(await field('systemPromptText').inputValue(), '');
    await field('cancel').click(); assert.equal((await saveCalls()).length, 0);

    await open();
    assert.equal(await selectedMode(), 'append');
    assert.equal(await field('systemPromptText').inputValue(), original.systemPromptPolicy.text);
    assert.equal(await field('systemPromptText').getAttribute('maxlength'), '32000');
    assert.equal(await field('systemPromptText').evaluate(node => getComputedStyle(node).fontSize), '15px');
    assert.match(await field('systemPromptCount').innerText(), new RegExp('^' + original.systemPromptPolicy.text.length + ' / 32,000'));
    await selectMode('replace'); await selectMode('off');
    assert.equal(await field('systemPromptText').inputValue(), original.systemPromptPolicy.text);
    assert.equal(await field('systemPromptText').isEditable(), false);
    await frame.locator('#systemPromptMode input[value=off]').focus();
    await page.keyboard.press('ArrowRight'); assert.equal(await selectedMode(), 'append');
    assert.equal(await field('systemPromptText').isEditable(), true);
    await page.keyboard.press('ArrowRight'); assert.equal(await selectedMode(), 'replace');
    assert.match(await field('systemPromptModeHint').innerText(), /覆盖/);

    // Validate active prompts before sending and focus the relevant tab without losing the draft.
    await field('systemPromptText').fill(' \n\t '); await field('tab-basic').click(); await field('save').click();
    assert.match(await field('formError').innerText(), /请填写系统提示词/);
    assert.equal(await field('tab-prompt').getAttribute('aria-selected'), 'true');
    assert.equal(await field('systemPromptText').evaluate(node => document.activeElement === node), true);
    assert.equal((await saveCalls()).length, 0);
    await field('systemPromptText').evaluate(node => { node.value = 'x'.repeat(32001); node.dispatchEvent(new Event('input', { bubbles: true })); });
    await field('save').click(); assert.match(await field('formError').innerText(), /不能超过/);
    assert.equal((await saveCalls()).length, 0);
    await field('systemPromptText').fill('<img src=x onerror=window.injected=true>');
    assert.equal(await field('panel-prompt').locator('img').count(), 0);
    const exactText = '  请用简体中文回答。\n\n先给结论，再解释原因。\n保留代码里的 `literal` 和 ${variables}。 😀  \n';
    await field('systemPromptText').fill(exactText);
    await page.evaluate(() => { promptState.failSave = true; });
    await field('save').click(); await field('formError').filter({ hasText: '模拟渠道保存失败' }).waitFor();
    assert.equal(await field('editor').isVisible(), true);
    assert.equal(await selectedMode(), 'replace');
    assert.equal(await field('systemPromptText').inputValue(), exactText);
    assert.deepEqual(await page.evaluate(() => promptState.accounts[0]), original);
    assert.deepEqual((await saveCalls()).at(-1).body.systemPromptPolicy, { mode: 'replace', text: exactText });
    await page.evaluate(() => { promptState.failSave = false; }); await save();
    assert.deepEqual(await page.evaluate(() => promptState.accounts[0].systemPromptPolicy), { mode: 'replace', text: exactText });

    // Saving off retains inactive text, reopening preserves settings, and cancelling changes is independent.
    await field('reload').click(); await open();
    assert.equal(await selectedMode(), 'replace'); assert.equal(await field('systemPromptText').inputValue(), exactText);
    await selectMode('off'); await save();
    await open(); assert.equal(await selectedMode(), 'off');
    assert.equal(await field('systemPromptText').inputValue(), exactText); assert.equal(await field('systemPromptText').isEditable(), false);
    await selectMode('append'); await field('systemPromptText').fill('未保存的草稿');
    await field('cancel').click();
    assert.deepEqual(await page.evaluate(() => promptState.accounts[0].systemPromptPolicy), { mode: 'off', text: exactText });

    const sourceBeforeCopy = await page.evaluate(() => structuredClone(promptState.accounts[0]));
    await openCopy(); assert.equal(await selectedMode(), 'off'); assert.equal(await field('systemPromptText').inputValue(), exactText);
    await selectMode('append'); await field('systemPromptText').fill('副本草稿'); await field('cancel').click();
    assert.equal(await page.evaluate(() => promptState.accounts.length), 2);
    await openCopy(); assert.equal(await selectedMode(), 'off'); assert.equal(await field('systemPromptText').inputValue(), exactText);
    await selectMode('append'); await save();
    assert.deepEqual(await page.evaluate(() => promptState.accounts[2].systemPromptPolicy), { mode: 'append', text: exactText });
    assert.equal(await page.evaluate(() => promptState.accounts[2].enabled), false);
    assert.deepEqual(await page.evaluate(() => promptState.accounts[0]), sourceBeforeCopy);

    // Check contrast in every mode, including readonly, focus and hover, in the real plugin sandbox.
    for (const appearance of ['light', 'dark']) {
      await frame.locator(`.workspace-header [data-appearance-mode=${appearance}]`).click();
      await field('appearanceStatus').waitFor({ state: 'hidden' });
      await open();
      for (const mode of ['off', 'append', 'replace']) {
        await selectMode(mode);
        await frame.locator(`#systemPromptMode .reasoning-mode-card:has(input[value=${mode}])`).hover();
        await assertContrast(frame, '#panel-prompt');
        await field('systemPromptText').focus(); await assertContrast(frame, '#panel-prompt');
      }
      assert.equal(await field('systemPromptDetails').evaluate(node => node.open), false);
      await page.screenshot({ path: path.join(output, `system-prompt-${appearance}.png`) });
      await field('systemPromptDetails').locator('summary').click();
      await assertContrast(frame, '#panel-prompt');
      assert.match(await field('systemPromptDetails').innerText(), /instructions/);
      assert.match(await field('systemPromptDetails').innerText(), /system／developer/);
      await field('cancel').click();
      for (const width of [390, 320]) {
        await page.setViewportSize({ width, height: 844 }); await open();
        await selectMode('replace');
        assert.equal(await field('editor').evaluate(node => node.scrollWidth <= node.clientWidth + 1), true);
        assert.equal(await field('panel-prompt').evaluate(node => node.scrollWidth <= node.clientWidth + 1), true);
        const bounds = await field('systemPromptText').boundingBox();
        assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= width);
        await assertContrast(frame, '#panel-prompt');
        await page.screenshot({ path: path.join(output, `system-prompt-${appearance}-${width}.png`) });
        await field('cancel').click();
      }
      await page.setViewportSize({ width: 1440, height: 1040 });
    }
    assert.deepEqual(errors, []);
    assert.equal(await frame.locator('body').evaluate(() => window.injected), undefined);
    console.log('System prompt UI passed: defaults, keyboard, independent drafts, exact multiline text, validation, save recovery, dormant text, copying, light/dark contrast and 320/390px sandbox layouts.');
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
