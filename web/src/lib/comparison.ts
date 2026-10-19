import { bytes } from './format.ts';
import type { HistoryResult, ProviderHistory } from '../types.ts';

export interface ComparisonRow { name: string; local: string | null; upload: string | null; download: string | null; raw: string | null; billed: string | null }
/** Name equality is a display grouping only. No key/identity association is persisted. */
export function compareNames(local: HistoryResult, provider: ProviderHistory): ComparisonRow[] {
  const rows = new Map<string, ComparisonRow>();
  for (const node of local.comparisonNodes ?? local.nodes) {
    const row = rows.get(node.name) ?? { name: node.name, local: null, upload: null, download: null, raw: null, billed: null };
    row.upload = (bytes(row.upload ?? '0') + bytes(node.upload)).toString();
    row.download = (bytes(row.download ?? '0') + bytes(node.download)).toString();
    row.local = (bytes(row.local ?? '0') + bytes(node.upload) + bytes(node.download)).toString();
    rows.set(node.name, row);
  }
  for (const node of provider.nodes) {
    const row = rows.get(node.name) ?? { name: node.name, local: null, upload: null, download: null, raw: null, billed: null };
    row.raw = node.rawBytes; row.billed = node.billedBytes; rows.set(node.name, row);
  }
  return [...rows.values()].sort((a, b) => a.name.localeCompare(b.name));
}
export function compareBuckets(local: HistoryResult, provider: ProviderHistory): ComparisonRow[] {
  const rows = new Map<string, ComparisonRow>();
  for (const point of local.series)
    rows.set(point.label, { name: point.label, local: (bytes(point.upload) + bytes(point.download)).toString(), upload: point.upload, download: point.download, raw: null, billed: null });
  for (const point of provider.series) {
    const row = rows.get(point.label) ?? { name: point.label, local: null, upload: null, download: null, raw: null, billed: null };
    row.raw = point.rawBytes; row.billed = point.billedBytes; rows.set(point.label, row);
  }
  return [...rows.values()].sort((a, b) => a.name.includes('T') ? Date.parse(a.name) - Date.parse(b.name) : a.name.localeCompare(b.name));
}
