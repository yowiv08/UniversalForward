const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const pageSource = require('./page-source.cjs');
const output = path.resolve(__dirname, '../../artifacts');

function workspaceMock() {
  window.calls = [];
  window.holdLogs = false;
  window.failAccounts = false;
  window.accounts = [{ id: 'primary', label: '主力渠道', baseUrl: 'https://upstream.example/v1', enabled: true, weight: 100,
    models: ['model-a'], endpoints: ['/v1/responses'], keys: [{ id: 'a', name: '主 Key', masked: '••••1234', enabled: true }] },
  { id: 'disabled', label: '备用渠道', baseUrl: 'https://backup.example/v1', enabled: false, models: ['model-b'], keys: [] }];
  window.Router2API = { request: async (method, route, body) => {
    window.calls.push({ method, route, body });
    if (route === 'accounts') {
      if (window.failAccounts) throw Error('读取失败，请重试');
      return { accounts: structuredClone(window.accounts) };
    }
    if (route === 'network/proxies') return { subscriptions: [], nodes: [] };
    if (route === 'logs/status') return { available: true, records: 0 };
    if (route.startsWith('logs?')) {
      if (window.holdLogs) await new Promise(resolve => { window.releaseLogs = resolve; });
      return { rows: [], page: 1, pageSize: 10, total: 0 };
    }
    if (route === 'accounts/enabled') {
      window.accounts.find(account => account.id === body.id).enabled = body.enabled;
      return { account: structuredClone(window.accounts.find(account => account.id === body.id)) };
    }
    if (route === 'accounts/delete') { window.accounts = window.accounts.filter(account => account.id !== body.id); return {}; }
    throw Error('Unexpected route: ' + route);
  } };
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const source = pageSource().replace('<head>', '<head><script>(' + workspaceMock + ')();</script>');
  const html = '<html><head><style>html,body{margin:0}iframe{display:block;border:0;width:100%;height:100vh}</style></head><body><iframe title="UniversalForward" sandbox="allow-scripts allow-forms"></iframe><script>document.querySelector("iframe").srcdoc=' + JSON.stringify(source).replaceAll('<', '\\u003c') + '</script></body></html>';
  const server = http.createServer((_, response) => { response.setHeader('Content-Type', 'text/html; charset=utf-8'); response.end(html); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    const frame = page.frameLocator('iframe');
    const scriptFrame = page.frames().find(item => item.parentFrame());
    await frame.locator('.channel-card').first().waitFor();
    assert.equal(await frame.locator('#showChannels').getAttribute('aria-current'), 'page');
    assert.equal(await frame.locator('#journal').isVisible(), false);
    await frame.locator('#showEnabled').click(); assert.equal(await frame.locator('.channel-card').count(), 1);
    assert.equal(await frame.locator('#statusFilter').inputValue(), 'enabled');
    await frame.locator('#showDisabled').click(); assert.equal(await frame.locator('.badge').innerText(), '已禁用');
    await frame.locator('#showChannels').click(); assert.equal(await frame.locator('.channel-card').count(), 2);
    const trigger = frame.locator('.channel-menu-trigger').first();
    await trigger.click(); assert.equal(await trigger.getAttribute('aria-expanded'), 'true');
    assert.equal(await frame.getByRole('menuitem', { name: '日志', exact: true }).evaluate(element => element === document.activeElement), true);
    await page.keyboard.press('ArrowDown');
    assert.equal(await scriptFrame.evaluate(() => document.activeElement.textContent), '删除');
    await page.keyboard.press('End'); assert.equal(await scriptFrame.evaluate(() => document.activeElement.textContent), '删除');
    await page.keyboard.press('Escape');
    assert.equal(await trigger.getAttribute('aria-expanded'), 'false');
    assert.equal(await trigger.evaluate(element => element === document.activeElement), true);
    await trigger.click();
    const menuBounds = await frame.getByRole('menu').boundingBox(); assert.ok(menuBounds.x >= 8 && menuBounds.y >= 8 && menuBounds.x + menuBounds.width <= 1440);
    await frame.locator('#channelsHeading').click(); assert.equal(await trigger.getAttribute('aria-expanded'), 'false');
    await trigger.click(); await frame.getByRole('menuitem', { name: '日志', exact: true }).click();
    await frame.locator('#journalEmpty').waitFor();
    assert.equal(await frame.locator('#channelsWorkspace').isVisible(), false);
    assert.equal(await frame.locator('#openJournal').getAttribute('aria-current'), 'page');
    assert.equal(await frame.locator('#journalFilters [name=channel]').inputValue(), 'primary');
    assert.equal(await frame.locator('#journalScopeLabel').innerText(), '渠道：主力渠道');
    assert.equal(await frame.locator('dialog[open]').count(), 0);
    await frame.locator('#journalScopeClear').click(); await frame.locator('#journalScope').waitFor({ state: 'hidden' });
    await frame.locator('#journalClose').click();
    assert.equal(await frame.locator('#channelsWorkspace').isVisible(), true);
    await scriptFrame.evaluate(() => { window.holdLogs = true; });
    await frame.locator('#openJournal').click();
    await scriptFrame.waitForFunction(() => typeof window.releaseLogs === 'function');
    await frame.locator('#showChannels').click();
    await scriptFrame.evaluate(() => { window.holdLogs = false; window.releaseLogs(); });
    assert.equal(await frame.locator('#journal').isVisible(), false);
    await frame.locator('#openJournal').click(); await frame.locator('#journalStatus').waitFor({ state: 'hidden' });
    await frame.locator('#journalClose').click();
    await frame.locator('#navToggle').click();
    assert.equal(await scriptFrame.evaluate(() => document.body.classList.contains('sidebar-collapsed')), true);
    assert.equal(await frame.locator('#navToggle').getAttribute('aria-expanded'), 'false');
    await page.setViewportSize({ width: 390, height: 844 });
    assert.equal(await frame.locator('#appSidebar').evaluate(element => element.inert), true);
    await frame.locator('#navToggle').click();
    assert.equal(await frame.locator('#appSidebar').evaluate(element => element.inert), false);
    assert.equal(await frame.locator('.app-main').evaluate(element => element.inert), true);
    assert.equal(await frame.locator('#sidebarOverlay').isVisible(), true);
    const mobileSidebar = await frame.locator('#appSidebar').boundingBox(); assert.equal(mobileSidebar.width, 232);
    await page.screenshot({ path: path.join(output, 'workspace-mobile-navigation.png') });
    await page.keyboard.press('Shift+Tab');
    assert.equal(await frame.locator('#showDisabled').evaluate(element => element === document.activeElement), true);
    await page.keyboard.press('Tab');
    assert.equal(await frame.locator('#showChannels').evaluate(element => element === document.activeElement), true);
    await page.keyboard.press('Escape'); assert.equal(await frame.locator('.app-main').evaluate(element => element.inert), false);
    await frame.locator('#navToggle').click(); await frame.locator('#openJournal').click();
    await frame.locator('#journalEmpty').waitFor(); assert.equal(await frame.locator('#sidebarOverlay').isVisible(), false);
    await frame.locator('#journalClose').click();
    await page.setViewportSize({ width: 1440, height: 1000 });
    await frame.locator('#navToggle').click();
    await scriptFrame.evaluate(() => { window.accounts = []; window.failAccounts = true; });
    await frame.locator('#reload').click(); await frame.locator('#message').filter({ hasText: '读取失败' }).waitFor();
    assert.equal(await frame.locator('.channel-card').count(), 2, 'Refresh failure must retain existing channels');
    await scriptFrame.evaluate(() => { window.failAccounts = false; });
    await frame.locator('#reload').click();
    await frame.locator('#cards').getByRole('heading', { name: '添加你的第一个渠道' }).waitFor();
    assert.equal(await frame.locator('#metricChannels').innerText(), '0');
    await frame.locator('#cards').getByRole('button', { name: '添加渠道' }).click(); assert.equal(await frame.locator('#editor').isVisible(), true);
    await frame.locator('#close').click();
    assert.deepEqual(errors, []);
    console.log('Workspace UI passed: page navigation, status shortcuts, contextual logs, menu keyboard/focus, stale request guard, mobile sidebar/focus trap, refresh failure and empty channels.');
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
