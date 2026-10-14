export interface Settings { v2rayNPath: string; timeZoneId: string; demo: boolean }
export interface ProxyCollectionScope { basis: 'routed_proxy'; isPartial: boolean; uncollectedOutbounds: number; uncollectedGroups: number; description: string }
export interface CollectorStatus { state: string; message: string; currentNode: string | null; lastSample: string | null; uploadPerSecond: string | null; downloadPerSecond: string | null; demo: boolean; scope?: ProxyCollectionScope | null; collectionBasis?: string }
export interface NodeInfo { key: string; name: string }
export interface TrafficRow { start: string; end: string; nodeKey: string; nodeName: string; upload: string; download: string; quality: string; partialCoverage?: boolean }
export interface HistoryResult {
  basis?: string;
  start: string; end: string; upload: string; download: string; unallocatedUpload: string; unallocatedDownload: string;
  gapCount: number; series: { label: string; upload: string; download: string; recoveredUpload?: string; recoveredDownload?: string }[];
  nodes: { key: string; name: string; upload: string; download: string; recoveredUpload?: string; recoveredDownload?: string }[];
  records: TrafficRow[]; recordCount: number;
  recoveredUpload?: string; recoveredDownload?: string; partialUpload?: string; partialDownload?: string;
  unbucketedUpload?: string; unbucketedDownload?: string;
  gaps?: { start: string; end: string; reason: string; isOpen: boolean; origin?: string | null }[];
  partialCoverage?: boolean;
}
export interface ImportPreview { hash: string; rowCount: number; rawBytes: string; billedBytes: string; firstTime: string | null; lastTime: string | null; duplicate: boolean; warnings: string[]; newRowCount: number; skippedRowCount: number; newRawBytes: string; newBilledBytes: string }
export interface ImportBatch { source: string; hash: string; rowCount: number; rawBytes: string; billedBytes: string; firstTime?: string; lastTime?: string; importedAt?: string; timeZoneId?: string }
export interface ProviderHistory {
  source: string; rawBytes: string; billedBytes: string; recordCount: number;
  series: { label: string; rawBytes: string; billedBytes: string }[];
  nodes: { name: string; rawBytes: string; billedBytes: string }[];
  records: { at: string; nodeName: string; rate: string; rawBytes: string; billedBytes: string }[];
}
