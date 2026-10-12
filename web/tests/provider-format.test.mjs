import { test } from 'node:test';
import assert from 'node:assert/strict';
import { exactProviderBytes, formatProviderBytes, exactProviderTotal, formatProviderTotal, providerUnits, providerUnitsText } from '../src/lib/provider-format.ts';
import { bytes } from '../src/lib/format.ts';

test('provider decimal values keep every digit beyond integer and floating-point precision', () => {
  assert.equal(exactProviderBytes('800.800000000123'), '800.800000000123 bytes');
  assert.equal(exactProviderBytes('2600.800000000123'), '2,600.800000000123 bytes');
  assert.equal(exactProviderBytes('9007199254740993.123456789012'), '9,007,199,254,740,993.123456789012 bytes');
  assert.equal(exactProviderBytes('79228162514264337593543950335'), '79,228,162,514,264,337,593,543,950,335 bytes');
  assert.equal(exactProviderBytes('0.0000000000000000000000000001'), '0.0000000000000000000000000001 bytes');
});

test('provider units round with integer arithmetic and preserve exact text independently', () => {
  assert.equal(formatProviderBytes('1073741824.123', 'GB'), '1.074 GB');
  assert.equal(formatProviderBytes('1073741824.123', 'GiB'), '1.000 GiB');
  assert.equal(formatProviderBytes('1999499999.9999999999', 'GB'), '1.999 GB');
  assert.equal(formatProviderBytes('1999500000.0000000001', 'GB'), '2.000 GB');
  assert.equal(formatProviderBytes('0', 'GB'), '0.000 GB');
  assert.equal(formatProviderBytes('0.0000000000000000000000000001', 'GB'), '< 0.001 GB');
  assert.equal(formatProviderBytes('79228162514264337593543950335', 'GB'), '79,228,162,514,264,337,593.544 GB');
});

test('provider decimals do not loosen local integer accounting or accept malformed payloads', () => {
  assert.throws(() => bytes('800.800000000123'));
  for (const value of ['-1', '1e3', 'NaN', 'Infinity', '1.2.3', '<script>', ' 1', '1 ', '0.00000000000000000000000000001', '79228162514264337593543950336']) {
    assert.throws(() => exactProviderBytes(value));
    assert.throws(() => formatProviderBytes(value, 'GB'));
  }
});

test('cross-batch query totals remain exact beyond a single import bound', () => {
  assert.equal(exactProviderTotal('158456325028528675187087900670'), '158,456,325,028,528,675,187,087,900,670 bytes');
  assert.equal(providerUnitsText(providerUnits('1000.800000000123')), '1000.800000000123');
  assert.equal(formatProviderTotal('1999500000.0000000001', 'GB'), '2.000 GB');
  assert.ok(providerUnits('0.0000000000000000000000000002') > providerUnits('0.0000000000000000000000000001'));
});
