const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const output = path.resolve(__dirname, '../../artifacts');

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const preview = path.join(output, 'endpoint-headers-preview.html');
  fs.writeFileSync(preview, require('./page-source.cjs')());
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 960 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('dialog', () => errors.push('Unexpected native dialog'));
    await page.addInitScript(() => {
      window.calls = [];
      window.account = {
        id: 'channel', label: '接口请求头', baseUrl: 'https://upstream.example/v1', enabled: true, weight: 100,
        models: ['model'], endpoints: ['/v1/responses', '/v1/messages'], keyRevision: 1, keySelectionMode: 'roundRobin',
        keys: [{ id: 'key', name: 'Key', masked: '••••test', enabled: true }],
        headerOverride: { 'X-Common': 'one' }
      };
      Object.defineProperty(navigator, 'clipboard', {
        value: { writeText: async () => { throw Error('Clipboard disabled for test'); } }
      });
      window.Router2API = { request: async (method, route, body) => {
        window.calls.push({ method, route, body: structuredClone(body) });
        if (route === 'accounts') return { accounts: [window.account] };
        if (route === 'accounts/save') {
          window.account = { ...window.account, ...structuredClone(body) };
          return { account: window.account };
        }
        if (route === 'models/discover' || route === 'models/refresh') {
          if (window.discoveryFailure) throw Error(window.discoveryFailure);
          return { models: ['model'] };
        }
        throw Error('Unexpected route: ' + route);
      } };
    });
    await page.goto('file:///' + preview.replaceAll('\\', '/'));
    const scope = value => page.locator(`[data-header-scope="${value}"]`);
    const read = async () => JSON.parse(await page.locator('#headerOverride').inputValue());
    const set = async value => page.locator('#headerOverride').fill(JSON.stringify(value, null, 2));
    const open = async () => {
      await page.locator('#cards').getByRole('button', { name: '编辑', exact: true }).click();
      await page.locator('#tab-headers').click();
      await page.locator('#headerJson').click();
    };
    const save = async () => {
      await page.locator('#save').click();
      await page.locator('#editor').waitFor({ state: 'hidden' });
    };
    const apply = async name => {
      const populated = Object.keys(await read()).length > 0;
      await page.locator(name === 'codex' ? '#templateCodex' : '#templateClaude').click();
      if (populated) await page.locator('#confirmYes').click();
    };

    await open();
    assert.equal(await page.locator('#headerShared').getAttribute('aria-pressed'), 'true');
    assert.equal(await page.locator('#headerScopes').isVisible(), false);
    assert.deepEqual(await read(), { 'X-Common': 'one' });
    await page.locator('#headerPerEndpoint').click();
    await scope('/v1/responses').click();
    assert.equal(await page.locator('#headerUseCommon').isChecked(), true);
    assert.equal(await page.locator('#headerOverride').getAttribute('readonly'), '');
    for (const id of ['templateCodex', 'templateClaude', 'headerAdd', 'headerClear', 'headerFormat'])
      assert.equal(await page.locator('#' + id).isDisabled(), true, id);
    await page.locator('#headerVisual').click();
    assert.equal(await page.locator('#headerRows input').first().getAttribute('readonly'), '');
    assert.equal(await page.locator('#headerRows button').first().isDisabled(), true);
    await page.locator('#headerJson').click();
    await page.locator('#headerCopy').click();
    assert.deepEqual(JSON.parse(await page.locator('#copyContent').inputValue()), { 'X-Common': 'one' });
    await page.locator('#copyClose').click();
    await page.locator('#headerUseCommon').uncheck();
    assert.deepEqual(await read(), { 'X-Common': 'one' });
    assert.equal(await page.locator('#headerOverride').getAttribute('readonly'), null);
    await set({ 'X-Response': 'independent' });

    await scope('/v1/messages').click();
    assert.deepEqual(await read(), { 'X-Common': 'one' });
    await page.locator('#headerUseCommon').uncheck();
    await apply('claude');
    const claude = await read();
    assert.equal(claude['x-app'], 'cli');
    assert.equal(claude.Originator, undefined);
    await scope('/v1/responses').click();
    assert.deepEqual(await read(), { 'X-Response': 'independent' });
    await apply('codex');
    const codex = await read();
    assert.equal(codex.Originator, 'codex_exec');
    assert.equal(codex['x-app'], undefined);
    await scope('common').click();
    await set({ 'X-Common': 'two' });
    await scope('/v1/responses').click();
    assert.deepEqual(await read(), codex);
    await scope('/v1/messages').click();
    assert.deepEqual(await read(), claude);
    await page.locator('#headerUseCommon').check();
    assert.deepEqual(await read(), { 'X-Common': 'two' });
    await scope('common').click();
    await set({ 'X-Common': 'three' });
    await scope('/v1/messages').click();
    assert.deepEqual(await read(), { 'X-Common': 'three' });
    await page.locator('#headerUseCommon').uncheck();
    assert.deepEqual(await read(), claude);

    await page.locator('#headerOverride').fill('{broken');
    for (const button of [scope('/v1/responses'), page.locator('#headerShared'), page.locator('#headerVisual')]) {
      await button.click();
      assert.equal(await scope('/v1/messages').getAttribute('aria-pressed'), 'true');
      assert.equal(await page.locator('#headerPerEndpoint').getAttribute('aria-pressed'), 'true');
      assert.equal(await page.locator('#headerJson').getAttribute('aria-pressed'), 'true');
      assert.match(await page.locator('#headerStatus').innerText(), /格式错误/);
    }
    await page.locator('#headerUseCommon').click();
    assert.equal(await page.locator('#headerUseCommon').isChecked(), false);
    assert.equal(await page.locator('#headerOverride').inputValue(), '{broken');
    await page.locator('#save').click();
    assert.equal(await page.locator('#editor').isVisible(), true);
    assert.match(await page.locator('#formError').innerText(), /格式错误/);
    await set(claude);

    await page.locator('#headerClear').click();
    await page.locator('#confirmNo').click();
    assert.deepEqual(await read(), claude);
    await page.locator('#headerClear').click();
    await page.locator('#confirmYes').click();
    assert.deepEqual(await read(), {});
    await page.locator('#headerUseCommon').check();
    assert.deepEqual(await read(), { 'X-Common': 'three' });
    await page.locator('#headerUseCommon').uncheck();
    assert.deepEqual(await read(), {});
    await apply('codex');
    assert.deepEqual(await read(), codex);
    await scope('/v1/responses').click();
    await apply('claude');
    assert.deepEqual(await read(), claude);
    await scope('/v1/messages').click();

    await page.locator('#tab-models').click();
    await page.locator('#discover').click();
    await page.locator('#discoveryStatus').filter({ hasText: '已获取' }).waitFor();
    const discovery = await page.evaluate(() => window.calls.filter(c => c.route === 'models/discover').at(-1).body);
    assert.deepEqual(discovery.headerOverride, { 'X-Common': 'three' });
    assert.equal(await page.evaluate(() => window.account.headerOverride['X-Common']), 'one');
    await page.locator('#refreshModels').click();
    await page.locator('#discoveryResponse').filter({ hasText: '通用请求头或高级参数尚未保存' }).waitFor();
    assert.equal(await page.evaluate(() => window.calls.filter(c => c.route === 'models/refresh').length), 0);
    assert.equal(await page.locator('#candidates').textContent(), 'model');
    const rawFailure = 'HTTP: 200\nContent-Type: text/html\n响应正文:\n<html><img src=x onerror="window.injected=true"></html>\n' + 'x'.repeat(3000);
    await page.evaluate(text => { window.discoveryFailure = text; }, rawFailure);
    await page.locator('#discover').click();
    await page.locator('#discoveryResponse').filter({ hasText: 'HTTP: 200' }).waitFor();
    assert.equal(await page.locator('#discoveryResponse').textContent(), rawFailure);
    assert.equal(await page.locator('#discoveryResponse img').count(), 0);
    assert.equal(await page.evaluate(() => !!window.injected), false);
    assert.equal(await page.locator('#candidates').textContent(), 'model');
    await page.locator('#copyDiscoveryResponse').click();
    await page.locator('#discoveryStatus').filter({ hasText: '复制失败' }).waitFor();
    await page.evaluate(() => { navigator.clipboard.writeText = async text => { window.copied = text; }; });
    await page.locator('#copyDiscoveryResponse').click();
    await page.locator('#discoveryStatus').filter({ hasText: '已复制' }).waitFor();
    assert.equal(await page.evaluate(() => window.copied), rawFailure);
    await page.setViewportSize({ width: 390, height: 844 });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
    await page.setViewportSize({ width: 1280, height: 960 });
    await page.evaluate(() => { window.discoveryFailure = null; });
    await page.locator('#tab-headers').click();
    assert.deepEqual(await read(), codex);
    await page.locator('#headerOverride').fill('{');
    await page.locator('#tab-models').click();
    const callsBeforeInvalid = await page.evaluate(() => window.calls.length);
    await page.locator('#refreshModels').click();
    await page.locator('#discoveryResponse').filter({ hasText: 'JSON' }).waitFor();
    assert.equal(await page.evaluate(() => window.calls.length), callsBeforeInvalid);
    await page.locator('#tab-headers').click();
    await set(codex);
    await save();
    const saved = await page.evaluate(() => window.calls.filter(c => c.route === 'accounts/save').at(-1).body);
    assert.equal(saved.headerOverrideMode, 'perEndpoint');
    assert.deepEqual(saved.headerOverride, { 'X-Common': 'three' });
    assert.deepEqual(saved.endpointHeaderOverrides, {
      '/v1/responses': { useCommon: false, headers: claude },
      '/v1/messages': { useCommon: false, headers: codex }
    });

    await open();
    assert.equal(await page.locator('#headerPerEndpoint').getAttribute('aria-pressed'), 'true');
    await scope('/v1/messages').click();
    assert.deepEqual(await read(), codex);
    await page.locator('#headerShared').click();
    assert.deepEqual(await read(), { 'X-Common': 'three' });
    await save();
    assert.equal(await page.evaluate(() => window.account.headerOverrideMode), 'shared');
    assert.deepEqual(await page.evaluate(() => window.account.endpointHeaderOverrides), saved.endpointHeaderOverrides);
    await open();
    assert.equal(await page.locator('#headerScopes').isVisible(), false);
    await page.locator('#headerPerEndpoint').click();
    await scope('/v1/responses').click();
    assert.deepEqual(await read(), claude);
    await scope('/v1/messages').click();
    assert.deepEqual(await read(), codex);
    await page.locator('#headerClear').click();
    await page.locator('#confirmYes').click();
    await page.locator('#headerUseCommon').check();
    await save();
    await open();
    await scope('/v1/messages').click();
    assert.equal(await page.locator('#headerUseCommon').isChecked(), true);
    assert.deepEqual(await read(), { 'X-Common': 'three' });
    await page.locator('#headerUseCommon').uncheck();
    assert.deepEqual(await read(), {});
    await apply('codex');
    await page.locator('#headerVisual').click();
    await page.screenshot({ path: path.join(output, 'endpoint-headers-desktop.png') });
    await page.setViewportSize({ width: 390, height: 844 });
    assert.equal(await page.locator('#headerScopes').isVisible(), true);
    await scope('/v1/responses').click();
    await page.locator('#headerUseCommon').check();
    const layout = await page.locator('#editor').evaluate(editor => ({
      horizontal: editor.scrollWidth <= editor.clientWidth + 1,
      footer: editor.querySelector('.editor-footer').getBoundingClientRect().bottom <= innerHeight,
      content: editor.querySelector('.editor-content').scrollWidth <= editor.querySelector('.editor-content').clientWidth + 1
    }));
    assert.deepEqual(layout, { horizontal: true, footer: true, content: true });
    await page.screenshot({ path: path.join(output, 'endpoint-headers-mobile.png') });
    await page.locator('#headerUseCommon').uncheck();
    await page.locator('#headerJson').click();
    assert.deepEqual(await read(), claude);
    await page.locator('#close').click();
    await page.locator('#add').click();
    await page.locator('#tab-headers').click();
    await page.locator('#headerJson').click();
    assert.equal(await page.locator('#headerShared').getAttribute('aria-pressed'), 'true');
    assert.deepEqual(await read(), {});
    await page.locator('#headerPerEndpoint').click();
    await scope('/v1/messages').click();
    assert.equal(await page.locator('#headerUseCommon').isChecked(), true);
    assert.deepEqual(await read(), {});
    assert.deepEqual(errors, []);
    console.log('Endpoint header UI passed: inheritance, isolated drafts, templates, JSON validation, discovery, persistence, desktop/mobile.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
