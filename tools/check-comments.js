#!/usr/bin/env node
/**
 * Comment hygiene gate: comments are English, undated and plain.
 *
 * The app's own strings are Italian - that is what a person reads on the phone - so a
 * comment may quote one. Quoted spans are blanked before a line is judged, which is why
 * `"gli stati non compaiono"` passes and a comment written *in* Italian does not.
 *
 * Four rules, all mechanical: no Italian prose (two listed words on one line is a
 * sentence, not an identifier), no dates, no emoji, no template openers such as
 * `Why it exists:` - say the thing.
 *
 * Scope: the shipped code (`WhatsappApp`, `WhatsappApp.Tasks`, `ProtocolTests`) must be
 * clean and fails the gate. The developer scripts in `tools/` still carry Italian prose
 * written before this rule; they are counted and printed as debt, and translated as they
 * are touched, so the number can only go down.
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');

/** Directories that hold build output or tool state, never sources. */
const SKIP = new Set(['bin', 'obj', '.git', '.tools', '.bug-hunter', 'node_modules', '.vs']);

const EXTENSIONS = /\.(cs|c|h|js|cjs|bat)$/;

/** Where the rules are enforced. Anything outside these is reported as debt. */
const STRICT_ROOTS = ['WhatsappApp/', 'WhatsappApp.Tasks/', 'ProtocolTests/'];

/**
 * Words that only appear in Italian prose. Two on one line is a sentence: a single one
 * ("non" in `non-zero`, "la" in an identifier) is why the rule counts instead of matching.
 */
const ITALIAN = [
  'il', 'lo', 'la', 'le', 'gli', 'di', 'da', 'del', 'dei', 'della', 'delle', 'degli',
  'al', 'alla', 'nel', 'nella', 'sul', 'sulla', 'con', 'per', 'che', 'non', 'una', 'uno',
  'sono', 'questo', 'questa', 'questi', 'quando', 'viene', 'essere', 'stato', 'cosi',
  'perche', 'anche', 'quindi', 'dopo', 'prima', 'ogni', 'tutti', 'senza', 'solo', 'piu',
  'loro', 'suo', 'sua', 'poi', 'nostro', 'nelle', 'pero', 'adesso'
];

const DATE = /\b(January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{4}\b/;

const EMOJI = /[\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}\u{FE0F}]/u;

const RHETORIC = /\bWhy it exists\b|\bWhy this exists\b|\bWhat it is not\b|\bWhat this is not\b|\bWhat it deliberately is\b|\bWhy it is a\b/;

/** Whether a path relative to the repository root is enforced or only counted. */
function isStrict(relativePath) {
  const normalized = String(relativePath || '').split(path.sep).join('/');
  return STRICT_ROOTS.some((root) => normalized.startsWith(root));
}

/** Whether the trimmed line opens a comment, of any of the forms this repository uses. */
function opensComment(trimmed) {
  return trimmed.startsWith('//')
    || trimmed.startsWith('/*')
    || trimmed.startsWith('*')
    || /^rem\b/i.test(trimmed)
    || trimmed.startsWith('#');
}

/** The line with every quoted span emptied: identifiers and UI text stop counting. */
function unquoted(line) {
  return line.replace(/"[^"]*"/g, '""').replace(/`[^`]*`/g, '``');
}

/**
 * Every problem in one file's text. `file` is only used to name the finding, so a test
 * can lint a string without a file behind it.
 */
function lintText(text, file) {
  const found = [];
  const lines = String(text === undefined || text === null ? '' : text).split(/\r?\n/);

  let inBlock = false;

  for (let i = 0; i < lines.length; i++) {
    const trimmed = lines[i].trim();

    const blockStart = trimmed.indexOf('/*') >= 0;
    const isComment = inBlock || opensComment(trimmed);

    if (inBlock && trimmed.indexOf('*/') >= 0) inBlock = false;
    else if (blockStart && trimmed.indexOf('*/') < 0) inBlock = true;

    if (!isComment) continue;

    const bare = unquoted(trimmed);

    const words = ITALIAN.filter((word) => new RegExp('\\b' + word + '\\b', 'i').test(bare));
    if (words.length >= 2) {
      found.push({ file: file, line: i + 1, kind: 'italian', text: trimmed });
      continue;
    }

    if (DATE.test(bare)) {
      found.push({ file: file, line: i + 1, kind: 'date', text: trimmed });
      continue;
    }

    if (EMOJI.test(bare)) {
      found.push({ file: file, line: i + 1, kind: 'emoji', text: trimmed });
      continue;
    }

    if (RHETORIC.test(bare)) {
      found.push({ file: file, line: i + 1, kind: 'rhetoric', text: trimmed });
    }
  }

  return found;
}

/** The source files this guard reads, relative to the repository root. */
function sources() {
  const files = [];

  (function walk(directory) {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const full = path.join(directory, entry.name);

      if (entry.isDirectory()) {
        if (!SKIP.has(entry.name)) walk(full);
        continue;
      }

      if (EXTENSIONS.test(entry.name)) files.push(full);
    }
  })(ROOT);

  return files.sort();
}

/** Every problem in the repository, each tagged with whether its file is enforced. */
function lint() {
  const found = [];

  for (const file of sources()) {
    const relative = path.relative(ROOT, file).split(path.sep).join('/');
    const strict = isStrict(relative);

    for (const problem of lintText(fs.readFileSync(file, 'utf8'), relative)) {
      problem.strict = strict;
      found.push(problem);
    }
  }

  return found;
}

function main() {
  const found = lint();
  const strict = found.filter((problem) => problem.strict);
  const debt = found.filter((problem) => !problem.strict);

  for (const problem of strict.slice(0, 40)) {
    console.log(problem.file + ':' + problem.line + ': ' + problem.kind + ' - ' + problem.text);
  }

  if (strict.length > 40) console.log('... and ' + (strict.length - 40) + ' more');

  if (strict.length > 0) {
    console.log('\n' + strict.length + ' comment(s) in the shipped code break the rules in AGENTS.md.');
    process.exitCode = 1;
    return;
  }

  console.log('OK: every comment in the shipped code is English, undated, plain and free of emoji.');

  if (debt.length > 0) {
    console.log('Debt: ' + debt.length + ' Italian comment line(s) remain in tools/.');
  }
}

if (require.main === module) main();

module.exports = { lint, lintText, isStrict, sources, STRICT_ROOTS, ITALIAN, DATE, EMOJI, RHETORIC };
