#!/usr/bin/env node
/**
 * tools/make-brand-assets.js
 *
 * Rewrites the tiles, icons and background of WhatsappApp/Assets/ from the logo
 * the owner supplied (tools/brand/logo-source.png): the green bubble in its white
 * sticker outline. Every surface the shell shows - the app-list icon, the tiles,
 * the store logo, the lock badge - is drawn from that one file, so the mark is the
 * same everywhere and no surface is a transparent placeholder.
 *
 * The sizes are the WinRT 240% set the manifests ask for (150->360, 44->106,
 * 71->170, 310x150->744x360) plus the app-list icon at 160 and the tile icons
 * this app pins at 200 and 480. The splash and the chat background are the
 * official package's own drawings, copied from tools/brand/official/.
 *
 * The generated PNGs are committed, so the script only runs when the art
 * changes.
 *
 * The Silverlight splash at the XAP root (WhatsappApp/SplashScreenImage.jpg,
 * 480x800) is a committed JPEG: the phone reads the splash only at the root and
 * only under that name, and this script only writes PNG.
 *
 * Usage:
 * node tools/make-brand-assets.js        # rewrites the files
 * node tools/make-brand-assets.js --preview  # rewrites and previews them
 */
'use strict';

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const ROOT = path.resolve(__dirname, '..');
const OUT_DIR = path.join(ROOT, 'WhatsappApp', 'Assets');
const OFFICIAL = path.join(__dirname, 'brand', 'official');
const PREVIEW = process.argv.includes('--preview');

/** How much of the tile's side the mark takes; the rest is breathing room. */
const TileInset = 0.78;

/** The same, in height, for the wide tile. */
const WideInset = 0.8;

// PNG -----------------------------------------------------------------------

const CRC_TABLE = (() => {
  const table = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c;
  }
  return table;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const out = Buffer.alloc(12 + data.length);
  out.writeUInt32BE(data.length, 0);
  out.write(type, 4, 'ascii');
  data.copy(out, 8);
  out.writeUInt32BE(crc32(out.slice(4, 8 + data.length)), 8 + data.length);
  return out;
}

/**
 * Decodes an 8-bit, non-interlaced PNG (RGBA, RGB or greyscale) into an RGBA
 * image. `{ width, height, data }` with data of length width*height*4.
 */
function decodePNG(buf, name) {
  if (buf.readUInt32BE(0) !== 0x89504e47) {
    throw new Error('not a PNG: ' + (name || 'input'));
  }
  let off = 8;
  let width, height, bit, colorType, interlace;
  const idat = [];
  while (off + 8 <= buf.length) {
    const len = buf.readUInt32BE(off);
    const type = buf.slice(off + 4, off + 8).toString('ascii');
    const data = buf.slice(off + 8, off + 8 + len);
    if (type === 'IHDR') {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      bit = data[8];
      colorType = data[9];
      interlace = data[12];
    } else if (type === 'IDAT') {
      idat.push(data);
    } else if (type === 'IEND') {
      break;
    }
    off += 12 + len;
  }
  if (bit !== 8) throw new Error('unsupported depth: ' + bit);
  if (interlace !== 0) throw new Error('interlaced PNG is not supported');
  const channels = colorType === 6 ? 4 : colorType === 2 ? 3 : colorType === 0 ? 1 : -1;
  if (channels < 0) throw new Error('unsupported color type: ' + colorType);

  const raw = zlib.inflateSync(Buffer.concat(idat));
  const stride = width * channels;
  const out = Buffer.alloc(width * height * 4);
  let prev = Buffer.alloc(stride);
  let pos = 0;
  for (let y = 0; y < height; y++) {
    const filter = raw[pos++];
    const line = Buffer.from(raw.slice(pos, pos + stride));
    pos += stride;
    for (let i = 0; i < stride; i++) {
      const a = i >= channels ? line[i - channels] : 0;
      const b = prev[i];
      const c = i >= channels ? prev[i - channels] : 0;
      let v = line[i];
      if (filter === 1) v = (v + a) & 255;
      else if (filter === 2) v = (v + b) & 255;
      else if (filter === 3) v = (v + ((a + b) >> 1)) & 255;
      else if (filter === 4) {
        const p = a + b - c;
        const pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
        v = (v + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c)) & 255;
      }
      line[i] = v;
    }
    prev = line;
    for (let x = 0; x < width; x++) {
      const s = x * channels;
      const d = (y * width + x) * 4;
      if (channels === 4) {
        out[d] = line[s]; out[d + 1] = line[s + 1]; out[d + 2] = line[s + 2]; out[d + 3] = line[s + 3];
      } else if (channels === 3) {
        out[d] = line[s]; out[d + 1] = line[s + 1]; out[d + 2] = line[s + 2]; out[d + 3] = 255;
      } else {
        out[d] = out[d + 1] = out[d + 2] = line[s]; out[d + 3] = 255;
      }
    }
  }
  return { width, height, data: out };
}

/** Encodes an RGBA image as an 8-bit PNG (single row filter, filter 0). */
function encodePNG(img) {
  const { width, height, data } = img;
  const stride = 1 + width * 4;
  const raw = Buffer.alloc(height * stride);
  for (let y = 0; y < height; y++) {
    raw[y * stride] = 0;
    data.copy(raw, y * stride + 1, y * width * 4, (y + 1) * width * 4);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;  // bit depth
  ihdr[9] = 6;  // RGBA
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

function blank(w, h) {
  return { width: w, height: h, data: Buffer.alloc(w * h * 4) };
}

/**
 * The icon shrunk inside a transparent square frame: `fraction` is the share
 * of the side the icon covers (0.78 = 11% of breathing room per side).
 *
 * The bare icon is not enough: the tile model draws the image edge to edge, so
 * a mark that fills the asset looks zoomed and, on the wide tile, cropped. The
 * frame keeps the mark large but whole.
 */
// The frame around an inset mark is the mark's own green, edge to edge: on the
// Start screen a transparent frame is just empty space around a floating square,
// and a green of its own only shows as a second square inside the first.
function inset(img, size, fraction) {
  const side = Math.max(1, Math.round(size * fraction));
  const canvas = blank(size, size);
  const edge = [img.data[0], img.data[1], img.data[2], 255];
  for (let i = 0; i < canvas.data.length; i += 4) {
    canvas.data[i] = edge[0]; canvas.data[i + 1] = edge[1]; canvas.data[i + 2] = edge[2];
    canvas.data[i + 3] = edge[3];
  }
  const scaled = resample(img, side, side);
  const at = Math.round((size - side) / 2);
  for (let y = 0; y < side; y++) {
    scaled.data.copy(canvas.data, ((y + at) * size + at) * 4, y * side * 4, (y + 1) * side * 4);
  }
  return canvas;
}

/** The brand green: the tile's `BackgroundColor` in the manifest. */
const BRAND_GREEN = [0x45, 0xd8, 0x2c];

/**
 * The icon inside a wide frame the size of the wide tile.
 *
 * Not the same square image: the system scales it to fill 310x150 and crops
 * top and bottom, which is exactly the crop that showed. Here the mark is
 * whole at the centre, on the brand green, and the frame already carries the
 * tile's proportion.
 */
function banner(img, width, height, fraction) {
  const canvas = blank(width, height);
  for (let p = 0; p < width * height; p++) {
    canvas.data[p * 4] = BRAND_GREEN[0];
    canvas.data[p * 4 + 1] = BRAND_GREEN[1];
    canvas.data[p * 4 + 2] = BRAND_GREEN[2];
    canvas.data[p * 4 + 3] = 255;
  }

  const side = Math.max(1, Math.round(height * fraction));
  const scaled = resample(img, side, side);
  const atX = Math.round((width - side) / 2);
  const atY = Math.round((height - side) / 2);
  for (let y = 0; y < side; y++) {
    scaled.data.copy(canvas.data, ((y + atY) * width + atX) * 4, y * side * 4, (y + 1) * side * 4);
  }
  return canvas;
}

function clamp255(v) {
  return v < 0 ? 0 : v > 255 ? 255 : Math.round(v);
}

/**
 * Resamples with a triangular kernel, on alpha-premultiplied channels: in
 * reduction the triangle widens and averages the area (clean edges), in
 * enlargement it stays bilinear. The alpha is de-premultiplied afterwards.
 */
function resample(src, dstW, dstH) {
  const { width: sw, height: sh, data } = src;
  const pre = new Float32Array(sw * sh * 4);
  for (let p = 0; p < sw * sh; p++) {
    const a = data[p * 4 + 3] / 255;
    pre[p * 4] = data[p * 4] * a;
    pre[p * 4 + 1] = data[p * 4 + 1] * a;
    pre[p * 4 + 2] = data[p * 4 + 2] * a;
    pre[p * 4 + 3] = a;
  }

  const rows = new Float32Array(dstW * sh * 4);
  const rx = Math.max(1, sw / dstW);
  for (let y = 0; y < sh; y++) {
    for (let x = 0; x < dstW; x++) {
      const c = (x + 0.5) * sw / dstW - 0.5;
      let sum = 0, r = 0, g = 0, b = 0, a = 0;
      const from = Math.max(0, Math.ceil(c - rx));
      const to = Math.min(sw - 1, Math.floor(c + rx));
      for (let s = from; s <= to; s++) {
        const w = 1 - Math.abs(s - c) / rx;
        if (w <= 0) continue;
        const o = (y * sw + s) * 4;
        r += pre[o] * w; g += pre[o + 1] * w; b += pre[o + 2] * w; a += pre[o + 3] * w;
        sum += w;
      }
      if (sum <= 0) sum = 1;
      const o = (y * dstW + x) * 4;
      rows[o] = r / sum; rows[o + 1] = g / sum; rows[o + 2] = b / sum; rows[o + 3] = a / sum;
    }
  }

  const out = blank(dstW, dstH);
  const ry = Math.max(1, sh / dstH);
  for (let x = 0; x < dstW; x++) {
    for (let y = 0; y < dstH; y++) {
      const c = (y + 0.5) * sh / dstH - 0.5;
      let sum = 0, r = 0, g = 0, b = 0, a = 0;
      const from = Math.max(0, Math.ceil(c - ry));
      const to = Math.min(sh - 1, Math.floor(c + ry));
      for (let s = from; s <= to; s++) {
        const w = 1 - Math.abs(s - c) / ry;
        if (w <= 0) continue;
        const o = (s * dstW + x) * 4;
        r += rows[o] * w; g += rows[o + 1] * w; b += rows[o + 2] * w; a += rows[o + 3] * w;
        sum += w;
      }
      if (sum <= 0) sum = 1;
      const o = (y * dstW + x) * 4;
      const alpha = a / sum;
      if (alpha > 0) {
        out.data[o] = clamp255(r / sum / alpha);
        out.data[o + 1] = clamp255(g / sum / alpha);
        out.data[o + 2] = clamp255(b / sum / alpha);
      }
      out.data[o + 3] = clamp255(alpha * 255);
    }
  }
  return out;
}

// Preview -----------------------------------------------------------------

function preview(img, label, cols) {
  const rows = Math.max(1, Math.round(cols * 0.55));
  const ramp = ' .:-=+*#%@';
  console.log('\n  ' + label);
  for (let ry = 0; ry < rows; ry++) {
    let line = '';
    for (let rx = 0; rx < cols; rx++) {
      const x0 = Math.floor(rx * img.width / cols);
      const x1 = Math.max(x0 + 1, Math.floor((rx + 1) * img.width / cols));
      const y0 = Math.floor(ry * img.height / rows);
      const y1 = Math.max(y0 + 1, Math.floor((ry + 1) * img.height / rows));
      let sum = 0, n = 0;
      for (let y = y0; y < y1; y++) {
        for (let x = x0; x < x1; x++) {
          const o = (y * img.width + x) * 4;
          const a = img.data[o + 3] / 255;
          const lum = 0.299 * img.data[o] + 0.587 * img.data[o + 1] + 0.114 * img.data[o + 2];
          sum += lum * a + 255 * (1 - a); // transparency reads as white
          n++;
        }
      }
      const v = n ? sum / n : 255;
      line += ramp[Math.min(ramp.length - 1, Math.floor((255 - v) / 256 * ramp.length))];
    }
    console.log('  ' + line);
  }
}

// Main ----------------------------------------------------------------------

function copy(name, from) {
  fs.copyFileSync(path.join(OFFICIAL, from), path.join(OUT_DIR, name));
  console.log(`${name.padEnd(32)} copied from official/${from}`);
}

function write(name, img, from) {
  fs.writeFileSync(path.join(OUT_DIR, name), encodePNG(img));
  console.log(`${name.padEnd(32)} ${img.width}x${img.height} from ${from || 'the brand art'}`);
}

// The lock screen badge: one colour is all a badge gets. The bubble's green is
// painted white and everything else - the sticker's white edge, the field around
// it - is left out, so the mark reads in whatever colour the lock screen paints
// it. The edge keeps partial alpha, which is the antialiasing.
function badge(src, size) {
  const small = resample(src, size, size);
  for (let i = 0; i < small.data.length; i += 4) {
    const r = small.data[i];
    const g = small.data[i + 1];
    const b = small.data[i + 2];
    const green = g - Math.max(r, b);
    small.data[i] = 255;
    small.data[i + 1] = 255;
    small.data[i + 2] = 255;
    small.data[i + 3] = green > 0 ? Math.min(255, green * 6) : 0;
  }
  return small;
}

function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });

  // The splash and the chat background are the official package's own drawings.
  copy('SplashScreen.scale-240.png', 'SplashScreen-1152x1920.png');
  copy('ChatBackground.jpg', 'Background.jpg');

  // The identity mark is the bubble the released build drew
  // (official/logo.png): the green bubble in its white outline, on whatever
  // background the surface has. That is the mark a person recognises - the one
  // the history shows working - and both things drawn since, the flat square
  // and the sticker on a white card, read as "no logo".
  const mark = decodePNG(fs.readFileSync(path.join(OFFICIAL, 'logo.png')), 'logo.png');

  // The app-list icon is the bubble itself, as the released build drew it. Its
  // field is transparent and that is the point: the list's own background shows
  // around the mark instead of a card standing in for it.
  write('WhatsAppLogo.png', resample(mark, 160, 160), 'official/logo.png');

  // The tiles: the official green square - the bubble on the brand green -
  // inside the frame, so it does not look zoomed. The wide frame carries the
  // wide tile's proportion, or the system crops it.
  const icon = decodePNG(fs.readFileSync(path.join(OFFICIAL, 'Icon1.png')), 'Icon1.png');

  write('TileIcon.png', inset(icon, 200, TileInset), 'official/Icon1.png');
  write('TileIcon.scale-240.png', inset(icon, 480, TileInset), 'official/Icon1.png');
  write('TileIconWide.png', banner(icon, 310, 150, WideInset), 'official/Icon1.png');
  write('TileIconWide.scale-240.png', banner(icon, 744, 360, WideInset), 'official/Icon1.png');

  // The visuals Package.appxmanifest names, at the sizes it asks for: the same
  // green square and bubble, so every one of them carries the mark on whatever
  // background the shell paints. These were transparent placeholders copied
  // from the official package and drew as nothing on a coloured tile.
  write('Logo.scale-240.png', inset(icon, 360, TileInset), 'official/Icon1.png');
  write('SmallLogo.scale-240.png', inset(icon, 106, TileInset), 'official/Icon1.png');
  write('Square71x71Logo.scale-240.png', inset(icon, 170, TileInset), 'official/Icon1.png');
  write('WideLogo.scale-240.png', banner(icon, 744, 360, WideInset), 'official/Icon1.png');
  write('StoreLogo.scale-240.png', inset(icon, 120, TileInset), 'official/Icon1.png');

  // The lock screen badge, the one surface that is monochrome.
  write('LockScreenBadge.scale-100.png', badge(mark, 24), 'official/logo.png');
  write('LockScreenBadge.scale-240.png', badge(mark, 58), 'official/logo.png');

  if (PREVIEW) {
    preview(decodePNG(fs.readFileSync(path.join(OUT_DIR, 'TileIcon.scale-240.png'))), 'TileIcon.scale-240.png', 40);
    preview(decodePNG(fs.readFileSync(path.join(OUT_DIR, 'TileIconWide.scale-240.png'))), 'TileIconWide.scale-240.png', 60);
    preview(decodePNG(fs.readFileSync(path.join(OUT_DIR, 'Logo.scale-240.png'))), 'Logo.scale-240.png', 40);
  }
}

main();
