const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      window.catalog = Array.from({ length: 12 }, (_, i) => ({
        id: 'channel-' + i, label: (i < 4 ? 'A组渠道 ' : 'B组渠道 ') + (i + 1),
        baseUrl: 'https://upstream-' + i + '.example/v1', enabled: i < 2, weight: 100,
        models: ['model-' + i % 3], endpoints: ['/v1/responses', '/v1/chat/completions'],
        extraParams: { apiKeyHeader: 'x-api-key', apiKeyPrefix: '' },
        keys: [{ id: 'key-' + i, name: 'Test key', secret: 'test-only-key-' + i, enabled: true }]
      }));
      window.originalCatalog = structuredClone(catalog);
      window.calls = []; window.failedEnableIds = []; window.failedDeleteIds = [];
      window.activeChanges = 0; window.maxActiveChanges = 0;
      window.Router2API = { request: async (method, route, input) => {
        const body = input ? structuredClone(input) : input;
        calls.push({ method, route, body });
        if (route === 'appearance/settings') return { mode: 'light' };
        if (route === 'execution/settings') return { defaultHostAttempts: 1, execution: { setupTimeoutSeconds: 300, totalSetupTimeoutSeconds: 180, responseTimeoutSeconds: 600 } };
        if (route === 'accounts') {
          if (window.failReload) throw Error('模拟列表刷新失败');
          return { accounts: structuredClone(catalog) };
        }
        if (route === 'accounts/enabled' || route === 'accounts/delete') {
          activeChanges++; maxActiveChanges = Math.max(maxActiveChanges, activeChanges);
          try {
            if (window.holdChange) await new Promise(resolve => { window.releaseChange = () => { window.holdChange = false; resolve(); }; });
            const index = catalog.findIndex(account => account.id === body.id);
            if (index < 0) throw Error('渠道不存在');
            if (route === 'accounts/enabled') {
              if (failedEnableIds.includes(body.id)) throw Error('模拟配置保存冲突');
              catalog[index] = { ...catalog[index], enabled: body.enabled };
              return { account: structuredClone(catalog[index]) };
            }
            if (failedDeleteIds.includes(body.id)) throw Error('模拟删除失败');
            catalog.splice(index, 1); return { deleted: true };
          } finally { activeChanges--; }
        }
        throw Error('Unexpected route: ' + route);
      } };
    });
    const output = path.resolve(__dirname, '../../artifacts/channel-bulk-operations');
    fs.mkdirSync(output, { recursive: true });
    const preview = path.join(output, 'preview.html');
    fs.writeFileSync(preview, require('./page-source.cjs')());
    await page.goto('file:///' + preview.split(path.sep).join('/'));
    await page.locator('#cards .channel-card').nth(11).waitFor();
    const field = id => page.locator('#' + id);
    const all = field('channelSelectAll');
    const selected = () => page.locator('#cards .channel-selection input:checked').evaluateAll(nodes => nodes.map(node => node.value));
    const select = id => page.locator('#cards .channel-selection input[value="' + id + '"]');
    const finish = () => page.waitForFunction(() => !document.getElementById('filter').disabled && document.getElementById('channelSelectionSummary').textContent.startsWith('已选'));
    assert.equal(await field('channelDeleteSelected').isDisabled(), true);
    await select('channel-0').check(); await select('channel-4').check();
    assert.equal(await all.evaluate(node => node.indeterminate), true);
    await page.evaluate(() => { window.holdChange = true; });
    await field('channelDisableSelected').click();
    await page.waitForFunction(() => !!window.releaseChange);
    for (const selector of ['#filter', '#statusFilter', '#showEnabled', '#showDisabled', '#add', '#reload',
      '#channelSelectAll', '#channelClearSelection', '#channelEnableSelected', '#channelDisableSelected', '#channelDeleteSelected',
      '#cards .channel-selection input', '#cards .channel-name-button', '#cards .channel-actions button', '#cards .model-count-button']) {
      assert.equal(await page.locator(selector).evaluateAll(nodes => nodes.length > 0 && nodes.every(node => node.disabled)), true, selector + ' must be locked during the operation');
    }
    await field('viewList').click();
    assert.deepEqual(await selected(), ['channel-0', 'channel-4']);
    await page.evaluate(() => window.releaseChange()); await finish();
    assert.deepEqual(await page.evaluate(() => catalog.slice(0, 5).map(account => account.enabled)), [false, true, false, false, false]);
    assert.equal(await page.evaluate(() => calls.filter(call => call.route === 'accounts/enabled').length), 1);
    assert.deepEqual(await selected(), []);

    await all.check();
    await page.evaluate(() => { window.failedEnableIds = ['channel-5', 'channel-8']; });
    await field('channelEnableSelected').click(); await finish();
    assert.match(await field('channelBulkStatus').innerText(), /成功 9 个，失败 2 个/);
    assert.match(await field('channelBulkStatus').innerText(), /B组渠道 6：模拟配置保存冲突/);
    assert.deepEqual(await selected(), ['channel-5', 'channel-8']);
    assert.equal(await page.evaluate(() => catalog.filter(account => account.enabled).length), 10);
    await page.evaluate(() => { window.failedEnableIds = []; });
    await field('channelEnableSelected').click(); await finish();
    assert.equal(await page.evaluate(() => catalog.every(account => account.enabled)), true);
    assert.equal(await page.evaluate(() => catalog.every(account => {
      const original = originalCatalog.find(old => old.id === account.id);
      return JSON.stringify(account.keys) === JSON.stringify(original.keys)
        && JSON.stringify(account.models) === JSON.stringify(original.models)
        && JSON.stringify(account.extraParams) === JSON.stringify(original.extraParams);
    })), true);

    await all.check(); await field('filter').fill('A组渠道');
    assert.deepEqual(await selected(), ['channel-0', 'channel-1', 'channel-2', 'channel-3']);
    assert.equal(await all.isChecked(), true);
    for (const view of ['Grid', 'List']) {
      await field('view' + view).click();
      assert.equal((await selected()).length, 4);
      await page.locator('.channel-bulk-toolbar').scrollIntoViewIfNeeded();
      await page.screenshot({ path: path.join(output, view.toLowerCase() + '-1440.png') });
    }
    for (const width of [390, 320]) {
      await page.setViewportSize({ width, height: 844 });
      for (const view of ['Grid', 'List']) {
        await field('view' + view).click();
        await page.locator('.channel-bulk-toolbar').scrollIntoViewIfNeeded();
        assert.equal(await page.locator('.channel-section').evaluate(node => node.scrollWidth <= node.clientWidth + 1), true);
        assert.equal(await field('channelDeleteSelected').isVisible(), true);
        await page.screenshot({ path: path.join(output, view.toLowerCase() + '-' + width + '.png') });
      }
    }
    await page.setViewportSize({ width: 1440, height: 1000 });
    await field('filter').fill('');
    assert.equal((await selected()).length, 4, 'Clearing the filter must not restore hidden selections');
    assert.equal(await all.evaluate(node => node.indeterminate), true);
    await field('filter').fill('A组渠道'); await field('statusFilter').selectOption('enabled');
    await field('channelDisableSelected').click(); await finish();
    assert.equal(await page.locator('#cards .channel-card').count(), 0);
    assert.equal(await all.isDisabled(), true);
    assert.equal(await page.evaluate(() => catalog.slice(0, 4).some(account => account.enabled)), false, 'All selected channels must finish even when they leave the filter');
    await field('statusFilter').selectOption('all'); await all.check();
    const beforeDelete = await page.evaluate(() => calls.filter(call => call.route === 'accounts/delete').length);
    await field('channelDeleteSelected').click();
    assert.match(await field('confirmText').innerText(), /4 个渠道/);
    await field('confirmNo').click(); await finish();
    assert.equal((await selected()).length, 4);
    assert.equal(await page.evaluate(() => calls.filter(call => call.route === 'accounts/delete').length), beforeDelete);
    await page.evaluate(() => { window.failedDeleteIds = ['channel-1']; });
    await field('channelDeleteSelected').click(); await field('confirmYes').click(); await finish();
    assert.equal(await page.evaluate(() => catalog.length), 9);
    assert.deepEqual(await selected(), ['channel-1']);
    assert.match(await field('channelBulkStatus').innerText(), /成功 3 个，失败 1 个/);
    await page.evaluate(() => { window.failedDeleteIds = []; });
    await field('channelDeleteSelected').click(); await field('confirmYes').click(); await finish();
    assert.equal(await page.evaluate(() => catalog.length), 8);
    assert.equal(await all.isDisabled(), true);

    await field('filter').fill(''); await all.check();
    await page.evaluate(() => { window.failReload = true; });
    await field('channelDisableSelected').click(); await finish();
    assert.match(await field('channelBulkStatus').innerText(), /渠道列表刷新失败/);
    assert.equal(await page.locator('#cards .channel-card[data-enabled=false]').count(), 8);
    assert.equal(await page.evaluate(() => catalog.some(account => account.enabled)), false);
    await page.evaluate(() => { window.failReload = false; });
    await field('reload').click(); await page.waitForFunction(() => !document.getElementById('reload').disabled);
    await all.check(); await field('channelClearSelection').click();
    assert.deepEqual(await selected(), []);
    await select('channel-4').focus(); await page.keyboard.press('Space');
    assert.deepEqual(await selected(), ['channel-4']);
    await all.check(); await field('channelDeleteSelected').click(); await field('confirmYes').click(); await finish();
    assert.equal(await page.evaluate(() => catalog.length), 0);
    assert.equal(await field('channelSelectAll').isDisabled(), true);
    assert.equal(await field('add').isEnabled(), true);
    assert.equal(await page.evaluate(() => maxActiveChanges), 1, 'Do not flood the backend with concurrent channel writes');
    assert.equal(await page.evaluate(() => calls.filter(call => call.route === 'accounts/enabled')
      .every(call => Object.keys(call.body).sort().join(',') === 'enabled,id')), true);
    assert.equal(await page.evaluate(() => calls.filter(call => call.route === 'accounts/delete')
      .every(call => Object.keys(call.body).join(',') === 'id')), true);
    assert.deepEqual(errors, []);
    console.log('Channel bulk operations passed: card/list selection, filtered scope, locked serial writes, enable/disable, partial failures and retry, unchanged credentials/models, delete confirmation, empty lists, refresh failure and 320/390px layouts.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
