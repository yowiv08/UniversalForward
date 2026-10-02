const http = require('node:http');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const source = require('./page-source.cjs')();
const account = {
  id: 'channel', label: 'Clipboard', baseUrl: 'https://upstream.example', enabled: true, weight: 100,
  models: ['model'], endpoints: ['/v1/responses'], keys: [{ id: 'key', enabled: true, masked: 'test' }],
  headerOverride: { 'X-Custom': '中文 😀' }
};
const response = 'HTTP: 200\nContent-Type: text/html\n<html>blocked</html>\n' + '测试 😀\n'.repeat(6000);
const mock = `<script>window.Router2API={request:async(method,route)=>{if(route==='accounts')return{accounts:[${JSON.stringify(account)}]};throw Error(${JSON.stringify(response)})}}<\/script>`;
const pageHtml = `<!doctype html><iframe title="Plugin" style="width:100%;height:95vh" sandbox="allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox"></iframe><script>document.querySelector('iframe').srcdoc=${JSON.stringify(source.replace('<head>', '<head>' + mock)).replaceAll('<', '\\u003c')}<\/script>`;

(async () => {
  const server = http.createServer((_, res) => { res.setHeader('Content-Type', 'text/html; charset=utf-8'); res.end(pageHtml); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ headless: true });
  try {
    const origin = `http://127.0.0.1:${server.address().port}`;
    const context = await browser.newContext();
    await context.grantPermissions(['clipboard-read'], { origin });
    const page = await context.newPage();
    await page.goto(origin);
    const frame = page.frameLocator('iframe');
    await frame.locator('#cards').getByRole('button', { name: '编辑', exact: true }).click();
    await frame.locator('#tab-headers').click();
    await frame.locator('#headerJson').click();
    const headers = JSON.stringify({ 'X-Custom': '中文 😀' });
    await frame.locator('#headerOverride').fill(headers);
    await frame.locator('#headerCopy').click();
    await frame.locator('#headerStatus').filter({ hasText: '已复制请求头' }).waitFor();
    assert.deepEqual(JSON.parse(await page.evaluate(() => navigator.clipboard.readText())), JSON.parse(headers));
    await frame.locator('#tab-models').click();
    await frame.locator('#discover').click();
    await frame.locator('#discoveryResponse').filter({ hasText: 'HTTP: 200' }).waitFor();
    for (const viewport of [{ width: 1280, height: 900 }, { width: 390, height: 844 }]) {
      await page.setViewportSize(viewport);
      await frame.locator('#copyDiscoveryResponse').click();
      await frame.locator('#discoveryStatus').filter({ hasText: '已复制响应详情' }).waitFor();
      const copied = await page.evaluate(() => navigator.clipboard.readText());
      assert.equal(copied.replaceAll('\r\n', '\n'), response);
      assert.equal(await frame.locator('#copyDialog').count(), 0);
      assert.equal(await frame.locator('textarea[style*="position:"]').count(), 0);
    }
    await context.close();
    console.log('Clipboard passed: real clipboard readback from sandboxed iframe, headers, long Unicode response, desktop/mobile.');
  } finally {
    await browser.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
