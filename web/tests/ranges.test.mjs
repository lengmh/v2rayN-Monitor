import { test } from 'node:test';
import assert from 'node:assert/strict';
import { dateInZone, dateRange, periodRange, shiftAnchor, startOfDate } from '../src/lib/ranges.ts';
import { chartSlots, shortLabel } from '../src/lib/chart.ts';
test('selected calendar day uses timezone boundaries, not browser timezone', () => {
  const range = dateRange('2026-10-09', '2026-10-09', 'Asia/Shanghai');
  assert.equal(range.start, '2026-10-08T16:00:00.000Z'); assert.equal(range.end, '2026-10-09T16:00:00.000Z');
});
test('DST spring day has 23 hours, fall day has 25, half-open end', () => {
  const spring = dateRange('2026-03-08', '2026-03-08', 'America/New_York');
  const fall = dateRange('2026-11-01', '2026-11-01', 'America/New_York');
  assert.equal((Date.parse(spring.end) - Date.parse(spring.start)) / 3600000, 23);
  assert.equal((Date.parse(fall.end) - Date.parse(fall.start)) / 3600000, 25);
  const slots = chartSlots([], fall, 'hour', 'America/New_York');
  assert.equal(slots.length, 25);
  assert.ok(slots.some(p => p.label === '2026-11-01T01:00-04:00'));
  assert.ok(slots.some(p => p.label === '2026-11-01T01:00-05:00'));
});
test('midnight DST shift resolves to first instant of calendar date', () => { assert.equal(startOfDate('2018-11-04', 'America/Sao_Paulo'), '2018-11-04T03:00:00.000Z'); });
test('Monday weeks handle year boundary', () => {
  const range = periodRange('week', '2026-01-01', 'UTC'); assert.equal(range.from, '2025-12-29'); assert.equal(range.through, '2026-01-04');
});
test('month navigation never skips February', () => { assert.equal(shiftAnchor('month', '2026-01-31', 1), '2026-02-01'); assert.equal(shiftAnchor('month', '2026-12-31', 1), '2027-01-01'); });
test('leap year month and invalid date checks', () => { assert.equal(periodRange('month', '2024-02-09', 'UTC').through, '2024-02-29'); assert.throws(() => dateRange('2026-02-30', '2026-03-01', 'UTC')); assert.throws(() => dateRange('2026-10-10', '2026-10-09', 'UTC')); });
test('current local day may differ from UTC', () => { assert.equal(dateInZone(new Date('2026-10-09T20:00:00Z'), 'Asia/Shanghai'), '2026-10-10'); });
test('empty chart slots remain unknown rather than fabricated zero', () => {
  const range = dateRange('2026-10-01', '2026-10-03', 'UTC');
  const slots = chartSlots([{ label: '2026-10-02', upload: '0', download: '0' }], range, 'day', 'UTC');
  assert.equal(slots.length, 3); assert.equal(slots[0].point, undefined); assert.equal(slots[1].point.download, '0'); assert.equal(slots[2].point, undefined);
});
test('too-dense chart is declined rather than silently truncating', () => { assert.equal(chartSlots([], periodRange('year', '2026-01-01', 'UTC'), 'hour', 'UTC'), undefined); assert.equal(shortLabel('2026-11-01T01:00-04:00', 'hour'), '01:00'); });

test('repeated positive-offset DST hours retain UTC chronological order', () => { const range = dateRange('2026-10-25', '2026-10-25', 'Europe/Berlin'); const labels = chartSlots([], range, 'hour', 'Europe/Berlin').map(p => p.label); assert.equal(labels.length, 25); assert.ok(labels.indexOf('2026-10-25T02:00+02:00') < labels.indexOf('2026-10-25T02:00+01:00')); });
