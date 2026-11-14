// Connection tests report results; only an explicit post-test selection changes Key state.
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
      const account = window.mockAccount = { id: 'channel', label: 'Key 测试渠道', baseUrl: 'https://example.test/v1', enabled: true,
        weight: 100, models: ['model-a', 'model-b'], endpoints: ['/v1/responses'], keyRevision: 7,
        keySelectionMode: 'priority', enabledKeyCount: 2, totalKeyCount: 2,
        keys: [{ id: 'a', name: 'Alpha', masked: '••••st-a', secret: 'test-a', enabled: true, healthVersion: 0 },
          { id: 'b', name: 'Beta', masked: '••••st-b', secret: 'test-b', enabled: true, healthVersion: 0 }] };
      let rows = [], serial = 0;
      window.Router2API = { request: async (method, route, body) => {
        window.calls.push({ method, route, body });
        if (route === 'appearance/settings') return { mode: 'light' };
        if (route === 'execution/settings') return { defaultHostAttempts: 1, execution: { setupTimeoutSeconds: 300, totalSetupTimeoutSeconds: 180, responseTimeoutSeconds: 600 } };
        if (route === 'accounts') return { accounts: [structuredClone(account)] };
        if (route === 'accounts/save') {
          account.keyAutoDisable = body.keyAutoDisable;
          account.keys = body.keys.map(k => {
            const old = account.keys.find(item => item.id === k.id), secret = k.secret || old.secret;
            return { ...old, ...k, secret, healthVersion: old.healthVersion + (old.enabled !== k.enabled || secret !== old.secret ? 1 : 0) };
          });
          account.enabledKeyCount = account.keys.filter(k => k.enabled).length;
          account.keyRevision++; return { account: structuredClone(account) };
        }
        if (route === 'tests/start') {
          serial++;
          const keys = account.keys.filter(k => k.enabled && (body.keyMode !== 'specified' || body.keyId === k.id));
          rows = body.models.flatMap((model, i) => keys.map((k, j) => ({ model, jobId: 'job' + serial, rowId: i * keys.length + j,
            keyId: k.id, keyName: k.name, keyHealthVersion: k.healthVersion,
            state: k.id === 'a' && i === 0 ? 'failed' : 'succeeded', success: k.id !== 'a' || i > 0,
            originalStatus: k.id === 'a' && i === 0 ? 403 : 200, mappedStatus: k.id === 'a' && i === 0 ? 403 : 200,
            endpoint: '/v1/responses', durationMs: 15, retries: 0,
            error: k.id === 'a' && i === 0 ? 'denied' : null, responseAvailable: true })));
          return { id: 'job' + serial, state: 'Queued' };
        }
        if (route.startsWith('tests/status')) return window.jobRunning
          ? { state: 'Running', progress: { completed: 0, total: rows.length } }
          : { state: 'Completed', result: { completed: rows.length, total: rows.length, resultVersion: serial } };
        if (route.startsWith('tests/results')) {
          if (window.expiredResults) throw Error('测试结果已清理');
          const offset = Number(new URLSearchParams(route.split('?')[1]).get('offset'));
          return { rows: rows.slice(offset, offset + 2), total: rows.length, nextOffset: Math.min(rows.length, offset + 2), resultVersion: serial };
        }
        if (route.startsWith('tests/detail')) {
          const offset = Number(new URLSearchParams(route.split('?')[1]).get('offset'));
          return { text: offset ? '\n响应结束' : '中文响应第一段', done: !!offset, nextOffset: 100, truncated: false, evicted: false };
        }
        if (route === 'accounts/keys/disable') {
          if (window.failDisable) throw Error('保存失败，请重试');
          if (body.keyRevision !== account.keyRevision) throw Error('Key 配置已变化，请刷新渠道并核对测试结果后重试');
          const selected = account.keys.filter(k => body.keyIds.includes(k.id) && k.enabled);
          for (const k of selected) { k.enabled = false; k.healthVersion++; }
          if (selected.length) account.keyRevision++;
          account.enabledKeyCount = account.keys.filter(k => k.enabled).length;
          return { account: structuredClone(account), disabledCount: selected.length, disabledKeyIds: selected.map(k => k.id) };
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
    await page.locator('#keyAutoDisableCodes').fill('200'); await page.locator('#save').click();
    await page.getByText('自动禁用状态码须为 400–599 的单值或区间', { exact: true }).waitFor();
    await page.locator('#keyAutoDisableCodes').fill('401,403'); await page.locator('#keyAutoDisableThreshold').fill('1');
    await page.screenshot({ path: path.join(output, 'key-auto-disable-settings.png'), fullPage: true });
    await page.locator('#save').click(); await page.locator('#editor').waitFor({ state: 'hidden' });
    await page.getByRole('button', { name: '测试连接', exact: true }).click();
    assert.equal(await page.locator('#testAutoDisableKeys').count(), 0);
    assert.equal(await page.locator('#testDisableKeysOpen').isVisible(), false);
    await page.locator('#testKey').selectOption('all');
    await page.evaluate(() => { window.jobRunning = true; });
    await page.locator('#testAll').click(); await page.locator('#confirmYes').click();
    await page.locator('#testProgress').filter({ hasText: '测试中' }).waitFor();
    assert.equal(await page.locator('#testAll').isEnabled(), false);
    assert.equal(await page.evaluate(() => 'autoDisableKeys' in window.calls.find(c => c.route === 'tests/start').body), false);
    assert.equal(await page.evaluate(() => mockAccount.keys.every(k => k.enabled)), true);
    await page.evaluate(() => { window.jobRunning = false; });
    await page.locator('#testDisableKeysOpen').waitFor({ state: 'visible' });
    await page.waitForFunction(() => !document.getElementById('testAll').disabled);
    assert.equal(await page.evaluate(() => calls.filter(c => c.route === 'accounts/keys/disable').length), 0);
    assert.equal(await page.locator('#testKey option[value="key:a"]').count(), 1);
    assert.equal(await page.evaluate(() => calls.filter(c => c.route.startsWith('tests/detail')).length), 0);

    await page.locator('#testDisableKeysOpen').click();
    const alpha = page.locator('#testDisableKeyList input[value="a"]'), beta = page.locator('#testDisableKeyList input[value="b"]');
    assert.equal(await alpha.isChecked(), true);
    assert.equal(await beta.isChecked(), false);
    assert.match(await page.locator('#testDisableKeyList').innerText(), /成功 1 · 失败 1/);
    await page.screenshot({ path: path.join(output, 'manual-key-selector.png'), fullPage: true });
    await page.locator('#testDisableSelectNone').click(); assert.equal(await page.locator('#testDisableConfirm').isEnabled(), false);
    await page.locator('#testDisableSelectAll').click(); assert.equal(await beta.isChecked(), true);
    await page.locator('#testDisableSelectFailed').click(); assert.equal(await beta.isChecked(), false);
    await page.locator('#testDisableConfirm').click(); await page.locator('#confirmNo').click();
    assert.equal(await page.evaluate(() => calls.filter(c => c.route === 'accounts/keys/disable').length), 0);

    // A replaced secret must never be disabled from the old test results.
    await page.evaluate(() => { mockAccount.keys[0].secret = 'new-secret'; mockAccount.keys[0].healthVersion++; mockAccount.keyRevision++; });
    await page.locator('#testDisableConfirm').click(); await page.locator('#confirmYes').click();
    await page.locator('#testError').filter({ hasText: 'Key 配置已变化' }).waitFor();
    await alpha.waitFor({ state: 'detached' });
    assert.equal(await page.evaluate(() => mockAccount.keys[0].enabled), true);
    await page.locator('#testDisableCancel').click();
    await page.locator('#testAll').click(); await page.locator('#confirmYes').click();
    await page.waitForFunction(() => !document.getElementById('testAll').disabled);
    await page.locator('#testDisableKeysOpen').click();
    assert.equal(await alpha.isChecked(), true);
    await page.evaluate(() => { window.failDisable = true; });
    await page.locator('#testDisableConfirm').click(); await page.locator('#confirmYes').click();
    await page.locator('#testError').filter({ hasText: '保存失败' }).waitFor();
    await page.waitForFunction(() => !document.getElementById('testDisableConfirm').disabled);
    assert.equal(await page.evaluate(() => mockAccount.keys.every(k => k.enabled)), true);
    await page.evaluate(() => { window.failDisable = false; });
    await page.locator('#testDisableConfirm').click(); await page.locator('#confirmYes').click();
    await page.locator('#testProgress').filter({ hasText: '已禁用 1 个 Key' }).waitFor();
    assert.deepEqual(await page.evaluate(() => calls.filter(c => c.route === 'accounts/keys/disable').at(-1).body.keyIds), ['a']);
    assert.equal(await page.locator('#testKey option[value="key:a"]').count(), 0);
    assert.equal(await page.evaluate(() => mockAccount.keys[1].enabled), true);
    assert.equal(await page.evaluate(() => mockAccount.keys[0].autoDisabledAt), undefined);
    assert.match(await page.locator('#testRows').innerText(), /Alpha · 已禁用/);
    await page.screenshot({ path: path.join(output, 'key-auto-disable-results.png'), fullPage: true });

    await page.getByRole('button', { name: '详情', exact: true }).first().click();
    await page.getByText('Alpha\n中文响应第一段', { exact: true }).waitFor();
    assert.equal(await page.locator('dialog[open]').count(), 1, 'Details must remain inside the test workspace');
    await page.locator('#testDetailMore').click();
    await page.waitForFunction(() => document.getElementById('testDetails').textContent.includes('响应结束'));
    await page.locator('#testDetailClose').click(); await page.locator('#testClose').click();
    await page.getByRole('button', { name: '编辑', exact: true }).click(); await page.locator('#tab-keys').click();
    await page.locator('#keyRows .key-enabled input').first().check(); await page.locator('#save').click();
    await page.locator('#editor').waitFor({ state: 'hidden' });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('button', { name: '测试连接', exact: true }).click();
    await page.locator('#testDisableKeysOpen').click();
    assert.equal(await alpha.count(), 0, 'Re-enabling a Key invalidates its previous test result');
    await page.locator('#testDisableCancel').click();
    await page.getByRole('button', { name: '详情', exact: true }).first().click();
    await page.locator('#testDetails').filter({ hasText: '中文响应' }).waitFor();
    assert.equal(await page.locator('.test-content').isVisible(), false);
    assert.equal(await page.locator('#testDetailDialog').evaluate(n => n.scrollWidth <= n.clientWidth), true);
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('#testDialog').isVisible(), true);
    assert.equal(await page.locator('.test-content').isVisible(), true);
    await page.screenshot({ path: path.join(output, 'key-auto-disable-mobile.png'), fullPage: true });
    await page.evaluate(() => { window.expiredResults = true; });
    await page.locator('#testAll').click(); await page.locator('#confirmYes').click();
    await page.locator('#testError').filter({ hasText: '结果读取失败或已清理' }).waitFor();
    await page.waitForFunction(() => !document.getElementById('testAll').disabled);
    assert.equal(await page.locator('#testDisableKeysOpen').isVisible(), false);
    assert.deepEqual(errors, []);
    console.log('Key tests passed: manual selection, formal-rule isolation, stale generations, save failure, recovery, inline details, pagination and mobile.');
  } finally { await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
