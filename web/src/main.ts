import './style.css';
import './ui.css';
import { chartSlots, shortLabel } from './lib/chart.ts';
import { compareNames, compareBuckets } from './lib/comparison.ts';
import { displayNodes } from './lib/display.ts';
import { initializeTheme } from './lib/theme.ts';
import { createDetailPopover } from './lib/detail-popover.ts';
import type { ComparisonRow } from './lib/comparison.ts';
import { bytes, exactBytes, formatBytes, percentage, rate } from './lib/format.ts';
import { exactProviderBytes, formatProviderBytes, exactProviderTotal, formatProviderTotal, providerUnits, providerUnitsText } from './lib/provider-format.ts';
import type { Unit } from './lib/format.ts';
import { dateInZone, dateRange, periodRange, shiftAnchor, timestamp } from './lib/ranges.ts';
import type { Bucket, Period, Range } from './lib/ranges.ts';
import type { CollectorStatus, HistoryResult, ImportBatch, ImportPreview, NodeInfo, Settings, ProviderHistory, TrafficDisplay } from './types.ts';

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
initializeTheme(el<HTMLSelectElement>('theme'));
const detailPopover = createDetailPopover(showExactValue);
function discloseGroups(button: HTMLButtonElement, groups: string[], key: string, note?: string): void {
  button.classList.add('has-groups');
  button.removeAttribute('title');
  button.setAttribute('aria-label', `${button.textContent}，策略组：${groups.join('、')}`);
  detailPopover.connect(button, { key, groups, note });
}
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
let probeBasis = 'probe_urltest_member';
let rankingMode: 'ranking' | 'share' = 'ranking';
const comparisonModes: Record<'nodes' | 'buckets', 'list' | 'share'> = { nodes: 'list', buckets: 'list' };
const probeView = (): boolean => activeView === 'probes';
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
function query(localComparison = false, nonZeroRecords = false): string {
  const params = new URLSearchParams({ start: range.start, end: range.end, timeZoneId: settings.timeZoneId, bucket, page: String(page), pageSize: String(pageSize) });
  if (nodeKey) params.set(localComparison ? 'nodeName' : 'nodeKey', nodeKey);
  if (statisticsSource && !localComparison) params.set('source', statisticsSource);
  if (nonZeroRecords) params.set('nonZeroRecords', 'true');
  if (probeView()) params.set('basis', probeBasis);
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
function selectPeriod(next: Period): void {
  period = next;
  if (next !== 'custom') {
    bucket = next === 'day' ? 'hour' : next === 'year' ? 'month' : 'day';
    el<HTMLSelectElement>('bucket').value = bucket;
  }
  selectRange();
}
function renderStatus(status: CollectorStatus): void {
  latestStatus = status;
  const states: Record<string, string> = { collecting: '正在采集', connected: '正在采集', running: '正在采集', ready: '采集器已连接', baseline: '已建立基线', disconnected: '等待连接', waiting: '等待采样', unconfigured: '等待设置目录', unsupported: '当前模式不支持', error: '采集暂不可用', demo: '合成演示模式', needs_setup: '需要设置', needs_statistics: '需要开启统计', setup: '等待设置目录', needs_directory: '等待设置目录', unsupported_platform: '系统暂不支持', unsupported_configuration: '配置暂不支持', unsupported_connection: '连接字段暂不支持', unsupported_core: '内核暂不支持', unsupported_mode: '模式暂不支持', statistics_disabled: '需要开启统计', statistics_unavailable: '统计接口不可用', interface_unavailable: '统计接口不可用', invalid_metrics: '统计响应无效', missing_counters: '统计计数缺失', unavailable: '暂时无法读取', core_not_running: '等待内核运行', ambiguous_instance: '实例归属不明确', unsupported_version: '版本尚未核实', waiting_for_core: '等待内核加载', snapshot_changed: '正在切换配置', missing_database: '缺少配置数据库', unknown_node: '节点归属未知', ambiguous_node: '节点归属不明确', mapping_changed: '等待节点配置同步' };
  const state = status.scope?.isPartial || status.state === 'partial' ? '部分采集' : states[status.state.toLowerCase()] || status.state;
  const message = [status.message || '等待有效采样。', status.scope?.description].filter(Boolean).join(' ');
  put('collector-state', state);
  put('collector-message', message);
  document.querySelector('.collector-strip')?.classList.toggle('is-partial', !!status.scope?.isPartial || status.state === 'partial');
  put('current-node', status.currentNode || '未确定'); el('current-node').title = status.currentNode || '未确定';
  put('live-upload', `↑ ${rate(status.uploadPerSecond, unit)}`); put('live-download', `↓ ${rate(status.downloadPerSecond, unit)}`);
  put('settings-collector-status', `当前状态：${state}。${message} 最近采样：${timestamp(status.lastSample, settings.timeZoneId)}`);
  el('status-dot').className = `status-dot ${status.scope?.isPartial || status.state === 'partial' ? 'partial' : ['collecting', 'connected', 'running', 'ready', 'demo'].includes(status.state.toLowerCase()) ? 'connected' : ['error', 'unsupported'].includes(status.state.toLowerCase()) ? 'error' : ''}`;
  show('demo-notice', status.demo); show('demo-controls', status.demo);
  const probeStates: Record<string, string> = { disabled: '探测历史未启用', waiting: '等待探测采样', unavailable: '探测计数暂不可用', unsupported: '当前核心未支持探测计量', partial: '探测部分采集', available: '探测计数可用' };
  put('probe-state', probeStates[status.probes?.state ?? 'disabled'] ?? '探测状态未知');
  put('probe-message', status.probes?.message ?? '此实例未提供探测能力信息；探测量保持未知。');
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
    else { const download = element('span', '', `bar-part ${provider ? 'bar-provider' : 'bar-download'}`); download.style.height = `${percentage(bytes(point.download), max)}%`; const upload = element('span', '', 'bar-part bar-upload'); upload.style.height = `${percentage(bytes(point.upload), max)}%`; stack.append(download, upload); }
    column.append(stack);
    if (index === 0 || index === slots.length - 1 || (index % labelEvery === 0 && slots.length - 1 - index >= labelEvery / 2))
      column.append(element('span', label, 'chart-label'));
    chart.append(column);
  });
  put('chart-scale', `最高 ${display(max)}`);
}
type HistoryPointInput = Pick<HistoryResult, 'series'>;
function recordTrafficCell(value: string, provider = false): HTMLTableCellElement {
  const cell = element('td', '', `numeric traffic-cell${(provider ? providerUnits(value) : bytes(value)) === 0n ? ' zero' : ''}`);
  cell.title = provider ? exactProviderTotal(value) : exactBytes(value);
  const amount = element('button', provider ? formatProviderTotal(value, unit) : formatBytes(value, unit), 'traffic-amount');
  amount.type = 'button'; amount.title = cell.title;
  amount.addEventListener('click', () => showExactValue(cell.title));
  cell.append(amount);
  return cell;
}
const shareColors = ['#087f79', '#477da5', '#a27038', '#785a9c', '#b15369', '#557a39', '#4d8291', '#717b87'];
type ShareItem = { name: string; total: bigint; colorIndex?: number };
function renderDonut(target: HTMLElement, sorted: ShareItem[], total: bigint, display: (value: bigint) => string,
  precise: (value: bigint) => string, description: string): void {
  const chart = element('div', '', 'share-chart');
  const tooltip = element('span', '', 'share-tooltip');
  tooltip.hidden = true; tooltip.setAttribute('role', 'tooltip');
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 120 120');
  svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label', description);
  let cumulative = 0n, previous = 0;
  sorted.forEach((node, index) => {
    cumulative += node.total;
    const end = percentage(cumulative, total), length = end - previous;
    if (length > 0) {
      const circle = document.createElementNS(svg.namespaceURI, 'circle');
      for (const [name, value] of Object.entries({ cx: '60', cy: '60', r: '44', fill: 'none', stroke: shareColors[(node.colorIndex ?? index) % shareColors.length],
        'stroke-width': '16', pathLength: '100', 'stroke-dasharray': `${length} ${100 - length}`,
        'stroke-dashoffset': String(-previous), transform: 'rotate(-90 60 60)' })) circle.setAttribute(name, value);
      circle.setAttribute('tabindex', '0');
      circle.setAttribute('aria-label', `${node.name || '未归属'} · ${precise(node.total)}`);
      const reveal = (): void => { tooltip.textContent = display(node.total); tooltip.hidden = false; };
      const hide = (): void => { tooltip.hidden = true; };
      circle.addEventListener('pointerenter', reveal); circle.addEventListener('pointerleave', hide);
      circle.addEventListener('focus', reveal); circle.addEventListener('blur', hide);
      svg.append(circle);
    }
    previous = end;
  });
  chart.append(svg, tooltip);
  target.append(chart);
}
type RankingItem = { key: string; name: string; total: bigint; upload?: bigint; download?: bigint; recovered?: bigint; groups?: string[] };
function renderRanking(items: RankingItem[], provider = false): void {
  const ranking = el('ranking'); detailPopover.beforeReplace(ranking); ranking.replaceChildren();
  const metric = (item: RankingItem): bigint => item.total;
  const total = items.reduce((sum, item) => sum + metric(item), 0n);
  const sorted = [...items].sort((a, b) => {
    const x = metric(a), y = metric(b);
    return x > y ? -1 : x < y ? 1 : a.name.localeCompare(b.name);
  });
  const aggregate = probeView() && probeBasis !== 'probe_urltest_member';
  const share = rankingMode === 'share' && !aggregate;
  ranking.classList.toggle('show-shares', share);
  el<HTMLButtonElement>('ranking-share').disabled = aggregate;
  for (const button of document.querySelectorAll<HTMLButtonElement>('[data-ranking-mode]')) {
    const selected = button.dataset.rankingMode === (share ? 'share' : 'ranking');
    button.classList.toggle('selected', selected); button.setAttribute('aria-pressed', String(selected));
  }
  const display = (value: bigint): string => provider ? formatProviderTotal(providerUnitsText(value), unit) : formatBytes(value, unit);
  const precise = (value: bigint): string => provider ? exactProviderTotal(providerUnitsText(value)) : exactBytes(value);
  if (!sorted.length) {
    detailPopover.close();
    ranking.append(element('p', '所选范围尚无节点记录', 'empty-state'));
    return;
  }
  if (share && total === 0n) {
    detailPopover.finishRender();
    ranking.append(element('p', sorted.length ? '所选范围只有零值记录' : '所选范围尚无节点记录', 'empty-state'));
    return;
  }
  if (share) renderDonut(ranking, sorted, total, display, precise,
    '节点流量占比，分母为当前筛选下全部节点的合计；明细见下方列表');
  const max = metric(sorted[0]);
  const list = share ? element('div', '', 'share-legend') : ranking;
  if (share) ranking.append(list);
  sorted.forEach((node, index) => {
    const row = element('div', '', `rank-row ${share ? 'share-mode' : ''}`);
    const heading = element('div', '', 'rank-heading');
    const name = element('button', node.name || '未归属', 'rank-node');
    name.type = 'button'; name.title = node.name || '未归属';
    name.addEventListener('click', () => {
      nodeKey = provider ? node.name : node.key;
      el<HTMLSelectElement>('node-filter').value = nodeKey; page = 0; void refresh(true);
    });
    if (node.groups?.length) discloseGroups(name, node.groups, `rank-groups:${node.key}`);
    const value = metric(node);
    const shareText = value > 0n && percentage(value, total) < .01 ? '<0.01%' : `${percentage(value, total).toFixed(2)}%`;
    if (share) {
      const swatch = element('span', '', 'share-swatch');
      swatch.style.backgroundColor = shareColors[index % shareColors.length];
      heading.append(swatch, name, element('span', shareText, 'rank-index'));
      row.append(heading); list.append(row);
      return;
    }
    heading.append(element('span', String(index + 1).padStart(2, '0'), 'rank-index'), name);
    const recovery = node.recovered ?? 0n;
    const valueBox = element('span', '', 'rank-value-box');
    const marker = element('span', '', `rank-recovery-marker${recovery > 0n ? '' : ' vacant'}`);
    marker.setAttribute('aria-hidden', 'true');
    const known = node.upload !== undefined && node.download !== undefined;
    const amount = element('button', '', 'rank-value');
    amount.type = 'button';
    amount.setAttribute('aria-label', `${display(value)}${recovery > 0n ? '，含恢复量' : ''}，查看流量明细`);
    amount.append(marker, element('span', display(value), 'rank-total'));
    const exact = (known ? `上传 ${precise(node.upload!)}\n下载 ${precise(node.download!)}\n合计 ${precise(value)}` : `合计 ${precise(value)}\n方向未提供`) +
      (recovery > 0n ? `\n其中恢复量 ${exactBytes(recovery)}（已包含在合计中）` : '');
    detailPopover.connect(amount, { key: `${provider ? 'provider' : probeView() ? probeBasis : 'local'}:${node.key}`, total: display(value),
      upload: known ? display(node.upload!) : undefined, download: known ? display(node.download!) : undefined,
      recovery: recovery > 0n ? display(recovery) : undefined, exact });
    valueBox.append(amount);
    heading.append(valueBox);
    const track = element('div', '', 'rank-track');
    const bar = element('div', '', 'rank-bar');
    bar.style.width = `${percentage(value, max)}%`;
    if (known) {
      const down = element('span', '', 'rank-download'), up = element('span', '', 'rank-upload');
      down.style.width = `${percentage(node.download!, value)}%`; up.style.flex = '1';
      bar.append(down, up);
    } else bar.classList.add('rank-neutral');
    track.append(bar);
    row.append(heading, track);
    list.append(row);
  });
  detailPopover.finishRender();
}
function showExactValue(text: string): void {
  put('exact-value-text', text);
  put('copy-value-result', '');
  el<HTMLDialogElement>('exact-value-dialog').showModal();
}
function renderEstimate(display: TrafficDisplay | null | undefined): void {
  const covered = display?.estimateCoveredBytes ?? '0', unknown = display?.estimateUnknownBytes;
  put('billing-value', display?.estimatedBilledBytes == null ? '—' : formatProviderTotal(display.estimatedBilledBytes, unit));
  put('billing-exact', display?.estimatedBilledBytes == null ? '没有可估算的倍率快照' : exactProviderTotal(display.estimatedBilledBytes));
  put('billing-note', display?.estimatedBilledBytes == null ? '未估算，不表示计费为零' :
    `名称倍率估算 · 覆盖 ${formatBytes(covered, unit)}${unknown && bytes(unknown) > 0n ? ` · 未估算 ${formatBytes(unknown, unit)}` : ''} · 非服务商扣费`);
}
function renderHistory(history: HistoryResult): void {
  latestHistory = history;
  latestProvider = null;
  statisticLabels();
  const attribution = el('attribution-note');
  attribution.textContent = probeView()
    ? '探测与业务分别记账。成员和总量分别查看，不重复相加。'
    : history.basis === 'local_proxy'
    ? '包含当前账本中的 sing-box 成员和 Xray／旧 proxy 历史，切换核心不隐藏记录。同名记录仍按原身份分账；直连、业务汇总和独立探测不重复计入。'
    : '按已核实的节点或出站身份分账，名称和策略组前缀仅用于展示。';
  const up = bytes(history.upload), down = bytes(history.download), total = up + down;
  renderEstimate(history.display);
  if (history.displayMetadataEnabled === false) put('billing-exact', '倍率快照未启用，原始量正常记录');
  put('total-value', formatBytes(total, unit)); put('total-exact', exactBytes(total));
  put('download-value', formatBytes(down, unit)); put('download-exact', exactBytes(down));
  put('upload-value', formatBytes(up, unit)); put('upload-exact', exactBytes(up));
  if (history.nodes.length === 0) {
    for (const id of ['total-value', 'upload-value', 'download-value']) put(id, '—');
    for (const id of ['total-exact', 'upload-exact', 'download-exact']) put(id, history.recordCount ? '所选范围无已知量' : '所选范围无记录');
  }
  const recovered = bytes(history.recoveredUpload ?? '0') + bytes(history.recoveredDownload ?? '0');
  const partial = bytes(history.partialUpload ?? '0') + bytes(history.partialDownload ?? '0');
  const unbucketed = bytes(history.unbucketedUpload ?? '0') + bytes(history.unbucketedDownload ?? '0');
  const quality = ['首次采样建立基线，接入前累计不回填；缺失记录不等于零流量。'];
  if (probeView()) {
    quality.push('当前仅为所选 URLTest 探测维度，不与业务流量或服务商计费相加。');
    put('probe-coverage', history.earliestRecord
      ? `最早保存区间：${dateInZone(new Date(history.earliestRecord), settings.timeZoneId)} ${timestamp(history.earliestRecord, settings.timeZoneId).split(' ').at(-1)}（按分钟显示，不是精确的首次基线）。`
      : '所选维度及节点尚无已保存的探测区间；首次有效采样只建立基线，不回填此前累计。');
  }
  if (history.basis === 'actual_member')
    quality.push('当前为实际成员维度，直连和汇总分别保存。');
  if ((history.gaps ?? []).some(gap => gap.origin === 'legacy' && gap.isOpen))
    quality.push('本范围仍有历史遗留开放缺口。');
  if (history.partialCoverage || (history.gaps ?? []).some(gap => gap.reason === 'uncollected_outbounds') || history.records.some(record => record.partialCoverage))
    quality.push('本范围包含其他出站未采集的区间；未覆盖范围的流量未知，合计仅包含已核实的计量范围。');
  if (recovered > 0n) quality.push(`合计及节点排行已包含恢复量 ${formatBytes(recovered, unit)}（${exactBytes(recovered)}）。`);
  if (unbucketed > 0n) quality.push(`其中 ${formatBytes(unbucketed, unit)} 跨越多个时间桶，已计入合计，尚未分配到当前趋势图。`);
  if (partial > 0n) quality.push(`另有部分重叠的恢复区间，共 ${formatBytes(partial, unit)}（${exactBytes(partial)}）；本范围内用量未知，未计入合计，不能跨日期相加。`);
  if (history.gapCount) quality.push(`本范围有 ${history.gapCount} 段历史采集间断，当前采集状态见上方。`);
  put('quality-notice', quality.join(' '));
  put('quality-summary', recovered > 0n ? `已知合计包含 ${formatBytes(recovered, unit)} 恢复量` : history.gapCount ? `本范围有 ${history.gapCount} 段历史采集间断` : '统计口径与数据说明');
  const details = el('gap-list'); details.replaceChildren();
  const reasons: Record<string, string> = { uncollected_outbounds: '其他出站未采集，流量未知', long_interval: '采样间隔较长', ambiguous_instance: '实例归属核对', listener_unverified: '监听端口核对', snapshot_changed: '配置快照变化', identity_change: '会话或节点变化', counter_reset: '计数重置', mapping_changed: '节点映射变化' };
  for (const gap of history.gaps ?? []) details.append(element('li', `${timestamp(gap.start, settings.timeZoneId)} → ${timestamp(gap.end, settings.timeZoneId)} · ${reasons[gap.reason] ?? gap.reason} · ${gap.isOpen ? '尚未结束' : '已结束（不代表流量全部恢复）'}`));
  show('gap-details', history.gapCount > 0);
  put('gap-summary', `查看历史间断与覆盖缺口（${history.gapCount} 段）`);
  put('trend-subtitle', `${({ hour: '小时', day: '日', week: '周', month: '月', year: '年' } as const)[bucket]}分桶 · ${settings.timeZoneId}`);
  renderChart(history);
  const nodes = displayNodes(history.nodes);
  put('node-count', String(nodes.length));
  renderRanking(nodes.map(node => ({ key: node.key, name: node.name, groups: node.display?.groups, total: bytes(node.upload) + bytes(node.download),
    upload: bytes(node.upload), download: bytes(node.download), recovered: bytes(node.recoveredUpload ?? '0') + bytes(node.recoveredDownload ?? '0') })));
  const records = el('records'); detailPopover.beforeReplace(records); records.replaceChildren();
  const labels: Record<string, string> = { exact: '已记录', normal: '已记录', measured: '已记录', approximate: '分时近似', 'recovered-contained': '恢复量 · 已计入', 'recovered-partial': '部分重叠 · 未计入', 'recovered-unallocated': '恢复未分时', estimated: '分时近似', interpolated: '分时近似', apportioned: '分时近似', unallocated: '恢复未分时', recovered_unallocated: '恢复未分时', unknown: '未归属', gap: '采集缺口', synthetic: '合成演示' };
  if (!history.records.length) { const row = element('tr'), cell = element('td', '这个区间没有非零采集明细。零字节记录已隐藏，采集缺口仍单独显示。', 'empty-state'); cell.colSpan = 9; row.append(cell); records.append(row); }
  for (const record of history.records) {
    const row = element('tr');
    const time = element('td', timestamp(record.start, settings.timeZoneId)); time.title = `${record.start} → ${record.end}`;
    const name = element('td', record.display?.originalName ?? record.nodeName ?? '未归属', 'node-cell'); name.title = record.nodeName || '未归属';
    if (record.display?.groups.length) {
      const label = element('button', name.textContent!, 'node-name'); label.type = 'button';
      discloseGroups(label, record.display.groups, `record-groups:${JSON.stringify([record.nodeKey, record.start, record.end, record.upload, record.download, record.display])}`);
      name.removeAttribute('title'); name.replaceChildren(label);
    }
    const download = recordTrafficCell(record.download);
    const upload = recordTrafficCell(record.upload);
    const approximate = ['approximate', 'estimated', 'interpolated', 'apportioned'].includes(record.quality);
    const qualityCell = element('td'); const label = element('span', (labels[record.quality] || record.quality) + (record.partialCoverage ? ' · 部分出站' : ''), `quality-pill ${record.partialCoverage ? 'warning' : ['exact', 'normal', 'measured'].includes(record.quality) ? '' : approximate ? 'approximate' : 'warning'}`);
    if (approximate) label.title = '区间流量按分钟分配，分时用量为近似值；原始总量保持不变。';
    if (record.quality === 'recovered-partial') label.title = '恢复区间仅与当前查询范围部分重叠，范围内用量未知，未计入本范围合计。';
    qualityCell.append(label);
    const multiplier = element('td', record.display?.rate ?? '—', 'numeric');
    multiplier.title = record.display?.rate ? '采集时节点名或明确倍率组的标记，仅用于估算' : '此历史记录没有保存可核实的倍率，不按当前名称推算';
    const estimated = record.display?.estimatedBilledBytes == null ? element('td', '—', 'numeric') :
      recordTrafficCell(record.display.estimatedBilledBytes, true);
    row.append(time, name, element('td', '无'), upload, download,
      recordTrafficCell((bytes(record.upload) + bytes(record.download)).toString()), multiplier,
      estimated, qualityCell); records.append(row);
  }
  pagination(history.recordCount, true);
  detailPopover.finishRender();
  el('summary').setAttribute('aria-busy', 'false');
}
function pagination(count: number, nonZeroRecords = false): void {
  const pages = Math.max(1, Math.ceil(count / pageSize));
  put('record-count', `${count.toLocaleString('zh-CN')} 条${nonZeroRecords ? '非零' : ''}记录`); put('page-label', `第 ${page + 1} / ${pages} 页 · 每页 ${pageSize} 条`);
  el<HTMLButtonElement>('previous-page').disabled = page === 0;
  el<HTMLButtonElement>('next-page').disabled = (page + 1) * pageSize >= count;
}
function statisticLabels(): void {
  const provider = statisticsSource !== '';
  const probe = probeView();
  show('source-controls', !probe); show('probe-controls', probe); show('probe-notice', probe);
  put('statistics-title', probe ? '探测流量' : '流量统计');
  put('statistics-eyebrow', probe ? 'URLTEST PROBES' : 'TRAFFIC OVERVIEW');
  put('statistics-subtitle', probe ? '独立查看测活与延迟探测，按实际成员、日期和已知范围排查。' : '按来源和节点查看历史，清楚区分已知流量与采集间断。');
  el('records').closest('.records-panel')?.classList.toggle('local-records', !provider);
  const metric = providerMetric === 'raw' ? '服务商原始量' : '服务商计费量';
  put('provider-metric-caption', comparison ? '对比口径' : '排行与趋势');
  el<HTMLSelectElement>('provider-metric').options[0]!.text = comparison ? '原始量对比' : '服务商原始量';
  el<HTMLSelectElement>('provider-metric').options[1]!.text = comparison ? '计费量对比' : '服务商计费量';
  show('provider-metric-control', provider); show('upload-legend', !provider);
  show('compare-control', provider); show('comparison-view', comparison);
  el('overview-view').classList.toggle('comparison-mode', comparison);
  show('export-button', !comparison);
  show('attribution-note', !provider);
  el('summary').classList.toggle('provider-summary', provider);
  show('raw-card', true); show('billed-card', true);
  put('source-caption', provider ? `服务商：${statisticsSource} · 与本机分别统计` : '本机内核统计 · 含可确定范围的恢复量');
  put('total-title', provider ? '原始量' : probe ? '所选维度探测量' : '区间已知合计');
  put('total-note', provider ? '仅当前来源 · 不与本机流量相加' : probe ? '上传 + 下载 · 与业务分别保存' : '上传 + 下载 · 包含区间内恢复量');
  put('download-title', '下载'); put('upload-title', '上传');
  put('download-note', provider ? '不从原始总量推算' : probe ? '从探测目标接收的流字节' : '从代理节点接收');
  put('upload-note', provider ? '不从原始总量推算' : probe ? '发往探测目标的流字节' : '通过代理节点发送');
  put('billing-title', provider ? '计费量' : '估算计费量');
  put('billing-value', '—'); put('billing-exact', provider ? '等待记录' : '历史倍率未核实');
  put('billing-note', provider ? 'CSV 精确值 · 不重新乘倍率' : '未估算，不表示计费为零');
  put('download-legend', provider ? metric : '下载');
  put('ranking-caption', provider ? `按${metric}排序` : '按区间已知合计排序');
  put('records-heading', provider ? '服务商记录' : probe ? '探测明细' : '采集明细');
  put('records-caption', provider ? '独立来源 · 方向、设备和 IP 未提供 · 点击数值查看精确值' : '仅显示非零明细 · 点击数值查看精确值 · 仅按采集时倍率快照估算，旧记录保持未知');
  put('billing-column', provider ? '计费量' : '估算计费量');
}
function comparisonValues(row: ComparisonRow): [string | null, string | null] {
  return providerMetric === 'raw' ? [row.local, row.raw] : [row.localEstimate, row.billed];
}
function comparisonCoverage(row: Pick<ComparisonRow, 'estimateCoveredBytes' | 'estimateUnknownBytes'>): string {
  return `已估算覆盖原始量 ${exactBytes(row.estimateCoveredBytes)}；未估算原始量 ${exactBytes(row.estimateUnknownBytes)}`;
}
function renderComparisonShares(section: 'nodes' | 'buckets', rows: ComparisonRow[]): void {
  const target = el(`comparison-${section}-shares`);
  target.replaceChildren();
  const share = comparisonModes[section] === 'share';
  show(`comparison-${section}-list`, !share); show(`comparison-${section}-shares`, share);
  for (const button of document.querySelectorAll<HTMLButtonElement>(`[data-comparison-section="${section}"]`)) {
    const selected = button.dataset.comparisonMode === comparisonModes[section];
    button.classList.toggle('selected', selected); button.setAttribute('aria-pressed', String(selected));
  }
  if (!share) return;
  const category = section === 'nodes' ? '节点' : '时间桶';
  for (const source of ['local', 'provider'] as const) {
    const title = source === 'local' ? providerMetric === 'raw' ? '本机原始量' : '本机估算计费量' : providerMetric === 'raw' ? '服务商原始量' : '服务商计费量';
    const group = element('section', '', 'comparison-share-source'); group.dataset.source = source;
    group.append(element('h3', title));
    const note = (section === 'nodes' ? '分母为当前筛选下此来源的全部节点合计。' : '分母为当前筛选下此来源的全部已记录时间桶合计；不受翻页影响，不含未分时恢复量。') +
      (source === 'local' && providerMetric === 'billed' ? ' 仅包含有倍率快照的估算量，未估算部分不参与占比。' : '');
    group.append(element('p', note, 'comparison-share-note'));
    const content = element('div', '', 'ranking show-shares');
    const items = rows.flatMap((row, colorIndex) => {
      const value = comparisonValues(row)[source === 'local' ? 0 : 1];
      return value === null ? [] : [{ name: row.name, total: providerUnits(value), colorIndex }];
    }).sort((a, b) => a.total > b.total ? -1 : a.total < b.total ? 1 : a.name.localeCompare(b.name));
    const total = items.reduce((sum, item) => sum + item.total, 0n);
    const display = (value: bigint): string => formatProviderTotal(providerUnitsText(value), unit);
    const precise = (value: bigint): string => exactProviderTotal(providerUnitsText(value));
    if (!items.length || total === 0n) {
      content.classList.remove('show-shares');
      content.append(element('p', !items.length ? source === 'local' && providerMetric === 'billed' && rows.some(row => row.local !== null)
        ? '没有可估算的倍率快照，不能视为零计费量' : '此来源无记录，不能视为零流量' : '此来源只有零值记录，暂无占比', 'empty-state'));
    } else {
      renderDonut(content, items, total, display, precise, `${title}的${category}占比。${note} 缺失记录不补零，与另一来源分别统计。`);
      const legend = element('div', '', 'share-legend');
      for (const item of items) {
        const row = element('div', '', 'rank-row share-mode'), heading = element('div', '', 'rank-heading');
        const swatch = element('span', '', 'share-swatch'); swatch.style.backgroundColor = shareColors[item.colorIndex % shareColors.length];
        const name = element('button', item.name, 'rank-node'); name.type = 'button';
        name.title = `${item.name} · ${precise(item.total)}`;
        name.setAttribute('aria-label', name.title);
        name.addEventListener('click', () => showExactValue(name.title));
        const percent = percentage(item.total, total);
        heading.append(swatch, name, element('span', item.total > 0n && percent < .01 ? '<0.01%' : `${percent.toFixed(2)}%`, 'rank-index'));
        row.append(heading); legend.append(row);
      }
      content.append(legend);
    }
    group.append(content); target.append(group);
  }
}
function renderComparison(local: HistoryResult, provider: ProviderHistory): void {
  if (latestComparison?.local !== local || latestComparison?.provider !== provider) comparisonBucketPage = 0;
  latestComparison = { local, provider }; latestProvider = null; latestHistory = null; statisticLabels();
  const card = (id: string, value: string | null, empty = '所选范围无记录'): void => {
    put(`comparison-${id}`, value === null ? '—' : formatProviderTotal(value, unit));
    put(`comparison-${id}-exact`, value === null ? empty : exactProviderTotal(value));
  };
  const billed = providerMetric === 'billed';
  put('comparison-local-title', billed ? '本机估算计费量' : '本机原始量');
  put('comparison-local-note', billed ? '仅采集时倍率快照覆盖部分' : '上传 + 下载 · 当前范围');
  card('local', billed ? local.display?.estimatedBilledBytes ?? null : local.nodes.length ? (bytes(local.upload) + bytes(local.download)).toString() : null,
    billed ? '没有可估算的倍率快照' : '所选范围无已知量');
  card('raw', provider.recordCount ? provider.rawBytes : null); card('billed', provider.recordCount ? provider.billedBytes : null);
  show('comparison-estimate-note', billed);
  put('comparison-estimate-note', `${comparisonCoverage({
    estimateCoveredBytes: local.display?.estimateCoveredBytes ?? '0',
    estimateUnknownBytes: local.display?.estimateUnknownBytes ?? (bytes(local.upload) + bytes(local.download)).toString()
  })}。仅对已知倍率片段估算，不等同服务商实际扣费。`);
  show('comparison-local-legend', billed); show('comparison-upload-legend', !billed); show('comparison-download-legend', !billed);
  const unbucketed = bytes(local.unbucketedUpload ?? '0') + bytes(local.unbucketedDownload ?? '0');
  const partial = bytes(local.partialUpload ?? '0') + bytes(local.partialDownload ?? '0');
  put('comparison-recovery', `本机历史间断 ${local.gapCount} 段。已计入总量但未分配到当前时间桶：${exactBytes(unbucketed)}；部分重叠恢复区间（未计入）：${exactBytes(partial)}。`);
  put('quality-summary', '同名汇总对照 · 两边数值分别计量');
  put('quality-notice', '名称相同仅用于展示对照，不证明节点身份相同；服务商记录范围可能不同。缺口不是零，不把差额归因于设备或漏计。');
  show('gap-details', false);
  const table = (id: string, rows: ComparisonRow[], scaleRows = rows): void => {
    const body = el(id); detailPopover.beforeReplace(body); body.replaceChildren();
    const maximum = scaleRows.reduce((max, row) => {
      for (const value of comparisonValues(row))
        if (value !== null && providerUnits(value) > max) max = providerUnits(value);
      return max;
    }, 0n);
    for (const row of rows) {
      comparisonValues(row).forEach((value, index) => {
        const unknownEstimate = index === 0 && billed && row.local !== null && value === null;
        const tr = element('tr', '', `comparison-pair ${value === null ? 'missing' : ''}`);
        if (index === 0) {
          const name = element('td', row.name, 'comparison-name'); name.rowSpan = 2;
          if (id === 'comparison-nodes') {
            const groups = [...new Set(local.nodes.filter(node => node.display?.originalName === row.name).flatMap(node => node.display?.groups ?? []))];
            if (groups.length) {
              const label = element('button', row.name, 'node-name'); label.type = 'button';
              discloseGroups(label, groups, `comparison-groups:${row.name}`, '来自本机记录的策略组；同名不表示身份已验证。');
              name.replaceChildren(label);
            }
          }
          tr.append(name);
        }
        tr.append(element('td', index === 0 ? billed ? '本机估算' : '本机' : '服务商'));
        const cell = element('td', '', 'paired-traffic');
        const amount = element('button', unknownEstimate ? '—' : formatProviderTotal(value ?? '0', unit), 'paired-value');
        amount.type = 'button';
        amount.title = unknownEstimate ? `没有可估算的倍率快照\n${comparisonCoverage(row)}` :
          value === null ? '此来源没有记录，显示零仅为占位，不代表实际零流量' :
          index === 0 ? billed ? `估算计费量 ${exactProviderTotal(value)}\n${comparisonCoverage(row)}\n非服务商实际扣费` :
            `上传 ${exactBytes(row.upload!)}\n下载 ${exactBytes(row.download!)}\n合计 ${exactProviderTotal(value)}` :
            `${providerMetric === 'raw' ? '原始量' : '计费量'} ${exactProviderTotal(value)}\n方向未提供`;
        amount.setAttribute('aria-label', amount.title);
        detailPopover.connect(amount, { key: `comparison:${id}:${index}:${row.name}`, total: amount.textContent!,
          upload: index === 0 && !billed && value !== null ? formatBytes(row.upload!, unit) : undefined,
          download: index === 0 && !billed && value !== null ? formatBytes(row.download!, unit) : undefined,
          exact: amount.title, note: value === null ? amount.title : index === 0 ?
            billed ? comparisonCoverage(row) + '。仅倍率已知部分，不等同服务商实际扣费。' : '本机已知量，与服务商分别计量。' : '服务商未提供上下行方向，与本机分别计量。' });
        const track = element('div', '', 'paired-track'), bar = element('div', '', 'paired-bar');
        bar.style.width = `${percentage(value === null ? 0n : providerUnits(value), maximum)}%`;
        if (index === 0 && !billed && value !== null) {
          const down = element('span', '', 'rank-download'), up = element('span', '', 'rank-upload');
          down.style.width = `${percentage(bytes(row.download!), bytes(value))}%`; up.style.flex = '1';
          bar.append(down, up);
        } else bar.classList.add(index === 0 ? 'local-estimate' : 'rank-neutral');
        track.append(bar); cell.append(amount, track); tr.append(cell); body.append(tr);
      });
    }
    if (!rows.length) { const row = element('tr'), cell = element('td', '所选范围没有可对照记录', 'empty-state'); cell.colSpan = 3; row.append(cell); body.append(row); }
  };
  const names = compareNames(local, provider);
  const sortSource = el<HTMLSelectElement>('comparison-node-sort').value;
  const valueForSort = (row: ComparisonRow): string | null => comparisonValues(row)[sortSource === 'local' ? 0 : 1];
  const sortedNames = [...names].sort((a, b) => {
    const x = valueForSort(a), y = valueForSort(b);
    if (x === null || y === null) return x === y ? a.name.localeCompare(b.name) : x === null ? 1 : -1;
    return providerUnits(x) > providerUnits(y) ? -1 : providerUnits(x) < providerUnits(y) ? 1 : a.name.localeCompare(b.name);
  });
  table('comparison-nodes', sortedNames);
  renderComparisonShares('nodes', names);
  const buckets = compareBuckets(local, provider);
  renderComparisonShares('buckets', buckets);
  comparisonBucketCount = buckets.length;
  const pages = Math.max(1, Math.ceil(buckets.length / pageSize));
  comparisonBucketPage = Math.min(comparisonBucketPage, pages - 1);
  table('comparison-buckets', buckets.slice(comparisonBucketPage * pageSize, (comparisonBucketPage + 1) * pageSize), buckets);
  detailPopover.finishRender();
  put('comparison-bucket-page', `共 ${buckets.length.toLocaleString('zh-CN')} 个已记录桶 · 第 ${comparisonBucketPage + 1} / ${pages} 页 · 每页 ${pageSize} 个`);
  el<HTMLButtonElement>('comparison-bucket-previous').disabled = comparisonBucketPage === 0;
  el<HTMLButtonElement>('comparison-bucket-next').disabled = comparisonBucketPage + 1 >= pages;
  put('comparison-trend-caption', `${({ hour: '小时', day: '日', week: '周一开始的周', month: '月', year: '年' })[bucket]} · ${settings.timeZoneId}`);
  put('comparison-metric-label', providerMetric === 'raw' ? '服务商原始量' : '服务商计费量');
  const chart = el('comparison-chart'); chart.replaceChildren(); chart.classList.remove('empty');
  const slots = chartSlots(buckets.map(row => ({ label: row.name, upload: '0', download: '0' })), range, bucket, settings.timeZoneId,
    Math.max(31, Math.floor((chart.clientWidth || 600) / 12)));
  if (!buckets.length || !slots) {
    chart.classList.add('empty'); chart.append(element('p', !buckets.length ? '没有已记录的时间桶，请调整范围或导入 CSV。' : '当前宽度无法清楚展示全部时间桶。\n请缩小日期范围或选择周/月；完整总量保留，分桶表可翻页查看。'));
  } else {
    const byLabel = new Map(buckets.map(row => [row.name, row]));
    const scale = buckets.reduce((max, row) => {
      for (const value of comparisonValues(row)) {
        if (value !== null && providerUnits(value) > max) max = providerUnits(value);
      }
      return max;
    }, 0n);
    const every = Math.max(1, Math.ceil(slots.length / Math.max(2, Math.floor((chart.clientWidth || 600) / 65))));
    slots.forEach(({ label }, index) => {
      const row = byLabel.get(label), column = element('div', '', 'chart-column comparison-column'), bars = element('div', '', 'comparison-bars');
      const values = row ? comparisonValues(row) : [null, null];
      column.title = `${label}\n${billed ? '本机估算' : '本机'}：${values[0] == null ? billed && row?.local !== null ? '倍率未知或无记录' : '无记录' : exactProviderTotal(values[0])}\n服务商：${values[1] == null ? '无记录' : exactProviderTotal(values[1])}`;
      if (billed && row?.local !== null && row) column.title += `\n${comparisonCoverage(row)}`;
      values.forEach((value, i) => {
        const bar = element('span', '', value === null ? 'chart-missing' : providerUnits(value) === 0n ? 'chart-zero' : i === 0 ? 'comparison-bar local' : 'comparison-bar provider');
        if (value !== null && providerUnits(value) > 0n) bar.style.height = `${percentage(providerUnits(value), scale)}%`;
        if (i === 0 && !billed && value !== null && bytes(value) > 0n && row) {
          const down = element('span', '', 'rank-download'), up = element('span', '', 'rank-upload');
          down.style.height = `${percentage(bytes(row.download!), bytes(value))}%`;
          up.style.flex = '1'; bar.append(down, up);
          column.title += `\n本机上传 ${exactBytes(row.upload!)} · 下载 ${exactBytes(row.download!)}`;
        }
        if (i === 0 && billed) bar.classList.add('local-estimate');
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
  put('total-value', formatBytes(history.rawBytes, unit)); put('total-exact', exactBytes(history.rawBytes));
  put('download-value', '—'); put('download-exact', 'CSV 未提供方向');
  put('upload-value', '—'); put('upload-exact', 'CSV 未提供方向');
  put('billing-value', formatProviderTotal(history.billedBytes, unit)); put('billing-exact', exactProviderTotal(history.billedBytes));
  if (history.recordCount === 0) {
    for (const id of ['total-value', 'billing-value']) put(id, '—');
    for (const id of ['total-exact', 'billing-exact']) put(id, '所选范围无记录');
  }
  put('quality-notice', '仅统计所选服务商的已导入记录，不代表完整使用量。上下行方向、设备和 IP 未知；不与本机流量相加。');
  put('quality-summary', '服务商独立记录 · 与本机分别统计');
  show('gap-details', false);
  put('trend-subtitle', `${providerMetric === 'raw' ? '原始量' : '计费量'} · ${settings.timeZoneId}`);
  renderChart({ series: history.series.map(point => ({ label: point.label, upload: '0', download: providerUnits(metric(point)).toString() })) }, true);
  put('node-count', String(history.nodes.length));
  renderRanking(history.nodes.map(node => ({ key: node.name, name: node.name, total: providerUnits(metric(node)) })), true);
  const records = el('records'); records.replaceChildren();
  if (!history.records.length) { const row = element('tr'), cell = element('td', '这个范围没有已导入记录，请调整日期或来源。', 'empty-state'); cell.colSpan = 9; row.append(cell); records.append(row); }
  for (const record of history.records) {
    const row = element('tr'), name = element('td', record.nodeName, 'node-cell'); name.title = record.nodeName;
    const quality = element('td'); quality.append(element('span', '服务商导入', 'quality-pill'));
    row.append(element('td', timestamp(record.at, settings.timeZoneId)), name, element('td', '无'),
      element('td', '—', 'numeric'), element('td', '—', 'numeric'), recordTrafficCell(record.rawBytes),
      element('td', record.rate, 'numeric'), recordTrafficCell(record.billedBytes, true), quality);
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
  if (force) { detailPopover.close(); show('global-error', false); el('summary').setAttribute('aria-busy', 'true'); put('refresh-note', '正在更新…'); }
  try {
    let probeStatus: CollectorStatus | undefined;
    if (probeView()) {
      probeStatus = await api<CollectorStatus>('/api/status', { signal: controller.signal });
      if (controller.signal.aborted || refreshController !== controller) return;
      renderStatus(probeStatus);
      if (!probeStatus.probes?.historyEnabled) {
        latestHistory = null; statisticLabels();
        for (const id of ['total-value', 'upload-value', 'download-value', 'node-count']) put(id, '—');
        for (const id of ['total-exact', 'upload-exact', 'download-exact']) put(id, '探测量未知');
        for (const id of ['records', 'ranking', 'chart']) el(id).replaceChildren();
        detailPopover.finishRender();
        put('probe-coverage', '尚未启用探测历史；现有业务历史继续保留。');
        put('quality-notice', '探测量未知，不能记为零。首次接入建立基线，不能追记此前探测流量。');
        put('quality-summary', '探测历史未启用');
        put('attribution-note', '探测量与业务分别保存。当前实例未启用探测历史，不以业务量推算探测量。');
        el('chart').classList.add('empty');
        el('chart').append(element('p', '探测历史尚未启用，流量未知。'));
        el('chart').setAttribute('aria-label', '探测量未知');
        show('gap-details', false); pagination(0);
        el('summary').setAttribute('aria-busy', 'false');
        el<HTMLButtonElement>('export-button').disabled = true;
        put('refresh-note', '探测未启用 · 5 秒检查');
        return;
      }
      el<HTMLButtonElement>('export-button').disabled = false;
    }
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
      const names = [...new Set([...localNodes, ...providerNodes, ...(local.comparisonNodes ?? [])].map(node => node.name))].sort();
      renderNodes(names.map(name => ({ key: name, name })));
      renderComparison(local, provider);
      put('refresh-note', `已更新 ${timestamp(new Date().toISOString(), settings.timeZoneId).split(' ').at(-1)} · 对照按操作刷新`);
      return;
    }
    const results = await Promise.allSettled([
      probeStatus ? Promise.resolve(probeStatus) : api<CollectorStatus>('/api/status', { signal: controller.signal }),
      api<NodeInfo[]>(statisticsSource ? `/api/provider/nodes?source=${encodeURIComponent(statisticsSource)}` : `/api/nodes${probeView() ? `?basis=${probeBasis}` : ''}`, { signal: controller.signal }),
      api<HistoryResult | ProviderHistory>(`${statisticsSource ? '/api/provider/history' : '/api/history'}?${query(false, !statisticsSource)}`, { signal: controller.signal })
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
  detailPopover.close();
  const wasProbe = probeView();
  activeView = view;
  for (const id of ['overview', 'settings', 'provider']) show(`${id}-view`, id === view || (id === 'overview' && probeView()));
  for (const nav of document.querySelectorAll<HTMLButtonElement>('.nav-item')) { const active = nav.dataset.view === view; nav.classList.toggle('active', active); if (active) nav.setAttribute('aria-current', 'page'); else nav.removeAttribute('aria-current'); }
  put('page-name', ({ overview: '流量统计', probes: '探测流量', settings: '采集与设置', provider: '导入记录' } as Record<string, string>)[view]);
  if (wasProbe !== probeView()) {
    chooseSource(probeView() ? '' : statisticsSource);
    el<HTMLButtonElement>('export-button').disabled = expired || !ready;
    if (ready) void refresh(true);
  }
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
    const amount = element('button', formatProviderBytes(batch.billedBytes, unit), 'traffic-amount');
    amount.type = 'button'; amount.title = exactProviderBytes(batch.billedBytes);
    amount.addEventListener('click', () => showExactValue(amount.title));
    billed.append(amount, element('small', exactProviderBytes(batch.billedBytes), 'sr-only'));
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
  const options: [Bucket, string][] = comparison ? [['hour', '按小时'], ['day', '按日'], ['week', '按周'], ['month', '按月']] :
    [['hour', '按小时'], ['day', '按日'], ['week', '按周'], ['month', '按月'], ['year', '按年']];
  if (!options.some(([value]) => value === bucket)) bucket = period === 'day' ? 'hour' : period === 'year' ? 'month' : 'day';
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
  for (const id of ['comparison-nodes', 'comparison-buckets', 'comparison-chart', 'comparison-nodes-shares', 'comparison-buckets-shares']) el(id).replaceChildren();
  for (const id of ['local', 'raw', 'billed']) { put(`comparison-${id}`, '—'); put(`comparison-${id}-exact`, '正在读取…'); }
  pagination(0); put('chart-scale', ''); show('gap-details', false);
  put('quality-notice', '正在读取所选来源…'); put('quality-summary', '正在读取所选来源…');
  if (probeView()) put('probe-coverage', '正在读取所选探测维度及节点的历史起点…');
}
function renderImportPreview(preview: ImportPreview, source: string, zone: string): void {
  const target = el('preview-details'); target.replaceChildren();
  target.append(element('p', `来源：${source}`, 'preview-source'));
  const counts = element('div', '', 'preview-counts');
  for (const [label, count] of [['文件记录：', preview.rowCount], ['本次新增：', preview.newRowCount], ['跳过已存在：', preview.skippedRowCount]] as const) {
    const item = element('div', '', 'preview-count');
    item.append(element('span', label), element('strong', `${count.toLocaleString('zh-CN')} 条`)); counts.append(item);
  }
  const meta = element('dl', '', 'preview-meta');
  for (const [label, formatted, precise] of [
    ['新增原始量：', formatBytes(preview.newRawBytes, unit), exactBytes(preview.newRawBytes)],
    ['新增计费量：', formatProviderBytes(preview.newBilledBytes, unit), exactProviderBytes(preview.newBilledBytes)],
    ['文件原始量：', formatBytes(preview.rawBytes, unit), exactBytes(preview.rawBytes)],
    ['文件计费量：', formatProviderBytes(preview.billedBytes, unit), exactProviderBytes(preview.billedBytes)],
    ['最早记录：', timestamp(preview.firstTime, zone), ''], ['最晚记录：', timestamp(preview.lastTime, zone), '']]) {
    const description = element('dd');
    if (precise) {
      const amount = element('button', formatted, 'traffic-amount'); amount.type = 'button'; amount.title = precise;
      amount.addEventListener('click', () => showExactValue(precise));
      description.append(amount, element('span', precise, 'sr-only'));
    } else description.textContent = formatted;
    meta.append(element('dt', label), description);
  }
  target.append(counts, meta);
  show('import-preview-placeholder', false);
}
function invalidateImport(): void { importRevision++; importRequest = null; show('import-preview', false); show('import-preview-placeholder', true); put('import-result', ''); }
function formResult(id: string, value: string, isError = false): void { put(id, value); el(id).classList.toggle('error', isError); }

for (const button of document.querySelectorAll<HTMLButtonElement>('[data-view]')) button.addEventListener('click', () => navigate(button.dataset.view!));
document.querySelector('.brand')?.addEventListener('click', event => { event.preventDefault(); navigate('overview'); });
for (const button of document.querySelectorAll<HTMLButtonElement>('[data-period]')) button.addEventListener('click', () => selectPeriod(button.dataset.period as Period));
el('custom-range').addEventListener('submit', event => { event.preventDefault(); selectRange(); });
el('previous-period').addEventListener('click', () => { if (period !== 'custom') { anchor = shiftAnchor(period, anchor, -1); selectRange(); } });
el('next-period').addEventListener('click', () => { if (period !== 'custom') { anchor = shiftAnchor(period, anchor, 1); selectRange(); } });
el('today-button').addEventListener('click', () => { anchor = dateInZone(new Date(), settings.timeZoneId); if (period === 'custom') selectPeriod('day'); else selectRange(); });
el('node-filter').addEventListener('change', () => { nodeKey = el<HTMLSelectElement>('node-filter').value; page = 0; void refresh(true); });
el('bucket').addEventListener('change', () => { bucket = el<HTMLSelectElement>('bucket').value as Bucket; page = 0; void refresh(true); });
el('unit').addEventListener('change', () => { detailPopover.close(); unit = el<HTMLSelectElement>('unit').value as Unit; if (latestHistory) renderHistory(latestHistory); if (latestProvider) renderProviderHistory(latestProvider); if (latestComparison) renderComparison(latestComparison.local, latestComparison.provider); if (latestStatus) renderStatus(latestStatus); renderBatches(); });
el('previous-page').addEventListener('click', () => { if (page > 0) { page--; void refresh(true); } });
el('next-page').addEventListener('click', () => { if ((page + 1) * pageSize < (latestProvider?.recordCount ?? latestHistory?.recordCount ?? 0)) { page++; void refresh(true); } });
el('comparison-node-sort').addEventListener('change', () => {
  if (latestComparison) renderComparison(latestComparison.local, latestComparison.provider);
});
for (const button of document.querySelectorAll<HTMLButtonElement>('[data-comparison-mode]')) {
  button.addEventListener('click', () => {
    const section = button.dataset.comparisonSection === 'nodes' ? 'nodes' : 'buckets';
    comparisonModes[section] = button.dataset.comparisonMode === 'share' ? 'share' : 'list';
    detailPopover.close();
    if (latestComparison) renderComparison(latestComparison.local, latestComparison.provider);
  });
}
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
el('probe-basis').addEventListener('change', () => {
  probeBasis = el<HTMLSelectElement>('probe-basis').value;
  chooseSource(''); void refresh(true);
});
el('provider-metric').addEventListener('change', () => {
  providerMetric = el<HTMLSelectElement>('provider-metric').value as 'raw' | 'billed';
  if (latestProvider) renderProviderHistory(latestProvider);
  if (latestComparison) renderComparison(latestComparison.local, latestComparison.provider);
});
for (const button of document.querySelectorAll<HTMLButtonElement>('[data-ranking-mode]')) {
  button.addEventListener('click', () => {
    detailPopover.close();
    rankingMode = button.dataset.rankingMode === 'share' ? 'share' : 'ranking';
    for (const peer of document.querySelectorAll<HTMLButtonElement>('[data-ranking-mode]')) {
      const selected = peer.dataset.rankingMode === rankingMode;
      peer.classList.toggle('selected', selected);
      peer.setAttribute('aria-pressed', String(selected));
    }
    if (latestHistory) renderHistory(latestHistory);
    else if (latestProvider) renderProviderHistory(latestProvider);
  });
}
el('compare-toggle').addEventListener('change', () => {
  comparison = el<HTMLInputElement>('compare-toggle').checked && statisticsSource !== '';
  chooseSource(statisticsSource); void refresh(true);
});
el('copy-exact-value').addEventListener('click', async () => {
  try { await navigator.clipboard.writeText(el('exact-value-text').textContent ?? ''); put('copy-value-result', '已复制'); }
  catch { put('copy-value-result', '复制未完成，可选中文本复制'); }
});
for (const button of document.querySelectorAll<HTMLButtonElement>('[data-exact]')) {
  button.addEventListener('pointerenter', () => { button.title = el(button.dataset.exact!).textContent ?? ''; });
  button.addEventListener('click', () => showExactValue(el(button.dataset.exact!).textContent ?? ''));
}
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
    renderImportPreview(preview, request.source, request.timeZoneId);
    el('import-warnings').replaceChildren(...(preview.warnings || []).map(warning => element('li', warning)));
    show('import-preview', true); el<HTMLButtonElement>('commit-import').disabled = preview.duplicate || preview.warnings.length > 0;
    formResult('import-result', preview.duplicate ? '全部记录已存在，不会重复记账。' : preview.warnings.length ?
      '发现计费记录冲突，请核对后重新预览；本批不会导入。' : '预览完成。相同来源按重复导出快照比较，保留文件内相同行的出现次数。请确认来源与时区后导入。');
  } catch (error) { if (revision === importRevision) formResult('import-result', errorText(error), true); } finally { button.disabled = expired; }
});
el('commit-import').addEventListener('click', async () => {
  if (!importRequest) return;
  const request = importRequest, button = el<HTMLButtonElement>('commit-import'); button.disabled = true;
    try {
      const result = await post<ImportPreview>('/api/imports/commit', request); invalidateImport();
      formResult('import-result', `导入完成：新增 ${result.newRowCount.toLocaleString('zh-CN')} 条，跳过 ${result.skippedRowCount.toLocaleString('zh-CN')} 条。${result.duplicate ?
        '全部记录已存在，未新增批次。' : `新增原始量 ${exactBytes(result.newRawBytes)}，新增计费量 ${exactProviderBytes(result.newBilledBytes)}。已保存到独立服务商账本。`}可在统计来源中选择查看节点历史。`);
      await loadBatches(); await loadSources(); if (statisticsSource === request.source.trim()) await refresh(true);
    }
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

