#!/usr/bin/env node
/**
 * tools/make-brand-assets.js
 *
 * Riscrive tile, icone e splash screen del marchio WhatsApp in
 * WhatsappApp/Assets/.
 *
 * Il marchio non e' piu' disegnato a vettori: si parte dall'immagine di
 * riferimento committata in tools/brand/logo-source.png (il logo verde su fondo
 * bianco), si toglie il fondo e lo si compone su trasparente (tile), su bianco
 * (icone quadrate e tile larga) o sul gradiente verde (splash).
 *
 * Non serve nessuno strumento esterno: il PNG viene letto e scritto con lo
 * zlib di Node. I PNG generati sono committati, quindi lo script gira solo
 * quando cambia la grafica.
 *
 * Usage:
 *   node tools/make-brand-assets.js              # riscrive i PNG
 *   node tools/make-brand-assets.js --preview    # riscrive + anteprima ASCII
 */
'use strict';

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const ROOT = path.resolve(__dirname, '..');
const OUT_DIR = path.join(ROOT, 'WhatsappApp', 'Assets');
const SOURCE = path.join(__dirname, 'brand', 'logo-source.png');
const PREVIEW = process.argv.includes('--preview');

// Palette WhatsApp. Le icone quadrate e la tile larga sono bianche: il marchio
// e' verde, e su un fondo verde non si distingueva.
const WHITE = '#FFFFFF';       // sfondo di icone e tile
const GREEN_MID = '#128C7E';   // splash, in alto
const GREEN_DARK = '#075E54';  // splash, in basso (come l'header dell'app)

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
 * Decodifica un PNG a 8 bit, non interlacciato (RGBA, RGB o scala di grigi) in
 * un'immagine RGBA. `{ width, height, data }` con data lungo width*height*4.
 */
function decodePNG(buf) {
  if (buf.readUInt32BE(0) !== 0x89504e47) {
    throw new Error('non e un PNG: ' + SOURCE);
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
  if (bit !== 8) throw new Error('profondita non supportata: ' + bit);
  if (interlace !== 0) throw new Error('PNG interlacciato non supportato');
  const channels = colorType === 6 ? 4 : colorType === 2 ? 3 : colorType === 0 ? 1 : -1;
  if (channels < 0) throw new Error('tipo colore non supportato: ' + colorType);

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

/** Codifica un'immagine RGBA come PNG a 8 bit (una riga, filtro 0). */
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

function hexToRgb(hex) {
  const n = parseInt(hex.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function clamp255(v) {
  return v < 0 ? 0 : v > 255 ? 255 : Math.round(v);
}

// Immagini ------------------------------------------------------------------

/** Gradiente verticale dal colore in alto a quello in basso. */
function gradient(w, h, topHex, bottomHex) {
  const top = hexToRgb(topHex);
  const bottom = hexToRgb(bottomHex);
  const img = blank(w, h);
  for (let y = 0; y < h; y++) {
    const f = h === 1 ? 0 : y / (h - 1);
    const r = Math.round(top[0] + (bottom[0] - top[0]) * f);
    const g = Math.round(top[1] + (bottom[1] - top[1]) * f);
    const b = Math.round(top[2] + (bottom[2] - top[2]) * f);
    for (let x = 0; x < w; x++) {
      const o = (y * w + x) * 4;
      img.data[o] = r; img.data[o + 1] = g; img.data[o + 2] = b; img.data[o + 3] = 255;
    }
  }
  return img;
}

/** Un riempimento pieno: lo sfondo delle icone, che non e' piu' un gradiente. */
function solid(w, h, hex) {
  const c = hexToRgb(hex);
  const img = blank(w, h);
  for (let p = 0; p < w * h; p++) {
    const o = p * 4;
    img.data[o] = c[0]; img.data[o + 1] = c[1]; img.data[o + 2] = c[2]; img.data[o + 3] = 255;
  }
  return img;
}

/**
 * Il marchio, ricavato da tools/brand/logo-source.png.
 *
 * L'immagine e' il logo verde su fondo bianco, con un'ombra grigia intorno.
 * L'alpha viene dalla "verdosita'" del pixel - g meno il maggiore fra r e b -
 * cosi' il bianco e il grigio dell'ombra, che hanno r=g=b, spariscono mentre i
 * bordi del verde restano morbidi; la cornetta bianca interna diventa il buco
 * trasparente. Il marchio e' di un verde piatto, quindi tutti i pixel opachi
 * prendono il colore pieno (il pixel piu' verde dell'immagine) invece del
 * colore mescolato col bianco sui bordi: nessun alone. Si ritaglia il riquadro
 * dei pixel opachi, cosi' l'ombra non lascia margine.
 */
function loadMark() {
  const img = decodePNG(fs.readFileSync(SOURCE));
  const { width, height, data } = img;
  const greenOf = (i) => data[i + 1] - Math.max(data[i], data[i + 2]);

  let maxGreen = 0;
  let pure = [0, 0, 0];
  for (let p = 0; p < width * height; p++) {
    const i = p * 4;
    const v = greenOf(i);
    if (v > maxGreen) {
      maxGreen = v;
      pure = [data[i], data[i + 1], data[i + 2]];
    }
  }
  if (maxGreen <= 0) {
    throw new Error('marchio vuoto: ' + path.relative(ROOT, SOURCE) + ' non contiene verde');
  }

  let minX = width, minY = height, maxX = -1, maxY = -1;
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const i = (y * width + x) * 4;
      const v = greenOf(i);
      let a = v <= 0 ? 0 : Math.min(1, v / maxGreen);
      if (a < 0.02) a = 0;
      if (a === 0) {
        data[i + 3] = 0;
        continue;
      }
      data[i] = pure[0];
      data[i + 1] = pure[1];
      data[i + 2] = pure[2];
      data[i + 3] = clamp255(a * 255);
      if (x < minX) minX = x;
      if (x > maxX) maxX = x;
      if (y < minY) minY = y;
      if (y > maxY) maxY = y;
    }
  }
  if (maxX < 0) {
    throw new Error('marchio vuoto: ' + path.relative(ROOT, SOURCE) + ' non contiene verde');
  }

  const w = maxX - minX + 1;
  const h = maxY - minY + 1;
  const out = blank(w, h);
  for (let y = 0; y < h; y++) {
    const from = ((y + minY) * width + minX) * 4;
    data.copy(out.data, y * w * 4, from, from + w * 4);
  }
  return out;
}

/**
 * Ridimensiona con un kernel triangolare, su canali premoltiplicati per
 * l'alpha: in riduzione il triangolo si allarga e media l'area (bordi puliti),
 * in ingrandimento resta un bilineare. L'alpha viene poi de-premoltiplicato.
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

/** Compone src su dst con alpha over, nell'angolo (dx, dy). */
function draw(dst, src, dx, dy) {
  for (let y = 0; y < src.height; y++) {
    const ty = y + dy;
    if (ty < 0 || ty >= dst.height) continue;
    for (let x = 0; x < src.width; x++) {
      const tx = x + dx;
      if (tx < 0 || tx >= dst.width) continue;
      const s = (y * src.width + x) * 4;
      const a = src.data[s + 3] / 255;
      if (a <= 0) continue;
      const d = (ty * dst.width + tx) * 4;
      for (let c = 0; c < 3; c++) {
        dst.data[d + c] = clamp255(src.data[s + c] * a + dst.data[d + c] * (1 - a));
      }
      dst.data[d + 3] = clamp255(a * 255 + dst.data[d + 3] * (1 - a));
    }
  }
}

/**
 * Compone il marchio, largo `inner` pixel, al centro di un canvas w x h
 * (trasparente, o sul gradiente passato in `background`), spostato di
 * `yOffset` dal centro verticale.
 */
function placeMark(mark, w, h, inner, background, yOffset) {
  const img = background || blank(w, h);
  const m = resample(mark, Math.max(1, Math.round(inner)), Math.max(1, Math.round(inner)));
  draw(img, m, Math.round((w - m.width) / 2), Math.round((h - m.height) / 2) + (yOffset || 0));
  return img;
}

// Anteprima -----------------------------------------------------------------

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
          sum += lum * a + 255 * (1 - a); // il trasparente si legge come bianco
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

function write(name, img) {
  const file = path.join(OUT_DIR, name);
  fs.writeFileSync(file, encodePNG(img));
  return file;
}

function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const mark = loadMark();

  // L'icona della tile (modello IconWithBadge): il marchio su sfondo
  // trasparente, alla misura che il modello chiede (almeno 200x200) e alla sua
  // versione al 240%. Il marchio NON va disegnato a tutta tile: a tutta tile il
  // disegno sembra tagliato e ingrandito su una tile da 150 px. La misura
  // interna e' la stessa proporzione dei logo del manifest (vedi `square`).
  const TILE_MARK = 0.82;
  const tiles = [['TileIcon.png', 200], ['TileIcon.scale-240.png', 480]];
  for (const [name, size] of tiles) {
    write(name, placeMark(mark, size, size, size * TILE_MARK));
  }

  // Asset qualificati al 240%, stesse misure in pixel di prima.
  const square = [
    ['Logo.scale-240.png', 360, 0.82],
    ['SmallLogo.scale-240.png', 106, 0.88],
    ['Square71x71Logo.scale-240.png', 170, 0.84],
    ['StoreLogo.scale-240.png', 120, 0.84],
  ];
  for (const [name, size, frac] of square) {
    write(name, placeMark(mark, size, size, size * frac, solid(size, size, WHITE)));
  }

  // tile larga
  write('WideLogo.scale-240.png',
    placeMark(mark, 744, 360, 360 * 0.84, solid(744, 360, WHITE)));

  // splash: gradiente verde profondo, marchio poco sopra il centro verticale,
  // che finisce sullo stesso #075E54 dell'header dell'app.
  const splashMark = 530;
  const splashCentreY = Math.round(1920 * 0.42);
  const splash = placeMark(mark, 1152, 1920, splashMark,
    gradient(1152, 1920, GREEN_MID, GREEN_DARK), splashCentreY - 960);
  write('SplashScreen.scale-240.png', splash);

  const written = fs.readdirSync(OUT_DIR).filter((f) => f.endsWith('.png')).sort();
  for (const f of written) {
    const { size } = fs.statSync(path.join(OUT_DIR, f));
    console.log(`${f.padEnd(32)} ${String(size).padStart(7)} bytes`);
  }

  if (PREVIEW) {
    preview(decodePNG(fs.readFileSync(path.join(OUT_DIR, 'TileIcon.scale-240.png'))),
      'TileIcon.scale-240.png (marchio su trasparente)', 46);
    preview(decodePNG(fs.readFileSync(path.join(OUT_DIR, 'Logo.scale-240.png'))),
      'Logo.scale-240.png (marchio su bianco)', 46);
    preview(splash, 'SplashScreen.scale-240.png', 46);
  }
}

main();
