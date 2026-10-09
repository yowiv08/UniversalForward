const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const output = path.resolve(__dirname, '../../artifacts');

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const preview = path.join(output, 'reasoning-mapping-preview.html');
  fs.writeFileSync(preview, require('./page-source.cjs')());
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 1080 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    page.on('dialog', () => errors.push('Unexpected native dialog'));
    await page.addInitScript(() => {
      window.calls = [];
      window.account = {
        id: 'channel', label: '思考映射示例', baseUrl: 'https://upstream.example/v1', enabled: true, weight: 100,
        models: ['model', 'second/model'], endpoints: ['/v1/responses', '/v1/messages'],
        keyRevision: 1, keySelectionMode: 'roundRobin', keys: [{ id: 'key', name: 'Key', masked: '••••test', enabled: true }]
      };
      window.journalRow = {
        id: 'mapped', channel: 'channel', channelLabel: '思考映射示例', started: Date.now(), model: 'model',
        endpoint: '/v1/responses', kind: 'forward', state: 'completed', status: 200,
        receivedReasoning: { 'reasoning.effort': 'low' }, sentReasoning: { 'reasoning.effort': 'max' },
        reportedReasoning: { 'reasoning.effort': 'high' },
        reasoningMapping: { source: 'model', model: 'model', protocol: 'responses', mode: 'map', reason: 'mapped', received: 'low', sent: 'max' }
      };
      window.Router2API = { request: async (method, route, body) => {
        window.calls.push({ method, route, body: structuredClone(body) });
        if (route === 'accounts') return { accounts: [window.account] };
        if (route === 'accounts/save') {
          window.account = { ...window.account, ...structuredClone(body) }; return { account: window.account };
        }
        if (route === 'logs/status') return { available: true, storage: 'memory' };
        if (route.startsWith('logs?')) return { rows: [window.journalRow], page: 1, pageSize: 10, total: 1 };
        if (route.startsWith('logs/detail?')) return { ...window.journalRow, attempts: [], parts: [] };
        throw Error('Unexpected route: ' + route);
      } };
    });
    await page.goto('file:///' + preview.replaceAll('\\', '/'));
    const open = async () => {
      await page.locator('#cards').getByRole('button', { name: '编辑', exact: true }).click();
      await page.locator('#tab-reasoning').click();
    };
    const source = n => page.getByLabel('来源等级 ' + n, { exact: true });
    const target = n => page.getByLabel('目标等级 ' + n, { exact: true });
    const previewText = () => page.locator('#reasoningPreview').innerText();
    const save = async () => {
      await page.locator('#save').click(); await page.locator('#editor').waitFor({ state: 'hidden' });
    };

    await open();
    assert.equal(await page.locator('#reasoningMode').inputValue(), 'off');
    assert.match(await previewText(), /不改写/);
    await page.locator('#reasoningMode').selectOption('map');
    await page.locator('#reasoningAdd').click(); await source(1).fill(' low '); await target(1).fill(' high ');
    await page.locator('#reasoningAdd').click(); await source(2).fill('high'); await target(2).fill('max');
    await page.locator('#reasoningPreviewInput').fill(' LOW ');
    assert.match(await previewText(), /发送 "high"/); // one pass, not low -> high -> max
    await page.locator('#reasoningAdd').click(); await source(3).fill('LOW'); await target(3).fill('max');
    await page.locator('#save').click(); assert.match(await page.locator('#formError').innerText(), /来源等级重复/);
    assert.equal(await page.evaluate(() => window.calls.some(c => c.route === 'accounts/save')), false);
    await page.getByRole('button', { name: '删除映射 3', exact: true }).click();
    await target(1).fill(''); await page.locator('#save').click();
    assert.match(await page.locator('#formError').innerText(), /不能为空/);
    await target(1).fill('high');
    await page.locator('#reasoningDefault').fill('medium');
    await page.locator('#reasoningPreviewMissing').check(); assert.match(await previewText(), /未提供 → 发送 "medium"/);
    await page.locator('#reasoningPreviewMissing').uncheck();
    await page.locator('#reasoningPreviewInput').fill('unknown'); assert.match(await previewText(), /不改写/);
    await page.locator('#reasoningPreviewInput').fill('low');

    await page.locator('#reasoningProtocol').selectOption('messages');
    await page.locator('#reasoningMode').selectOption('fixed'); await page.locator('#reasoningFixed').fill('vendor-level');
    assert.match(await previewText(), /发送 "vendor-level"/);
    await page.locator('#reasoningProtocol').selectOption('responses');
    assert.equal(await source(1).inputValue(), ' low '); assert.equal(await page.locator('#reasoningDefault').inputValue(), 'medium');
    await page.locator('#reasoningScope').selectOption('model');
    assert.equal(await page.locator('#reasoningUseDefault').isChecked(), true);
    assert.equal(await page.locator('#reasoningMode').isDisabled(), true);
    await page.locator('#reasoningUseDefault').uncheck();
    await page.locator('#reasoningMode').selectOption('fixed'); await page.locator('#reasoningFixed').fill('max');
    assert.match(await previewText(), /发送 "max"[\s\S]*模型规则：model/);
    await page.locator('#reasoningScope').selectOption('second/model');
    assert.match(await previewText(), /发送 "high"[\s\S]*渠道默认规则/);
    await page.locator('#reasoningScope').selectOption('model');
    await page.locator('#reasoningMode').selectOption('off'); assert.match(await previewText(), /不改写/);
    await page.locator('#reasoningUseDefault').check(); assert.match(await previewText(), /发送 "high"/);
    await page.locator('#reasoningUseDefault').uncheck(); assert.equal(await page.locator('#reasoningMode').inputValue(), 'off');
    await page.locator('#reasoningMode').selectOption('fixed'); assert.equal(await page.locator('#reasoningFixed').inputValue(), 'max');
    await page.locator('#reasoningProtocol').selectOption('messages');
    assert.equal(await page.locator('#reasoningUseDefault').isChecked(), true);
    assert.match(await previewText(), /vendor-level/);
    await page.locator('#reasoningUseDefault').uncheck(); await page.locator('#reasoningMode').selectOption('off');
    await save();
    const saved = await page.evaluate(() => window.account.reasoningPolicy);
    assert.deepEqual(saved.defaults.responses.mappings, [{ from: 'low', to: 'high' }, { from: 'high', to: 'max' }]);
    assert.equal(saved.models.model.responses.fixedEffort, 'max'); assert.equal(saved.models.model.messages.mode, 'off');
    assert.equal(saved.models['second/model'], undefined);

    await open();
    await page.locator('#reasoningScope').selectOption('model'); assert.equal(await page.locator('#reasoningMode').inputValue(), 'fixed');
    await page.locator('#reasoningProtocol').selectOption('messages'); assert.equal(await page.locator('#reasoningMode').inputValue(), 'off');
    await page.locator('#reasoningScope').selectOption(''); await page.locator('#reasoningProtocol').selectOption('responses');
    await target(1).fill('max'); assert.match(await previewText(), /发送 "max"/);
    await page.screenshot({ path: path.join(output, 'reasoning-mapping-desktop.png'), fullPage: true });
    await page.locator('#reasoningPreview').scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(output, 'reasoning-mapping-desktop-preview.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 }); await page.locator('#tab-reasoning').click();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
    assert.equal(await page.locator('[role=tabpanel]:visible').count(), 1);
    await page.screenshot({ path: path.join(output, 'reasoning-mapping-mobile.png'), fullPage: true });
    await page.locator('#reasoningPreview').scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(output, 'reasoning-mapping-mobile-preview.png'), fullPage: true });
    await save();
    await page.setViewportSize({ width: 1280, height: 1080 });

    await page.locator('#cards').getByRole('button', { name: '日志', exact: true }).click();
    await page.locator('#journalRows tr').waitFor();
    assert.match(await page.locator('#journalRows').innerText(), /客户端 "low" → 发送 "max"/);
    assert.match(await page.locator('#journalRows').innerText(), /模型规则：model/);
    assert.match(await page.locator('#journalRows').innerText(), /上游：reasoning.effort="high"/);
    await page.locator('#journalRows').getByRole('button', { name: '详情' }).click();
    await page.locator('#journalDetail').waitFor({ state: 'visible' });
    assert.match(await page.locator('#journalOverview').innerText(), /客户端[\s\S]*low[\s\S]*实际发送[\s\S]*max/);
    await page.locator('#journalDetailClose').click(); await page.locator('#journalClose').click();

    await open();
    await page.locator('#reasoningMode').selectOption('fixed');
    await page.locator('#reasoningFixed').fill('<img src=x onerror=window.injected=true>');
    assert.equal(await page.locator('#reasoningPreview img').count(), 0);
    await page.locator('#reasoningFixed').fill('max');
    await page.locator('#tab-models').click(); await page.locator('#models').fill('second/model');
    await page.locator('#tab-reasoning').click();
    assert.deepEqual(await page.locator('#reasoningScope option').evaluateAll(options => options.map(x => x.value)), ['', 'second/model']);
    await save(); assert.equal(await page.evaluate(() => Object.keys(window.account.reasoningPolicy.models).length), 0);
    assert.equal(await page.evaluate(() => window.injected), undefined);
    assert.deepEqual(errors, []);
    console.log('Reasoning UI passed: inheritance, protocol isolation, drafts, validation, preview, persistence, logs, safe text, mobile.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
