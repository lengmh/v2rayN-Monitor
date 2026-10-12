import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bytes, decimal, exactBytes, formatBytes, formatRecordBytes, recordByteParts, percentage, rate } from '../src/lib/format.ts';
import { compareNames, compareBuckets } from '../src/lib/comparison.ts';
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
test('name comparison aggregates exact names only and preserves absent versus recorded zero', () => {
  const local = { nodes: [{ name: 'A', upload: '9007199254740993', download: '1' }, { name: 'A', upload: '2', download: '0' },
    { name: 'a', upload: '1', download: '0' }, { name: 'A ', upload: '2', download: '0' }],
    series: [{ label: '2026-01-01T00:00+00:00', upload: '0', download: '0' }] };
  const provider = { nodes: [{ name: 'A', rawBytes: '9', billedBytes: '7.200000000123' }, { name: 'CSV only', rawBytes: '0', billedBytes: '0' }],
    series: [{ label: '2026-01-01T01:00+00:00', rawBytes: '0', billedBytes: '0' }] };
  const rows = compareNames(local, provider);
  assert.equal(rows.length, 4);
  assert.deepEqual(rows.find(row => row.name === 'A'), { name: 'A', local: '9007199254740996', raw: '9', billed: '7.200000000123' });
  assert.equal(rows.find(row => row.name === 'CSV only').local, null);
  assert.equal(rows.find(row => row.name === 'a').raw, null);
  const buckets = compareBuckets(local, provider);
  assert.equal(buckets[0].local, '0'); assert.equal(buckets[0].raw, null);
  assert.equal(buckets[1].local, null); assert.equal(buckets[1].billed, '0');
});

test('local record display chooses decimal or binary units without losing integer precision', () => {
  for (const [value, expected] of [['0', '0 B'], ['1', '1 B'], ['999', '999 B'], ['1000', '1.00 KB'], ['12345', '12.35 KB'], ['1000000', '1.00 MB'], ['1000000000', '1.00 GB'], ['9007199254740993', '9,007,199.25 GB']]) {
    assert.equal(formatRecordBytes(value, 'GB'), expected);
  }
  assert.equal(formatRecordBytes(null, 'GB'), '—');
  assert.equal(formatRecordBytes('1023', 'GiB'), '1,023 B');
  assert.equal(formatRecordBytes(1024n, 'GiB'), '1.00 KiB');
  assert.equal(formatRecordBytes('1048576', 'GiB'), '1.00 MiB');
  assert.equal(formatRecordBytes('1073741824', 'GiB'), '1.00 GiB');
  assert.equal(exactBytes('9007199254740993'), '9,007,199,254,740,993 bytes');
});

test('record value and unit parts preserve zero, missing and binary preference', () => {
  assert.deepEqual(recordByteParts('0', 'GB'), { value: '0', unit: 'B', isZero: true });
  assert.deepEqual(recordByteParts(null, 'GB'), { value: '—', unit: '', isZero: false });
  assert.deepEqual(recordByteParts('6420', 'GB'), { value: '6.42', unit: 'KB', isZero: false });
  assert.deepEqual(recordByteParts('1048576', 'GiB'), { value: '1.00', unit: 'MiB', isZero: false });
  assert.equal(recordByteParts('9007199254740993', 'GB').value, '9,007,199.25');
});
