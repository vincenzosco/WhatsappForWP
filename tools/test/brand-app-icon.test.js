// The identity assets must carry the bubble the released build drew
// (official/logo.png) - the green bubble in its white outline - and not the flat
// square or the sticker on a white card that replaced it: both read as "no
// logo". This pins the generator's source and the shape of the app-list icon,
// whose field is the bubble's own transparency and not a card.
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

test('the app-list icon is the bubble on its own field, not a card', () => {
  // The bubble's field is transparent and that is the point: the list's own
  // background shows around the mark. A white card behind it is what hid the
  // logo on the tile, and a flat square is what made it unreadable.
  for (const file of [
    path.join(root, 'WhatsappApp', 'Assets', 'WhatsAppLogo.png'),
    path.join(root, 'tools', 'brand', 'official', 'logo.png'),
  ]) {
    const row = firstRows(file, 3)[0];
    const alpha = row[3];
    assert.ok(alpha < 40, file + ' must keep the bubble\'s own field, found corner alpha ' + alpha);
  }
});

test('the generator draws every identity surface from the bubble', () => {
  const src = fs.readFileSync(path.join(root, 'tools', 'make-brand-assets.js'), 'utf8');
  assert.match(src, /official\/'?\)?\s*,\s*'logo\.png'|OFFICIAL, 'logo\.png'/, 'the bubble is the source');
  assert.ok(
    !/copy\(\s*'WhatsAppLogo\.png'\s*,\s*'logo\.png'\s*\)/.test(src),
    'the app icon is drawn, not copied raw'
  );
  for (const name of [
    'WhatsAppLogo.png', 'TileIcon.png', 'Logo.scale-240.png',
    'SmallLogo.scale-240.png', 'Square71x71Logo.scale-240.png', 'WideLogo.scale-240.png',
    'StoreLogo.scale-240.png', 'LockScreenBadge.scale-240.png',
  ]) {
    assert.ok(src.includes("write('" + name + "'"), name + ' is generated, not copied');
  }
});

test('every surface the manifests name exists and carries a mark', () => {
  for (const name of [
    'WhatsAppLogo.png', 'TileIcon.png', 'TileIconWide.png', 'Logo.scale-240.png',
    'SmallLogo.scale-240.png', 'Square71x71Logo.scale-240.png', 'WideLogo.scale-240.png',
    'StoreLogo.scale-240.png', 'LockScreenBadge.scale-100.png', 'LockScreenBadge.scale-240.png',
  ]) {
    const file = path.join(root, 'WhatsappApp', 'Assets', name);
    const rows = firstRows(file, 4000);
    let painted = 0;
    for (const row of rows) {
      for (let i = 3; i < row.length; i += 4) {
        if (row[i] > 0) painted++;
      }
    }
    assert.ok(painted > 0, name + ' is drawn as nothing (every pixel transparent)');
  }
});
