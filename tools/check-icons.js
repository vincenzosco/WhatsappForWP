#!/usr/bin/env node
/**
 * tools/check-icons.js
 *
 * Guard for the WP8.1 app icons.
 *
 * This is a Windows Phone 8.1 *WinRT* app (Windows.UI.Xaml), and for it three
 * things are hard requirements, not style preferences:
 *
 *  1. No "Segoe MDL2 Assets". It is a Windows 10 font that WP8.1 does not
 *     ship, so a glyph-based icon button renders blank. Icons are vector paths.
 *
 *  2. A Geometry must NOT travel through a ResourceDictionary. This compiles
 *     and then throws at runtime:
 *
 *       <Path Data="{StaticResource IconBack}" .../>
 *       Windows.UI.Xaml.Markup.XamlParseException: Failed to assign to
 *       property 'Windows.UI.Xaml.Shapes.Path.Data'.
 *
 *     A Geometry is not shareable through a StaticResource in WinRT
 *     (microsoft-ui-xaml issues #1909 and #5780). The form that works is the
 *     geometry inlined on the Path itself:
 *
 *       <Path ...>
 *           <!-- IconBack -->
 *           <Path.Data>
 *               <PathGeometry><PathGeometry.Figures>...</PathGeometry.Figures></PathGeometry>
 *           </Path.Data>
 *       </Path>
 *
 *  3. The element form is mandatory: PathGeometry.Figures="M..." does not
 *     compile, because the WP8.1 XAML type converter for PathFigureCollection
 *     does not support converting from a string.
 *
 * The icon name used by --preview and by the "same icon, same geometry" check
 * comes from the <!-- IconX --> comment each Path carries above its Path.Data.
 *
 * Usage:
 *   node tools/check-icons.js            # rules + references + consistency
 *   node tools/check-icons.js --preview  # + ASCII preview (needs ImageMagick)
 */
'use strict';

const { execFileSync, spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');
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

/**
 * Validates one inline <PathGeometry> and returns the equivalent path
 * mini-language (used by --preview and to compare repeated icons).
 */
function parseGeometry(body, where, problems) {
  const geometry = body.match(/<PathGeometry(?=[\s>])([^>]*)>([\s\S]*?)<\/PathGeometry>/);
  if (!geometry) {
    problems.push(`${where}: <Path.Data> does not contain a <PathGeometry>`);
    return null;
  }

  const figures = [];
  let figureCount = 0;
  for (const figure of geometry[2].matchAll(/<PathFigure(?=[\s>])([^>]*)>([\s\S]*?)<\/PathFigure>/g)) {
    figureCount++;
    const start = attribute(figure[1], 'StartPoint');
    if (!start || !POINT.test(start)) {
      problems.push(`${where}: PathFigure needs StartPoint="x,y"`);
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
          problems.push(`${where}: LineSegment needs Point="x,y"`);
          continue;
        }
        mini += ' L' + point;
      } else if (kind === 'PolyLineSegment') {
        const points = (attribute(tag, 'Points') || '').trim();
        if (!POINTS.test(points)) {
          problems.push(`${where}: PolyLineSegment needs Points="x,y x,y ..."`);
          continue;
        }
        mini += ' L' + points.split(/\s+/).join(' L');
      } else {
        const size = attribute(tag, 'Size');
        const point = attribute(tag, 'Point');
        if (!size || !POINT.test(size) || !point || !POINT.test(point)) {
          problems.push(`${where}: ArcSegment needs Size="rx,ry" and Point="x,y"`);
          continue;
        }
        const rotation = attribute(tag, 'RotationAngle') || '0';
        const large = attribute(tag, 'IsLargeArc') === 'True' ? 1 : 0;
        const sweep = attribute(tag, 'SweepDirection') === 'Clockwise' ? 1 : 0;
        mini += ' A' + size + ' ' + rotation + ' ' + large + ' ' + sweep + ' ' + point;
      }
      segments++;
    }

    if (segments === 0) problems.push(`${where}: PathFigure has no segment`);
    if (attribute(figure[1], 'IsClosed') === 'True') mini += ' Z';
    figures.push(mini);
  }

  if (figureCount === 0) problems.push(`${where}: PathGeometry has no PathFigure`);
  return figures.join(' ');
}

const problems = [];
const icons = new Map();       // name -> mini-language
const iconOrigin = new Map();  // name -> "file:line" of the first copy
const uses = new Map();        // name -> [{ fill, stroke }]
let iconCount = 0;

for (const file of walk(APP, [])) {
  const rel = path.relative(ROOT, file);
  const raw = fs.readFileSync(file, 'utf8');
  // Comments are blanked for the pattern checks (App.xaml quotes the rejected
  // forms in prose), newlines kept so line numbers stay right.
  const code = raw.replace(/<!--[\s\S]*?-->/g, (comment) => comment.replace(/[^\n]/g, ' '));
  const lineOf = (index) => code.slice(0, index).split('\n').length;
  const fail = (index, message) => problems.push(`${rel}:${lineOf(index)}: ${message}`);

  // Rule 1: the Windows 10 icon font.
  code.split('\n').forEach((line, i) => {
    if (/Segoe MDL2 Assets/.test(line)) {
      problems.push(`${rel}:${i + 1}: uses "Segoe MDL2 Assets", which WP8.1 does not have`);
    }
  });

  // Rule 2: a resource Geometry cannot be assigned to Path.Data.
  for (const m of code.matchAll(/Data="\{StaticResource (Icon[A-Za-z]+)\}"/g)) {
    fail(m.index, `Path.Data="{StaticResource ${m[1]}}" throws XamlParseException at ` +
      'runtime (a Geometry is not shareable through a StaticResource in WinRT) -> ' +
      'inline <Path.Data><PathGeometry>...</PathGeometry></Path.Data> on the Path');
  }

  // Rule 3: figures in string form do not compile.
  for (const m of code.matchAll(/\bFigures\s*=/g)) {
    fail(m.index, 'Figures="..." does not compile on WP8.1 (PathFigureCollection has ' +
      'no string converter) -> write PathFigure + LineSegment/PolyLineSegment/ArcSegment');
  }

  // Every <PathGeometry> has to be the inline value of a <Path.Data>.
  for (const m of code.matchAll(/<PathGeometry(?=[\s>])/g)) {
    const before = code.slice(Math.max(0, m.index - 160), m.index);
    if (!/<Path\.Data>\s*$/.test(before)) {
      fail(m.index, '<PathGeometry> is not the inline value of a <Path.Data> ' +
        '(a geometry in a ResourceDictionary cannot be assigned to Path.Data)');
    }
  }

  // Every <Path> is one icon: exactly one inline geometry, named by a comment.
  const open = /<Path(?=[\s/>])/g;
  let match;
  while ((match = open.exec(code)) !== null) {
    const gt = code.indexOf('>', match.index);
    if (gt < 0) break;
    const openTag = code.slice(match.index, gt + 1);
    const where = `${rel}:${lineOf(match.index)}`;

    if (openTag.trim().endsWith('/>')) {
      fail(match.index, '<Path/> has no Data: an icon Path needs an inline <Path.Data>');
      open.lastIndex = gt + 1;
      continue;
    }

    const close = code.indexOf('</Path>', gt);
    if (close < 0) break;
    const body = code.slice(gt + 1, close);
    const rawBody = raw.slice(match.index, close + '</Path>'.length);
    open.lastIndex = close + '</Path>'.length;

    const nameMatch = rawBody.match(/<!--\s*(Icon[A-Za-z]+)\s*-->/);
    const name = nameMatch ? nameMatch[1] : null;
    if (!name) problems.push(`${where}: Path has no <!-- IconX --> comment naming the icon`);

    const data = body.match(/<Path\.Data>([\s\S]*?)<\/Path\.Data>/);
    if (!data) {
      problems.push(`${where}: Path has no inline <Path.Data>`);
      continue;
    }

    iconCount++;
    const geometry = parseGeometry(data[1], where, problems);
    if (!geometry || !name) continue;

    if (!icons.has(name)) {
      icons.set(name, geometry);
      iconOrigin.set(name, where);
    } else if (icons.get(name) !== geometry) {
      problems.push(`${where}: ${name} differs from the copy at ${iconOrigin.get(name)} ` +
        '(the same icon must be the same geometry everywhere)');
    }
    if (!uses.has(name)) uses.set(name, []);
    uses.get(name).push({
      fill: /\bFill\s*=/.test(openTag),
      stroke: /\bStroke\s*=/.test(openTag),
    });
  }
}

if (problems.length) {
  console.log(problems.join('\n'));
  console.log(`\n${problems.length} icon problem(s).`);
  process.exit(1);
}
console.log(`OK: ${iconCount} inline icon Path(s), ${icons.size} distinct icon(s).`);

if (PREVIEW) {
  const hasMagick = spawnSync('magick', ['-version'], { stdio: 'ignore' }).status === 0;
  if (!hasMagick) {
    console.log('preview skipped: ImageMagick (magick) not available');
  } else {
    for (const [name, figures] of icons) {
      const paths = uses.get(name) || [];
      // Filled only when every use is Fill and no use is Stroke: an open
      // outline drawn with the even-odd fill rule is a blob, not an icon.
      const filled = paths.length > 0 && paths.every((u) => u.fill && !u.stroke);
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
