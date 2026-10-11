import { formatAmount, unitDivisor } from './format.ts';
import type { Unit } from './format.ts';

/** Provider accounting is decimal text; local raw-byte parsing must stay integer-only. */
function providerValue(value: string, aggregate = false): { whole: string; fraction: string; coefficient: bigint; scale: bigint } {
  const match = /^(\d+)(?:\.(\d{1,28}))?$/.exec(value);
  if (!match) throw new Error('服务返回了无效的服务商计费量。');
  const whole = match[1]!, fraction = match[2] || '';
  const coefficient = BigInt(whole + fraction);
  if (!aggregate && coefficient > 79_228_162_514_264_337_593_543_950_335n) throw new Error('服务商计费量超出精确十进制范围。');
  return { whole, fraction, coefficient, scale: 10n ** BigInt(fraction.length) };
}

/** Unit conversion rounds for display only; always show exactProviderBytes alongside it. */
export function formatProviderBytes(value: string, unit: Unit): string {
  const { coefficient, scale } = providerValue(value);
  return formatAmount(coefficient, unitDivisor(unit) * scale, unit);
}

export function exactProviderBytes(value: string): string {
  const { whole, fraction } = providerValue(value);
  return `${BigInt(whole).toLocaleString('zh-CN')}${fraction ? `.${fraction}` : ''} bytes`;
}

/** Query totals may exceed one import's bound; fixed-scale integers keep sorting and charts exact. */
export function providerUnits(value: string): bigint {
  const parsed = providerValue(value, true);
  return parsed.coefficient * (10n ** 28n / parsed.scale);
}
export function providerUnitsText(value: bigint): string {
  const text = value.toString().padStart(29, '0');
  const fraction = text.slice(-28).replace(/0+$/, '');
  return `${text.slice(0, -28)}${fraction ? `.${fraction}` : ''}`;
}
export function formatProviderTotal(value: string, unit: Unit): string {
  return formatAmount(providerUnits(value), unitDivisor(unit) * 10n ** 28n, unit);
}
export function exactProviderTotal(value: string): string {
  const { whole, fraction } = providerValue(value, true);
  return `${BigInt(whole).toLocaleString('zh-CN')}${fraction ? `.${fraction}` : ''} bytes`;
}
