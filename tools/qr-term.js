#!/usr/bin/env node
/**
 * tools/qr-term.js
 *
 * Disegna nel terminale il QR code di login che GOWA serve come PNG.
 *
 * Perché non basta mostrare il PNG: il QR di WhatsApp va inquadrato da un
 * telefono, quindi deve essere disegnato con moduli **quadrati** e con i colori
 * giusti. Un terminale ha celle più alte che larghe, per cui si disegna un
 * modulo per colonna e due moduli per riga usando i blocchi orizzontali
 * ("▀" con colore di primo piano sopra e di sfondo sotto): così il QR resta
 * quadrato e leggibile, e i colori sono neri/bianchi espliciti, quindi funziona
 * sia su terminale chiaro sia su terminale scuro.
 *
 * GOWA genera il PNG con skip2/go-qrcode a 512px: l'immagine è una scala a
 * blocchi della griglia di moduli, ma la dimensione in pixel non è un multiplo
 * esatto del numero di moduli (es. 65 moduli da 7px = 455px dentro 512px). Per
 * questo la griglia si ricostruisce così:
 *
 *   1. si binarizza il PNG (ImageMagick) e si trova il riquadro dei moduli
 *      scuri, cioè il QR senza il margine bianco (quiet zone);
 *   2. la prima riga del riquadro inizia con la marca di posizionamento, larga
 *      esattamente 7 moduli: la sua lunghezza dà il passo in pixel e quindi il
 *      numero di moduli (21 + 4k);
 *   3. si campiona il centro di ogni modulo e si verifica la ricostruzione: se
 *      ogni pixel dell'originale corrisponde al modulo sotto di lui, la griglia
 *      è quella giusta. Se non lo è si provano gli altri conteggi validi.
 *
 * Il punto 3 è la verifica che rende affidabile il disegno: se la griglia fosse
 * sbagliata di un modulo il QR sarebbe illeggibile, e il punteggio lo direbbe.
 *
 * Richiede ImageMagick 7 (`magick` in PATH), come tools/check-icons.js.
 *
 * Usage:
 *   node tools/qr-term.js --info <qr.png>                 # diagnosi della griglia
 *   node tools/qr-term.js --preview <qr.png>              # disegno a colori (ANSI)
 *   node tools/qr-term.js --preview --plain <qr.png>      # disegno senza colori
 *   node tools/qr-term.js --self-test                     # controlli automatici
 */
'use strict';

const { execFileSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

// Un QR può avere solo questi lati (21 moduli + 4 per ogni versione).
const VALID_COUNTS = [];
for (let count = 21; count <= 177; count += 4) VALID_COUNTS.push(count);

const DARK_THRESHOLD = 128;
const DEFAULT_QUIET_ZONE = 4;

// Blocchi orizzontali: indice "sopra,sotto" con 1 = modulo scuro.
const HALF_BLOCK = { '0,0': ' ', '1,0': '▀', '0,1': '▄', '1,1': '█' };
const RESET = '\x1b[0m';
const BLACK = '\x1b[38;5;0m\x1b[48;5;0m';
const WHITE = '\x1b[38;5;15m\x1b[48;5;15m';
const DARK_ON_LIGHT = '\x1b[38;5;0m\x1b[48;5;15m';
const LIGHT_ON_DARK = '\x1b[38;5;15m\x1b[48;5;0m';

function styleFor(top, bottom) {
  if (top && bottom) return BLACK;
  if (top) return DARK_ON_LIGHT;
  if (bottom) return LIGHT_ON_DARK;
  return WHITE;
}

/** Moduli [sopra, sotto] di una riga disegnata senza colori. */
function modulesFromPlain(line) {
  const out = [];
  for (const glyph of line) {
    if (glyph === ' ') out.push([false, false]);
    else if (glyph === '\u2580') out.push([true, false]);
    else if (glyph === '\u2584') out.push([false, true]);
    else if (glyph === '\u2588') out.push([true, true]);
  }
  return out;
}

/**
 * Moduli [sopra, sotto] di una riga disegnata a colori: il primo piano e' il
 * modulo sopra, lo sfondo quello sotto. E' il rovescio di renderHalfBlocks, e
 * serve a verificare che i due disegni mostrino lo stesso codice: senza di
 * esso un glifo sbagliato poteva invertire meta' delle celle in silenzio.
 */
function modulesFromAnsi(line) {
  const out = [];
  const token = /\x1b\[(38|48);5;(\d+)m|([\u2580\u2584\u2588 ])/g;
  let fgDark = false;
  let bgDark = false;
  let match;
  while ((match = token.exec(line)) !== null) {
    if (match[1]) {
      if (match[1] === '38') fgDark = match[2] === '0';
      else bgDark = match[2] === '0';
      continue;
    }
    const glyph = match[3];
    if (glyph === ' ') out.push([false, false]);
    else if (glyph === '\u2580') out.push([fgDark, bgDark]);   // blocco alto: sopra = primo piano
    else if (glyph === '\u2584') out.push([bgDark, fgDark]);   // blocco basso: sotto = primo piano
    else out.push([fgDark, bgDark]);                           // blocco pieno: tutto primo piano
  }
  return out;
}

/** Legge il PNG con ImageMagick e lo riduce a 1 bit per pixel (255/0). */
function readGrayPng(pngPath) {
  let format;
  try {
    format = execFileSync('magick', ['identify', '-format', '%w %h', pngPath], { encoding: 'utf8' });
  } catch (err) {
    throw new Error(
      `ImageMagick could not read ${pngPath}: ${err.message}\n` +
      'ImageMagick 7 is required (`magick` in PATH): brew install imagemagick',
    );
  }
  const parts = format.trim().split(/\s+/).map(Number);
  const width = parts[0];
  const height = parts[1];
  if (!width || !height || width !== height) {
    throw new Error(`the QR code must be square, but it is ${width}x${height}`);
  }

  const pixels = execFileSync(
    'magick',
    [pngPath, '-colorspace', 'Gray', '-threshold', '50%', '-depth', '8', 'gray:-'],
    { maxBuffer: 64 * 1024 * 1024 },
  );
  if (pixels.length !== width * height) {
    throw new Error(`unexpected pixels: ${pixels.length} instead of ${width * height}`);
  }
  return { width, height, pixels };
}

/** Riquadro dei moduli scuri: il QR senza il margine bianco. */
function findSymbol(pixels, size) {
  let minX = size;
  let minY = size;
  let maxX = -1;
  let maxY = -1;
  for (let y = 0; y < size; y++) {
    const row = y * size;
    for (let x = 0; x < size; x++) {
      if (pixels[row + x] < DARK_THRESHOLD) {
        if (x < minX) minX = x;
        if (x > maxX) maxX = x;
        if (y < minY) minY = y;
        if (y > maxY) maxY = y;
      }
    }
  }
  if (maxX < 0) throw new Error('the PNG contains no dark modules');
  return { minX, minY, width: maxX - minX + 1, height: maxY - minY + 1 };
}

function sampleModules(pixels, size, minX, minY, side, count) {
  const pitch = side / count;
  const modules = [];
  for (let my = 0; my < count; my++) {
    const cy = Math.min(size - 1, minY + Math.floor((my + 0.5) * pitch));
    const row = new Array(count);
    for (let mx = 0; mx < count; mx++) {
      const cx = Math.min(size - 1, minX + Math.floor((mx + 0.5) * pitch));
      row[mx] = pixels[cy * size + cx] < DARK_THRESHOLD;
    }
    modules.push(row);
  }
  return modules;
}

/** Quota di pixel che non corrispondono al modulo sotto di loro (0 = perfetto). */
function reconstructionError(pixels, size, minX, minY, side, modules) {
  const count = modules.length;
  const pitch = side / count;
  let bad = 0;
  for (let y = 0; y < side; y++) {
    const my = Math.min(count - 1, Math.floor(y / pitch));
    const row = modules[my];
    const base = (minY + y) * size + minX;
    for (let x = 0; x < side; x++) {
      const mx = Math.min(count - 1, Math.floor(x / pitch));
      if ((pixels[base + x] < DARK_THRESHOLD) !== row[mx]) bad++;
    }
  }
  return bad / (side * side);
}

function detectGrid(pixels, size) {
  const symbol = findSymbol(pixels, size);
  const side = Math.min(symbol.width, symbol.height);

  // La marca di posizionamento in alto a sinistra è 7 moduli: la sua lunghezza
  // nella prima riga del simbolo dà il passo, quindi una stima del conteggio.
  let run = 0;
  const firstRow = symbol.minY * size + symbol.minX;
  while (run < side && pixels[firstRow + run] < DARK_THRESHOLD) run++;
  const guessedPitch = run / 7;

  const candidates = [];
  if (guessedPitch > 0) {
    for (const count of VALID_COUNTS) {
      const pitch = side / count;
      if (Math.abs(pitch - guessedPitch) <= guessedPitch * 0.15) candidates.push(count);
    }
  }
  for (const count of VALID_COUNTS) if (!candidates.includes(count)) candidates.push(count);

  let best = null;
  for (const count of candidates) {
    const modules = sampleModules(pixels, size, symbol.minX, symbol.minY, side, count);
    const error = reconstructionError(pixels, size, symbol.minX, symbol.minY, side, modules);
    if (!best || error < best.error) best = { count, modules, error };
    // Ricostruzione perfetta: la griglia è dimostrata, nessun altro tentativo.
    if (error === 0 && count === candidates[0]) break;
  }

  if (best.error > 0.02) {
    throw new Error(
      `grid not recognised: the best ${best.count} modules explain only ` +
      `${((1 - best.error) * 100).toFixed(1)}% of the pixels. Is the PNG a valid QR code?`,
    );
  }

  return {
    count: best.count,
    pitch: side / best.count,
    side,
    originX: symbol.minX,
    originY: symbol.minY,
    error: best.error,
    modules: best.modules,
  };
}

/** Aggiunge il margine bianco (quiet zone) e impagina due moduli per riga. */
function renderHalfBlocks(modules, options) {
  const opts = options || {};
  const quietZone = opts.quietZone === undefined ? DEFAULT_QUIET_ZONE : opts.quietZone;
  const plain = !!opts.plain;
  const total = modules.length + quietZone * 2;
  const isDark = (y, x) => {
    const sy = y - quietZone;
    const sx = x - quietZone;
    if (sy < 0 || sx < 0 || sy >= modules.length || sx >= modules.length) return false;
    return modules[sy][sx];
  };

  const lines = [];
  for (let y = 0; y < total; y += 2) {
    let line = '';
    let style = null;
    for (let x = 0; x < total; x++) {
      const top = isDark(y, x);
      const bottom = y + 1 < total ? isDark(y + 1, x) : false;
      if (plain) {
        line += HALF_BLOCK[`${top ? 1 : 0},${bottom ? 1 : 0}`];
        continue;
      }
      // A colori si usa *sempre* il blocco alto (▀): il primo piano e' il modulo
      // sopra, lo sfondo quello sotto. Con i glifi ▄/█ i due colori si scambiano
      // e ogni cella chiaro-sopra/scuro-sotto veniva disegnata al contrario: il
      // QR risultava un mosaico invertito e non si leggeva.
      const next = styleFor(top, bottom);
      if (next !== style) {
        line += next;
        style = next;
      }
      line += '▀';
    }
    if (!plain && style !== null) line += RESET;
    lines.push(line);
  }
  return lines;
}

/** PNG -> griglia di moduli + righe pronte da stampare. */
function qrFromPng(pngPath, options) {
  const opts = options || {};
  const image = readGrayPng(pngPath);
  const grid = detectGrid(image.pixels, image.width);
  return {
    count: grid.count,
    pitch: grid.pitch,
    error: grid.error,
    modules: grid.modules,
    lines: renderHalfBlocks(grid.modules, {
      quietZone: opts.quietZone,
      plain: opts.plain,
    }),
  };
}

function main() {
  const args = process.argv.slice(2);
  const has = (flag) => args.includes(flag);
  const valueOf = (flag) => {
    const index = args.indexOf(flag);
    return index >= 0 ? args[index + 1] : undefined;
  };
  const file = args.find((a) => !a.startsWith('--') && /\.png$/i.test(a));

  if (has('--self-test')) {
    selfTest();
    return;
  }
  if (!file) {
    console.error('Usage: node tools/qr-term.js (--info | --preview) <qr.png> [--plain] [--quiet-zone <n>] [--self-test]');
    process.exit(2);
  }

  const quietZone = valueOf('--quiet-zone');
  const qr = qrFromPng(file, {
    quietZone: quietZone === undefined ? DEFAULT_QUIET_ZONE : Number(quietZone),
    plain: has('--plain'),
  });

  if (has('--info')) {
    console.log(`${file}: ${qr.count} modules, ${qr.pitch.toFixed(2)}px per module, ` +
      `reconstruction error ${(qr.error * 100).toFixed(2)}%`);
    const plain = renderHalfBlocks(qr.modules, {
      quietZone: quietZone === undefined ? DEFAULT_QUIET_ZONE : Number(quietZone),
      plain: true,
    });
    console.log(`drawing: ${plain.length} rows x ${plain[0].length} columns ` +
      `(2 modules per row, ${qr.count} modules + margin)`);
    return;
  }
  if (has('--preview')) {
    console.log(qr.lines.join('\n'));
    return;
  }
  console.error('No mode selected: use --info or --preview.');
  process.exit(2);
}

// ─── Controlli automatici ────────────────────────────────────────────────────

function withMagick(fn) {
  try {
    execFileSync('magick', ['-version'], { stdio: 'ignore' });
  } catch (err) {
    console.log('SKIP: ImageMagick 7 (magick) not available');
    return;
  }
  fn();
}

/**
 * Costruisce un PNG che imita un QR: marche di posizionamento agli angoli,
 * bordo di 4 moduli e riempimento pseudo-casuale (deterministico). Serve per
 * verificare che detectGrid ritrovi conteggio e griglia senza dipendere da un
 * QR reale, che cambierebbe a ogni esecuzione.
 */
function buildSyntheticQr(dir, { count, moduleSize, margin }) {
  const size = (count + margin * 2) * moduleSize;
  const isDark = (x, y) => {
    const mx = Math.floor(x / moduleSize) - margin;
    const my = Math.floor(y / moduleSize) - margin;
    if (mx < 0 || my < 0 || mx >= count || my >= count) return false;
    const inFinder = (fx, fy) => mx >= fx && mx < fx + 7 && my >= fy && my < fy + 7;
    const ring = (fx, fy) => {
      const dx = mx - fx;
      const dy = my - fy;
      const edge = dx === 0 || dx === 6 || dy === 0 || dy === 6;
      const core = dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4;
      return edge || core;
    };
    if (inFinder(0, 0)) return ring(0, 0);
    if (inFinder(count - 7, 0)) return ring(count - 7, 0);
    if (inFinder(0, count - 7)) return ring(0, count - 7);
    // Riempimento deterministico (riproduce moduli grandi e piccoli).
    return ((mx * 7919 + my * 104729) % 5) < 2;
  };

  return { size, isDark };
}

function renderSyntheticPng(target, spec) {
  const { size, isDark } = spec;
  const pixels = Buffer.alloc(size * size);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) pixels[y * size + x] = isDark(x, y) ? 0 : 255;
  }
  execFileSync('magick', ['-size', `${size}x${size}`, '-depth', '8', 'gray:-', target], {
    input: pixels,
    maxBuffer: 64 * 1024 * 1024,
  });
}

function selfTest() {
  withMagick(() => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'qr-term-'));
    const cases = [
      { count: 65, moduleSize: 7, margin: 4 },
      { count: 25, moduleSize: 12, margin: 4 },
      { count: 41, moduleSize: 3, margin: 6 },
    ];
    let failures = 0;
    for (const testCase of cases) {
      const file = path.join(dir, `qr-${testCase.count}.png`);
      const spec = buildSyntheticQr(dir, testCase);
      renderSyntheticPng(file, spec);
      const qr = qrFromPng(file);
      const ok = qr.count === testCase.count && qr.error === 0;
      if (!ok) failures++;
      console.log(`${ok ? 'OK  ' : 'FAIL'} ${testCase.count} modules: detected ${qr.count}, ` +
        `error ${(qr.error * 100).toFixed(2)}%`);
    }

    // Il disegno deve avere il margine bianco e due moduli per riga.
    const file = path.join(dir, 'qr-25.png');
    const qr = qrFromPng(file);
    const lines = renderHalfBlocks(qr.modules, { quietZone: 2, plain: true });
    const expectedColumns = 25 + 4;
    const expectedLines = Math.ceil(expectedColumns / 2);
    const shapeOk = lines.length === expectedLines && lines.every((l) => l.length === expectedColumns);
    const marginOk = lines[0].trim() === '' && lines[lines.length - 1].trim() === '';
    if (!(shapeOk && marginOk)) failures++;
    console.log(`${shapeOk && marginOk ? 'OK  ' : 'FAIL'} drawing: ${lines.length} rows x ` +
      `${lines[0].length} columns, white margin present`);

    // Il colore cambia solo quando cambia la coppia di moduli.
    const ansi = renderHalfBlocks(qr.modules, { quietZone: 1, plain: false });
    const ansiOk = ansi[0].startsWith('\x1b[') && ansi[0].endsWith(RESET);
    if (!ansiOk) failures++;
    console.log(`${ansiOk ? 'OK  ' : 'FAIL'} ANSI: colour sequences present and reset at the end of a line`);

    // Il disegno a colori, riletto colore per colore, deve mostrare gli stessi
    // moduli di quello senza colori. E' l'unico controllo che si accorge di un
    // glifo sbagliato: ▄ con primo piano chiaro e sfondo scuro inverte la cella.
    const mono = renderHalfBlocks(qr.modules, { quietZone: 1, plain: true });
    let coloursOk = mono.length === ansi.length && mono[0].length > 0;
    for (let i = 0; coloursOk && i < ansi.length; i++) {
      const a = modulesFromPlain(mono[i]);
      const b = modulesFromAnsi(ansi[i]);
      coloursOk = a.length === b.length
        && a.every((cell, k) => cell[0] === b[k][0] && cell[1] === b[k][1]);
    }
    if (!coloursOk) failures++;
    console.log(`${coloursOk ? 'OK  ' : 'FAIL'} colori: il disegno a colori mostra gli stessi moduli`);

    fs.rmSync(dir, { recursive: true, force: true });
    if (failures > 0) {
      console.error(`\n${failures} check(s) failed.`);
      process.exit(1);
    }
    console.log('\nAll checks passed.');
  });
}

module.exports = {
  VALID_COUNTS,
  DEFAULT_QUIET_ZONE,
  readGrayPng,
  findSymbol,
  sampleModules,
  reconstructionError,
  detectGrid,
  renderHalfBlocks,
  qrFromPng,
};

if (require.main === module) main();
