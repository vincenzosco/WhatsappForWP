#!/usr/bin/env node
/**
 * tools/check-send-path.js
 *
 * Guard on the exits of the composer.
 *
 * Why it exists: an operator reported that messages and media
 * were not going out, and the phone's own `diag.log` had nothing to say - not one
 * line from the send path. The reason was in the code: five `return`s on the way
 * out, each of them silent. `SendMessage` dropped a second tap while a send was
 * in flight, `SendAttachmentAsync` dropped a pick with no file, both send paths
 * marked the bubble Failed when the socket was closed and said nothing, and an
 * empty composer and a recording that stopped with no file returned without a
 * word. A tap that fails and a tap that was never made therefore looked exactly
 * the same, which is the one thing a log must never allow.
 *
 * The rule: every `return;` in the send path sits in a block that says why -
 * `Diag.Ok(.)`, `Diag.Failed(.)`, `BeginSend(.)` which logs the refusal
 * itself, or the hand-off to the attachment path. The evidence is looked for in
 * the statement that holds the `return` and not in the lines above it: a `Diag`
 * line belonging to the previous `if` does not explain this one, which is exactly
 * how a guard written as "a log line within eight lines" let two silent returns
 * through on the first run.
 *
 * What this guard does not do: it does not care which sentence, nor whether the
 * send succeeded. It holds one property, that no exit on the way out is mute.
 *
 * Usage:
 * node tools/check-send-path.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const PAGE = 'WhatsappApp/Pages/ChatPage.xaml.cs';

/**
 * The methods a tap goes through, and the signature that finds each one.
 *
 * `RetryAsync` is here because a failed bubble's second send is a send path like
 * any other, and the one that runs when the connection is flaky.
 */
const METHODS = [
  { name: 'SendMessage', signature: 'private async void SendMessage()' },
  { name: 'SendMessageCoreAsync', signature: 'private async System.Threading.Tasks.Task SendMessageCoreAsync()' },
  { name: 'SendAttachmentAsync', signature: 'private async System.Threading.Tasks.Task SendAttachmentAsync' },
  { name: 'AddAndSendMessage', signature: 'private async void AddAndSendMessage' },
  { name: 'RetryAsync', signature: 'private async System.Threading.Tasks.Task RetryAsync' }
];

/**
 * What counts as saying why a tap did not go: a line of `Diag`, a lane taken (or
 * refused, which `BeginSend` logs), or the hand-off to the attachment path, which is
 * checked here as well and so cannot be silent either.
 */
const SAYS_WHY = /Diag\.(Ok|Failed)\(|BeginSend\(|SendAttachmentAsync\(|AddAndSendMessage\(/;

/** The indentation of a line, in characters. */
function indentation(line) {
  const m = /^[ \t]*/.exec(line);
  return m ? m[0].length : 0;
}

/**
 * The statement that holds the `return`: the `return` itself when it shares its line
 * with the `if` that guards it (`if (!BeginSend(x)) return;`), and otherwise the
 * block from the `{` that opened it - so a `Diag` line belonging to the `if` above
 * cannot excuse it. A closing brace at the `return`'s own indentation ends the walk:
 * that brace closes the previous block, not this one.
 */
function explainingStatement(lines, index) {
  const own = lines[index];

  // A one-liner is its own statement, and carries its own reason.
  if (own.trim() !== 'return;') return own;

  const indent = indentation(own);
  const region = [];

  for (let i = index - 1; i >= 0; i--) {
    const line = lines[i];
    const trimmed = line.trim();

    if (!trimmed) continue;
    if (indentation(line) < indent) { region.unshift(line); break; }
    if (trimmed === '}') break;

    region.unshift(line);
  }

  region.push(own);
  return region.join('\n');
}

/**
 * The body of a method, with the line number it starts at: a problem names a line
 * of `ChatPage.xaml.cs`, which is where the next person looks.
 */
function methodAt(text, signature) {
  const at = text.indexOf(signature);
  if (at < 0) return null;

  const startLine = text.slice(0, at).split('\n').length;
  const rest = text.slice(at + signature.length);
  const end = rest.indexOf('\n        }');
  const body = end < 0 ? rest : rest.slice(0, end);

  return { startLine: startLine + 1, lines: body.split('\n') };
}

/**
 * The problems of the page.
 * Returns an array of sentences, empty when every exit says why.
 */
function sendPathProblems(page) {
  const source = String(page || '');
  const problems = [];

  for (let m = 0; m < METHODS.length; m++) {
    const method = METHODS[m];
    const found = methodAt(source, method.signature);

    if (!found) {
      problems.push(`${PAGE}: the send path has no ${method.name} - the method this guard `
        + 'reads was renamed or moved, so the taps it drops are checked by nothing');
      continue;
    }

    for (let i = 0; i < found.lines.length; i++) {
      if (!/\breturn\s*;/.test(found.lines[i])) continue;

      const statement = explainingStatement(found.lines, i);
      if (SAYS_WHY.test(statement)) continue;

      problems.push(`${PAGE}: ${method.name} has a return with no DIAG line `
        + `(line ${found.startLine + i}): a tap the send path drops must say why in `
        + 'diag.log, or a message that never left is indistinguishable from one never '
        + 'written');
    }
  }

  return problems;
}

function main() {
  const problems = sendPathProblems(fs.readFileSync(path.join(ROOT, PAGE), 'utf8'));

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} silent exit(s) in the send path.`);
    process.exit(1);
  }

  console.log('OK: every exit of the send path says in diag.log why the tap did not go.');
}

if (require.main === module) main();

module.exports = { sendPathProblems, methodAt };
