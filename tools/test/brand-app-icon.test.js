// The app-list icon must be the official green square (Icon1.png), not the
// floating bubble glyph: the bubble's transparent corners turn the small
// app-list tile into a green blob with no visible mark. This pins both the
// generator's source and the generated PNG's opaque corner.
const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');

const root = path.resolve(__dirname, '..', '..');

function firstRows(file, rows) {
  const data = fs.readFileSync(file);
  let at = 8;
  let width = 0;
  let height = 0;
  let depth = 0;
  const idat = [];
  while (at < data.length - 8) {
    const len = data.readUInt32BE(at);
    const type = data.slice(at + 4, at + 8).toString('ascii');
    if (type === 'IHDR') {
      width = data.readUInt32BE(at + 8);
      height = data.readUInt32BE(at + 12);
      depth = data[at + 17];
    } else if (type === 'IDAT') {
      idat.push(data.slice(at + 8, at + 8 + len));
    }
    at += 12 + len;
  }
  assert.strictEqual(depth, 6, file + ' must stay RGBA');
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const stride = width * 4;
  const out = [];
  let prev = Buffer.alloc(stride);
  for (let y = 0; y < Math.min(rows, height); y++) {
    const filter = raw[y * (stride + 1)];
    const row = Buffer.from(raw.slice(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    for (let i = 0; i < stride; i++) {
      const left = i >= 4 ? row[i - 4] : 0;
      const up = prev[i];
      const upLeft = i >= 4 ? prev[i - 4] : 0;
      if (filter === 1) row[i] = (row[i] + left) & 255;
      else if (filter === 2) row[i] = (row[i] + up) & 255;
      else if (filter === 3) row[i] = (row[i] + ((left + up) >> 1)) & 255;
      else if (filter === 4) {
        const p = left + up - upLeft;
        const pa = Math.abs(p - left);
        const pb = Math.abs(p - up);
        const pc = Math.abs(p - upLeft);
        const pick = pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
        row[i] = (row[i] + pick) & 255;
      }
    }
    out.push(row);
    prev = row;
  }
  return out;
}

test('the app-list icon keeps its corners opaque like the official icon', () => {
  for (const file of [
    path.join(root, 'WhatsappApp', 'Assets', 'WhatsAppLogo.png'),
    path.join(root, 'tools', 'brand', 'official', 'Icon1.png'),
  ]) {
    const row = firstRows(file, 3)[0];
    const alpha = row[3];
    assert.ok(alpha > 200, file + ' must be an opaque square, found corner alpha ' + alpha);
  }
});

test('the generator draws the app icon from Icon1.png, not from the bubble glyph', () => {
  const src = fs.readFileSync(path.join(root, 'tools', 'make-brand-assets.js'), 'utf8');
  assert.ok(
    !/copy\(\s*'WhatsAppLogo\.png'\s*,\s*'logo\.png'\s*\)/.test(src),
    'the app icon must not be the floating bubble glyph (logo.png)'
  );
  assert.match(src, /write\(\s*'WhatsAppLogo\.png'/);
});
