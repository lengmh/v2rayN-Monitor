export type Unit = 'GB' | 'GiB';
export const unitDivisor = (unit: Unit): bigint => unit === 'GB' ? 1_000_000_000n : 1_073_741_824n;
export function bytes(value: string): bigint {
  if (!/^\d+$/.test(value)) throw new Error('服务返回了无效的字节数。');
  return BigInt(value);
}
export function decimal(value: bigint, divisor: bigint, places = 3): string {
  if (value < 0n || divisor <= 0n || places < 0 || places > 6) throw new Error('无效的显示数值。');
  const scale = 10n ** BigInt(places);
  const rounded = (value * scale + divisor / 2n) / divisor;
  const whole = (rounded / scale).toLocaleString('zh-CN');
  return places ? `${whole}.${(rounded % scale).toString().padStart(places, '0')}` : whole;
}
export function formatAmount(value: bigint, divisor: bigint, unit: Unit): string {
  const text = decimal(value, divisor);
  return `${value > 0n && text === '0.000' ? '< 0.001' : text} ${unit}`;
}
export const formatBytes = (value: string | bigint, unit: Unit): string => formatAmount(typeof value === 'bigint' ? value : bytes(value), unitDivisor(unit), unit);
export const exactBytes = (value: string | bigint): string => `${(typeof value === 'bigint' ? value : bytes(value)).toLocaleString('zh-CN')} bytes`;
export function rate(value: string | null, unit: Unit): string {
  if (value === null) return '—';
  const number = bytes(value);
  const base = unit === 'GB' ? 1000n : 1024n;
  const labels = unit === 'GB' ? ['B', 'KB', 'MB', 'GB'] : ['B', 'KiB', 'MiB', 'GiB'];
  let divisor = 1n, index = 0;
  while (number >= divisor * base && index < 3) { divisor *= base; index++; }
  return `${decimal(number, divisor, index ? 2 : 0)} ${labels[index]}/s`;
}
/** A bounded integer ratio is the only conversion to floating point. Never use it for accounting. */
export function percentage(value: bigint, maximum: bigint): number {
  if (maximum <= 0n) return 0;
  return Number((value > maximum ? maximum : value) * 10000n / maximum) / 100;
}
