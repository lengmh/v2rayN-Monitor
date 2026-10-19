export interface Settings { v2rayNPath: string; timeZoneId: string; demo: boolean }
export interface ProxyCollectionScope { basis: 'routed_proxy' | 'xray_outbound_proxy'; isPartial: boolean; uncollectedOutbounds: number; uncollectedGroups: number; description: string }
export interface CollectorStatus { state: string; message: string; currentNode: string | null; lastSample: string | null; uploadPerSecond: string | null; downloadPerSecond: string | null; demo: boolean; scope?: ProxyCollectionScope | null; collectionBasis?: string; probes?: { historyEnabled: boolean; state: string; message: string } | null }
export interface NodeInfo { key: string; name: string }
export interface TrafficDisplay { originalName: string | null; groups: string[]; mergeKey: string | null; rate: string | null; rateStatus: string; estimatedBilledBytes: string | null; estimateCoveredBytes: string; estimateUnknownBytes: string }
export interface LocalNode extends NodeInfo { upload: string; download: string; recoveredUpload?: string; recoveredDownload?: string; display?: TrafficDisplay | null }
export interface TrafficRow { start: string; end: string; nodeKey: string; nodeName: string; upload: string; download: string; quality: string; partialCoverage?: boolean; display?: TrafficDisplay | null }
export interface HistoryResult {
  basis?: string;
  start: string; end: string; upload: string; download: string; unallocatedUpload: string; unallocatedDownload: string;
  gapCount: number; series: { label: string; upload: string; download: string; recoveredUpload?: string; recoveredDownload?: string }[];
  nodes: LocalNode[];
  comparisonNodes?: LocalNode[] | null;
  display?: TrafficDisplay | null;
  displayMetadataEnabled?: boolean;
  records: TrafficRow[]; recordCount: number;
  recoveredUpload?: string; recoveredDownload?: string; partialUpload?: string; partialDownload?: string;
  unbucketedUpload?: string; unbucketedDownload?: string;
  gaps?: { start: string; end: string; reason: string; isOpen: boolean; origin?: string | null }[];
  partialCoverage?: boolean;
  earliestRecord?: string | null;
}
export interface ImportPreview { hash: string; rowCount: number; rawBytes: string; billedBytes: string; firstTime: string | null; lastTime: string | null; duplicate: boolean; warnings: string[]; newRowCount: number; skippedRowCount: number; newRawBytes: string; newBilledBytes: string }
export interface ImportBatch { source: string; hash: string; rowCount: number; rawBytes: string; billedBytes: string; firstTime?: string; lastTime?: string; importedAt?: string; timeZoneId?: string }
export interface ProviderHistory {
  source: string; rawBytes: string; billedBytes: string; recordCount: number;
  series: { label: string; rawBytes: string; billedBytes: string }[];
  nodes: { name: string; rawBytes: string; billedBytes: string }[];
  records: { at: string; nodeName: string; rate: string; rawBytes: string; billedBytes: string }[];
}
