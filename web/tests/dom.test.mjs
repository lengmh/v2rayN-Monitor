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
test('recovered unallocated totals are disclosed separately and never added to cards', async t => {
  const { get } = await fixture(t, { '/api/history': () => Response.json({ ...history, unallocatedUpload: '1000000000', unallocatedDownload: '2000000000', gapCount: 2 }) });
  assert.equal(get('total-value').textContent, '16.106 GB'); assert.ok(get('quality-notice').textContent.includes('3.000 GB')); assert.ok(get('quality-notice').textContent.includes('未计入上方合计与图表')); assert.ok(get('quality-notice').textContent.includes('不能跨日期视图相加'));
});
