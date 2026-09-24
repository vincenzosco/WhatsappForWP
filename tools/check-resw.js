#!/usr/bin/env node
/**
 * tools/check-resw.js
 *
 * Guard for the app localization (WhatsappApp/Strings/<lang>/Resources.resw).
 *
 * WP8.1 resolves x:Uid and ResourceLoader.GetString at run time: a missing or
 * mistyped entry does NOT fail the build, it silently leaves the literal from
 * the markup in place. A .resw that is not registered in the csproj is never
 * compiled into the package at all. None of that is caught by the compiler, so
 * this guard is the only thing that catches it.
 *
 * Rules:
 *   A. every literal localizable attribute in XAML is a binding, a known
 *      non-translatable value, or backed by x:Uid="X" plus an entry
 *      "X.<Attr>" in BOTH resource files;
 *   B. en-US and it-IT declare exactly the same keys, and no key collides with
 *      a property identifier of the same name;
 *   C. every Loc.Get("Key", ...) in C# exists in BOTH resource files;
 *   D. (--strict) no key is left unused;
 *   E. both .resw files are registered as PRIResource in the csproj and
 *      <DefaultLanguage> is one of the supported languages.
 *
 * Usage:
 *   node tools/check-resw.js
 *   node tools/check-resw.js --strict
 */
'use strict';

const { spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');
const STRINGS = path.join(APP, 'Strings');
const CSPROJ = path.join(APP, 'WhatsappApp.csproj');
const LANGS = ['en-US', 'it-IT'];
const STRICT = process.argv.includes('--strict');

// Properties that carry user-visible text.
const LOCALIZABLE = ['Text', 'Content', 'PlaceholderText', 'Header'];
// Literals that never need a translation (brand names).
const ALLOWED_LITERALS = new Set(['WhatsApp']);
// Values made only of symbols/digits/punctuation need no translation.
const SYMBOL_ONLY = /^[^\p{L}]*$/u;
// Matches one XML start tag; attribute values in this project never contain < or >.
const TAG = /<[A-Za-z_][^<>]*>/g;

function walk(dir, out, keep) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'obj' || entry.name === 'bin') continue;
      walk(path.join(dir, entry.name), out, keep);
    } else if (keep(entry.name)) {
      out.push(path.join(dir, entry.name));
    }
  }
  return out;
}

function readResw(file) {
  const xml = fs.readFileSync(file, 'utf8');
  const keys = new Set();
  for (const m of xml.matchAll(/<data\s+name="([^"]+)"/g)) keys.add(m[1]);
  return { xml, keys };
}

const problems = [];
const warnings = [];
const used = new Set();

// ---------------------------------------------------------------------------
// E. csproj wiring
// ---------------------------------------------------------------------------
const csproj = fs.readFileSync(CSPROJ, 'utf8');
for (const lang of LANGS) {
  const rel = 'Strings\\' + lang + '\\Resources.resw';
  if (!csproj.includes('<PRIResource Include="' + rel + '"')) {
    problems.push('WhatsappApp/WhatsappApp.csproj: "' + rel +
      '" is not registered as a <PRIResource> (it would never ship)');
  }
}
const defaultLang = /<DefaultLanguage>([^<]+)<\/DefaultLanguage>/.exec(csproj);
if (!defaultLang) {
  problems.push('WhatsappApp/WhatsappApp.csproj: no <DefaultLanguage>');
} else if (LANGS.indexOf(defaultLang[1]) === -1) {
  problems.push('WhatsappApp/WhatsappApp.csproj: <DefaultLanguage>' + defaultLang[1] +
    '</DefaultLanguage> is not one of ' + LANGS.join(', '));
}

// ---------------------------------------------------------------------------
// B. both languages, same keys
// ---------------------------------------------------------------------------
const resw = {};
const hasXmllint = spawnSync('xmllint', ['--version'], { stdio: 'ignore' }).status === 0;
for (const lang of LANGS) {
  const file = path.join(STRINGS, lang, 'Resources.resw');
  if (!fs.existsSync(file)) {
    problems.push('missing ' + path.relative(ROOT, file));
    resw[lang] = { xml: '', keys: new Set() };
    continue;
  }
  if (hasXmllint && spawnSync('xmllint', ['--noout', file], { stdio: 'ignore' }).status !== 0) {
    problems.push(path.relative(ROOT, file) + ': not well-formed XML');
  }
  resw[lang] = readResw(file);
}

for (const key of resw[LANGS[0]].keys) {
  if (!resw[LANGS[1]].keys.has(key)) {
    problems.push('Strings/en-US/Resources.resw: "' + key + '" has no it-IT translation');
  }
}
for (const key of resw[LANGS[1]].keys) {
  if (!resw[LANGS[0]].keys.has(key)) {
    problems.push('Strings/it-IT/Resources.resw: "' + key + '" has no en-US entry');
  }
}
for (const key of resw[LANGS[0]].keys) {
  const dot = key.indexOf('.');
  if (dot === -1) continue;
  const base = key.slice(0, dot);
  if (resw[LANGS[0]].keys.has(base)) {
    problems.push('Strings/en-US/Resources.resw: "' + base + '" and "' + key +
      '" cannot coexist (duplicate resource identifier)');
  }
}

function exists(key) {
  return resw[LANGS[0]].keys.has(key) && resw[LANGS[1]].keys.has(key);
}

// ---------------------------------------------------------------------------
// A. x:Uid coverage of literal attributes
// ---------------------------------------------------------------------------
for (const file of walk(APP, [], (n) => n.endsWith('.xaml'))) {
  const rel = path.relative(ROOT, file);
  const xaml = fs.readFileSync(file, 'utf8').replace(/<!--[\s\S]*?-->/g, '');
  for (const tag of xaml.match(TAG) || []) {
    const tagName = (/^<([A-Za-z_][\w.:]*)/.exec(tag) || [, '?'])[1];
    const uidMatch = /\sx:Uid="([^"]*)"/.exec(tag);
    const uid = uidMatch ? uidMatch[1] : null;
    for (const prop of LOCALIZABLE) {
      const m = new RegExp('\\s' + prop + '="([^"]*)"').exec(tag);
      if (!m) continue;
      const value = m[1];
      if (value === '' || value.charAt(0) === '{') continue;
      if (ALLOWED_LITERALS.has(value) || SYMBOL_ONLY.test(value)) continue;
      if (uid && exists(uid + '.' + prop)) {
        used.add(uid + '.' + prop);
        continue;
      }
      problems.push(rel + ': <' + tagName + '> ' + prop + '="' + value + '" -> ' +
        (uid ? 'no entry "' + uid + '.' + prop + '" in both .resw files' : 'missing x:Uid'));
    }
  }
}

// ---------------------------------------------------------------------------
// C. Loc.Get keys used from code
// ---------------------------------------------------------------------------
for (const file of walk(APP, [], (n) => n.endsWith('.cs'))) {
  const rel = path.relative(ROOT, file);
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
  lines.forEach((line, i) => {
    for (const m of line.matchAll(/\bLoc\.Get\(\s*"([^"]+)"/g)) {
      const key = m[1];
      used.add(key);
      if (!exists(key)) {
        problems.push(rel + ':' + (i + 1) + ': Loc.Get("' + key +
          '") has no entry in both .resw files');
      }
    }
  });
}

// ---------------------------------------------------------------------------
// D. unused keys
// ---------------------------------------------------------------------------
for (const key of resw[LANGS[0]].keys) {
  if (!used.has(key)) warnings.push('Strings/en-US/Resources.resw: "' + key + '" is never used');
}

const report = problems.concat(STRICT ? warnings : []);
if (report.length) {
  console.log(report.join('\n'));
  if (!STRICT && warnings.length) {
    console.log('\n(' + warnings.length + ' unused key(s) — use --strict to fail on them)');
  }
  console.log('\n' + report.length + ' problem(s).');
  process.exit(1);
}
console.log('OK: ' + resw[LANGS[0]].keys.size + ' key(s) in en-US and it-IT, ' +
  'every x:Uid and Loc.Get lookup resolved.');
if (warnings.length) console.log('(' + warnings.length + ' unused key(s); --strict would fail.)');
