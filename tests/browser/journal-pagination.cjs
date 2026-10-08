const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const source = require('./page-source.cjs')();
const mock = `<script>
window.calls = [];
window.setLogs = count => window.logRows = Array.from({ length: count }, (_, index) => ({
  id: 'log-' + (index + 1), trace: 'trace-' + (index + 1), started: 1791417600000 - index * 1000,
  channel: 'channel', channelLabel: '分页测试渠道', model: 'model', endpoint: '/v1/responses',
  kind: 'forward', network: 'direct', state: 'completed', status: 200
}));
window.setLogs(11);
window.Router2API = { request: async (method, route, body) => {
  window.calls.push({ method, route, body });
  if (route === 'accounts') return { accounts: [] };
  if (route === 'logs/status') return { available: true, records: window.logRows.length };
  if (route.startsWith('logs?')) {
    if (window.holdList) await new Promise(resolve => window.releaseList = () => { window.holdList = false; resolve(); });
    if (window.failList) { window.failList = false; throw Error('模拟读取失败'); }
    const query = new URLSearchParams(route.split('?')[1]);
    const rows = window.logRows.filter(row => !query.get('trace') || row.trace === query.get('trace'));
    const pageSize = Number(query.get('pageSize') || 25);
    const page = Math.max(1, Math.min(Number(query.get('page') || 1), Math.ceil(rows.length / pageSize)));
    return { rows: rows.slice((page - 1) * pageSize, page * pageSize), page, pageSize, total: rows.length };
  }
  if (route.startsWith('logs/detail?')) return { ...window.logRows.find(row => row.id === new URLSearchParams(route.split('?')[1]).get('id')), attempts: [], parts: [] };
  if (route === 'logs/delete') { window.logRows = window.logRows.filter(row => row.id !== body.id); return { success: true }; }
  throw Error('Unexpected route: ' + route);
}};<\/script>`;
const html = `<html><head><style>html,body{margin:0}iframe{display:block;border:0;width:100%;height:100vh}</style></head><body><iframe sandbox="allow-scripts allow-forms"></iframe><script>document.querySelector('iframe').srcdoc=${JSON.stringify(source.replace('<head>', '<head>' + mock)).replaceAll('<', '\\u003c')}<\/script></body></html>`;

(async () => {
  const server = http.createServer((_, res) => { res.setHeader('Content-Type', 'text/html; charset=utf-8'); res.end(html); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    const frame = page.frameLocator('iframe');
    const scriptFrame = page.frames().find(item => item.parentFrame());
    const rows = frame.locator('#journalRows tr');
    const previous = frame.locator('#journalPrevious'), next = frame.locator('#journalNext');
    const pageSize = frame.locator('#journalPageSize');
    const expectPage = async (current, pages, total, count) => {
      await frame.locator('#journalPage').filter({ hasText: `第 ${current} / ${pages} 页 · 共 ${total} 条` }).waitFor();
      assert.equal(await rows.count(), count);
      assert.equal(await previous.isDisabled(), current === 1);
      assert.equal(await next.isDisabled(), current === pages);
    };
    await frame.locator('#openJournal').click();
    await rows.first().waitFor();
    assert.equal(await next.isEnabled(), true, '11 records must allow a second page at the default page size.');
    assert.equal(await pageSize.inputValue(), '10');
    await expectPage(1, 2, 11, 10);
    assert.deepEqual(await rows.locator('.record-trace').allTextContents(), Array.from({ length: 10 }, (_, i) => 'trace-' + (i + 1)));
    fs.mkdirSync(path.resolve(__dirname, '../../artifacts'), { recursive: true });
    await next.scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.resolve(__dirname, '../../artifacts/journal-pagination-desktop.png'), fullPage: true });
    await next.click();
    await expectPage(2, 2, 11, 1);
    assert.deepEqual(await rows.locator('.record-trace').allTextContents(), ['trace-11']);
    await previous.click();
    await expectPage(1, 2, 11, 10);
    await pageSize.selectOption('25');
    await expectPage(1, 1, 11, 11);
    await pageSize.selectOption('10');
    await next.click();
    await expectPage(2, 2, 11, 1);
    await rows.getByRole('button', { name: '详情' }).click();
    await frame.locator('#journalDelete').click();
    await frame.locator('#confirmYes').click();
    await expectPage(1, 1, 10, 10);

    await scriptFrame.evaluate(() => window.setLogs(51));
    await frame.locator('#journalFilters button[type=submit]').click();
    await expectPage(1, 6, 51, 10);
    await pageSize.selectOption('25');
    await expectPage(1, 3, 51, 25);
    await next.click();
    await expectPage(2, 3, 51, 25);
    assert.equal(await rows.locator('.record-trace').first().textContent(), 'trace-26');
    await next.click();
    await expectPage(3, 3, 51, 1);
    assert.equal(await rows.locator('.record-trace').first().textContent(), 'trace-51');
    await pageSize.selectOption('50');
    await expectPage(1, 2, 51, 50);
    await next.click();
    await expectPage(2, 2, 51, 1);
    await pageSize.selectOption('100');
    await expectPage(1, 1, 51, 51);
    await pageSize.selectOption('25');
    await expectPage(1, 3, 51, 25);

    const callsBefore = await scriptFrame.evaluate(() => window.calls.filter(call => call.route.startsWith('logs?')).length);
    await scriptFrame.evaluate(() => { window.holdList = true; });
    await next.click();
    await scriptFrame.waitForFunction(() => typeof window.releaseList === 'function');
    assert.equal(await previous.isDisabled(), true);
    assert.equal(await next.isDisabled(), true);
    assert.equal(await pageSize.isDisabled(), true);
    await next.evaluate(button => button.click());
    assert.equal(await scriptFrame.evaluate(() => window.calls.filter(call => call.route.startsWith('logs?')).length), callsBefore + 1);
    await scriptFrame.evaluate(() => window.releaseList());
    await expectPage(2, 3, 51, 25);
    await scriptFrame.evaluate(() => { window.failList = true; });
    await next.click();
    await frame.locator('#journalStatus').filter({ hasText: '模拟读取失败' }).waitFor();
    await expectPage(2, 3, 51, 25);
    await next.click();
    await expectPage(3, 3, 51, 1);

    await frame.locator('#journalExpand').click();
    await frame.locator('#journalFilters [name=trace]').fill('trace-3');
    await frame.locator('#journalFilters button[type=submit]').click();
    await expectPage(1, 1, 1, 1);
    assert.deepEqual(await rows.locator('.record-trace').allTextContents(), ['trace-3']);
    await frame.locator('#journalFilters [name=trace]').fill('no-matching-trace');
    await frame.locator('#journalFilters button[type=submit]').click();
    await expectPage(1, 1, 0, 0);
    await frame.locator('#journalFilters [name=trace]').fill('');
    await scriptFrame.evaluate(() => window.setLogs(11));
    await pageSize.selectOption('10');
    await expectPage(1, 2, 11, 10);
    await page.setViewportSize({ width: 390, height: 844 });
    await next.scrollIntoViewIfNeeded();
    assert.equal(await frame.locator('.journal-pagination').evaluate(node => node.scrollWidth <= node.clientWidth + 1), true);
    assert.equal(await frame.locator('#journal').evaluate(node => node.scrollWidth <= node.clientWidth + 1), true);
    await page.screenshot({ path: path.resolve(__dirname, '../../artifacts/journal-pagination-mobile.png'), fullPage: true });
    await next.click();
    await expectPage(2, 2, 11, 1);
    assert.deepEqual(errors, []);
    console.log('Journal pagination passed: 11 records, all page sizes, previous/next, deletion, loading guard, retry after failure, filters, empty results, mobile.');
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
