const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const { bridge, hostMock, assertContrast } = require('./settings-fixture.cjs');
const source = require('./page-source.cjs')();
const output = path.resolve(__dirname, '../../artifacts/v0.3.6/ui');

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
    const field = name => frame.locator('#' + name);
    const fill = async (name, value) => field(name).fill(String(value));
    const expected = async seconds => {
      await field('timeoutRecommendation').filter({ hasText: new RegExp('^' + seconds + '$') }).waitFor();
    };
    const openEditor = async (expand = true) => {
      await frame.locator('.channel-edit-button').first().click(); await field('tab-policy').click();
      assert.equal(await field('timeoutApply').isVisible(), false, 'Advice starts collapsed each time the editor opens');
      if (expand) await field('timeoutAdvisorTitle').click();
    };
    const save = async () => {
      await field('save').click(); await field('editor').waitFor({ state: 'hidden' });
      await field('message').filter({ hasText: '渠道已保存' }).waitFor();
    };
    const limits = async (setup, total, reading) => {
      await field('cancel').click();
      await page.evaluate(([setup, total, reading]) => {
        hostState.execution = { setupTimeoutSeconds: setup, totalSetupTimeoutSeconds: total, responseTimeoutSeconds: reading };
      }, [setup, total, reading]);
      await openEditor();
    };
    const mode = async value => {
      await frame.locator(`.workspace-header [data-appearance-mode=${value}]`).evaluate(node => node.click());
      await field('appearanceStatus').waitFor({ state: 'hidden' });
    };
    await frame.locator('.channel-card').first().waitFor();
    // Regression: 1-second channel limits must not inherit the host's 600-second maximum as a recommendation.
    await page.evaluate(() => {
      hostState.execution.setupTimeoutSeconds = 180;
      Object.assign(hostState.accounts[0].requestPolicy, { maxRetries: 1, headerTimeoutSeconds: 1,
        totalTimeoutSeconds: 1, streamIdleTimeoutSeconds: 1, retryStatusCodes: '429,500-520',
        rateLimitRetryEnabled: true, emptyResponseRetryEnabled: true, responseMaxRetries: 1, responseRetryIntervalSeconds: 2 });
      openPlugin();
    });
    await frame.locator('.channel-card').first().waitFor(); await openEditor(); await expected(9);
    assert.equal(await field('totalTimeoutSeconds').inputValue(), '1');
    assert.match(await field('timeoutCalculation').innerText(), /基础等待 6 秒.*重试间隔 2 秒.*520 退避 0.25 秒.*合计 8.25 秒/);
    assert.match(await field('timeoutSendDetail').textContent(), /最多 3 次.*共享重试 1 次/);
    assert.equal(await field('timeoutEstimateHint').isVisible(), true);
    assert.match(await field('timeoutEstimateHint').innerText(), /基础建议.*不含持续输出时间/);
    await field('timeoutAdvisorTitle').scrollIntoViewIfNeeded();
    await assertContrast(frame, '#panel-policy');
    await page.screenshot({ path: path.join(output, 'one-second-light.png') });
    await mode('dark'); await assertContrast(frame, '#panel-policy');
    await page.screenshot({ path: path.join(output, 'one-second-dark.png') });
    await mode('light');
    await field('timeoutApply').click(); await save(); await openEditor(); await expected(9);
    assert.equal(await field('totalTimeoutSeconds').inputValue(), '9');
    await limits(180, 180, 300); await expected(9);
    await limits(180, 180, 3600); await expected(9);
    await limits(180, 180, 600); await expected(9);
    await field('rateLimitRetryEnabled').uncheck(); await expected(9);
    await field('emptyResponseRetryEnabled').uncheck(); await expected(5);
    await fill('retryStatusCodes', '429,500-503'); await expected(4);
    await fill('maxRetries', 0); await expected(2);
    await fill('streamIdleTimeoutSeconds', 3); await expected(4);
    await field('cancel').click();

    await page.evaluate(() => {
      hostState.defaultHostAttempts = 3;
      hostState.execution.setupTimeoutSeconds = 180;
      Object.assign(hostState.accounts[0].requestPolicy, { maxRetries: 10, headerTimeoutSeconds: 20,
        totalTimeoutSeconds: 200, streamIdleTimeoutSeconds: 40, retryStatusCodes: '429,500-503',
        rateLimitRetryEnabled: false, emptyResponseRetryEnabled: false, responseMaxRetries: 3, responseRetryIntervalSeconds: 5 });
      openPlugin();
    });
    await frame.locator('.channel-card').first().waitFor(); await openEditor(false);
    await assertContrast(frame, '#panel-policy');
    await page.screenshot({ path: path.join(output, 'timeout-collapsed-light.png') });
    await mode('dark'); await assertContrast(frame, '#panel-policy');
    await page.screenshot({ path: path.join(output, 'timeout-collapsed-dark.png') });
    await mode('light');
    await field('timeoutAdvisorTitle').focus(); await field('timeoutAdvisorTitle').press('Enter');
    await expected(660);
    assert.equal(await field('totalTimeoutSeconds').inputValue(), '200', 'Opening must preserve saved values');
    assert.match(await field('timeoutDuration').innerText(), /11 分钟/);
    assert.match(await field('timeoutCalculation').innerText(), /基础等待 660 秒.*响应头 20 秒.*空闲 40 秒/);
    assert.match(await field('timeoutBudgetWarning').innerText(), /预估 220 秒.*宿主 180 秒/);
    assert.match(await field('hostAttemptsHint').textContent(), /尝试 3 次.*不乘宿主次数/);
    assert.match(await field('timeoutCurrent').innerText(), /当前 200 秒.*少 460 秒/);
    await field('timeoutDetails').evaluate(node => { node.open = true; });
    assert.match(await field('timeoutSendDetail').innerText(), /最多 11 次内部发送/);
    assert.match(await field('timeoutIdleDetail').innerText(), /40 秒.*不能据此推算完整输出时长/);
    await assertContrast(frame, '#panel-policy');
    await field('timeoutDetails').evaluate(node => { node.open = false; });
    await field('timeoutApply').hover(); await assertContrast(frame, '.timeout-advisor');
    await field('totalTimeoutSeconds').focus(); await assertContrast(frame, '#panel-policy');
    await field('timeoutAdvisorTitle').scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(output, 'timeout-light.png') });
    await mode('dark'); await assertContrast(frame, '#panel-policy');
    await page.screenshot({ path: path.join(output, 'timeout-dark.png') });
    await fill('streamIdleTimeoutSeconds', 80); await expected(780);
    await fill('streamIdleTimeoutSeconds', 40);
    await field('timeoutAdvisorTitle').click();
    assert.equal(await field('timeoutApply').isVisible(), false);
    assert.equal(await field('totalTimeoutSeconds').inputValue(), '200');
    await field('timeoutAdvisorTitle').click(); await expected(660);
    await field('tab-basic').click(); await fill('label', '总时限草稿'); await field('tab-policy').click();
    await field('timeoutApply').focus(); await field('timeoutApply').press('Enter');
    assert.equal(await field('totalTimeoutSeconds').inputValue(), '660');
    assert.equal(await field('label').inputValue(), '总时限草稿');
    assert.equal(await field('timeoutApply').isDisabled(), true);
    assert.equal(await page.evaluate(() => hostState.calls.filter(call => call.route === 'accounts/save').length), 1,
      'Applying the suggestion must edit only the draft');
    await save(); await openEditor(); await expected(660);
    assert.equal(await field('totalTimeoutSeconds').inputValue(), '660', 'Applied value survives saving and reopening');
    await fill('totalTimeoutSeconds', 345); await save(); await openEditor(); await expected(660);
    assert.equal(await field('totalTimeoutSeconds').inputValue(), '345', 'Manual override survives saving and reopening');
    await fill('maxRetries', 0); await expected(60);
    assert.equal(await field('totalTimeoutSeconds').inputValue(), '345', 'Changing inputs must not overwrite a manual total');
    assert.equal(await field('timeoutBudgetWarning').isVisible(), false);
    for (const [name, value, restore] of [['maxRetries', '', 0], ['maxRetries', '2.5', 0],
      ['maxRetries', '11', 0], ['headerTimeoutSeconds', '0', 20], ['headerTimeoutSeconds', '3601', 20],
      ['streamIdleTimeoutSeconds', '', 40], ['streamIdleTimeoutSeconds', '0', 40], ['streamIdleTimeoutSeconds', '1.5', 40]]) {
      await fill(name, value);
      assert.equal(await field('timeoutApply').isDisabled(), true, name + '=' + value);
      assert.equal(await field('timeoutRecommendation').innerText(), '—');
      await fill(name, restore); await expected(60);
    }
    await fill('maxRetries', 2); await fill('headerTimeoutSeconds', 10); await expected(150);
    await field('rateLimitRetryEnabled').check();
    await fill('responseMaxRetries', 3); await fill('responseRetryIntervalSeconds', 5); await expected(315);
    await field('emptyResponseRetryEnabled').check(); await expected(315);
    assert.match(await field('timeoutSendDetail').textContent(), /最多 6 次.*共享重试 3 次/);
    assert.match(await field('timeoutWaitDetail').textContent(), /响应头等待 60 秒.*空闲等待 240 秒.*重试间隔 15 秒/);
    await field('rateLimitRetryEnabled').uncheck(); await expected(315);
    await fill('responseMaxRetries', 0); await fill('responseRetryIntervalSeconds', ''); await expected(150);
    await fill('responseMaxRetries', 2);
    assert.equal(await field('timeoutApply').isDisabled(), true);
    await fill('responseRetryIntervalSeconds', 3600); await expected(780);
    assert.match(await field('timeoutBudgetWarning').innerText(), /预估 7250 秒/);
    await fill('responseRetryIntervalSeconds', 0);
    assert.equal(await field('timeoutApply').isDisabled(), true);
    await fill('responseRetryIntervalSeconds', 5);
    await field('emptyResponseRetryEnabled').uncheck(); await expected(150);
    await fill('retryStatusCodes', ' 519 - 521 '); await expected(151);
    assert.match(await field('timeoutWaitDetail').textContent(), /520 退避预留 0.75 秒/);
    await fill('maxRetries', 10); await fill('headerTimeoutSeconds', 1); await expected(467);
    for (const codes of ['503-500', '429,', '600', 'invalid']) {
      await fill('retryStatusCodes', codes); assert.equal(await field('timeoutApply').isDisabled(), true);
    }
    await fill('retryStatusCodes', ''); await expected(451);
    await fill('totalTimeoutSeconds', '');
    assert.equal(await field('timeoutApply').isDisabled(), false, 'A blank total may be filled by the calculator');
    await field('timeoutApply').click(); assert.equal(await field('totalTimeoutSeconds').inputValue(), '451');
    await fill('headerTimeoutSeconds', 3600); await field('rateLimitRetryEnabled').check();
    await fill('responseMaxRetries', 10); await fill('responseRetryIntervalSeconds', 3600); await expected(780);
    assert.match(await field('timeoutBudgetWarning').innerText(), /111600 秒/);

    // Cancel the draft; host attempts and limits are independent from channel fields.
    await limits(180, 180, 600); await expected(660);
    await field('cancel').click();
    await page.evaluate(() => { hostState.defaultHostAttempts = 35; });
    await openEditor(); await expected(660);
    assert.match(await field('hostAttemptsHint').textContent(), /尝试 35 次/);
    assert.equal(await field('totalTimeoutSeconds').inputValue(), '345');
    await limits(120, 60, 300); await expected(360);
    await limits(30, 180, 120); await expected(150);
    await limits(29.5, 180, 119.25); await expected(148);
    await limits(180, 180, 3600); await expected(660);
    await fill('headerTimeoutSeconds', 3600); await fill('streamIdleTimeoutSeconds', 3600); await expected(3600);
    assert.match(await field('timeoutBudgetWarning').innerText(), /79200 秒.*上限 3600 秒/);
    await limits(180, 0, 600);
    await field('timeoutCalculation').filter({ hasText: '读取失败' }).waitFor();
    assert.equal(await field('timeoutApply').isDisabled(), true);
    assert.equal(await field('timeoutRecommendation').innerText(), '—', 'Invalid/stale host limits must not yield a recommendation');
    await page.evaluate(() => { hostState.execution.totalSetupTimeoutSeconds = 180; hostState.failExecution = true; });
    await field('timeoutReload').click();
    await field('timeoutCalculation').filter({ hasText: '读取失败' }).waitFor();
    await fill('totalTimeoutSeconds', 420); await save(); await openEditor();
    await field('timeoutCalculation').filter({ hasText: '读取失败' }).waitFor();
    assert.equal(await field('totalTimeoutSeconds').inputValue(), '420', 'Read failures must not block manual configuration');
    await page.evaluate(() => { hostState.failExecution = false; hostState.holdExecution = true; });
    await field('timeoutReload').click();
    await page.waitForFunction(() => hostState.pendingExecution.length === 1);
    assert.equal(await field('timeoutApply').isDisabled(), true);
    assert.equal(await field('timeoutRecommendation').innerText(), '—', 'Pending refresh must not advertise stale limits');
    await page.evaluate(() => { hostState.holdExecution = false; hostState.pendingExecution.shift()(); });
    await expected(660);
    assert.equal(await field('totalTimeoutSeconds').inputValue(), '420');

    // Narrow screens, expanded help, and both color modes keep all text and buttons reachable.
    for (const width of [390, 320]) {
      await page.setViewportSize({ width, height: 844 });
      for (const color of ['light', 'dark']) {
        await mode(color);
        await field('timeoutDetails').evaluate(node => { node.open = true; });
        assert.equal(await frame.locator('.editor-content').evaluate(node => node.scrollWidth <= node.clientWidth), true);
        await assertContrast(frame, '#panel-policy');
        await field('timeoutDetails').evaluate(node => { node.open = false; });
        await frame.locator('.timeout-advisor').scrollIntoViewIfNeeded();
        await page.screenshot({ path: path.join(output, `timeout-${color}-${width}.png`) });
        await field('timeoutAdvisorTitle').click();
        assert.equal(await field('timeoutApply').isVisible(), false);
        await field('maxRetries').scrollIntoViewIfNeeded();
        await assertContrast(frame, '#panel-policy');
        await page.screenshot({ path: path.join(output, `timeout-collapsed-${color}-${width}.png`) });
        await field('timeoutAdvisorTitle').click();
      }
    }
    assert.deepEqual(errors, []);
    console.log('Timeout advice passed: screenshot scenario, shared retries, backoff, host caps, validation, persistence, read failures, contrast and mobile.');
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
