#!/usr/bin/env node
/**
 * Guard for localized strings in the WP8.1 Silverlight application.
 *
 * Silverlight compiles strings as embedded .resx resources in WhatsappApp.Core. The previous
 * WinRT app used .resw files registered as PRIResource, but a Silverlight XAP has no PRI resource
 * index. This guard now validates the actual neutral and it-IT embedded resources and their
 * project registration, while keeping the literal-text, x:Uid and Loc.Get checks.
 *
 * Usage:
 * node tools/check-resw.js
 * node tools/check-resw.js --strict
 */
'use strict';

const { spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');
const CORE = path.join(ROOT, 'WhatsappApp.Core');
const STRINGS = path.join(CORE, 'Strings');
const APP_CSPROJ = path.join(APP, 'WhatsApp.csproj');
const CORE_CSPROJ = path.join(CORE, 'WhatsappApp.Core.csproj');
const LANGS = ['en-US', 'it-IT'];
const STRICT = process.argv.includes('--strict');

const LOCALIZABLE = ['Text', 'Content', 'PlaceholderText', 'Header'];
const ALLOWED_LITERALS = new Set(['WhatsApp']);
const SYMBOL_ONLY = /^[^\p{L}]*$/u;
const TAG = /<[A-Za-z_][^<>]*>/g;

function walk(dir, out, keep) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'obj' || entry.name === 'bin') continue;
      walk(path.join(dir, entry.name), out, keep);
    } else if (keep(entry.name)) out.push(path.join(dir, entry.name));
  }
  return out;
}

function readResources(file) {
  const xml = fs.readFileSync(file, 'utf8');
  const keys = new Set();
  for (const match of xml.matchAll(/<data\s+name="([^"]+)"/g)) keys.add(match[1]);
  return { xml, keys };
}

const problems = [];
const warnings = [];
const used = new Set();
const appProject = fs.readFileSync(APP_CSPROJ, 'utf8');
const coreProject = fs.readFileSync(CORE_CSPROJ, 'utf8');
const resourcesByLanguage = {
  'en-US': path.join(STRINGS, 'Resources.resx'),
  'it-IT': path.join(STRINGS, 'Resources.it-IT.resx')
};

for (const lang of LANGS) {
  const file = resourcesByLanguage[lang];
  const relative = path.relative(ROOT, file).replace(/\\/g, '/');
  if (!fs.existsSync(file)) {
    problems.push('missing ' + relative);
    continue;
  }
  const include = 'Strings\\' + (lang === 'en-US' ? 'Resources.resx' : 'Resources.it-IT.resx');
  if (!coreProject.includes('<EmbeddedResource Include="' + include + '"')) {
    problems.push('WhatsappApp.Core/WhatsappApp.Core.csproj: "' + include +
      '" is not registered as an EmbeddedResource');
  }
}

const defaultLanguage = /<DefaultLanguage>([^<]+)<\/DefaultLanguage>/.exec(appProject);
if (!defaultLanguage) {
  problems.push('WhatsappApp/WhatsApp.csproj: no <DefaultLanguage>');
} else if (LANGS.indexOf(defaultLanguage[1]) < 0) {
  problems.push('WhatsappApp/WhatsApp.csproj: <DefaultLanguage>' + defaultLanguage[1] +
    '</DefaultLanguage> is not one of ' + LANGS.join(', '));
}
if (!/<SupportedCultures>[^<]*en-US[^<]*it-IT[^<]*<\/SupportedCultures>/.test(appProject)) {
  problems.push('WhatsappApp/WhatsApp.csproj: <SupportedCultures> must include en-US and it-IT');
}

const resx = {};
const hasXmllint = spawnSync('xmllint', ['--version'], { stdio: 'ignore' }).status === 0;
for (const lang of LANGS) {
  const file = resourcesByLanguage[lang];
  if (!fs.existsSync(file)) {
    resx[lang] = { xml: '', keys: new Set() };
    continue;
  }
  if (hasXmllint && spawnSync('xmllint', ['--noout', file], { stdio: 'ignore' }).status !== 0) {
    problems.push(path.relative(ROOT, file).replace(/\\/g, '/') + ': not well-formed XML');
  }
  resx[lang] = readResources(file);
}

for (const key of resx['en-US'].keys) {
  if (!resx['it-IT'].keys.has(key)) {
    problems.push('WhatsappApp.Core/Strings/Resources.resx: "' + key + '" has no it-IT translation');
  }
}
for (const key of resx['it-IT'].keys) {
  if (!resx['en-US'].keys.has(key)) {
    problems.push('WhatsappApp.Core/Strings/Resources.it-IT.resx: "' + key + '" has no en-US entry');
  }
}
for (const key of resx['en-US'].keys) {
  const dot = key.indexOf('.');
  if (dot < 0) continue;
  const base = key.slice(0, dot);
  if (resx['en-US'].keys.has(base)) {
    problems.push('WhatsappApp.Core/Strings/Resources.resx: "' + base + '" and "' + key +
      '" cannot coexist (duplicate resource identifier)');
  }
}

function exists(key) {
  return resx['en-US'].keys.has(key) && resx['it-IT'].keys.has(key);
}

for (const file of walk(APP, [], (name) => name.endsWith('.xaml'))) {
  const relative = path.relative(ROOT, file).replace(/\\/g, '/');
  const xaml = fs.readFileSync(file, 'utf8').replace(/<!--[\s\S]*?-->/g, '');
  for (const tag of xaml.match(TAG) || []) {
    const tagName = (/^<([A-Za-z_][\w.:]*)/.exec(tag) || [, '?'])[1];
    const uidMatch = /\sx:Uid="([^"]*)"/.exec(tag);
    const uid = uidMatch ? uidMatch[1] : null;
    for (const property of LOCALIZABLE) {
      const match = new RegExp('\\s' + property + '="([^"]*)"').exec(tag);
      if (!match) continue;
      const value = match[1];
      if (value === '' || value.charAt(0) === '{') continue;
      if (ALLOWED_LITERALS.has(value) || SYMBOL_ONLY.test(value)) continue;
      if (uid && exists(uid + '.' + property)) {
        used.add(uid + '.' + property);
        continue;
      }
      problems.push(relative + ': <' + tagName + '> ' + property + '="' + value + '" -> ' +
        (uid ? 'no entry "' + uid + '.' + property + '" in both resources' : 'missing x:Uid'));
    }
  }
}

for (const file of walk(APP, [], (name) => name.endsWith('.cs'))) {
  const relative = path.relative(ROOT, file).replace(/\\/g, '/');
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
  lines.forEach((line, index) => {
    for (const match of line.matchAll(/\bLoc\.Get\(\s*"([^"]+)"/g)) {
      const key = match[1];
      used.add(key);
      if (!exists(key)) {
        problems.push(relative + ':' + (index + 1) + ': Loc.Get("' + key +
          '") has no entry in both embedded resources');
      }
    }
  });
}

for (const key of resx['en-US'].keys) {
  if (used.has(key)) continue;
  if (key === 'ChatPage_Retry.Text' && /Loc\.Get\s*\(\s*key\s*,\s*"Try again"\s*\)/.test(
      fs.readFileSync(path.join(APP, 'Pages', 'ChatPage.xaml.cs'), 'utf8'))) {
    used.add(key);
    continue;
  }
  warnings.push('WhatsappApp.Core/Strings/Resources.resx: "' + key + '" is never used');
}

const report = problems.concat(STRICT ? warnings : []);
if (report.length) {
  console.log(report.join('\n'));
  if (!STRICT && warnings.length) console.log('\n(' + warnings.length + ' unused key(s) — use --strict to fail on them)');
  console.log('\n' + report.length + ' problem(s).');
  process.exit(1);
}
console.log('OK: ' + resx['en-US'].keys.size + ' key(s) in embedded en-US and it-IT resources, every x:Uid and Loc.Get lookup resolved.');
if (warnings.length) console.log('(' + warnings.length + ' unused key(s); --strict would fail.)');
