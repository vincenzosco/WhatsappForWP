#!/usr/bin/env node
/**
 * tools/make-brand-assets.js
 *
 * Regenerates the WhatsApp-branded tiles, icons and splash screen of the
 * Windows Phone 8.1 app into WhatsappApp/Assets/. The mark is drawn as
 * vector geometry (white speech bubble with the phone handset punched out)
 * on the WhatsApp green gradient, so no source image and no font is needed.
 *
 * Requires ImageMagick 7 (`magick` in PATH). The generated PNGs are
 * committed, so this script only has to run when the artwork changes.
 *
 * Usage:
 *   node tools/make-brand-assets.js              # write the PNGs
 *   node tools/make-brand-assets.js --preview    # write them + ASCII preview
 */
'use strict';

const { execFileSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const OUT_DIR = path.join(ROOT, 'WhatsappApp', 'Assets');
const PREVIEW = process.argv.includes('--preview');

// WhatsApp palette
const GREEN_LIGHT = '#25D366'; // icon gradient, top
const GREEN_MID = '#128C7E';   // icon gradient, bottom
const GREEN_DARK = '#075E54';  // splash gradient, bottom (same as the app header)
const INK = '#FFFFFF';

const MASTER = 1024; // master glyph resolution

function magick(args) {
  execFileSync('magick', args, { stdio: ['ignore', 'pipe', 'pipe'] });
}

/**
 * White speech bubble (circle + tail) with the handset punched out, on a
 * transparent square of MASTER x MASTER pixels.
 */
function buildGlyph() {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'wa-glyph-'));
  const bubble = path.join(tmp, 'bubble.png');
  const handset = path.join(tmp, 'handset.png');
  const glyph = path.join(tmp, 'glyph.png');

  const P = (v) => (v * MASTER).toFixed(2);
  const deg = (d) => (d * Math.PI) / 180;

  // --- bubble: circle centred at (0.50, 0.46) with radius 0.415 ---
  const cx = 0.5, cy = 0.46, r = 0.415;
  // --- tail: wedge whose base is a chord of the circle (148deg..112deg),
  //     pointing to the bottom-left (0.055, 0.95) ---
  const onCircle = (angle) => [cx + r * Math.cos(deg(angle)), cy + r * Math.sin(deg(angle))];
  const [tx1, ty1] = onCircle(148);
  const [tx2, ty2] = onCircle(112);
  const tipX = 0.055, tipY = 0.95; // tail points to the bottom-left
  const tail = `M ${P(tx1)},${P(ty1)} L ${P(tipX)},${P(tipY)} L ${P(tx2)},${P(ty2)} Z`;

  magick([
    '-size', `${MASTER}x${MASTER}`, 'xc:none',
    '-fill', INK, '-stroke', 'none',
    '-draw', `circle ${P(cx)},${P(cy)} ${P(cx)},${P(cy - r)}`,
    '-draw', `path '${tail}'`,
    bubble
  ]);

  // --- handset: thick arc (rounded caps) spanning 45deg -> 225deg,
  //     i.e. convex side towards the bottom-left, opening to the top-right ---
  const hcx = 0.5, hcy = 0.44, hr = 0.19, ht = 0.15;
  const k = Math.SQRT1_2;
  const sx = hcx + hr * k, sy = hcy + hr * k;   // 45deg  (bottom-right)
  const ex = hcx - hr * k, ey = hcy - hr * k;   // 225deg (top-left)
  const arc = `M ${P(sx)},${P(sy)} A ${P(hr)},${P(hr)} 0 0 1 ${P(ex)},${P(ey)}`;

  magick([
    '-size', `${MASTER}x${MASTER}`, 'xc:none',
    '-fill', 'none', '-stroke', '#000000', '-strokewidth', P(ht),
    '-draw', `stroke-linecap round path '${arc}'`,
    handset
  ]);

  // punch the handset out of the bubble
  magick([bubble, handset, '-compose', 'DstOut', '-composite', glyph]);
  fs.rmSync(bubble);
  fs.rmSync(handset);

  const master = path.join(OUT_DIR, '.glyph-master.png');
  fs.copyFileSync(glyph, master);
  fs.rmSync(tmp, { recursive: true, force: true });
  return master;
}

/** Green gradient of w x h pixels. */
function background(w, h, top, bottom, out) {
  magick(['-size', `${w}x${h}`, `gradient:${top}-${bottom}`, '-depth', '8', out]);
}

/** Composites the mark (resized to markSize) over a gradient background. */
function compose(w, h, top, bottom, markSize, out, yOffset = 0) {
  const glyph = path.join(OUT_DIR, '.glyph-master.png');
  const args = ['-size', `${w}x${h}`, `gradient:${top}-${bottom}`,
    '(', glyph, '-resize', `${markSize}x${markSize}`, ')',
    '-gravity', 'center', '-geometry', `+0${yOffset >= 0 ? '+' : ''}${yOffset}`,
    '-composite', '-depth', '8', out];
  magick(args);
}

/** ASCII preview so the mark can be checked without opening the image. */
function preview(file, cols = 46, crop = null) {
  const rows = Math.max(1, Math.round((cols * 0.55)));
  const args = crop ? [file, '-crop', crop, '+repage'] : [file];
  const out = execFileSync('magick',
    [...args, '-resize', `${cols}x${rows}!`, 'txt:-'],
    { encoding: 'utf8' });
  const grid = Array.from({ length: rows }, () => Array(cols).fill(' '));
  for (const line of out.split('\n')) {
    const m = line.match(/^(\d+),(\d+): \((\d+),(\d+),(\d+)(?:,(\d+))?\)/);
    if (!m) continue;
    const [, x, y, r, g, b, a] = m.map(Number);
    const alpha = a === undefined ? 255 : a;
    const cell = alpha < 40 ? ' '
      : (r > 200 && g > 200 && b > 200) ? '#'
        : (g > 90 && g > r + 20 && g > b + 20) ? '.'
          : '+';
    grid[y][x] = cell;
  }
  console.log(`\n  ${path.relative(ROOT, file)}`);
  for (const row of grid) console.log('  ' + row.join(''));
}

function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const glyph = buildGlyph();

  // 150% / 240% qualified assets, same pixel sizes as the template ones.
  const square = [
    ['Logo.scale-240.png', 360, 0.82],
    ['SmallLogo.scale-240.png', 106, 0.88],
    ['Square71x71Logo.scale-240.png', 170, 0.84],
    ['StoreLogo.scale-240.png', 120, 0.84]
  ];
  for (const [name, size, mark] of square) {
    compose(size, size, GREEN_LIGHT, GREEN_MID, Math.round(size * mark),
      path.join(OUT_DIR, name));
  }

  // wide tile
  compose(744, 360, GREEN_LIGHT, GREEN_MID, Math.round(360 * 0.84),
    path.join(OUT_DIR, 'WideLogo.scale-240.png'));

  // splash: deep green gradient, mark slightly above the vertical centre,
  // ending on the same #075E54 the app header uses.
  const splash = path.join(OUT_DIR, 'SplashScreen.scale-240.png');
  const splashMark = 530;
  const splashMarkY = Math.round(1920 * 0.42); // mark centre, above the middle
  // -geometry is an offset from the centre of the image
  compose(1152, 1920, GREEN_MID, GREEN_DARK, splashMark, splash, splashMarkY - 960);
  fs.rmSync(glyph);

  const written = fs.readdirSync(OUT_DIR).filter((f) => f.endsWith('.png')).sort();
  for (const f of written) {
    const { size } = fs.statSync(path.join(OUT_DIR, f));
    console.log(`${f.padEnd(32)} ${String(size).padStart(7)} bytes`);
  }

  if (PREVIEW) {
    preview(path.join(OUT_DIR, 'Logo.scale-240.png'));
    const half = Math.round(splashMark * 0.65);
    preview(path.join(OUT_DIR, 'SplashScreen.scale-240.png'), 46,
      `${half * 2}x${half * 2}+${576 - half}+${splashMarkY - half}`);
  }
}

main();
