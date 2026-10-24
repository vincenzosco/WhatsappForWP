#!/usr/bin/env node
/**
 * tools/check-one-row-per-chat.js
 *
 * Guard for the one thing the chat list must never do: hold two rows for one conversation.
 *
 * Why it exists: a row is created by four methods in `DataService`, and each of them is a
 * door a chat can come in by - a live message, a sync row, a tapped tile, the new-chat
 * dialog. Two of them took the id they were handed and made a row out of it:
 *
 *  - `ContactFor` looked the row up through `FindContact`, which canonicalises, and then
 *    created the row under the **raw** id it was given. A tile tapped for a chat that
 *    arrives as `...@lid` while the list holds its number made the second row, and the
 *    conversation opened from the tile was a different conversation from the one on the
 *    list - its messages there, its preview there, and a person on the list twice;
 *  - `AddContact` inserted whatever it was handed, after no lookup at all.
 *
 * The fix has two halves and the guard refuses a file that has only one of them: every
 * row is created **under the canonical address** (`ChatIdFor`, which is
 * `ChatListMerge.CanonicalId`) and **after a lookup** for the row that already has that
 * address (`FindContact`), and what slipped in anyway is folded by `FoldDuplicateChats`,
 * whose key is the canonical address and not the row's own id.
 *
 * Usage:
 *   node tools/check-one-row-per-chat.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const DATA = 'WhatsappApp/Services/DataService.cs';
const MERGE = 'WhatsappApp/Services/ChatListMerge.cs';

/** A method declaration at the service's own indentation. */
const METHOD = /^ {8}(public|private|internal|protected)\s+[^=;]*\(/;

/** The row is inserted here, and only here. */
const INSERT = /_contacts\.(Add|Insert)\(/;

/**
 * Il corpo di un metodo, dalla firma alla graffa che lo chiude (le stringhe saltate).
 * The same rule `check-chat-list-refresh.js` reads with: a brace inside a string is not
 * the end of anything, and a guard that counted braces naively would stop at the first
 * `"}"` in a log line.
 */
function bodyOf(text, signature) {
  const at = text.indexOf(signature);
  if (at < 0) return null;

  let depth = 0;
  let started = false;

  for (let i = at + signature.length; i < text.length; i++) {
    const ch = text[i];
    if (ch === '"') {
      for (i++; i < text.length; i++) {
        if (text[i] === '\\') i++;
        else if (text[i] === '"') break;
      }
      continue;
    }
    if (ch === '{') {
      depth++;
      started = true;
      continue;
    }
    if (ch === '}') {
      depth--;
      if (started && depth === 0) return text.slice(at, i + 1);
    }
  }

  return text.slice(at);
}

/** The method a line belongs to: the nearest declaration above it. */
function methodAt(lines, index) {
  for (let i = index; i >= 0; i--) {
    if (METHOD.test(lines[i])) return { line: i, name: lines[i].trim() };
  }
  return null;
}

/**
 * I problemi dei due file. Un array vuoto quando una chat ha una riga sola.
 */
function oneRowPerChatProblems(files) {
  const problems = [];
  const data = String((files && files.data) || '');
  const merge = String((files && files.merge) || '');
  const lines = data.split(/\r?\n/);

  let rows = 0;

  lines.forEach((line, i) => {
    if (!INSERT.test(line) || /^\s*\/\//.test(line)) return;
    rows++;

    const method = methodAt(lines, i);
    const start = method == null ? 0 : method.line;
    const label = method == null ? '(no method)' : method.name;
    const before = lines.slice(start, i).join('\n');

    const canonical = before.lastIndexOf('ChatIdFor(');
    const lookup = before.lastIndexOf('FindContact(');

    if (canonical < 0) {
      problems.push(`${DATA}: ${label} creates a chat row without canonicalising its id ` +
        '(no ChatIdFor before the insert): a `@lid` and the number it is paired with are ' +
        'two rows for one chat - two previews, two counts, and the conversation split in half');
    }

    if (lookup < 0 || lookup < canonical) {
      problems.push(`${DATA}: ${label} creates a chat row without looking for the row that ` +
        'already has that address (FindContact before the insert, after ChatIdFor): every ' +
        'arrival then adds a row, and the list shows one person as many times as WhatsApp ' +
        'spells their chat');
    }

    const after = lines.slice(i, i + 7).join('\n');
    if (!/_contactIndex\[/.test(after)) {
      problems.push(`${DATA}: ${label} inserts a row and does not file it in _contactIndex ` +
        'within seven lines: the next lookup for that chat misses it and adds a second row');
    }
  });

  if (rows === 0) {
    problems.push(`${DATA}: no chat row is created by this file at all (no _contacts.Add or ` +
      '_contacts.Insert): the list would have nothing to show, so the guard cannot be ' +
      'reading the file it was written for');
  }

  // The net under the four doors: what slipped in is folded, by canonical address.
  const fold = bodyOf(data, 'private int FoldDuplicateChats()');
  if (fold == null) {
    problems.push(`${DATA}: FoldDuplicateChats is missing: the rows that arrive under the ` +
      'other spelling before the pair is known would stay side by side for ever');
  } else {
    if (!fold.includes('ChatIdFor(contact.Id)')) {
      problems.push(`${DATA}: FoldDuplicateChats does not key its map on ChatIdFor(contact.Id): ` +
        'the fold is decided by the spelling the row happens to carry, which is the very ' +
        'thing that makes two rows one chat');
    }
    if (!fold.includes('OnChatMerged(')) {
      problems.push(`${DATA}: FoldDuplicateChats never calls OnChatMerged: the row goes away ` +
        'and its messages, its count and its pin with it - the conversation loses half of itself');
    }
  }

  // And the rule itself, in the one place it is written.
  const canonicalId = bodyOf(data, 'public string ChatIdFor(string chatId)');
  if (canonicalId == null || !canonicalId.includes('ChatListMerge.CanonicalId')) {
    problems.push(`${DATA}: ChatIdFor does not ask ChatListMerge.CanonicalId: the rule that ` +
      'decides which addresses are one chat would have a second copy here, and the two copies ' +
      'would drift');
  }

  if (!merge.includes('public static string CanonicalId(')) {
    problems.push(`${MERGE}: CanonicalId is gone: the rule that decides which addresses are ` +
      'one chat lives there, and is tested in ProtocolTests');
  }

  return problems;
}

function main() {
  const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

  const problems = oneRowPerChatProblems({
    data: read(DATA),
    merge: read(MERGE)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} row(s) a chat would be shown twice by.`);
    process.exit(1);
  }

  console.log('OK: every chat row is created under its canonical address and after a lookup, ' +
    'filed in the index, and the rows that slipped in are folded by canonical address.');
}

if (require.main === module) main();

module.exports = {
  oneRowPerChatProblems,
  bodyOf,
  DATA,
  MERGE,
  INSERT
};
