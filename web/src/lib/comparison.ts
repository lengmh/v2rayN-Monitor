import { bytes } from './format.ts';
import { providerUnits, providerUnitsText } from './provider-format.ts';
import type { HistoryResult, ProviderHistory, TrafficDisplay } from '../types.ts';

export interface ComparisonRow {
  name: string; local: string | null; upload: string | null; download: string | null; raw: string | null; billed: string | null;
  localEstimate: string | null; estimateCoveredBytes: string; estimateUnknownBytes: string;
}
function emptyRow(name: string): ComparisonRow {
  return { name, local: null, upload: null, download: null, raw: null, billed: null,
    localEstimate: null, estimateCoveredBytes: '0', estimateUnknownBytes: '0' };
}
function addLocal(row: ComparisonRow, value: { upload: string; download: string; display?: TrafficDisplay | null }): void {
  const total = bytes(value.upload) + bytes(value.download), display = value.display;
  row.upload = (bytes(row.upload ?? '0') + bytes(value.upload)).toString();
  row.download = (bytes(row.download ?? '0') + bytes(value.download)).toString();
  row.local = (bytes(row.local ?? '0') + total).toString();
  row.estimateCoveredBytes = (bytes(row.estimateCoveredBytes) + bytes(display?.estimateCoveredBytes ?? '0')).toString();
  row.estimateUnknownBytes = (bytes(row.estimateUnknownBytes) + bytes(display?.estimateUnknownBytes ?? total.toString())).toString();
  if (display?.estimatedBilledBytes != null)
    row.localEstimate = providerUnitsText(providerUnits(row.localEstimate ?? '0') + providerUnits(display.estimatedBilledBytes));
}
/** Name equality is a display grouping only. No key/identity association is persisted. */
export function compareNames(local: HistoryResult, provider: ProviderHistory): ComparisonRow[] {
  const rows = new Map<string, ComparisonRow>();
  for (const node of local.comparisonNodes ?? local.nodes) {
    const row = rows.get(node.name) ?? emptyRow(node.name);
    addLocal(row, node);
    rows.set(node.name, row);
  }
  for (const node of provider.nodes) {
    const row = rows.get(node.name) ?? emptyRow(node.name);
    row.raw = node.rawBytes; row.billed = node.billedBytes; rows.set(node.name, row);
  }
  return [...rows.values()].sort((a, b) => a.name.localeCompare(b.name));
}
export function compareBuckets(local: HistoryResult, provider: ProviderHistory): ComparisonRow[] {
  const rows = new Map<string, ComparisonRow>();
  for (const point of local.series) {
    const row = emptyRow(point.label); addLocal(row, point); rows.set(point.label, row);
  }
  for (const point of provider.series) {
    const row = rows.get(point.label) ?? emptyRow(point.label);
    row.raw = point.rawBytes; row.billed = point.billedBytes; rows.set(point.label, row);
  }
  return [...rows.values()].sort((a, b) => a.name.includes('T') ? Date.parse(a.name) - Date.parse(b.name) : a.name.localeCompare(b.name));
}
