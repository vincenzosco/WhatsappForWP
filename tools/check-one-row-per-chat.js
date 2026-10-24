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
 * The same rule is owed by the two caches that hold rows of their own:
 *
 *  - **the client's conversation list** (`NativeChats`), which is where the groups are -
 *    a group is a row like any other, and its jid can arrive with a device on it as easily
 *    as a person's can. `EnsureRow` looks the row up first and canonicalises before it
 *    adds, and `Find` compares canonical addresses and not spellings;
 *  - **the status feed** (`StatusCache` and `DataService`). A status is keyed on its id
 *    (`FindStatus`), and the file restored at a start can carry the same id twice: a row
 *    drawn twice is an update watched once and a ring filled twice.
 *
 * And the duplicate that does survive is **named**: `NameDuplicateRows` writes, once per
 * pair, the two rows and the address they share. A list that holds one chat twice and says
 * so nowhere is a bug nobody can find on a phone nobody can look at.
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
const CHATS = 'WhatsappApp/Protocol/Client/NativeChats.cs';
const STATUS = 'WhatsappApp/Services/StatusCache.cs';

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

/** How many times a phrase appears. */
function count(text, phrase) {
  let seen = 0;
  let at = text.indexOf(phrase);
  while (at >= 0) {
    seen++;
    at = text.indexOf(phrase, at + phrase.length);
  }
  return seen;
}

/**
 * I problemi dei due file. Un array vuoto quando una chat ha una riga sola.
 */
function oneRowPerChatProblems(files) {
  const problems = [];
  const data = String((files && files.data) || '');
  const merge = String((files && files.merge) || '');
  const chats = String((files && files.chats) || '');
  const status = String((files && files.status) || '');
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

  // 2. The client's own list, where the groups are.
  const rowWrites = count(chats, '_rows.Add(');
  if (rowWrites !== 1) {
    problems.push(`${CHATS}: _rows.Add appears ${rowWrites} time(s) and must appear once, in ` +
      'EnsureRow: a row added anywhere else is a row added without a lookup');
  }

  const ensure = bodyOf(chats, 'private HistoryConversation EnsureRow(string jid)');
  if (ensure == null) {
    problems.push(`${CHATS}: EnsureRow is missing: the conversation list has no one door a row ` +
      'can come in by, so this guard cannot say what every row was checked for');
  } else {
    const lookup = ensure.indexOf('Find(jid)');
    const canonical = ensure.indexOf('_names.Canonical(jid)');
    const add = ensure.indexOf('_rows.Add(');

    if (lookup < 0 || add < 0 || lookup > add) {
      problems.push(`${CHATS}: EnsureRow creates a row without looking for the row that already ` +
        'has that address (Find(jid) before _rows.Add): a group is a row like any other, and ' +
        'a second row for one group is the same conversation twice');
    }

    if (canonical < 0 || canonical > add) {
      problems.push(`${CHATS}: EnsureRow creates the row under the address it was handed ` +
        '(_names.Canonical after _rows.Add, or not at all): the spelling a group or a contact ' +
        'arrives with is not the one the list keys on, and the fold exists precisely because ' +
        'those two drift apart');
    }
  }

  const find = bodyOf(chats, 'public HistoryConversation Find(string jid)');
  if (find == null || !find.includes('_names.Canonical(jid)')) {
    problems.push(`${CHATS}: Find compares the jid it was handed with the rows as they were ` +
      'spelled (no _names.Canonical): the row for a chat is not found under its other address, ' +
      'so the lookup that keeps a second row from being made misses');
  }

  // 3. The status feed: one row per status id, in memory and in the file.
  const load = bodyOf(status, 'public static async Task<List<ChatMessage>> LoadAsync()');
  if (load == null) {
    problems.push(`${STATUS}: LoadAsync is missing: the feed restored at a start has no reader ` +
      'this guard can check');
  } else if (!load.includes('HashSet<string>') || !load.includes('Add(entry.Id)')) {
    problems.push(`${STATUS}: LoadAsync restores every entry of the file (no HashSet of ids): a ` +
      'copy that carries the same status twice is drawn twice - an update watched once and a ' +
      'ring that fills for it twice');
  }

  const statusRows = count(data, '_statuses.Insert(');
  if (statusRows !== 1) {
    problems.push(`${DATA}: _statuses.Insert appears ${statusRows} time(s) and must appear once, ` +
      'in InsertStatusInOrder: a status inserted anywhere else is a status inserted without ' +
      'being looked for');
  }

  const post = bodyOf(data, 'private void OnNativeStatusPost(object sender, StatusPost post)');
  if (post == null) {
    problems.push(`${DATA}: OnNativeStatusPost is missing: the status feed has no single door a ` +
      'status can come in by');
  } else {
    const lookup = post.indexOf('FindStatus(post.Id)');
    const insert = post.indexOf('InsertStatusInOrder(');
    if (lookup < 0 || insert < 0 || lookup > insert) {
      problems.push(`${DATA}: OnNativeStatusPost inserts a status without looking for the row ` +
        'that already has its id (FindStatus(post.Id) before InsertStatusInOrder): the sync ' +
        'sends the same status again in every chunk, and each copy is another row');
    }
  }

  // 4. And the duplicate that survives the fold is named in the log.
  const namer = bodyOf(data, 'private void NameDuplicateRows()');
  if (namer == null) {
    problems.push(`${DATA}: NameDuplicateRows is missing: a list that holds one chat twice says ` +
      'so nowhere, and a report from a phone nobody can look at cannot name it');
  } else {
    if (!namer.includes('ChatIdFor(row.Id)')) {
      problems.push(`${DATA}: NameDuplicateRows compares the rows by the spelling they carry ` +
        '(no ChatIdFor(row.Id)): the two rows it is looking for differ in exactly that');
    }
    if (!namer.includes('Diag.Ok(')) {
      problems.push(`${DATA}: NameDuplicateRows names the pair to nobody (no Diag.Ok): the guard ` +
        'exists so the duplicate is written to the log, not so it is found and dropped');
    }
  }

  const refreshBody = bodyOf(data, 'public void RefreshChatList()');
  if (refreshBody != null && !refreshBody.includes('NameDuplicateRows();')) {
    problems.push(`${DATA}: RefreshChatList does not call NameDuplicateRows(): nothing ever looks ` +
      'for the two rows a fold cannot take, so the line the last check is about is never written');
  }

  return problems;
}

function main() {
  const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

  const problems = oneRowPerChatProblems({
    data: read(DATA),
    merge: read(MERGE),
    chats: read(CHATS),
    status: read(STATUS)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} row(s) a chat would be shown twice by.`);
    process.exit(1);
  }

  console.log('OK: every chat row - the list\'s, the client\'s (groups included) and the status '
    + 'feed\'s - is created under its canonical address and after a lookup, filed in the index, '
    + 'the rows that slipped in are folded by canonical address, and the pair that survives the '
    + 'fold is named in the log.');
}

if (require.main === module) main();

module.exports = {
  oneRowPerChatProblems,
  bodyOf,
  DATA,
  MERGE,
  CHATS,
  STATUS,
  INSERT
};
