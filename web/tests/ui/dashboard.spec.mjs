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
      '/api/imports': [], '/api/imports/preview': { hash: 'synthetic', rowCount: 5, rawBytes: '3154944', billedBytes: '1058816',
        newRowCount: 5, skippedRowCount: 0, newRawBytes: '3154944', newBilledBytes: '1058816',
        firstTime: '2026-01-01T10:00:00Z', lastTime: '2026-01-01T10:03:00Z', duplicate: false, warnings: [] },
      '/api/imports/commit': { newRowCount: 5, skippedRowCount: 0, newRawBytes: '3154944', newBilledBytes: '1058816', duplicate: false }, '/api/demo': { ok: true }
    };
    await route.fulfill(json(routes[url.pathname] ?? {}));
  });
  await page.clock.setFixedTime(new Date('2026-10-09T19:00:00Z'));
  await page.goto('/#one-time-synthetic-token');
  await expect(page.locator('#total-value')).toHaveText('16.106 GB');
  return calls;
}
test('probe view separates dimensions and keeps a readable desktop and mobile layout', async ({ page }) => {
  const calls = await mock(page, {
    '/api/status': route => route.fulfill(json({ state: 'collecting', message: 'Synthetic collector', demo: false,
      currentNode: 'Synthetic member', lastSample: null, uploadPerSecond: null, downloadPerSecond: null,
      probes: { historyEnabled: true, state: 'partial', message: 'Synthetic missing probe counter remains unknown' } })),
    '/api/history': (route, request, url) => route.fulfill(json(url.searchParams.has('basis')
      ? { ...history, basis: url.searchParams.get('basis'), earliestRecord: '2026-10-01T00:00:00Z',
        upload: '1024', download: '3072', recordCount: 1,
        nodes: [{ key: 'scope:7', name: 'Synthetic probe member', upload: '1024', download: '3072' }],
        series: [{ label: '2026-10-01', upload: '1024', download: '3072' }],
        records: [{ ...history.records[0], nodeKey: 'scope:7', nodeName: 'Synthetic probe member',
          start: '2026-10-01T00:00:00Z', end: '2026-10-01T00:01:00Z', upload: '1024', download: '3072' }] } : history))
  });
  await page.click('.nav-item[data-view=probes]');
  await expect(page.locator('#statistics-title')).toHaveText('探测流量');
  await expect(page.locator('#total-exact')).toHaveText('4,096 bytes');
  await expect(page.locator('#probe-state')).toHaveText('探测部分采集');
  await expect(page.locator('#probe-coverage')).toContainText('不是精确的首次基线');
  await expect(page.locator('#source-controls')).toBeHidden();
  for (const [width, height, suffix] of [[1440, 1100, 'desktop'], [390, 844, 'mobile']]) {
    await page.setViewportSize({ width, height });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/probes-${suffix}.png`, fullPage: true });
  }
  await page.selectOption('#probe-basis', 'probe_urltest_excluded');
  await expect.poll(() => calls.filter(c => c.path === '/api/history').at(-1).url.searchParams.get('basis')).toBe('probe_urltest_excluded');
  await page.click('.nav-item[data-view=overview]');
  await expect(page.locator('#total-value')).toHaveText('16.106 GB');
  expect(calls.filter(c => c.path === '/api/history').at(-1).url.searchParams.has('basis')).toBe(false);
});
test('member group labels wrap fully and local details request nonzero rows', async ({ page }) => {
  const name = '【US-1x】 1-Synthetic member with a deliberately long readable name for the ranking';
  const calls = await mock(page, {
    '/api/history': route => route.fulfill(json({ ...history, basis: 'actual_member', recordCount: 1,
      nodes: [{ ...history.nodes[0], name }], records: [{ ...history.records[0], nodeName: name }] }))
  });
  expect(calls.find(x => x.path === '/api/history').url.searchParams.get('nonZeroRecords')).toBe('true');
  await expect(page.locator('#record-count')).toHaveText('1 条非零记录');
  for (const [width, height, suffix] of [[1200, 1000, 'desktop'], [390, 844, 'mobile']]) {
    await page.setViewportSize({ width, height });
    const label = page.locator('.rank-node').first();
    await expect(label).toHaveText(name);
    expect(await label.evaluate(el => el.scrollWidth <= el.clientWidth + 1 && getComputedStyle(el).whiteSpace === 'normal')).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/member-label-${suffix}.png`, fullPage: true });
  }
});
test('issue23 shares and recovery keep directions, exact values and mobile layout', async ({ page }) => {
  await mock(page, {
    '/api/history': route => route.fulfill(json({ ...history,
      nodes: history.nodes.map((n, i) => ({ ...n, recoveredUpload: i === 0 ? '123456789' : '0', recoveredDownload: '0' })) }))
  });
  await page.locator('.rank-recovery-marker:not(.vacant)').click();
  await expect(page.locator('#exact-value-dialog')).toBeVisible();
  await expect(page.locator('#exact-value-text')).toContainText('123,456,789 bytes');
  await page.locator('#exact-value-dialog form button').click();
  await page.click('[data-ranking-mode=share]');
  await expect(page.locator('.share-chart circle')).toHaveCount(3);
  await expect(page.locator('.rank-download')).toHaveCount(3);
  for (const [width, height, suffix] of [[1440, 1100, 'desktop'], [390, 844, 'mobile']]) {
    await page.setViewportSize({ width, height });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/issue23-shares-${suffix}.png`, fullPage: true });
  }
});
test('issue23 captured rates, group names and exact copy work across business and probe views', async ({ page }) => {
  const display = { originalName: 'Synthetic original 0.5x', groups: ['US-0.5x'], mergeKey: null, rate: '0.5', rateStatus: 'known',
    estimatedBilledBytes: '8053063680', estimateCoveredBytes: '16106127360', estimateUnknownBytes: '0' };
  await mock(page, {
    '/api/status': route => route.fulfill(json({ state: 'collecting', message: 'Synthetic', currentNode: 'Synthetic', lastSample: null,
      uploadPerSecond: null, downloadPerSecond: null, demo: false, probes: { historyEnabled: true, state: 'available', message: 'Synthetic' } })),
    '/api/history': (route, request, url) => route.fulfill(json({ ...history, basis: url.searchParams.get('basis') ?? 'local_proxy',
      displayMetadataEnabled: true, display, nodes: [{ ...history.nodes[0], display }], records: [{ ...history.records[0], display }] }))
  });
  await expect(page.locator('#billing-value')).toHaveText('8.053 GB');
  await expect(page.locator('#records .node-cell .node-groups')).toHaveText('【US-0.5x】');
  await expect(page.locator('#records tr').first().locator('td').nth(6)).toHaveText('0.5');
  await page.evaluate(() => Object.defineProperty(navigator, 'clipboard', { configurable: true,
    value: { writeText: async text => { window.syntheticCopied = text; } } }));
  await page.locator('#records tr').first().locator('td').nth(7).locator('button').click();
  await page.click('#copy-exact-value');
  await expect(page.locator('#copy-value-result')).toHaveText('已复制');
  expect(await page.evaluate(() => window.syntheticCopied)).toBe('8,053,063,680 bytes');
  await page.locator('#exact-value-dialog form button').click();
  await page.click('.nav-item[data-view=probes]');
  await expect(page.locator('#billing-value')).toHaveText('8.053 GB');
  await page.screenshot({ path: 'test-results/issue23-captured-desktop.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: 'test-results/issue23-captured-mobile.png', fullPage: true });
});
test('cross-core history stays visible with Xray active and setup copy is current', async ({ page }) => {
  await mock(page, {
    '/api/status': route => route.fulfill(json({ state: 'collecting', message: 'Synthetic Xray proxy',
      currentNode: 'Synthetic Xray', collectionBasis: 'legacy_proxy', demo: false,
      lastSample: null, uploadPerSecond: null, downloadPerSecond: null })),
    '/api/history': route => route.fulfill(json({ ...history, basis: 'local_proxy',
      nodes: [{ ...history.nodes[0], name: 'Synthetic sing-box history' },
        { ...history.nodes[1], name: 'Synthetic Xray history' }] }))
  });
  await expect(page.locator('#ranking')).toContainText('Synthetic sing-box history');
  await expect(page.locator('#ranking')).toContainText('Synthetic Xray history');
  await expect(page.locator('#attribution-note')).toContainText('切换核心不隐藏记录');
  for (const [width, height, suffix] of [[1440, 1100, 'desktop'], [390, 844, 'mobile']]) {
    await page.setViewportSize({ width, height });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/cross-core-${suffix}.png`, fullPage: true });
  }
  await page.click('.nav-item[data-view=settings]');
  await expect(page.locator('#settings-view')).not.toContainText('test.2');
  await expect(page.locator('#settings-view')).toContainText('策略组实际成员');
});
test('partial sing-box coverage is visible on desktop and mobile after collection resumes', async ({ page }) => {
  const description = '仅采集路由直接匹配 proxy 的字节。其他 37 个出站（含 4 个策略组）未采集，其流量未知；当前合计不是全部代理流量。';
  await mock(page, {
    '/api/status': route => route.fulfill(json({ state: 'collecting', message: '正在采集 sing-box proxy 原始字节。',
      currentNode: 'Synthetic · AnyTLS', lastSample: '2026-10-09T11:01:00Z', uploadPerSecond: '0', downloadPerSecond: '0', demo: false,
      scope: { basis: 'routed_proxy', isPartial: true, uncollectedOutbounds: 37, uncollectedGroups: 4, description } })),
    '/api/history': route => route.fulfill(json({ ...history, gapCount: 1,
      records: history.records.map(row => ({ ...row, partialCoverage: true })),
      gaps: [{ start: '2026-10-09T10:00:00Z', end: '2026-10-09T11:01:00Z', reason: 'uncollected_outbounds', isOpen: false }] }))
  });
  await expect(page.locator('#collector-state')).toHaveText('部分采集');
  await expect(page.locator('#collector-message')).toContainText(description);
  await expect(page.locator('#records')).toContainText('部分出站');
  await page.locator('#quality-summary').click();
  await expect(page.locator('#quality-notice')).toContainText('未覆盖范围的流量未知');
  await page.locator('#gap-summary').click();
  await expect(page.locator('#gap-list')).toContainText('其他出站未采集，流量未知');
  await page.screenshot({ path: 'test-results/partial-singbox-desktop.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await expect(page.locator('#collector-message')).toContainText('当前合计不是全部代理流量');
  await page.screenshot({ path: 'test-results/partial-singbox-mobile.png', fullPage: true });
});
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
  await expect(page.locator('#download-title')).toHaveText('下载');
  await expect(page.locator('#download-value')).toHaveText('—');
  await expect(page.locator('#billing-exact')).toHaveText('1,000.800000000123 bytes');
  await page.selectOption('#provider-metric', 'billed');
  await expect(page.locator('#total-exact')).toHaveText('1,300 bytes');
  await page.locator('#records tr').first().locator('td').nth(7).locator('button').click();
  await expect(page.locator('#exact-value-text')).toHaveText('1,000.800000000123 bytes');
  await page.locator('#exact-value-dialog form button').click();
  await page.screenshot({ path: 'test-results/provider-statistics-desktop.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await expect(page.locator('#summary .metric-card')).toHaveCount(4);
  const cards = await page.locator('#summary .metric-card').evaluateAll(nodes => nodes.map(node => {
    const box = node.getBoundingClientRect(); return { top: box.top, left: box.left };
  }));
  expect(cards[0].top).toBe(cards[1].top);
  expect(cards[2].top).toBe(cards[3].top);
  expect(cards[0].left).toBe(cards[2].left);
  await expect.poll(() => page.locator('.chart-label').count()).toBeLessThanOrEqual(6);
  await expect(page.locator('#total-value')).toHaveText('< 0.001 GB');
  await page.screenshot({ path: 'test-results/provider-statistics-mobile.png', fullPage: true });
  await page.selectOption('#statistics-source', '');
  await expect(page.locator('#total-value')).toHaveText('16.106 GB');
});
test('same-name comparison aligns sources by hour and keeps week month and missing data explicit', async ({ page }) => {
  let localReads = 0, partialOnly = false;
  const local = { ...history, upload: '3500000000', download: '0', unbucketedUpload: '500', unbucketedDownload: '0',
    nodes: [{ key: 'a', name: 'Synthetic shared node', upload: '3000000000', download: '0' },
      { key: 'b', name: 'Synthetic shared node', upload: '500000000', download: '0' }], recordCount: 2 };
  const provider = { source: 'Synthetic comparison', rawBytes: '4000000000', billedBytes: '3200000000.000123', recordCount: 2,
    nodes: [{ name: 'Synthetic shared node', rawBytes: '4000000000', billedBytes: '3200000000.000123' },
      { name: 'CSV only', rawBytes: '0', billedBytes: '0' }], records: [] };
  const label = url => ({ hour: '2026-10-09T10:00+00:00', day: '2026-10-09', week: '2026-10-05', month: '2026-10' })[url.searchParams.get('bucket')] ?? '2026-10-09';
  const calls = await mock(page, {
    '/api/provider/sources': route => route.fulfill(json(['Synthetic comparison'])),
    '/api/nodes': route => route.fulfill(json(local.nodes.map(n => ({ key: n.key, name: n.name })))),
    '/api/provider/nodes': route => route.fulfill(json(provider.nodes.map(n => ({ key: n.name, name: n.name })))),
    '/api/history': (route, request, url) => route.fulfill(json(localReads++ ? partialOnly ?
      { ...local, upload: '0', download: '0', nodes: [], series: [], recordCount: 1, unbucketedUpload: '0', partialUpload: '500' } :
      { ...local, series: [{ label: label(url), upload: '3499999500', download: '0' }] } : history)),
    '/api/provider/history': (route, request, url) => route.fulfill(json({ ...provider, series: [{ label: label(url), rawBytes: provider.rawBytes, billedBytes: provider.billedBytes }] }))
  });
  await page.selectOption('#statistics-source', provider.source);
  await page.check('#compare-toggle');
  await expect(page.locator('#comparison-view')).toBeVisible();
  await expect(page.locator('#comparison-local-exact')).toHaveText('3,500,000,000 bytes');
  await page.selectOption('#provider-metric', 'billed');
  await expect(page.locator('#comparison-nodes .paired-value').last()).toHaveAttribute('title', /3,200,000,000.000123 bytes/);
  const only = page.locator('#comparison-nodes tr').filter({ hasText: 'CSV only' });
  await expect(only).toHaveClass(/missing/); await expect(only).toContainText('0.000 GB');
  await expect(only.locator('button')).toHaveAttribute('aria-label', /没有记录/);
  await expect(page.locator('#comparison-recovery')).toContainText('500 bytes');
  await expect(page.locator('#export-button')).toBeHidden();
  await page.click('[data-period=day]');
  await expect(page.locator('#bucket')).toHaveValue('hour');
  await expect(page.locator('#comparison-chart .comparison-bar')).toHaveCount(2);
  await expect.poll(() => page.locator('#comparison-chart .comparison-bar').first().evaluate(node => parseFloat(getComputedStyle(node).height))).toBeGreaterThan(0);
  await page.screenshot({ path: 'test-results/comparison-desktop.png', fullPage: true });
  await page.selectOption('#node-filter', 'Synthetic shared node');
  await expect.poll(() => calls.filter(c => c.path === '/api/history').at(-1).url.searchParams.get('nodeName')).toBe('Synthetic shared node');
  await page.click('[data-period=week]');
  await expect(page.locator('#bucket')).toHaveValue('day');
  await expect(page.locator('#comparison-chart .comparison-column')).toHaveCount(7);
  await expect(page.locator('#comparison-trend-caption')).toHaveText('日 · UTC');
  await page.selectOption('#bucket', 'week');
  await expect(page.locator('#comparison-buckets')).toContainText('2026-10-05');
  await page.click('[data-period=year]');
  await expect(page.locator('#bucket')).toHaveValue('month');
  await expect(page.locator('#comparison-chart .comparison-column')).toHaveCount(12);
  await page.click('[data-period=month]');
  await expect(page.locator('#bucket')).toHaveValue('day');
  await expect(page.locator('#comparison-chart .comparison-column')).toHaveCount(31);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await expect(page.locator('#comparison-chart .comparison-column')).toHaveCount(31);
  await expect(page.locator('#comparison-chart')).not.toContainText('无法清楚展示');
  await page.screenshot({ path: 'test-results/comparison-mobile.png', fullPage: true });
  partialOnly = true; await page.selectOption('#bucket', 'hour');
  await expect(page.locator('#comparison-local')).toHaveText('—');
  await expect(page.locator('#comparison-local-exact')).toHaveText('所选范围无已知量');
  await expect(page.locator('#comparison-recovery')).toContainText('部分重叠恢复区间（未计入）：500 bytes');
  await expect(page.locator('#comparison-raw-exact')).toHaveText('4,000,000,000 bytes');
  await page.selectOption('#statistics-source', '');
  await expect(page.locator('#comparison-view')).toBeHidden();
  expect(await page.locator('#bucket option').count()).toBe(5);
});
test('year of hourly comparison buckets stays paged and keeps the full exact total', async ({ page }) => {
  let reads = 0;
  const series = Array.from({ length: 8760 }, (_, i) => ({
    label: new Date(Date.UTC(2026, 0, 1, i)).toISOString().slice(0, 16) + '+00:00', upload: '1', download: '0'
  }));
  const local = { ...history, upload: '8760', download: '0', recordCount: 8760, records: [], series,
    nodes: [{ key: 'a', name: 'Synthetic hourly', upload: '8760', download: '0' }] };
  const provider = { source: 'Synthetic paging', rawBytes: '0', billedBytes: '0', recordCount: 0, nodes: [], series: [], records: [] };
  const calls = await mock(page, {
    '/api/provider/sources': route => route.fulfill(json([provider.source])),
    '/api/provider/nodes': route => route.fulfill(json([])),
    '/api/provider/history': route => route.fulfill(json(provider)),
    '/api/history': route => route.fulfill(json(reads++ ? local : history))
  });
  await page.selectOption('#statistics-source', provider.source);
  await page.click('[data-period=custom]'); await page.fill('#custom-from', '2026-01-01'); await page.fill('#custom-through', '2026-12-31');
  await page.locator('#custom-range').getByRole('button').click();
  await page.selectOption('#bucket', 'hour');
  await page.check('#compare-toggle');
  await expect(page.locator('#comparison-local-exact')).toHaveText('8,760 bytes');
  await expect(page.locator('#comparison-buckets tr')).toHaveCount(100);
  await expect(page.locator('#comparison-bucket-page')).toContainText('8,760');
  await expect(page.locator('#comparison-chart')).toContainText('请缩小日期范围或选择周/月');
  const queries = calls.filter(c => c.path === '/api/history').length;
  await page.click('#comparison-bucket-next');
  await expect(page.locator('#comparison-buckets tr').first()).toContainText(series[50].label);
  await page.selectOption('#unit', 'GiB');
  await expect(page.locator('#comparison-buckets tr').first()).toContainText(series[50].label);
  await expect(page.locator('#comparison-local-exact')).toHaveText('8,760 bytes');
  expect(calls.filter(c => c.path === '/api/history').length).toBe(queries);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});
test('one-time session, secure text rendering, exact unit toggle, and desktop layout', async ({ page }) => {
  const malicious = '<img src=x onerror=alert(1)>';
  const calls = await mock(page, { '/api/nodes': route => route.fulfill(json([{ key: 'x', name: malicious }])), '/api/history': route => route.fulfill(json({ ...history, nodes: [{ key: 'x', name: malicious, upload: '1073741824', download: '15032385536' }], records: [{ ...history.records[0], nodeName: malicious }] })) });
  expect(new URL(page.url()).hash).toBe('');
  expect(calls.filter(c => c.path === '/api/session')).toHaveLength(1);
  expect(calls.find(c => c.path === '/api/session').headers['x-monitor-request']).toBe('1');
  await expect(page.locator('#ranking')).toContainText(malicious);
  await expect(page.locator('img')).toHaveCount(1);
  await expect(page.locator('.brand > img.brand-symbol')).toHaveAttribute('src', '/icon.svg');
  await expect(page.locator('#ranking img, #records img, #import-batches img')).toHaveCount(0);
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
test('overlap CSV preview and commit show new skipped counts without duplicate billing', async ({ page }) => {
  let done = false;
  const result = { hash: 'synthetic-overlap', rowCount: 4, rawBytes: '50', billedBytes: '34.000000000492',
    firstTime: '2026-01-01T10:00:00Z', lastTime: '2026-01-01T11:00:00Z', warnings: [] };
  const added = { ...result, newRowCount: 2, skippedRowCount: 2, newRawBytes: '30', newBilledBytes: '18.000000000246', duplicate: false };
  const duplicate = { ...result, newRowCount: 0, skippedRowCount: 4, newRawBytes: '0', newBilledBytes: '0', duplicate: true };
  const calls = await mock(page, {
    '/api/imports/preview': route => route.fulfill(json(done ? duplicate : added)),
    '/api/imports/commit': route => { done = true; return route.fulfill(json(added)); }
  });
  await page.click('.nav-item[data-view=provider]'); await page.fill('#provider-source', 'Synthetic overlapping source');
  await page.setInputFiles('#csv-file', { name: 'synthetic-overlap.csv', mimeType: 'text/csv',
    buffer: Buffer.from('node_name,rate,origin_traffic,origin_x_rate,traffic,log_time\nSynthetic-A,0.8,10,8.000000000123,display,2026-01-01 10:00:00') });
  await page.click('#preview-import');
  await expect(page.locator('#preview-details')).toContainText('本次新增：2 条');
  await expect(page.locator('#preview-details')).toContainText('跳过已存在：2 条');
  await expect(page.locator('#preview-details')).toContainText('18.000000000246 bytes');
  await page.screenshot({ path: 'test-results/overlap-preview-desktop.png', fullPage: true });
  await page.click('#commit-import');
  await expect(page.locator('#import-result')).toContainText('新增 2 条，跳过 2 条');
  await expect(page.locator('#import-result')).toContainText('18.000000000246 bytes');
  await page.click('#preview-import');
  await expect(page.locator('#preview-details')).toContainText('本次新增：0 条');
  await expect(page.locator('#preview-details')).toContainText('跳过已存在：4 条');
  await expect(page.locator('#commit-import')).toBeDisabled();
  expect(calls.filter(c => c.path === '/api/imports/commit')).toHaveLength(1);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'test-results/overlap-preview-mobile.png', fullPage: true });
});
test('provider decimal preview, commit and reloaded list display every digit on desktop and mobile', async ({ page }) => {
  let committed = false;
  const batch = { source: 'Synthetic Decimal', hash: 'synthetic-decimal', rowCount: 3, rawBytes: '3001', billedBytes: '2600.800000000123', importedAt: '2026-01-02T00:00:00Z', firstTime: '2026-01-01T00:00:00Z', lastTime: '2026-01-01T00:02:00Z' };
  const calls = await mock(page, {
    '/api/imports/preview': route => route.fulfill(json({ ...batch, warnings: [], duplicate: committed,
      newRowCount: committed ? 0 : 3, skippedRowCount: committed ? 3 : 0, newRawBytes: committed ? '0' : '3001', newBilledBytes: committed ? '0' : batch.billedBytes })),
    '/api/imports/commit': route => { committed = true; return route.fulfill(json({ ...batch, newRowCount: 3,
      skippedRowCount: 0, newRawBytes: '3001', newBilledBytes: batch.billedBytes, duplicate: false })); },
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

test('local minute details keep compact columns, exact bytes and scoped horizontal scrolling', async ({ page }) => {
  const values = ['12345', '98765', '432', '0', '1000000', '1234567890', '9007199254740993'];
  const records = Array.from({ length: 50 }, (_, i) => ({ ...history.records[0],
    start: new Date(Date.UTC(2026, 9, 9, 11, -i)).toISOString(),
    nodeName: i === 5 ? 'Synthetic · Long node name for ellipsis and full-name tooltip verification' : 'Synthetic · Singapore',
    download: values[i % values.length], upload: String(i * 123), quality: i === 6 ? 'recovered-partial' : 'approximate'
  }));
  await mock(page, { '/api/history': route => route.fulfill(json({ ...history, records, recordCount: 1882 })) });
  const panel = page.locator('.records-panel');
  await expect(page.locator('#records tr')).toHaveCount(50);
  await expect(page.locator('#records tr').first().locator('td').nth(4)).toHaveText('< 0.001 GB');
  await expect(page.locator('#records tr').first().locator('td').nth(4)).toHaveAttribute('title', '12,345 bytes');
  await expect(page.locator('#records .approximate')).toHaveCount(49);
  await expect(page.locator('#records tr').first().locator('.zero')).toHaveText('0.000 GB');
  await page.locator('#records tr').first().locator('td').nth(4).locator('button').click();
  await expect(page.locator('#exact-value-text')).toHaveText('12,345 bytes');
  await page.locator('#exact-value-dialog form button').click();
  await expect(page.locator('#records tr').nth(5).locator('.node-cell')).toHaveAttribute('title', records[5].nodeName);
  await expect(page.locator('#records .warning')).toHaveCount(1);
  await panel.screenshot({ path: 'test-results/local-details-after-desktop.png' });
  await page.selectOption('#unit', 'GiB');
  await expect(page.locator('#records tr').first().locator('td').nth(4)).toHaveText('< 0.001 GiB');
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  const scroll = panel.locator('.table-scroll');
  expect(await scroll.evaluate(node => node.scrollWidth > node.clientWidth)).toBe(true);
  await panel.screenshot({ path: 'test-results/local-details-after-mobile.png' });
  await scroll.evaluate(node => { node.scrollLeft = node.scrollWidth; });
  await expect(page.locator('#quality-column')).toBeInViewport();
  await page.click('#next-page');
  await expect(page.locator('#page-label')).toContainText('第 2 / 38 页');
  await page.click('#previous-page');
  await expect(page.locator('#page-label')).toContainText('第 1 / 38 页');
});

test('local partial recovery is unknown and a known idle record remains zero', async ({ page }) => {
  let result = history;
  await mock(page, { '/api/history': route => route.fulfill(json(result)) });
  result = { ...history, upload: '0', download: '0', nodes: [], series: [], recordCount: 1,
    partialUpload: '500', partialDownload: '0', records: [{ ...history.records[0], upload: '500', download: '0', quality: 'recovered-partial' }] };
  await page.selectOption('#bucket', 'hour');
  for (const id of ['total', 'download', 'upload']) {
    await expect(page.locator(`#${id}-value`)).toHaveText('—');
    await expect(page.locator(`#${id}-exact`)).toHaveText('所选范围无已知量');
  }
  await expect(page.locator('#records')).toContainText('部分重叠');
  result = { ...result, nodes: [{ ...history.nodes[0], upload: '0', download: '0' }], recordCount: 2 };
  await page.selectOption('#bucket', 'day');
  await expect(page.locator('#total-value')).toHaveText('0.000 GB');
  await expect(page.locator('#total-exact')).toHaveText('0 bytes');
});
