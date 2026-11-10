#!/usr/bin/env node
/**
 * tools/check-docs.js
 *
 * Guard for the project documentation.
 *
 * The docs are written in multiple languages, and a doc that exists only in one of
 * them is worse than no doc: the reader who needs it is the one who does not
 * get it. Three rules, all easy to break when a section is added in a hurry:
 *
 * 1. Every document group listed in GROUPS exists, and the files have the
 * **same headings in the same order at the same depth**. A section added to
 * one language and not the others shows up here as a different heading count.
 * The heading *text* is translated and therefore not compared.
 *
 * 2. The `## Disclosure` section is present and is the **last** section of the
 * groups marked `disclosure: true` - the ones that present the project: what
 * it is, that maintainers are wanted, that an AI agent wrote it, and who
 * answers for the WhatsApp account. It lives at the end of each project README,
 * so every language version carries the same disclosure.
 *
 * 3. No emoji anywhere in the Markdown. The only exception is the warning sign
 * (U+26A0, with or without the variation selector), for a real hazard: the
 * risk of a banned phone number.
 *
 * Heading levels are compared, not text, so translating a title is free; moving,
 * adding or dropping a section is not.
 *
 * Usage:
 * node tools/check-docs.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');

// Each group lists the README files that must stay in sync.
const GROUPS = [
  { files: ['README.md', 'README.it.md', 'README.pt.md'], disclosure: true },
];

const DISCLOSURE = 'Disclosure';
const EMOJI = /[\u{1F000}-\u{1FAFF}\u{2600}-\u{27BF}\u{2B00}-\u{2BFF}\u{FE0F}\u{200D}]/gu;
// The only exception is the warning sign, for an actual hazard.
const WARNING_SIGN = /\u26A0\uFE0F?/g;
// `skills` holds third-party skill instructions, not the project's docs: they
// follow their own rules, so the emoji rule does not apply to them.
const SKIP_DIRS = ['.git', '.tools', 'node_modules', 'obj', 'bin', 'skills'];

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

/** Return document headings, ignoring headings inside code blocks. */
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

// ─── 1. Documentation groups ────────────────────────────────────────────────

for (const { files: names, disclosure } of GROUPS) {
  const files = names.map((name) => path.resolve(ROOT, name));
  const missing = files.filter((file) => !fs.existsSync(file));
  if (missing.length) {
    problems.push(`  ${missing.map(rel).join(', ')}: missing - all language versions are maintained together`);
    continue;
  }

  const shapes = files.map((file) => headings(fs.readFileSync(file, 'utf8')));

  for (let i = 1; i < shapes.length; i++) {
    const name = names[i];
    if (shapes[0].length !== shapes[i].length) {
      problems.push(`  ${names[0]} / ${name}: ${shapes[0].length} vs ${shapes[i].length} heading`);
      continue;
    }

    for (let j = 0; j < shapes[0].length; j++) {
      if (shapes[0][j].level !== shapes[i][j].level) {
        problems.push(`  ${names[0]} / ${name}: heading #${j + 1} has level `
          + `${shapes[0][j].level} in ${names[0]} ("${shapes[0][j].text}") and `
          + `${shapes[i][j].level} in ${name} ("${shapes[i][j].text}")`);
        break;
      }
    }
  }

  // Disclosure closes every README that presents the project.
  if (disclosure) {
    shapes.forEach((shape, i) => {
      const last = shape[shape.length - 1];
      if (!last || last.level !== 2 || last.text !== DISCLOSURE) {
        problems.push(`  ${rel(files[i])}: the last section must be "## ${DISCLOSURE}"`
          + (last ? ` (found "${'#'.repeat(last.level)} ${last.text}")` : ' (no heading)'));
      }
    });
  }

  // Every README links to every other language version.
  files.forEach((file) => {
    const text = fs.readFileSync(file, 'utf8');
    files.filter((other) => other !== file).forEach((other) => {
      const basename = path.basename(other);
      if (!text.includes(`](${basename})`)) {
        problems.push(`  ${rel(file)}: missing link to ${basename}`);
      }
    });
  });
}

// ─── 2. No emoji in Markdown ────────────────────────────────────────────────

const markdown = [];
walk(ROOT, markdown);

for (const file of markdown) {
  const text = fs.readFileSync(file, 'utf8').replace(WARNING_SIGN, '');
  text.split('\n').forEach((line, index) => {
    const found = line.match(EMOJI);
    if (!found) return;
    problems.push(`  ${rel(file)}:${index + 1}: emoji ${found.map(codePoint).join(', ')}`
      + ' - only the warning sign U+26A0 is allowed');
  });
}

// ─── Result ─────────────────────────────────────────────────────────────────

if (problems.length) {
  console.log(problems.join('\n'));
  console.log(`\n${problems.length} doc problem(s).`);
  process.exit(1);
}
const withDisclosure = GROUPS.filter((group) => group.disclosure).length;
console.log(`OK: ${GROUPS.length} doc group(s) in sync (${withDisclosure} closed by "${DISCLOSURE}"), `
  + `no emoji in ${markdown.length} .md file(s).`);
