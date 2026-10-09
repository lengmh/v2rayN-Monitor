import './style.css';
import { chartSlots, shortLabel } from './lib/chart.ts';
import { bytes, exactBytes, formatBytes, percentage, rate } from './lib/format.ts';
import type { Unit } from './lib/format.ts';
import { dateInZone, dateRange, periodRange, shiftAnchor, timestamp } from './lib/ranges.ts';
import type { Bucket, Period, Range } from './lib/ranges.ts';
import type { CollectorStatus, HistoryResult, ImportBatch, ImportPreview, NodeInfo, Settings } from './types.ts';

const el = <T extends HTMLElement = HTMLElement>(id: string): T => {
  const found = document.getElementById(id);
  if (!found) throw new Error(`页面组件缺失：${id}`);
  return found as T;
};
const put = (id: string, value: string): void => { el(id).textContent = value; };
const show = (id: string, visible: boolean): void => { el(id).hidden = !visible; };
const element = <K extends keyof HTMLElementTagNameMap>(tag: K, text = '', className = ''): HTMLElementTagNameMap[K] => {
  const node = document.createElement(tag); node.textContent = text; node.className = className; return node;
};
const fail = (message: string): void => { put('global-error', message); show('global-error', true); };
class ApiError extends Error { constructor(message: string, public status: number) { super(message); } }
let expired = false;
let ready = false;
const writeControls = ['save-settings', 'export-button', 'preview-import', 'commit-import', 'seed-demo'];
for (const id of writeControls) el<HTMLButtonElement>(id).disabled = true;
let refreshController: AbortController | null = null;
let refreshTimer: ReturnType<typeof setTimeout> | null = null;
let settings: Settings = { v2rayNPath: '', timeZoneId: 'UTC', demo: false };
let period: Period = 'month';
let anchor = dateInZone(new Date(), 'UTC');
let range: Range;
let bucket: Bucket = 'day';
let unit: Unit = 'GB';
let nodeKey = '';
let page = 0;
const pageSize = 50;
let latestHistory: HistoryResult | null = null;
let latestStatus: CollectorStatus | null = null;
let lastBatchList: ImportBatch[] = [];
let activeView = 'overview';
let importRequest: { source: string; timeZoneId: string; csv: string } | null = null;
let importRevision = 0;

function expireSession(): void {
  expired = true; refreshController?.abort();
  if (refreshTimer) clearTimeout(refreshTimer);
  show('session-alert', true); put('refresh-note', '会话已失效');
  for (const id of writeControls) el<HTMLButtonElement>(id).disabled = true;
}
async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  if (expired) throw new ApiError('请从托盘重新打开页面。', 401);
  const signal = options.signal ? AbortSignal.any([options.signal, AbortSignal.timeout(12000)]) : AbortSignal.timeout(12000);
  const response = await fetch(path, { ...options, signal, credentials: 'same-origin', cache: 'no-store', headers: { 'x-monitor-request': '1', ...(options.body ? { 'Content-Type': 'application/json' } : {}), ...options.headers } });
  if (response.status === 401 || response.status === 403) { expireSession(); throw new ApiError('本机会话已失效，请从托盘重新打开页面。', response.status); }
  if (!response.ok) {
    const text = await response.text(); let message = `请求失败（${response.status}）`;
    try { const body = JSON.parse(text) as { message?: string; error?: string }; message = body.message || body.error || message; } catch { /* Never render untrusted HTML error pages. */ }
    throw new ApiError(message, response.status);
  }
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}
const post = <T>(path: string, body: unknown): Promise<T> => api<T>(path, { method: 'POST', body: JSON.stringify(body) });
const errorText = (error: unknown): string => error instanceof Error ? error.message : '操作失败，请检查本机采集器。';
function query(): string {
  const params = new URLSearchParams({ start: range.start, end: range.end, timeZoneId: settings.timeZoneId, bucket, page: String(page), pageSize: String(pageSize) });
  if (nodeKey) params.set('nodeKey', nodeKey);
  return params.toString();
}
function updateRange(): void {
  range = period === 'custom' ? dateRange(el<HTMLInputElement>('custom-from').value, el<HTMLInputElement>('custom-through').value, settings.timeZoneId) : periodRange(period, anchor, settings.timeZoneId);
  put('range-label', period === 'month' ? `${range.from.slice(0, 4)} 年 ${Number(range.from.slice(5, 7))} 月` : period === 'year' ? `${range.from.slice(0, 4)} 年` : range.from === range.through ? range.from : `${range.from} – ${range.through}`);
  if (period !== 'custom') { el<HTMLInputElement>('custom-from').value = range.from; el<HTMLInputElement>('custom-through').value = range.through; }
  put('timezone-note', `统计时区：${settings.timeZoneId} · 周一开始`);
  for (const button of document.querySelectorAll<HTMLButtonElement>('[data-period]')) {
    const chosen = button.dataset.period === period; button.classList.toggle('selected', chosen); button.setAttribute('aria-pressed', String(chosen));
  }
  show('custom-range', period === 'custom');
  el<HTMLButtonElement>('previous-period').disabled = period === 'custom'; el<HTMLButtonElement>('next-period').disabled = period === 'custom';
}
function selectRange(): void {
  try { updateRange(); page = 0; void refresh(true); } catch (error) { fail(errorText(error)); }
}
function renderStatus(status: CollectorStatus): void {
  latestStatus = status;
  const states: Record<string, string> = { collecting: '正在采集', connected: '正在采集', running: '正在采集', ready: '采集器已连接', baseline: '已建立基线', disconnected: '等待连接', waiting: '等待采样', unconfigured: '等待设置目录', unsupported: '当前模式不支持', error: '采集暂不可用', demo: '合成演示模式', needs_setup: '需要设置', needs_statistics: '需要开启统计', setup: '等待设置目录', needs_directory: '等待设置目录', unsupported_platform: '系统暂不支持', unsupported_configuration: '配置暂不支持', unsupported_mode: '模式暂不支持', statistics_disabled: '需要开启统计', statistics_unavailable: '统计接口不可用', interface_unavailable: '统计接口不可用', invalid_metrics: '统计响应无效', missing_counters: '统计计数缺失', unavailable: '暂时无法读取', core_not_running: '等待内核运行', ambiguous_instance: '实例归属不明确', unsupported_version: '版本尚未核实', waiting_for_core: '等待内核加载', snapshot_changed: '正在切换配置', missing_database: '缺少配置数据库', unknown_node: '节点归属未知', ambiguous_node: '节点归属不明确', mapping_changed: '等待节点配置同步' };
  put('collector-state', states[status.state.toLowerCase()] || status.state);
  put('collector-message', status.message || '等待有效采样。');
  put('current-node', status.currentNode || '未确定'); el('current-node').title = status.currentNode || '未确定';
  put('live-upload', `↑ ${rate(status.uploadPerSecond, unit)}`); put('live-download', `↓ ${rate(status.downloadPerSecond, unit)}`);
  put('settings-collector-status', `当前状态：${states[status.state.toLowerCase()] || status.state}。${status.message} 最近采样：${timestamp(status.lastSample, settings.timeZoneId)}`);
  el('status-dot').className = `status-dot ${['collecting', 'connected', 'running', 'ready', 'demo'].includes(status.state.toLowerCase()) ? 'connected' : ['error', 'unsupported'].includes(status.state.toLowerCase()) ? 'error' : ''}`;
  show('demo-notice', status.demo); show('demo-controls', status.demo);
}
function renderNodes(nodes: NodeInfo[]): void {
  const select = el<HTMLSelectElement>('node-filter');
  const existing = Array.from(select.options).map(o => [o.value, o.textContent]);
  const next = [['', '全部节点'], ...nodes.map(n => [n.key, n.name])];
  if (JSON.stringify(existing) === JSON.stringify(next)) return;
  select.replaceChildren(...next.map(([value, name]) => { const option = element('option', name); option.value = value; return option; }));
  if (nodeKey && !nodes.some(n => n.key === nodeKey)) { const option = element('option', '已删除 / 历史节点'); option.value = nodeKey; select.append(option); }
  select.value = nodeKey;
}
function renderChart(history: HistoryPointInput): void {
  const chart = el('chart'); chart.replaceChildren();
  chart.classList.toggle('empty', !history.series.length);
  if (!history.series.length) { chart.append(element('p', '这个区间还没有可分时的记录。\n采集器连接后，新增流量会显示在这里。')); chart.setAttribute('aria-label', '此区间没有可分时记录，缺失不代表零流量'); put('chart-scale', ''); return; }
  const max = history.series.reduce((result, p) => { const total = bytes(p.upload) + bytes(p.download); return total > result ? total : result; }, 0n);
  chart.setAttribute('aria-label', `流量趋势，共 ${history.series.length} 个有记录的时间桶；空白时间不代表零流量。`);
  const slots = chartSlots(history.series, range, bucket, settings.timeZoneId);
  if (!slots) { chart.classList.add('empty'); chart.append(element('p', '此范围的时间桶过多，请切换为按日、周或月查看。\n上方合计与下方明细仍包含完整查询结果。')); put('chart-scale', ''); return; }
  const labelEvery = Math.max(1, Math.ceil(slots.length / 10));
  slots.forEach(({ label: key, point }, index) => {
    const total = point ? bytes(point.upload) + bytes(point.download) : 0n;
    const column = element('div', '', 'chart-column');
    const label = shortLabel(key, bucket);
    column.title = point ? `${key}\n下载 ${formatBytes(point.download, unit)}（${exactBytes(point.download)}）\n上传 ${formatBytes(point.upload, unit)}（${exactBytes(point.upload)}）` : `${key}\n无记录，不能视为零流量`;
    const stack = element('div', '', 'chart-stack');
    if (!point) stack.append(element('span', '', 'chart-missing'));
    else if (total === 0n) stack.append(element('span', '', 'chart-zero'));
    else { const download = element('span', '', 'bar-part bar-download'); download.style.height = `${percentage(bytes(point.download), max)}%`; const upload = element('span', '', 'bar-part bar-upload'); upload.style.height = `${percentage(bytes(point.upload), max)}%`; stack.append(download, upload); }
    column.append(stack);
    if (index % labelEvery === 0 || index === slots.length - 1) column.append(element('span', label, 'chart-label'));
    chart.append(column);
  });
  put('chart-scale', `最高 ${formatBytes(max, unit)}`);
}
type HistoryPointInput = Pick<HistoryResult, 'series'>;
function renderHistory(history: HistoryResult): void {
  latestHistory = history;
  const up = bytes(history.upload), down = bytes(history.download), total = up + down;
  put('total-value', formatBytes(total, unit)); put('total-exact', exactBytes(total));
  put('download-value', formatBytes(down, unit)); put('download-exact', exactBytes(down));
  put('upload-value', formatBytes(up, unit)); put('upload-exact', exactBytes(up));
  const unallocated = bytes(history.unallocatedUpload) + bytes(history.unallocatedDownload);
  const quality = ['缺失记录不等于零流量。'];
  if (history.gapCount) quality.push(`此范围有 ${history.gapCount} 次采集缺口。`);
  if (unallocated > 0n) quality.push(`另有恢复但未分时：${formatBytes(unallocated, unit)}（${exactBytes(unallocated)}），未计入上方合计与图表。它涉及与查询重叠的完整区间，不能跨日期视图相加。`);
  if (!history.gapCount && unallocated === 0n) quality.push('首次接入仅建立基线；短区间跨桶的分时量可能为守恒近似。');
  put('quality-notice', quality.join(' '));
  put('trend-subtitle', `${({ hour: '小时', day: '日', week: '周', month: '月', year: '年' } as const)[bucket]}分桶 · ${settings.timeZoneId}`);
  renderChart(history);
  const ranking = el('ranking'); ranking.replaceChildren();
  const sorted = [...history.nodes].sort((a, b) => { const x = bytes(a.upload) + bytes(a.download), y = bytes(b.upload) + bytes(b.download); return x > y ? -1 : x < y ? 1 : 0; });
  put('node-count', String(sorted.length));
  if (!sorted.length) ranking.append(element('p', '所选范围尚无节点记录', 'empty-state'));
  const max = sorted.length ? bytes(sorted[0].upload) + bytes(sorted[0].download) : 0n;
  sorted.forEach((node, index) => {
    const row = element('div', '', 'rank-row'), heading = element('div', '', 'rank-heading'), name = element('button', node.name || '未归属', 'rank-node');
    name.type = 'button'; name.title = node.name; name.addEventListener('click', () => { nodeKey = node.key; el<HTMLSelectElement>('node-filter').value = nodeKey; page = 0; void refresh(true); });
    const value = bytes(node.upload) + bytes(node.download);
    heading.append(element('span', String(index + 1).padStart(2, '0'), 'rank-index'), name, element('span', formatBytes(value, unit), 'rank-value'));
    const track = element('div', '', 'rank-track'), bar = element('div', '', 'rank-bar'); bar.style.width = `${percentage(value, max)}%`; track.append(bar); row.append(heading, track); ranking.append(row);
  });
  const records = el('records'); records.replaceChildren();
  const labels: Record<string, string> = { exact: '已记录', normal: '已记录', measured: '已记录', approximate: '分时近似', 'recovered-unallocated': '恢复未分时', estimated: '分时近似', interpolated: '分时近似', apportioned: '分时近似', unallocated: '恢复未分时', recovered_unallocated: '恢复未分时', unknown: '未归属', gap: '采集缺口', synthetic: '合成演示' };
  if (!history.records.length) { const row = element('tr'), cell = element('td', '这个区间没有采集记录。请检查采集器状态或选择其他日期。', 'empty-state'); cell.colSpan = 5; row.append(cell); records.append(row); }
  for (const record of history.records) {
    const row = element('tr');
    const time = element('td', timestamp(record.start, settings.timeZoneId)); time.title = `${record.start} → ${record.end}`;
    const name = element('td', record.nodeName || '未归属', 'node-cell'); name.title = record.nodeName || '未归属';
    const download = element('td', formatBytes(record.download, unit), 'numeric'); download.title = exactBytes(record.download);
    const upload = element('td', formatBytes(record.upload, unit), 'numeric'); upload.title = exactBytes(record.upload);
    const qualityCell = element('td'); const label = element('span', labels[record.quality] || record.quality, `quality-pill ${['exact', 'normal', 'measured'].includes(record.quality) ? '' : 'warning'}`); qualityCell.append(label);
    row.append(time, name, download, upload, qualityCell); records.append(row);
  }
  const pages = Math.max(1, Math.ceil(history.recordCount / pageSize));
  put('record-count', `${history.recordCount.toLocaleString('zh-CN')} 条记录`); put('page-label', `第 ${page + 1} / ${pages} 页 · 每页 ${pageSize} 条`);
  el<HTMLButtonElement>('previous-page').disabled = page === 0; el<HTMLButtonElement>('next-page').disabled = (page + 1) * pageSize >= history.recordCount;
  el('summary').setAttribute('aria-busy', 'false');
}
async function refresh(force = false): Promise<void> {
  if (expired || !ready || !range) return;
  if (refreshController && !force) return;
  if (refreshTimer) clearTimeout(refreshTimer);
  refreshController?.abort();
  const controller = new AbortController(); refreshController = controller;
  if (force) { show('global-error', false); el('summary').setAttribute('aria-busy', 'true'); put('refresh-note', '正在更新…'); }
  try {
    const results = await Promise.allSettled([
      api<CollectorStatus>('/api/status', { signal: controller.signal }),
      api<NodeInfo[]>('/api/nodes', { signal: controller.signal }),
      api<HistoryResult>(`/api/history?${query()}`, { signal: controller.signal })
    ]);
    if (controller.signal.aborted || refreshController !== controller) return;
    const [status, nodes, history] = results;
    if (status.status === 'fulfilled') renderStatus(status.value);
    if (nodes.status === 'fulfilled') renderNodes(nodes.value);
    if (history.status === 'fulfilled') { renderHistory(history.value); show('global-error', false); }
    for (const result of results) if (result.status === 'rejected') throw result.reason;
    put('refresh-note', `已更新 ${timestamp(new Date().toISOString(), settings.timeZoneId).split(' ').at(-1)} · 5 秒`);
  } catch (error) {
    if (!controller.signal.aborted && !expired) { fail(`暂时无法刷新。${errorText(error)} 已显示的数据可能不是最新。`); put('refresh-note', '连接中断 · 自动重试'); }
  } finally {
    if (refreshController === controller) { refreshController = null; if (!expired) refreshTimer = setTimeout(() => { void refresh(); }, 5000); }
  }
}
function navigate(view: string): void {
  activeView = view;
  for (const id of ['overview', 'settings', 'provider']) show(`${id}-view`, id === view);
  for (const nav of document.querySelectorAll<HTMLButtonElement>('.nav-item')) { const active = nav.dataset.view === view; nav.classList.toggle('active', active); if (active) nav.setAttribute('aria-current', 'page'); else nav.removeAttribute('aria-current'); }
  put('page-name', ({ overview: '本机总览', settings: '采集与设置', provider: '服务商账本' } as Record<string, string>)[view]);
  if (view === 'provider' && ready) void loadBatches();
}
async function loadBatches(): Promise<void> {
  try { lastBatchList = await api<ImportBatch[]>('/api/imports'); renderBatches(); } catch (error) { if (!expired) put('import-result', errorText(error)); }
}
function renderBatches(): void {
  const body = el('import-batches'); body.replaceChildren();
  if (!lastBatchList.length) { const row = element('tr'), cell = element('td', '尚未导入服务商账单。本机自动采集无需导入 CSV。', 'empty-state'); cell.colSpan = 5; row.append(cell); body.append(row); }
  for (const batch of lastBatchList) { const row = element('tr'); row.append(element('td', batch.source), element('td', String(batch.rowCount)), element('td', formatBytes(batch.rawBytes, unit), 'numeric'), element('td', formatBytes(batch.billedBytes, unit), 'numeric'), element('td', timestamp(batch.importedAt || null, settings.timeZoneId))); body.append(row); }
}
function invalidateImport(): void { importRevision++; importRequest = null; show('import-preview', false); put('import-result', ''); }
function formResult(id: string, value: string, isError = false): void { put(id, value); el(id).classList.toggle('error', isError); }

for (const button of document.querySelectorAll<HTMLButtonElement>('[data-view]')) button.addEventListener('click', () => navigate(button.dataset.view!));
document.querySelector('.brand')?.addEventListener('click', event => { event.preventDefault(); navigate('overview'); });
for (const button of document.querySelectorAll<HTMLButtonElement>('[data-period]')) button.addEventListener('click', () => { period = button.dataset.period as Period; selectRange(); });
el('custom-range').addEventListener('submit', event => { event.preventDefault(); selectRange(); });
el('previous-period').addEventListener('click', () => { if (period !== 'custom') { anchor = shiftAnchor(period, anchor, -1); selectRange(); } });
el('next-period').addEventListener('click', () => { if (period !== 'custom') { anchor = shiftAnchor(period, anchor, 1); selectRange(); } });
el('today-button').addEventListener('click', () => { if (period === 'custom') period = 'day'; anchor = dateInZone(new Date(), settings.timeZoneId); selectRange(); });
el('node-filter').addEventListener('change', () => { nodeKey = el<HTMLSelectElement>('node-filter').value; page = 0; void refresh(true); });
el('bucket').addEventListener('change', () => { bucket = el<HTMLSelectElement>('bucket').value as Bucket; page = 0; void refresh(true); });
el('unit').addEventListener('change', () => { unit = el<HTMLSelectElement>('unit').value as Unit; if (latestHistory) renderHistory(latestHistory); if (latestStatus) renderStatus(latestStatus); renderBatches(); });
el('previous-page').addEventListener('click', () => { if (page > 0) { page--; void refresh(true); } });
el('next-page').addEventListener('click', () => { if (latestHistory && (page + 1) * pageSize < latestHistory.recordCount) { page++; void refresh(true); } });
el('settings-form').addEventListener('submit', async event => {
  event.preventDefault(); const button = el<HTMLButtonElement>('save-settings'); button.disabled = true; formResult('settings-result', '正在保存…');
  try {
    const next = { ...settings, v2rayNPath: el<HTMLInputElement>('v2rayn-path').value.trim(), timeZoneId: el<HTMLSelectElement>('time-zone').value };
    // Validate that the chosen IANA timezone can be interpreted before persisting it.
    dateInZone(new Date(), next.timeZoneId);
    await post('/api/settings', next); settings = next; anchor = dateInZone(new Date(), settings.timeZoneId);
    el<HTMLSelectElement>('import-zone').value = settings.timeZoneId; invalidateImport();
    selectRange(); formResult('settings-result', '设置已保存。采集器会重新检查实例，首次有效采样先建立基线。');
  } catch (error) { formResult('settings-result', errorText(error), true); } finally { button.disabled = expired; }
});
el('export-button').addEventListener('click', async () => {
  const button = el<HTMLButtonElement>('export-button'); button.disabled = true;
  try {
    const response = await fetch(`/api/export?${query()}`, { credentials: 'same-origin', cache: 'no-store', headers: { 'x-monitor-request': '1' } });
    if (response.status === 401 || response.status === 403) { expireSession(); return; }
    if (!response.ok) { const error = await response.json().catch(() => null) as { error?: string } | null; throw new Error(error?.error || `导出失败（${response.status}）。`); }
    const blob = await response.blob(), url = URL.createObjectURL(blob), link = element('a');
    link.href = url; link.download = `v2rayN-local-${range.from}-${range.through}.csv`; document.body.append(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
  } catch (error) { fail(errorText(error)); } finally { button.disabled = expired; }
});
for (const id of ['provider-source', 'import-zone', 'csv-file']) el(id).addEventListener('change', invalidateImport);
el('provider-source').addEventListener('input', invalidateImport);
el('import-form').addEventListener('submit', async event => {
  event.preventDefault(); invalidateImport(); const revision = importRevision; const button = el<HTMLButtonElement>('preview-import'); button.disabled = true; formResult('import-result', '正在校验文件…');
  try {
    const file = el<HTMLInputElement>('csv-file').files?.[0]; if (!file) throw new Error('请先选择 CSV 文件。');
    if (file.size > 4_000_000) throw new Error('CSV 文件超过 4 MB，请使用较小的导出文件。');
    const request = { source: el<HTMLInputElement>('provider-source').value.trim(), timeZoneId: el<HTMLSelectElement>('import-zone').value, csv: await file.text() };
    if (!request.source) throw new Error('请填写来源名称。');
    const preview = await post<ImportPreview>('/api/imports/preview', request);
    if (revision !== importRevision) return;
    importRequest = request;
    put('preview-details', `来源：${request.source}\n记录：${preview.rowCount.toLocaleString('zh-CN')} 条\n服务商原始量：${formatBytes(preview.rawBytes, unit)}（${exactBytes(preview.rawBytes)}）\n服务商计费量：${formatBytes(preview.billedBytes, unit)}（${exactBytes(preview.billedBytes)}）\n最早记录：${timestamp(preview.firstTime, request.timeZoneId)}\n最晚记录：${timestamp(preview.lastTime, request.timeZoneId)}`);
    el('import-warnings').replaceChildren(...(preview.warnings || []).map(warning => element('li', warning)));
    show('import-preview', true); el<HTMLButtonElement>('commit-import').disabled = preview.duplicate;
    formResult('import-result', preview.duplicate ? '这个文件已经导入，不会重复记账。' : '预览完成。请确认来源与时区后导入。');
  } catch (error) { if (revision === importRevision) formResult('import-result', errorText(error), true); } finally { button.disabled = expired; }
});
el('commit-import').addEventListener('click', async () => {
  if (!importRequest) return;
  const request = importRequest, button = el<HTMLButtonElement>('commit-import'); button.disabled = true;
  try { await post('/api/imports/commit', request); invalidateImport(); formResult('import-result', '导入完成，已保存到独立服务商账本。'); await loadBatches(); }
  catch (error) { formResult('import-result', errorText(error), true); } finally { button.disabled = expired; }
});
el('seed-demo').addEventListener('click', async () => {
  const button = el<HTMLButtonElement>('seed-demo'); button.disabled = true;
  try { await post('/api/demo', {}); put('demo-result', '已生成合成记录，请回到本机总览查看。'); await refresh(true); }
  catch (error) { put('demo-result', errorText(error)); } finally { button.disabled = expired; }
});
window.addEventListener('pagehide', () => { refreshController?.abort(); if (refreshTimer) clearTimeout(refreshTimer); });
window.addEventListener('pageshow', event => { if (event.persisted && !expired) void refresh(true); });
document.addEventListener('visibilitychange', () => { if (!document.hidden && !expired && range) void refresh(true); });

async function start(): Promise<void> {
  // A launch credential is used once, kept out of storage, and removed before any async work.
  const token = location.hash.slice(1); history.replaceState(null, '', location.pathname + location.search);
  try {
    if (token) await post('/api/session', { token });
    const [initial, zones] = await Promise.all([api<Settings>('/api/settings'), api<{ id: string; name: string }[]>('/api/timezones')]);
    settings = initial;
    dateInZone(new Date(), settings.timeZoneId);
    el<HTMLInputElement>('v2rayn-path').value = settings.v2rayNPath || '';
    for (const id of ['time-zone', 'import-zone']) {
      const select = el<HTMLSelectElement>(id); select.replaceChildren();
      const valid = zones.filter(zone => { try { dateInZone(new Date(), zone.id); return true; } catch { return false; } });
      if (!valid.some(zone => zone.id === settings.timeZoneId)) valid.unshift({ id: settings.timeZoneId, name: settings.timeZoneId });
      select.append(...valid.map(zone => { const option = element('option', `${zone.name} · ${zone.id}`); option.value = zone.id; return option; })); select.value = settings.timeZoneId;
    }
    ready = true; for (const id of writeControls) el<HTMLButtonElement>(id).disabled = false;
    anchor = dateInZone(new Date(), settings.timeZoneId); updateRange(); show('demo-notice', settings.demo); show('demo-controls', settings.demo);
    await refresh(true);
    if (activeView === 'provider') await loadBatches();
  } catch (error) { if (!expired) fail(`无法连接本机采集器。${errorText(error)} 请确认托盘仍在运行，并从托盘重新打开页面。`); }
}
void start();
