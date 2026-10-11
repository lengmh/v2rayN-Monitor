import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bytes, decimal, exactBytes, formatBytes, percentage, rate } from '../src/lib/format.ts';
test('bytes beyond Number.MAX_SAFE_INTEGER remain exact', () => {
  assert.equal(bytes('9007199254740993'), 9007199254740993n);
  assert.equal(exactBytes('9007199254740993'), '9,007,199,254,740,993 bytes');
  assert.equal(formatBytes('9007199254740993', 'GB'), '9,007,199.255 GB');
});
test('GB and GiB use different exact divisors and integer rounding', () => {
  assert.equal(formatBytes('1073741824', 'GiB'), '1.000 GiB');
  assert.equal(formatBytes('1073741824', 'GB'), '1.074 GB');
  assert.equal(decimal(1999500000n, 1000000000n), '2.000');
  assert.equal(formatBytes('0', 'GiB'), '0.000 GiB');
  assert.equal(formatBytes('1', 'GB'), '< 0.001 GB');
});
test('rates preserve missing vs zero', () => { assert.equal(rate(null, 'GB'), '—'); assert.equal(rate('0', 'GB'), '0 B/s'); assert.equal(rate('2048', 'GiB'), '2.00 KiB/s'); });
test('invalid byte payload fails closed', () => { for (const value of ['-1', '1.5', '1e3', 'NaN', '<script>']) assert.throws(() => bytes(value)); });
test('chart percentage converts only bounded ratio', () => { assert.equal(percentage(10n ** 30n, 2n * 10n ** 30n), 50); assert.equal(percentage(1n, 0n), 0); assert.equal(percentage(3n, 2n), 100); });
