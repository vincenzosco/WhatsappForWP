#!/usr/bin/env node
/**
 * tools/check-icons.js
 *
 * Guard for the WP8.1 app icons. "Segoe MDL2 Assets" is a Windows 10 font and
 * is NOT present on Windows Phone 8.1, so an icon button using it renders
 * nothing. All icons live in App.xaml as PathGeometry resources and are
 * consumed as Data="{StaticResource IconX}".
 *
 * Usage:
 *   node tools/check-icons.js            # references + font check
 *   node tools/check-icons.js --preview  # + ASCII preview (needs ImageMagick)
 */
'use strict';

const { execFileSync, spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');
const APP_XAML = path.join(APP, 'App.xaml');
const PREVIEW = process.argv.includes('--preview');

function walk(dir, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'obj' || entry.name === 'bin') continue;
      walk(path.join(dir, entry.name), out);
    } else if (entry.name.endsWith('.xaml')) {
      out.push(path.join(dir, entry.name));
    }
  }
  return out;
}

const appXaml = fs.readFileSync(APP_XAML, 'utf8');
const defined = new Map();
for (const m of appXaml.matchAll(/<PathGeometry\s+x:Key="([^"]+)"\s+Figures="([^"]+)"/g)) {
  defined.set(m[1], m[2]);
}

const files = walk(APP, []).filter((f) => f !== APP_XAML);
const problems = [];
const used = new Set();

for (const file of files) {
  const rel = path.relative(ROOT, file);
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
  lines.forEach((line, i) => {
    if (/Segoe MDL2 Assets/.test(line)) {
      problems.push(`${rel}:${i + 1}: uses "Segoe MDL2 Assets", which WP8.1 does not have -> ${line.trim()}`);
    }
    for (const m of line.matchAll(/\{StaticResource (Icon[A-Za-z]+)\}/g)) {
      used.add(m[1]);
      if (!defined.has(m[1])) problems.push(`${rel}:${i + 1}: {StaticResource ${m[1]}} is not defined in App.xaml`);
    }
  });
}

for (const key of defined.keys()) {
  if (!used.has(key)) problems.push(`WhatsappApp/App.xaml: ${key} is defined but never used`);
}

if (problems.length) {
  console.log(problems.join('\n'));
  console.log(`\n${problems.length} icon problem(s).`);
  process.exit(1);
}
console.log(`OK: ${defined.size} icon(s) defined, ${used.size} reference(s) resolved.`);

if (PREVIEW) {
  const hasMagick = spawnSync('magick', ['-version'], { stdio: 'ignore' }).status === 0;
  if (!hasMagick) {
    console.log('preview skipped: ImageMagick (magick) not available');
  } else {
    for (const [name, figures] of defined) {
      const filled = /A1\.5,1\.5|L21\.5,12/.test(figures);
      const args = ['-size', '24x24', 'xc:black'];
      if (filled) args.push('-fill', 'white', '-stroke', 'none');
      else args.push('-fill', 'none', '-stroke', 'white', '-strokewidth', '2');
      args.push('-draw', `stroke-linecap round stroke-linejoin round path '${figures}'`,
        '-depth', '8', `/tmp/${name}.png`);
      execFileSync('magick', args);
      const txt = execFileSync('magick', [`/tmp/${name}.png`, '-resize', '30x30!', 'txt:-'],
        { encoding: 'utf8' });
      const grid = Array.from({ length: 30 }, () => Array(30).fill('.'));
      for (const line of txt.split('\n')) {
        const px = line.match(/^(\d+),(\d+): \(([\d.]+)(?:,[\d.]+)*\)/);
        if (px) grid[+px[2]][+px[1]] = Number(px[3]) > 100 ? '#' : '.';
      }
      console.log(`\n== ${name} ==`);
      for (const row of grid) console.log('  ' + row.join(''));
    }
  }
}
