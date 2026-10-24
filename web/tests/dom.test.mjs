import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { JSDOM } from 'jsdom';
const html = await readFile(new URL('../dist/index.html', import.meta.url), 'utf8');
const asset = (await readdir(new URL('../dist/assets/', import.meta.url))).find(name => name.endsWith('.js'));
const script = await readFile(new URL(`../dist/assets/${asset}`, import.meta.url), 'utf8');
const history = { upload: '1073741824', download: '15032385536', unallocatedUpload: '0', unallocatedDownload: '0', gapCount: 0, series: [{ label: '2026-10-09', upload: '1073741824', download: '15032385536' }], nodes: [{ key: 'a', name: 'Synthetic A', upload: '1073741824', download: '15032385536' }], records: [{ start: '2026-10-09T10:00:00Z', end: '2026-10-09T10:01:00Z', nodeKey: 'a', nodeName: 'Synthetic A', upload: '1073741824', download: '15032385536', quality: 'measured' }], recordCount: 51 };
async function until(condition, timeout = 1000) { for (let i = 0; i < timeout / 5; i++) { if (condition()) return; await new Promise(r => setTimeout(r, 5)); } throw new Error('DOM condition not reached'); }
async function fixture(t, overrides = {}, savedUnits = {}, timers = null) {
  const dom = new JSDOM(html, { url: 'http://127.0.0.1:4173/#synthetic-once', runScripts: 'outside-only', pretendToBeVisual: true });
  t.after(() => dom.window.close());
  const w = dom.window, calls = [], errors = [];
  if (timers) {
    const originalTimeout = w.setTimeout.bind(w);
    w.setTimeout = (callback, delay, ...args) => {
      if (delay === 2000 || delay === 30000) { timers.set(delay, () => callback(...args)); return 0; }
      return originalTimeout(callback, delay, ...args);
    };
  }
  for (const [view, unit] of Object.entries(savedUnits)) w.localStorage.setItem(`v2rayn-monitor-unit-${view}`, unit);
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
  await until(() => w.document.getElementById('total-exact').textContent === '16,106,127,360 bytes').catch(error => { throw new Error(`${error.message}; alert=${w.document.getElementById('global-error').textContent}; calls=${calls.map(c=>c.path).join(',')}; errors=${errors.map(e=>String(e)).join(',')}`); });
  return { w, calls, errors, get: id => w.document.getElementById(id), change: (id, value) => { const el = w.document.getElementById(id); el.value = value; el.dispatchEvent(new w.Event('change')); }, submit: id => w.document.getElementById(id).dispatchEvent(new w.Event('submit', { cancelable: true })) };
}
test('production DOM exchanges fragment once, keeps integer bytes, separates missing rate from zero', async t => {
  const { w, calls, get, change, errors } = await fixture(t);
  assert.equal(w.location.hash, ''); assert.equal(calls.filter(c => c.path === '/api/session').length, 1);
  assert.equal(calls.find(c => c.path === '/api/session').headers['x-monitor-request'], '1');
  assert.equal(get('live-upload').textContent, '↑ —'); assert.equal(get('live-download').textContent, '↓ 0 B/s');
  change('unit', 'GiB'); assert.equal(get('total-value').textContent, '15.000 GiB'); assert.equal(get('total-exact').textContent, '16,106,127,360 bytes'); assert.equal(errors.length, 0);
});
test('ordinary history dimensions keep directory, filters, pagination and export aligned without adding totals', async t => {
  const bases = ['local_proxy', 'actual_member', 'business', 'excluded', 'unknown', 'legacy_proxy', 'routed_outbound'];
  const { w, get, calls, change } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'collecting', demo: false, historyBases: bases,
      uploadPerSecond: '0', downloadPerSecond: '0' }),
    '/api/nodes': url => Response.json([{ key: `scope:${url.searchParams.get('basis') || 'local_proxy'}`,
      name: url.searchParams.get('basis') || 'Synthetic A' }]),
    '/api/history': url => {
      const basis = url.searchParams.get('basis');
      if (!basis) return Response.json(history);
      const missing = basis === 'unknown', upload = basis === 'excluded' || missing ? '0' : '123';
      return Response.json({ ...history, basis, upload, download: '0',
        nodes: missing ? [] : [{ key: `scope:${basis}`, name: basis, upload, download: '0' }],
        records: missing ? [] : [{ ...history.records[0], nodeKey: `scope:${basis}`, nodeName: basis, upload, download: '0' }],
        series: missing ? [] : [{ label: '2026-10-09', upload, download: '0' }], recordCount: missing ? 0 : 51 });
    }
  });
  assert.ok(get('history-basis'), 'ordinary history has a dimension selector');
  assert.equal(get('history-basis-control').hidden, false);
  assert.equal(get('history-basis').value, 'local_proxy');
  assert.equal(calls.find(c => c.path === '/api/history').query.has('basis'), false);
  w.URL.createObjectURL = () => 'blob:synthetic-history';
  w.URL.revokeObjectURL = () => {};
  const downloads = [];
  w.HTMLAnchorElement.prototype.click = function () { downloads.push(this.download); };
  for (const basis of bases.slice(1)) {
    change('history-basis', basis);
    await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('basis') === basis &&
      get('summary').getAttribute('aria-busy') === 'false');
    await until(() => Array.from(get('node-filter').options).some(option => option.value === `scope:${basis}`));
    assert.equal(calls.filter(c => c.path === '/api/nodes').at(-1).query.get('basis'), basis);
    const query = calls.filter(c => c.path === '/api/history').at(-1).query;
    assert.equal(query.get('page'), '0');
    assert.equal(query.has('nodeKey'), false);
    assert.ok(get('attribution-note').textContent.includes('不相加'));
    assert.equal(get('live-upload').textContent, '↑ 0 B/s');
    if (basis === 'unknown') {
      assert.equal(get('total-value').textContent, '—');
      assert.ok(get('chart').textContent.includes('缺失记录不等于零流量'));
    } else {
      assert.equal(get('total-exact').textContent, basis === 'excluded' ? '0 bytes' : '123 bytes');
      change('node-filter', `scope:${basis}`);
      await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('nodeKey') === `scope:${basis}`);
      get('next-page').click();
      await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('page') === '1');
    }
    const count = calls.filter(c => c.path === '/api/export').length;
    get('export-button').click();
    await until(() => calls.filter(c => c.path === '/api/export').length === count + 1);
    const exported = calls.filter(c => c.path === '/api/export').at(-1).query;
    assert.equal(exported.get('basis'), basis);
    assert.equal(exported.has('nonZeroRecords'), false);
    await until(() => get('export-button').disabled === false);
    assert.ok(downloads.at(-1).startsWith(`v2rayN-local-${basis}-`));
  }
  change('history-basis', 'local_proxy');
  await until(() => get('total-exact').textContent === '16,106,127,360 bytes');
  assert.equal(calls.filter(c => c.path === '/api/history').at(-1).query.has('basis'), false);
});
test('ordinary dimension stays separate from probe dimensions and the default provider comparison', async t => {
  const provider = { source: 'Synthetic provider', rawBytes: '100', billedBytes: '100', recordCount: 0, nodes: [], series: [], records: [] };
  const { w, get, calls, change } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'collecting', demo: false, historyBases: ['local_proxy', 'actual_member'],
      uploadPerSecond: null, downloadPerSecond: null, probes: { historyEnabled: true, state: 'available' } }),
    '/api/provider/sources': () => Response.json([provider.source]),
    '/api/provider/nodes': () => Response.json([]),
    '/api/provider/history': () => Response.json(provider)
  });
  await until(() => get('statistics-source').options.length === 2);
  change('history-basis', 'actual_member');
  await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('basis') === 'actual_member');
  change('statistics-source', provider.source);
  await until(() => calls.some(c => c.path === '/api/provider/history') && get('summary').getAttribute('aria-busy') === 'false');
  assert.equal(get('history-basis-control').hidden, true);
  get('compare-toggle').checked = true; get('compare-toggle').dispatchEvent(new w.Event('change'));
  await until(() => get('summary').getAttribute('aria-busy') === 'false');
  assert.equal(calls.filter(c => c.path === '/api/history').at(-1).query.has('basis'), false);
  assert.equal(calls.filter(c => c.path === '/api/provider/history').at(-1).query.has('basis'), false);
  change('statistics-source', '');
  await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('basis') === 'actual_member');
  assert.equal(get('history-basis').value, 'actual_member');
  assert.equal(get('history-basis-control').hidden, false);
  w.document.querySelector('.nav-item[data-view=probes]').click();
  await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('basis') === 'probe_urltest_member');
  assert.equal(get('history-basis-control').hidden, true);
  w.document.querySelector('.nav-item[data-view=overview]').click();
  await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('basis') === 'actual_member');
  assert.equal(get('history-basis').value, 'actual_member');
});
test('ordinary dimension changes ignore late history and directory responses', async t => {
  let finishHistory, finishNodes;
  const { get, calls, change } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'collecting', demo: false,
      historyBases: ['local_proxy', 'business', 'excluded'], uploadPerSecond: null, downloadPerSecond: null }),
    '/api/history': url => url.searchParams.get('basis') === 'business'
      ? new Promise(resolve => { finishHistory = () => resolve(Response.json({ ...history, upload: '999', download: '0' })); })
      : Response.json(history),
    '/api/nodes': url => url.searchParams.get('basis') === 'business'
      ? new Promise(resolve => { finishNodes = () => resolve(Response.json([{ key: 'scope:old', name: 'Stale business' }])); })
      : Response.json([{ key: 'scope:new', name: 'Current dimension' }])
  });
  change('history-basis', 'business');
  await until(() => finishHistory && finishNodes);
  change('history-basis', 'excluded');
  await until(() => get('summary').getAttribute('aria-busy') === 'false');
  finishHistory(); finishNodes();
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(get('history-basis').value, 'excluded');
  assert.equal(get('total-exact').textContent, '16,106,127,360 bytes');
  assert.ok(!get('node-filter').textContent.includes('Stale business'));
  assert.equal(calls.filter(c => c.path === '/api/history').at(-1).query.get('basis'), 'excluded');
});
test('legacy or older servers keep the ordinary history selector hidden', async t => {
  const { get } = await fixture(t);
  assert.ok(get('history-basis-control'));
  assert.equal(get('history-basis-control').hidden, true);
});
test('statistics and probes default to GB and MB and retain independent display units', async t => {
  const { w, calls, get, change, errors } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'collecting', demo: false, uploadPerSecond: null, downloadPerSecond: null,
      probes: { historyEnabled: true, state: 'available' } })
  });
  assert.deepEqual(Array.from(get('unit').options, option => option.value).sort(), ['GB', 'GiB', 'MB', 'MiB'].sort());
  assert.equal(get('unit').value, 'GB');
  change('unit', 'GiB');
  w.document.querySelector('.nav-item[data-view=probes]').click();
  await until(() => get('statistics-title').textContent === '探测流量' && get('summary').getAttribute('aria-busy') === 'false');
  assert.equal(get('unit').value, 'MB');
  assert.equal(get('total-value').textContent, '16,106.127 MB');
  assert.equal(get('total-value').querySelector('.metric-unit').textContent, ' MB');
  const historyReads = calls.filter(call => call.path === '/api/history').length;
  change('unit', 'MiB');
  assert.equal(get('total-value').textContent, '15,360.000 MiB');
  assert.equal(get('total-exact').textContent, '16,106,127,360 bytes');
  assert.equal(calls.filter(call => call.path === '/api/history').length, historyReads);
  w.document.querySelector('.nav-item[data-view=overview]').click();
  await until(() => get('total-value').textContent === '15.000 GiB');
  assert.equal(get('unit').value, 'GiB');
  w.document.querySelector('.nav-item[data-view=probes]').click();
  await until(() => get('total-value').textContent === '15,360.000 MiB');
  assert.equal(get('unit').value, 'MiB');
  assert.equal(w.localStorage.getItem('v2rayn-monitor-unit-overview'), 'GiB');
  assert.equal(w.localStorage.getItem('v2rayn-monitor-unit-probes'), 'MiB');
  assert.deepEqual(errors, []);
});
test('saved page units restore on a new page and invalid preferences retain the defaults', async t => {
  const overrides = { '/api/status': () => Response.json({ state: 'collecting', demo: false, uploadPerSecond: null, downloadPerSecond: null,
    probes: { historyEnabled: true, state: 'available' } }) };
  const saved = await fixture(t, overrides, { overview: 'MB', probes: 'MiB' });
  assert.equal(saved.get('unit').value, 'MB');
  assert.equal(saved.get('total-value').textContent, '16,106.127 MB');
  saved.w.document.querySelector('.nav-item[data-view=probes]').click();
  await until(() => saved.get('total-value').textContent === '15,360.000 MiB');
  const invalid = await fixture(t, overrides, { overview: 'TB', probes: 'NaN' });
  assert.equal(invalid.get('unit').value, 'GB');
  invalid.w.document.querySelector('.nav-item[data-view=probes]').click();
  await until(() => invalid.get('total-value').textContent === '16,106.127 MB');
  assert.equal(invalid.get('unit').value, 'MB');
});
test('closed gap details defer all list rows and expansion preserves the complete history', async t => {
  const gaps = Array.from({ length: 1500 }, () => ({ start: '2026-10-09T10:00:00Z', end: '2026-10-09T10:01:00Z',
    reason: 'snapshot_changed', isOpen: false }));
  const { get, change } = await fixture(t, { '/api/history': () => Response.json({ ...history, gapCount: gaps.length, gaps }) });
  assert.equal(get('gap-details').open, false);
  assert.equal(get('gap-list').children.length, 0);
  assert.ok(get('gap-summary').textContent.includes('1500 段'));
  get('gap-details').open = true;
  await until(() => get('gap-list').children.length === gaps.length);
  assert.ok(get('gap-list').lastElementChild.textContent.includes('配置快照变化'));
  const first = get('gap-list').firstElementChild;
  change('unit', 'MB');
  assert.equal(get('gap-list').firstElementChild, first);
  assert.equal(get('gap-list').children.length, gaps.length);
  get('gap-details').open = false;
  await until(() => get('gap-list').children.length === 0);
  get('gap-details').open = true;
  await until(() => get('gap-list').children.length === gaps.length);
  assert.equal(get('total-exact').textContent, '16,106,127,360 bytes');
});
test('changing gap context closes and releases old rows, including disabled probe history', async t => {
  let historyEnabled = true;
  const gaps = Array.from({ length: 1500 }, () => ({ start: '2026-10-09T10:00:00Z', end: '2026-10-09T10:01:00Z',
    reason: 'snapshot_changed', isOpen: false }));
  const provider = { source: 'Synthetic provider', rawBytes: '1000000', billedBytes: '1000000', recordCount: 0, series: [], nodes: [], records: [] };
  const { w, get, change } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'collecting', demo: false, uploadPerSecond: null, downloadPerSecond: null,
      probes: { historyEnabled, state: historyEnabled ? 'available' : 'disabled' } }),
    '/api/history': () => Response.json({ ...history, gapCount: gaps.length, gaps }),
    '/api/provider/sources': () => Response.json([provider.source]),
    '/api/provider/nodes': () => Response.json([]),
    '/api/provider/history': () => Response.json(provider)
  });
  const expand = async () => { get('gap-details').open = true; await until(() => get('gap-list').children.length === gaps.length); };
  const closed = () => { assert.equal(get('gap-details').open, false); assert.equal(get('gap-list').children.length, 0); };
  await until(() => get('statistics-source').options.length === 2);
  await expand();
  change('statistics-source', provider.source);
  closed();
  await until(() => get('summary').getAttribute('aria-busy') === 'false');
  change('statistics-source', '');
  await until(() => get('total-value').textContent === '16.106 GB');
  closed();
  await expand();
  w.document.querySelector('.nav-item[data-view=probes]').click();
  closed();
  await until(() => get('total-value').textContent === '16,106.127 MB');
  await expand();
  historyEnabled = false;
  w.document.dispatchEvent(new w.Event('visibilitychange'));
  await until(() => get('refresh-note').textContent === '探测未启用 · 2 秒检查');
  closed();
  w.document.querySelector('.nav-item[data-view=overview]').click();
  await until(() => get('total-value').textContent === '16.106 GB');
  closed();
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
  assert.equal(get('collector-message').textContent, description);
  assert.ok(get('settings-collector-status').textContent.includes(description));
  assert.equal(get('status-dot').classList.contains('connected'), false);
  assert.ok(get('quality-notice').textContent.includes('未覆盖范围的流量未知'));
  get('gap-details').open = true;
  await until(() => get('gap-list').textContent.includes('其他出站未采集，流量未知'));
  assert.ok(get('gap-list').textContent.includes('其他出站未采集，流量未知'));
  assert.ok(get('records').textContent.includes('部分出站'));
  assert.equal(get('total-value').textContent, '16.106 GB');
});
test('Xray verified outbound coverage remains distinct from internal probe support', async t => {
  const description = '采集 3 个 Xray 独立代理出站，包含 1 个策略组选择后的连接；排除直连和阻断，内部探测尚未单独区分。';
  const { get } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'collecting', message: 'Xray 多出站联合采样', currentNode: 'Synthetic A',
      lastSample: null, uploadPerSecond: null, downloadPerSecond: null, demo: false,
      scope: { basis: 'xray_outbound_members', isPartial: false, collectedOutbounds: 3, collectedGroups: 1,
        uncollectedOutbounds: 0, uncollectedGroups: 0, description },
      probes: { historyEnabled: true, state: 'unsupported', message: 'Xray 尚未拆分内部探测' } })
  });
  assert.equal(get('collector-state').textContent, '正在采集');
  assert.equal(get('collector-message').textContent, 'Xray · 3 个代理出站 · 1 个策略组 · 不含直连和阻断');
  assert.ok(!get('collector-message').textContent.includes('联合采样'));
  assert.ok(get('settings-collector-status').textContent.includes(description));
  assert.ok(!get('settings-collector-status').textContent.includes('联合采样'));
  assert.equal(get('status-dot').classList.contains('connected'), true);
  assert.equal(get('probe-state').textContent, '当前核心未支持探测计量');
});
test('collector failure keeps the diagnostic visible even with a known scope', async t => {
  const message = '统计接口暂不可用，请检查 v2rayN 是否运行。';
  const description = 'Xray 出站可能包含探测，不能与探测量相加或直接相减。';
  const { get } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'error', message, currentNode: null, lastSample: null,
      uploadPerSecond: null, downloadPerSecond: null, demo: false,
      scope: { basis: 'xray_outbound_members', isPartial: false, collectedOutbounds: 3, collectedGroups: 1,
        uncollectedOutbounds: 0, uncollectedGroups: 0, description } })
  });
  assert.equal(get('collector-state').textContent, '采集暂不可用');
  assert.equal(get('collector-message').textContent, `${message} ${description}`);
  assert.ok(get('settings-collector-status').textContent.includes(message));
  assert.equal(get('live-upload').textContent, '↑ —');
  assert.equal(get('live-download').textContent, '↓ —');
});
for (const [state, label] of [['baseline', '正在采样'], ['reset', '正在采样'], ['ready', '已连接']])
  test(`${state} keeps the collector strip concise while retaining scope details`, async t => {
    const description = '采集 64 个 Xray 独立代理出站，包含 3 个策略组；排除直连和阻断，内部探测未单独区分。';
    const { get } = await fixture(t, {
      '/api/status': () => Response.json({ state, message: 'Xray 策略组联合采样；各独立代理出站分别记账，不叠加组汇总。',
        currentNode: 'Synthetic group', lastSample: null, uploadPerSecond: null, downloadPerSecond: null, demo: false,
        scope: { basis: 'xray_outbound_members', isPartial: false, collectedOutbounds: 64, collectedGroups: 3,
          uncollectedOutbounds: 0, uncollectedGroups: 0, description } })
    });
    assert.equal(get('collector-state').textContent, label);
    assert.equal(get('collector-message').textContent, 'Xray · 64 个代理出站 · 3 个策略组 · 不含直连和阻断');
    assert.ok(get('settings-collector-status').textContent.includes(description));
    assert.ok(!get('settings-collector-status').textContent.includes('联合采样'));
    assert.ok(get('settings-collector-status').textContent.includes('尚无有效采样'));
    assert.equal(get('live-upload').textContent, '↑ —');
    assert.equal(get('live-download').textContent, '↓ —');
  });
test('first sampling without scope explains the missing rate rather than exposing baseline jargon', async t => {
  const { get } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'baseline', message: '已建立采样基线；节点切换后从新基线开始记录。',
      lastSample: null, uploadPerSecond: null, downloadPerSecond: null, demo: false })
  });
  assert.equal(get('collector-state').textContent, '正在采样');
  assert.equal(get('collector-message').textContent, '正在读取累计流量，连续采样后显示速率。');
  assert.equal(get('live-upload').textContent, '↑ —');
});
for (const state of ['duplicate', 'stale']) test(`${state} waits for a fresh sample without showing an internal state code`, async t => {
  const message = '等待更新的有效采样，已有历史保留。';
  const { get } = await fixture(t, {
    '/api/status': () => Response.json({ state, message, demo: false,
      lastSample: null, uploadPerSecond: null, downloadPerSecond: null })
  });
  assert.equal(get('collector-state').textContent, '等待新采样');
  assert.equal(get('collector-message').textContent, message);
  assert.equal(get('live-download').textContent, '↓ —');
});
test('member collection without scope uses a plain summary and retains its detailed message', async t => {
  const message = '按独立代理成员记账；业务汇总、直连和未知归属分别保存，不相加。';
  const { get } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'collecting', message, collectionBasis: 'actual_member',
      lastSample: null, uploadPerSecond: '0', downloadPerSecond: '0', demo: false })
  });
  assert.equal(get('collector-message').textContent, '按独立代理成员统计 · 不叠加汇总和直连');
  assert.ok(get('settings-collector-status').textContent.includes(message));
  assert.equal(get('live-upload').textContent, '↑ 0 B/s');
});
for (const [state, label] of [['error', '采集暂不可用'], ['snapshot_changed', '正在切换配置']])
  test(`retained partial scope cannot replace the ${state} diagnostic`, async t => {
    const message = '统计暂不可用，请检查 v2rayN 后重试。';
    const description = '其他出站未采集，其流量未知。';
    const { get } = await fixture(t, {
      '/api/status': () => Response.json({ state, message, demo: false,
        lastSample: null, uploadPerSecond: null, downloadPerSecond: null,
        scope: { basis: 'routed_proxy', isPartial: true, uncollectedOutbounds: 2,
          uncollectedGroups: 1, description } })
    });
    assert.equal(get('collector-state').textContent, label);
    assert.equal(get('collector-message').textContent, `${message} ${description}`);
    assert.ok(get('settings-collector-status').textContent.includes(message));
    assert.equal(get('status-dot').classList.contains('partial'), false);
    assert.equal(get('status-dot').classList.contains('error'), state === 'error');
    assert.equal(get('live-upload').textContent, '↑ —');
  });
test('partial counters retain both the unavailable count and coverage warning', async t => {
  const message = '4 个范围计数未知，未补零。';
  const description = '其他出站未采集，其流量未知。';
  const { get } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'partial', message, demo: false,
      lastSample: null, uploadPerSecond: null, downloadPerSecond: null,
      scope: { basis: 'routed_proxy', isPartial: true, uncollectedOutbounds: 2,
        uncollectedGroups: 1, description } })
  });
  assert.equal(get('collector-state').textContent, '部分采集');
  assert.equal(get('collector-message').textContent, `${message} ${description}`);
  assert.ok(get('status-dot').classList.contains('partial'));
});
test('identical status and scope descriptions are not repeated', async t => {
  const description = '统计接口不可用，请检查 v2rayN。';
  const { get } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'error', message: description, demo: false,
      lastSample: null, uploadPerSecond: null, downloadPerSecond: null,
      scope: { basis: 'routed_proxy', isPartial: false, uncollectedOutbounds: 0,
        uncollectedGroups: 0, description } })
  });
  assert.equal(get('collector-message').textContent, description);
  assert.equal(get('settings-collector-status').textContent.split(description).length - 1, 1);
});
test('empty historical trend does not promise that reconnecting will fill old records', async t => {
  const { get } = await fixture(t, {
    '/api/history': () => Response.json({ ...history, series: [], unbucketedUpload: '1073741824',
      unbucketedDownload: '0', recoveredUpload: '1073741824', recoveredDownload: '0' })
  });
  assert.ok(get('chart').textContent.includes('所选范围没有可分时的记录'));
  assert.ok(!get('chart').textContent.includes('连接后'));
  assert.ok(get('chart').textContent.includes('缺失记录不等于零流量'));
  assert.ok(get('quality-notice').textContent.includes('尚未分配到当前趋势图'));
  assert.equal(get('total-value').textContent, '16.106 GB');
});
test('saved collector outages have readable reasons without changing their history', async t => {
  const reasons = ['collector_stopped', 'collector_error', 'unsupported_mode'];
  const { get } = await fixture(t, {
    '/api/history': () => Response.json({ ...history, gapCount: 3, gaps: reasons.map(reason => ({
      start: '2026-10-09T10:00:00Z', end: '2026-10-09T10:01:00Z', reason, isOpen: false
    })) })
  });
  get('gap-details').open = true;
  await until(() => get('gap-list').children.length === reasons.length);
  for (const label of ['采集器已停止', '采集或保存失败', '当时的模式不支持'])
    assert.ok(get('gap-list').textContent.includes(label));
  for (const reason of reasons) assert.ok(!get('gap-list').textContent.includes(reason));
  assert.ok(get('gap-list').textContent.includes('不代表流量全部恢复'));
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
  await until(() => get('refresh-note').textContent === '探测未启用 · 2 秒检查', 8000);
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
  assert.equal(get('collector-message').textContent, description);
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
  assert.ok(get('probe-coverage').textContent.includes('按分钟保存，并非精确的首次采样时间'));
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
  get('gap-details').open = true;
  await until(() => get('gap-list').textContent.includes('快照'));
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
test('history renders while status and the directory are pending, and period changes reuse both', async t => {
  let completeStatus, completeNodes;
  const { w, calls, get } = await fixture(t, {
    '/api/status': () => new Promise(resolve => { completeStatus = resolve; }),
    '/api/nodes': () => new Promise(resolve => { completeNodes = resolve; })
  });
  assert.equal(get('summary').getAttribute('aria-busy'), 'false');
  assert.equal(get('node-filter').options.length, 1);
  const before = calls.length;
  w.document.querySelector('[data-period=day]').click();
  await until(() => get('summary').getAttribute('aria-busy') === 'false');
  assert.deepEqual(calls.slice(before).map(call => call.path), ['/api/history']);
  assert.equal(calls.filter(call => call.path === '/api/status').length, 1);
  assert.equal(calls.filter(call => call.path === '/api/nodes').length, 1);
  completeStatus(Response.json({ state: 'collecting', demo: false, uploadPerSecond: '1', downloadPerSecond: '2' }));
  completeNodes(Response.json([{ key: 'a', name: 'Synthetic A' }]));
  await until(() => get('node-filter').options.length === 2 && get('live-upload').textContent === '↑ 1 B/s');
  assert.equal(get('total-exact').textContent, '16,106,127,360 bytes');
});
test('rapid period changes cancel only obsolete history and render the final range', async t => {
  const pending = [];
  let hold = false;
  const { w, calls, get } = await fixture(t, {
    '/api/history': (url, options) => hold ? new Promise(resolve => pending.push({ url, options, resolve })) : Response.json(history)
  });
  await until(() => get('node-filter').options.length === 3);
  const before = calls.length;
  hold = true;
  for (const period of ['year', 'month', 'day']) w.document.querySelector(`[data-period=${period}]`).click();
  assert.equal(pending.length, 3);
  assert.equal(pending[0].options.signal.aborted, true);
  assert.equal(pending[1].options.signal.aborted, true);
  assert.equal(pending[2].options.signal.aborted, false);
  pending[2].resolve(Response.json(history));
  await until(() => get('summary').getAttribute('aria-busy') === 'false');
  for (const request of pending.slice(0, 2)) request.resolve(Response.json({ ...history, upload: '1', download: '1' }));
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(get('total-exact').textContent, '16,106,127,360 bytes');
  assert.deepEqual(calls.slice(before).map(call => call.path), ['/api/history', '/api/history', '/api/history']);
});
test('comparison renders both histories before directories, then switches periods with two requests', async t => {
  const pending = [];
  let hold = false;
  const provider = { source: 'Synthetic provider', rawBytes: '10', billedBytes: '12.5', recordCount: 1, series: [], nodes: [], records: [] };
  const { w, calls, get, change } = await fixture(t, {
    '/api/provider/sources': () => Response.json([provider.source]),
    '/api/nodes': () => hold ? new Promise(resolve => pending.push(resolve)) : Response.json([{ key: 'a', name: 'Synthetic A' }]),
    '/api/provider/nodes': () => hold ? new Promise(resolve => pending.push(resolve)) : Response.json([]),
    '/api/provider/history': () => Response.json(provider)
  });
  await until(() => get('statistics-source').options.length === 2);
  hold = true;
  change('statistics-source', provider.source);
  await until(() => get('total-exact').textContent === '10 bytes');
  get('compare-toggle').checked = true; get('compare-toggle').dispatchEvent(new w.Event('change'));
  await until(() => get('comparison-local-exact').textContent === '16,106,127,360 bytes');
  assert.equal(get('comparison-billed-exact').textContent, '12.5 bytes');
  const before = calls.length;
  w.document.querySelector('[data-period=day]').click();
  await until(() => get('summary').getAttribute('aria-busy') === 'false');
  assert.deepEqual(calls.slice(before).map(call => call.path).sort(), ['/api/history', '/api/provider/history']);
  for (const resolve of pending) resolve(Response.json([{ key: 'a', name: 'Synthetic A' }]));
  await until(() => [...get('node-filter').options].some(option => option.value === 'Synthetic A'));
  assert.equal(get('comparison-billed-exact').textContent, '12.5 bytes');
});
test('two-second rate updates do not query history before its thirty-second tick', async t => {
  const timers = new Map();
  let upload = '1';
  const { w, calls, get } = await fixture(t, {
    '/api/status': () => Response.json({ state: 'collecting', message: 'Synthetic rates', demo: false,
      currentNode: 'Synthetic group selection', lastSample: null, uploadPerSecond: upload, downloadPerSecond: '0' })
  }, {}, timers);
  const historyReads = () => calls.filter(call => call.path === '/api/history').length;
  const before = historyReads();
  upload = '2'; timers.get(2000)();
  await until(() => get('live-upload').textContent === '↑ 2 B/s');
  assert.equal(get('live-download').textContent, '↓ 0 B/s');
  assert.equal(historyReads(), before);
  timers.get(30000)();
  await until(() => historyReads() === before + 1 && get('summary').getAttribute('aria-busy') === 'false');
  Object.defineProperty(w.document, 'hidden', { configurable: true, value: true });
  w.document.dispatchEvent(new w.Event('visibilitychange'));
  const count = calls.length;
  timers.get(2000)(); timers.get(30000)();
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(calls.length, count);
});
test('hidden tabs cancel requests and settings ticks refresh status without scanning history', async t => {
  const pending = [];
  let hold = false;
  const { w, calls, get, change } = await fixture(t, {
    '/api/history': (url, options) => hold ? new Promise(resolve => pending.push({ resolve, options })) : Response.json(history)
  });
  hold = true; change('bucket', 'hour');
  assert.equal(pending.length, 1);
  Object.defineProperty(w.document, 'hidden', { configurable: true, value: true });
  w.document.dispatchEvent(new w.Event('visibilitychange'));
  assert.equal(pending[0].options.signal.aborted, true);
  const before = calls.length;
  w.document.querySelector('[data-period=year]').click();
  assert.equal(calls.length, before);
  pending[0].resolve(Response.json({ ...history, upload: '1', download: '1' }));
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(get('total-exact').textContent, '16,106,127,360 bytes');
  hold = false;
  let tick;
  const originalTimeout = w.setTimeout.bind(w);
  w.setTimeout = (callback, delay, ...args) => delay === 2000 ? (tick = callback, 0) : originalTimeout(callback, delay, ...args);
  w.document.querySelector('.nav-item[data-view=settings]').click();
  Object.defineProperty(w.document, 'hidden', { configurable: true, value: false });
  w.document.dispatchEvent(new w.Event('visibilitychange'));
  await until(() => calls.at(-1).path === '/api/status');
  const historyReads = calls.filter(call => call.path === '/api/history').length;
  tick();
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(calls.filter(call => call.path === '/api/history').length, historyReads);
  w.document.querySelector('.nav-item[data-view=overview]').click();
  await until(() => calls.filter(call => call.path === '/api/history').length === historyReads + 1 && get('summary').getAttribute('aria-busy') === 'false');
});
test('a failed comparison cancels its remaining history request and the next selection recovers', async t => {
  let failing = false, pending;
  const provider = { source: 'Synthetic provider', rawBytes: '10', billedBytes: '12.5', recordCount: 1, series: [], nodes: [], records: [] };
  const { w, get, change } = await fixture(t, {
    '/api/provider/sources': () => Response.json([provider.source]),
    '/api/provider/nodes': () => Response.json([]),
    '/api/provider/history': () => failing ? new Response('{}', { status: 500 }) : Response.json(provider),
    '/api/history': (url, options) => failing ? new Promise(resolve => { pending = { options, resolve }; }) : Response.json(history)
  });
  await until(() => get('statistics-source').options.length === 2);
  change('statistics-source', provider.source);
  await until(() => get('summary').getAttribute('aria-busy') === 'false');
  failing = true;
  get('compare-toggle').checked = true; get('compare-toggle').dispatchEvent(new w.Event('change'));
  await until(() => !get('global-error').hidden);
  assert.equal(pending.options.signal.aborted, true);
  assert.equal(get('summary').getAttribute('aria-busy'), 'false');
  failing = false;
  w.document.querySelector('[data-period=day]').click();
  await until(() => get('comparison-local-exact').textContent === '16,106,127,360 bytes');
  pending.resolve(Response.json({ ...history, upload: '1', download: '1' }));
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(get('comparison-local-exact').textContent, '16,106,127,360 bytes');
  assert.equal(get('global-error').hidden, true);
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
// Synthetic capability responses; no installed core, real directory or ledger is inspected.
const coreCapabilities = {
  targetState: 'identified', ledgerVersion: 3, startupMode: '--probe-ledger',
  collectorState: 'collecting', lastSample: '2026-10-09T10:01:00Z', collectionBasis: 'actual_member', probeState: 'unavailable',
  components: [
    { id: 'sing-box-statistics', name: 'Synthetic sing-box', version: 'test.5', fileState: 'identity_matched', downloadState: 'asset_unavailable', license: 'GPL-3.0' },
    { id: 'xray-probes', name: 'Synthetic Xray', version: 'test.1', fileState: 'digest_mismatch', downloadState: 'asset_unavailable', license: 'MPL-2.0' }
  ]
};
test('core capabilities are explicit authenticated reads, not installation or runtime verification', async t => {
  const { w, get, calls } = await fixture(t, {
    '/api/core-capabilities': () => Response.json({ ...coreCapabilities,
      components: [{ ...coreCapabilities.components[0], name: '<img src=x onerror=alert(1)>' }, coreCapabilities.components[1]] })
  });
  w.document.querySelector('.nav-item[data-view=settings]').click();
  assert.equal(calls.filter(c => c.path === '/api/core-capabilities').length, 0);
  get('check-core-capabilities').click();
  await until(() => !get('core-capabilities-details').hidden);
  const call = calls.find(c => c.path === '/api/core-capabilities');
  assert.equal(call.headers['x-monitor-request'], '1');
  assert.equal(call.body, undefined); assert.equal(call.query.size, 0);
  assert.ok(get('core-components').textContent.includes('文件摘要一致'));
  assert.ok(get('core-components').textContent.includes('文件摘要不符'));
  assert.ok(get('core-components').textContent.includes('不影响原生 Xray 普通统计'));
  assert.ok(get('core-components').textContent.includes('独立组件尚未发布'));
  assert.ok(get('core-components').textContent.includes('GPL-3.0'));
  assert.ok(get('core-components').textContent.includes('<img src=x onerror=alert(1)>'));
  assert.equal(get('core-components').querySelector('img'), null);
  assert.ok(!/已安装|已生效/.test(get('core-capabilities-panel').textContent));
  assert.ok(get('core-capabilities-panel').textContent.includes('当前进程、普通统计接口与独立探测分别核验'));
  assert.ok(get('core-ledger').textContent.includes('v3 · 启动模式：--probe-ledger'));
  assert.ok(get('core-collector').textContent.includes('最近采样：'));
  assert.ok(get('core-collector').textContent.includes('10:01:00'));
  assert.ok(get('core-probe').textContent.includes('探测计数暂不可用'));
  assert.equal(get('core-capabilities-panel').querySelectorAll('button').length, 7);
  assert.equal(calls.filter(c => c.path === '/api/core-actions').length, 0);
});
test('core capability demo and unavailable targets do not imply checked files', async t => {
  for (const [targetState, text, ledgerVersion, startupMode] of [
    ['demo', '不检查本机核心文件', 1, ''], ['unavailable', '暂时无法核实目标', 2, '--scope-ledger'],
    ['not_selected', '未选择数据根目录', 3, '--probe-ledger']
  ]) await t.test(targetState, async t => {
    const { get } = await fixture(t, { '/api/core-capabilities': () => Response.json({
      ...coreCapabilities, targetState, components: [], ledgerVersion, startupMode, lastSample: null
    }) });
    get('check-core-capabilities').click(); await until(() => !get('core-capabilities-details').hidden);
    assert.ok(get('core-target').textContent.includes(text));
    assert.equal(get('core-components').children.length, 0);
    assert.ok(get('core-capabilities-result').textContent.includes('未核实配套核心文件身份'));
    assert.ok(get('core-ledger').textContent.includes(`v${ledgerVersion}`));
    assert.ok(get('core-ledger').textContent.includes(startupMode || '默认入口'));
    assert.ok(get('core-collector').textContent.includes('尚无有效采样'));
  });
});
test('core check failures allow retry and session expiry clears results and disables checks', async t => {
  let attempt = 0;
  const { get } = await fixture(t, { '/api/core-capabilities': () => {
    attempt++;
    return attempt === 1 ? Response.json({ error: 'Synthetic unavailable' }, { status: 503 })
      : attempt === 2 ? Response.json(coreCapabilities) : new Response('{}', { status: 403 });
  } });
  get('check-core-capabilities').click();
  await until(() => get('core-capabilities-result').textContent.includes('检查失败'));
  assert.ok(get('core-capabilities-result').textContent.includes('重试'));
  assert.equal(get('check-core-capabilities').disabled, false);
  get('check-core-capabilities').click(); await until(() => !get('core-capabilities-details').hidden);
  get('check-core-capabilities').click(); await until(() => !get('session-alert').hidden);
  assert.equal(get('check-core-capabilities').disabled, true);
  assert.equal(get('core-capabilities-details').hidden, true);
  assert.equal(get('core-components').textContent, '');
  assert.ok(get('core-capabilities-result').textContent.includes('托盘'));
});
test('directory edits discard even late core checks and never send the unsaved path', async t => {
  let resolve;
  const { w, get, calls, submit } = await fixture(t, { '/api/core-capabilities': () => new Promise(r => { resolve = r; }) });
  get('check-core-capabilities').click();
  assert.equal(get('check-core-capabilities').disabled, true);
  await until(() => resolve);
  const oldCall = calls.find(c => c.path === '/api/core-capabilities');
  get('v2rayn-path').value = 'D:\\Unsaved synthetic'; get('v2rayn-path').dispatchEvent(new w.Event('input'));
  assert.equal(oldCall.signal.aborted, true);
  assert.equal(get('check-core-capabilities').disabled, true);
  assert.ok(get('core-capabilities-result').textContent.includes('先保存'));
  resolve(Response.json(coreCapabilities)); await new Promise(r => setTimeout(r, 10));
  assert.equal(get('core-capabilities-details').hidden, true);
  assert.equal(get('core-components').textContent, '');
  get('check-core-capabilities').click();
  assert.equal(calls.filter(c => c.path === '/api/core-capabilities').length, 1);
  submit('settings-form'); await until(() => get('settings-result').textContent.includes('设置已保存'));
  assert.equal(get('check-core-capabilities').disabled, false);
  assert.equal(get('core-capabilities-details').hidden, true);
  assert.equal(oldCall.query.size, 0); assert.equal(oldCall.body, undefined);
});
test('settings saves invalidate completed and pending core checks, including failed saves', async t => {
  let resolve, pending = false, failSave = false;
  const { get, submit } = await fixture(t, {
    '/api/core-capabilities': () => pending ? new Promise(r => { resolve = r; }) : Response.json(coreCapabilities),
    '/api/settings': (_url, options) => options.body && failSave ? Response.json({ error: 'Synthetic save failed' }, { status: 409 })
      : Response.json({ v2rayNPath: 'C:\\Synthetic', timeZoneId: 'UTC', demo: true })
  });
  get('check-core-capabilities').click(); await until(() => !get('core-capabilities-details').hidden);
  failSave = true; submit('settings-form');
  assert.equal(get('core-capabilities-details').hidden, true); assert.equal(get('core-components').textContent, '');
  await until(() => get('settings-result').textContent.includes('Synthetic save failed'));
  assert.equal(get('check-core-capabilities').disabled, false);
  pending = true; failSave = false; get('check-core-capabilities').click(); await until(() => resolve);
  submit('settings-form');
  assert.equal(get('check-core-capabilities').disabled, true);
  await until(() => get('settings-result').textContent.includes('设置已保存'));
  resolve(Response.json(coreCapabilities)); await new Promise(r => setTimeout(r, 10));
  assert.equal(get('core-capabilities-details').hidden, true); assert.equal(get('core-components').textContent, '');
  assert.equal(get('check-core-capabilities').disabled, false);
});
test('core installation is an explicit authenticated action and remains pending until Host returns', async t => {
  let resolve;
  const timers = new Map();
  const { get, calls, submit } = await fixture(t, {
    '/api/core-capabilities': () => Response.json(coreCapabilities),
    '/api/core-actions': () => new Promise(r => { resolve = r; })
  }, {}, timers);
  assert.equal(calls.filter(c => c.path === '/api/core-actions').length, 0);
  get('check-core-capabilities').click(); await until(() => !get('core-capabilities-details').hidden);
  assert.equal(calls.filter(c => c.path === '/api/core-actions').length, 0);
  assert.equal(get('core-action-result').textContent, '尚未发起安装或恢复。');
  get('prepare-core-install').click(); await until(() => resolve);
  const call = calls.find(c => c.path === '/api/core-actions');
  assert.deepEqual(call.body, { action: 'install', componentId: 'sing-box-statistics' });
  assert.equal(call.headers['x-monitor-request'], '1'); assert.equal(call.query.size, 0);
  assert.equal(get('save-settings').disabled, true); assert.equal(get('v2rayn-path').disabled, true);
  assert.equal(get('time-zone').disabled, true); assert.equal(get('check-core-capabilities').disabled, true);
  assert.equal(get('prepare-core-restore').disabled, true);
  assert.ok(get('core-action-result').textContent.includes('原生确认窗口'));
  assert.ok(!get('core-action-result').textContent.includes('已替换'));
  submit('settings-form'); assert.equal(calls.filter(c => c.path === '/api/settings' && c.body).length, 0);
  resolve(Response.json({ state: 'InstalledPendingReload', operationId: '11111111-2222-3333-4444-555555555555',
    backupDirectory: 'Synthetic <img src=x>', details: '临时文件未完全清理：Synthetic <img src=x>' }));
  await until(() => get('core-action-result').textContent.includes('核心文件已替换'));
  assert.ok(get('core-action-result').textContent.includes('手动重载'));
  assert.ok(!get('core-action-result').textContent.includes('已生效'));
  assert.equal(get('v2rayn-path').disabled, false); assert.equal(get('save-settings').disabled, false);
  assert.ok(get('core-action-record').textContent.includes('Synthetic <img src=x>'));
  assert.ok(get('core-action-record').textContent.includes('临时文件未完全清理'));
  assert.equal(get('core-action-record').querySelector('img'), null);
  const outcome = get('core-action-result').textContent, record = get('core-action-record').textContent;
  const reads = calls.filter(c => c.path === '/api/status').length;
  timers.get(2000)(); await until(() => calls.filter(c => c.path === '/api/status').length > reads);
  assert.equal(get('core-action-result').textContent, outcome); assert.equal(get('core-action-record').textContent, record);
  get('check-core-capabilities').click(); await until(() => !get('core-capabilities-details').hidden);
  assert.equal(get('core-action-result').textContent, outcome); assert.equal(get('core-action-record').textContent, record);
});
test('core restoration accepts only a fixed component and operation GUID, never a path', async t => {
  const { get, calls, change } = await fixture(t, { '/api/core-actions': () => Response.json({
    state: 'Restored', operationId: '11111111222233334444555555555555'
  }) });
  for (const invalid of ['', 'F:\\Synthetic\\backup', 'not-a-guid', '00000000-0000-0000-0000-000000000000',
    '00000000000000000000000000000000', '1111111122223333-4444555555555555']) {
    get('core-restore-operation').value = invalid; get('prepare-core-restore').click();
    assert.ok(get('core-action-result').textContent.includes('GUID'));
  }
  assert.equal(calls.filter(c => c.path === '/api/core-actions').length, 0);
  get('core-action-component').value = 'arbitrary-component'; get('prepare-core-install').click();
  assert.ok(get('core-action-result').textContent.includes('支持的配套组件'));
  assert.equal(calls.filter(c => c.path === '/api/core-actions').length, 0);
  change('core-action-component', 'xray-probes');
  get('core-restore-operation').value = ' 11111111-2222-3333-4444-555555555555 ';
  get('prepare-core-restore').click(); await until(() => get('core-action-result').textContent.includes('核心文件已恢复'));
  assert.deepEqual(calls.find(c => c.path === '/api/core-actions').body, {
    action: 'restore', componentId: 'xray-probes', operationId: '11111111222233334444555555555555'
  });
  assert.ok(get('core-action-result').textContent.includes('账本未改动'));
  get('core-restore-operation').value = get('core-action-record').textContent.replace('操作 ID：', '');
  get('prepare-core-restore').click(); await until(() => get('core-action-result').textContent.includes('核心文件已恢复'));
  assert.equal(calls.filter(c => c.path === '/api/core-actions').length, 2);
  assert.equal(calls.filter(c => c.path === '/api/core-actions').at(-1).body.operationId, '11111111222233334444555555555555');
});
test('core action states retain precise outcomes without implying statistics are effective', async t => {
  let state;
  const { get } = await fixture(t, { '/api/core-actions': () => Response.json({
    state, operationId: '11111111-2222-3333-4444-555555555555', backupDirectory: 'Synthetic backup'
  }) });
  for (const [next, label] of [
    ['AssetUnavailable', '未下载或安装'], ['ExistingFileMatched', '现有文件摘要一致'], ['AlreadyMatched', '现有文件摘要一致'],
    ['Unavailable', '未提供配套核心操作能力'], ['UnsafePath', '路径未通过安全核验'],
    ['InvalidCandidate', '候选组件未通过核验'], ['UnsupportedExisting', '现有文件不受支持'], ['CoreRunning', '仍在运行'],
    ['Ready', '尚未完成文件操作'], ['ConfirmationRequired', '尚需 Host 原生窗口确认'], ['RecoveryRequired', '需要恢复处理'],
    ['FailedRestored', '操作失败，原核心文件已恢复'], ['AlreadyRestored', '无需重复恢复'], ['PlanChanged', '准备条件已变化'],
    ['Cancelled', '操作已取消'], ['TimedOut', '不会自动重试'], ['DownloadFailed', '组件下载失败'],
    ['InvalidArchive', '归档校验失败'], ['PermissionDenied', '未自动提权'], ['FileBusy', '正在占用'],
    ['StorageFailed', '文件操作失败'], ['UnknownTarget', '目标身份尚未核实'], ['UnsupportedTarget', '当前目标不受支持'],
    ['InvalidRequest', '请求无效'], ['future_state', '返回状态未知']
  ]) {
    state = next; get('prepare-core-install').click();
    await until(() => get('core-action-result').textContent.includes(label));
    assert.ok(!get('core-action-result').textContent.includes('已生效'));
    assert.ok(get('core-action-record').textContent.includes('11111111-2222-3333-4444-555555555555'));
  }
});
test('settings invalidate action records and late action responses after directory edits are ignored', async t => {
  let pending = false, resolve;
  const { w, get, calls, submit } = await fixture(t, { '/api/core-actions': () => pending
    ? new Promise(r => { resolve = r; }) : Response.json({ state: 'RecoveryRequired', operationId: '11111111-2222-3333-4444-555555555555' }) });
  get('prepare-core-install').click(); await until(() => get('core-action-result').textContent.includes('需要恢复处理'));
  submit('settings-form'); await until(() => get('settings-result').textContent.includes('设置已保存'));
  assert.equal(get('core-action-record').hidden, true); assert.equal(get('core-action-record').textContent, '');
  pending = true; get('prepare-core-install').click(); await until(() => resolve);
  const old = calls.filter(c => c.path === '/api/core-actions').at(-1);
  // Programmatic edits still invalidate an in-flight response even though the input is disabled.
  get('v2rayn-path').value = 'D:\\Changed synthetic'; get('v2rayn-path').dispatchEvent(new w.Event('input'));
  assert.equal(old.signal.aborted, true);
  resolve(Response.json({ state: 'InstalledPendingReload', operationId: 'old' }));
  await new Promise(r => setTimeout(r, 10));
  assert.ok(!get('core-action-result').textContent.includes('核心文件已替换'));
  assert.equal(get('core-action-record').hidden, true); assert.equal(get('prepare-core-install').disabled, true);
});
test('session expiry cancels a pending core action and does not display its late result', async t => {
  let resolve, expired = false;
  const { get, calls, change } = await fixture(t, {
    '/api/core-actions': () => new Promise(r => { resolve = r; }),
    '/api/history': () => expired ? new Response('{}', { status: 401 }) : Response.json(history)
  });
  get('prepare-core-install').click(); await until(() => resolve);
  expired = true; change('bucket', 'week'); await until(() => !get('session-alert').hidden);
  assert.equal(calls.find(c => c.path === '/api/core-actions').signal.aborted, true);
  resolve(Response.json({ state: 'InstalledPendingReload', operationId: 'old' })); await new Promise(r => setTimeout(r, 10));
  assert.equal(get('prepare-core-install').disabled, true); assert.equal(get('prepare-core-restore').disabled, true);
  assert.equal(get('core-action-record').hidden, true);
  assert.ok(get('core-action-result').textContent.includes('托盘'));
  assert.ok(!get('core-action-result').textContent.includes('核心文件已替换'));
});
const sidecarPlan = '11111111222233334444555555555555';
const runtimeObservation = { state: 'verified', coreName: 'sing-box', fileIdentityVerified: true,
  statisticsAvailable: true, memberCount: 3, probeState: 'unavailable', checkedAt: '2026-10-23T01:00:00Z' };
test('sidecar uses only fixed payloads and retains a Host plan only for explicit retry states', async t => {
  let applies = 0;
  const { get, calls } = await fixture(t, { '/api/core-actions': (_url, options) => {
    const action = JSON.parse(options.body).action;
    return Response.json(action === 'install' ? { state: 'RecoveryRequired', operationId: sidecarPlan }
      : action === 'prepare-sidecar' ? { state: 'SidecarPrepared', planId: sidecarPlan, details: 'Synthetic <img src=x>' }
      : action === 'apply-sidecar' ? { state: ['CoreRunning', 'Cancelled', 'FileBusy', 'PortUnavailable', 'SidecarInstalledPendingReload'][applies++], planId: sidecarPlan }
      : { state: 'SidecarRestored', operationId: sidecarPlan });
  } });
  assert.equal(calls.filter(c => c.path === '/api/core-actions').length, 0);
  assert.equal(get('apply-core-sidecar').disabled, true);
  get('prepare-core-install').click(); await until(() => get('core-action-result').textContent.includes('需要恢复处理'));
  const recovery = get('core-action-record').textContent;
  get('prepare-core-sidecar').click(); await until(() => !get('apply-core-sidecar').disabled);
  assert.deepEqual(calls.filter(c => c.path === '/api/core-actions').at(-1).body,
    { action: 'prepare-sidecar', componentId: 'sing-box-statistics' });
  assert.equal(get('core-sidecar-details').querySelector('img'), null);
  assert.ok(get('core-sidecar-details').textContent.includes('<img src=x>'));
  for (const label of ['仍在运行', '操作已取消', '正在占用', '返回状态未知', '统计配置已写入']) {
    get('apply-core-sidecar').click(); await until(() => get('core-sidecar-result').textContent.includes(label));
    assert.deepEqual(calls.filter(c => c.path === '/api/core-actions').at(-1).body,
      { action: 'apply-sidecar', componentId: 'sing-box-statistics', planId: sidecarPlan });
    assert.equal(get('apply-core-sidecar').disabled, label === '统计配置已写入');
  }
  assert.equal(get('apply-core-sidecar').disabled, true);
  assert.equal(get('restore-core-sidecar').disabled, false);
  const preparations = calls.filter(c => c.path === '/api/core-actions' && c.body.action === 'prepare-sidecar').length;
  get('core-restore-operation').value = '11111111-2222-3333-4444-555555555555';
  get('restore-core-sidecar').click(); await until(() => get('core-sidecar-result').textContent.includes('统计配置已恢复'));
  assert.deepEqual(calls.filter(c => c.path === '/api/core-actions').at(-1).body,
    { action: 'restore-sidecar', componentId: 'sing-box-statistics', operationId: sidecarPlan });
  assert.equal(calls.filter(c => c.path === '/api/core-actions' && c.body.action === 'prepare-sidecar').length, preparations);
  assert.equal(get('restore-core-sidecar').disabled, false);
  assert.equal(get('core-action-record').textContent, recovery);
  assert.ok(get('core-action-result').textContent.includes('需要恢复处理'));
});
test('runtime verification is explicit and ordinary statistics never imply probe availability or a selected-core match', async t => {
  let runtime = runtimeObservation;
  const { get, calls } = await fixture(t, {
    '/api/core-capabilities': () => Response.json({ ...coreCapabilities,
      components: [{ ...coreCapabilities.components[0], downloadState: 'available' }] }),
    '/api/core-actions': () => Response.json({ state: 'RuntimeChecked', runtime })
  });
  get('check-core-capabilities').click(); await until(() => !get('core-capabilities-details').hidden);
  assert.ok(get('core-components').textContent.includes('固定兼容版本可下载'));
  assert.equal(calls.filter(c => c.path === '/api/core-actions').length, 0);
  get('verify-core-runtime').click(); await until(() => !get('core-runtime-details').hidden);
  assert.deepEqual(calls.filter(c => c.path === '/api/core-actions').at(-1).body,
    { action: 'verify-runtime', componentId: 'sing-box-statistics' });
  assert.ok(get('core-runtime-details').textContent.includes('普通统计接口：可读（reset=false） · 成员数：3'));
  assert.ok(get('core-runtime-details').textContent.includes('独立探测：计数不可用'));
  runtime = { ...runtimeObservation, state: 'different_core', coreName: 'Synthetic <img src=x>', probeState: 'available' };
  get('verify-core-runtime').click(); await until(() => get('core-runtime-result').textContent.includes('另一种核心'));
  assert.ok(!get('core-runtime-result').textContent.includes('普通统计接口已核实'));
  assert.equal(get('core-runtime-details').querySelector('img'), null);
  runtime = { ...runtimeObservation, state: 'unverified_core', fileIdentityVerified: false, statisticsAvailable: false, probeState: 'available' };
  get('verify-core-runtime').click(); await until(() => get('core-runtime-result').textContent.includes('身份尚未核实'));
  assert.ok(get('core-runtime-details').textContent.includes('成员数：未知'));
  assert.ok(get('core-runtime-details').textContent.includes('独立探测：计数可用'));
});
test('settings edits saves and component changes clear sidecar plans runtime results and restore IDs', async t => {
  const { w, get, change, submit } = await fixture(t, { '/api/core-actions': (_url, options) => Response.json(
    JSON.parse(options.body).action === 'verify-runtime' ? { state: 'RuntimeChecked', runtime: runtimeObservation }
      : { state: 'SidecarAlreadyMatched', planId: sidecarPlan }) });
  get('prepare-core-sidecar').click(); await until(() => !get('apply-core-sidecar').disabled);
  get('verify-core-runtime').click(); await until(() => !get('core-runtime-details').hidden);
  get('core-restore-operation').value = sidecarPlan;
  change('core-action-component', 'xray-probes');
  assert.equal(get('prepare-core-sidecar').disabled, true); assert.equal(get('apply-core-sidecar').disabled, true);
  assert.equal(get('core-restore-operation').value, ''); assert.equal(get('core-runtime-details').hidden, true);
  change('core-action-component', 'sing-box-statistics');
  get('prepare-core-sidecar').click(); await until(() => !get('apply-core-sidecar').disabled);
  submit('settings-form'); await until(() => get('settings-result').textContent.includes('设置已保存'));
  assert.equal(get('apply-core-sidecar').disabled, true);
  get('prepare-core-sidecar').click(); await until(() => !get('apply-core-sidecar').disabled);
  get('v2rayn-path').value = 'D:\\Synthetic changed'; get('v2rayn-path').dispatchEvent(new w.Event('input'));
  assert.equal(get('apply-core-sidecar').disabled, true);
  assert.equal(get('core-sidecar-record').textContent, '');
});
test('late sidecar and runtime replies cannot survive a settings edit or session expiry', async t => {
  let resolve, expired = false;
  const { w, get, calls, change } = await fixture(t, {
    '/api/core-actions': () => new Promise(r => { resolve = r; }),
    '/api/history': () => expired ? new Response('{}', { status: 401 }) : Response.json(history)
  });
  get('prepare-core-sidecar').click(); await until(() => resolve);
  get('v2rayn-path').dispatchEvent(new w.Event('input'));
  assert.equal(calls.filter(c => c.path === '/api/core-actions').at(-1).signal.aborted, true);
  resolve(Response.json({ state: 'SidecarPrepared', planId: sidecarPlan })); await new Promise(r => setTimeout(r, 10));
  assert.equal(get('apply-core-sidecar').disabled, true);
  get('verify-core-runtime').click(); await until(() => calls.filter(c => c.path === '/api/core-actions').length === 2);
  expired = true; change('bucket', 'week'); await until(() => !get('session-alert').hidden);
  resolve(Response.json({ state: 'RuntimeChecked', runtime: runtimeObservation })); await new Promise(r => setTimeout(r, 10));
  assert.equal(get('core-runtime-details').hidden, true);
  assert.equal(get('verify-core-runtime').disabled, true); assert.equal(get('apply-core-sidecar').disabled, true);
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
test('provider tracking upgrade is explicit and legacy file totals and references stay unknown', async t => {
  let tracking = false;
  const { w, get, calls } = await fixture(t, {
    '/api/imports': () => Response.json([{ source: 'Synthetic legacy', hash: 'legacy', rowCount: 1, rawBytes: '1', billedBytes: '0.1', referencesComplete: false }]),
    '/api/imports/features': () => Response.json({ trackingAvailable: tracking, upgradeAvailable: !tracking }),
    '/api/imports/enable-tracking': () => { tracking = true; return Response.json({ state: 'Enabled', backupPath: 'Synthetic backup', preservedBatches: 1, preservedRecords: 1 }); }
  });
  w.document.querySelector('.nav-item[data-view=provider]').click();
  await until(() => !get('enable-import-tracking').disabled);
  assert.equal(calls.filter(c => c.path === '/api/imports/enable-tracking').length, 0);
  assert.ok(get('import-batches').textContent.includes('引用未知，不可撤销'));
  assert.ok(get('import-batches').textContent.includes('原始量：未知'));
  get('enable-import-tracking').click(); await until(() => get('tracking-result').textContent.includes('批次引用已启用'));
  assert.deepEqual(calls.find(c => c.path === '/api/imports/enable-tracking').body, {});
  assert.ok(get('tracking-result').textContent.includes('Synthetic backup'));
  await until(() => get('enable-import-tracking').disabled);
  assert.equal(get('import-batches').querySelector('[data-revoke-preview]'), null);
});
test('a distinct fully overlapped file can explicitly save zero-increment batch references', async t => {
  const preview = { hash: 'different-synthetic-text', rowCount: 1, rawBytes: '1', billedBytes: '0.1',
    firstTime: null, lastTime: null, duplicate: false, warnings: [], newRowCount: 0, skippedRowCount: 1, newRawBytes: '0', newBilledBytes: '0' };
  const { get, submit, calls } = await fixture(t, {
    '/api/imports/preview': () => Response.json(preview), '/api/imports/commit': () => Response.json(preview)
  });
  Object.defineProperty(get('csv-file'), 'files', { value: [{ size: 1, text: async () => 'Synthetic CSV' }] });
  get('provider-source').value = 'Synthetic'; submit('import-form'); await until(() => !get('import-preview').hidden);
  assert.equal(get('commit-import').disabled, false);
  assert.ok(get('import-result').textContent.includes('可新增本批引用'));
  get('commit-import').click(); await until(() => get('import-result').textContent.includes('已保存批次引用'));
  assert.equal(calls.filter(c => c.path === '/api/imports/commit').length, 1);
});
test('late revoke previews are invalidated by form edits and session expiry clears confirmation', async t => {
  let resolve, expired = false;
  const preview = { source: 'Synthetic', hash: 'hash', token: 'token', canRevoke: true, reason: null, rowCount: 1,
    deletedRowCount: 1, retainedRowCount: 0, deletedRawBytes: '1', deletedBilledBytes: '1', retainedRawBytes: '0', retainedBilledBytes: '0' };
  const { w, get, calls, change } = await fixture(t, {
    '/api/imports/features': () => Response.json({ trackingAvailable: true, upgradeAvailable: false }),
    '/api/imports': () => Response.json([{ ...preview, referencesComplete: true, rawBytes: '1', billedBytes: '1' }]),
    '/api/imports/revoke-preview': () => new Promise(r => { resolve = r; }),
    '/api/history': () => expired ? new Response('{}', { status: 401 }) : Response.json(history)
  });
  w.document.querySelector('.nav-item[data-view=provider]').click(); await until(() => get('import-batches').querySelector('[data-revoke-preview]'));
  get('import-batches').querySelector('[data-revoke-preview]').click(); await until(() => resolve);
  get('provider-source').dispatchEvent(new w.Event('input'));
  assert.equal(calls.find(c => c.path === '/api/imports/revoke-preview').signal.aborted, true);
  resolve(Response.json(preview)); await new Promise(r => setTimeout(r, 10));
  assert.equal(get('confirm-revoke').disabled, true); assert.equal(get('revoke-preview').hidden, true);
  w.document.querySelector('.nav-item[data-view=overview]').click(); expired = true; change('bucket', 'week');
  await until(() => !get('session-alert').hidden);
  assert.equal(get('confirm-revoke').disabled, true); assert.equal(get('enable-import-tracking').disabled, true);
});
test('provider revoke previews exact shared amounts before confirmation and last-source removal falls back to local', async t => {
  let removed = false;
  const preview = { source: 'Synthetic', hash: 'synthetic-hash', token: 'synthetic-token', canRevoke: true, reason: null,
    rowCount: 2, deletedRowCount: 1, retainedRowCount: 1, deletedRawBytes: '1', deletedBilledBytes: '0.000000000001',
    retainedRawBytes: '9007199254740993', retainedBilledBytes: '123456789.000000000001' };
  const { w, get, calls, change } = await fixture(t, {
    '/api/provider/sources': () => Response.json(removed ? [] : ['Synthetic']),
    '/api/provider/nodes': () => Response.json([]),
    '/api/provider/history': () => Response.json({ source: 'Synthetic', rawBytes: '1', billedBytes: '1', recordCount: 0, nodes: [], series: [], records: [] }),
    '/api/imports/features': () => Response.json({ trackingAvailable: true, upgradeAvailable: false }),
    '/api/imports': () => Response.json(removed ? [] : [{ ...preview, referencesComplete: true, rowCount: 0, rawBytes: '0', billedBytes: '0', fileRowCount: 2, fileRawBytes: '9007199254740994', fileBilledBytes: '123456789.000000000002' }]),
    '/api/imports/revoke-preview': () => Response.json(preview),
    '/api/imports/revoke': () => { removed = true; return Response.json({ state: 'Revoked' }); }
  });
  await until(() => get('statistics-source').options.length === 2); change('statistics-source', 'Synthetic');
  w.document.querySelector('.nav-item[data-view=provider]').click(); await until(() => get('import-batches').querySelector('[data-revoke-preview]'));
  get('import-batches').querySelector('[data-revoke-preview]').click(); await until(() => !get('confirm-revoke').disabled);
  assert.equal(calls.filter(c => c.path === '/api/imports/revoke').length, 0);
  assert.ok(get('revoke-details').textContent.includes('0.000000000001 bytes'));
  assert.ok(get('revoke-details').textContent.includes('9,007,199,254,740,993 bytes'));
  get('confirm-revoke').click(); await until(() => get('statistics-source').value === '');
  assert.deepEqual(calls.find(c => c.path === '/api/imports/revoke').body, { source: 'Synthetic', hash: 'synthetic-hash', token: 'synthetic-token' });
  assert.equal(get('compare-toggle').checked, false); assert.equal(get('confirm-revoke').disabled, true);
});
test('revoke tokens are cleared by import edits and a 409 requires another preview', async t => {
  const preview = { source: 'Synthetic', hash: 'hash', token: 'token', canRevoke: true, reason: null, rowCount: 1, deletedRowCount: 1, retainedRowCount: 0,
    deletedRawBytes: '1', deletedBilledBytes: '1', retainedRawBytes: '0', retainedBilledBytes: '0' };
  const { w, get, calls } = await fixture(t, {
    '/api/imports/features': () => Response.json({ trackingAvailable: true, upgradeAvailable: false }),
    '/api/imports': () => Response.json([{ ...preview, referencesComplete: true, rawBytes: '1', billedBytes: '1' }]),
    '/api/imports/revoke-preview': () => Response.json(preview),
    '/api/imports/revoke': () => Response.json({ error: 'Synthetic changed' }, { status: 409 })
  });
  w.document.querySelector('.nav-item[data-view=provider]').click(); await until(() => get('import-batches').querySelector('[data-revoke-preview]'));
  const previewButton = () => get('import-batches').querySelector('[data-revoke-preview]');
  previewButton().click(); await until(() => !get('confirm-revoke').disabled);
  get('provider-source').dispatchEvent(new w.Event('input')); assert.equal(get('confirm-revoke').disabled, true);
  previewButton().click(); await until(() => !get('confirm-revoke').disabled);
  get('confirm-revoke').click(); await until(() => get('revoke-result').textContent.includes('请重新预览'));
  get('confirm-revoke').click(); assert.equal(calls.filter(c => c.path === '/api/imports/revoke').length, 1);
});
test('original CSV archiving writes the preview-bound Blob bytes and reports saved only after close', async t => {
  const { w, get, submit, calls } = await fixture(t);
  const original = new Blob([new Uint8Array([239, 187, 191, 97, 13, 10])], { type: 'text/csv' });
  Object.defineProperty(original, 'name', { value: 'original.csv' });
  Object.defineProperty(get('csv-file'), 'files', { value: [original] });
  get('provider-source').value = 'Synthetic'; get('archive-csv').checked = true;
  let written, close, pickerCalls = 0;
  w.showSaveFilePicker = async () => {
    pickerCalls++;
    return { createWritable: async () => ({ write: async value => { written = value; }, close: () => new Promise(r => { close = r; }) }) };
  };
  submit('import-form'); await until(() => !get('import-preview').hidden); get('commit-import').click();
  await until(() => !get('archive-original').hidden);
  assert.ok(get('archive-result').textContent.includes('尚未保存'));
  get('save-original-csv').click(); await until(() => close);
  assert.equal(written, original); assert.ok(!get('archive-result').textContent.includes('已保存'));
  const featuresReads = calls.filter(c => c.path === '/api/imports/features').length;
  w.document.querySelector('.nav-item[data-view=provider]').click();
  await until(() => calls.filter(c => c.path === '/api/imports/features').length > featuresReads);
  await new Promise(r => setTimeout(r, 10));
  assert.equal(get('save-original-csv').disabled, true, 'batch refresh must not unlock a pending native save');
  get('save-original-csv').dispatchEvent(new w.Event('click'));
  assert.equal(pickerCalls, 1, 'pending archive also rejects synthetic repeated clicks');
  close(); await until(() => get('archive-result').textContent.includes('原 CSV 已保存'));
  assert.deepEqual([...new Uint8Array(await written.arrayBuffer())], [239, 187, 191, 97, 13, 10]);
  w.showSaveFilePicker = async () => { throw new DOMException('Synthetic cancel', 'AbortError'); };
  get('save-original-csv').click(); await until(() => get('archive-result').textContent.includes('原 CSV 未保存'));
  assert.ok(get('archive-result').textContent.includes('账本不受影响'));
});
test('recorded zero trend buckets drill through calendar timezone while missing slots and stale charts do not', async t => {
  const { w, get, change, calls } = await fixture(t, {
    '/api/settings': () => Response.json({ v2rayNPath: 'C:\\Synthetic', timeZoneId: 'Asia/Shanghai', demo: true }),
    '/api/status': () => Response.json({ state: 'demo', demo: true, historyBases: ['local_proxy', 'actual_member'] }),
    '/api/history': url => Response.json({ ...history, series: url.searchParams.get('bucket') === 'month'
      ? [{ label: '2026-11', upload: '0', download: '0' }]
      : [{ label: '2026-11-01', upload: '0', download: '0' }] })
  });
  change('history-basis', 'actual_member');
  await until(() => Array.from(get('node-filter').options).some(option => option.value === 'b'));
  change('node-filter', 'b');
  assert.equal(get('node-filter').value, 'b');
  w.document.querySelector('[data-period=year]').click();
  await until(() => get('chart').querySelector('button[data-bucket-label="2026-11"]'));
  const old = get('chart').querySelector('button');
  assert.equal(get('chart').querySelectorAll('button').length, 1);
  old.click(); await until(() => get('chart').querySelector('button[data-bucket-label="2026-11-01"]'));
  let q = calls.filter(c => c.path === '/api/history').at(-1).query;
  assert.equal(q.get('start'), '2026-10-31T16:00:00.000Z'); assert.equal(q.get('bucket'), 'day');
  assert.equal(q.get('basis'), 'actual_member'); assert.equal(q.get('nodeKey'), 'b'); assert.equal(q.get('page'), '0');
  const count = calls.filter(c => c.path === '/api/history').length;
  old.click(); assert.equal(calls.filter(c => c.path === '/api/history').length, count);
  get('chart').querySelector('button').click(); await until(() => calls.filter(c => c.path === '/api/history').at(-1).query.get('bucket') === 'hour');
  q = calls.filter(c => c.path === '/api/history').at(-1).query;
  assert.equal(q.get('end'), '2026-11-01T16:00:00.000Z'); assert.equal(get('chart').getAttribute('role'), 'group');
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
  get('gap-details').open = true;
  await until(() => get('gap-list').textContent.includes('已结束（不代表流量全部恢复）'));
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
  const timers = new Map();
  let result = { ...history, nodes: history.nodes.map(node => ({ ...node,
    display: { originalName: node.name, groups: ['Synthetic initial group'], mergeKey: null } })) };
  const { w, get, errors } = await fixture(t, { '/api/history': () => Response.json(result) }, {}, timers);
  get('ranking-share').click();
  const firstAnchor = get('ranking').querySelector('.has-groups');
  firstAnchor.focus();
  const popup = get('detail-popover');
  assert.equal(popup.hidden, false);
  assert.equal(popup.querySelector('li').textContent, 'Synthetic initial group');

  // A history tick must reconnect the same item and refresh its details.
  result = { ...result, nodes: result.nodes.map(node => ({ ...node,
    display: { ...node.display, groups: ['Synthetic updated group'] } })) };
  timers.get(30000)();
  await until(() => popup.querySelector('li')?.textContent === 'Synthetic updated group');
  const previousAnchor = get('ranking').querySelector('.has-groups');
  assert.equal(firstAnchor.isConnected, false);
  assert.equal(popup.hidden, false);
  assert.equal(previousAnchor.getAttribute('aria-expanded'), 'true');
  await until(() => w.document.activeElement === previousAnchor);

  result = { ...result, upload: '0', download: '0', series: [], records: [], recordCount: 0,
    nodes: empty ? [] : result.nodes.map(node => ({ ...node, upload: '0', download: '0' })) };
  timers.get(30000)();
  await until(() => !!get('ranking').querySelector('.empty-state'));
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

test('polished metrics preserve exact text and mobile filters disclose current selections', async t => {
  const { w, get, change, errors } = await fixture(t);
  assert.equal(get('total-value').textContent, '16.106 GB');
  assert.equal(get('total-value').querySelector('.metric-unit').textContent, ' GB');
  assert.equal(get('filter-disclosure').getAttribute('aria-expanded'), 'false');
  assert.match(get('filter-disclosure').textContent, /全部节点.*按日.*GB/);
  get('filter-disclosure').click();
  assert.equal(get('filter-extra').classList.contains('expanded'), true);
  assert.equal(get('filter-disclosure').getAttribute('aria-expanded'), 'true');
  change('unit', 'GiB');
  assert.equal(get('total-value').textContent, '15.000 GiB');
  assert.match(get('filter-disclosure').textContent, /GiB/);
  get('filter-disclosure').click();
  assert.equal(get('filter-extra').classList.contains('expanded'), false);
  assert.equal(get('filter-disclosure').getAttribute('aria-expanded'), 'false');
  assert.equal(get('ranking').classList.contains('user-view-change'), false);
  get('ranking-share').click();
  assert.equal(w.document.querySelector('.motion-snapshot'), null); // No WAAPI in JSDOM: synchronous fallback.
  get('ranking').dispatchEvent(new w.Event('animationend'));
  assert.equal(get('ranking').classList.contains('user-view-change'), false);
  assert.deepEqual(errors, []);
});

test('node sorting sits before list/share in the card heading without duplicate controls', async t => {
  const { w, get } = await fixture(t);
  assert.equal(w.document.querySelectorAll('#comparison-node-sort').length, 1);
  assert.ok(get('comparison-node-sort').closest('.panel-heading-actions'));
  assert.equal(get('comparison-node-sort-control').nextElementSibling.getAttribute('aria-label'), '同名节点对照显示方式');
  assert.equal(get('comparison-nodes-list').querySelector('#comparison-node-sort'), null);
});

test('same-name footer pages whole node pairs, keeps full share denominators, and resets on sort and filters', async t => {
  let reads=0, empty=false;
  const nodes=Array.from({length:52},(_,i)=>({key:`n${i}`,name:`Node ${String(i+1).padStart(2,'0')}`,upload:String(i+1),download:'0'}));
  const providerNodes=[...nodes.map((n,i)=>({name:n.name,rawBytes:String(100-i),billedBytes:`${100-i}.000123`})),{name:'Provider only',rawBytes:'999',billedBytes:'999.000123'}];
  const local={...history,upload:'1378',download:'0',nodes,records:[],recordCount:52};
  const provider={source:'paged',rawBytes:'4873',billedBytes:'4873.006519',recordCount:53,nodes:providerNodes,series:[],records:[]};
  const {get,change,w,errors}=await fixture(t,{
    '/api/provider/sources':()=>Response.json(['paged']),
    '/api/provider/nodes':()=>Response.json(providerNodes.map(n=>({key:n.name,name:n.name}))),
    '/api/provider/history':u=>Response.json({...provider,nodes:empty?[]:u.searchParams.has('nodeKey')?providerNodes.filter(n=>n.name===u.searchParams.get('nodeKey')):providerNodes}),
    '/api/history':u=>Response.json(reads++?{...local,nodes:empty?[]:u.searchParams.has('nodeName')?nodes.filter(n=>n.name===u.searchParams.get('nodeName')):nodes}:history)
  });
  await until(()=>get('statistics-source').options.length===2);
  change('statistics-source','paged');await until(()=>get('summary').getAttribute('aria-busy')==='false');
  get('compare-toggle').checked=true;get('compare-toggle').dispatchEvent(new w.Event('change'));
  await until(()=>get('comparison-node-page').textContent.includes('共 53'));
  assert.match(get('comparison-node-page').textContent,/第 1 \/ 2 页.*每页 50 个/);
  assert.equal(get('comparison-nodes').children.length,100);assert.equal(get('comparison-node-previous').disabled,true);
  get('comparison-node-next').click();
  assert.equal(get('comparison-nodes').children.length,6);assert.match(get('comparison-node-page').textContent,/第 2 \/ 2 页/);
  const keys=[...get('comparison-nodes').children].map(r=>JSON.parse(r.dataset.motionKey));
  for(let i=0;i<keys.length;i+=2){assert.equal(keys[i][0],keys[i+1][0]);assert.deepEqual([keys[i][1],keys[i+1][1]],[0,1]);}
  assert.ok(parseFloat(get('comparison-nodes').querySelector('.paired-bar').style.width)<10); // Full-query scale, not current page.
  assert.equal(get('comparison-node-next').disabled,true);
  w.document.querySelector('[data-comparison-section=nodes][data-comparison-mode=share]').click();
  assert.equal(get('comparison-nodes-shares').querySelector('[data-source=local]').querySelectorAll('.rank-row').length,52);
  assert.equal(get('comparison-nodes-shares').querySelector('[data-source=provider]').querySelectorAll('.rank-row').length,53);
  w.document.querySelector('[data-comparison-section=nodes][data-comparison-mode=list]').click();
  assert.match(get('comparison-node-page').textContent,/第 2 \/ 2 页/);
  change('comparison-node-sort','provider');assert.match(get('comparison-node-page').textContent,/第 1 \/ 2 页/);
  assert.equal(get('comparison-nodes').querySelector('.comparison-name').textContent,'Provider only');
  get('comparison-node-next').click();change('provider-metric','billed');assert.match(get('comparison-node-page').textContent,/第 1 \/ 2 页/);
  get('comparison-node-next').click();change('node-filter','Node 01');
  await until(()=>get('comparison-node-page').textContent.includes('共 1 个节点'));
  assert.equal(get('comparison-nodes').children.length,2);assert.equal(get('comparison-node-next').disabled,true);
  empty=true;change('bucket','hour');await until(()=>get('comparison-node-page').textContent.includes('共 0 个节点'));
  assert.match(get('comparison-node-page').textContent,/第 1 \/ 1 页/);assert.equal(get('comparison-node-previous').disabled,true);assert.equal(get('comparison-node-next').disabled,true);
  change('statistics-source','');
  assert.equal(get('comparison-node-page').textContent,'正在读取…');
  assert.equal(get('comparison-node-previous').disabled,true);assert.equal(get('comparison-node-next').disabled,true);
  assert.deepEqual(errors,[]);
});

test('comparison list flex layout anchors pagination without stretching table rows', async () => {
  const css = await readFile(new URL('../src/ui.css', import.meta.url), 'utf8');
  const dom = new JSDOM(html);
  try {
    const style = dom.window.document.createElement('style'); style.textContent = css; dom.window.document.head.append(style);
    for (const kind of ['nodes', 'buckets']) {
      const list = dom.window.document.getElementById(`comparison-${kind}-list`);
      const computed = el => dom.window.getComputedStyle(el);
      assert.equal(computed(list.parentElement).flexDirection, 'column');
      assert.equal(computed(list).display, 'flex');
      assert.equal(computed(list).flexGrow, '1');
      assert.equal(computed(list.querySelector('.pagination')).marginTop, 'auto');
      assert.equal(computed(list.querySelector('.table-scroll')).flexGrow, '0');
      assert.equal(computed(list.querySelector('.table-scroll')).maxHeight, '450px');
      list.hidden = true;
      assert.notEqual(computed(list).display, 'flex');
    }
  } finally { dom.window.close(); }
});
