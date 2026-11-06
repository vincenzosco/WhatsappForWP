#!/usr/bin/env node
/**
 * tools/check-session-save.js
 *
 * Guard on where a send writes its sessions back.
 *
 * Why it exists: `SessionRecord.Encrypt` advances the sending chain, and the store is the
 * only thing that says how far it got. `KeepSessions` writes those records back, and on
 * it stood on the line *after* the stanza had been written - so a send
 * that threw while the stanza was being built (`the platform's generator returned no
 * entropy`, in the media path) left the chain advanced in memory and the store behind it.
 * The next send re-encrypted a key the receiver had already spent and was dropped as a
 * duplicate, with no receipt and no retry.
 *
 * The rule: every `KeepSessions` call sits in a `finally`, so the store is written whether
 * the stanza left or not.
 *
 * Usage:
 * node tools/check-session-save.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const CONNECTION = 'WhatsappApp/Services/NativeConnection.cs';

/** The body of a method, from its signature to its closing brace (or 4000 chars). */
function methodBody(text, signature) {
  const at = text.indexOf(signature);
  if (at < 0) return '';

  const rest = text.slice(at + signature.length);
  const end = rest.indexOf('\n        }');
  return end < 0 ? rest.slice(0, 4000) : rest.slice(0, end);
}

/**
 * The block the call at `at` sits directly inside, by its braces: `finally`, `try` or
 * something else.
 *
 * By braces and not by the nearest keyword before it: that is how the first version of this
 * guard read a call that had been taken out of a `try` as if the `finally` below it still
 * held it, and printed OK.
 */
function enclosingBlock(body, at) {
  let depth = 0;

  for (let i = at - 1; i >= 0; i--) {
    const ch = body[i];

    if (ch === '}') depth++;
    else if (ch === '{') {
      if (depth > 0) {
        depth--;
        continue;
      }

      const before = body.slice(Math.max(0, i - 24), i).trim();
      if (before.endsWith('finally')) return 'finally';
      if (before.endsWith('try')) return 'try';

      return 'other';
    }
  }

  return 'none';
}

/**
 * The problems of a connection file.
 * Returns an array of sentences, empty when nothing is wrong.
 */
function sessionSaveProblems(files) {
  // The file is CRLF on disk and this reads by lines: one normalisation, here, and every
  // pattern below is written with a plain `\n`.
  const connection = String((files && files.connection) || '').replace(/\r\n/g, '\n');
  const problems = [];

  const calls = [...connection.matchAll(/KeepSessions\s*\(/g)]
    // The declaration is not a call: `private static void KeepSessions(.)`.
    .filter((match) => !/void\s+$/.test(connection.slice(Math.max(0, match.index - 40), match.index)));

  if (calls.length === 0) {
    problems.push(`${CONNECTION}: there is no KeepSessions call at all - every send would ` +
      'leave its sessions at their pre-send state and re-encrypt the same key');
    return problems;
  }

  const sendMethods = [
    'public async Task<string> SendTextAsync(string jid, string body, IList<string> mentionedJids)',
    'public async Task<string> RevokeAsync(string jid, string messageId)',
    'public async Task<string> SendMediaAsync(string jid, byte[] file, string mediaType, string mimeType,'
  ];

  for (const signature of sendMethods) {
    const body = methodBody(connection, signature);
    const name = signature.replace(/^(public async Task<string> )/, '').split('(')[0];

    if (!body) {
      problems.push(`${CONNECTION}: ${name} is missing: a send path this guard knows has gone`);
      continue;
    }

    const at = body.indexOf('KeepSessions(');
    if (at < 0) {
      problems.push(`${CONNECTION}: ${name} never writes its sessions back (no KeepSessions): ` +
        'the next message re-encrypts a key the receiver has already spent');
      continue;
    }

    if (enclosingBlock(body, at) !== 'finally') {
      problems.push(`${CONNECTION}: ${name} writes its sessions back outside a finally - a ` +
        'send that throws after encrypting leaves the store behind the chain');
    }
  }

  return problems;
}

function main() {
  const problems = sessionSaveProblems({
    connection: fs.readFileSync(path.join(ROOT, CONNECTION), 'utf8')
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} session save problem(s).`);
    process.exit(1);
  }

  console.log('OK: every send writes its sessions back, whether the stanza left or threw.');
}

if (require.main === module) main();

module.exports = { sessionSaveProblems, methodBody };
