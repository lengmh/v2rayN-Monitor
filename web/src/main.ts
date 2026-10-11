import './style.css';
import './ui.css';
import { chartSlots, shortLabel } from './lib/chart.ts';
import { compareNames, compareBuckets } from './lib/comparison.ts';
import type { ComparisonRow } from './lib/comparison.ts';
import { bytes, exactBytes, formatBytes, percentage, rate } from './lib/format.ts';
import { exactProviderBytes, formatProviderBytes, exactProviderTotal, formatProviderTotal, providerUnits, providerUnitsText } from './lib/provider-format.ts';
import type { Unit } from './lib/format.ts';
import { dateInZone, dateRange, periodRange, shiftAnchor, timestamp } from './lib/ranges.ts';
import type { Bucket, Period, Range } from './lib/ranges.ts';
import type { CollectorStatus, HistoryResult, ImportBatch, ImportPreview, NodeInfo, Settings, ProviderHistory } from './types.ts';

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
let latestProvider: ProviderHistory | null = null;
let statisticsSource = '';
let providerMetric: 'raw' | 'billed' = 'raw';
let comparison = false;
let latestComparison: { local: HistoryResult; provider: ProviderHistory } | null = null;
let comparisonBucketPage = 0;
let comparisonBucketCount = 0;
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
function query(localComparison = false): string {
  const params = new URLSearchParams({ start: range.start, end: range.end, timeZoneId: settings.timeZoneId, bucket, page: String(page), pageSize: String(pageSize) });
  if (nodeKey) params.set(localComparison ? 'nodeName' : 'nodeKey', nodeKey);
  if (statisticsSource && !localComparison) params.set('source', statisticsSource);
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
  const states: Record<string, string> = { collecting: '正在采集', connected: '正在采集', running: '正在采集', ready: '采集器已连接', baseline: '已建立基线', disconnected: '等待连接', waiting: '等待采样', unconfigured: '等待设置目录', unsupported: '当前模式不支持', error: '采集暂不可用', demo: '合成演示模式', needs_setup: '需要设置', needs_statistics: '需要开启统计', setup: '等待设置目录', needs_directory: '等待设置目录', unsupported_platform: '系统暂不支持', unsupported_configuration: '配置暂不支持', unsupported_connection: '连接字段暂不支持', unsupported_core: '内核暂不支持', unsupported_mode: '模式暂不支持', statistics_disabled: '需要开启统计', statistics_unavailable: '统计接口不可用', interface_unavailable: '统计接口不可用', invalid_metrics: '统计响应无效', missing_counters: '统计计数缺失', unavailable: '暂时无法读取', core_not_running: '等待内核运行', ambiguous_instance: '实例归属不明确', unsupported_version: '版本尚未核实', waiting_for_core: '等待内核加载', snapshot_changed: '正在切换配置', missing_database: '缺少配置数据库', unknown_node: '节点归属未知', ambiguous_node: '节点归属不明确', mapping_changed: '等待节点配置同步' };
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
function renderChart(history: HistoryPointInput, provider = false): void {
  const display = (value: string | bigint): string => provider ? formatProviderTotal(providerUnitsText(BigInt(value)), unit) : formatBytes(value, unit);
  const exact = (value: string | bigint): string => provider ? exactProviderTotal(providerUnitsText(BigInt(value))) : exactBytes(value);
  const chart = el('chart'); chart.replaceChildren();
  chart.classList.toggle('empty', !history.series.length);
  if (!history.series.length) { chart.append(element('p', provider ? '所选日期没有已导入记录。\n请调整范围或导入服务商 CSV。' : '这个区间还没有可分时的记录。\n采集器连接后，新增流量会显示在这里。')); chart.setAttribute('aria-label', '此区间没有可分时记录，缺失不代表零流量'); put('chart-scale', ''); return; }
  const max = history.series.reduce((result, p) => { const total = bytes(p.upload) + bytes(p.download); return total > result ? total : result; }, 0n);
  chart.setAttribute('aria-label', `流量趋势，共 ${history.series.length} 个有记录的时间桶；空白时间不代表零流量。`);
  const slots = chartSlots(history.series, range, bucket, settings.timeZoneId);
  if (!slots) { chart.classList.add('empty'); chart.append(element('p', '此范围的时间桶过多，请切换为按日、周或月查看。\n上方合计与下方明细仍包含完整查询结果。')); put('chart-scale', ''); return; }
  const labelEvery = Math.max(1, Math.ceil(slots.length / Math.max(2, Math.floor((chart.clientWidth || 600) / 65))));
  slots.forEach(({ label: key, point }, index) => {
    const total = point ? bytes(point.upload) + bytes(point.download) : 0n;
    const column = element('div', '', 'chart-column');
    const label = shortLabel(key, bucket);
    column.title = point ? provider ? `${key}\n${providerMetric === 'raw' ? '服务商原始量' : '服务商计费量'} ${display(total)}（${exact(total)}）` : `${key}\n下载 ${display(point.download)}（${exact(point.download)}）\n上传 ${display(point.upload)}（${exact(point.upload)}）` : `${key}\n无记录，不能视为零流量`;
    const recovered = bytes(point?.recoveredUpload ?? '0') + bytes(point?.recoveredDownload ?? '0');
    if (recovered > 0n) column.title += `\n其中恢复量 ${exactBytes(recovered)}，更细时间分布未知`;
    const stack = element('div', '', 'chart-stack');
    if (!point) stack.append(element('span', '', 'chart-missing'));
    else if (total === 0n) stack.append(element('span', '', 'chart-zero'));
    else { const download = element('span', '', 'bar-part bar-download'); download.style.height = `${percentage(bytes(point.download), max)}%`; const upload = element('span', '', 'bar-part bar-upload'); upload.style.height = `${percentage(bytes(point.upload), max)}%`; stack.append(download, upload); }
    column.append(stack);
    if (index === 0 || index === slots.length - 1 || (index % labelEvery === 0 && slots.length - 1 - index >= labelEvery / 2))
      column.append(element('span', label, 'chart-label'));
    chart.append(column);
  });
  put('chart-scale', `最高 ${display(max)}`);
}
type HistoryPointInput = Pick<HistoryResult, 'series'>;
function renderHistory(history: HistoryResult): void {
  latestHistory = history;
  latestProvider = null;
  statisticLabels();
  const up = bytes(history.upload), down = bytes(history.download), total = up + down;
  put('total-value', formatBytes(total, unit)); put('total-exact', exactBytes(total));
  put('download-value', formatBytes(down, unit)); put('download-exact', exactBytes(down));
  put('upload-value', formatBytes(up, unit)); put('upload-exact', exactBytes(up));
  if (history.recordCount === 0) {
    for (const id of ['total-value', 'upload-value', 'download-value']) put(id, '—');
    for (const id of ['total-exact', 'upload-exact', 'download-exact']) put(id, '所选范围无记录');
  }
  const recovered = bytes(history.recoveredUpload ?? '0') + bytes(history.recoveredDownload ?? '0');
  const partial = bytes(history.partialUpload ?? '0') + bytes(history.partialDownload ?? '0');
  const unbucketed = bytes(history.unbucketedUpload ?? '0') + bytes(history.unbucketedDownload ?? '0');
  const quality = ['已知总量不等于完整使用量；缺失记录不等于零流量。'];
  if (recovered > 0n) quality.push(`合计及节点排行已包含恢复量 ${formatBytes(recovered, unit)}（${exactBytes(recovered)}）。`);
  if (unbucketed > 0n) quality.push(`其中 ${formatBytes(unbucketed, unit)} 跨越多个时间桶，已计入合计，尚未分配到当前趋势图。`);
  if (partial > 0n) quality.push(`另有部分重叠的恢复区间，共 ${formatBytes(partial, unit)}（${exactBytes(partial)}）；本范围内用量未知，未计入合计，不能跨日期相加。`);
  if (history.gapCount) quality.push(`本范围有 ${history.gapCount} 段历史采集间断，当前采集状态见上方。`);
  put('quality-notice', quality.join(' '));
  put('quality-summary', recovered > 0n ? `已知合计包含 ${formatBytes(recovered, unit)} 恢复量` : history.gapCount ? `本范围有 ${history.gapCount} 段历史采集间断` : '统计口径与数据说明');
  const details = el('gap-list'); details.replaceChildren();
  const reasons: Record<string, string> = { long_interval: '采样间隔较长', ambiguous_instance: '实例归属核对', listener_unverified: '监听端口核对', snapshot_changed: '配置快照变化', identity_change: '会话或节点变化', counter_reset: '计数重置', mapping_changed: '节点映射变化' };
  for (const gap of history.gaps ?? []) details.append(element('li', `${timestamp(gap.start, settings.timeZoneId)} → ${timestamp(gap.end, settings.timeZoneId)} · ${reasons[gap.reason] ?? gap.reason} · ${gap.isOpen ? '尚未结束' : '已结束（不代表流量全部恢复）'}`));
  show('gap-details', history.gapCount > 0);
  put('gap-summary', `查看历史间断（${history.gapCount} 段，最多展示最近 200 段）`);
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
    const recovered = bytes(node.recoveredUpload ?? '0') + bytes(node.recoveredDownload ?? '0');
    if (recovered > 0n) {
      const note = element('small', `其中恢复量 ${formatBytes(recovered, unit)}`, 'rank-recovery');
      note.title = exactBytes(recovered); row.append(note);
    }
  });
  const records = el('records'); records.replaceChildren();
  const labels: Record<string, string> = { exact: '已记录', normal: '已记录', measured: '已记录', approximate: '分时近似', 'recovered-contained': '恢复量 · 已计入', 'recovered-partial': '部分重叠 · 未计入', 'recovered-unallocated': '恢复未分时', estimated: '分时近似', interpolated: '分时近似', apportioned: '分时近似', unallocated: '恢复未分时', recovered_unallocated: '恢复未分时', unknown: '未归属', gap: '采集缺口', synthetic: '合成演示' };
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
  pagination(history.recordCount);
  el('summary').setAttribute('aria-busy', 'false');
}
function pagination(count: number): void {
  const pages = Math.max(1, Math.ceil(count / pageSize));
  put('record-count', `${count.toLocaleString('zh-CN')} 条记录`); put('page-label', `第 ${page + 1} / ${pages} 页 · 每页 ${pageSize} 条`);
  el<HTMLButtonElement>('previous-page').disabled = page === 0;
  el<HTMLButtonElement>('next-page').disabled = (page + 1) * pageSize >= count;
}
function statisticLabels(): void {
  const provider = statisticsSource !== '';
  const metric = providerMetric === 'raw' ? '服务商原始量' : '服务商计费量';
  show('provider-metric-control', provider); show('upload-legend', !provider);
  show('compare-control', provider); show('comparison-view', comparison);
  el('overview-view').classList.toggle('comparison-mode', comparison);
  show('export-button', !comparison);
  show('attribution-note', !provider);
  el('summary').classList.toggle('provider-summary', provider);
  show('raw-card', !provider || providerMetric !== 'raw'); show('billed-card', !provider || providerMetric !== 'billed');
  put('source-caption', provider ? `服务商：${statisticsSource} · 与本机分别统计` : '本机内核统计 · 含可确定范围的恢复量');
  put('total-title', provider ? metric : '区间已知合计');
  put('total-note', provider ? '仅当前来源 · 不与本机流量相加' : '上传 + 下载 · 包含区间内恢复量');
  put('download-title', provider ? '服务商原始量' : '下载'); put('upload-title', provider ? '服务商计费量' : '上传');
  put('download-note', provider ? 'CSV 原始字节 · 未乘倍率' : '从代理节点接收');
  put('upload-note', provider ? '保留 CSV 精确数值 · 不重新计算' : '通过代理节点发送');
  put('download-legend', provider ? metric : '下载');
  put('ranking-caption', provider ? `按${metric}排序` : '按区间已知合计排序');
  put('records-heading', provider ? '服务商记录' : '采集明细');
  put('records-caption', provider ? '独立来源 · 方向、设备和 IP 未知' : '本机独立账本 · 原始整数 bytes');
  put('download-column', provider ? '原始量' : '下载'); put('upload-column', provider ? '计费量' : '上传');
  put('quality-column', provider ? '倍率' : '质量');
}
function renderComparison(local: HistoryResult, provider: ProviderHistory): void {
  if (latestComparison?.local !== local || latestComparison?.provider !== provider) comparisonBucketPage = 0;
  latestComparison = { local, provider }; latestProvider = null; latestHistory = null; statisticLabels();
  const card = (id: string, value: string | null, empty = '所选范围无记录'): void => {
    put(`comparison-${id}`, value === null ? '—' : formatProviderTotal(value, unit));
    put(`comparison-${id}-exact`, value === null ? empty : exactProviderTotal(value));
  };
  card('local', local.nodes.length ? (bytes(local.upload) + bytes(local.download)).toString() : null, '所选范围无已知量');
  card('raw', provider.recordCount ? provider.rawBytes : null); card('billed', provider.recordCount ? provider.billedBytes : null);
  const unbucketed = bytes(local.unbucketedUpload ?? '0') + bytes(local.unbucketedDownload ?? '0');
  const partial = bytes(local.partialUpload ?? '0') + bytes(local.partialDownload ?? '0');
  put('comparison-recovery', `本机历史间断 ${local.gapCount} 段。已计入总量但未分配到当前时间桶：${exactBytes(unbucketed)}；部分重叠恢复区间（未计入）：${exactBytes(partial)}。`);
  put('quality-summary', '同名汇总对照 · 两边数值分别计量');
  put('quality-notice', '名称相同仅用于展示对照，不证明节点身份相同；服务商记录范围可能不同。缺口不是零，不把差额归因于设备或漏计。');
  show('gap-details', false);
  const table = (id: string, rows: ComparisonRow[]): void => {
    const body = el(id); body.replaceChildren();
    for (const row of rows) {
      const tr = element('tr'); tr.append(element('td', row.name));
      for (const value of [row.local, row.raw, row.billed]) {
        const cell = element('td', value === null ? '无记录' : formatProviderTotal(value, unit), 'numeric');
        if (value !== null) cell.append(element('small', exactProviderTotal(value), 'exact-cell'));
        tr.append(cell);
      }
      body.append(tr);
    }
    if (!rows.length) { const row = element('tr'), cell = element('td', '所选范围没有可对照记录', 'empty-state'); cell.colSpan = 4; row.append(cell); body.append(row); }
  };
  table('comparison-nodes', compareNames(local, provider));
  const buckets = compareBuckets(local, provider);
  comparisonBucketCount = buckets.length;
  const pages = Math.max(1, Math.ceil(buckets.length / pageSize));
  comparisonBucketPage = Math.min(comparisonBucketPage, pages - 1);
  table('comparison-buckets', buckets.slice(comparisonBucketPage * pageSize, (comparisonBucketPage + 1) * pageSize));
  put('comparison-bucket-page', `共 ${buckets.length.toLocaleString('zh-CN')} 个已记录桶 · 第 ${comparisonBucketPage + 1} / ${pages} 页 · 每页 ${pageSize} 个`);
  el<HTMLButtonElement>('comparison-bucket-previous').disabled = comparisonBucketPage === 0;
  el<HTMLButtonElement>('comparison-bucket-next').disabled = comparisonBucketPage + 1 >= pages;
  put('comparison-trend-caption', `${bucket === 'hour' ? '小时' : bucket === 'week' ? '周一开始的周' : '月'} · ${settings.timeZoneId}`);
  put('comparison-metric-label', providerMetric === 'raw' ? '服务商原始量' : '服务商计费量');
  const chart = el('comparison-chart'); chart.replaceChildren(); chart.classList.remove('empty');
  const slots = chartSlots(buckets.map(row => ({ label: row.name, upload: '0', download: '0' })), range, bucket, settings.timeZoneId,
    Math.max(25, Math.floor((chart.clientWidth || 600) / 12)));
  if (!buckets.length || !slots) {
    chart.classList.add('empty'); chart.append(element('p', !buckets.length ? '没有已记录的时间桶，请调整范围或导入 CSV。' : '当前宽度无法清楚展示全部时间桶。\n请缩小日期范围或选择周/月；完整总量保留，分桶表可翻页查看。'));
  } else {
    const byLabel = new Map(buckets.map(row => [row.name, row]));
    const scale = buckets.reduce((max, row) => {
      for (const value of [row.local, providerMetric === 'raw' ? row.raw : row.billed]) {
        if (value !== null && providerUnits(value) > max) max = providerUnits(value);
      }
      return max;
    }, 0n);
    const every = Math.max(1, Math.ceil(slots.length / Math.max(2, Math.floor((chart.clientWidth || 600) / 65))));
    slots.forEach(({ label }, index) => {
      const row = byLabel.get(label), column = element('div', '', 'chart-column comparison-column'), bars = element('div', '', 'comparison-bars');
      const values = [row?.local ?? null, (providerMetric === 'raw' ? row?.raw : row?.billed) ?? null];
      column.title = `${label}\n本机：${values[0] === null ? '无记录' : exactProviderTotal(values[0])}\n服务商：${values[1] === null ? '无记录' : exactProviderTotal(values[1])}`;
      values.forEach((value, i) => {
        const bar = element('span', '', value === null ? 'chart-missing' : providerUnits(value) === 0n ? 'chart-zero' : i === 0 ? 'comparison-bar local' : 'comparison-bar provider');
        if (value !== null && providerUnits(value) > 0n) bar.style.height = `${percentage(providerUnits(value), scale)}%`;
        bars.append(bar);
      });
      column.append(bars);
      if (index === 0 || index === slots.length - 1 || (index % every === 0 && slots.length - 1 - index >= every / 2))
        column.append(element('span', shortLabel(label, bucket), 'chart-label'));
      chart.append(column);
    });
  }
  el('summary').setAttribute('aria-busy', 'false');
}
function renderProviderHistory(history: ProviderHistory): void {
  latestProvider = history; latestHistory = null; statisticLabels();
  const metric = (value: { rawBytes: string; billedBytes: string }): string => providerMetric === 'raw' ? value.rawBytes : value.billedBytes;
  put('total-value', formatProviderTotal(metric(history), unit)); put('total-exact', exactProviderTotal(metric(history)));
  put('download-value', formatBytes(history.rawBytes, unit)); put('download-exact', exactBytes(history.rawBytes));
  put('upload-value', formatProviderTotal(history.billedBytes, unit)); put('upload-exact', exactProviderTotal(history.billedBytes));
  if (history.recordCount === 0) {
    for (const id of ['total-value', 'upload-value', 'download-value']) put(id, '—');
    for (const id of ['total-exact', 'upload-exact', 'download-exact']) put(id, '所选范围无记录');
  }
  put('quality-notice', '仅统计所选服务商的已导入记录，不代表完整使用量。上下行方向、设备和 IP 未知；不与本机流量相加。');
  put('quality-summary', '服务商独立记录 · 与本机分别统计');
  show('gap-details', false);
  put('trend-subtitle', `${providerMetric === 'raw' ? '原始量' : '计费量'} · ${settings.timeZoneId}`);
  renderChart({ series: history.series.map(point => ({ label: point.label, upload: '0', download: providerUnits(metric(point)).toString() })) }, true);
  const ranking = el('ranking'); ranking.replaceChildren();
  const sorted = [...history.nodes].sort((a, b) => {
    const x = providerUnits(metric(a)), y = providerUnits(metric(b)); return x > y ? -1 : x < y ? 1 : a.name.localeCompare(b.name);
  });
  put('node-count', String(sorted.length));
  if (!sorted.length) ranking.append(element('p', '所选范围没有导入的节点记录', 'empty-state'));
  const max = sorted.length ? providerUnits(metric(sorted[0])) : 0n;
  sorted.forEach((node, index) => {
    const row = element('div', '', 'rank-row'), heading = element('div', '', 'rank-heading');
    const name = element('button', node.name, 'rank-node'); name.type = 'button'; name.title = node.name;
    name.addEventListener('click', () => { nodeKey = node.name; el<HTMLSelectElement>('node-filter').value = nodeKey; page = 0; void refresh(true); });
    const amount = element('span', formatProviderTotal(metric(node), unit), 'rank-value'); amount.title = exactProviderTotal(metric(node));
    heading.append(element('span', String(index + 1), 'rank-index'), name, amount);
    const track = element('div', '', 'rank-track'), bar = element('div', '', 'rank-bar');
    bar.style.width = `${percentage(providerUnits(metric(node)), max)}%`; track.append(bar); row.append(heading, track); ranking.append(row);
  });
  const records = el('records'); records.replaceChildren();
  if (!history.records.length) { const row = element('tr'), cell = element('td', '这个范围没有已导入记录，请调整日期或来源。', 'empty-state'); cell.colSpan = 5; row.append(cell); records.append(row); }
  for (const record of history.records) {
    const row = element('tr'), name = element('td', record.nodeName, 'node-cell'); name.title = record.nodeName;
    const raw = element('td', formatBytes(record.rawBytes, unit), 'numeric'); raw.title = exactBytes(record.rawBytes);
    const billed = element('td', formatProviderTotal(record.billedBytes, unit), 'numeric'); billed.title = exactProviderTotal(record.billedBytes);
    raw.append(element('small', exactBytes(record.rawBytes), 'exact-cell'));
    billed.append(element('small', exactProviderTotal(record.billedBytes), 'exact-cell'));
    row.append(element('td', timestamp(record.at, settings.timeZoneId)), name, raw, billed, element('td', record.rate));
    records.append(row);
  }
  pagination(history.recordCount); el('summary').setAttribute('aria-busy', 'false');
}
async function loadSources(): Promise<void> {
  const sources = await api<string[]>('/api/provider/sources');
  const select = el<HTMLSelectElement>('statistics-source');
  select.replaceChildren(element('option', '本机采集'));
  select.options[0].value = '';
  for (const source of sources) { const option = element('option', `服务商 · ${source}`); option.value = source; select.append(option); }
  select.value = statisticsSource;
}
async function refresh(force = false): Promise<void> {
  if (expired || !ready || !range) return;
  if (refreshController && !force) return;
  if (refreshTimer) clearTimeout(refreshTimer);
  refreshController?.abort();
  const controller = new AbortController(); refreshController = controller;
  if (force) { show('global-error', false); el('summary').setAttribute('aria-busy', 'true'); put('refresh-note', '正在更新…'); }
  try {
    // Imported history changes only on an explicit operation, not each collector tick.
    if (statisticsSource && (latestProvider || latestComparison) && !force) {
      const status = await api<CollectorStatus>('/api/status', { signal: controller.signal });
      if (!controller.signal.aborted && refreshController === controller) renderStatus(status);
      return;
    }
    if (comparison) {
      const [status, localNodes, providerNodes, local, provider] = await Promise.all([
        api<CollectorStatus>('/api/status', { signal: controller.signal }),
        api<NodeInfo[]>('/api/nodes', { signal: controller.signal }),
        api<NodeInfo[]>(`/api/provider/nodes?source=${encodeURIComponent(statisticsSource)}`, { signal: controller.signal }),
        api<HistoryResult>(`/api/history?${query(true)}`, { signal: controller.signal }),
        api<ProviderHistory>(`/api/provider/history?${query()}`, { signal: controller.signal })
      ]);
      if (controller.signal.aborted || refreshController !== controller) return;
      renderStatus(status);
      const names = [...new Set([...localNodes, ...providerNodes].map(node => node.name))].sort();
      renderNodes(names.map(name => ({ key: name, name })));
      renderComparison(local, provider);
      put('refresh-note', `已更新 ${timestamp(new Date().toISOString(), settings.timeZoneId).split(' ').at(-1)} · 对照按操作刷新`);
      return;
    }
    const results = await Promise.allSettled([
      api<CollectorStatus>('/api/status', { signal: controller.signal }),
      api<NodeInfo[]>(statisticsSource ? `/api/provider/nodes?source=${encodeURIComponent(statisticsSource)}` : '/api/nodes', { signal: controller.signal }),
      api<HistoryResult | ProviderHistory>(`${statisticsSource ? '/api/provider/history' : '/api/history'}?${query()}`, { signal: controller.signal })
    ]);
    if (controller.signal.aborted || refreshController !== controller) return;
    const [status, nodes, history] = results;
    if (status.status === 'fulfilled') renderStatus(status.value);
    if (nodes.status === 'fulfilled') renderNodes(nodes.value);
    if (history.status === 'fulfilled') {
      if (statisticsSource) renderProviderHistory(history.value as ProviderHistory); else renderHistory(history.value as HistoryResult);
      show('global-error', false);
    }
    for (const result of results) if (result.status === 'rejected') throw result.reason;
    put('refresh-note', `已更新 ${timestamp(new Date().toISOString(), settings.timeZoneId).split(' ').at(-1)}${statisticsSource ? ' · 导入或筛选后刷新' : ' · 5 秒'}`);
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
  put('page-name', ({ overview: '流量统计', settings: '采集与设置', provider: '导入记录' } as Record<string, string>)[view]);
  if (view === 'provider' && ready) void loadBatches();
}
async function loadBatches(): Promise<void> {
  try { lastBatchList = await api<ImportBatch[]>('/api/imports'); renderBatches(); } catch (error) { if (!expired) put('import-result', errorText(error)); }
}
function renderBatches(): void {
  const body = el('import-batches'); body.replaceChildren();
  if (!lastBatchList.length) { const row = element('tr'), cell = element('td', '尚未导入服务商账单。本机自动采集无需导入 CSV。', 'empty-state'); cell.colSpan = 5; row.append(cell); body.append(row); }
  for (const batch of lastBatchList) {
    const row = element('tr'), billed = element('td', undefined, 'numeric');
    billed.append(element('div', formatProviderBytes(batch.billedBytes, unit)), element('small', exactProviderBytes(batch.billedBytes)));
    const source = element('td'), group = element('div', '', 'batch-source'), view = element('button', '查看来源统计 →', 'text-button');
    view.type = 'button'; view.setAttribute('aria-label', `按当前日期范围查看来源 ${batch.source} 的统计`);
    view.addEventListener('click', async () => {
      try {
        await loadSources(); chooseSource(batch.source);
        navigate('overview'); void refresh(true);
      } catch (error) { formResult('import-result', errorText(error), true); }
    });
    group.append(element('span', batch.source), view); source.append(group);
    row.append(source, element('td', String(batch.rowCount)), element('td', formatBytes(batch.rawBytes, unit), 'numeric'), billed, element('td', timestamp(batch.importedAt || null, settings.timeZoneId))); body.append(row);
  }
}
function chooseSource(source: string): void {
  statisticsSource = source; el<HTMLSelectElement>('statistics-source').value = source;
  if (!source) comparison = false;
  el<HTMLInputElement>('compare-toggle').checked = comparison;
  const select = el<HTMLSelectElement>('bucket');
  const options: [Bucket, string][] = comparison ? [['hour', '按小时'], ['week', '按周'], ['month', '按月']] :
    [['hour', '按小时'], ['day', '按日'], ['week', '按周'], ['month', '按月'], ['year', '按年']];
  if (!options.some(([value]) => value === bucket)) bucket = 'hour';
  select.replaceChildren(...options.map(([value, label]) => { const option = element('option', label); option.value = value; return option; }));
  select.value = bucket;
  nodeKey = ''; page = 0; latestHistory = null; latestProvider = null; latestComparison = null; statisticLabels();
  comparisonBucketPage = 0; comparisonBucketCount = 0;
  put('comparison-bucket-page', '正在读取…');
  el<HTMLButtonElement>('comparison-bucket-previous').disabled = true;
  el<HTMLButtonElement>('comparison-bucket-next').disabled = true;
  const allNodes = element('option', '全部节点'); allNodes.value = '';
  el<HTMLSelectElement>('node-filter').replaceChildren(allNodes);
  for (const id of ['total-value', 'total-exact', 'upload-value', 'upload-exact', 'download-value', 'download-exact', 'node-count']) put(id, '—');
  for (const id of ['records', 'ranking', 'chart']) el(id).replaceChildren();
  for (const id of ['comparison-nodes', 'comparison-buckets', 'comparison-chart']) el(id).replaceChildren();
  for (const id of ['local', 'raw', 'billed']) { put(`comparison-${id}`, '—'); put(`comparison-${id}-exact`, '正在读取…'); }
  pagination(0); put('chart-scale', ''); show('gap-details', false);
  put('quality-notice', '正在读取所选来源…'); put('quality-summary', '正在读取所选来源…');
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
el('unit').addEventListener('change', () => { unit = el<HTMLSelectElement>('unit').value as Unit; if (latestHistory) renderHistory(latestHistory); if (latestProvider) renderProviderHistory(latestProvider); if (latestComparison) renderComparison(latestComparison.local, latestComparison.provider); if (latestStatus) renderStatus(latestStatus); renderBatches(); });
el('previous-page').addEventListener('click', () => { if (page > 0) { page--; void refresh(true); } });
el('next-page').addEventListener('click', () => { if ((page + 1) * pageSize < (latestProvider?.recordCount ?? latestHistory?.recordCount ?? 0)) { page++; void refresh(true); } });
el('comparison-bucket-previous').addEventListener('click', () => {
  if (latestComparison && comparisonBucketPage > 0) { comparisonBucketPage--; renderComparison(latestComparison.local, latestComparison.provider); }
});
el('comparison-bucket-next').addEventListener('click', () => {
  if (latestComparison && (comparisonBucketPage + 1) * pageSize < comparisonBucketCount) { comparisonBucketPage++; renderComparison(latestComparison.local, latestComparison.provider); }
});
el('statistics-source').addEventListener('change', () => {
  chooseSource(el<HTMLSelectElement>('statistics-source').value);
  void refresh(true);
});
el('provider-metric').addEventListener('change', () => {
  providerMetric = el<HTMLSelectElement>('provider-metric').value as 'raw' | 'billed';
  if (latestProvider) renderProviderHistory(latestProvider);
  if (latestComparison) renderComparison(latestComparison.local, latestComparison.provider);
});
el('compare-toggle').addEventListener('change', () => {
  comparison = el<HTMLInputElement>('compare-toggle').checked && statisticsSource !== '';
  chooseSource(statisticsSource); void refresh(true);
});
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
    const response = await fetch(`${statisticsSource ? '/api/provider/export' : '/api/export'}?${query()}`, { credentials: 'same-origin', cache: 'no-store', headers: { 'x-monitor-request': '1' } });
    if (response.status === 401 || response.status === 403) { expireSession(); return; }
    if (!response.ok) { const error = await response.json().catch(() => null) as { error?: string } | null; throw new Error(error?.error || `导出失败（${response.status}）。`); }
    const blob = await response.blob(), url = URL.createObjectURL(blob), link = element('a');
    link.href = url; link.download = `v2rayN-${statisticsSource ? 'provider' : 'local'}-${range.from}-${range.through}.csv`; document.body.append(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
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
    put('preview-details', `来源：${request.source}\n记录：${preview.rowCount.toLocaleString('zh-CN')} 条\n服务商原始量：${formatBytes(preview.rawBytes, unit)}（${exactBytes(preview.rawBytes)}）\n服务商计费量：${formatProviderBytes(preview.billedBytes, unit)}（${exactProviderBytes(preview.billedBytes)}）\n最早记录：${timestamp(preview.firstTime, request.timeZoneId)}\n最晚记录：${timestamp(preview.lastTime, request.timeZoneId)}`);
    el('import-warnings').replaceChildren(...(preview.warnings || []).map(warning => element('li', warning)));
    show('import-preview', true); el<HTMLButtonElement>('commit-import').disabled = preview.duplicate;
    formResult('import-result', preview.duplicate ? '这个文件已经导入，不会重复记账。' : '预览完成。请确认来源与时区后导入。');
  } catch (error) { if (revision === importRevision) formResult('import-result', errorText(error), true); } finally { button.disabled = expired; }
});
el('commit-import').addEventListener('click', async () => {
  if (!importRequest) return;
  const request = importRequest, button = el<HTMLButtonElement>('commit-import'); button.disabled = true;
  try { await post('/api/imports/commit', request); invalidateImport(); formResult('import-result', '导入完成，已保存到独立服务商账本。可在统计来源中选择查看节点历史。'); await loadBatches(); await loadSources(); if (statisticsSource === request.source.trim()) await refresh(true); }
  catch (error) { formResult('import-result', errorText(error), true); } finally { button.disabled = expired; }
});
el('seed-demo').addEventListener('click', async () => {
  const button = el<HTMLButtonElement>('seed-demo'); button.disabled = true;
  try { await post('/api/demo', {}); put('demo-result', '已生成合成记录，请回到本机总览查看。'); await refresh(true); }
  catch (error) { put('demo-result', errorText(error)); } finally { button.disabled = expired; }
});
window.addEventListener('pagehide', () => { refreshController?.abort(); if (refreshTimer) clearTimeout(refreshTimer); });
window.addEventListener('pageshow', event => { if (event.persisted && !expired) void refresh(true); });
let resizePending = false;
window.addEventListener('resize', () => {
  if (resizePending) return;
  resizePending = true;
  requestAnimationFrame(() => {
    resizePending = false;
    if (latestComparison) renderComparison(latestComparison.local, latestComparison.provider);
    else if (latestProvider) renderProviderHistory(latestProvider); else if (latestHistory) renderChart(latestHistory);
  });
});
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
    await loadSources();
    if (activeView === 'provider') await loadBatches();
  } catch (error) { if (!expired) fail(`无法连接本机采集器。${errorText(error)} 请确认托盘仍在运行，并从托盘重新打开页面。`); }
}
void start();
