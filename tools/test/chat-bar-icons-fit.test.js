'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');

const ROOT = path.join(__dirname, '..', '..');
const ASSET_DIR = path.join(ROOT, 'WhatsappApp/Assets/Reference/ThemeDark');

/**
 * The bounding box of the drawn pixels of an RGBA PNG, from the file itself:
 * inflate the scanlines, undo the per-row filter, and look at the alpha channel.
 */
function drawnBox(file) {
  const png = fs.readFileSync(file);
  assert.equal(png.readUInt32BE(0), 0x89504e47, `${path.basename(file)} is a PNG`);
  const width = png.readUInt32BE(16);
  const height = png.readUInt32BE(20);
  assert.equal(png[24], 8, '8 bits per channel');
  assert.equal(png[25], 6, 'RGBA');

  const idat = [];
  for (let at = 8; at < png.length;) {
    const len = png.readUInt32BE(at);
    const kind = png.toString('ascii', at + 4, at + 8);
    if (kind === 'IDAT') idat.push(png.subarray(at + 8, at + 8 + len));
    at += 12 + len;
  }
  const raw = zlib.inflateSync(Buffer.concat(idat));

  const stride = width * 4;
  const out = Buffer.alloc(height * stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const row = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
    for (let x = 0; x < stride; x++) {
      const left = x >= 4 ? out[y * stride + x - 4] : 0;
      const up = y > 0 ? out[(y - 1) * stride + x] : 0;
      const upLeft = (y > 0 && x >= 4) ? out[(y - 1) * stride + x - 4] : 0;
      let value = row[x];
      if (filter === 1) value += left;
      else if (filter === 2) value += up;
      else if (filter === 3) value += (left + up) >> 1;
      else if (filter === 4) {
        const p = left + up - upLeft;
        const pa = Math.abs(p - left), pb = Math.abs(p - up), pc = Math.abs(p - upLeft);
        value += (pa <= pb && pa <= pc) ? left : (pb <= pc ? up : upLeft);
      }
      out[y * stride + x] = value & 0xff;
    }
  }

  let x0 = width, y0 = height, x1 = -1, y1 = -1;
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      if (out[y * stride + x * 4 + 3] <= 8) continue;
      if (x < x0) x0 = x;
      if (x > x1) x1 = x;
      if (y < y0) y0 = y;
      if (y > y1) y1 = y;
    }
  }
  assert.ok(x1 >= 0, `${path.basename(file)} draws something`);
  return { width, height, x0, y0, x1, y1 };
}

test('the chat bar icons sit inside their circle instead of filling the tile', () => {
  // The app bar draws one ring per command and fits the icon file into it. The
  // four composer-scale glyphs were cut for an input bar of their own, where the
  // tile is the button: nearly full-bleed (88, 85, 81, 77 percent of the canvas)
  // they reached past the ring, while the reference's own bar glyphs - the
  // handset at 50 percent - sit inside it with room. These are trimmed to the
  // same rule: the drawn pixels fill at most 65 percent of the canvas.
  for (const file of ['inputbar-send.png', 'inputbar-mic.png', 'inputbar-emoji.png', 'inputbar-attach.png']) {
    const box = drawnBox(path.join(ASSET_DIR, file));
    const fill = Math.max((box.x1 - box.x0 + 1) / box.width, (box.y1 - box.y0 + 1) / box.height);
    assert.ok(fill <= 0.65, `${file} fills ${(fill * 100).toFixed(0)}% of its canvas, must be at most 65%`);
  }
});
