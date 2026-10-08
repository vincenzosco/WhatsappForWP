#!/usr/bin/env node
/**
 * tools/check-xaml-names.js
 *
 * Guard per i nomi del XAML: un `x:Name` compare una volta sola nel suo namescope.
 *
 * Perche' esiste: un `DataTemplate` registra i suoi nomi quando una riga viene
 * realizzata, e un namescope tiene un nome solo. Due `x:Name` uguali non sono un
 * errore di compilazione - la build tace, e il guard delle azioni vede due
 * pulsanti diversi solo se hanno nomi diversi - ma al primo item il runtime
 * risponde `XamlParseException 0x802B000A` e la schermata che lo contiene si
 * chiude invece di aprirsi. Era il pulsante di riproduzione del bubble in arrivo
 * e quello del bubble in uscita, due `PlayAudioButton` nello stesso
 * `DataTemplate` di `ChatPage.xaml`: la chat si chiudeva appena aperta, con
 * `ok: conversation bound 31 message(s)` gia' sul disco e nessun `Diag.Failed`.
 *
 * Namescope: la radice del documento, piu' uno per ogni `DataTemplate`,
 * `ControlTemplate` o `ItemsPanelTemplate`, annidati compresi. Lo stesso nome in
 * due template diversi e' legittimo, perche' ognuno si istanzia nel suo; un nome
 * dentro un commento non esiste.
 *
 * Usage:
 *   node tools/check-xaml-names.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');

// I tag che aprono un namescope proprio.
const SCOPES = ['DataTemplate', 'ControlTemplate', 'ItemsPanelTemplate'];

// Un commento, un tag di scope, o un nome. L'ordine conta: il commento per primo,
// cosi' un elemento commentato non entra ne' nei nomi ne' nella profondita'.
const TOKEN = new RegExp(
  '<!--[\\s\\S]*?-->'
    + '|<(\\/?)(?:' + SCOPES.join('|') + ')\\b([^>]*)>'
    + '|x:Name="([^"]*)"',
  'g');

/** Gli offset di inizio riga, per tradurre una posizione in un numero di riga. */
function lineStarts(text) {
  const starts = [0];
  for (let i = 0; i < text.length; i++) {
    if (text[i] === '\n') starts.push(i + 1);
  }
  return starts;
}

function lineAt(starts, offset) {
  let low = 0;
  let high = starts.length - 1;
  while (low < high) {
    const mid = (low + high + 1) >> 1;
    if (starts[mid] <= offset) low = mid;
    else high = mid - 1;
  }
  return low + 1;
}

/** I nomi usati piu' di una volta nello stesso namescope, in un file XAML. */
function nameProblems(xaml, file) {
  const problems = [];
  const starts = lineStarts(xaml);
  const stack = [];
  const first = new Map();

  TOKEN.lastIndex = 0;
  let m;
  while ((m = TOKEN.exec(xaml)) !== null) {
    if (m[0].startsWith('<!--')) continue;

    if (m[3] === undefined) {
      // Un tag di scope: chiuso svuota, auto-chiuso non apre niente.
      if (m[1] === '/') stack.pop();
      else if (!/\/\s*$/.test(m[2])) stack.push(m.index);
      continue;
    }

    const name = m[3];
    const key = stack.join('/') + '\u0000' + name;
    const line = lineAt(starts, m.index);
    const earlier = first.get(key);
    if (earlier === undefined) {
      first.set(key, line);
      continue;
    }
    problems.push(`${file}: x:Name="${name}" is used twice in one namescope `
      + `(lines ${earlier} and ${line}): a template registers its names when an item `
      + 'is realized, so the second one throws XamlParseException 0x802B000A at run '
      + 'time and the screen that holds it closes instead of opening');
  }
  return problems;
}

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

function main() {
  const problems = [];
  let names = 0;
  const files = walk(APP, []);

  for (const file of files) {
    const rel = path.relative(ROOT, file).replace(/\\/g, '/');
    const xaml = fs.readFileSync(file, 'utf8');
    // Un nome commentato non e' un nome, quindi non si conta.
    names += (xaml.replace(/<!--[\s\S]*?-->/g, '').match(/x:Name="/g) || []).length;
    problems.push(...nameProblems(xaml, rel));
  }

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} name problem(s).`);
    process.exit(1);
  }
  console.log(`OK: ${names} name(s) in ${files.length} XAML file(s), `
    + 'no name repeats inside one namescope.');
}

if (require.main === module) main();

module.exports = { nameProblems };
