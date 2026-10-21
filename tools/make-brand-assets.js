#!/usr/bin/env node
/**
 * tools/make-brand-assets.js
 *
 * Riscrive tile, icone, splash e sfondo di WhatsappApp/Assets/ partendo dagli
 * asset del pacchetto ufficiale WhatsApp per WP8.1, copiati in
 * tools/brand/official/ (Icon1.png, Images/Tile*.png, Images/Background.jpg,
 * Images/logo.png, SplashScreenImage.jpg).
 *
 * Perche' non si disegna piu' il marchio: il pacchetto ufficiale porta gia' il
 * disegno giusto per ogni misura, cioe' il glifo bianco su trasparente per le
 * tile (che il manifest mette sul verde #45D82C) e l'icona verde per la lista
 * app. Ricomporlo a mano dava una tile diversa da quella vera.
 *
 * Le uniche misure che il pacchetto non ha sono le due dell'icona della tile
 * (200 e 480), lo StoreLogo (120) e lo splash che il manifest chiede a 1152x1920:
 * quelle si ricavano da Icon1.png e dallo splash 480x800 con un filtro
 * triangolare, e lo splash ricavato e' committato in official/ perche' il
 * sorgente e' un JPEG e questo script legge solo PNG.
 *
 * I PNG generati sono committati, quindi lo script gira solo quando cambia la
 * grafica.
 *
 * Usage:
 *   node tools/make-brand-assets.js              # riscrive i file
 *   node tools/make-brand-assets.js --preview    # riscrive + anteprima ASCII
 */
'use strict';

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const ROOT = path.resolve(__dirname, '..');
const OUT_DIR = path.join(ROOT, 'WhatsappApp', 'Assets');
const OFFICIAL = path.join(__dirname, 'brand', 'official');
const PREVIEW = process.argv.includes('--preview');

/** Quanto del lato della tile occupa il marchio: il resto e' respiro. */
const TileInset = 0.78;

/** Lo stesso, in altezza, per la tile larga. */
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
 * Decodifica un PNG a 8 bit, non interlacciato (RGBA, RGB o scala di grigi) in
 * un'immagine RGBA. `{ width, height, data }` con data lungo width*height*4.
 */
function decodePNG(buf, name) {
  if (buf.readUInt32BE(0) !== 0x89504e47) {
    throw new Error('non e un PNG: ' + (name || 'input'));
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

/**
 * L'icona rimpicciolita dentro una cornice quadrata trasparente: `fraction` e'
 * la parte del lato che l'icona occupa (0.78 = 11% di respiro per lato).
 *
 * Perche' non basta l'icona intera: il modello della tile disegna l'immagine a
 * piena superficie, quindi un marchio che riempie l'asset sembra zoommato e,
 * sulla tile larga, tagliato. Con la cornice il marchio resta grande ma intero.
 */
function inset(img, size, fraction) {
  const side = Math.max(1, Math.round(size * fraction));
  const canvas = blank(size, size);
  const scaled = resample(img, side, side);
  const at = Math.round((size - side) / 2);
  for (let y = 0; y < side; y++) {
    scaled.data.copy(canvas.data, ((y + at) * size + at) * 4, y * side * 4, (y + 1) * side * 4);
  }
  return canvas;
}

/** Il verde del marchio: il `BackgroundColor` della tile nel manifest. */
const BRAND_GREEN = [0x45, 0xd8, 0x2c];

/**
 * L'icona in una cornice larga della misura della tile larga.
 *
 * Perche' non la stessa immagine quadrata: il sistema la scala per riempire
 * 310x150 e ne taglia sopra e sotto, che e' il taglio che si vedeva. Qui il
 * marchio e' intero al centro, sul verde del marchio, e la cornice ha gia' la
 * proporzione della tile.
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

function copy(name, from) {
  fs.copyFileSync(path.join(OFFICIAL, from), path.join(OUT_DIR, name));
  console.log(`${name.padEnd(32)} copied from official/${from}`);
}

function write(name, img) {
  fs.writeFileSync(path.join(OUT_DIR, name), encodePNG(img));
  console.log(`${name.padEnd(32)} ${img.width}x${img.height} from official/Icon1.png`);
}

function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });

  // Le tile e le icone che il pacchetto ufficiale ha gia' alle misure che
  // WinRT chiede al 240%: 150->360, 71->170, 44->106, 310x150->744x360.
  copy('Logo.scale-240.png', 'Tile150x150.png');
  copy('SmallLogo.scale-240.png', 'Tile44x44.png');
  copy('Square71x71Logo.scale-240.png', 'Tile71x71.png');
  copy('WideLogo.scale-240.png', 'Tile310x150.png');
  copy('SplashScreen.scale-240.png', 'SplashScreen-1152x1920.png');
  copy('ChatBackground.jpg', 'Background.jpg');
  copy('WhatsAppLogo.png', 'logo.png');

  // L'icona della tile (modello IconWithBadge) e lo StoreLogo: ricavati
  // dall'icona verde dell'app, che il pacchetto ha a 419x418.
  const icon = decodePNG(fs.readFileSync(path.join(OFFICIAL, 'Icon1.png')), 'Icon1.png');

  // La tile: il marchio dentro una cornice, cosi' non sembra zoommato. La
  // cornice della tile larga ha la proporzione della tile, altrimenti il
  // sistema la ritaglia.
  write('TileIcon.png', inset(icon, 200, TileInset));
  write('TileIcon.scale-240.png', inset(icon, 480, TileInset));
  write('TileIconWide.png', banner(icon, 310, 150, WideInset));
  write('TileIconWide.scale-240.png', banner(icon, 744, 360, WideInset));
  write('StoreLogo.scale-240.png', resample(icon, 120, 120));

  if (PREVIEW) {
    preview(decodePNG(fs.readFileSync(path.join(OUT_DIR, 'TileIcon.scale-240.png'))), 'TileIcon.scale-240.png', 40);
    preview(decodePNG(fs.readFileSync(path.join(OUT_DIR, 'TileIconWide.scale-240.png'))), 'TileIconWide.scale-240.png', 60);
    preview(decodePNG(fs.readFileSync(path.join(OUT_DIR, 'Logo.scale-240.png'))), 'Logo.scale-240.png', 40);
  }
}

main();
