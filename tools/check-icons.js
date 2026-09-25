#!/usr/bin/env node
/**
 * tools/check-icons.js
 *
 * Guard for the WP8.1 app icons. "Segoe MDL2 Assets" is a Windows 10 font and
 * is NOT present on Windows Phone 8.1, so an icon button using it renders
 * nothing. All icons live in App.xaml as PathGeometry resources and are
 * consumed as Data="{StaticResource IconX}".
 *
 * The geometries are written in *element* form (PathFigure + LineSegment /
 * PolyLineSegment / ArcSegment). The WP8.1 XAML compiler rejects the path
 * mini-language in PathGeometry.Figures with
 *
 *   The TypeConverter for "PathFigureCollection" does not support converting
 *   from a string.
 *
 * so a Figures="M..." attribute is a build error, not a style preference, and
 * this guard fails on it by name. The element form is the one the Windows
 * Phone Silverlight XAML vocabulary documents.
 *
 * Usage:
 *   node tools/check-icons.js            # form + references + font check
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

const POINT = /^-?\d+(\.\d+)?,-?\d+(\.\d+)?$/;
const POINTS = /^-?\d+(\.\d+)?,-?\d+(\.\d+)?(\s+-?\d+(\.\d+)?,-?\d+(\.\d+)?)*$/;
const SEGMENT = /<(LineSegment|PolyLineSegment|ArcSegment)(?=[\s/>])([^>]*?)\/?>/g;

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

function attribute(tag, name) {
  const m = tag.match(new RegExp(`\\b${name}="([^"]*)"`));
  return m ? m[1] : null;
}

const appXaml = fs.readFileSync(APP_XAML, 'utf8');
const problems = [];

// XML comments are blanked out, newlines kept so line numbers stay right: the
// explanations in App.xaml quote the forbidden form on purpose.
const source = appXaml.replace(/<!--[\s\S]*?-->/g, (comment) => comment.replace(/[^\n]/g, ' '));

// The form WP8.1 rejects, by name: one error per attribute.
source.split(/\r?\n/).forEach((line, i) => {
  if (/\bFigures\s*=/.test(line)) {
    problems.push('WhatsappApp/App.xaml:' + (i + 1) +
      ': Figures="..." does not compile on WP8.1 (PathFigureCollection has no' +
      ' string converter) -> write PathFigure + LineSegment elements instead');
  }
});

/**
 * Parses the element form and rebuilds the equivalent path mini-language (used
 * only by --preview, so the ASCII render keeps working unchanged).
 */
const defined = new Map();
for (const geometry of source.matchAll(/<PathGeometry(?=[\s>])([^>]*)>([\s\S]*?)<\/PathGeometry>/g)) {
  const key = attribute(geometry[1], 'x:Key');
  if (!key) continue;
  const where = 'WhatsappApp/App.xaml: ' + key;
  if (defined.has(key)) problems.push(where + ': duplicate resource key');

  const figures = [];
  for (const figure of geometry[2].matchAll(/<PathFigure(?=[\s>])([^>]*)>([\s\S]*?)<\/PathFigure>/g)) {
    const start = attribute(figure[1], 'StartPoint');
    if (!start || !POINT.test(start)) {
      problems.push(where + ': PathFigure needs StartPoint="x,y"');
      continue;
    }

    let mini = 'M' + start;
    let segments = 0;
    for (const segment of figure[2].matchAll(SEGMENT)) {
      const kind = segment[1];
      const tag = segment[2];
      if (kind === 'LineSegment') {
        const point = attribute(tag, 'Point');
        if (!point || !POINT.test(point)) {
          problems.push(where + ': LineSegment needs Point="x,y"');
          continue;
        }
        mini += ' L' + point;
      } else if (kind === 'PolyLineSegment') {
        const points = (attribute(tag, 'Points') || '').trim();
        if (!POINTS.test(points)) {
          problems.push(where + ': PolyLineSegment needs Points="x,y x,y ..."');
          continue;
        }
        mini += ' L' + points.split(/\s+/).join(' L');
      } else {
        const size = attribute(tag, 'Size');
        const point = attribute(tag, 'Point');
        if (!size || !POINT.test(size) || !point || !POINT.test(point)) {
          problems.push(where + ': ArcSegment needs Size="rx,ry" and Point="x,y"');
          continue;
        }
        const rotation = attribute(tag, 'RotationAngle') || '0';
        const large = attribute(tag, 'IsLargeArc') === 'True' ? 1 : 0;
        const sweep = attribute(tag, 'SweepDirection') === 'Clockwise' ? 1 : 0;
        mini += ' A' + size + ' ' + rotation + ' ' + large + ' ' + sweep + ' ' + point;
      }
      segments++;
    }

    if (segments === 0) problems.push(where + ': PathFigure has no segment');
    if (attribute(figure[1], 'IsClosed') === 'True') mini += ' Z';
    figures.push(mini);
  }

  if (figures.length === 0) problems.push(where + ': PathGeometry has no PathFigure');
  defined.set(key, figures.join(' '));
}

const files = walk(APP, []).filter((f) => f !== APP_XAML);
const used = new Set();
const withFill = new Set();
const withStroke = new Set();

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
      if (/\bFill\s*=/.test(line)) withFill.add(m[1]);
      if (/\bStroke\s*=/.test(line)) withStroke.add(m[1]);
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
      // Filled only when every use is Fill and no use is Stroke: an open
      // outline drawn with the even-odd fill rule is a blob, not an icon.
      const filled = withFill.has(name) && !withStroke.has(name);
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
