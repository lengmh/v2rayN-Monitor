import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { JSDOM } from 'jsdom';
const html = await readFile(new URL('../dist/index.html', import.meta.url), 'utf8');
const asset = (await readdir(new URL('../dist/assets/', import.meta.url))).find(name => name.endsWith('.js'));
const script = await readFile(new URL(`../dist/assets/${asset}`, import.meta.url), 'utf8');
const history = { upload: '1073741824', download: '15032385536', unallocatedUpload: '0', unallocatedDownload: '0', gapCount: 0, series: [{ label: '2026-10-09', upload: '1073741824', download: '15032385536' }], nodes: [{ key: 'a', name: 'Synthetic A', upload: '1073741824', download: '15032385536' }], records: [{ start: '2026-10-09T10:00:00Z', end: '2026-10-09T10:01:00Z', nodeKey: 'a', nodeName: 'Synthetic A', upload: '1073741824', download: '15032385536', quality: 'measured' }], recordCount: 51 };
async function until(condition) { for (let i = 0; i < 200; i++) { if (condition()) return; await new Promise(r => setTimeout(r, 5)); } throw new Error('DOM condition not reached'); }
async function fixture(t, overrides = {}) {
  const dom = new JSDOM(html, { url: 'http://127.0.0.1:4173/#synthetic-once', runScripts: 'outside-only', pretendToBeVisual: true });
  t.after(() => dom.window.close());
  const w = dom.window, calls = [], errors = [];
  w.addEventListener('error', event => errors.push(event.error));
  w.AbortSignal = AbortSignal; w.AbortController = AbortController;
  w.fetch = async (url, options = {}) => {
    const parsed = new URL(url, w.location.href), body = options.body ? JSON.parse(options.body) : undefined;
    calls.push({ path: parsed.pathname, query: parsed.searchParams, body, headers: options.headers, signal: options.signal });
    if (overrides[parsed.pathname]) return overrides[parsed.pathname](parsed, options);
    if (parsed.pathname === '/api/provider/sources') return Response.json([]);
    const routes = { '/api/session': { ok: true }, '/api/settings': { v2rayNPath: 'C:\\Synthetic', timeZoneId: 'UTC', demo: true }, '/api/timezones': [{ id: 'UTC', name: 'UTC' }, { id: 'Asia/Shanghai', name: '中国标准时间' }], '/api/nodes': [{ key: 'a', name: 'Synthetic A' }, { key: 'b', name: 'Synthetic B' }], '/api/history': history, '/api/status': { state: 'demo', message: '合成数据', currentNode: 'Synthetic A', lastSample: null, uploadPerSecond: null, downloadPerSecond: '0', demo: true }, '/api/imports': [], '/api/imports/preview': { hash: 'test', rowCount: 1, rawBytes: '1024', billedBytes: '1024', firstTime: '2026-01-01T00:00:00Z', lastTime: '2026-01-01T00:00:00Z', warnings: [], duplicate: false }, '/api/imports/commit': { ok: true } };
    return Response.json(routes[parsed.pathname] || {});
  };
  w.eval(script);
  await until(() => w.document.getElementById('total-value').textContent === '16.106 GB').catch(error => { throw new Error(`${error.message}; alert=${w.document.getElementById('global-error').textContent}; calls=${calls.map(c=>c.path).join(',')}; errors=${errors.map(e=>String(e)).join(',')}`); });
  return { w, calls, errors, get: id => w.document.getElementById(id), change: (id, value) => { const el = w.document.getElementById(id); el.value = value; el.dispatchEvent(new w.Event('change')); }, submit: id => w.document.getElementById(id).dispatchEvent(new w.Event('submit', { cancelable: true })) };
}
test('production DOM exchanges fragment once, keeps integer bytes, separates missing rate from zero', async t => {
  const { w, calls, get, change, errors } = await fixture(t);
  assert.equal(w.location.hash, ''); assert.equal(calls.filter(c => c.path === '/api/session').length, 1);
  assert.equal(calls.find(c => c.path === '/api/session').headers['x-monitor-request'], '1');
  assert.equal(get('live-upload').textContent, '↑ —'); assert.equal(get('live-download').textContent, '↓ 0 B/s');
  change('unit', 'GiB'); assert.equal(get('total-value').textContent, '15.000 GiB'); assert.equal(get('total-exact').textContent, '16,106,127,360 bytes'); assert.equal(errors.length, 0);
});
test('backend node labels are inserted as text, not HTML', async t => {
  const name = '<img src=x onerror=alert(1)>';
  const { w, get } = await fixture(t, { '/api/history': () => Response.json({ ...history, nodes: [{ ...history.nodes[0], name }], records: [{ ...history.records[0], nodeName: name }] }) });
  assert.ok(get('ranking').textContent.includes(name)); assert.equal(w.document.querySelectorAll('img').length, 0);
});
test('custom dates, bucket, node and pagination retain independent semantics', async t => {
  const { w, get, change, calls, submit } = await fixture(t);
  w.document.querySelector('[data-period=custom]').click(); get('custom-from').value = '2026-10-02'; get('custom-through').value = '2026-10-04'; submit('custom-range'); change('bucket', 'hour'); change('node-filter', 'b'); get('next-page').click();
  await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('page') === '1');
  const q = calls.filter(c => c.path === '/api/history').at(-1).query;
  assert.equal(q.get('start'), '2026-10-02T00:00:00.000Z'); assert.equal(q.get('end'), '2026-10-05T00:00:00.000Z'); assert.equal(q.get('nodeKey'), 'b'); assert.equal(q.get('bucket'), 'hour');
});
test('newer navigation cancels and supersedes earlier API responses', async t => {
  let resolve;
  const { change, get, calls } = await fixture(t, { '/api/history': url => url.searchParams.get('nodeKey') === 'a' ? new Promise(r => { resolve = r; }) : Response.json(history) });
  change('node-filter', 'a'); await until(() => resolve); const older = calls.filter(c => c.path === '/api/history').at(-1);
  change('node-filter', 'b'); assert.equal(older.signal.aborted, true); resolve(Response.json({ ...history, download: '1', upload: '1' }));
  await new Promise(r => setTimeout(r, 10)); assert.equal(get('total-value').textContent, '16.106 GB');
});
test('401 disables writes and asks to reopen tray', async t => {
  let expired = false;
  const { change, get } = await fixture(t, { '/api/history': () => expired ? new Response('{}', { status: 401 }) : Response.json(history) });
  expired = true; change('bucket', 'week'); await until(() => !get('session-alert').hidden);
  assert.equal(get('export-button').disabled, true); assert.equal(get('save-settings').disabled, true); assert.ok(get('session-alert').textContent.includes('托盘'));
});
test('settings save round-trips the data path and timezone', async t => {
  const { get, change, submit, calls } = await fixture(t);
  get('v2rayn-path').value = 'D:\\Synthetic'; change('time-zone', 'Asia/Shanghai'); submit('settings-form'); await until(() => get('settings-result').textContent.includes('设置已保存'));
  const saved = calls.filter(c => c.path === '/api/settings' && c.body).at(-1);
  assert.equal(saved.body.v2rayNPath, 'D:\\Synthetic'); assert.equal(saved.body.timeZoneId, 'Asia/Shanghai'); assert.equal(calls.filter(c => c.path === '/api/history').at(-1).query.get('timeZoneId'), 'Asia/Shanghai');
});
test('provider preview is invalidated on edits and commit remains an explicit separate action', async t => {
  const { w, get, submit, calls } = await fixture(t);
  w.document.querySelector('.nav-item[data-view=provider]').click(); get('provider-source').value = 'Synthetic';
  Object.defineProperty(get('csv-file'), 'files', { value: [{ size: 100, text: async () => 'synthetic csv' }] }); submit('import-form'); await until(() => !get('import-preview').hidden);
  assert.equal(calls.filter(c => c.path === '/api/imports/commit').length, 0);
  get('provider-source').value = 'Changed'; get('provider-source').dispatchEvent(new w.Event('input')); assert.equal(get('import-preview').hidden, true);
  submit('import-form'); await until(() => !get('import-preview').hidden); get('commit-import').click(); await until(() => get('import-result').textContent.includes('导入完成'));
  assert.equal(calls.filter(c => c.path === '/api/imports/commit').length, 1); assert.equal(get('total-value').textContent, '16.106 GB');
});
test('recovery is disclosed as already included while partial overlap stays excluded', async t => {
  const { get } = await fixture(t, { '/api/history': () => Response.json({ ...history, recoveredUpload: '1000000000', recoveredDownload: '2000000000', partialUpload: '100', partialDownload: '0', unbucketedUpload: '1000000000', gapCount: 2, gaps: [{ start: '2026-10-09T10:00:00Z', end: '2026-10-09T11:00:00Z', reason: 'long_interval', isOpen: false }] }) });
  assert.equal(get('total-value').textContent, '16.106 GB');
  assert.ok(get('quality-notice').textContent.includes('已包含恢复量 3.000 GB'));
  assert.ok(get('quality-notice').textContent.includes('尚未分配到当前趋势图'));
  assert.ok(get('quality-notice').textContent.includes('本范围内用量未知，未计入合计'));
  assert.ok(get('gap-list').textContent.includes('已结束（不代表流量全部恢复）'));
});

test('unified statistics switch provider source without mixing local totals or directions', async t => {
  const provider = { source: 'source-one', rawBytes: '1300', billedBytes: '1000.800000000123',
    series: [{ label: '2026-10-09', rawBytes: '1300', billedBytes: '1000.800000000123' }],
    nodes: [{ name: 'CSV node', rawBytes: '1300', billedBytes: '1000.800000000123' }],
    records: [{ at: '2026-10-09T10:00:00Z', nodeName: 'CSV node', rate: '0.8', rawBytes: '1300', billedBytes: '1000.800000000123' }], recordCount: 1 };
  const { get, change, calls } = await fixture(t, {
    '/api/provider/sources': () => Response.json(['source-one']),
    '/api/provider/nodes': () => Response.json([{ key: 'CSV node', name: 'CSV node' }]),
    '/api/provider/history': () => Response.json(provider)
  });
  await until(() => get('statistics-source')?.options.length === 2);
  change('statistics-source', 'source-one');
  await until(() => get('records').textContent.includes('CSV node'));
  assert.equal(get('total-exact').textContent, '1,300 bytes');
  assert.equal(get('upload-exact').textContent, '1,000.800000000123 bytes');
  assert.equal(get('download-title').textContent, '服务商原始量');
  change('provider-metric', 'billed');
  assert.equal(get('total-exact').textContent, '1,000.800000000123 bytes');
  assert.ok(calls.some(c => c.path === '/api/provider/history' && c.query.get('source') === 'source-one'));
  change('statistics-source', '');
  await until(() => get('total-value').textContent === '16.106 GB');
  assert.equal(get('download-title').textContent, '下载');
});

test('import list opens source statistics and preserves the explicit date filter', async t => {
  const batch = { source: 'batch-source', hash: 'synthetic', rowCount: 1, rawBytes: '50', billedBytes: '40.1',
    firstTime: '2026-01-02T10:00:00Z', lastTime: '2026-01-03T11:00:00Z' };
  const { w, get, calls } = await fixture(t, {
    '/api/imports': () => Response.json([batch]),
    '/api/provider/sources': () => Response.json(['batch-source']),
    '/api/provider/nodes': () => Response.json([]),
    '/api/provider/history': () => Response.json({ source: 'batch-source', rawBytes: '50', billedBytes: '40.1', series: [], nodes: [], records: [], recordCount: 0 })
  });
  const original = calls.filter(c => c.path === '/api/history').at(-1).query;
  w.document.querySelector('.nav-item[data-view=provider]').click();
  await until(() => get('import-batches').querySelector('button'));
  get('import-batches').querySelector('button').click();
  await until(() => calls.some(c => c.path === '/api/provider/history'));
  const query = calls.filter(c => c.path === '/api/provider/history').at(-1).query;
  assert.equal(query.get('source'), 'batch-source');
  assert.equal(query.get('start'), original.get('start'));
  assert.equal(query.get('end'), original.get('end'));
  assert.equal(get('overview-view').hidden, false);
});

test('empty provider range is unknown while a recorded zero remains zero', async t => {
  let count = 0;
  const { get, change, calls } = await fixture(t, {
    '/api/provider/sources': () => Response.json(['empty-source']),
    '/api/provider/nodes': () => new Response('{}', { status: 500 }),
    '/api/provider/history': () => Response.json({ source: 'empty-source', rawBytes: '0', billedBytes: '0', series: [], nodes: [], records: [], recordCount: count })
  });
  change('node-filter', 'a');
  await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('nodeKey') === 'a');
  await until(() => get('statistics-source').options.length === 2);
  change('statistics-source', 'empty-source');
  assert.equal(get('node-filter').value, '');
  assert.equal(get('node-filter').options.length, 1);
  await until(() => calls.some(c => c.path === '/api/provider/history'));
  await until(() => get('summary').getAttribute('aria-busy') === 'false');
  assert.equal(get('total-value').textContent, '—');
  assert.equal(get('total-exact').textContent, '所选范围无记录');
  count = 1; change('bucket', 'hour');
  await until(() => get('total-value').textContent === '0.000 GB');
  assert.equal(get('total-exact').textContent, '0 bytes');
});

test('empty local range is unknown while recorded idle traffic stays zero', async t => {
  let empty = false, count = 0;
  const { get, change } = await fixture(t, {
    '/api/history': () => Response.json(empty ? { ...history, upload: '0', download: '0', recordCount: count, records: [], series: [], nodes: [] } : history)
  });
  empty = true; change('bucket', 'hour');
  await until(() => get('total-value').textContent === '—');
  assert.equal(get('total-exact').textContent, '所选范围无记录');
  count = 1; change('bucket', 'day');
  await until(() => get('total-value').textContent === '0.000 GB');
  assert.equal(get('total-exact').textContent, '0 bytes');
});

test('node ranking discloses each node recovery contribution', async t => {
  const { get } = await fixture(t, { '/api/history': () => Response.json({ ...history,
    nodes: [{ ...history.nodes[0], recoveredUpload: '1000000000', recoveredDownload: '0' }] }) });
  assert.ok(get('ranking').textContent.includes('其中恢复量 1.000 GB'));
  assert.equal(get('ranking').querySelector('.rank-recovery').title, '1,000,000,000 bytes');
});

test('same-name comparison keeps sources separate and requests hourly week and month buckets', async t => {
  let localReads = 0;
  const comparisonLocal = { ...history, upload: '35', download: '0', nodes: [
    { key: 'a', name: 'Same', upload: '10', download: '0' }, { key: 'b', name: 'Same', upload: '25', download: '0' }],
    series: [{ label: '2026-10-09T10:00+00:00', upload: '35', download: '0' }] };
  const provider = { source: 'cmp', rawBytes: '40', billedBytes: '32.000000000123', recordCount: 1,
    nodes: [{ name: 'Same', rawBytes: '40', billedBytes: '32.000000000123' }, { name: 'Only CSV', rawBytes: '0', billedBytes: '0' }],
    series: [{ label: '2026-10-09T10:00+00:00', rawBytes: '40', billedBytes: '32.000000000123' }], records: [] };
  const { get, change, w, calls } = await fixture(t, {
    '/api/provider/sources': () => Response.json(['cmp']),
    '/api/provider/nodes': () => Response.json(provider.nodes.map(n => ({ key: n.name, name: n.name }))),
    '/api/provider/history': () => Response.json(provider),
    '/api/history': () => Response.json(localReads++ ? comparisonLocal : history)
  });
  await until(() => get('statistics-source').options.length === 2);
  change('statistics-source', 'cmp'); await until(() => get('records').textContent.includes('这个范围'));
  const toggle = get('compare-toggle'); assert.ok(toggle);
  toggle.checked = true; toggle.dispatchEvent(new w.Event('change'));
  await until(() => get('comparison-nodes').textContent.includes('Only CSV'));
  assert.equal(get('bucket').value, 'hour');
  assert.equal(get('comparison-local-exact').textContent, '35 bytes');
  const rows = get('comparison-nodes').querySelectorAll('tr');
  const same = [...rows].find(r => r.textContent.includes('Same'));
  assert.ok(same.textContent.includes('35 bytes')); assert.ok(same.textContent.includes('32.000000000123 bytes'));
  const only = [...rows].find(r => r.textContent.includes('Only CSV'));
  assert.ok(only.textContent.includes('无记录')); assert.ok(only.textContent.includes('0 bytes'));
  change('node-filter', 'Same');
  await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('nodeName') === 'Same');
  assert.equal(calls.filter(c => c.path === '/api/history').at(-1).query.has('nodeKey'), false);
  change('bucket', 'week'); change('bucket', 'month');
  await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('bucket') === 'month');
  toggle.checked = false; toggle.dispatchEvent(new w.Event('change'));
  await until(() => get('comparison-view').hidden);
});

test('partial-only recovery is unknown in comparison while provider recorded zero remains zero', async t => {
  let reads = 0, knownZero = false;
  const partial = { ...history, upload: '0', download: '0', recordCount: 1, nodes: [], series: [], records: [],
    partialUpload: '25', partialDownload: '0' };
  const provider = { source: 'partial', rawBytes: '0', billedBytes: '0', recordCount: 1,
    nodes: [{ name: 'Synthetic A', rawBytes: '0', billedBytes: '0' }], series: [], records: [] };
  const { get, change, w } = await fixture(t, {
    '/api/provider/sources': () => Response.json(['partial']),
    '/api/provider/nodes': () => Response.json([{ key: 'Synthetic A', name: 'Synthetic A' }]),
    '/api/provider/history': () => Response.json(provider),
    '/api/history': () => Response.json(reads++ ? { ...partial,
      nodes: knownZero ? [{ key: 'a', name: 'Synthetic A', upload: '0', download: '0' }] : [] } : history)
  });
  await until(() => get('statistics-source').options.length === 2);
  change('statistics-source', 'partial'); await until(() => get('summary').getAttribute('aria-busy') === 'false');
  get('compare-toggle').checked = true; get('compare-toggle').dispatchEvent(new w.Event('change'));
  await until(() => get('comparison-recovery').textContent.includes('25 bytes'));
  assert.equal(get('comparison-local').textContent, '—');
  assert.equal(get('comparison-local-exact').textContent, '所选范围无已知量');
  assert.equal(get('comparison-raw-exact').textContent, '0 bytes');
  knownZero = true; change('bucket', 'month');
  await until(() => get('comparison-local-exact').textContent === '0 bytes');
  assert.equal(get('comparison-local').textContent, '0.000 GB');
});

test('comparison bucket table pages long hourly ranges without dropping exact totals', async t => {
  let reads = 0;
  const series = Array.from({ length: 101 }, (_, i) => ({
    label: new Date(Date.UTC(2026, 9, 1, i)).toISOString().slice(0, 16) + '+00:00', upload: '1', download: '0'
  }));
  const local = { ...history, upload: '101', download: '0',
    nodes: [{ key: 'a', name: 'Synthetic A', upload: '101', download: '0' }], recordCount: 101, records: [] };
  const provider = { source: 'paged', rawBytes: '0', billedBytes: '0', recordCount: 0, nodes: [], series: [], records: [] };
  const { get, change, w, calls } = await fixture(t, {
    '/api/provider/sources': () => Response.json(['paged']),
    '/api/provider/nodes': () => Response.json([]),
    '/api/provider/history': () => Response.json(provider),
    '/api/history': url => Response.json(reads++ ? { ...local, series: url.searchParams.get('bucket') === 'month' ?
      [{ label: '2026-10', upload: '101', download: '0' }] : series } : history)
  });
  await until(() => get('statistics-source').options.length === 2);
  change('statistics-source', 'paged'); await until(() => get('summary').getAttribute('aria-busy') === 'false');
  get('compare-toggle').checked = true; get('compare-toggle').dispatchEvent(new w.Event('change'));
  await until(() => get('comparison-local-exact').textContent === '101 bytes');
  assert.equal(get('comparison-buckets').querySelectorAll('tr').length, 50);
  assert.ok(get('comparison-bucket-page').textContent.includes('101'));
  assert.equal(get('comparison-bucket-previous').disabled, true);
  const queries = calls.filter(c => c.path === '/api/history').length;
  get('comparison-bucket-next').click();
  assert.ok(get('comparison-buckets').querySelector('tr').textContent.includes(series[50].label));
  change('unit', 'GiB');
  assert.ok(get('comparison-buckets').querySelector('tr').textContent.includes(series[50].label));
  get('comparison-bucket-next').click();
  assert.equal(get('comparison-buckets').querySelectorAll('tr').length, 1);
  assert.ok(get('comparison-buckets').textContent.includes(series[100].label));
  assert.equal(get('comparison-bucket-next').disabled, true);
  assert.equal(get('comparison-local-exact').textContent, '101 bytes');
  assert.equal(calls.filter(c => c.path === '/api/history').length, queries);
  get('comparison-bucket-previous').click();
  assert.ok(get('comparison-buckets').querySelector('tr').textContent.includes(series[50].label));
  change('bucket', 'month');
  await until(() => get('comparison-buckets').textContent.includes('2026-10') && get('comparison-bucket-next').disabled);
  assert.equal(get('comparison-buckets').querySelectorAll('tr').length, 1);
  assert.equal(get('comparison-bucket-previous').disabled, true);
});

test('provider upload help agrees with the shared backend parser limits', async t => {
  const source = await readFile(new URL('../../src/Core/ProviderImports.cs', import.meta.url), 'utf8');
  const maxRows = Number(source.match(/public const int MaxRows = ([\d_]+);/)[1].replaceAll('_', ''));
  const maxBytes = Number(source.match(/public const int MaxCsvBytes = ([\d_]+);/)[1].replaceAll('_', ''));
  const { get } = await fixture(t);
  assert.ok(get('import-limits').textContent.includes(maxRows.toLocaleString('zh-CN')));
  assert.ok(get('import-limits').textContent.includes(`${maxBytes / 1_000_000} MB`));
  assert.ok(get('import-limits').textContent.includes('不含表头'));
});

test('provider decimal preview, committed list and unit changes retain exact billing without changing local totals', async t => {
  const billedBytes = '2600.800000000123', batch = { source: 'Synthetic Decimal', hash: 'synthetic-decimal', rowCount: 3, rawBytes: '3001', billedBytes, firstTime: '2026-01-01T00:00:00Z', lastTime: '2026-01-01T00:02:00Z', importedAt: '2026-01-02T00:00:00Z' };
  let committed = false;
  const { w, get, submit, change, calls, errors } = await fixture(t, {
    '/api/imports/preview': () => Response.json({ ...batch, warnings: [], duplicate: committed }),
    '/api/imports/commit': () => { committed = true; return Response.json(batch); },
    '/api/imports': () => Response.json(committed ? [batch] : [])
  });
  w.document.querySelector('.nav-item[data-view=provider]').click(); get('provider-source').value = 'Synthetic Decimal';
  Object.defineProperty(get('csv-file'), 'files', { value: [{ size: 100, text: async () => 'synthetic decimal csv' }] });
  submit('import-form'); await until(() => !get('import-preview').hidden);
  assert.ok(get('preview-details').textContent.includes('2,600.800000000123 bytes'));
  assert.ok(get('preview-details').textContent.includes('3,001 bytes'));
  get('commit-import').click(); await until(() => get('import-batches').textContent.includes('2,600.800000000123 bytes'));
  assert.equal(get('import-preview').hidden, true);
  assert.ok(get('import-batches').textContent.includes('< 0.001 GB'));
  change('unit', 'GiB');
  assert.ok(get('import-batches').textContent.includes('< 0.001 GiB'));
  assert.ok(get('import-batches').textContent.includes('2,600.800000000123 bytes'));
  assert.equal(get('total-exact').textContent, '16,106,127,360 bytes');
  submit('import-form'); await until(() => !get('import-preview').hidden);
  assert.equal(get('commit-import').disabled, true);
  assert.ok(get('preview-details').textContent.includes('2,600.800000000123 bytes'));
  assert.equal(calls.filter(call => call.path === '/api/imports/commit').length, 1);
  assert.equal(errors.length, 0);
});

test('provider list preserves large exact decimal values and source names remain plain text', async t => {
  const source = '<img src=x onerror=alert(1)>', billedBytes = '9007199254740993.123456789012';
  const { w, get, errors } = await fixture(t, { '/api/imports': () => Response.json([{ source, rowCount: 1, rawBytes: '1', billedBytes, importedAt: '2026-01-02T00:00:00Z' }]) });
  w.document.querySelector('.nav-item[data-view=provider]').click();
  await until(() => get('import-batches').textContent.includes('9,007,199,254,740,993.123456789012 bytes'));
  assert.ok(get('import-batches').textContent.includes(source));
  assert.equal(w.document.querySelectorAll('img').length, 0);
  assert.equal(errors.length, 0);
});

test('local attribution note states active configuration identity without exposing identifiers', async t => {
  const { get } = await fixture(t);
  assert.ok(get('attribution-note').textContent.includes('本机统计按 v2rayN 活动配置归属，连接参数相同的不同 IndexId 分账；名称仅用于展示。'));
});
