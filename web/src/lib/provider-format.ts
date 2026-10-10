import { decimal, unitDivisor } from './format.ts';
import type { Unit } from './format.ts';

/** Provider accounting is decimal text; local raw-byte parsing must stay integer-only. */
function providerValue(value: string): { whole: string; fraction: string; coefficient: bigint; scale: bigint } {
  const match = /^(\d{1,29})(?:\.(\d{1,28}))?$/.exec(value);
  if (!match) throw new Error('服务返回了无效的服务商计费量。');
  const whole = match[1]!, fraction = match[2] || '';
  const coefficient = BigInt(whole + fraction);
  if (coefficient > 79_228_162_514_264_337_593_543_950_335n) throw new Error('服务商计费量超出精确十进制范围。');
  return { whole, fraction, coefficient, scale: 10n ** BigInt(fraction.length) };
}

/** Unit conversion rounds for display only; always show exactProviderBytes alongside it. */
export function formatProviderBytes(value: string, unit: Unit): string {
  const { coefficient, scale } = providerValue(value);
  return `${decimal(coefficient, unitDivisor(unit) * scale)} ${unit}`;
}

export function exactProviderBytes(value: string): string {
  const { whole, fraction } = providerValue(value);
  return `${BigInt(whole).toLocaleString('zh-CN')}${fraction ? `.${fraction}` : ''} bytes`;
}
