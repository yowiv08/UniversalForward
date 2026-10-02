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
        endpoints: ['/v1/messages'], keyRevision: 7,
        keySelectionMode: 'roundRobin', enabledKeyCount: 2, totalKeyCount: 3,
        keys: [
          { id: 'a', name: 'Alpha', masked: '••••••0001', enabled: true },
          { id: 'b', name: 'Beta', masked: '••••••0002', enabled: true },
          { id: 'c', name: 'Disabled', masked: '••••••0003', enabled: false }
        ]
      };
      window.Router2API = { request: async (method, route, body) => {
        window.calls.push({ method, route, body });
        if (route === 'accounts') return { accounts: [account] };
        if (route === 'accounts/save') {
          if (window.conflict) throw Error('Key 配置已变化，请重新加载后编辑');
          return { account };
        }
        if (route === 'models/discover' || route === 'models/refresh') return { models: ['model-a'] };
        if (route === 'tests/start') {
          job++; cancelled = false; polls = 0;
          const ids = body.keyMode === 'all' ? ['a', 'b'] : [body.keyId || 'a'];
          targets = body.models.flatMap(model => ids.map(keyId => ({ model, keyId })));
          return { id: 'job' + job, state: 0 };
        }
        if (route === 'tests/cancel') { cancelled = true; return { accepted: true }; }
        if (route.startsWith('tests/status')) {
          polls++;
          if (cancelled) return { state: 4, progress: { rows: [], total: targets.length, completed: 0 } };
          if (job !== 2 && polls > 1) return { state: 2, result: { rows: targets.map(row => ({
            ...row, keyName: row.keyId === 'a' ? 'Alpha' : 'Beta', success: true,
            originalStatus: 200, mappedStatus: 200, durationMs: 24, retries: 0
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
    await page.locator('#tab-keys').click();
    assert.equal(await page.locator('#keyRows .card').count(), 3);
    assert.deepEqual(await page.locator('#keyRows input[type=password]').evaluateAll(xs => xs.map(x => x.value)), ['', '', '']);
    await page.locator('#keyBatch').fill(' new-one \n\nnew-two\n');
    await page.locator('#keyAdd').click();
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
    assert.equal(saved.keySelectionMode, 'priority');
    assert.equal(saved.keys[0].id, 'b');
    assert.equal(saved.keys[0].secret, '');
    assert.equal(saved.keys[0].name, 'Beta renamed');
    assert.equal(saved.keys[1].enabled, false);
    assert.deepEqual(saved.deletedKeyIds, ['c']);
    assert.deepEqual(saved.keys.slice(2).map(k => k.secret), ['new-one', 'new-two']);
    assert.equal('apiKey' in saved, false);
    const discovery = await page.evaluate(() => window.calls.filter(x => x.route.startsWith('models/')));
    assert.ok(discovery.every(x => x.body.keyId === 'b' && !('apiKey' in x.body)));
    await page.getByRole('button', { name: '测试连接', exact: true }).click();
    await page.locator('#testKey').selectOption('all');
    await page.getByRole('checkbox', { name: '选择 model-a', exact: true }).check();
    await page.locator('#testSelected').click();
    await page.locator('#confirmYes').click();
    await page.waitForFunction(() => document.querySelector('#testProgress').textContent.includes('已完成'));
    const firstRow = page.locator('#testRows tr').first();
    assert.match(await firstRow.innerText(), /Alpha/);
    assert.match(await firstRow.innerText(), /Beta/);
    assert.equal(await firstRow.locator('td').nth(3).locator('div').count(), 2);
    await page.screenshot({ path: path.join(output, 'multi-key-tests-desktop.png'), fullPage: true });
    await firstRow.getByRole('button', { name: '详情', exact: true }).click();
    assert.equal(await page.locator('#testDetailDialog').isVisible(), true);
    assert.match(await page.locator('#testDetailOverview').innerText(), /model-a/);
    await page.screenshot({ path: path.join(output, 'connection-detail-desktop.png'), fullPage: true });
    await page.locator('#testDetailClose').click();
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
    await page.locator('#tab-keys').click();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: path.join(output, 'multi-key-editor-mobile.png'), fullPage: true });
    assert.deepEqual(errors, []);
    console.log('Multi-Key browser mock passed: edit, batch, duplicate, order, enable/delete, revision conflict, discovery/refresh, all/specified tests, cancellation, desktop/mobile.');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
