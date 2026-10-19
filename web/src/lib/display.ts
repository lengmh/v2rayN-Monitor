import { bytes } from './format.ts';
import type { LocalNode } from '../types.ts';

/** Only the server's verified merge key can combine scopes. Equal names never do. */
export function displayNodes(nodes: LocalNode[]): LocalNode[] {
  const groups = new Map<string, LocalNode[]>();
  for (const node of nodes) {
    const key = node.display?.mergeKey ? `merge:${node.display.mergeKey}` : node.key;
    const group = groups.get(key) ?? []; group.push(node); groups.set(key, group);
  }
  return [...groups].map(([key, group]) => {
    const first = group[0];
    const names = [...new Set(group.map(node => node.display?.originalName ?? node.name))];
    const sum = (field: 'upload' | 'download' | 'recoveredUpload' | 'recoveredDownload'): string =>
      group.reduce((total, node) => total + bytes(node[field] ?? '0'), 0n).toString();
    return { ...first, key: group.length > 1 ? key : first.key,
      name: names.length === 1 ? names[0] : `${names[0]}（含历史名称）`,
      upload: sum('upload'), download: sum('download'), recoveredUpload: sum('recoveredUpload'), recoveredDownload: sum('recoveredDownload'),
      display: first.display ? { ...first.display, groups: [...new Set(group.flatMap(node => node.display?.groups ?? []))] } : first.display };
  });
}
