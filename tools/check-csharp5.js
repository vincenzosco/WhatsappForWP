#!/usr/bin/env node
/**
 * tools/check-csharp5.js
 *
 * Guard for the Windows Phone 8.1 app: the WP8.1 toolchain compiles the app
 * with the legacy C# 5 compiler, so any C# 6/7 syntax in the WhatsappApp or
 * WhatsappServer C# sources breaks the build with errors such as
 * "Invalid token '=' in class, struct, or interface member declaration"
 * and "Unexpected character '$'".
 *
 * Usage:  node tools/check-csharp5.js
 * Exit code 0 = every file is C# 5 compatible, 1 = violations found.
 */
'use strict';

const fs = require('fs');
const path = require('path');

const PROJECT_ROOT = path.resolve(__dirname, '..');
const SCAN_ROOTS = ['WhatsappApp', 'WhatsappServer'];
const SKIP_DIRS = new Set(['bin', 'obj', 'node_modules', '.git', 'AppPackages']);

const RULES = [
  { name: 'interpolated string', re: /\$@?"/ },
  { name: 'null-conditional operator ?.', re: /\?\./ },
  { name: 'expression-bodied property', re: /^\s*(public|private|protected|internal)[^;{}()]*=>/ },
  { name: 'expression-bodied method', re: /^\s*(public|private|protected|internal)[^;{}]*\)\s*=>/ },
  { name: 'expression-bodied accessor', re: /^\s*(get|set)\s*=>/ },
  { name: 'auto-property initializer', re: /\{[^{}]*\bget;[^{}]*\bset;[^{}]*\}\s*=/ },
  { name: 'inline out variable declaration', re: /\bout\s+(var|int|string|bool|long|byte|double|float)\s+\w+\s*[,)]/ },
  { name: 'pattern matching (is Type name)', re: /\bis\s+[A-Z]\w*\s+[a-z]\w*\s*[,)]/ },
  { name: 'nameof(...)', re: /\bnameof\s*\(/ },
  { name: 'discard assignment (_ = ...)', re: /^\s*_+\s*=[^=]/ },
  { name: 'using static', re: /^\s*using\s+static\s/ }
];

function walk(dir, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (SKIP_DIRS.has(entry.name)) continue;
      walk(path.join(dir, entry.name), out);
    } else if (entry.name.endsWith('.cs')) {
      out.push(path.join(dir, entry.name));
    }
  }
  return out;
}

const files = [];
for (const root of SCAN_ROOTS) {
  const abs = path.join(PROJECT_ROOT, root);
  if (fs.existsSync(abs)) walk(abs, files);
}

let violations = 0;
for (const file of files) {
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
  const rel = path.relative(PROJECT_ROOT, file);
  for (let i = 0; i < lines.length; i++) {
    for (const rule of RULES) {
      if (rule.re.test(lines[i])) {
        violations++;
        console.log(rel + ':' + (i + 1) + ': ' + rule.name + '  ->  ' + lines[i].trim());
      }
    }
  }
}

if (violations > 0) {
  console.log('\n' + violations + ' C# 6/7 construct(s) found in ' + files.length + ' file(s).');
  console.log('The WP8.1 toolchain needs C# 5 syntax (see docs/superpowers/plans/2026-09-24-wp81-csharp5-port.md).');
  process.exit(1);
}

console.log('OK: ' + files.length + ' C# file(s) are C# 5 compatible.');
