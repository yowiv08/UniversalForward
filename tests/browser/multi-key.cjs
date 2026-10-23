// 离线模拟宿主管理桥，不连接真实上游。
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../..');
const html = require('./page-source.cjs')();

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    page.on('dialog', dialog => dialog.accept());
    await page.addInitScript(() => {
      window.calls = [];
      window.conflict = false;
      let targets = [], cancelled = false, polls = 0, job = 0;
      const account = {
        id: 'channel', label: '多 Key 渠道', baseUrl: 'https://example.test/v1',
        enabled: true, weight: 100, models: ['model-a', 'model-b'],
        endpoints: ['/v1/responses'], keyRevision: 7,
        keySelectionMode: 'roundRobin', enabledKeyCount: 2, totalKeyCount: 3,
        keys: [
          { id: 'a', name: 'Alpha', secret: 'sk-demo-existing-0001', enabled: true },
          { id: 'b', name: 'Beta', secret: 'sk-demo-existing-0002', enabled: true },
          { id: 'c', name: 'Disabled', secret: 'sk-demo-existing-0003', enabled: false }
        ]
      };
      window.Router2API = { request: async (method, route, body) => {
        window.calls.push({ method, route, body });
        if (route === 'accounts') return { accounts: [account] };
        if (route === 'accounts/save') {
          if (window.conflict) throw Error('Key 配置已变化，请重新加载后编辑');
          account.responsesTransport = body.responsesTransport;
          account.stripResponsesResourceState = body.stripResponsesResourceState;
          account.keys = body.keys.map((key, index) => ({ ...key, id: key.id || 'new-' + index,
            secret: key.secret || account.keys.find(old => old.id === key.id)?.secret || '' }));
          return { account };
        }
        if (route === 'models/discover' || route === 'models/refresh') return { models: ['model-a'] };
        if (route === 'tests/start') {
          job++; cancelled = false; polls = 0;
          const ids = body.keyMode === 'all' ? account.keys.filter(key => key.enabled).map(key => key.id)
            : [body.keyId || account.keys.find(key => key.enabled).id];
          targets = body.models.flatMap(model => ids.map(keyId => ({ model, keyId })));
          return { id: 'job' + job, state: 0 };
        }
        if (route === 'tests/cancel') { cancelled = true; return { accepted: true }; }
        if (route.startsWith('tests/status')) {
          polls++;
          if (cancelled) return { state: 4, progress: { rows: [], total: targets.length, completed: 0 } };
          if (job !== 2 && polls > 1) return { state: 2, result: { rows: targets.map(row => ({
            ...row, keyName: account.keys.find(key => key.id === row.keyId).name, success: true,
            originalStatus: 200, mappedStatus: 200, durationMs: 24, retries: 0, transport: account.responsesTransport, endpoint: '/v1/responses'
          })) } };
          return { state: 1, progress: { rows: [], completed: 0, total: targets.length, running: targets[0].model } };
        }
        throw Error('Unexpected API ' + route);
      } };
    });
    const output = path.join(root, 'artifacts');
    fs.mkdirSync(output, { recursive: true });
    const preview = path.join(output, 'multi-key-preview.html');
    fs.writeFileSync(preview, html);
    await page.goto('file:///' + preview.replaceAll('\\', '/'));
    await page.getByRole('button', { name: '编辑', exact: true }).click();
    assert.equal(await page.locator('#responsesTransport').inputValue(), 'http');
    assert.equal(await page.locator('#stripResponsesResourceState').isChecked(), false);
    await page.locator('#responsesTransport').selectOption('websocket');
    await page.locator('#stripResponsesResourceState').check();
    await page.screenshot({ path: path.join(output, 'websocket-settings-desktop.png'), fullPage: true });
    await page.locator('#tab-keys').click();
    assert.equal(await page.locator('#keyRows .card').count(), 3);
    assert.equal(await page.locator('#keyRows input[type=password]').count(), 0);
    assert.deepEqual(await page.locator('#keyRows .key-secret-cell input').evaluateAll(xs => xs.map(x => x.value)),
      ['sk-demo-existing-0001', 'sk-demo-existing-0002', 'sk-demo-existing-0003']);
    assert.equal(await page.locator('#keyRows .key-secret-cell small').count(), 0);
    const aligned = await page.locator('#keyRows .key-row').evaluateAll(rows => rows.every(row => {
      const name = row.querySelector('.key-name-cell input').getBoundingClientRect();
      const secret = row.querySelector('.key-secret-cell input').getBoundingClientRect();
      const enabled = row.querySelector('.key-enabled').getBoundingClientRect();
      const actions = row.querySelector('.actions').getBoundingClientRect();
      return Math.abs(name.top - secret.top) <= 1 && Math.abs(name.height - secret.height) <= 1 &&
        Math.abs(name.top + name.height / 2 - (enabled.top + enabled.height / 2)) <= 1 &&
        Math.abs(name.top + name.height / 2 - (actions.top + actions.height / 2)) <= 1;
    }));
    assert.equal(aligned, true);
    await page.screenshot({ path: path.join(output, 'multi-key-editor-desktop.png'), fullPage: true });
    await page.locator('#keyBatch').fill(' new-one \n\nnew-two\n');
    await page.locator('#keyAdd').click();
    assert.equal(await page.locator('#keyRows .card').count(), 5);
    assert.deepEqual(await page.locator('#keyRows .key-name-cell input').evaluateAll(xs => xs.slice(3).map(x => x.value)), ['-one', '-two']);
    await page.getByRole('textbox', { name: 'Key 4 密钥', exact: true }).fill('new-one-updated');
    assert.equal(await page.getByRole('textbox', { name: 'Key 4 名称', exact: true }).inputValue(), 'ated');
    await page.getByRole('textbox', { name: 'Key 4 密钥', exact: true }).fill('new-one');
    await page.getByRole('textbox', { name: 'Key 5 名称', exact: true }).fill('Custom name');
    await page.getByRole('textbox', { name: 'Key 5 密钥', exact: true }).fill('new-two-updated');
    assert.equal(await page.getByRole('textbox', { name: 'Key 5 名称', exact: true }).inputValue(), 'Custom name');
    await page.getByRole('textbox', { name: 'Key 5 密钥', exact: true }).fill('new-two');
    await page.getByRole('textbox', { name: 'Key 1 密钥', exact: true }).fill('temporary-key');
    assert.equal(await page.getByRole('textbox', { name: 'Key 1 名称', exact: true }).inputValue(), 'Alpha');
    await page.getByRole('textbox', { name: 'Key 1 密钥', exact: true }).fill('');
    await page.locator('#keyBatch').fill('sk-demo-existing-0001'); await page.locator('#keyAdd').click();
    assert.match(await page.locator('#formError').innerText(), /重复/);
    assert.equal(await page.locator('#keyRows .card').count(), 5);
    await page.locator('#keyBatch').fill('same\nsame');
    await page.locator('#keyAdd').click();
    assert.match(await page.locator('#formError').innerText(), /重复/);
    await page.locator('#keyBatch').fill('');
    await page.locator('#keyRows .card').nth(1).getByRole('button', { name: '上移' }).click();
    await page.getByRole('textbox', { name: 'Key 1 名称', exact: true }).fill('Beta renamed');
    await page.locator('#keyRows .card').nth(1).getByRole('checkbox').uncheck();
    await page.locator('#keyRows .card').nth(2).getByRole('button', { name: '删除 Key' }).click();
    await page.locator('#confirmYes').click();
    await page.locator('#keySelectionMode').selectOption('priority');
    await page.locator('#tab-models').click();
    await page.locator('#discoveryKey').selectOption('b');
    await page.locator('#discover').click();
    await page.locator('#refreshModels').click();
    await page.evaluate(() => { window.conflict = true; });
    await page.locator('#save').click();
    await page.waitForFunction(() => document.querySelector('#formError').textContent.includes('配置已变化'));
    assert.equal(await page.locator('#editor').evaluate(x => x.open), true);
    await page.evaluate(() => { window.conflict = false; });
    await page.locator('#save').click();
    await page.waitForFunction(() => !document.querySelector('#editor').open);
    const saved = await page.evaluate(() => window.calls.filter(x => x.route === 'accounts/save').at(-1).body);
    assert.equal(saved.keyRevision, 7);
    assert.equal(saved.responsesTransport, 'websocket');
    assert.equal(saved.stripResponsesResourceState, true);
    assert.equal(saved.keySelectionMode, 'priority');
    assert.equal(saved.keys[0].id, 'b');
    assert.equal(saved.keys[0].secret, '');
    assert.equal(saved.keys[0].name, 'Beta renamed');
    assert.equal(saved.keys[1].enabled, false);
    assert.deepEqual(saved.deletedKeyIds, ['c']);
    assert.deepEqual(saved.keys.slice(2).map(k => k.secret), ['new-one', 'new-two']);
    assert.deepEqual(saved.keys.slice(2).map(k => k.name), ['-one', 'Custom name']);
    assert.equal('apiKey' in saved, false);
    const discovery = await page.evaluate(() => window.calls.filter(x => x.route.startsWith('models/')));
    assert.ok(discovery.every(x => x.body.keyId === 'b' && !('apiKey' in x.body)));
    await page.getByRole('button', { name: '测试连接', exact: true }).click();
    assert.equal(await page.locator('#testSelected').isDisabled(), true);
    await page.locator('#testKey').selectOption('all');
    await page.getByRole('checkbox', { name: '选择 model-a', exact: true }).check();
    assert.match(await page.locator('#testSelected').innerText(), /1 个/);
    assert.equal(await page.locator('#testSelectAll').evaluate(input => input.indeterminate), true);
    await page.locator('#testSelectAll').check();
    assert.match(await page.locator('#testSelected').innerText(), /2 个/);
    await page.getByRole('checkbox', { name: '选择 model-b', exact: true }).uncheck();
    await page.locator('#testSelected').click();
    await page.locator('#confirmYes').click();
    await page.waitForFunction(() => document.querySelector('#testProgress').textContent.includes('已完成'));
    const firstRow = page.locator('#testRows tr').first();
    assert.match(await firstRow.innerText(), /Beta renamed/);
    assert.match(await firstRow.innerText(), /Custom name/);
    assert.doesNotMatch(await firstRow.innerText(), /Alpha/);
    assert.equal(await firstRow.locator('.result-line').count(), 3);
    assert.deepEqual(await firstRow.locator('.result-endpoint').allTextContents(), ['/v1/responses']);
    assert.equal(await page.locator('#testConfiguration').evaluate(details => details.open), false, 'Submitted tests should focus the results');
    for (const viewport of [{ width: 1440, height: 900 }, { width: 1280, height: 660 }]) {
      await page.setViewportSize(viewport);
      const layout = await page.locator('#testDialog').evaluate(dialog => {
        const bounds = dialog.getBoundingClientRect();
        const lastRow = dialog.querySelector('#testRows tr:last-child').getBoundingClientRect();
        const footer = dialog.querySelector('.test-pagination').getBoundingClientRect();
        return {
          compactWidth: bounds.width <= 960,
          backdropVisible: bounds.top >= 23 && bounds.bottom <= innerHeight - 23,
          resultsVisible: lastRow.bottom <= footer.top,
          footerVisible: footer.bottom <= bounds.bottom,
          noHorizontalOverflow: dialog.scrollWidth <= dialog.clientWidth
        };
      });
      assert.deepEqual(layout, {
        compactWidth: true, backdropVisible: true, resultsVisible: true,
        footerVisible: true, noHorizontalOverflow: true
      }, JSON.stringify(viewport));
    }
    await page.screenshot({ path: path.join(output, 'connection-compact-desktop.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.locator('#testConfiguration summary').click();
    assert.equal(await page.locator('#testConfiguration').evaluate(details => details.open), true);
    await page.locator('#testConfiguration summary').click();
    assert.equal(await page.locator('#testConfiguration').evaluate(details => details.open), false);
    assert.equal(await page.locator('#testDialog').evaluate(dialog => dialog.scrollWidth <= dialog.clientWidth), true);
    await page.screenshot({ path: path.join(output, 'connection-compact-mobile.png'), fullPage: true });
    await page.setViewportSize({ width: 1200, height: 900 });
    await page.locator('#testConfiguration summary').click();
    await page.screenshot({ path: path.join(output, 'multi-key-tests-desktop.png'), fullPage: true });
    await firstRow.getByRole('button', { name: '详情', exact: true }).click();
    assert.equal(await page.locator('#testDetailDialog').isVisible(), true);
    assert.match(await page.locator('#testDetailOverview').innerText(), /model-a/);
    assert.match(await page.locator('#testDetailOverview').innerText(), /WebSocket/);
    assert.match(await page.locator('#testDetailOverview').innerText(), /测试端点\s+\/v1\/responses/);
    assert.match(await page.locator('#testDetails').innerText(), /测试端点：\/v1\/responses/);
    await page.screenshot({ path: path.join(output, 'connection-detail-desktop.png'), fullPage: true });
    await page.locator('#testDetailTitle').click();
    assert.equal(await page.locator('#testDetailDialog').isVisible(), true);
    const detailBounds = await page.locator('#testDetailDialog').boundingBox();
    await page.mouse.click(detailBounds.x - 8, detailBounds.y + 50);
    assert.equal(await page.locator('#testDetailDialog').isVisible(), false);
    assert.equal(await page.locator('#testDialog').isVisible(), true);
    await page.locator('#testKey').selectOption('key:b');
    await page.locator('#testAll').click();
    await page.locator('#confirmYes').click();
    await page.locator('#testCancel').click();
    await page.waitForFunction(() => document.querySelector('#testProgress').textContent.includes('已取消'));
    assert.match(await page.locator('#testRows').innerText(), /已取消/);
    const tests = await page.evaluate(() => window.calls.filter(x => x.route === 'tests/start').map(x => x.body));
    assert.equal(tests[0].keyMode, 'all');
    assert.equal(tests[1].keyMode, 'specified');
    assert.equal(tests[1].keyId, 'b');
    await page.locator('#testClose').click();
    await page.getByRole('button', { name: '编辑', exact: true }).click();
    assert.equal(await page.locator('#responsesTransport').inputValue(), 'websocket');
    assert.equal(await page.locator('#stripResponsesResourceState').isChecked(), true);
    await page.locator('#tab-keys').click();
    assert.deepEqual(await page.locator('#keyRows .key-secret-cell input').evaluateAll(xs => xs.map(x => x.value)),
      ['sk-demo-existing-0002', 'sk-demo-existing-0001', 'new-one', 'new-two']);
    assert.deepEqual(await page.locator('#keyRows .key-name-cell input').evaluateAll(xs => xs.map(x => x.value)),
      ['Beta renamed', 'Alpha', '-one', 'Custom name']);
    await page.locator('#tab-basic').click();
    await page.locator('#stripResponsesResourceState').uncheck();
    await page.getByRole('button', { name: '测试连接', exact: true, includeHidden: true }).evaluate(button => button.click());
    assert.match(await page.locator('#formError').innerText(), /连接设置尚未保存，请先保存渠道再测试/);
    assert.equal(await page.locator('#testDialog').evaluate(dialog => dialog.open), false);
    assert.equal(await page.evaluate(() => window.calls.filter(x => x.route === 'tests/start').length), tests.length);
    await page.screenshot({ path: path.join(output, 'responses-resource-state-unsaved-desktop.png'), fullPage: true });
    await page.locator('#save').click();
    await page.waitForFunction(() => !document.querySelector('#editor').open);
    assert.equal(await page.evaluate(() => window.calls.filter(x => x.route === 'accounts/save').at(-1).body.stripResponsesResourceState), false);
    await page.getByRole('button', { name: '编辑', exact: true }).click();
    assert.equal(await page.locator('#stripResponsesResourceState').isChecked(), false);
    await page.screenshot({ path: path.join(output, 'responses-resource-state-disabled-desktop.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: path.join(output, 'websocket-settings-mobile.png'), fullPage: true });
    await page.locator('#tab-keys').click();
    await page.screenshot({ path: path.join(output, 'multi-key-editor-mobile.png'), fullPage: true });
    assert.deepEqual(errors, []);
    console.log('Multi-Key browser mock passed: edit, batch, duplicate, order, enable/delete, revision conflict, discovery/refresh, all/specified tests, cancellation, resource-state setting persistence and unsaved test guard, desktop/mobile.');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
