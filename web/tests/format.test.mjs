import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bytes, decimal, exactBytes, formatBytes, formatRecordBytes, recordByteParts, percentage, rate } from '../src/lib/format.ts';
import { compareNames, compareBuckets } from '../src/lib/comparison.ts';
import { displayNodes } from '../src/lib/display.ts';
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
test('MB and MiB use exact megabyte divisors and the selected decimal or binary family', () => {
  assert.equal(formatBytes('1000000', 'MB'), '1.000 MB');
  assert.equal(formatBytes('1048576', 'MiB'), '1.000 MiB');
  assert.equal(formatBytes('1048576', 'MB'), '1.049 MB');
  assert.equal(formatBytes('0', 'MiB'), '0.000 MiB');
  assert.equal(formatBytes('1', 'MB'), '< 0.001 MB');
  assert.equal(formatBytes('9007199254740993', 'MB'), '9,007,199,254.741 MB');
  assert.equal(formatRecordBytes('1000', 'MB'), '1.00 KB');
  assert.equal(formatRecordBytes('1024', 'MiB'), '1.00 KiB');
  assert.equal(rate('1000000', 'MB'), '1.00 MB/s');
  assert.equal(rate('1048576', 'MiB'), '1.00 MiB/s');
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
  assert.deepEqual(rows.find(row => row.name === 'A'), { name: 'A', local: '9007199254740996', upload: '9007199254740995', download: '1', raw: '9', billed: '7.200000000123',
    localEstimate: null, estimateCoveredBytes: '0', estimateUnknownBytes: '9007199254740996' });
  assert.equal(rows.find(row => row.name === 'CSV only').local, null);
  assert.equal(rows.find(row => row.name === 'a').raw, null);
  const buckets = compareBuckets(local, provider);
  assert.equal(buckets[0].local, '0'); assert.equal(buckets[0].raw, null);
  assert.equal(buckets[1].local, null); assert.equal(buckets[1].billed, '0');
});
test('comparison carries exact captured estimates and leaves missing rates unknown in names and buckets', () => {
  const display = { estimatedBilledBytes: '4503599627370496.5', estimateCoveredBytes: '9007199254740993', estimateUnknownBytes: '3' };
  const local = { nodes: [], comparisonNodes: [
    { name: 'A', upload: '9007199254740996', download: '0', display },
    { name: 'A', upload: '2', download: '0', display: { estimatedBilledBytes: '0', estimateCoveredBytes: '2', estimateUnknownBytes: '0' } },
    { name: 'A', upload: '1', download: '0' },
    { name: 'Zero rate', upload: '2', download: '0', display: { estimatedBilledBytes: '0', estimateCoveredBytes: '2', estimateUnknownBytes: '0' } }
  ], series: [{ label: '2026-01-01', upload: '9007199254740996', download: '0', display }] };
  const provider = { nodes: [{ name: 'A', rawBytes: '1', billedBytes: '0.0000000000000000000000000001' }], series: [] };
  const row = compareNames(local, provider)[0];
  assert.equal(row.localEstimate, '4503599627370496.5');
  assert.equal(row.estimateCoveredBytes, '9007199254740995');
  assert.equal(row.estimateUnknownBytes, '4');
  assert.equal(row.billed, '0.0000000000000000000000000001');
  assert.equal(compareNames(local, provider).find(row => row.name === 'Zero rate').localEstimate, '0');
  assert.equal(compareBuckets(local, provider)[0].localEstimate, '4503599627370496.5');
  assert.equal(compareBuckets(local, provider)[0].estimateUnknownBytes, '3');
});
test('node display combines only verified keys and comparison uses captured original names', () => {
  const node = (key, mergeKey, group) => ({ key, name: '【Display prefix】 same name', upload: '9007199254740993', download: '1',
    display: { originalName: 'Same original', groups: [group], mergeKey } });
  const grouped = displayNodes([node('a', 'actual_member:proof', 'US-1x'), node('b', 'actual_member:proof', 'HK-1x'),
    node('c', null, 'US-1x'), node('d', 'legacy_proxy:proof', 'US-1x')]);
  assert.equal(grouped.length, 3);
  assert.equal(grouped[0].key, 'merge:actual_member:proof');
  assert.equal(grouped[0].upload, '18014398509481986');
  assert.deepEqual(grouped[0].display.groups, ['US-1x', 'HK-1x']);
  assert.equal(compareNames({ nodes: [node('a', null, 'US-1x')],
    comparisonNodes: [{ name: 'Same original', upload: '3', download: '4' }] },
    { nodes: [{ name: 'Same original', rawBytes: '7', billedBytes: '3.5' }] })[0].local, '7');
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
