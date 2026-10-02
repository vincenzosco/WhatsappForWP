#!/usr/bin/env node
/**
 * tools/check-fire-and-forget.js
 *
 * Guard for a fault that no one observes.
 *
 * Perche' esiste: `StartRecordingAsync()` veniva chiamata senza await dentro un
 * `#pragma warning disable 4014`. Un'eccezione lanciata al call site - un tipo
 * che il telefono non carica, un metodo che non ha - finisce nel Task, che
 * nessuno osserva: il tap non fa nulla, non compare nessun messaggio e non
 * compare nessuna riga DIAG. Le prime due guardie del vocale erano inutili:
 * l'errore non arrivava mai ne' al `catch` dentro `AudioRecorder`, ne' al
 * chiamante, ed era quindi invisibile in un log in cui tutto il resto si vede.
 *
 * Regole:
 *  1. ogni `#pragma warning disable 4014` contiene una sola istruzione;
 *  2. quella istruzione e' una chiamata il cui fault e' osservato, cioe':
 *     - `RunGuardedAsync(...)`, che lo cattura e lo registra, oppure
 *     - una chiamata a un metodo definito nell'app il cui corpo contiene un
 *       `catch` (il metodo risponde di se stesso), oppure
 *     - `Task.Run(...)` con una chiamata diretta il cui metodo risponde di se;
 *  3. una chiamata a un metodo che l'app non definisce e' un problema: la
 *     guardia non puo' sapere se osserva il suo fault, e "non lo so" e' quello
 *     che questa guardia esiste per non accettare.
 *
 * Usage:
 *   node tools/check-fire-and-forget.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { stripComments } = require('./csharp');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');

const DISABLE = '#pragma warning disable 4014';
const RESTORE = '#pragma warning restore 4014';

/** Quanti `;` stanno nel testo fuori da ogni parentesi e da ogni blocco. */
function topLevelSemicolons(text) {
  let depth = 0;
  let count = 0;
  for (const ch of text) {
    if (ch === '(' || ch === '[' || ch === '{') depth++;
    else if (ch === ')' || ch === ']' || ch === '}') depth--;
    else if (ch === ';' && depth === 0) count++;
  }
  return count;
}

/** Il nome della prima chiamata dentro un'istruzione. */
function firstCall(text) {
  const match = /([A-Za-z_]\w*)\s*\(/.exec(text);
  return match === null ? null : match[1];
}

/**
 * Il nome di un metodo definito nel sorgente, con il suo corpo.
 *
 * La lettura e' per righe e non per testo intero: cercare la dichiarazione
 * ovunque fa combaciare `#pragma warning disable 4014` come se `4014` fosse il
 * tipo di ritorno e la chiamata sulla riga dopo fosse il nome del metodo, e la
 * guardia finisce per leggere il corpo del blocco sbagliato (e' successo con
 * `ShowLocalPreviewAsync`). Una dichiarazione comincia a inizio riga.
 */
function methods(source) {
  const code = stripComments(source);
  const found = new Map();
  const DECL = /^\s*(?:(?:public|private|internal|protected|static|async|sealed|override|new|partial|virtual)\s+)+([A-Za-z_][\w<>,\[\]\.\?]*)\s+([A-Za-z_]\w*)\s*\(/;
  const lines = code.split('\n');
  let offset = 0;
  for (const line of lines) {
    const match = DECL.exec(line);
    if (match !== null) {
      const name = match[2];
      const open = code.indexOf('{', offset + match[0].length);
      if (open >= 0) {
        let depth = 0;
        let end = -1;
        for (let i = open; i < code.length; i++) {
          if (code[i] === '{') depth++;
          else if (code[i] === '}') {
            depth--;
            if (depth === 0) { end = i; break; }
          }
        }
        // La prima definizione vince: la seconda con lo stesso nome, se
        // esiste, e' comunque un metodo che risponde di se' allo stesso modo.
        if (end >= 0 && !found.has(name)) found.set(name, code.slice(open, end + 1));
      }
    }
    offset += line.length + 1;
  }
  return found;
}

/** I corpi dietro il `#pragma`, con la riga della direttiva. */
function regions(source) {
  const code = stripComments(source);
  const out = [];
  let from = 0;
  for (;;) {
    const start = code.indexOf(DISABLE, from);
    if (start < 0) break;
    const line = code.slice(0, start).split('\n').length;
    const end = code.indexOf(RESTORE, start);
    if (end < 0) {
      out.push({ line, text: code.slice(start + DISABLE.length) });
      break;
    }
    out.push({ line, text: code.slice(start + DISABLE.length, end) });
    from = end + RESTORE.length;
  }
  return out;
}

/** Un corpo che risponde del proprio fault: un `catch` c'e'. */
function handlesItsOwnFault(body) {
  return /catch\s*\(/.test(body);
}

/**
 * Problemi di una regione: la chiamata lanciata senza await, e se qualcuno ne
 * osserva il fault.
 */
function regionProblems(region, file, known) {
  const problems = [];
  const text = region.text.trim();
  if (text === '') {
    problems.push(`${file}:${region.line}: the 4014 pragma guards nothing`);
    return problems;
  }

  // Una sola istruzione fra le due direttive: due chiamate in una regione
  // significano che una delle due e' stata aggiunta senza guardarla. I `;`
  // dentro una lambda o un delegato non contano: `Writes.RunAsync(delegate {
  // return ...; });` e' una chiamata sola.
  const semicolons = topLevelSemicolons(text);
  if (semicolons > 1) {
    problems.push(`${file}:${region.line}: more than one statement is fired without ` +
      'await between one disable/restore pair: give each one its own pair, so the ' +
      'guard can name the call that is not observed');
    return problems;
  }

  const call = firstCall(text);
  if (call === null) return problems;

  // Il caso buono, e quello per cui il helper esiste.
  if (call === 'RunGuardedAsync') return problems;

  // Task.Run(() => FooAsync()) e' la chiamata diretta dentro il delegato.
  if (call === 'Run' && /Task\.Run\s*\(/.test(text)) {
    const inner = /([A-Za-z_]\w*Async)\s*\(/.exec(text.replace(/Task\.Run\s*\(/, ''));
    if (inner === null) return problems;
    if (!known.has(inner[1])) {
      problems.push(`${file}:${region.line}: Task.Run fires ${inner[1]}(), which this app ` +
        'does not define: the guard cannot tell whether its fault is observed');
      return problems;
    }
    if (!handlesItsOwnFault(known.get(inner[1]))) {
      problems.push(`${file}:${region.line}: Task.Run fires ${inner[1]}(), which catches ` +
        'nothing: an exception it throws is lost, and the tap looks like it did nothing');
    }
    return problems;
  }

  // Dispatcher.RunAsync prende un delegato scritto li': il corpo e' cio' che si
  // vede, e deve avere un catch. `Writes.RunAsync` non entra qui: quel metodo
  // e' definito nell'app e risponde di se' come ogni altro.
  // The dispatcher is reached through a field as often as through the page's own
  // property (`_dispatcher.RunAsync`), so the name is matched without case.
  if (/Dispatcher\.RunAsync\s*\(/i.test(text)) {
    if (!handlesItsOwnFault(text)) {
      problems.push(`${file}:${region.line}: the dispatcher lambda fires without await and ` +
        'catches nothing: an exception it throws disappears');
    }
    return problems;
  }

  if (!known.has(call)) {
    problems.push(`${file}:${region.line}: ${call}() is fired without await and this app ` +
      'does not define it: the guard cannot tell whether its fault is observed. ' +
      'Route it through RunGuardedAsync, or expect it in this guard');
    return problems;
  }

  if (!handlesItsOwnFault(known.get(call))) {
    problems.push(`${file}:${region.line}: ${call}() is fired without await and catches ` +
      'nothing: an exception it throws is lost, and the caller looks like it did ' +
      'nothing at all (this is the voice note that never started)');
  }

  return problems;
}

function walk(dir, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'obj' || entry.name === 'bin') continue;
      walk(path.join(dir, entry.name), out);
    } else if (entry.name.endsWith('.cs')) {
      out.push(path.join(dir, entry.name));
    }
  }
  return out;
}

function main() {
  const files = walk(APP, []);
  const known = new Map();
  for (const file of files) {
    for (const [name, body] of methods(fs.readFileSync(file, 'utf8'))) {
      if (!known.has(name)) known.set(name, body);
    }
  }

  const problems = [];
  for (const file of files) {
    const rel = path.relative(ROOT, file).replace(/\\/g, '/');
    const source = fs.readFileSync(file, 'utf8');
    for (const region of regions(source)) {
      problems.push(...regionProblems(region, rel, known));
    }
  }

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} fire-and-forget problem(s).`);
    process.exit(1);
  }
  console.log('OK: every call fired without await is observed: a guarded helper catches ' +
    'its fault, or the method it names answers for its own.');
}

if (require.main === module) main();

module.exports = {
  firstCall,
  methods,
  regions,
  regionProblems,
  handlesItsOwnFault,
  topLevelSemicolons,
  walk
};
