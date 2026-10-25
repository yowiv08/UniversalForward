const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const source = require('./page-source.cjs')();
const output = path.resolve(__dirname, '../../artifacts/v0.3.5-ui');
const { bridge, hostMock, assertContrast } = require('./settings-fixture.cjs');

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const plugin = source.replace('<head>', '<head>' + bridge);
  const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>html,body{margin:0}iframe{border:0;width:100%;height:100vh;display:block}</style></head><body><iframe sandbox="allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox"></iframe><script>(${hostMock})();window.openPlugin=()=>document.querySelector('iframe').srcdoc=${JSON.stringify(plugin).replaceAll('<', '\\u003c')};openPlugin();</script></body></html>`;
  const server = http.createServer((_, response) => { response.setHeader('Content-Type', 'text/html; charset=utf-8'); response.end(html); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1040 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    const frame = page.frameLocator('iframe');
    const mode = async value => { await frame.locator(`.workspace-header [data-appearance-mode=${value}]`).click(); await frame.locator('#appearanceStatus').waitFor({ state: 'hidden' }); };
    await frame.locator('.channel-card').first().waitFor();
    await frame.locator('#appearanceStatus').waitFor({ state: 'hidden' });
    assert.equal(await frame.locator('html').getAttribute('data-mode'), 'light');
    await assertContrast(frame, '.app-main');
    await mode('dark');
    assert.equal(await page.evaluate(() => hostState.mode), 'dark');
    await assertContrast(frame, '.app-main');
    await frame.locator('.channel-card[data-enabled=false]').hover();
    await assertContrast(frame, '.channel-card[data-enabled=false]');
    assert.equal(await frame.locator('.channel-card[data-enabled=false]').evaluate(node => getComputedStyle(node).opacity), '1');
    assert.notEqual(await frame.locator('.channel-card[data-enabled=false]').evaluate(node => getComputedStyle(node).backgroundColor),
      await frame.locator('.channel-section').evaluate(node => getComputedStyle(node).backgroundColor));
    await page.screenshot({ path: path.join(output, 'dark-channels.png') });
    await frame.locator('.channel-menu-trigger').first().click();
    await assertContrast(frame, '.channel-menu:not([hidden])');
    await page.keyboard.press('Escape');

    await frame.locator('#openPluginSettings').click();
    await frame.locator('#defaultHostAttempts').fill('3');
    await frame.locator('#executionSave').click();
    await frame.locator('#executionStatus').filter({ hasText: '已保存' }).waitFor();
    assert.equal(await page.evaluate(() => hostState.defaultHostAttempts), 3);
    await assertContrast(frame, '#pluginSettingsDialog');
    await page.screenshot({ path: path.join(output, 'dark-settings.png') });
    await frame.locator('#defaultHostAttempts').fill('36'); await frame.locator('#executionSave').click();
    assert.match(await frame.locator('#executionStatus').innerText(), /1–35/);
    await page.evaluate(() => { hostState.failExecution = true; });
    await frame.locator('#defaultHostAttempts').fill('2'); await frame.locator('#executionSave').click();
    await frame.locator('#executionStatus').filter({ hasText: '保存失败' }).waitFor();
    assert.equal(await page.evaluate(() => hostState.defaultHostAttempts), 3);
    await page.evaluate(() => { hostState.failExecution = false; });
    await frame.locator('#pluginSettingsClose').click();

    await frame.locator('.channel-edit-button').first().click();
    await frame.locator('#label').fill('未保存草稿');
    await frame.locator('#tab-policy').click();
    await frame.locator('#timeoutAdvisorTitle').click();
    await frame.locator('#timeoutDetails > summary').click();
    await frame.locator('#hostAttemptsHint').filter({ hasText: '尝试 3 次' }).waitFor();
    assert.match(await frame.locator('#hostTimeoutHint').innerText(), /读取预算 600 秒/);
    assert.match(await frame.locator('#internalRetryHint').innerText(), /普通发送 3 次/);
    await frame.locator('#mappingAdd').click();
    await frame.getByLabel('映射 1 原始状态码').fill('503');
    await frame.getByLabel('映射 1 目标状态码').fill('200');
    assert.equal(await frame.locator('#mappingSuccessHint').isVisible(), true);
    await assertContrast(frame, '#editor');
    // Programmatic click tests theme changes during modal editing, without touching the draft.
    await frame.locator('.workspace-header [data-appearance-mode=light]').evaluate(node => node.click());
    await frame.locator('#appearanceStatus').waitFor({ state: 'hidden' });
    assert.equal(await frame.locator('#label').inputValue(), '未保存草稿');
    assert.equal(await frame.getByLabel('映射 1 目标状态码').inputValue(), '200');
    await assertContrast(frame, '#editor');
    await frame.locator('.workspace-header [data-appearance-mode=dark]').evaluate(node => node.click());
    await frame.locator('#appearanceStatus').waitFor({ state: 'hidden' });
    for (const tab of ['basic', 'keys', 'models', 'reasoning', 'headers']) {
      await frame.locator('#tab-' + tab).click();
      await assertContrast(frame, '#editor');
      if (tab === 'reasoning') {
        await frame.locator('.reasoning-mode-card').filter({ hasText: '固定等级' }).click();
        await frame.locator('#reasoningFixed').click();
        await frame.locator('.effort-option').first().waitFor();
        await assertContrast(frame, '.effort-popover');
        await page.screenshot({ path: path.join(output, 'dark-reasoning.png') });
        await page.keyboard.press('Escape');
      }
    }
    await frame.locator('#cancel').click();
    await frame.locator('#openJournal').click();
    await frame.locator('.record-model').waitFor();
    assert.equal(await frame.locator('.record-model').evaluate(node => getComputedStyle(node).fontSize), '15px');
    await frame.locator('#journalRows tr').hover();
    await assertContrast(frame, '#journal');
    await page.screenshot({ path: path.join(output, 'dark-journal.png') });
    await frame.locator('#journalRows button').first().click();
    await frame.locator('#journalDetail').waitFor();
    assert.match(await frame.locator('#journalOverview').innerText(), /本次内部发送\s*3 次/);
    assert.match(await frame.locator('#journalOverview').innerText(), /是，由宿主决定下一步/);
    await assertContrast(frame, '#journalDetail');
    await frame.locator('#journalDetailClose').click();

    await page.evaluate(() => { openPlugin(); });
    await frame.locator('html[data-mode=dark]').waitFor();
    await frame.locator('.channel-card').first().waitFor();
    await page.evaluate(() => { hostState.failAppearanceWrite = true; });
    await frame.locator('.workspace-header [data-appearance-mode=light]').click();
    await frame.locator('#appearanceStatus').filter({ hasText: '已恢复此前模式' }).waitFor();
    assert.equal(await frame.locator('html').getAttribute('data-mode'), 'dark');
    await page.evaluate(() => { hostState.failAppearanceWrite = false; hostState.holdAppearance = true; });
    await frame.locator('.workspace-header [data-appearance-mode=light]').click();
    await frame.locator('.workspace-header [data-appearance-mode=dark]').click();
    await page.waitForFunction(() => hostState.pending.length === 1);
    await page.evaluate(() => hostState.pending.shift()());
    await page.waitForFunction(() => hostState.pending.length === 1);
    await page.evaluate(() => { hostState.holdAppearance = false; hostState.pending.shift()(); });
    await frame.locator('#appearanceStatus').waitFor({ state: 'hidden' });
    assert.equal(await frame.locator('html').getAttribute('data-mode'), 'dark');
    assert.equal(await page.evaluate(() => hostState.mode), 'dark');

    await page.setViewportSize({ width: 390, height: 844 });
    await frame.locator('#appSidebar').evaluate(async node => {
      // Resizing starts the existing mobile navigation transition; verify its settled state.
      await Promise.all(node.getAnimations().map(animation => animation.finished));
    });
    assert.ok(await frame.locator('#appSidebar').evaluate(node => node.getBoundingClientRect().right <= 1),
      'The closed mobile sidebar must be completely outside the viewport');
    assert.equal(await frame.locator('body').evaluate(node => node.scrollWidth <= innerWidth), true);
    await assertContrast(frame, '.app-main');
    await page.screenshot({ path: path.join(output, 'dark-mobile.png') });
    await frame.locator('#openPluginSettings').click();
    assert.equal(await frame.locator('#pluginSettingsDialog').evaluate(node => node.scrollWidth <= node.clientWidth), true);
    await assertContrast(frame, '#pluginSettingsDialog');
    await frame.locator('#pluginSettingsClose').click();
    await page.evaluate(() => { hostState.failAppearanceRead = true; openPlugin(); });
    await frame.locator('#appearanceStatus').filter({ hasText: '读取失败' }).waitFor();
    assert.equal(await frame.locator('html').getAttribute('data-mode'), 'light');
    await frame.locator('.channel-card').first().waitFor();
    assert.deepEqual(errors, []);
    console.log('Settings, persistence, retry hints, dark UI, contrast and sandbox checks passed.');
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
