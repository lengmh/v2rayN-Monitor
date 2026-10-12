export type Period = 'day' | 'week' | 'month' | 'year' | 'custom';
export type Bucket = 'hour' | 'day' | 'week' | 'month' | 'year';
export interface Range { start: string; end: string; from: string; through: string }
const datePattern = /^\d{4}-\d{2}-\d{2}$/;
const formatters = new Map<string, Intl.DateTimeFormat>();
function formatter(timeZone: string): Intl.DateTimeFormat {
  let result = formatters.get(timeZone);
  if (!result) {
    result = new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' });
    formatters.set(timeZone, result);
  }
  return result;
}
export function dateInZone(date: Date, timeZone: string): string {
  const values = Object.fromEntries(formatter(timeZone).formatToParts(date).map(p => [p.type, p.value]));
  return `${values.year}-${values.month}-${values.day}`;
}
function calendarDate(key: string): Date {
  if (!datePattern.test(key)) throw new Error('请选择完整的日期。');
  const date = new Date(`${key}T12:00:00Z`);
  if (!Number.isFinite(date.getTime()) || date.toISOString().slice(0, 10) !== key) throw new Error('日期无效。');
  return date;
}
export function addDays(key: string, days: number): string {
  const date = calendarDate(key); date.setUTCDate(date.getUTCDate() + days); return date.toISOString().slice(0, 10);
}
/** First instant on a calendar date, including DST transitions at midnight. */
export function startOfDate(key: string, timeZone: string): string {
  const nominal = calendarDate(key).getTime();
  let low = nominal - 48 * 3600_000, high = nominal + 48 * 3600_000;
  while (low < high) {
    const middle = Math.floor((low + high) / 2);
    if (dateInZone(new Date(middle), timeZone) < key) low = middle + 1; else high = middle;
  }
  if (dateInZone(new Date(low), timeZone) !== key) throw new Error('所选时区不存在这个日期，请选择相邻日期。');
  return new Date(low).toISOString();
}
export function dateRange(from: string, through: string, timeZone: string): Range {
  calendarDate(from); calendarDate(through);
  if (from > through) throw new Error('结束日期不能早于开始日期。');
  return { start: startOfDate(from, timeZone), end: startOfDate(addDays(through, 1), timeZone), from, through };
}
export function periodRange(period: Exclude<Period, 'custom'>, anchor: string, timeZone: string): Range {
  const date = calendarDate(anchor);
  let from = anchor, next = addDays(anchor, 1);
  if (period === 'week') {
    from = addDays(anchor, -((date.getUTCDay() + 6) % 7)); next = addDays(from, 7);
  } else if (period === 'month') {
    from = `${anchor.slice(0, 7)}-01`; date.setUTCDate(1); date.setUTCMonth(date.getUTCMonth() + 1); next = date.toISOString().slice(0, 10);
  } else if (period === 'year') {
    from = `${anchor.slice(0, 4)}-01-01`; next = `${date.getUTCFullYear() + 1}-01-01`;
  }
  return dateRange(from, addDays(next, -1), timeZone);
}
export function shiftAnchor(period: Exclude<Period, 'custom'>, anchor: string, amount: number): string {
  const date = calendarDate(anchor);
  if (period === 'day' || period === 'week') return addDays(anchor, amount * (period === 'week' ? 7 : 1));
  date.setUTCDate(1);
  if (period === 'month') date.setUTCMonth(date.getUTCMonth() + amount); else date.setUTCFullYear(date.getUTCFullYear() + amount);
  return date.toISOString().slice(0, 10);
}
export function timestamp(value: string | null, timeZone: string): string {
  if (!value) return '尚无采样';
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return value;
  return new Intl.DateTimeFormat('zh-CN', { timeZone, month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false }).format(date);
}
