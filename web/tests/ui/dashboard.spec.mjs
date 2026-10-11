import { test, expect } from '@playwright/test';
const json = data => ({ status: 200, contentType: 'application/json', body: JSON.stringify(data) });
const history = {
  start: '2026-10-01T00:00:00Z', end: '2026-11-01T00:00:00Z', upload: '1073741824', download: '15032385536', unallocatedUpload: '0', unallocatedDownload: '0', gapCount: 0,
  series: Array.from({ length: 18 }, (_, i) => ({ label: `2026-10-${String(i + 1).padStart(2, '0')}`, upload: String((i + 1) * 11000000), download: String((i * 37 % 8 + 1) * 180000000) })),
  nodes: [{ key: 'a', name: 'Synthetic · Singapore', upload: '536870912', download: '8589934592' }, { key: 'b', name: 'Synthetic · Tokyo', upload: '268435456', download: '4294967296' }, { key: 'c', name: 'Synthetic · Frankfurt', upload: '268435456', download: '2147483648' }],
  records: [{ start: '2026-10-09T11:00:00Z', end: '2026-10-09T11:01:00Z', nodeKey: 'a', nodeName: 'Synthetic · Singapore', upload: '1073741824', download: '15032385536', quality: 'measured' }], recordCount: 51
};
async function mock(page, overrides = {}) {
  const calls = [];
  await page.route('http://127.0.0.1:4173/', async route => { const response = await route.fetch(); await route.fulfill({ response, headers: { ...response.headers(), 'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'" } }); });
  await page.route('**/api/**', async route => {
    const request = route.request(), url = new URL(request.url()); calls.push({ path: url.pathname, url, body: request.postDataJSON(), headers: request.headers() });
    if (overrides[url.pathname]) { await overrides[url.pathname](route, request, url); return; }
    if (url.pathname === '/api/provider/sources') { await route.fulfill(json([])); return; }
    const routes = {
      '/api/session': { ok: true }, '/api/settings': { v2rayNPath: 'C:\\Apps\\v2rayN', timeZoneId: 'UTC', demo: true },
      '/api/timezones': [{ id: 'UTC', name: 'UTC' }, { id: 'Asia/Shanghai', name: '中国标准时间' }, { id: 'America/New_York', name: '纽约' }],
      '/api/status': { state: 'demo', message: '合成示例 · 用于界面与汇总验证', currentNode: 'Synthetic · Singapore', lastSample: '2026-10-09T11:01:00Z', uploadPerSecond: '524288', downloadPerSecond: '2097152', demo: true },
      '/api/nodes': history.nodes.map(n => ({ key: n.key, name: n.name })), '/api/history': history,
      '/api/imports': [], '/api/imports/preview': { hash: 'synthetic', rowCount: 5, rawBytes: '3154944', billedBytes: '1058816', firstTime: '2026-01-01T10:00:00Z', lastTime: '2026-01-01T10:03:00Z', duplicate: false, warnings: ['设备、方向未知。'] }, '/api/imports/commit': { ok: true }, '/api/demo': { ok: true }
    };
    await route.fulfill(json(routes[url.pathname] ?? {}));
  });
  await page.clock.setFixedTime(new Date('2026-10-09T19:00:00Z'));
  await page.goto('/#one-time-synthetic-token');
  await expect(page.locator('#total-value')).toHaveText('16.106 GB');
  return calls;
}
test('CSV source uses node statistics and exact billing without local direction labels', async ({ page }) => {
  const provider = { source: 'Synthetic provider', rawBytes: '1300', billedBytes: '1000.800000000123', recordCount: 1,
    series: [{ label: '2026-10-09', rawBytes: '1300', billedBytes: '1000.800000000123' }],
    nodes: [{ name: 'Synthetic CSV node', rawBytes: '1300', billedBytes: '1000.800000000123' }],
    records: [{ at: '2026-10-09T10:00:00Z', nodeName: 'Synthetic CSV node', rate: '0.8', rawBytes: '1300', billedBytes: '1000.800000000123' }] };
  await mock(page, {
    '/api/provider/sources': route => route.fulfill(json(['Synthetic provider'])),
    '/api/provider/nodes': route => route.fulfill(json([{ key: 'Synthetic CSV node', name: 'Synthetic CSV node' }])),
    '/api/provider/history': route => route.fulfill(json(provider))
  });
  await page.selectOption('#statistics-source', 'Synthetic provider');
  await expect(page.locator('#records')).toContainText('Synthetic CSV node');
  await expect(page.locator('#download-title')).toHaveText('服务商原始量');
  await expect(page.locator('#upload-exact')).toHaveText('1,000.800000000123 bytes');
  await page.selectOption('#provider-metric', 'billed');
  await expect(page.locator('#total-exact')).toHaveText('1,000.800000000123 bytes');
  await page.screenshot({ path: 'test-results/provider-statistics-desktop.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await expect.poll(() => page.locator('.chart-label').count()).toBeLessThanOrEqual(6);
  await expect(page.locator('#total-value')).toHaveText('< 0.001 GB');
  await page.screenshot({ path: 'test-results/provider-statistics-mobile.png', fullPage: true });
  await page.selectOption('#statistics-source', '');
  await expect(page.locator('#total-value')).toHaveText('16.106 GB');
});
test('one-time session, secure text rendering, exact unit toggle, and desktop layout', async ({ page }) => {
  const malicious = '<img src=x onerror=alert(1)>';
  const calls = await mock(page, { '/api/nodes': route => route.fulfill(json([{ key: 'x', name: malicious }])), '/api/history': route => route.fulfill(json({ ...history, nodes: [{ key: 'x', name: malicious, upload: '1073741824', download: '15032385536' }], records: [{ ...history.records[0], nodeName: malicious }] })) });
  expect(new URL(page.url()).hash).toBe('');
  expect(calls.filter(c => c.path === '/api/session')).toHaveLength(1);
  expect(calls.find(c => c.path === '/api/session').headers['x-monitor-request']).toBe('1');
  await expect(page.locator('#ranking')).toContainText(malicious);
  await expect(page.locator('img')).toHaveCount(0);
  await page.selectOption('#unit', 'GiB'); await expect(page.locator('#total-value')).toHaveText('15.000 GiB');
  await expect(page.locator('#total-exact')).toHaveText('16,106,127,360 bytes');
  await expect(page.locator('.chart-missing')).toHaveCount(13);
  expect(await page.locator('.bar-download').first().evaluate(node => parseFloat(getComputedStyle(node).height))).toBeGreaterThan(0);
  expect(await page.locator('.rank-bar').first().evaluate(node => parseFloat(getComputedStyle(node).width))).toBeGreaterThan(0);
  await page.screenshot({ path: 'test-results/dashboard-desktop.png', fullPage: true });
});
test('custom dates, independent granularity, node filter and pagination', async ({ page }) => {
  const calls = await mock(page);
  await page.click('[data-period=custom]'); await page.fill('#custom-from', '2026-10-02'); await page.fill('#custom-through', '2026-10-04');
  await page.locator('#custom-range').getByRole('button').click(); await expect(page.locator('#range-label')).toHaveText('2026-10-02 – 2026-10-04');
  await page.selectOption('#bucket', 'hour'); await page.selectOption('#node-filter', 'b'); await page.click('#next-page');
  await expect.poll(() => calls.filter(c => c.path === '/api/history').at(-1).url.searchParams.get('page')).toBe('1');
  const query = calls.filter(c => c.path === '/api/history').at(-1).url.searchParams;
  expect(query.get('start')).toBe('2026-10-02T00:00:00.000Z'); expect(query.get('end')).toBe('2026-10-05T00:00:00.000Z'); expect(query.get('bucket')).toBe('hour'); expect(query.get('nodeKey')).toBe('b');
});
test('older delayed refresh cannot replace a newer range result', async ({ page }) => {
  let delayed = false;
  await mock(page, { '/api/history': async (route, request, url) => {
    if (url.searchParams.get('nodeKey') === 'a') { delayed = true; await new Promise(r => setTimeout(r, 300)); await route.fulfill(json({ ...history, upload: '1', download: '1' })).catch(() => {}); }
    else await route.fulfill(json(history));
  } });
  await page.selectOption('#node-filter', 'a'); await expect.poll(() => delayed).toBe(true); await page.selectOption('#node-filter', 'b');
  await page.waitForTimeout(450); await expect(page.locator('#total-value')).toHaveText('16.106 GB');
});
test('expired session clearly asks to reopen tray and disables writes', async ({ page }) => {
  let expired = false;
  await mock(page, { '/api/history': route => route.fulfill(expired ? { status: 401, body: '{}' } : json(history)) });
  expired = true; await page.selectOption('#bucket', 'week');
  await expect(page.locator('#session-alert')).toBeVisible(); await expect(page.locator('#export-button')).toBeDisabled();
  await page.click('.nav-item[data-view=settings]'); await expect(page.locator('#save-settings')).toBeDisabled();
});
test('settings save posts path and IANA timezone then queries that timezone', async ({ page }) => {
  const calls = await mock(page); await page.click('.nav-item[data-view=settings]');
  await page.fill('#v2rayn-path', 'D:\\Proxy\\v2rayN'); await page.selectOption('#time-zone', 'Asia/Shanghai'); await page.click('#save-settings');
  await expect(page.locator('#settings-result')).toContainText('设置已保存');
  const post = calls.filter(c => c.path === '/api/settings' && c.body).at(-1);
  expect(post.body.v2rayNPath).toBe('D:\\Proxy\\v2rayN'); expect(post.body.timeZoneId).toBe('Asia/Shanghai');
  await expect.poll(() => calls.filter(c => c.path === '/api/history').at(-1).url.searchParams.get('timeZoneId')).toBe('Asia/Shanghai');
});
test('provider preview is separate, edits invalidate preview, and explicit confirmation commits once', async ({ page }) => {
  const calls = await mock(page); await page.click('.nav-item[data-view=provider]'); await page.fill('#provider-source', 'Synthetic Provider');
  await page.setInputFiles('#csv-file', { name: 'synthetic.csv', mimeType: 'text/csv', buffer: Buffer.from('node_name,rate,origin_traffic,origin_x_rate,traffic,log_time\nSynthetic,1,1024,1024,1 KB,2026-01-01 10:00:00') });
  await page.click('#preview-import'); await expect(page.locator('#import-preview')).toBeVisible();
  expect(calls.filter(c => c.path === '/api/imports/commit')).toHaveLength(0);
  await page.fill('#provider-source', 'Changed Source'); await expect(page.locator('#import-preview')).toBeHidden();
  await page.click('#preview-import'); await expect(page.locator('#import-preview')).toBeVisible(); await page.click('#commit-import');
  await expect(page.locator('#import-result')).toContainText('导入完成'); expect(calls.filter(c => c.path === '/api/imports/commit')).toHaveLength(1);
  await page.click('.nav-item[data-view=overview]'); await expect(page.locator('#total-value')).toHaveText('16.106 GB');
});
test('provider decimal preview, commit and reloaded list display every digit on desktop and mobile', async ({ page }) => {
  let committed = false;
  const batch = { source: 'Synthetic Decimal', hash: 'synthetic-decimal', rowCount: 3, rawBytes: '3001', billedBytes: '2600.800000000123', importedAt: '2026-01-02T00:00:00Z', firstTime: '2026-01-01T00:00:00Z', lastTime: '2026-01-01T00:02:00Z' };
  const calls = await mock(page, {
    '/api/imports/preview': route => route.fulfill(json({ ...batch, warnings: [], duplicate: committed })),
    '/api/imports/commit': route => { committed = true; return route.fulfill(json(batch)); },
    '/api/imports': route => route.fulfill(json(committed ? [batch] : []))
  });
  await page.click('.nav-item[data-view=provider]'); await page.fill('#provider-source', batch.source);
  await page.setInputFiles('#csv-file', { name: 'synthetic-decimal.csv', mimeType: 'text/csv', buffer: Buffer.from('node_name,rate,origin_traffic,origin_x_rate,traffic,log_time\nSynthetic,0.8,1001,800.800000000123,synthetic,2026-01-01 00:02:00') });
  await page.click('#preview-import');
  await expect(page.locator('#preview-details')).toContainText('2,600.800000000123 bytes');
  await expect(page.locator('#preview-details')).toContainText('3,001 bytes');
  await page.click('#commit-import');
  await expect(page.locator('#import-batches')).toContainText('2,600.800000000123 bytes');
  await page.click('.nav-item[data-view=overview]'); await page.selectOption('#unit', 'GiB');
  await expect(page.locator('#total-exact')).toHaveText('16,106,127,360 bytes');
  await page.click('.nav-item[data-view=provider]');
  await expect(page.locator('#import-batches')).toContainText('< 0.001 GiB');
  await expect(page.locator('#import-batches')).toContainText('2,600.800000000123 bytes');
  await page.screenshot({ path: 'test-results/provider-decimal-desktop.png', fullPage: true });
  await page.reload(); await page.click('.nav-item[data-view=provider]');
  await expect(page.locator('#import-batches')).toContainText('2,600.800000000123 bytes');
  expect(calls.filter(c => c.path === '/api/imports/commit')).toHaveLength(1);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await expect(page.locator('#import-batches small')).toHaveText('2,600.800000000123 bytes');
  await page.screenshot({ path: 'test-results/provider-decimal-mobile.png', fullPage: true });
});
test('mobile page has no horizontal document overflow', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 }); await mock(page);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'test-results/dashboard-mobile.png', fullPage: true });
  await page.click('.nav-item[data-view=provider]'); expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
