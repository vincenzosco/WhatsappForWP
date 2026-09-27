#!/usr/bin/env node
/**
 * tools/check-csharp5.js
 *
 * Guard for the Windows Phone 8.1 app. It fails on two classes of problem:
 *  1. C# 6/7 syntax: the WP8.1 toolchain compiles the app with the legacy
 *     C# 5 compiler, so such syntax breaks the build with errors such as
 *     "Invalid token '=' in class, struct, or interface member declaration"
 *     and "Unexpected character '$'".
 *  2. Windows 8.1/Windows 10-only members that the reduced WP8.1 WinRT
 *     projection does not expose, which break the build with CS1501/CS1061.
 *
 * Usage:  node tools/check-csharp5.js
 * Exit code 0 = every file is compatible, 1 = violations found.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { stripComments } = require('./csharp');

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
  { name: 'using static', re: /^\s*using\s+static\s/ },
  // An async entry point needs C# 7.1 (and .NET 4.6.1+); on this toolchain the
  // compiler answers CS0028 "wrong signature to be an entry point" plus CS5001
  // "does not contain a static 'Main' method", so the project never builds at
  // all - which is how WhatsappServer sat broken.
  { name: 'async entry point (needs C# 7.1; use static void Main)', re: /\basync\s+Task(\s*<[^>]*>)?\s+Main\s*\(/ }
];

// Extension methods that only exist when `using System.Linq;` is in scope.
// Their absence is a build error the syntax rules above cannot see: the
// WP8.1 compiler answers CS1061, e.g. "'string' does not contain a definition
// for 'All' ... missing a using directive or an assembly reference".
// Deliberately narrow: instance methods with the same name (MemoryStream's
// ToArray, ICollection's Contains, Math.Min/Max) are not in the list.
const LINQ_USING = /^\s*using\s+System\.Linq\s*;/m;
const LINQ_EXTENSION = /\.(All|Any|Where|Select|SelectMany|First|FirstOrDefault|Last|LastOrDefault|Single|SingleOrDefault|OrderBy|OrderByDescending|ThenBy|GroupBy|Distinct|Skip|Take|ToList|Aggregate)\s*\(/;

// Members that exist on Windows 8.1 / Windows 10 but not on the WP8.1
// WinRT projection (the WP8.1 compiler answers CS1501 / CS1061, or CS0234 when
// a whole type is missing).
const API_RULES = [
  { name: 'CryptographicBuffer.CreateFromByteArray with 3 args (WP8.1 has only the 1-arg overload)', re: /CreateFromByteArray\s*\([^)]*,[^)]*,[^)]*\)/ },
  { name: 'ContentDialog.CloseButtonText (WP8.1 has no CloseButtonText)', re: /(\.CloseButtonText\b)|(^\s*CloseButtonText\s*=)/ },
  // The WP8.1 projection has no deferral for a share: `ShareOperation.GetDeferral()`
  // is a CS1061 and `Windows.Foundation.Deferral` is a CS0234. A share target does
  // not need one - its app is in the foreground, so the operation stays valid;
  // just call ReportStarted() -> ReportDataRetrieved() -> ReportCompleted().
  // SuspendingOperation.GetDeferral() is the one WP8.1 does have, so it is allowed.
  { name: 'ShareOperation.GetDeferral() (WP8.1 has no deferral on a share)', re: /(?<!SuspendingOperation)\.GetDeferral\s*\(/ },
  { name: 'Windows.Foundation.Deferral (does not exist in the WP8.1 projection)', re: /\bWindows\.Foundation\.Deferral\b/ }
];

/**
 * Le righe che contengono un await dentro il corpo di un catch.
 *
 * Perche' esiste: C# 5 non lascia attendere dentro un catch, e il compilatore
 * risponde CS1985 ("Impossibile attendere nel corpo di una clausola catch").
 * Non e' una proprieta' della riga ma di dove la riga si trova, quindi le
 * regole per riga qui sopra non lo vedono: lo ha trovato la build Windows, non
 * questo guard. Un await su una lambda ("=> ... await") appartiene alla lambda,
 * non al catch, e C# 5 lo accetta: si segnala solo un await nudo.
 */
function awaitInCatchBlocks(code) {
  const hits = [];
  const re = /\bcatch\b/g;
  let m;
  while ((m = re.exec(code)) !== null) {
    const open = code.indexOf('{', m.index);
    if (open < 0) continue;
    let depth = 0;
    let end = -1;
    for (let i = open; i < code.length; i++) {
      const ch = code[i];
      if (ch === '{') depth++;
      else if (ch === '}') { depth--; if (depth === 0) { end = i; break; } }
    }
    if (end < 0) continue;

    const bodyLines = code.slice(open + 1, end).split('\n');
    const firstLine = code.slice(0, open).split('\n').length;
    for (let i = 0; i < bodyLines.length; i++) {
      if (/\bawait\b/.test(bodyLines[i]) && !/=>/.test(bodyLines[i])) {
        hits.push(firstLine + i);
        break;
      }
    }
  }
  return hits;
}

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
  const source = fs.readFileSync(file, 'utf8');
  const lines = source.split(/\r?\n/);
  const rel = path.relative(PROJECT_ROOT, file);
  const hasLinqUsing = LINQ_USING.test(source);
  for (let i = 0; i < lines.length; i++) {
    for (const rule of RULES.concat(API_RULES)) {
      if (rule.re.test(lines[i])) {
        violations++;
        console.log(rel + ':' + (i + 1) + ': ' + rule.name + '  ->  ' + lines[i].trim());
      }
    }
    if (!hasLinqUsing && LINQ_EXTENSION.test(lines[i].replace(/\/\/.*$/, ''))) {
      violations++;
      console.log(rel + ':' + (i + 1) + ': LINQ extension method without "using System.Linq;"  ->  ' + lines[i].trim());
    }
  }

  const code = stripComments(source);
  for (const lineNo of awaitInCatchBlocks(code)) {
    violations++;
    console.log(rel + ':' + lineNo + ': await inside a catch block (C# 5 answers CS1985)  ->  ' +
      (lines[lineNo - 1] || '').trim());
  }
}

if (violations > 0) {
  console.log('\n' + violations + ' incompatible construct(s) found in ' + files.length + ' file(s).');
  console.log('The WP8.1 toolchain needs C# 5 syntax and the WP8.1 API surface');
  console.log('(see docs/superpowers/plans/2026-09-24-wp81-csharp5-port.md and');
  console.log('docs/superpowers/plans/2026-09-24-wp81-remaining-build-errors.md).');
  process.exit(1);
}

console.log('OK: ' + files.length + ' C# file(s) are C# 5 compatible.');
