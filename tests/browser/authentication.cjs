const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

(async () => {
  const output = path.resolve(__dirname, '../../artifacts/authentication-selector/browser');
  fs.mkdirSync(output, { recursive: true });
  const preview = path.join(output, 'preview.html');
  fs.writeFileSync(preview, require('./page-source.cjs')());
  const browser = await chromium.launch({ headless: true, executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      window.authFixture = { account: null, calls: [], failSave: false };
      window.Router2API = { request: async (method, route, body) => {
        const fixture = window.authFixture;
        fixture.calls.push({ method, route, body: structuredClone(body) });
        if (route === 'appearance/settings') return { mode: 'light' };
        if (route === 'execution/settings') return { defaultHostAttempts: 1, execution: { setupTimeoutSeconds: 300, totalSetupTimeoutSeconds: 180, responseTimeoutSeconds: 600 } };
        if (route === 'network/proxies') return { subscriptions: [], nodes: [] };
        if (route === 'accounts') return { accounts: fixture.account ? [structuredClone(fixture.account)] : [] };
        if (route === 'models/discover' || route === 'models/refresh') return { models: ['model'] };
        if (route === 'accounts/save') {
          if (fixture.failSave) throw Error('保存失败（测试）');
          const previous = fixture.account;
          fixture.account = { ...structuredClone(body), id: 'channel', keyRevision: (previous?.keyRevision || 0) + 1,
            totalKeyCount: body.keys.length, enabledKeyCount: body.keys.filter(key => key.enabled).length,
            keys: body.keys.map((key, index) => ({ ...key, id: key.id || 'key-' + index,
              secret: key.secret || previous?.keys.find(old => old.id === key.id)?.secret })) };
          return { account: structuredClone(fixture.account) };
        }
        throw Error('Unexpected API ' + route);
      } };
    });
    await page.goto('file:///' + preview.replaceAll('\\', '/'));
    const select = page.locator('#authenticationMode');
    const savedExtra = () => page.evaluate(() => authFixture.account.extraParams);
    const lastDiscovery = () => page.evaluate(() => authFixture.calls.filter(call => call.route === 'models/discover').at(-1).body);
    const open = async () => page.getByRole('button', { name: '编辑', exact: true }).click();
    const save = async () => { await page.locator('#save').click(); await page.locator('#editor').waitFor({ state: 'hidden' }); };
    const discover = async () => {
      await page.locator('#tab-models').click(); await page.locator('#discover').click();
      await page.locator('#discoveryStatus').filter({ hasText: '已获取' }).waitFor();
    };
    await page.locator('#add').click();
    assert.equal(await select.inputValue(), 'bearer');
    assert.deepEqual(await select.locator('option').allTextContents(), ['Authorization: Bearer', 'x-api-key']);
    await page.locator('#label').fill('认证方式测试');
    await page.locator('#baseUrl').fill('https://upstream.example/v1');
    await page.locator('#tab-keys').click(); await page.locator('#keyBatch').fill('sk-test'); await page.locator('#keyAdd').click();
    await page.locator('#tab-models').click(); await page.locator('#models').fill('model');
    await discover();
    assert.deepEqual((await lastDiscovery()).extraParams, { apiKeyHeader: 'Authorization', apiKeyPrefix: 'Bearer ' });
    await page.locator('#tab-basic').click(); await select.selectOption('x-api-key');
    await discover();
    assert.deepEqual((await lastDiscovery()).extraParams, { apiKeyHeader: 'x-api-key', apiKeyPrefix: '' });
    await save();
    assert.deepEqual(await savedExtra(), { apiKeyHeader: 'x-api-key', apiKeyPrefix: '' });
    await open();
    assert.equal(await select.inputValue(), 'x-api-key');
    await select.selectOption('bearer');
    await page.locator('#tab-models').click();
    const refreshes = await page.evaluate(() => authFixture.calls.filter(call => call.route === 'models/refresh').length);
    await page.locator('#refreshModels').click();
    await page.locator('#discoveryResponse').filter({ hasText: '认证方式、通用请求头或高级参数尚未保存' }).waitFor();
    assert.equal(await page.evaluate(() => authFixture.calls.filter(call => call.route === 'models/refresh').length), refreshes);
    await discover();
    assert.deepEqual((await lastDiscovery()).extraParams, { apiKeyHeader: 'Authorization', apiKeyPrefix: 'Bearer ' });
    // Draft auth edits must not mutate the original account when cancelled or when a save fails.
    assert.deepEqual(await savedExtra(), { apiKeyHeader: 'x-api-key', apiKeyPrefix: '' });
    await page.evaluate(() => { authFixture.failSave = true; });
    await page.locator('#save').click(); await page.locator('#formError').filter({ hasText: '保存失败' }).waitFor();
    assert.equal(await select.inputValue(), 'bearer');
    assert.deepEqual(await savedExtra(), { apiKeyHeader: 'x-api-key', apiKeyPrefix: '' });
    await page.locator('#cancel').click();
    await page.evaluate(() => { authFixture.failSave = false; });
    await open(); assert.equal(await select.inputValue(), 'x-api-key');
    await select.selectOption('bearer'); await save();
    assert.deepEqual(await savedExtra(), { apiKeyHeader: 'Authorization', apiKeyPrefix: 'Bearer ' });
    await open(); assert.equal(await select.inputValue(), 'bearer');
    await page.locator('#cancel').click();
    // Unsupported auth defaults to Bearer in the draft; cancelling preserves saved settings.
    const custom = { apiKeyHeader: 'X-Custom-Token', apiKeyPrefix: 'Token ', vendor: { nested: ['preserve'] } };
    await page.evaluate(extra => { authFixture.account.extraParams = extra; }, custom);
    await page.locator('#reload').click(); await open();
    assert.equal(await select.inputValue(), 'bearer');
    assert.deepEqual(await select.locator('option').allTextContents(), ['Authorization: Bearer', 'x-api-key']);
    await discover(); assert.deepEqual((await lastDiscovery()).extraParams, { ...custom, apiKeyHeader: 'Authorization', apiKeyPrefix: 'Bearer ' });
    assert.deepEqual(await savedExtra(), custom);
    await page.locator('#cancel').click(); assert.deepEqual(await savedExtra(), custom);
    await open(); await save();
    assert.deepEqual(await savedExtra(), { ...custom, apiKeyHeader: 'Authorization', apiKeyPrefix: 'Bearer ' });
    await open(); await select.selectOption('x-api-key'); await save();
    assert.deepEqual(await savedExtra(), { ...custom, apiKeyHeader: 'x-api-key', apiKeyPrefix: '' });
    await open(); assert.equal(await select.inputValue(), 'x-api-key');
    for (const width of [1280, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      await select.scrollIntoViewIfNeeded();
      assert.equal(await page.locator('#editor').evaluate(dialog => dialog.scrollWidth <= dialog.clientWidth), true);
      await page.screenshot({ path: path.join(output, `authentication-${width}.png`) });
    }
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.locator('#cancel').click();
    // Missing auth uses the displayed Bearer default for discovery and saving, including JSON settings.
    await page.evaluate(() => { authFixture.account.extraParams = '{"vendor":{"keep":true}}'; });
    await page.locator('#reload').click(); await open();
    assert.equal(await select.inputValue(), 'bearer');
    assert.deepEqual(await select.locator('option').allTextContents(), ['Authorization: Bearer', 'x-api-key']);
    await discover();
    assert.deepEqual((await lastDiscovery()).extraParams, { vendor: { keep: true }, apiKeyHeader: 'Authorization', apiKeyPrefix: 'Bearer ' });
    await save();
    assert.deepEqual(await savedExtra(), { vendor: { keep: true }, apiKeyHeader: 'Authorization', apiKeyPrefix: 'Bearer ' });
    assert.deepEqual(errors, []);
    console.log('Authentication UI passed: two auth choices, Bearer fallback, x-api-key selection, draft discovery, refresh guard, save/reopen, cancellation/failure isolation, advanced fields and desktop/mobile.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
