import { test, expect } from '@playwright/test';
const json = data => ({ status: 200, contentType: 'application/json', body: JSON.stringify(data) });
const history = {
  start: '2026-10-01T00:00:00Z', end: '2026-11-01T00:00:00Z', upload: '1073741824', download: '15032385536', unallocatedUpload: '0', unallocatedDownload: '0', gapCount: 0,
  series: Array.from({ length: 18 }, (_, i) => ({ label: `2026-10-${String(i + 1).padStart(2, '0')}`, upload: String((i + 1) * 11000000), download: String((i * 37 % 8 + 1) * 180000000) })),
  nodes: [{ key: 'a', name: 'Synthetic · Singapore', upload: '536870912', download: '8589934592' }, { key: 'b', name: 'Synthetic · Tokyo', upload: '268435456', download: '4294967296' }, { key: 'c', name: 'Synthetic · Frankfurt', upload: '268435456', download: '2147483648' }],
  records: [{ start: '2026-10-09T11:00:00Z', end: '2026-10-09T11:01:00Z', nodeKey: 'a', nodeName: 'Synthetic · Singapore', upload: '1073741824', download: '15032385536', quality: 'measured' }], recordCount: 51
};
async function mock(page, overrides = {}, controlClock = false) {
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
  if (controlClock) await page.clock.install({ time: new Date('2026-10-09T19:00:00Z') });
  else await page.clock.setFixedTime(new Date('2026-10-09T19:00:00Z'));
  await page.goto('/#one-time-synthetic-token');
  await expect(page.locator('#total-value')).toHaveText('16.106 GB');
  return calls;
}
async function settledScreenshot(page, path) {
  await expect(page.locator('.motion-snapshot')).toHaveCount(0);
  await page.evaluate(() => window.scrollTo(0, 0));
  await expect.poll(() => page.evaluate(() => document.getAnimations().filter(animation => animation.playState === 'running').length)).toBe(0);
  await page.screenshot({ path, fullPage: true, animations: 'disabled' });
}
test('raw and billing comparisons switch both sources and disclose missing multiplier coverage', async ({ page }) => {
  let reads = 0;
  const display = { originalName: 'Synthetic known multiplier', groups: [], mergeKey: null, rate: null, rateStatus: 'partial',
    estimatedBilledBytes: '1000000000.5', estimateCoveredBytes: '2000000001', estimateUnknownBytes: '999999999' };
  const local = { ...history, upload: '1000000000', download: '3000000000',
    display: { ...display, estimateUnknownBytes: '1999999999' },
    nodes: [{ key: 'known', name: display.originalName, upload: '1000000000', download: '2000000000', display },
      { key: 'unknown', name: 'Synthetic unknown multiplier', upload: '0', download: '1000000000' }],
    series: [{ label: '2026-10-09', upload: '1000000000', download: '3000000000',
      display: { ...display, estimateUnknownBytes: '1999999999' } }] };
  const provider = { source: 'Synthetic billing source', rawBytes: '5000000000', billedBytes: '2500000000.000000000123', recordCount: 1,
    nodes: [{ name: display.originalName, rawBytes: '5000000000', billedBytes: '2500000000.000000000123' }],
    series: [{ label: '2026-10-09', rawBytes: '5000000000', billedBytes: '2500000000.000000000123' }], records: [] };
  await mock(page, {
    '/api/provider/sources': route => route.fulfill(json([provider.source])),
    '/api/provider/nodes': route => route.fulfill(json(provider.nodes.map(n => ({ key: n.name, name: n.name })))),
    '/api/provider/history': route => route.fulfill(json(provider)),
    '/api/history': route => route.fulfill(json(reads++ ? local : history))
  });
  await page.selectOption('#statistics-source', provider.source);
  await page.check('#compare-toggle');
  await expect(page.locator('#comparison-local-exact')).toHaveText('4,000,000,000 bytes');
  await page.selectOption('#provider-metric', 'billed');
  await expect(page.locator('#comparison-local-title')).toHaveText('本机估算计费量');
  await expect(page.locator('#comparison-local-exact')).toHaveText('1,000,000,000.5 bytes');
  await expect(page.locator('#comparison-estimate-note')).toContainText('未估算原始量 1,999,999,999 bytes');
  const unknown = page.locator('#comparison-nodes tr').filter({ has: page.locator('.comparison-name', { hasText: 'Synthetic unknown multiplier' }) });
  await expect(unknown.locator('.paired-value')).toHaveText('—');
  await expect(page.locator('#comparison-nodes .rank-upload, #comparison-nodes .rank-download')).toHaveCount(0);
  for (const [width, height, theme, suffix] of [[1440, 1100, 'light', 'desktop'], [390, 844, 'dark', 'mobile']]) {
    await page.setViewportSize({ width, height }); await page.selectOption('#theme', theme);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await settledScreenshot(page, `test-results/billing-comparison-${suffix}.png`);
    await page.click('[data-comparison-section="nodes"][data-comparison-mode="share"]');
    await expect(page.locator('#comparison-nodes-shares [data-source="local"] h3')).toHaveText('本机估算计费量');
    await expect(page.locator('#comparison-nodes-shares [data-source="local"] circle')).toHaveCount(1);
    await settledScreenshot(page, `test-results/billing-shares-${suffix}.png`);
    await page.click('[data-comparison-section="nodes"][data-comparison-mode="list"]');
  }
  await page.selectOption('#provider-metric', 'raw');
  await expect(page.locator('#comparison-local-exact')).toHaveText('4,000,000,000 bytes');
  await expect(page.locator('#comparison-nodes .rank-download')).toHaveCount(2);
  await expect(page.locator('#comparison-estimate-note')).toBeHidden();
});
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
  await expect(page.locator('#probe-coverage')).toContainText('按分钟保存，并非精确的首次采样时间');
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
test('statistics and probes keep separate megabyte preferences across navigation and reload', async ({ page }) => {
  const calls = await mock(page, {
    '/api/status': route => route.fulfill(json({ state: 'collecting', message: 'Synthetic unit preferences', demo: false,
      currentNode: 'Synthetic member', lastSample: null, uploadPerSecond: '524288', downloadPerSecond: '2097152',
      probes: { historyEnabled: true, state: 'available' } }))
  });
  await expect(page.locator('#unit option')).toHaveText(['GB', 'GiB', 'MB', 'MiB']);
  await page.selectOption('#unit', 'GiB');
  await expect(page.locator('#total-value')).toHaveText('15.000 GiB');
  await page.click('.nav-item[data-view=probes]');
  await expect(page.locator('#total-value')).toHaveText('16,106.127 MB');
  await expect(page.locator('#unit')).toHaveValue('MB');
  await expect(page.locator('#total-value .metric-unit')).toHaveText(' MB');
  await settledScreenshot(page, 'test-results/probe-units-mb-desktop.png');
  const reads = calls.filter(call => call.path === '/api/history').length;
  await page.selectOption('#unit', 'MiB');
  await expect(page.locator('#total-value')).toHaveText('15,360.000 MiB');
  await expect(page.locator('#live-download')).toHaveText('↓ 2.00 MiB/s');
  expect(calls.filter(call => call.path === '/api/history').length).toBe(reads);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.selectOption('#theme', 'dark');
  await page.click('#filter-disclosure');
  await settledScreenshot(page, 'test-results/probe-units-mib-mobile.png');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.click('.nav-item[data-view=overview]');
  await expect(page.locator('#total-value')).toHaveText('15.000 GiB');
  await expect(page.locator('#unit')).toHaveValue('GiB');
  await page.reload();
  await expect(page.locator('#total-value')).toHaveText('15.000 GiB');
  await page.click('.nav-item[data-view=probes]');
  await expect(page.locator('#total-value')).toHaveText('15,360.000 MiB');
  await expect(page.locator('#unit')).toHaveValue('MiB');
  await expect(page.locator('#total-exact')).toHaveText('16,106,127,360 bytes');
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
  await page.locator('.rank-value').first().hover();
  await expect(page.locator('#detail-popover')).toBeVisible();
  await expect(page.locator('#detail-popover')).toContainText('其中恢复量');
  await expect(page.locator('.rank-value').first()).toHaveText('9.127 GB');
  await page.getByRole('button', { name: '查看精确值', exact: true }).click();
  await expect(page.locator('#exact-value-dialog')).toBeVisible();
  await expect(page.locator('#exact-value-text')).toContainText('123,456,789 bytes');
  await page.locator('#exact-value-dialog form button').click();
  for (const [width, height, suffix] of [[1440, 1100, 'desktop'], [390, 844, 'mobile']]) {
    await page.setViewportSize({ width, height });
    await page.click('[data-ranking-mode=ranking]');
    await expect(page.locator('.motion-snapshot')).toHaveCount(0);
    const panel = page.locator('.ranking-panel');
    const rankingHeight = (await panel.boundingBox()).height;
    const marker = await page.locator('#ranking .rank-recovery-marker:not(.vacant)').boundingBox();
    const value = await page.locator('.rank-value').first().boundingBox();
    expect(Math.abs(marker.y + marker.height / 2 - value.y - value.height / 2)).toBeLessThanOrEqual(1);
    expect(marker.width).toBeLessThanOrEqual(4);
    expect((await page.locator('.rank-row').first().boundingBox()).height).toBeLessThan(64);
    await page.screenshot({ path: `test-results/issue23-ranking-${suffix}.png`, fullPage: true });
    await page.click('[data-ranking-mode=share]');
    await expect(page.locator('.motion-snapshot')).toHaveCount(0);
    await expect(page.locator('.share-chart circle')).toHaveCount(3);
    await expect(page.locator('#ranking .rank-value, #ranking .rank-track, .share-total')).toHaveCount(0);
    expect(Math.abs((await panel.boundingBox()).height - rankingHeight)).toBeLessThanOrEqual(1);
    const tooltip = page.locator('.share-tooltip');
    await expect(tooltip).toBeHidden();
    const ring = await page.locator('.share-chart').boundingBox();
    await page.mouse.move(ring.x + ring.width * .86, ring.y + ring.height / 2);
    await expect(tooltip).toHaveText('9.127 GB');
    await expect(tooltip).toBeVisible();
    await page.screenshot({ path: `test-results/issue23-share-hover-${suffix}.png`, fullPage: true });
    await page.mouse.move(0, 0);
    await expect(tooltip).toBeHidden();
    await page.locator('.share-chart circle').first().focus();
    await expect(tooltip).toBeVisible();
    await page.locator('[data-ranking-mode=share]').focus();
    await expect(tooltip).toBeHidden();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/issue23-shares-${suffix}.png`, fullPage: true });
  }
});
test('group outlines preserve text alignment and share legends keep percentages on the right', async ({ page }) => {
  const display = { originalName: history.nodes[0].name, groups: ['Synthetic group'], mergeKey: null, rate: '1', rateStatus: 'known',
    estimatedBilledBytes: '9126805504', estimateCoveredBytes: '9126805504', estimateUnknownBytes: '0' };
  const nodes = [{ ...history.nodes[0], display }, history.nodes[1]];
  await mock(page, {
    '/api/history': route => route.fulfill(json({ ...history, nodes, records: [{ ...history.records[0], display },
      { ...history.records[0], nodeKey: nodes[1].key, nodeName: nodes[1].name }] })),
    '/api/provider/sources': route => route.fulfill(json(['Synthetic provider'])),
    '/api/provider/nodes': route => route.fulfill(json(nodes.map(node => ({ key: node.name, name: node.name })))),
    '/api/provider/history': route => route.fulfill(json({ source: 'Synthetic provider', rawBytes: '200', billedBytes: '200', recordCount: 2,
      nodes: nodes.map(node => ({ name: node.name, rawBytes: '100', billedBytes: '100' })), series: [], records: [] }))
  });
  const textInsets = locator => locator.evaluateAll(elements => elements.map(element => {
    const label = element.querySelector('button') ?? element;
    const range = document.createRange(); range.selectNodeContents(label);
    return range.getBoundingClientRect().left - element.getBoundingClientRect().left;
  }));
  for (const [width, height] of [[1440, 1100], [390, 844]]) {
    await page.setViewportSize({ width, height });
    const insets = await textInsets(page.locator('#ranking .rank-node'));
    expect(Math.abs(insets[0] - insets[1])).toBeLessThanOrEqual(1);
    const recordInsets = await textInsets(page.locator('#records .node-cell'));
    expect(Math.abs(recordInsets[0] - recordInsets[1])).toBeLessThanOrEqual(1);
    await page.locator('[data-ranking-mode=share]').click();
    const geometry = await page.locator('#ranking').evaluate(ranking => {
      const ring = ranking.querySelector('.share-chart').getBoundingClientRect(), panel = ranking.getBoundingClientRect();
      return { ringWidth: ring.width, centerOffset: ring.y + ring.height / 2 - panel.y - panel.height / 2,
        rows: [...ranking.querySelectorAll('.rank-heading')].map(row => {
          const name = row.querySelector('.rank-node').getBoundingClientRect();
          return { nameLeft: name.left, nameRight: name.right, swatchRight: row.querySelector('.share-swatch').getBoundingClientRect().right,
            percentageLeft: row.querySelector('.rank-index').getBoundingClientRect().left };
        }) };
    });
    expect(geometry.ringWidth).toBeGreaterThan(width > 640 ? 170 : 100);
    expect(Math.abs(geometry.centerOffset)).toBeLessThanOrEqual(6);
    for (const row of geometry.rows) { expect(row.swatchRight).toBeLessThan(row.nameLeft); expect(row.percentageLeft).toBeGreaterThan(row.nameRight); }
    expect(Math.abs(geometry.rows[0].nameLeft - geometry.rows[1].nameLeft)).toBeLessThanOrEqual(1);
    await page.locator('#statistics-source').selectOption('Synthetic provider');
    await page.locator('#compare-toggle').check();
    await expect(page.locator('#comparison-nodes .comparison-name')).toHaveCount(2);
    const comparisonInsets = await textInsets(page.locator('#comparison-nodes .comparison-name'));
    expect(Math.abs(comparisonInsets[0] - comparisonInsets[1])).toBeLessThanOrEqual(1);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.locator('#statistics-source').selectOption('');
    await expect(page.locator('#ranking .rank-node')).toHaveCount(2);
    await page.locator('[data-ranking-mode=ranking]').click();
  }
});
test('traffic details remain open across polling and support keyboard dismissal', async ({ page }) => {
  const calls = await mock(page, {
    '/api/history': route => route.fulfill(json({ ...history,
      nodes: history.nodes.map((n, i) => ({ ...n, recoveredUpload: i === 0 ? '123456789' : '0', recoveredDownload: '0' })) }))
  }, true);
  const value = page.locator('.rank-value').first(), popup = page.locator('#detail-popover');
  await value.focus();
  await expect(popup).toBeVisible();
  await value.press('Escape');
  await expect(popup).toBeHidden();
  await page.locator('[data-ranking-mode=ranking]').focus();
  await value.focus();
  await expect(popup).toBeVisible();
  const previousValue = await value.elementHandle();
  const before = calls.filter(c => c.path === '/api/history').length;
  await page.clock.fastForward(30000);
  await expect.poll(() => calls.filter(c => c.path === '/api/history').length, { timeout: 8000 }).toBeGreaterThan(before);
  await expect.poll(() => previousValue.evaluate(node => node.isConnected)).toBe(false);
  await expect(popup).toBeVisible();
  await expect(value).toHaveAttribute('aria-expanded', 'true');
  await expect(value).toBeFocused();
  await expect(popup).toContainText('已包含在上传、下载及合计中');
  await value.click();
  await popup.getByRole('button', { name: '关闭', exact: true }).click();
  await expect(popup).toBeHidden();
  await expect(value).toBeFocused();
  await page.locator('.rank-value').nth(1).focus();
  await expect(popup).toContainText('当前合计不含恢复量');
  await page.selectOption('#unit', 'GiB');
  await expect(popup).toBeHidden();
});
test('both themes preserve dense layouts and appearance persists independently of settings', async ({ page }) => {
  const calls = await mock(page);
  for (const theme of ['dark', 'light']) {
    await page.selectOption('#theme', theme);
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
    for (const [width, height] of [[1440, 1100], [960, 1000], [390, 844]]) {
      await page.setViewportSize({ width, height });
      for (const view of ['overview', 'probes', 'provider', 'settings']) {
        await page.click(`.nav-item[data-view=${view}]`);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
        await expect(page.locator('#theme')).toBeVisible();
      }
    }
  }
  expect(calls.filter(c => c.path === '/api/settings' && c.body !== null)).toHaveLength(0);
  await page.selectOption('#theme', 'dark');
  await page.reload();
  await expect(page.locator('#theme')).toHaveValue('dark');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await page.emulateMedia({ colorScheme: 'light' });
  await page.selectOption('#theme', 'system');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await page.emulateMedia({ colorScheme: 'dark' });
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
});
test.describe('touch traffic details', () => {
  test.use({ hasTouch: true, viewport: { width: 390, height: 844 } });
  test('total and recovery indicator share a tappable detail target', async ({ page }) => {
    await mock(page, { '/api/history': route => route.fulfill(json({ ...history,
      nodes: [{ ...history.nodes[0], recoveredUpload: '123456789', recoveredDownload: '0' }] })) });
    await page.locator('.rank-value').tap();
    const popup = page.locator('#detail-popover');
    await expect(popup).toBeVisible();
    await page.keyboard.press('Control+Home');
    const box = await popup.boundingBox();
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(390);
    expect(box.y).toBeGreaterThanOrEqual(0);
    expect(box.y + box.height).toBeLessThanOrEqual(844);
    await popup.getByRole('button', { name: '查看精确值', exact: true }).tap();
    await expect(page.locator('#exact-value-dialog')).toBeVisible();
    await expect(page.locator('#exact-value-text')).toContainText('123,456,789 bytes');
    await page.locator('#exact-value-dialog form button').tap();
    await expect(page.locator('#exact-value-dialog')).toBeHidden();
  });
});
test('issue23 captured rates, group names and exact copy work across business and probe views', async ({ page }) => {
  const display = { originalName: 'Synthetic original 0.5x', groups: ['US-0.5x'], mergeKey: null, rate: '0.5', rateStatus: 'known',
    estimatedBilledBytes: '8053063680', estimateCoveredBytes: '16106127360', estimateUnknownBytes: '0' };
  const calls = await mock(page, {
    '/api/status': route => route.fulfill(json({ state: 'collecting', message: 'Synthetic', currentNode: 'Synthetic', lastSample: null,
      uploadPerSecond: null, downloadPerSecond: null, demo: false, probes: { historyEnabled: true, state: 'available', message: 'Synthetic' } })),
    '/api/history': (route, request, url) => route.fulfill(json({ ...history, basis: url.searchParams.get('basis') ?? 'local_proxy',
      displayMetadataEnabled: true, display, nodes: [{ ...history.nodes[0], display }], records: [
        { ...history.records[0], display },
        { ...history.records[0], display: { ...display, groups: ['Synthetic second snapshot'] } }] }))
  }, true);
  await expect(page.locator('#billing-value')).toHaveText('8.053 GB');
  const groupedName = page.locator('#records .node-cell .has-groups').first();
  await expect(groupedName).toHaveText('Synthetic original 0.5x');
  await groupedName.hover();
  await expect(page.getByRole('dialog', { name: '策略组', exact: true })).toContainText('US-0.5x');
  await groupedName.focus();
  await expect(page.getByRole('dialog', { name: '策略组', exact: true })).toContainText('US-0.5x');
  await groupedName.press('Escape');
  await groupedName.click();
  const previousGroup = await groupedName.elementHandle();
  const before = calls.filter(c => c.path === '/api/history').length;
  await page.clock.fastForward(30000);
  await expect.poll(() => calls.filter(c => c.path === '/api/history').length, { timeout: 8000 }).toBeGreaterThan(before);
  await expect.poll(() => previousGroup.evaluate(node => node.isConnected)).toBe(false);
  await expect(page.locator('#detail-popover .node-group-list')).toHaveText('US-0.5x');
  await groupedName.press('Escape');
  await page.locator('#ranking .has-groups').click();
  await expect(page.locator('#node-filter')).toHaveValue('a');
  await expect.poll(() => calls.filter(c => c.path === '/api/history').at(-1).url.searchParams.get('nodeKey')).toBe('a');
  await expect(page.locator('#records tr').first().locator('td').nth(6)).toHaveText('0.5');
  await page.evaluate(() => Object.defineProperty(navigator, 'clipboard', { configurable: true,
    value: { writeText: async text => { window.syntheticCopied = text; } } }));
  await page.locator('#records tr').first().locator('td').nth(7).locator('button').click();
  await page.click('#copy-exact-value');
  await expect(page.locator('#copy-value-result')).toHaveText('已复制');
  expect(await page.evaluate(() => window.syntheticCopied)).toBe('8,053,063,680 bytes');
  await page.locator('#exact-value-dialog form button').click();
  await page.click('.nav-item[data-view=probes]');
  await expect(page.locator('#billing-value')).toHaveText('8,053.064 MB');
  await page.screenshot({ path: 'test-results/issue23-captured-desktop.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: 'test-results/issue23-captured-mobile.png', fullPage: true });
});
test('historical and captured group members use the same labels without merging same-name identities', async ({ page }) => {
  const nodes = history.nodes.map((node, index) => {
    const name = index < 2 ? 'Synthetic historical member' : 'Synthetic current member';
    const total = String(BigInt(node.upload) + BigInt(node.download));
    return { ...node, name, display: { originalName: name, groups: ['Synthetic Route'], mergeKey: null,
      rate: index < 2 ? null : '1', rateStatus: index < 2 ? 'unknown' : 'known',
      estimatedBilledBytes: index < 2 ? null : total,
      estimateCoveredBytes: index < 2 ? '0' : total, estimateUnknownBytes: index < 2 ? total : '0' } };
  });
  await mock(page, {
    '/api/status': route => route.fulfill(json({ state: 'collecting', message: 'Synthetic Xray',
      currentNode: 'Synthetic current member', demo: false,
      lastSample: null, uploadPerSecond: null, downloadPerSecond: null,
      probes: { historyEnabled: true, state: 'unsupported', message: 'Synthetic stock Xray; saved probe history remains visible' } })),
    '/api/nodes': route => route.fulfill(json(nodes.map(node => ({ key: node.key, name: node.name })))),
    '/api/history': route => route.fulfill(json({ ...history, nodes, records: nodes.map(node => ({
      ...history.records[0], nodeKey: node.key, nodeName: node.name, upload: node.upload, download: node.download,
      display: node.display
    })) }))
  });
  await expect(page.locator('#ranking .rank-node.has-groups')).toHaveCount(3);
  await expect(page.locator('#ranking .rank-node').filter({ hasText: 'Synthetic historical member' })).toHaveCount(2);
  await expect(page.locator('#node-filter option').filter({ hasText: 'Synthetic historical member' })).toHaveCount(2);
  await expect(page.locator('#records .has-groups')).toHaveCount(3);
  await expect(page.locator('#records tr').first().locator('td').nth(6)).toHaveText('—');
  await page.locator('#ranking .has-groups').first().hover();
  await expect(page.getByRole('dialog', { name: '策略组', exact: true })).toContainText('Synthetic Route');
  await page.locator('#ranking .has-groups').first().press('Escape');
  for (const [width, height, theme, suffix] of [[1440, 1100, 'light', 'desktop'], [390, 844, 'dark', 'mobile']]) {
    await page.setViewportSize({ width, height });
    await page.selectOption('#theme', theme);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/historical-groups-${suffix}.png`, fullPage: true, animations: 'disabled' });
  }
  await page.locator('#ranking .rank-node').filter({ hasText: 'Synthetic historical member' }).first().click();
  await expect(page.locator('#node-filter')).toHaveValue('a');
  await page.click('.nav-item[data-view=probes]');
  await expect(page.locator('#statistics-title')).toHaveText('探测流量');
  await expect(page.locator('#probe-state')).toHaveText('当前核心未支持探测计量');
  await expect(page.locator('#ranking .rank-node.has-groups')).toHaveCount(3);
  await expect(page.locator('#ranking .rank-node').filter({ hasText: 'Synthetic historical member' })).toHaveCount(2);
  await page.locator('#ranking .has-groups').first().hover();
  await expect(page.getByRole('dialog', { name: '策略组', exact: true })).toContainText('Synthetic Route');
  await page.screenshot({ path: 'test-results/historical-probe-groups-mobile.png', fullPage: true, animations: 'disabled' });
});
test('live proxy rates refresh independently of history and stay readable with a group selected', async ({ page }) => {
  let upload = '1048576', download = '2097152';
  const calls = await mock(page, {
    '/api/status': route => route.fulfill(json({ state: 'collecting', message: 'Synthetic grouped collector', demo: false,
      currentNode: 'Synthetic · strategy group', lastSample: '2026-10-09T19:00:00Z',
      uploadPerSecond: upload, downloadPerSecond: download,
      scope: { basis: 'xray_outbound_members', isPartial: false, collectedOutbounds: 3, collectedGroups: 2,
        uncollectedOutbounds: 0, uncollectedGroups: 0, description: 'Synthetic independent outbounds; groups are not added again.' } }))
  }, true);
  await page.selectOption('#unit', 'GiB');
  await expect(page.getByRole('group', { name: '已采集代理合计实时速率' })).toContainText('代理合计');
  await expect(page.locator('#live-upload')).toHaveText('↑ 1.00 MiB/s');
  const before = calls.filter(call => call.path === '/api/history').length;
  upload = '2097152'; download = '4194304';
  await page.clock.fastForward(2000);
  await expect(page.locator('#live-upload')).toHaveText('↑ 2.00 MiB/s');
  await expect(page.locator('#live-download')).toHaveText('↓ 4.00 MiB/s');
  expect(calls.filter(call => call.path === '/api/history')).toHaveLength(before);
  const response = page.waitForResponse(r => new URL(r.url()).pathname === '/api/history');
  await page.clock.fastForward(28000);
  await response;
  await expect(page.locator('#summary')).toHaveAttribute('aria-busy', 'false');
  expect(calls.filter(call => call.path === '/api/history')).toHaveLength(before + 1);
  for (const [width, height, theme, suffix] of [[1440, 1100, 'light', 'desktop'], [390, 844, 'dark', 'mobile']]) {
    await page.setViewportSize({ width, height }); await page.selectOption('#theme', theme);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/live-rates-${suffix}.png`, fullPage: true, animations: 'disabled' });
  }
});
test('collector strip aligns node and rates without repeating implementation details', async ({ page }) => {
  const name = 'Synthetic · A deliberately long current member name '.repeat(4);
  const description = '采集 35 个 Xray 独立代理出站，包含 2 个策略组；不叠加策略组，排除直连和阻断。内部探测未单独区分。';
  await mock(page, {
    '/api/status': route => route.fulfill(json({ state: 'collecting', message: 'Xray 多出站联合采样；proxy 延续原历史。',
      currentNode: name, demo: false, lastSample: null, uploadPerSecond: null, downloadPerSecond: '0',
      scope: { basis: 'xray_outbound_members', isPartial: false, collectedOutbounds: 35, collectedGroups: 2,
        uncollectedOutbounds: 0, uncollectedGroups: 0, description } }))
  });
  await expect(page.locator('#collector-message')).toHaveText('Xray · 35 个代理出站 · 2 个策略组 · 不含直连和阻断');
  await expect(page.locator('#live-upload')).toHaveText('↑ —');
  await expect(page.locator('#live-download')).toHaveText('↓ 0 B/s');
  await expect(page.locator('#current-node')).toHaveAttribute('title', name);
  for (const width of [2089, 1440, 900, 640, 390, 320]) {
    await page.setViewportSize({ width, height: 1000 });
    await expect(page.locator('#current-node')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    const up = await page.locator('#live-upload').boundingBox(), down = await page.locator('#live-download').boundingBox();
    expect(Math.abs(up.y - down.y)).toBeLessThan(2);
    const label = await page.locator('.current-node > span').boundingBox(), node = await page.locator('#current-node').boundingBox();
    expect(Math.abs(label.y + label.height - node.y - node.height)).toBeLessThan(3);
    if (width > 640) {
      const current = await page.locator('.current-node').boundingBox(), rates = await page.locator('.live-rate').boundingBox();
      expect(rates.x - current.x - current.width).toBeLessThanOrEqual(16);
      expect(Math.abs(rates.y - current.y)).toBeLessThan(3);
    }
  }
  await page.getByRole('button', { name: '采集详情 →', exact: true }).click();
  await expect(page.locator('#settings-collector-status')).toContainText(description);
  await expect(page.locator('#settings-collector-status')).not.toContainText('联合采样');
});
test('collector copy stays concise through sampling, switching and failures', async ({ page }) => {
  let state = 'baseline', message = 'Xray 策略组联合采样；各独立代理出站分别记账，不叠加组汇总。';
  let partial = false, upload = null, download = null;
  const description = '采集 64 个 Xray 独立代理出站，包含 3 个策略组；不叠加组汇总，排除直连和阻断。内部探测未单独区分。';
  await mock(page, {
    '/api/status': route => route.fulfill(json({ state, message, currentNode: 'Synthetic · strategy group',
      demo: false, lastSample: null, uploadPerSecond: upload, downloadPerSecond: download,
      scope: { basis: 'xray_outbound_members', isPartial: partial, collectedOutbounds: 64, collectedGroups: 3,
        uncollectedOutbounds: partial ? 2 : 0, uncollectedGroups: 0, description } }))
  }, true);
  const summary = 'Xray · 64 个代理出站 · 3 个策略组 · 不含直连和阻断';
  await expect(page.locator('#collector-state')).toHaveText('正在采样');
  await expect(page.locator('#collector-message')).toHaveText(summary);
  await expect(page.locator('#live-upload')).toHaveText('↑ —');
  for (const [width, height, theme, suffix] of [[1440, 1100, 'light', 'desktop'], [390, 844, 'dark', 'mobile'], [320, 844, 'light', 'narrow']]) {
    await page.setViewportSize({ width, height }); await page.selectOption('#theme', theme);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/sampling-copy-${suffix}.png`, fullPage: true, animations: 'disabled' });
  }
  await page.getByRole('button', { name: '采集详情 →', exact: true }).click();
  await expect(page.locator('#settings-collector-status')).toContainText(description);
  await expect(page.locator('#settings-collector-status')).not.toContainText('联合采样');
  await expect(page.locator('#settings-view')).not.toContainText('基线');
  await page.setViewportSize({ width: 1440, height: 1100 });
  await page.screenshot({ path: 'test-results/settings-copy-desktop.png', fullPage: true, animations: 'disabled' });
  await page.click('.nav-item[data-view=overview]');
  state = 'collecting'; upload = '0'; download = '0';
  await page.clock.fastForward(2000);
  await expect(page.locator('#collector-state')).toHaveText('正在采集');
  await expect(page.locator('#collector-message')).toHaveText(summary);
  await expect(page.locator('#live-upload')).toHaveText('↑ 0 B/s');
  partial = true;
  for (const [next, label, text] of [
    ['snapshot_changed', '正在切换配置', '配置已变化，正在等待内核加载。'],
    ['partial', '部分采集', '4 个范围计数未知，未补零。'],
    ['error', '采集暂不可用', '统计接口不可用，请检查 v2rayN 后重试。']
  ]) {
    state = next; message = text; upload = null; download = null;
    await page.clock.fastForward(2000);
    await expect(page.locator('#collector-state')).toHaveText(label);
    await expect(page.locator('#collector-message')).toContainText(text);
    await expect(page.locator('#collector-message')).toContainText(description);
    await expect(page.locator('#live-upload')).toHaveText('↑ —');
    if (next === 'snapshot_changed') await expect(page.locator('#status-dot')).not.toHaveClass(/partial|error/);
    else await expect(page.locator('#status-dot')).toHaveClass(next === 'partial' ? 'status-dot partial' : 'status-dot error');
  }
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'test-results/collector-error-copy-mobile.png', fullPage: true, animations: 'disabled' });
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
  await expect(page.locator('#settings-view')).toContainText('策略组实际出站');
  await expect(page.locator('#settings-view')).toContainText('Xray Observatory 探测');
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
  const localQualityBackground = await page.locator('#records .quality-pill').first().evaluate(el => getComputedStyle(el).backgroundColor);
  await page.selectOption('#statistics-source', 'Synthetic provider');
  await expect(page.locator('#records')).toContainText('Synthetic CSV node');
  await expect(page.locator('#records .quality-pill')).toHaveText('服务商导入');
  expect(await page.locator('#records .quality-pill').evaluate(el => getComputedStyle(el).backgroundColor)).toBe(localQualityBackground);
  await expect(page.locator('#download-title')).toHaveText('下载');
  await expect(page.locator('#download-value')).toHaveText('—');
  await expect(page.locator('#billing-exact')).toHaveText('1,000.800000000123 bytes');
  await page.selectOption('#provider-metric', 'billed');
  await expect(page.locator('#total-exact')).toHaveText('1,300 bytes');
  await page.locator('#records tr').first().locator('td').nth(7).locator('button').click();
  await expect(page.locator('#exact-value-text')).toHaveText('1,000.800000000123 bytes');
  await page.locator('#exact-value-dialog form button').click();
  await page.locator('.records-panel .table-scroll').evaluate(el => { el.scrollLeft = el.scrollWidth; });
  await page.locator('.records-panel').screenshot({ path: 'test-results/provider-quality-desktop.png' });
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
    nodes: [{ key: 'a', name: 'Synthetic shared node', upload: '3000000000', download: '0', display: { originalName: 'Synthetic shared node', groups: ['Synthetic Route'] } },
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
  await expect(page.locator('#summary')).toHaveAttribute('aria-busy', 'false');
  await page.locator('#comparison-nodes .has-groups').hover();
  await expect(page.getByRole('dialog', { name: '策略组', exact: true })).toContainText('Synthetic Route');
  await page.locator('#comparison-nodes .has-groups').press('Escape');
  const nodeCard = page.locator('#comparison-nodes').locator('xpath=ancestor::section[1]');
  const bucketCard = page.locator('#comparison-buckets').locator('xpath=ancestor::section[1]');
  const nodeBox = await nodeCard.boundingBox(), bucketBox = await bucketCard.boundingBox();
  expect(Math.abs(nodeBox.y - bucketBox.y)).toBeLessThanOrEqual(1);
  expect(bucketBox.x).toBeGreaterThanOrEqual(nodeBox.x + nodeBox.width);
  await expect(page.locator('#comparison-local-exact')).toHaveText('3,500,000,000 bytes');
  const localValue = page.locator('#comparison-nodes tr').filter({ hasText: 'Synthetic shared node' }).locator('.paired-value');
  await expect(localValue).toHaveText('3.500 GB');
  await localValue.click();
  await expect(page.locator('#detail-popover')).toContainText('上传3.500 GB');
  await expect(page.locator('#detail-popover .recovery-detail')).toHaveCount(0);
  await page.getByRole('button', { name: '查看精确值', exact: true }).click();
  await expect(page.locator('#exact-value-text')).toContainText('3,500,000,000 bytes');
  await page.locator('#exact-value-dialog form button').click();
  await page.selectOption('#provider-metric', 'billed');
  await expect(page.locator('#comparison-nodes').getByRole('button', { name: /计费量 3,200,000,000.000123 bytes/ })).toHaveAttribute('title', /3,200,000,000.000123 bytes/);
  await expect(localValue).toHaveText('—');
  await page.selectOption('#provider-metric', 'raw');
  const only = page.locator('#comparison-nodes tr').filter({ hasText: 'CSV only' });
  await expect(only).toHaveClass(/missing/); await expect(only).toContainText('0.000 GB');
  await expect(only.locator('button')).toHaveAttribute('aria-label', /没有记录/);
  const missingValue = only.locator('.paired-value'), missingCell = only.locator('.paired-traffic');
  expect(await missingValue.evaluate(el => getComputedStyle(el).backgroundColor)).toBe('rgb(238, 239, 235)');
  expect(await missingCell.evaluate(el => getComputedStyle(el).backgroundColor)).toBe('rgba(0, 0, 0, 0)');
  expect((await missingValue.boundingBox()).width).toBeLessThan((await missingCell.boundingBox()).width / 2);
  await expect(only.locator('.paired-track')).toBeVisible();
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
  const mobileNodeBox = await nodeCard.boundingBox(), mobileBucketBox = await bucketCard.boundingBox();
  expect(mobileBucketBox.y).toBeGreaterThanOrEqual(mobileNodeBox.y + mobileNodeBox.height);
  await expect(page.locator('#comparison-chart .comparison-column')).toHaveCount(31);
  await expect(page.locator('#comparison-chart')).not.toContainText('无法清楚展示');
  await page.screenshot({ path: 'test-results/comparison-mobile.png', fullPage: true });
  for (const theme of ['light', 'dark']) {
    await page.selectOption('#theme', theme);
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: 900 });
      for (const section of ['nodes', 'buckets']) await page.click(`[data-comparison-section="${section}"][data-comparison-mode="list"]`);
      await settledScreenshot(page, `test-results/comparison-list-${theme}-${width}.png`);
      for (const section of ['nodes', 'buckets']) {
        await page.click(`[data-comparison-section="${section}"][data-comparison-mode="share"]`);
        await expect(page.locator(`#comparison-${section}-shares svg`)).toHaveCount(2);
        await expect(page.locator(`#comparison-${section}-shares .rank-track`)).toHaveCount(0);
        await expect(page.locator(`#comparison-${section}-list`)).toBeHidden();
        const circle = page.locator(`#comparison-${section}-shares circle`).first();
        await circle.focus();
        await expect(page.locator(`#comparison-${section}-shares .share-tooltip`).first()).toBeVisible();
        const chart = await page.locator(`#comparison-${section}-shares .share-chart`).first().boundingBox();
        const legend = await page.locator(`#comparison-${section}-shares .share-legend`).first().boundingBox();
        expect(chart.x + chart.width).toBeLessThanOrEqual(legend.x + 1);
      }
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      await settledScreenshot(page, `test-results/comparison-shares-${theme}-${width}.png`);
    }
  }
  for (const section of ['nodes', 'buckets']) {
    await page.click(`[data-comparison-section="${section}"][data-comparison-mode="list"]`);
    await expect(page.locator(`#comparison-${section}-list`)).toBeVisible();
    await expect(page.locator(`#comparison-${section}-shares`)).toBeHidden();
  }
  partialOnly = true;
  if (await page.locator('#filter-disclosure').isVisible()) await page.locator('#filter-disclosure').click();
  await page.selectOption('#bucket', 'hour');
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
test('settings save posts path and timezone, deferring history until statistics is visible', async ({ page }) => {
  const calls = await mock(page); await page.click('.nav-item[data-view=settings]');
  const histories = calls.filter(c => c.path === '/api/history').length;
  await page.fill('#v2rayn-path', 'D:\\Proxy\\v2rayN'); await page.selectOption('#time-zone', 'Asia/Shanghai'); await page.click('#save-settings');
  await expect(page.locator('#settings-result')).toContainText('设置已保存');
  const post = calls.filter(c => c.path === '/api/settings' && c.body).at(-1);
  expect(post.body.v2rayNPath).toBe('D:\\Proxy\\v2rayN'); expect(post.body.timeZoneId).toBe('Asia/Shanghai');
  expect(calls.filter(c => c.path === '/api/history')).toHaveLength(histories);
  await page.click('.nav-item[data-view=overview]');
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


test('polished mobile disclosure and reduced motion remain accessible', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  const longName = '很长的合成节点名称 '.repeat(8) + 'ContinuousSyntheticName'.repeat(8);
  await mock(page, { '/api/nodes': route => route.fulfill(json([{ key: 'a', name: longName }, { key: 'b', name: 'Synthetic B' }])) });
  const disclosure = page.locator('#filter-disclosure');
  await expect(disclosure).toBeVisible();
  await expect(page.locator('#node-filter')).toBeHidden();
  await disclosure.focus(); await page.keyboard.press('Enter');
  await expect(disclosure).toHaveAttribute('aria-expanded', 'true');
  await page.selectOption('#bucket', 'hour');
  await expect(disclosure).toContainText('按小时');
  await page.selectOption('#unit', 'GiB');
  await expect(disclosure).toContainText('GiB');
  await page.selectOption('#node-filter', 'a');
  await expect(disclosure).toContainText('ContinuousSyntheticName');
  await disclosure.click();
  await expect(page.locator('#node-filter')).toBeHidden();
  await expect(page.locator('#demo-notice')).toBeVisible();
  await page.click('#ranking-share');
  expect(await page.locator('#ranking').evaluate(n => getComputedStyle(n).animationName)).toBe('none');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.setViewportSize({ width: 1440, height: 1100 });
  await expect(page.locator('#node-filter')).toBeVisible();
  await expect(disclosure).toBeHidden();
});

test('collector polling preserves filter focus and never replays user motion', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await page.setViewportSize({ width: 390, height: 844 });
  await mock(page, {}, true);
  await page.click('#ranking-share');
  await expect(page.locator('#ranking')).not.toHaveClass(/user-view-change/);
  await page.click('#filter-disclosure');
  await page.locator('#bucket').focus();
  await expect.poll(() => page.evaluate(() => document.getAnimations().length)).toBe(0);
  await page.evaluate(() => {
    window.previewAnimationStarts = 0;
    const animate = Element.prototype.animate;
    Element.prototype.animate = function (...args) { window.previewAnimationStarts++; return animate.apply(this, args); };
  });
  const response = page.waitForResponse(r => new URL(r.url()).pathname === '/api/history', { timeout: 8000 });
  await page.clock.fastForward(30000);
  await response;
  await expect(page.locator('#summary')).toHaveAttribute('aria-busy', 'false');
  await expect(page.locator('#bucket')).toBeFocused();
  await expect(page.locator('#filter-disclosure')).toHaveAttribute('aria-expanded', 'true');
  await expect(page.locator('#ranking')).not.toHaveClass(/user-view-change/);
  expect(await page.evaluate(() => window.previewAnimationStarts)).toBe(0);
});

test('production theme paints html body and shell, including narrow screens', async ({ page }) => {
  await mock(page);
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    for (const [theme, background, panel] of [
      ['dark', 'rgb(23, 27, 27)', 'rgb(32, 37, 37)'],
      ['light', 'rgb(245, 245, 242)', 'rgb(254, 254, 253)'],
      ['dark', 'rgb(23, 27, 27)', 'rgb(32, 37, 37)']
    ]) {
      await page.selectOption('#theme', theme);
      for (const selector of ['html', 'body', '.app-shell']) {
        await expect(page.locator(selector)).toHaveCSS('background-color', background);
      }
      await expect(page.locator('.filter-panel')).toHaveCSS('background-color', panel);
      await expect(page.locator('html')).toHaveCSS('color-scheme', theme);
      await settledScreenshot(page, `test-results/polished-overview-${theme}-${width}.png`);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    }
  }
});

test('v2 motion keeps both surfaces then cancels immediately on navigation', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'no-preference' }); await mock(page);
  const state = await page.evaluate(() => {
    document.querySelector('#ranking').scrollTop=30;
    document.querySelector('#ranking-share').click();
    const old=document.querySelector('.motion-snapshot');
    const result={old:!!old,inert:old?.inert,ids:old?.querySelectorAll('[id]').length,
      oldBars:old?.querySelectorAll('.rank-track').length,ring:document.querySelectorAll('#ranking .share-chart').length,
      animations:document.querySelector('#ranking').getAnimations().length};
    document.querySelector('.nav-item[data-view=settings]').click();
    return {...result,afterNavigate:document.querySelectorAll('.motion-snapshot').length};
  });
  expect(state).toMatchObject({old:true,inert:true,ids:0,ring:1,animations:1,afterNavigate:0});
  expect(state.oldBars).toBeGreaterThan(0);
  await page.click('.nav-item[data-view=overview]');
  await page.evaluate(()=>{for(let i=0;i<6;i++)document.querySelector(`[data-ranking-mode="${i%2?'share':'ranking'}"]`).click();});
  await expect(page.locator('.motion-snapshot')).toHaveCount(0);
  await expect(page.locator('#ranking .share-chart')).toHaveCount(1);
  await page.setViewportSize({width:390,height:844});
  expect(await page.evaluate(()=>{document.querySelector('#filter-disclosure').click();return document.querySelector('#filter-extra').getAnimations().length;})).toBe(1);
  await page.evaluate(()=>{document.querySelector('#filter-disclosure').click();document.querySelector('#filter-disclosure').click();});
  await expect.poll(()=>page.locator('#filter-extra').evaluate(n=>n.getAnimations().length)).toBe(0);
  await expect(page.locator('#filter-disclosure')).toHaveAttribute('aria-expanded','true');
  await expect(page.locator('#bucket')).toBeVisible();
});

test('v2 paired sort moves source pairs together and preserves exact-name order', async ({ page }) => {
  await page.emulateMedia({reducedMotion:'no-preference'});
  const provider={source:'Synthetic motion',rawBytes:'6000',billedBytes:'6000.000123',recordCount:3,series:[],records:[],nodes:[
    {name:'Synthetic · Singapore',rawBytes:'1000',billedBytes:'1000'},
    {name:'Synthetic · Tokyo',rawBytes:'3000',billedBytes:'3000.000123'},
    {name:'Synthetic · Frankfurt',rawBytes:'2000',billedBytes:'2000'}]};
  await mock(page,{'/api/provider/sources':r=>r.fulfill(json([provider.source])),
    '/api/provider/nodes':r=>r.fulfill(json(provider.nodes.map(n=>({key:n.name,name:n.name})))),
    '/api/provider/history':r=>r.fulfill(json(provider))});
  await page.selectOption('#statistics-source',provider.source);await page.check('#compare-toggle');
  await expect(page.locator('#comparison-nodes tr')).toHaveCount(6);
  // Unequal row counts must still place both pagination bars at the card bottom.
  const nodeFooter = await page.locator('#comparison-nodes-list > .pagination').boundingBox();
  const bucketFooter = await page.locator('#comparison-buckets-list > .pagination').boundingBox();
  expect(Math.abs(nodeFooter.y + nodeFooter.height - bucketFooter.y - bucketFooter.height)).toBeLessThanOrEqual(1);
  const sortBox = await page.locator('#comparison-node-sort').boundingBox();
  const switchBox = await page.locator('[aria-label="同名节点对照显示方式"]').boundingBox();
  expect(sortBox.x + sortBox.width).toBeLessThanOrEqual(switchBox.x);
  expect(Math.abs(sortBox.y + sortBox.height / 2 - switchBox.y - switchBox.height / 2)).toBeLessThanOrEqual(2);
  const moving=await page.evaluate(()=>{
    const control=document.querySelector('#comparison-node-sort');control.value='provider';control.dispatchEvent(new Event('change'));
    return [...document.querySelectorAll('#comparison-nodes tr')].map(r=>({key:JSON.parse(r.dataset.motionKey),frames:r.getAnimations()[0]?.effect.getKeyframes()}));
  });
  expect(moving.map(r=>r.key[0])).toEqual(['Synthetic · Tokyo','Synthetic · Tokyo','Synthetic · Frankfurt','Synthetic · Frankfurt','Synthetic · Singapore','Synthetic · Singapore']);
  // Collapsed table borders can put the first source row on a half CSS pixel.
  const offset = row => Number(row.frames[0].transform.match(/translateY\(([-.\d]+)px\)/)[1]);
  for (let i = 0; i < moving.length; i += 2) expect(Math.abs(offset(moving[i]) - offset(moving[i + 1]))).toBeLessThanOrEqual(1);
  await expect.poll(()=>page.locator('#comparison-nodes').evaluate(n=>n.getAnimations({subtree:true}).length)).toBe(0);
});

test('paired node page two survives status polling with global sorting and bar scales', async ({ page }) => {
  const nodes = Array.from({ length: 52 }, (_, i) => ({ key: `n${i}`,
    name: `Synthetic paged ${String(i + 1).padStart(2, '0')}`, upload: String((i + 1) * 100), download: '0' }));
  const providerNodes = [...nodes.map((node, i) => ({ name: node.name, rawBytes: String(100 - i),
    billedBytes: `${100 - i}.000123` })), { name: 'Synthetic provider only', rawBytes: '999', billedBytes: '999.000123' }];
  const local = { ...history, upload: '137800', download: '0', nodes, series: [], records: [], recordCount: 52 };
  const provider = { source: 'Synthetic pagination', rawBytes: '4873', billedBytes: '4873.006519',
    recordCount: 53, nodes: providerNodes, series: [], records: [] };
  let statusReads = 0;
  const calls = await mock(page, {
    '/api/nodes': route => route.fulfill(json(nodes.map(node => ({ key: node.key, name: node.name })))),
    '/api/history': (route, request, url) => route.fulfill(json(url.searchParams.has('nonZeroRecords') ? history : local)),
    '/api/provider/sources': route => route.fulfill(json([provider.source])),
    '/api/provider/nodes': route => route.fulfill(json(providerNodes.map(node => ({ key: node.name, name: node.name })))),
    '/api/provider/history': route => route.fulfill(json(provider)),
    '/api/status': route => route.fulfill(json({ state: 'demo', message: `Synthetic pagination status ${++statusReads}`,
      currentNode: nodes[0].name, lastSample: null, uploadPerSecond: '0', downloadPerSecond: '0', demo: true }))
  });
  await page.selectOption('#statistics-source', provider.source);
  await expect(page.locator('#total-exact')).toHaveText('4,873 bytes');
  await page.check('#compare-toggle');
  const rows = page.locator('#comparison-nodes tr');
  const pairKeys = () => rows.evaluateAll(items => items.map(row => JSON.parse(row.dataset.motionKey)));
  const expectPairs = async names => {
    await expect(rows).toHaveCount(names.length * 2);
    expect(await pairKeys()).toEqual(names.flatMap(name => [[name, 0], [name, 1]]));
    await expect(page.locator('#comparison-nodes .comparison-name')).toHaveText(names);
    expect(await page.locator('#comparison-nodes .comparison-name').evaluateAll(items => items.every(cell => cell.rowSpan === 2))).toBe(true);
  };
  const localOrder = [...nodes].reverse().map(node => node.name).concat('Synthetic provider only');
  await expectPairs(localOrder.slice(0, 50));
  await expect(page.locator('#comparison-node-page')).toContainText('共 53 个节点 · 第 1 / 2 页');
  await expect(page.locator('#comparison-node-previous')).toBeDisabled();
  await page.click('#comparison-node-next');
  await expectPairs(localOrder.slice(50));
  await expect(page.locator('#comparison-node-page')).toContainText('第 2 / 2 页');
  await expect(page.locator('#comparison-node-next')).toBeDisabled();
  // Page two's largest value is 999; the full-query maximum of 5,200 is on page one.
  expect(await rows.first().locator('.paired-bar').evaluate(bar => parseFloat(bar.style.width))).toBeCloseTo(3.84, 2);
  await expect(rows.nth(4)).toHaveClass(/missing/);
  await expect(rows.nth(4).locator('.paired-value')).toHaveAttribute('aria-label', /没有记录/);

  const historyReads = calls.filter(call => ['/api/history', '/api/provider/history'].includes(call.path)).length;
  const response = await page.waitForResponse(result => new URL(result.url()).pathname === '/api/status', { timeout: 8000 });
  const polled = await response.json();
  await expect(page.locator('#collector-message')).toHaveText(polled.message);
  expect(calls.filter(call => ['/api/history', '/api/provider/history'].includes(call.path))).toHaveLength(historyReads);
  await expect(page.locator('#comparison-node-page')).toContainText('第 2 / 2 页');
  await expectPairs(localOrder.slice(50));
  expect(await rows.first().locator('.paired-bar').evaluate(bar => parseFloat(bar.style.width))).toBeCloseTo(3.84, 2);

  // Sorting starts from all 53 nodes, including the provider-only node on the previous page two.
  await page.selectOption('#comparison-node-sort', 'provider');
  const providerOrder = ['Synthetic provider only', ...nodes.map(node => node.name)];
  await expect(page.locator('#comparison-node-page')).toContainText('第 1 / 2 页');
  await expectPairs(providerOrder.slice(0, 50));
  // The full-query maximum still belongs to local node 52, now outside this page.
  expect(await rows.nth(1).locator('.paired-bar').evaluate(bar => parseFloat(bar.style.width))).toBeCloseTo(19.21, 2);
  await page.click('#comparison-node-next');
  await expectPairs(providerOrder.slice(50));
  await expect(page.locator('#comparison-node-page')).toContainText('第 2 / 2 页');
  await expect(page.locator('#comparison-node-next')).toBeDisabled();
});
