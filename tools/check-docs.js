#!/usr/bin/env node
/**
 * tools/check-docs.js
 *
 * Guard for the project documentation.
 *
 * The docs are written in two languages, and a doc that exists only in one of
 * them is worse than no doc: the reader who needs it is the one who does not
 * get it. Three rules, all easy to break when a section is added in a hurry:
 *
 *  1. Every pair listed in PAIRS exists, and the two files have the **same
 *     headings in the same order at the same depth**. A section added to one
 *     language and not the other shows up here as a different heading count.
 *     The heading *text* is translated and therefore not compared.
 *
 *  2. The `## Disclosure` section is present and is the **last** section of the
 *     pairs marked `disclosure: true` - the ones that present the project: what
 *     it is, that maintainers are wanted, that an AI agent wrote it, and who
 *     answers for the WhatsApp account. It lives there and not in every README,
 *     so there is one copy to keep honest instead of four.
 *
 *  3. No emoji anywhere in the Markdown. The only exception is the warning sign
 *     (U+26A0, with or without the variation selector), for a real hazard: the
 *     risk of a banned phone number.
 *
 * Heading levels are compared, not text, so translating a title is free; moving,
 * adding or dropping a section is not.
 *
 * Usage:
 *   node tools/check-docs.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');

// Ogni coppia e' il contratto: o esistono entrambe, o il guard fallisce.
// `disclosure` dice quali README presentano il progetto e devono quindi chiudersi
// con la sezione omonima.
const PAIRS = [
  { english: 'README.md', italian: 'README.it.md', disclosure: true },
  { english: 'WhatsappBridge/README.md', italian: 'WhatsappBridge/README.it.md', disclosure: false },
];

const DISCLOSURE = 'Disclosure';
const EMOJI = /[\u{1F000}-\u{1FAFF}\u{2600}-\u{27BF}\u{2B00}-\u{2BFF}\u{FE0F}\u{200D}]/gu;
// L'unica eccezione ammessa: il segno di pericolo, per un rischio reale.
const WARNING_SIGN = /\u26A0\uFE0F?/g;
const SKIP_DIRS = ['.git', '.tools', 'node_modules', 'obj', 'bin'];

function walk(dir, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (SKIP_DIRS.includes(entry.name)) continue;
      walk(path.join(dir, entry.name), out);
    } else if (entry.name.endsWith('.md')) {
      out.push(path.join(dir, entry.name));
    }
  }
}

/** Gli heading di un documento, ignorando quelli dentro un blocco di codice. */
function headings(text) {
  const out = [];
  let fenced = false;
  for (const line of text.split('\n')) {
    if (/^\s*(```|~~~)/.test(line)) {
      fenced = !fenced;
      continue;
    }
    if (fenced) continue;
    const match = line.match(/^(#{1,6})\s+(\S.*?)\s*$/);
    if (match) out.push({ level: match[1].length, text: match[2] });
  }
  return out;
}

function codePoint(char) {
  return 'U+' + char.codePointAt(0).toString(16).toUpperCase().padStart(4, '0');
}

const rel = (file) => path.relative(ROOT, file);
const problems = [];

// ─── 1. Le coppie di documenti ───────────────────────────────────────────────

for (const { english, italian, disclosure } of PAIRS) {
  const files = [english, italian].map((name) => path.resolve(ROOT, name));
  const missing = files.filter((file) => !fs.existsSync(file));
  if (missing.length) {
    problems.push(`  ${missing.map(rel).join(', ')}: manca - le due lingue si scrivono insieme`);
    continue;
  }

  const shapes = files.map((file) => headings(fs.readFileSync(file, 'utf8')));

  if (shapes[0].length !== shapes[1].length) {
    problems.push(`  ${english} / ${italian}: ${shapes[0].length} vs ${shapes[1].length} heading`);
  } else {
    for (let i = 0; i < shapes[0].length; i++) {
      if (shapes[0][i].level !== shapes[1][i].level) {
        problems.push(`  ${english} / ${italian}: heading #${i + 1} e' di livello `
          + `${shapes[0][i].level} in inglese ("${shapes[0][i].text}") e ${shapes[1][i].level} `
          + `in italiano ("${shapes[1][i].text}")`);
        break;
      }
    }
  }

  // La disclosure chiude il documento che presenta il progetto: e' la sua
  // ultima sezione, in entrambe le lingue.
  if (disclosure) {
    shapes.forEach((shape, i) => {
      const last = shape[shape.length - 1];
      if (!last || last.level !== 2 || last.text !== DISCLOSURE) {
        problems.push(`  ${rel(files[i])}: l'ultima sezione deve essere "## ${DISCLOSURE}"`
          + (last ? ` (trovata "${'#'.repeat(last.level)} ${last.text}")` : ' (nessun heading)'));
      }
    });
  }

  // Ogni versione rimanda all'altra, altrimenti chi non conosce la lingua non
  // ha modo di arrivarci.
  files.forEach((file, i) => {
    const other = path.basename(files[1 - i]);
    if (!fs.readFileSync(file, 'utf8').includes(`](${other})`)) {
      problems.push(`  ${rel(file)}: manca il rimando a ${other} (es. \`[Italiano](${other})\`)`);
    }
  });
}

// ─── 2. Niente emoji, in nessun file Markdown ────────────────────────────────

const markdown = [];
walk(ROOT, markdown);

for (const file of markdown) {
  const text = fs.readFileSync(file, 'utf8').replace(WARNING_SIGN, '');
  text.split('\n').forEach((line, index) => {
    const found = line.match(EMOJI);
    if (!found) return;
    problems.push(`  ${rel(file)}:${index + 1}: emoji ${found.map(codePoint).join(', ')}`
      + ' - e\' ammesso solo il segno di pericolo U+26A0');
  });
}

// ─── Esito ───────────────────────────────────────────────────────────────────

if (problems.length) {
  console.log(problems.join('\n'));
  console.log(`\n${problems.length} doc problem(s).`);
  process.exit(1);
}
const withDisclosure = PAIRS.filter((pair) => pair.disclosure).length;
console.log(`OK: ${PAIRS.length} doc pair(s) in step (${withDisclosure} closed by "${DISCLOSURE}"), `
  + `no emoji in ${markdown.length} .md file(s).`);
