import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { JSDOM } from 'jsdom';

const root = new URL('../../', import.meta.url);
const read = path => readFile(new URL(path, root));
const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

test('production page uses local published icons and a decorative brand mark', async () => {
  const dom = new JSDOM(await read('web/dist/index.html'));
  const document = dom.window.document;
  assert.equal(document.querySelector('link[type="image/svg+xml"]').getAttribute('href'), '/icon.svg');
  assert.equal(document.querySelector('link[href="/icon.ico"]').getAttribute('rel'), 'icon');
  const brand = document.querySelector('img.brand-symbol');
  assert.equal(brand.getAttribute('src'), '/icon.svg');
  assert.equal(brand.getAttribute('alt'), '');
  for (const file of ['icon.svg', 'icon.ico', 'icon-256.png'])
    assert.deepEqual(await read(`web/dist/${file}`), await read(`web/public/${file}`));
  dom.window.close();
});

test('Windows icon contains every normal and high-DPI size with 32-bit alpha', async () => {
  const ico = await read('web/public/icon.ico');
  assert.equal(ico.readUInt16LE(0), 0);
  assert.equal(ico.readUInt16LE(2), 1);
  assert.equal(ico.readUInt16LE(4), sizes.length);
  for (const [i, size] of sizes.entries()) {
    const offset = 6 + i * 16;
    assert.equal(ico[offset] || 256, size);
    assert.equal(ico[offset + 1] || 256, size);
    assert.equal(ico.readUInt16LE(offset + 6), 32);
    const bytes = ico.readUInt32LE(offset + 8), start = ico.readUInt32LE(offset + 12);
    assert.ok(start + bytes <= ico.length);
    const png = ico.subarray(start, start + bytes);
    assert.equal(png.subarray(1, 4).toString(), 'PNG');
    assert.equal(png.readUInt32BE(16), size);
    assert.equal(png.readUInt32BE(20), size);
    assert.equal(png[25], 6); // RGBA, including transparent rounded corners.
  }
});

test('executable and tray embed the same multi-size icon independently of web output', async () => {
  const project = (await read('src/Host/Host.csproj')).toString();
  assert.match(project, /<ApplicationIcon>\.\.\/\.\.\/web\/public\/icon\.ico<\/ApplicationIcon>/);
  assert.match(project, /<EmbeddedResource Include="\.\.\/\.\.\/web\/public\/icon\.ico" LogicalName="V2rayNMonitor.Host.AppIcon.ico"/);
  const program = (await read('src/Host/Program.cs')).toString();
  assert.match(program, /GetManifestResourceStream\("V2rayNMonitor.Host.AppIcon.ico"\)/);
  assert.doesNotMatch(program, /SystemIcons\.Application/);
});
