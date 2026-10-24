#!/usr/bin/env node
/**
 * tools/check-accent.js
 *
 * Guard on how the app asks the platform for its accent colour.
 *
 * Why it exists: `Accent.Apply` looked up three candidate resource names inside a
 * `try`, and a name the build does not have answers with a COMException thrown from
 * inside the WinRT map (0x80004005, "Cannot find a resource with the given key"). The
 * `catch` handled it, so the app was right and the debugger stopped anyway: the output
 * of 2026-10-24 opened with
 *
 *   Eccezione generata: System.Runtime.InteropServices.COMException ...
 *       at WhatsappApp.Services.Accent.Apply()
 *
 * and two of the three names are absent on every build, so it was two first-chance
 * exceptions per launch for a question a `ResourceDictionary` answers with
 * `ContainsKey`.
 *
 * Rules:
 *  1. `Accent.Apply` asks about a name with `ContainsKey` before it reads it;
 *  2. neither `Apply` nor `SetBrush` reads a resource inside a `try` - a lookup that
 *     cannot throw needs no `catch`, and a `catch` around one is the probe coming back;
 *  3. `SetBrush` asks `ContainsKey` too, and answers nothing at all in a process with
 *     no application (the background check has no `Application.Current`).
 *
 * Usage:
 *   node tools/check-accent.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { stripComments } = require('./csharp');

const ROOT = path.resolve(__dirname, '..');

const ACCENT = 'WhatsappApp/Services/Accent.cs';

/**
 * The body of `signature`'s method, braces matched and comments gone. Null when the
 * method is not there.
 */
function bodyAt(source, signature) {
  const at = source.indexOf(signature);
  if (at < 0) return null;

  const open = source.indexOf('{', at + signature.length);
  if (open < 0) return null;

  let depth = 0;
  for (let i = open; i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}') {
      depth--;
      if (depth === 0) return stripComments(source.slice(open, i + 1));
    }
  }

  return null;
}

function problemsFor(source) {
  const problems = [];

  const apply = bodyAt(source, 'public static void Apply()');
  if (!apply) {
    problems.push(`${ACCENT}: no Apply(): the one place that paints the accent is gone`);
  } else {
    if (apply.indexOf('ContainsKey') < 0) {
      problems.push(`${ACCENT}: Apply reads a resource without asking ContainsKey first: a ` +
        'name this build does not have is a COMException from inside the WinRT map, and the ' +
        'debugger stops on it at every start - the two "Cannot find a resource with the given ' +
        'key" first-chance exceptions of 24 October 2026');
    }
    if (apply.indexOf('try') >= 0) {
      problems.push(`${ACCENT}: Apply has a try again: the lookup that needs one is the ` +
        'lookup that has to be replaced by ContainsKey');
    }
  }

  const brush = bodyAt(source, 'private static void SetBrush(string key, Color color)');
  if (!brush) {
    problems.push(`${ACCENT}: no SetBrush(): the brushes the pages resolve are painted by it`);
  } else {
    if (brush.indexOf('ContainsKey') < 0) {
      problems.push(`${ACCENT}: SetBrush reads the resource dictionary without ContainsKey: ` +
        'the same COMException, once per brush');
    }
    if (brush.indexOf('Application.Current') < 0 || brush.indexOf('== null') < 0) {
      problems.push(`${ACCENT}: SetBrush does not survive a process with no application: the ` +
        'background check imports this file and never builds one');
    }
    if (brush.indexOf('try') >= 0) {
      problems.push(`${ACCENT}: SetBrush has a try again: a guarded lookup needs no catch`);
    }
  }

  return problems;
}

function main() {
  const problems = problemsFor(fs.readFileSync(path.join(ROOT, ACCENT), 'utf8'));

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} accent problem(s).`);
    process.exit(1);
  }

  console.log('OK: the accent is asked for with ContainsKey, never with a lookup a missing ' +
    'name can throw out of - and a process with no window paints nothing.');
}

if (require.main === module) main();

module.exports = {
  problemsFor,
  bodyAt,
  ACCENT
};
