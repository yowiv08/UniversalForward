// 管理桥模拟：独立开关、分页摘要、按需正文、禁用状态刷新和手动恢复。
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.addInitScript(() => {
      window.calls = [];
      const account = { id: 'channel', label: '自动禁用测试', baseUrl: 'https://example.test/v1', enabled: true,
        weight: 100, models: ['model-a', 'model-b'], endpoints: ['/v1/responses'], keyRevision: 7,
        keySelectionMode: 'priority', enabledKeyCount: 2, totalKeyCount: 2,
        keys: [{ id: 'a', name: 'Alpha', secret: 'test-a', enabled: true }, { id: 'b', name: 'Beta', secret: 'test-b', enabled: true }] };
      let rows = [];
      window.Router2API = { request: async (method, route, body) => {
        window.calls.push({ method, route, body });
        if (route === 'appearance/settings') return { mode: 'light' };
        if (route === 'execution/settings') return { defaultHostAttempts: 1, execution: { setupTimeoutSeconds: 300, totalSetupTimeoutSeconds: 180, responseTimeoutSeconds: 600 } };
        if (route === 'accounts') return { accounts: [structuredClone(account)] };
        if (route === 'accounts/save') {
          account.keyAutoDisable = body.keyAutoDisable;
          account.keys = body.keys.map(k => ({ ...k, secret: k.secret || account.keys.find(old => old.id === k.id).secret }));
          account.keyRevision++; return { account: structuredClone(account) };
        }
        if (route === 'tests/start') {
          rows = body.models.flatMap((model, i) => account.keys.map((k, j) => ({ model, jobId: 'job', rowId: i * 2 + j,
            keyId: k.id, keyName: k.name, state: j ? 'succeeded' : i ? 'skipped' : 'failed', success: !!j,
            originalStatus: j ? 200 : i ? null : 403, mappedStatus: j ? 200 : i ? null : 403, endpoint: '/v1/responses',
            durationMs: 15, retries: 0, error: j ? null : 'Key 已禁用', responseAvailable: true })));
          Object.assign(account.keys[0], { enabled: false, autoDisabledAt: '2026-11-09T08:00:00Z', autoDisabledStatusCode: 403,
            autoDisabledSource: 'connection-test', autoDisabledFailures: 1 });
          account.enabledKeyCount = 1; account.keyRevision++;
          return { id: 'job', state: 'Queued' };
        }
        if (route.startsWith('tests/status')) return { state: 'Completed', result: { completed: 4, total: 4, resultVersion: 1 } };
        if (route.startsWith('tests/results')) {
          if (window.expiredResults) throw Error('测试结果已清理');
          const offset = Number(new URLSearchParams(route.split('?')[1]).get('offset'));
          return { rows: rows.slice(offset, offset + 2), total: 4, nextOffset: offset + 2, resultVersion: 1 };
        }
        if (route.startsWith('tests/detail')) {
          const offset = Number(new URLSearchParams(route.split('?')[1]).get('offset'));
          return { text: offset ? '\n响应结束' : '中文响应第一段', done: !!offset, nextOffset: 100, truncated: false, evicted: false };
        }
        throw Error('Unexpected route: ' + route);
      } };
    });
    const output = path.resolve(__dirname, '../../artifacts'); fs.mkdirSync(output, { recursive: true });
    const preview = path.join(output, 'key-auto-disable-preview.html');
    fs.writeFileSync(preview, require('./page-source.cjs')());
    await page.goto('file:///' + preview.replaceAll('\\', '/'));
    await page.getByRole('button', { name: '编辑', exact: true }).click();
    await page.locator('#tab-keys').click();
    assert.equal(await page.locator('#keyAutoDisableEnabled').isChecked(), false);
    assert.equal(await page.locator('#keyAutoDisableCodes').inputValue(), '401,403');
    assert.equal(await page.locator('#keyAutoDisableThreshold').inputValue(), '3');
    await page.locator('#keyAutoDisableEnabled').check();
    await page.locator('#keyAutoDisableCodes').fill('200');
    await page.locator('#save').click();
    await page.getByText('自动禁用状态码须为 400–599 的单值或区间', { exact: true }).waitFor();
    await page.locator('#keyAutoDisableCodes').fill('401,403');
    await page.locator('#keyAutoDisableThreshold').fill('1');
    await page.screenshot({ path: path.join(output, 'key-auto-disable-settings.png'), fullPage: true });
    await page.locator('#save').click();
    await page.locator('#dialog').waitFor({ state: 'hidden' }).catch(() => {});
    await page.getByRole('button', { name: '测试连接', exact: true }).click();
    assert.equal(await page.locator('#testAutoDisableKeys').isChecked(), false);
    await page.locator('#testAutoDisableKeys').check();
    await page.locator('#testKey').selectOption('all');
    await page.locator('#testAll').click(); await page.locator('#confirmYes').click();
    await page.getByText('部分成功', { exact: true }).first().waitFor();
    assert.equal(await page.evaluate(() => window.calls.find(c => c.route === 'tests/start').body.autoDisableKeys), true);
    assert.equal(await page.evaluate(() => window.calls.filter(c => c.route.startsWith('tests/detail')).length), 0);
    assert.equal(await page.locator('#testKey option[value="key:a"]').count(), 0);
    assert.match(await page.locator('#testRows').innerText(), /已跳过/);
    await page.screenshot({ path: path.join(output, 'key-auto-disable-results.png'), fullPage: true });
    await page.getByRole('button', { name: '详情', exact: true }).first().click();
    await page.getByText('Alpha\n中文响应第一段', { exact: true }).waitFor();
    await page.locator('#testDetailMore').click();
    await page.waitForFunction(() => document.getElementById('testDetails').textContent.includes('响应结束'));
    await page.locator('#testDetailClose').click(); await page.locator('#testClose').click();
    await page.getByRole('button', { name: '编辑', exact: true }).click(); await page.locator('#tab-keys').click();
    assert.match(await page.locator('#keyRows').innerText(), /自动禁用 · HTTP 403/);
    await page.locator('#keyRows .key-enabled input').first().check(); await page.locator('#save').click();
    assert.equal(await page.evaluate(() => window.calls.filter(c => c.route === 'accounts/save').at(-1).body.keys[0].enabled), true);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('button', { name: '测试连接', exact: true }).click();
    await page.screenshot({ path: path.join(output, 'key-auto-disable-mobile.png'), fullPage: true });
    await page.evaluate(() => { window.expiredResults = true; });
    await page.locator('#testAll').click(); await page.locator('#confirmYes').click();
    await page.waitForFunction(() => document.getElementById('testError').textContent.includes('结果读取失败或已清理'));
    assert.equal(await page.locator('#testAll').isEnabled(), true);
    assert.deepEqual(errors, []);
    console.log('key auto-disable UI passed');
  } finally { await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
