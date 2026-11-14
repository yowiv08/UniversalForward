const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      window.calls = [];
      window.bulkAccount = { id: 'bulk', label: '批量 Key 管理', baseUrl: 'https://example.test/v1', enabled: true,
        models: ['model'], endpoints: ['/v1/responses'], keyRevision: 1,
        keys: Array.from({ length: 100 }, (_, i) => ({ id: 'key-' + i, name: 'Channel ' + i,
          secret: 'test-only-secret-' + i, enabled: i < 2 })) };
      window.Router2API = { request: async (method, route, body) => {
        calls.push({ method, route, body });
        if (route === 'appearance/settings') return { mode: 'light' };
        if (route === 'execution/settings') return { defaultHostAttempts: 1, execution: { setupTimeoutSeconds: 300, totalSetupTimeoutSeconds: 180, responseTimeoutSeconds: 600 } };
        if (route === 'network/proxies') return { subscriptions: [], nodes: [] };
        if (route === 'accounts') return { accounts: [structuredClone(bulkAccount)] };
        if (route === 'accounts/save') {
          if (window.failSave) throw Error('模拟保存失败');
          if (body.keyRevision !== bulkAccount.keyRevision) throw Error('Key 配置已变化');
          const keys = body.keys.map((key, i) => ({ ...key, id: key.id || 'added-' + i,
            secret: key.secret || bulkAccount.keys.find(old => old.id === key.id).secret }));
          Object.assign(bulkAccount, body, { keys, keyRevision: bulkAccount.keyRevision + 1 });
          return { account: structuredClone(bulkAccount) };
        }
        throw Error('Unexpected route: ' + route);
      } };
    });
    const output = path.resolve(__dirname, '../../artifacts/key-bulk-operations');
    fs.mkdirSync(output, { recursive: true });
    const preview = path.join(output, 'preview.html');
    fs.writeFileSync(preview, require('./page-source.cjs')());
    await page.goto('file:///' + preview.split(path.sep).join('/'));
    const edit = async () => {
      await page.getByRole('button', { name: '编辑', exact: true }).click();
      await page.locator('#tab-keys').click();
    };
    const enabled = () => page.locator('#keyRows .key-enabled input').evaluateAll(items => items.map(item => item.checked));
    const selection = () => page.locator('#keyRows .key-selection input').evaluateAll(items => items.map(item => item.checked));
    const selectAll = page.locator('#keySelectAll');
    const save = async () => { await page.locator('#save').click(); await page.locator('#editor').waitFor({ state: 'hidden' }); };
    await edit();
    assert.equal(await page.locator('#keyRows .key-row').count(), 100);
    assert.equal(await page.locator('#keyDeleteSelected').isDisabled(), true);
    assert.equal(await selectAll.isChecked(), false);
    await page.getByRole('checkbox', { name: '选择 Key 1', exact: true }).check();
    await page.getByRole('checkbox', { name: '选择 Key 3', exact: true }).check();
    assert.equal(await selectAll.evaluate(input => input.indeterminate), true);
    await page.locator('#keyDisableSelected').click();
    assert.deepEqual((await enabled()).slice(0, 4), [false, true, false, false]);
    assert.deepEqual(await page.evaluate(() => bulkAccount.keys.slice(0, 4).map(key => key.enabled)), [true, true, false, false]);
    await page.locator('#keyEnableSelected').click();
    assert.deepEqual((await enabled()).slice(0, 4), [true, true, true, false]);
    await page.getByRole('textbox', { name: 'Key 3 名称', exact: true }).fill('Renamed third key');
    await page.locator('#keyRows .key-row').nth(2).getByRole('button', { name: '上移', exact: true }).click();
    assert.deepEqual((await selection()).slice(0, 3), [true, true, false]);
    assert.equal(await page.getByRole('textbox', { name: 'Key 2 名称', exact: true }).inputValue(), 'Renamed third key');
    await selectAll.check();
    assert.equal((await selection()).filter(Boolean).length, 100);
    await page.locator('#keyDisableSelected').click();
    assert.equal((await enabled()).some(Boolean), false);
    assert.equal(await page.locator('#discoveryKey option').count(), 1);
    await selectAll.uncheck();
    assert.equal((await selection()).some(Boolean), false);
    assert.equal(await page.locator('#keyEnableSelected').isDisabled(), true);
    await page.getByRole('checkbox', { name: '选择 Key 1', exact: true }).focus();
    await page.keyboard.press('Space');
    assert.equal(await selectAll.evaluate(input => input.indeterminate), true);
    await selectAll.check(); await page.locator('#keyEnableSelected').click();
    assert.equal((await enabled()).every(Boolean), true);
    assert.equal(await page.locator('#discoveryKey option').count(), 101);
    await page.locator('.key-bulk-toolbar').scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(output, 'desktop.png') });
    for (const width of [390, 320]) {
      await page.setViewportSize({ width, height: 844 });
      await page.locator('.key-bulk-toolbar').scrollIntoViewIfNeeded();
      assert.equal(await selectAll.isVisible(), true);
      assert.equal(await page.locator('#editor').evaluate(node => node.scrollWidth <= node.clientWidth), true);
      assert.equal(await page.locator('#keyRows').evaluate(node => node.scrollWidth <= node.clientWidth), true);
      await page.screenshot({ path: path.join(output, 'mobile-' + width + '.png') });
    }
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.locator('#keyDeleteSelected').click(); await page.locator('#confirmNo').click();
    assert.equal(await page.locator('#keyRows .key-row').count(), 100);
    assert.equal(await page.evaluate(() => calls.filter(call => call.route === 'accounts/save').length), 0);
    await page.evaluate(() => { window.failSave = true; });
    await page.locator('#save').click();
    await page.locator('#formError').filter({ hasText: '模拟保存失败' }).waitFor();
    assert.equal((await selection()).filter(Boolean).length, 100);
    assert.equal(await page.locator('#keyDeleteSelected').isEnabled(), true);
    await page.evaluate(() => { window.failSave = false; }); await save();
    assert.equal(await page.evaluate(() => bulkAccount.keys.every(key => key.enabled)), true);
    assert.equal(await page.evaluate(() => bulkAccount.keys[1].name), 'Renamed third key');
    assert.equal(await page.evaluate(() => calls.filter(call => call.route === 'accounts/save').at(-1).body.keys.every(key => key.secret === '' && !('selected' in key))), true);

    await edit();
    assert.equal(await selectAll.isChecked(), false);
    assert.equal((await selection()).some(Boolean), false);
    await selectAll.check(); await page.locator('#keyDeleteSelected').click(); await page.locator('#confirmYes').click();
    assert.equal(await page.locator('#keyRows .key-row').count(), 0);
    assert.equal(await selectAll.isDisabled(), true);
    assert.equal(await selectAll.evaluate(input => input.indeterminate), false);
    assert.equal(await page.evaluate(() => bulkAccount.keys.length), 100);
    await page.locator('#cancel').click(); await edit();
    assert.equal(await page.locator('#keyRows .key-row').count(), 100);
    await page.getByRole('checkbox', { name: '选择 Key 1', exact: true }).check();
    await page.getByRole('checkbox', { name: '选择 Key 3', exact: true }).check();
    await page.locator('#keyDeleteSelected').click(); await page.locator('#confirmYes').click(); await save();
    assert.equal(await page.evaluate(() => bulkAccount.keys.length), 98);
    assert.deepEqual(await page.evaluate(() => calls.filter(call => call.route === 'accounts/save').at(-1).body.deletedKeyIds), ['key-0', 'key-1']);
    await edit();
    await page.locator('#keyBatch').fill('new-key-one\nnew-key-two'); await page.locator('#keyAdd').click();
    await selectAll.check(); await page.locator('#keyDisableSelected').click(); await save();
    assert.equal(await page.evaluate(() => bulkAccount.keys.length), 100);
    assert.equal(await page.evaluate(() => bulkAccount.keys.some(key => key.enabled)), false);
    assert.deepEqual(await page.evaluate(() => bulkAccount.keys.slice(-2).map(key => key.secret)), ['new-key-one', 'new-key-two']);
    assert.deepEqual(errors, []);
    console.log('Bulk Key operations passed: 100 Keys, partial selection, independent enable state, reorder, cancellation, save failure, persisted enable/disable/delete, new Keys and 320/390px layout.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
