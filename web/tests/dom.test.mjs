import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { JSDOM } from 'jsdom';
const html = await readFile(new URL('../dist/index.html', import.meta.url), 'utf8');
const asset = (await readdir(new URL('../dist/assets/', import.meta.url))).find(name => name.endsWith('.js'));
const script = await readFile(new URL(`../dist/assets/${asset}`, import.meta.url), 'utf8');
const history = { upload: '1073741824', download: '15032385536', unallocatedUpload: '0', unallocatedDownload: '0', gapCount: 0, series: [{ label: '2026-10-09', upload: '1073741824', download: '15032385536' }], nodes: [{ key: 'a', name: 'Synthetic A', upload: '1073741824', download: '15032385536' }], records: [{ start: '2026-10-09T10:00:00Z', end: '2026-10-09T10:01:00Z', nodeKey: 'a', nodeName: 'Synthetic A', upload: '1073741824', download: '15032385536', quality: 'measured' }], recordCount: 51 };
async function until(condition, timeout = 1000) { for (let i = 0; i < timeout / 5; i++) { if (condition()) return; await new Promise(r => setTimeout(r, 5)); } throw new Error('DOM condition not reached'); }
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
    const routes = { '/api/session': { ok: true }, '/api/settings': { v2rayNPath: 'C:\\Synthetic', timeZoneId: 'UTC', demo: true }, '/api/timezones': [{ id: 'UTC', name: 'UTC' }, { id: 'Asia/Shanghai', name: '中国标准时间' }], '/api/nodes': [{ key: 'a', name: 'Synthetic A' }, { key: 'b', name: 'Synthetic B' }], '/api/history': history, '/api/status': { state: 'demo', message: '合成数据', currentNode: 'Synthetic A', lastSample: null, uploadPerSecond: null, downloadPerSecond: '0', demo: true }, '/api/imports': [], '/api/imports/preview': { hash: 'test', rowCount: 1, rawBytes: '1024', billedBytes: '1024', newRowCount: 1, skippedRowCount: 0, newRawBytes: '1024', newBilledBytes: '1024', firstTime: '2026-01-01T00:00:00Z', lastTime: '2026-01-01T00:00:00Z', warnings: [], duplicate: false }, '/api/imports/commit': { newRowCount: 1, skippedRowCount: 0, newRawBytes: '1024', newBilledBytes: '1024', duplicate: false } };
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
test('partial route coverage stays explicit in live status and historical records', async t => {
  const description = '仅采集路由直接匹配 proxy 的字节。其他 37 个出站（含 4 个策略组）未采集，其流量未知；当前合计不是全部代理流量。';
  const { get } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'collecting', message: '正在采集 sing-box proxy 原始字节。', currentNode: 'Synthetic A', lastSample: null, uploadPerSecond: null, downloadPerSecond: null, demo: false,
      scope: { basis: 'routed_proxy', isPartial: true, uncollectedOutbounds: 37, uncollectedGroups: 4, description } }),
    '/api/history': () => Response.json({ ...history, gapCount: 1,
      records: history.records.map(record => ({ ...record, partialCoverage: true })),
      gaps: [{ start: '2026-10-09T10:00:00Z', end: '2026-10-09T11:00:00Z', reason: 'uncollected_outbounds', isOpen: false }] })
  });
  assert.equal(get('collector-state').textContent, '部分采集');
  assert.ok(get('collector-message').textContent.includes(description));
  assert.ok(get('settings-collector-status').textContent.includes(description));
  assert.equal(get('status-dot').classList.contains('connected'), false);
  assert.ok(get('quality-notice').textContent.includes('未覆盖范围的流量未知'));
  assert.ok(get('gap-list').textContent.includes('其他出站未采集，流量未知'));
  assert.ok(get('records').textContent.includes('部分出站'));
  assert.equal(get('total-value').textContent, '16.106 GB');
});
for (const target of ['#ranking .rank-value', '#records .has-groups']) test(`disabling probe history on a collector tick closes ${target} details`, async t => {
  let historyEnabled = true;
  const display = { originalName: 'Synthetic probe', groups: ['Synthetic probe group'], mergeKey: null };
  const { w, get, calls, errors } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'collecting', demo: false, uploadPerSecond: null, downloadPerSecond: null,
      probes: { historyEnabled, state: historyEnabled ? 'available' : 'disabled' } }),
    '/api/history': () => Response.json({ ...history,
      nodes: history.nodes.map(node => ({ ...node, display })),
      records: history.records.map(record => ({ ...record, display })) })
  });
  w.document.querySelector('.nav-item[data-view=probes]').click();
  await until(() => calls.some(call => call.path === '/api/history' && call.query.has('basis')) &&
    get('summary').getAttribute('aria-busy') === 'false');
  const anchor = w.document.querySelector(target);
  anchor.click();
  assert.equal(get('detail-popover').hidden, false);
  assert.equal(anchor.getAttribute('aria-expanded'), 'true');
  const historyCalls = calls.filter(call => call.path === '/api/history').length;

  historyEnabled = false;
  await until(() => get('refresh-note').textContent === '探测未启用 · 5 秒检查', 8000);
  assert.equal(anchor.isConnected, false);
  assert.equal(get('detail-popover').hidden, true);
  assert.equal(anchor.getAttribute('aria-expanded'), 'false');
  assert.equal(w.document.querySelectorAll('[aria-controls="detail-popover"][aria-expanded="true"]').length, 0);
  assert.equal(get('total-value').textContent, '—');
  assert.equal(get('export-button').disabled, true);
  assert.equal(calls.filter(call => call.path === '/api/history').length, historyCalls);
  assert.deepEqual(errors, []);
});

test('Xray partial scope keeps outbound semantics and probe limitation visible', async t => {
  const description = '仅采集 Xray proxy 出站自身的累计字节，包含策略组选中该出站的连接；其他出站未采集，内部探测未单独区分。';
  const { get } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'collecting', message: 'Xray proxy', currentNode: 'Synthetic Xray', lastSample: null,
      uploadPerSecond: null, downloadPerSecond: null, demo: false, collectionBasis: 'legacy_proxy',
      scope: { basis: 'xray_outbound_proxy', isPartial: true, uncollectedOutbounds: 2, uncollectedGroups: 1, description },
      probes: { historyEnabled: true, state: 'unsupported', message: '当前 Xray 路径未接入内部探测计量。' } })
  });
  assert.equal(get('collector-state').textContent, '部分采集');
  assert.ok(get('collector-message').textContent.includes(description));
  assert.ok(get('settings-collector-status').textContent.includes(description));
  assert.equal(get('probe-state').textContent, '当前核心未支持探测计量');
});
test('historical scope warning survives pagination and the capped gap details', async t => {
  const { get } = await fixture(t, {
    '/api/history': () => Response.json({ ...history, partialCoverage: true, gapCount: 201,
      gaps: [{ start: '2026-10-09T10:00:00Z', end: '2026-10-09T11:00:00Z', reason: 'identity_change', isOpen: false }] })
  });
  assert.ok(get('quality-notice').textContent.includes('未覆盖范围的流量未知'));
  assert.equal(get('total-value').textContent, '16.106 GB');
});
test('opt-in member basis stays explicit without adding aggregate dimensions', async t => {
  const { get } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'partial', message: '实际成员维度，缺失不补零。', currentNode: 'Synthetic member', lastSample: null, uploadPerSecond: null, downloadPerSecond: null, demo: false, collectionBasis: 'actual_member' }),
    '/api/history': () => Response.json({ ...history, basis: 'actual_member', gapCount: 1,
      gaps: [{ start: '2026-10-09T10:00:00Z', end: '2026-10-09T10:00:00Z', reason: 'unsupported_mode', isOpen: true, origin: 'legacy' }] })
  });
  assert.equal(get('collector-state').textContent, '部分采集');
  assert.ok(get('status-dot').classList.contains('partial'));
  assert.ok(get('quality-notice').textContent.includes('实际成员维度'));
  assert.ok(get('quality-notice').textContent.includes('直连和汇总分别保存'));
  assert.ok(get('quality-notice').textContent.includes('历史遗留开放缺口'));
  assert.ok(get('quality-notice').textContent.includes('接入前累计不回填'));
  assert.ok(get('attribution-note').textContent.includes('按已核实的节点或出站身份分账'));
  assert.ok(!get('attribution-note').textContent.includes('test.2'));
  assert.equal(get('total-value').textContent, '16.106 GB');
});
test('probe page keeps basis, filters, export and business navigation separate', async t => {
  const { w, get, change, calls } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'collecting', message: 'Synthetic collection', demo: false,
      currentNode: 'Synthetic member', lastSample: null, uploadPerSecond: null, downloadPerSecond: null,
      probes: { historyEnabled: true, state: 'available', message: 'Synthetic probes available' } }),
    '/api/history': url => {
      const basis = url.searchParams.get('basis');
      return Response.json(basis ? { ...history, basis, upload: '100', download: '23',
        earliestRecord: '2026-10-01T10:00:00Z',
        nodes: [{ key: 'scope:99', name: 'Synthetic probe', upload: '100', download: '23' }],
        records: [{ ...history.records[0], nodeKey: 'scope:99', nodeName: 'Synthetic probe', upload: '100', download: '23' }], recordCount: 1 } : history);
    },
    '/api/nodes': url => Response.json(url.searchParams.has('basis') ? [{ key: 'scope:99', name: 'Synthetic probe' }] : [{ key: 'a', name: 'Synthetic A' }])
  });
  w.document.querySelector('.nav-item[data-view=probes]').click();
  await until(() => get('total-exact').textContent === '123 bytes');
  assert.equal(get('statistics-title').textContent, '探测流量');
  assert.equal(get('source-controls').hidden, true);
  assert.equal(get('probe-controls').hidden, false);
  assert.ok(get('probe-coverage').textContent.includes('2026-10-01'));
  assert.ok(get('probe-coverage').textContent.includes('不是精确的首次基线'));
  assert.ok(get('probe-notice').textContent.includes('不能直接换算服务商计费'));
  change('node-filter', 'scope:99');
  await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('nodeKey') === 'scope:99');
  for (const basis of ['probe_urltest_business', 'probe_urltest_excluded', 'probe_urltest_unknown']) {
    change('probe-basis', basis);
    await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('basis') === basis);
    assert.equal(calls.filter(c => c.path === '/api/history').at(-1).query.has('nodeKey'), false);
    await until(() => get('total-exact').textContent === '123 bytes');
  }
  w.URL.createObjectURL = () => 'blob:synthetic-probes';
  w.URL.revokeObjectURL = () => {};
  w.HTMLAnchorElement.prototype.click = () => {};
  get('export-button').click();
  await until(() => calls.some(c => c.path === '/api/export'));
  const exported = calls.find(c => c.path === '/api/export').query;
  assert.equal(exported.get('basis'), 'probe_urltest_unknown');
  assert.equal(exported.has('nonZeroRecords'), false);
  w.document.querySelector('.nav-item[data-view=overview]').click();
  await until(() => get('total-value').textContent === '16.106 GB');
  assert.equal(calls.filter(c => c.path === '/api/history').at(-1).query.has('basis'), false);
  assert.equal(get('probe-notice').hidden, true);
});
test('disabled probe history remains unknown without querying or exporting business as probes', async t => {
  const { w, get, calls } = await fixture(t);
  w.document.querySelector('.nav-item[data-view=probes]').click();
  await until(() => get('total-exact').textContent === '探测量未知');
  assert.equal(get('total-value').textContent, '—');
  assert.equal(get('export-button').disabled, true);
  assert.equal(get('probe-state').textContent, '探测历史未启用');
  assert.equal(calls.some(c => c.path === '/api/history' && c.query.has('basis')), false);
  w.document.querySelector('.nav-item[data-view=overview]').click();
  await until(() => get('total-value').textContent === '16.106 GB');
  assert.equal(get('export-button').disabled, false);
});
test('backend node labels are inserted as text, not HTML', async t => {
  const name = '<img src=x onerror=alert(1)>';
  const { w, get } = await fixture(t, { '/api/history': () => Response.json({ ...history, nodes: [{ ...history.nodes[0], name }], records: [{ ...history.records[0], nodeName: name }] }) });
  assert.ok(get('ranking').textContent.includes(name)); assert.deepEqual([...w.document.querySelectorAll('img')], [w.document.querySelector('.brand > img.brand-symbol')]);
});
test('local details request nonzero rows while export retains its raw query', async t => {
  const { w, calls, get } = await fixture(t, {
    '/api/history': parsed => Response.json({ ...history, recordCount: 1,
      records: parsed.searchParams.get('nonZeroRecords') === 'true' ? history.records : [
        { ...history.records[0], upload: '0', download: '0' }, ...history.records] })
  });
  assert.equal(calls.find(c => c.path === '/api/history').query.get('nonZeroRecords'), 'true');
  assert.equal(get('records').rows.length, 1);
  assert.equal(get('record-count').textContent, '1 条非零记录');
  assert.equal(get('next-page').disabled, true);
  w.URL.createObjectURL = () => 'blob:synthetic-export';
  w.URL.revokeObjectURL = () => {};
  w.HTMLAnchorElement.prototype.click = () => {};
  get('export-button').click();
  await until(() => calls.some(c => c.path === '/api/export'));
  assert.equal(calls.find(c => c.path === '/api/export').query.has('nonZeroRecords'), false);
});
test('zero-only local details explain that records are hidden without hiding quality warnings', async t => {
  const { get } = await fixture(t, {
    '/api/history': () => Response.json({ ...history, recordCount: 0, records: [], gapCount: 1,
      gaps: [{ start: '2026-10-09T10:00:00Z', end: '2026-10-09T10:01:00Z', reason: 'snapshot_changed', isOpen: false }] })
  });
  assert.ok(get('records').textContent.includes('零字节记录已隐藏'));
  assert.ok(get('gap-list').textContent.includes('快照'));
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
test('overlap preview separates new and skipped rows and commit reports its recalculated result', async t => {
  const preview = { hash: 'synthetic-overlap', rowCount: 3, rawBytes: '240', billedBytes: '192.000000000123',
    newRowCount: 1, skippedRowCount: 2, newRawBytes: '40', newBilledBytes: '32.000000000123',
    firstTime: '2026-01-01T00:00:00Z', lastTime: '2026-01-02T00:00:00Z', duplicate: false, warnings: [] };
  const { w, get, submit } = await fixture(t, {
    '/api/imports/preview': () => Response.json(preview),
    '/api/imports/commit': () => Response.json({ ...preview, newRowCount: 0, skippedRowCount: 3,
      newRawBytes: '0', newBilledBytes: '0', duplicate: true })
  });
  w.document.querySelector('.nav-item[data-view=provider]').click(); get('provider-source').value = 'Synthetic';
  Object.defineProperty(get('csv-file'), 'files', { value: [{ size: 100, text: async () => 'synthetic overlap csv' }] });
  submit('import-form'); await until(() => !get('import-preview').hidden);
  assert.ok(get('preview-details').textContent.includes('本次新增：1 条'));
  assert.ok(get('preview-details').textContent.includes('跳过已存在：2 条'));
  assert.ok(get('preview-details').textContent.includes('32.000000000123 bytes'));
  assert.equal(get('commit-import').disabled, false);
  get('commit-import').click(); await until(() => get('import-result').textContent.includes('新增 0 条'));
  assert.ok(get('import-result').textContent.includes('跳过 3 条'));
  assert.ok(get('import-result').textContent.includes('全部记录已存在'));
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
  assert.equal(get('billing-exact').textContent, '1,000.800000000123 bytes');
  assert.equal(get('download-title').textContent, '下载');
  assert.equal(get('download-value').textContent, '—');
  assert.equal(get('upload-value').textContent, '—');
  assert.equal(get('records').closest('.records-panel').classList.contains('local-records'), false);
  change('provider-metric', 'billed');
  assert.equal(get('total-exact').textContent, '1,300 bytes');
  assert.equal(get('billing-exact').textContent, '1,000.800000000123 bytes');
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
    '/api/history': () => Response.json(empty ? { ...history, upload: '0', download: '0', recordCount: count, records: [], series: [], nodes: count ? [{ ...history.nodes[0], upload: '0', download: '0' }] : [] } : history)
  });
  empty = true; change('bucket', 'hour');
  await until(() => get('total-value').textContent === '—');
  assert.equal(get('total-exact').textContent, '所选范围无记录');
  count = 1; change('bucket', 'day');
  await until(() => get('total-value').textContent === '0.000 GB');
  assert.equal(get('total-exact').textContent, '0 bytes');
});

test('node ranking discloses each node recovery contribution', async t => {
  const { w, get, errors } = await fixture(t, { '/api/history': () => Response.json({ ...history,
    nodes: [{ ...history.nodes[0], recoveredUpload: '1000000000', recoveredDownload: '0' }] }) });
  const amount = get('ranking').querySelector('.rank-value');
  assert.equal(amount.textContent, '16.106 GB');
  assert.ok(!get('ranking').textContent.includes('恢复'));
  assert.ok(!get('ranking').textContent.includes('↑'));
  assert.ok(!get('ranking').querySelector('.rank-recovery-marker').classList.contains('vacant'));
  assert.match(amount.getAttribute('aria-label'), /含恢复量/);
  amount.click();
  assert.equal(get('detail-popover').hidden, false);
  assert.ok(get('detail-popover').textContent.includes('其中恢复量1.000 GB'));
  assert.ok(get('detail-popover').textContent.includes('已包含在上传、下载及合计中'));
  // jsdom does not implement the native modal API; the real browser suite covers it.
  get('exact-value-dialog').showModal = function () { this.open = true; };
  get('detail-popover').querySelector('button').click();
  assert.equal(get('exact-value-dialog').open, true);
  assert.ok(get('exact-value-text').textContent.includes('1,000,000,000 bytes'));
  assert.equal(get('detail-popover').hidden, true);
  get('exact-value-dialog').open = false;
  amount.focus();
  w.document.dispatchEvent(new w.KeyboardEvent('keydown', { key: 'Escape' }));
  assert.equal(get('detail-popover').hidden, true);
  assert.equal(amount.getAttribute('aria-expanded'), 'false');
  assert.deepEqual(errors, []);
});

test('ranking without recovery keeps the same value target and shows directions on focus', async t => {
  const { get } = await fixture(t);
  const amount = get('ranking').querySelector('.rank-value');
  assert.ok(get('ranking').querySelector('.rank-recovery-marker').classList.contains('vacant'));
  amount.focus();
  const popup = get('detail-popover');
  assert.equal(popup.hidden, false);
  assert.ok(popup.textContent.includes('上传1.074 GB'));
  assert.ok(popup.textContent.includes('下载15.032 GB'));
  assert.ok(!popup.querySelector('.recovery-detail'));
});

for (const empty of [false, true]) test(`share group details close when polling returns ${empty ? 'no nodes' : 'only zero totals'}`, async t => {
  let result = { ...history, nodes: history.nodes.map(node => ({ ...node,
    display: { originalName: node.name, groups: ['Synthetic initial group'], mergeKey: null } })) };
  const { w, get, errors } = await fixture(t, { '/api/history': () => Response.json(result) });
  get('ranking-share').click();
  const firstAnchor = get('ranking').querySelector('.has-groups');
  firstAnchor.focus();
  const popup = get('detail-popover');
  assert.equal(popup.hidden, false);
  assert.equal(popup.querySelector('li').textContent, 'Synthetic initial group');

  // A normal collector tick must reconnect the same item and refresh its details.
  result = { ...result, nodes: result.nodes.map(node => ({ ...node,
    display: { ...node.display, groups: ['Synthetic updated group'] } })) };
  await until(() => popup.querySelector('li')?.textContent === 'Synthetic updated group', 8000);
  const previousAnchor = get('ranking').querySelector('.has-groups');
  assert.equal(firstAnchor.isConnected, false);
  assert.equal(popup.hidden, false);
  assert.equal(previousAnchor.getAttribute('aria-expanded'), 'true');
  await until(() => w.document.activeElement === previousAnchor);

  result = { ...result, upload: '0', download: '0', series: [], records: [], recordCount: 0,
    nodes: empty ? [] : result.nodes.map(node => ({ ...node, upload: '0', download: '0' })) };
  await until(() => !!get('ranking').querySelector('.empty-state'), 8000);
  assert.equal(get('ranking').textContent, empty ? '所选范围尚无节点记录' : '所选范围只有零值记录');
  assert.equal(popup.hidden, true);
  assert.equal(previousAnchor.isConnected, false);
  assert.equal(previousAnchor.getAttribute('aria-expanded'), 'false');
  assert.equal(w.document.querySelectorAll('[aria-controls="detail-popover"][aria-expanded="true"]').length, 0);
  assert.equal(get('global-error').hidden, true);
  assert.deepEqual(errors, []);
});

test('appearance changes stay local and do not alter the data source', async t => {
  const { w, get, change, calls } = await fixture(t);
  const before = calls.length;
  change('theme', 'dark');
  assert.equal(w.document.documentElement.dataset.theme, 'dark');
  assert.equal(w.localStorage.getItem('v2rayn-monitor-theme'), 'dark');
  change('theme', 'light');
  assert.equal(w.document.documentElement.dataset.theme, 'light');
  assert.equal(get('total-value').textContent, '16.106 GB');
  assert.equal(calls.length, before);
});

test('node ranking switches between ordered totals and exact share mode', async t => {
  const { w, get } = await fixture(t);
  assert.equal(get('ranking-mode').querySelector('[data-ranking-mode="ranking"]').getAttribute('aria-pressed'), 'true');
  assert.equal(w.document.querySelector('#ranking .rank-index').textContent, '01');
  w.document.querySelector('[data-ranking-mode="share"]').click();
  assert.equal(get('ranking-mode').querySelector('[data-ranking-mode="share"]').getAttribute('aria-pressed'), 'true');
  assert.match(w.document.querySelector('#ranking .rank-index').textContent, /%$/);
  assert.equal(get('ranking').querySelectorAll('.rank-value, .rank-track, .share-total').length, 0);
  assert.equal(get('ranking').querySelectorAll('.share-chart svg circle').length, 1);
  const segment = get('ranking').querySelector('.share-chart circle');
  segment.dispatchEvent(new w.Event('pointerenter'));
  assert.equal(get('ranking').querySelector('.share-tooltip').hidden, false);
  assert.equal(get('ranking').querySelector('.share-tooltip').textContent, '16.106 GB');
  segment.dispatchEvent(new w.Event('pointerleave'));
  assert.equal(get('ranking').querySelector('.share-tooltip').hidden, true);
});

test('share denominator includes every identity and preserves small nonzero and recorded zero', async t => {
  const { w, get } = await fixture(t, { '/api/history': () => Response.json({ ...history, nodes: [
    { key: 'a', name: 'Same synthetic name', upload: '9007199254740993', download: '0' },
    { key: 'b', name: 'Same synthetic name', upload: '0', download: '9007199254740993' },
    { key: 'c', name: 'Tiny', upload: '1', download: '0' },
    { key: 'd', name: 'Idle', upload: '0', download: '0' }
  ] }) });
  w.document.querySelector('[data-ranking-mode="share"]').click();
  assert.equal(get('ranking').querySelectorAll('.rank-row').length, 4);
  const shares = [...get('ranking').querySelectorAll('.rank-index')].map(n => n.textContent);
  assert.deepEqual(shares, ['49.99%', '49.99%', '<0.01%', '0.00%']);
  assert.ok(get('ranking').textContent.includes('Idle'));
  assert.equal(get('ranking').querySelectorAll('.share-swatch').length, 4);
  assert.equal(get('ranking').querySelectorAll('.rank-bar, .rank-value').length, 0);
});

test('period defaults follow day week month year while manual granularity survives other controls', async t => {
  const provider = { source: 'calendar', rawBytes: '1', billedBytes: '1', recordCount: 1, nodes: [], series: [], records: [] };
  const { get, change, w, calls } = await fixture(t, {
    '/api/provider/sources': () => Response.json(['calendar']),
    '/api/provider/nodes': () => Response.json([]),
    '/api/provider/history': () => Response.json(provider)
  });
  await until(() => get('statistics-source').options.length === 2);
  for (const mode of ['local', 'provider', 'comparison']) {
    if (mode === 'provider') { change('statistics-source', 'calendar'); await until(() => get('summary').getAttribute('aria-busy') === 'false'); }
    if (mode === 'comparison') { get('compare-toggle').checked = true; get('compare-toggle').dispatchEvent(new w.Event('change')); }
    for (const [period, expected] of [['day', 'hour'], ['week', 'day'], ['month', 'day'], ['year', 'month']]) {
      w.document.querySelector(`[data-period=${period}]`).click();
      assert.equal(get('bucket').value, expected, `${mode} ${period}`);
      const path = mode === 'comparison' || mode === 'local' ? '/api/history' : '/api/provider/history';
      await until(() => calls.filter(c => c.path === path).at(-1).query.get('bucket') === expected);
    }
  }
  change('bucket', 'week'); get('previous-period').click(); change('unit', 'GiB');
  assert.equal(get('bucket').value, 'week');
  get('compare-toggle').checked = false; get('compare-toggle').dispatchEvent(new w.Event('change'));
  change('statistics-source', '');
  assert.equal(get('bucket').value, 'week');
  w.document.querySelector('[data-period=custom]').click();
  assert.equal(get('bucket').value, 'week');
});

test('same-name comparison keeps sources separate and requests hourly week and month buckets', async t => {
  let localReads = 0;
  const comparisonLocal = { ...history, upload: '35', download: '0', nodes: [
    { key: 'a', name: 'Same', upload: '10', download: '0', display: { originalName: 'Same', groups: ['Synthetic Group'] } }, { key: 'b', name: 'Same', upload: '25', download: '0' }],
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
  assert.equal(get('bucket').value, 'day');
  assert.equal(get('comparison-local-exact').textContent, '35 bytes');
  const rows = get('comparison-nodes').querySelectorAll('tr');
  const same = [...rows].find(r => r.textContent.includes('Same'));
  assert.ok(same.querySelector('.paired-value').title.includes('35 bytes'));
  same.querySelector('.has-groups').focus();
  assert.equal(get('detail-popover').querySelector('li').textContent, 'Synthetic Group');
  assert.ok(get('detail-popover').textContent.includes('来自本机记录'));
  assert.equal(same.querySelector('.paired-value').textContent, '< 0.001 GB');
  same.querySelector('.paired-value').focus();
  assert.ok(get('detail-popover').textContent.includes('上传< 0.001 GB'));
  assert.ok(!get('detail-popover').querySelector('.recovery-detail'));
  assert.ok(!get('comparison-nodes').textContent.includes('↑'));
  assert.ok(!get('comparison-buckets').textContent.includes('↑'));
  change('provider-metric', 'billed');
  assert.ok([...get('comparison-nodes').querySelectorAll('.paired-value')].some(value => value.title.includes('32.000000000123 bytes')));
  const only = [...rows].find(r => r.textContent.includes('Only CSV'));
  assert.ok(only.classList.contains('missing')); assert.ok(only.textContent.includes('0.000 GB'));
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
  w.document.querySelector('[data-comparison-section="nodes"][data-comparison-mode="share"]').click();
  assert.match(get('comparison-nodes-shares').querySelector('[data-source="local"]').textContent, /无记录，不能视为零流量/);
  assert.match(get('comparison-nodes-shares').querySelector('[data-source="provider"]').textContent, /只有零值记录/);
  assert.equal(get('comparison-nodes-shares').querySelectorAll('circle').length, 0);
  knownZero = true; change('bucket', 'month');
  await until(() => get('comparison-local-exact').textContent === '0 bytes');
  assert.equal(get('comparison-local').textContent, '0.000 GB');
  assert.match(get('comparison-nodes-shares').querySelector('[data-source="local"]').textContent, /只有零值记录/);
});

test('comparison switches both sources between raw and billing while unknown multipliers stay unknown', async t => {
  let localReads = 0;
  const captured = { originalName: 'Same', groups: [], mergeKey: null, rate: null, rateStatus: 'partial',
    estimatedBilledBytes: '1000000000.5', estimateCoveredBytes: '2000000001', estimateUnknownBytes: '999999999' };
  const local = { ...history, upload: '1000000000', download: '3000000000', display: {
    ...captured, estimateUnknownBytes: '1999999999'
  }, nodes: [
    { key: 'same', name: 'Same', upload: '1000000000', download: '2000000000', display: captured },
    { key: 'unknown', name: 'Unknown', upload: '0', download: '1000000000' }
  ], series: [{ label: '2026-10-09', upload: '1000000000', download: '3000000000', display: { ...captured, estimateUnknownBytes: '1999999999' } }] };
  const provider = { source: 'billing', rawBytes: '5000000000', billedBytes: '2500000000.000000000123', recordCount: 1,
    nodes: [{ name: 'Same', rawBytes: '5000000000', billedBytes: '2500000000.000000000123' }],
    series: [{ label: '2026-10-09', rawBytes: '5000000000', billedBytes: '2500000000.000000000123' }], records: [] };
  const { get, change, w, errors } = await fixture(t, {
    '/api/provider/sources': () => Response.json(['billing']),
    '/api/provider/nodes': () => Response.json(provider.nodes.map(n => ({ key: n.name, name: n.name }))),
    '/api/provider/history': () => Response.json(provider),
    '/api/history': () => Response.json(localReads++ ? local : history)
  });
  change('statistics-source', 'billing');
  await until(() => get('compare-control').hidden === false);
  get('compare-toggle').checked = true; get('compare-toggle').dispatchEvent(new w.Event('change'));
  await until(() => get('comparison-local-exact').textContent === '4,000,000,000 bytes');
  assert.equal(get('provider-metric-caption').textContent, '对比口径');
  assert.equal(get('comparison-estimate-note').hidden, true);
  change('provider-metric', 'billed');
  assert.equal(get('comparison-local-title').textContent, '本机估算计费量');
  assert.equal(get('comparison-local-exact').textContent, '1,000,000,000.5 bytes');
  assert.ok(get('comparison-estimate-note').textContent.includes('未估算原始量 1,999,999,999 bytes'));
  assert.equal(get('comparison-local-legend').hidden, false);
  assert.equal(get('comparison-upload-legend').hidden, true);
  const pairs = [...get('comparison-nodes').querySelectorAll('tr')];
  const same = pairs.find(row => row.querySelector('.comparison-name')?.textContent === 'Same');
  assert.ok(same.querySelector('.paired-value').title.includes('1,000,000,000.5 bytes'));
  assert.ok(same.querySelector('.paired-value').title.includes('未估算原始量 999,999,999 bytes'));
  assert.equal(same.querySelectorAll('.rank-upload,.rank-download').length, 0);
  const unknown = pairs.find(row => row.querySelector('.comparison-name')?.textContent === 'Unknown');
  assert.equal(unknown.querySelector('.paired-value').textContent, '—');
  assert.ok(unknown.querySelector('.paired-value').title.includes('倍率快照'));
  assert.ok([...get('comparison-chart').querySelectorAll('.chart-column')].some(column => column.title.includes('1,000,000,000.5 bytes')));
  assert.equal(get('comparison-chart').querySelectorAll('.rank-upload,.rank-download').length, 0);
  get('comparison-nodes').querySelector('.paired-value').focus();
  assert.ok(get('detail-popover').textContent.includes('仅倍率已知部分'));
  w.document.querySelector('[data-comparison-section="nodes"][data-comparison-mode="share"]').click();
  const shares = get('comparison-nodes-shares').querySelector('[data-source="local"]');
  assert.equal(shares.querySelector('h3').textContent, '本机估算计费量');
  assert.equal(shares.querySelectorAll('circle').length, 1);
  assert.ok(shares.textContent.includes('未估算部分不参与占比'));
  change('provider-metric', 'raw');
  assert.equal(get('comparison-local-exact').textContent, '4,000,000,000 bytes');
  assert.equal(get('comparison-local-title').textContent, '本机原始量');
  assert.equal(get('comparison-local-legend').hidden, true);
  assert.equal(get('comparison-nodes-shares').querySelector('[data-source="local"]').querySelectorAll('circle').length, 2);
  assert.equal(errors.length, 0);
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
  assert.equal(get('comparison-buckets').querySelectorAll('tr').length, 100);
  assert.ok(get('comparison-bucket-page').textContent.includes('101'));
  assert.equal(get('comparison-bucket-previous').disabled, true);
  const queries = calls.filter(c => c.path === '/api/history').length;
  w.document.querySelector('[data-comparison-section="buckets"][data-comparison-mode="share"]').click();
  const shareValues = () => [...get('comparison-buckets-shares').querySelectorAll('[data-source="local"] .rank-index')].map(n => n.textContent);
  assert.equal(shareValues().length, 101);
  assert.ok(shareValues().every(value => value === '0.99%'));
  assert.match(get('comparison-buckets-shares').querySelector('svg').getAttribute('aria-label'), /不受翻页影响/);
  w.document.querySelector('[data-comparison-section="buckets"][data-comparison-mode="list"]').click();
  get('comparison-bucket-next').click();
  assert.ok(get('comparison-buckets').querySelector('tr').textContent.includes(series[50].label));
  change('unit', 'GiB');
  assert.ok(get('comparison-buckets').querySelector('tr').textContent.includes(series[50].label));
  get('comparison-bucket-next').click();
  assert.equal(get('comparison-buckets').querySelectorAll('tr').length, 2);
  assert.ok(get('comparison-buckets').textContent.includes(series[100].label));
  assert.equal(get('comparison-bucket-next').disabled, true);
  w.document.querySelector('[data-comparison-section="buckets"][data-comparison-mode="share"]').click();
  assert.equal(shareValues().length, 101);
  assert.ok(shareValues().every(value => value === '0.99%'));
  w.document.querySelector('[data-comparison-section="buckets"][data-comparison-mode="list"]').click();
  assert.equal(get('comparison-local-exact').textContent, '101 bytes');
  assert.equal(calls.filter(c => c.path === '/api/history').length, queries);
  get('comparison-bucket-previous').click();
  assert.ok(get('comparison-buckets').querySelector('tr').textContent.includes(series[50].label));
  change('bucket', 'month');
  await until(() => get('comparison-buckets').textContent.includes('2026-10') && get('comparison-bucket-next').disabled);
  assert.equal(get('comparison-buckets').querySelectorAll('tr').length, 2);
  assert.equal(get('comparison-bucket-previous').disabled, true);
  w.document.querySelector('[data-comparison-section="buckets"][data-comparison-mode="share"]').click();
  assert.deepEqual(shareValues(), ['100.00%']);
  assert.equal(get('comparison-buckets-shares').querySelector('.rank-node').textContent, '2026-10');
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
    '/api/imports/preview': () => Response.json({ ...batch, warnings: [], duplicate: committed,
      newRowCount: committed ? 0 : 3, skippedRowCount: committed ? 3 : 0, newRawBytes: committed ? '0' : '3001', newBilledBytes: committed ? '0' : billedBytes }),
    '/api/imports/commit': () => { committed = true; return Response.json({ ...batch, newRowCount: 3,
      skippedRowCount: 0, newRawBytes: '3001', newBilledBytes: billedBytes, duplicate: false }); },
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
  assert.deepEqual([...w.document.querySelectorAll('img')], [w.document.querySelector('.brand > img.brand-symbol')]);
  assert.equal(errors.length, 0);
});

test('local history explains cross-core records without obsolete setup claims', async t => {
  const { get, w } = await fixture(t, {
    '/api/history': () => Response.json({ ...history, basis: 'local_proxy' })
  });
  assert.ok(get('attribution-note').textContent.includes('切换核心不隐藏记录'));
  assert.ok(get('attribution-note').textContent.includes('同名记录仍按原身份分账'));
  assert.ok(!w.document.body.textContent.includes('test.2'));
  assert.ok(!w.document.body.textContent.includes('策略组实际成员、代理链和自定义配置尚未支持'));
});

test('local details use selected units, preserve exact totals and unknown metadata', async t => {
  const { get, change } = await fixture(t, { '/api/history': () => Response.json({ ...history, records: [
    { ...history.records[0], download: '12345', upload: '0', quality: 'approximate' },
    { ...history.records[0], download: '9007199254740993', upload: '1', quality: 'recovered-partial' }
  ] }) });
  const cells = get('records').rows[0].cells;
  assert.equal(cells.length, 9);
  assert.equal(cells[2].textContent, '无');
  assert.equal(cells[4].textContent, '< 0.001 GB'); assert.equal(cells[4].title, '12,345 bytes');
  assert.equal(cells[3].textContent, '0.000 GB');
  assert.ok(cells[3].classList.contains('zero'));
  assert.equal(cells[5].title, '12,345 bytes');
  assert.equal(cells[6].textContent, '—'); assert.equal(cells[7].textContent, '—');
  assert.equal(get('billing-value').textContent, '—');
  assert.equal(cells[8].querySelector('.approximate').textContent, '分时近似');
  assert.ok(cells[8].querySelector('span').title.includes('近似值'));
  assert.ok(get('records').rows[1].querySelector('.warning'));
  assert.equal(get('records').rows[1].querySelector('.warning').textContent, '部分重叠 · 未计入');
  assert.ok(get('records').rows[1].querySelector('.warning').title.includes('当前查询范围'));
  assert.equal(get('records').rows[1].cells[1].title, history.records[0].nodeName);
  assert.equal(get('records').rows[1].cells[4].title, '9,007,199,254,740,993 bytes');
  assert.equal(get('records').rows[1].cells[5].title, '9,007,199,254,740,994 bytes');
  assert.ok(get('records').closest('.local-records'));
  change('unit', 'GiB');
  assert.equal(get('records').rows[0].cells[4].textContent, '< 0.001 GiB');
  assert.equal(get('records').rows[0].cells[4].title, '12,345 bytes');
  assert.ok(get('records-caption').textContent.includes('旧记录保持未知'));
});

test('captured billing displays partial coverage and keeps group labels separate from original names', async t => {
  const display = { originalName: 'Original synthetic', groups: ['US-0.5x'], mergeKey: null, rate: '0.5', rateStatus: 'known',
    estimatedBilledBytes: '8053063680', estimateCoveredBytes: '16106127360', estimateUnknownBytes: '0' };
  const { get } = await fixture(t, { '/api/history': () => Response.json({ ...history, displayMetadataEnabled: true,
    display: { ...display, rate: null, rateStatus: 'partial', estimateUnknownBytes: '500' },
    nodes: [{ ...history.nodes[0], display }], records: [{ ...history.records[0], display }] }) });
  assert.equal(get('billing-value').textContent, '8.053 GB');
  assert.equal(get('billing-exact').textContent, '8,053,063,680 bytes');
  assert.ok(get('billing-note').textContent.includes('未估算'));
  const cells = get('records').rows[0].cells;
  assert.equal(cells[6].textContent, '0.5');
  assert.equal(cells[7].title, '8,053,063,680 bytes');
  assert.ok(cells[1].textContent.startsWith('Original synthetic'));
  const name = cells[1].querySelector('.has-groups');
  assert.equal(name.textContent, 'Original synthetic');
  assert.ok(!cells[1].textContent.includes('US-0.5x'));
  name.focus();
  assert.equal(get('detail-popover').getAttribute('aria-label'), '策略组');
  assert.equal(get('detail-popover').querySelector('li').textContent, 'US-0.5x');
  assert.equal(get('ranking').querySelector('.has-groups').textContent, 'Original synthetic');
});

test('partial-only local recovery keeps summary unknown while measured idle remains zero', async t => {
  let result = history;
  const { get, change } = await fixture(t, { '/api/history': () => Response.json(result) });
  result = { ...history, upload: '0', download: '0', nodes: [], series: [], recordCount: 1,
    partialUpload: '500', partialDownload: '0', records: [{ ...history.records[0], upload: '500', download: '0', quality: 'recovered-partial' }] };
  change('bucket', 'hour');
  await until(() => get('records').textContent.includes('部分重叠'));
  for (const id of ['total', 'download', 'upload']) {
    assert.equal(get(`${id}-value`).textContent, '—');
    assert.equal(get(`${id}-exact`).textContent, '所选范围无已知量');
  }
  assert.ok(get('quality-notice').textContent.includes('本范围内用量未知'));
  result = { ...result, nodes: [{ ...history.nodes[0], upload: '0', download: '0' }], recordCount: 2 };
  change('bucket', 'day');
  await until(() => get('total-value').textContent === '0.000 GB');
  assert.equal(get('total-exact').textContent, '0 bytes');
});


test('comparison shares and node ranking keep independent exact denominators and follow metric and filters', async t => {
  let reads = 0;
  const local = { ...history, upload: '400', download: '0', unbucketedUpload: '300',
    nodes: [{ key: 'a', name: 'A', upload: '100', download: '0' }, { key: 'b', name: 'B', upload: '300', download: '0' }],
    series: [{ label: '2026-10-09', upload: '25', download: '0' }, { label: '2026-10-10', upload: '75', download: '0' }] };
  const provider = { source: 'shares', rawBytes: '100', billedBytes: '100.0000000000000000000000000001', recordCount: 3,
    nodes: [{ name: 'A', rawBytes: '80', billedBytes: '10' }, { name: 'B', rawBytes: '20', billedBytes: '90' },
      { name: 'Tiny', rawBytes: '0', billedBytes: '0.0000000000000000000000000001' }],
    series: [{ label: '2026-10-09', rawBytes: '80', billedBytes: '10' }, { label: '2026-10-10', rawBytes: '20', billedBytes: '90.0000000000000000000000000001' }], records: [] };
  const { get, change, w, calls, errors } = await fixture(t, {
    '/api/provider/sources': () => Response.json(['shares']),
    '/api/provider/nodes': () => Response.json(provider.nodes.map(n => ({ key: n.name, name: n.name }))),
    '/api/provider/history': url => Response.json({ ...provider, nodes: url.searchParams.has('nodeKey') ? provider.nodes.filter(n => n.name === url.searchParams.get('nodeKey')) : provider.nodes }),
    '/api/history': url => Response.json(reads++ ? { ...local, nodes: url.searchParams.has('nodeName') ? local.nodes.filter(n => n.name === url.searchParams.get('nodeName')) : local.nodes } : history)
  });
  await until(() => get('statistics-source').options.length === 2);
  change('statistics-source', 'shares'); await until(() => get('summary').getAttribute('aria-busy') === 'false');
  get('compare-toggle').checked = true; get('compare-toggle').dispatchEvent(new w.Event('change'));
  await until(() => get('comparison-local-exact').textContent === '400 bytes');
  const names = () => [...get('comparison-nodes').querySelectorAll('.comparison-name')].map(n => n.textContent);
  assert.deepEqual(names(), ['B', 'A', 'Tiny']);
  change('comparison-node-sort', 'provider'); assert.deepEqual(names(), ['A', 'B', 'Tiny']);
  const shares = (section, source) => get(`comparison-${section}-shares`).querySelector(`[data-source="${source}"]`);
  const percentages = (section, source) => [...shares(section, source).querySelectorAll('.rank-index')].map(n => n.textContent);
  for (const section of ['nodes', 'buckets']) w.document.querySelector(`[data-comparison-section="${section}"][data-comparison-mode="share"]`).click();
  assert.deepEqual(percentages('nodes', 'local'), ['75.00%', '25.00%']);
  assert.deepEqual(percentages('nodes', 'provider'), ['80.00%', '20.00%', '0.00%']);
  assert.deepEqual(percentages('buckets', 'local'), ['75.00%', '25.00%']); // 100 bucketed, not the 400 total
  assert.deepEqual(percentages('buckets', 'provider'), ['80.00%', '20.00%']);
  const circle = shares('buckets', 'local').querySelector('circle'); circle.focus();
  assert.equal(shares('buckets', 'local').querySelector('.share-tooltip').hidden, false);
  assert.match(circle.getAttribute('aria-label'), /2026-10-10.*75 bytes/);
  change('provider-metric', 'billed');
  assert.deepEqual(names(), ['B', 'A', 'Tiny']);
  assert.deepEqual(percentages('nodes', 'provider'), ['89.99%', '9.99%', '<0.01%']);
  const tiny = [...shares('nodes', 'provider').querySelectorAll('.rank-node')].find(n => n.textContent === 'Tiny');
  assert.match(tiny.title, /0\.0000000000000000000000000001 bytes/);
  change('node-filter', 'A');
  await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('nodeName') === 'A' && get('summary').getAttribute('aria-busy') === 'false');
  assert.deepEqual(percentages('nodes', 'local'), []);
  assert.ok(shares('nodes', 'local').textContent.includes('没有可估算的倍率快照'));
  assert.deepEqual(percentages('nodes', 'provider'), ['100.00%']);
  change('provider-metric', 'raw');
  assert.deepEqual(percentages('nodes', 'local'), ['100.00%']);
  assert.equal(get('comparison-nodes-list').hidden, true);
  w.document.querySelector('[data-comparison-section="nodes"][data-comparison-mode="list"]').click();
  assert.equal(get('comparison-nodes-shares').hidden, true);
  assert.equal(get('comparison-nodes-list').hidden, false);
  change('statistics-source', '');
  assert.equal(get('comparison-nodes-shares').children.length, 0);
  assert.equal(get('comparison-buckets-shares').children.length, 0);
  assert.equal(get('comparison-view').hidden, true);
  assert.deepEqual(errors, []);
});
