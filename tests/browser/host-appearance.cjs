const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const http = require('node:http');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const { bridge, hostMock, assertContrast } = require('./settings-fixture.cjs');
const hostRoot = process.env.ROUTER_HOST_ROOT;
if (!hostRoot) throw Error('Set ROUTER_HOST_ROOT to the official Router2API 2.1.2 source directory.');
const ts = require(path.join(hostRoot, 'web/node_modules/typescript'));
const hostModule = { exports: {} };
vm.runInNewContext(ts.transpileModule(fs.readFileSync(path.join(hostRoot, 'web/src/services/pluginAppearance.ts'), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 }
}).outputText, hostModule);
const source = require('./page-source.cjs')().replace('<head>', '<head>' + bridge + hostModule.exports.pluginAppearanceScript);
const output = path.resolve(__dirname, '../../artifacts/v0.3.5-ui');
function appearanceHost() {
  hostState.mode = 'host';
  window.appearance = { theme: 'minimal', colorMode: 'light', colors: {
    background: '#101114', surface: '#18191d', text: '#d0d5dd', muted: '#98a2b3', border: '#343741'
  } };
  window.sendAppearance = (colorMode, theme = 'minimal') => {
    window.appearance.colorMode = colorMode;
    window.appearance.theme = theme;
    document.querySelector('iframe').contentWindow.postMessage({ type: 'router2api-appearance', appearance: window.appearance }, '*');
  };
  addEventListener('message', event => {
    if (event.source === document.querySelector('iframe').contentWindow && event.data?.type === 'router2api-appearance-ready')
      sendAppearance(appearance.colorMode, appearance.theme);
  });
}
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>html,body{margin:0}iframe{border:0;width:100%;height:100vh;display:block}</style></head><body><iframe sandbox="allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox"></iframe><script>(${hostMock})();(${appearanceHost})();window.openPlugin=()=>document.querySelector('iframe').srcdoc=${JSON.stringify(source).replaceAll('<', '\\u003c')};openPlugin();</script></body></html>`;
  const server = http.createServer((_, response) => { response.setHeader('Content-Type', 'text/html; charset=utf-8'); response.end(html); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1040 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    const frame = page.frameLocator('iframe');
    const settle = async () => frame.locator('html').evaluate(() => new Promise(resolve =>
      requestAnimationFrame(() => requestAnimationFrame(() => requestAnimationFrame(resolve)))));
    const select = async mode => {
      await frame.locator(`.workspace-header [data-appearance-mode=${mode}]`).click();
      await frame.locator('#appearanceStatus').waitFor({ state: 'hidden' });
      await settle();
    };
    const hostMode = async (mode, theme = 'minimal') => {
      await page.evaluate(([mode, theme]) => sendAppearance(mode, theme), [mode, theme]); await settle();
    };
    const mode = async expected => assert.equal(await frame.locator('html').getAttribute('data-mode'), expected);
    const noOverrides = async () => assert.equal(await frame.locator('body').evaluate(node =>
      [...node.querySelectorAll('[style]')].some(element => /(?:color|background|border)[^;]*!important/.test(element.getAttribute('style')))), false,
      'The host generic recoloring must not override the plugin palette');
    await frame.locator('.channel-card').first().waitFor(); await settle();
    await mode('light');
    assert.equal(await frame.locator('[data-appearance-mode=host]').first().getAttribute('aria-pressed'), 'true');
    await hostMode('dark'); await mode('dark'); await noOverrides();
    assert.equal(await frame.locator('.channel-card[data-enabled=false]').evaluate(node => getComputedStyle(node).backgroundColor), 'rgb(41, 44, 50)');
    assert.equal(await frame.locator('#add').evaluate(node => getComputedStyle(node).backgroundColor), 'rgb(244, 244, 245)');
    await assertContrast(frame, '.app-main');
    await page.screenshot({ path: path.join(output, 'host-dark-channels.png') });
    await frame.locator('.channel-edit-button').first().click();
    await frame.locator('#label').fill('宿主切换时保留的草稿');
    await frame.locator('#tab-reasoning').click();
    await hostMode('light', 'default'); await mode('light');
    assert.equal(await frame.locator('#label').inputValue(), '宿主切换时保留的草稿');
    assert.equal(await frame.locator('#tab-reasoning').getAttribute('aria-selected'), 'true');
    await hostMode('dark', 'future-theme'); await mode('dark');
    await noOverrides(); await assertContrast(frame, '#editor');
    await frame.locator('#tab-policy').click();
    await frame.locator('#timeoutAdvisorTitle').click();
    await frame.locator('#timeoutApply:enabled').waitFor();
    await frame.locator('#timeoutDetails').evaluate(node => { node.open = true; });
    await noOverrides(); await assertContrast(frame, '#panel-policy');
    await hostMode('light'); await noOverrides(); await assertContrast(frame, '#panel-policy');
    assert.equal(await frame.locator('#label').inputValue(), '宿主切换时保留的草稿');
    await hostMode('dark');
    await frame.locator('#cancel').click();
    await frame.locator('#openJournal').click();
    await frame.locator('.record-model').waitFor();
    await frame.locator('#journalExpand').click();
    await frame.locator('[name=trace]').fill('未提交筛选');
    await hostMode('light'); await hostMode('dark');
    assert.equal(await frame.locator('[name=trace]').inputValue(), '未提交筛选');
    assert.equal(await frame.locator('#journal').isVisible(), true);
    assert.equal(await frame.locator('.record-model').evaluate(node => getComputedStyle(node).fontSize), '15px');
    await assertContrast(frame, '#journal');
    await page.screenshot({ path: path.join(output, 'host-dark-journal.png') });
    await select('light'); await hostMode('dark'); await mode('light'); await noOverrides();
    await assertContrast(frame, '#journal');
    await select('dark'); await hostMode('light'); await mode('dark');
    await select('host'); await mode('light');
    await frame.locator('html').evaluate(() => {
      dispatchEvent(new MessageEvent('message', { source: window, data: { type: 'router2api-appearance', appearance: { colorMode: 'dark' } } }));
    });
    await settle(); await mode('light');
    await page.evaluate(() => document.querySelector('iframe').contentWindow.postMessage({ type: 'router2api-appearance', appearance: { colorMode: 'invalid' } }, '*'));
    await settle(); await mode('light');
    assert.equal(await page.evaluate(() => hostState.mode), 'host');
    await hostMode('dark');
    await page.evaluate(() => openPlugin());
    await frame.locator('.channel-card').first().waitFor(); await settle(); await mode('dark');
    for (const width of [390, 320]) {
      await page.setViewportSize({ width, height: 844 });
      await frame.locator('#appSidebar').evaluate(async node => { await Promise.all(node.getAnimations().map(animation => animation.finished)); });
      assert.equal(await frame.locator('body').evaluate(node => node.scrollWidth <= innerWidth), true);
      await assertContrast(frame, '.app-main'); await noOverrides();
      await page.screenshot({ path: path.join(output, `host-dark-mobile-${width}.png`) });
    }
    await page.evaluate(() => { hostState.failAppearanceRead = true; openPlugin(); });
    await frame.locator('#appearanceStatus').filter({ hasText: '读取失败' }).waitFor();
    await mode('light'); await hostMode('dark'); await mode('light');
    await page.evaluate(() => { hostState.failAppearanceWrite = true; });
    await frame.locator('.workspace-header [data-appearance-mode=dark]').click();
    await frame.locator('#appearanceStatus').filter({ hasText: '已恢复此前模式' }).waitFor();
    await mode('light');
    assert.deepEqual(errors, []);
    console.log('Official 2.1.2 sandbox script passed: host mode, custom palette, manual override, drafts, filters, persistence, invalid messages and mobile.');
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
