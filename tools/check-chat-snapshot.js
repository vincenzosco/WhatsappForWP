#!/usr/bin/env node
/**
 * tools/check-chat-snapshot.js
 *
 * Guard for what the phone keeps of the chat list and the conversations, and in
 * which order it is written: **the chat list first, the conversations after it**,
 * and **a conversation that changed while the write was walking the tails is not
 * written back from the copy taken before it**.
 *
 * Why it exists: the app was closed and reopened on an empty list, which stayed
 * empty until a message arrived - and then the list held only that one chat. The
 * phone's store said why: `chats.json` was not there at all, while there were 57
 * `messages_*.json`. `SaveSnapshotAsync` wrote every conversation's tail (one
 * file, read and rewritten, one at a time) and the list only at the end: on a
 * phone with 169 rows that round does not reach the end before the app is closed,
 * and closing is the normal case, not the exception. A row that is on disk and a
 * tail missing is a list that opens empty the next time; the other way round is
 * only a conversation that opens one message short.
 *
 * Rules:
 *
 * 1. Inside `DataService.SaveSnapshotAsync` the call to `ChatCache.SaveAsync`
 *    comes before the loop that calls `MessageCache.AppendAsync`.
 * 2. The live message path asks for the write (`ScheduleListSave`), because a run
 *    does not always end with a suspension - it was stopped from the debugger with
 *    a row that had just arrived, and the list was the day before's. Every writer
 *    was an *ending* (end of sync, suspension, end of a background check).
 * 3. The start asks the tails for the rows `chats.json` does not name
 *    (`RestoreRowsFromTheirTailsAsync`): that file held 2 rows against 56
 *    `messages_*.json`, so 54 conversations were on the phone and not on the list,
 *    and no server names the list again except at a link.
 * 4. The tail loop checks every tail against the conversation it belongs to
 *    before writing it. The rows were copied when the snapshot began and the loop
 *    writes one file at a time - minutes on the phone - so a chat deleted (or a
 *    message removed) while it runs would be written back by the copy taken
 *    before the deletion, and the deleted conversation returns at the next
 *    open-and-close. `MessageCache.AppendAsync` merges by id, so a re-written
 *    file resurrects rows the reader had just deleted.
 * 5. `MessageCache` runs its deletes through the same write queue as its writes.
 *    A delete that runs beside a write still in flight is undone by that write:
 *    the queue makes "load, merge, write" one step and must own the delete too.
 *
 * Usage:
 * node tools/check-chat-snapshot.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { stripComments } = require('./csharp');

const ROOT = path.resolve(__dirname, '..');
const SERVICE = path.join(ROOT, 'WhatsappApp', 'Services', 'DataService.cs');
const CACHE = path.join(ROOT, 'WhatsappApp', 'Services', 'MessageCache.cs');

/** The body of a method, from its declaration to the closing brace at eight spaces. */
function methodBody(source, name) {
  const at = source.search(new RegExp('(?:private|public|internal)[^;{]*\\b' + name + '\\s*\\('));
  if (at < 0) return '';

  const rest = source.slice(at);
  const end = rest.indexOf('\n        }');

  return end < 0 ? rest : rest.slice(0, end);
}

/**
 * The problems of the snapshot and of the tail cache: empty when the list is
 * written first, a stale tail is skipped and the deletes go through the queue.
 * `cache` is the source of `MessageCache.cs`; when it is missing the rules that
 * read it are not run (fixtures that carry one file only).
 */
function problemsFor(service, cache) {
  const problems = [];
  const where = 'WhatsappApp/Services/DataService.cs';
  const source = stripComments(service);
  const body = methodBody(source, 'SaveSnapshotAsync');

  if (!body) {
    problems.push(where + ': no SaveSnapshotAsync - the list and the conversations the next '
      + 'start draws are written from there');
    return problems;
  }

  const list = body.indexOf('ChatCache.SaveAsync');
  const tails = body.indexOf('MessageCache.AppendAsync');

  if (list < 0) {
    problems.push(where + ': the snapshot never writes the chat list (ChatCache.SaveAsync): the '
      + 'next start opens on an empty screen until a message brings a row of its own');
  }

  if (list >= 0 && tails >= 0 && list > tails) {
    problems.push(where + ': the chat list is written after the conversations\' tails. Each tail '
      + 'is a read and a write of its own file, one at a time: on the phone of 23 October 2026 '
      + '56 of them took the better part of two minutes against 169 rows, the app was closed in '
      + 'the middle and chats.json was never written - the list was empty at the next start '
      + '(57 messages_*.json, no chats.json). The list is what that screen draws; write it first');
  }

  // A run does not have to end with a suspension: it ended with a stop from
  // the debugger, and the rows the run had made went with the process.
  const live = methodBody(source, 'OnNetworkMessageReceived');
  if (!live) {
    problems.push(where + ': no OnNetworkMessageReceived - the path every live message takes is '
      + 'where the row it makes has to reach the phone');
  } else if (!/\bScheduleListSave\s*\(/.test(live)) {
    problems.push(where + ': OnNetworkMessageReceived does not ask for the chat list to be '
      + 'written (ScheduleListSave), so the row a message makes is on the phone only if the run '
      + 'ends with a suspension, a sync or a background check. On 24 October 2026 the app was '
      + 'stopped from the debugger with a row that had just arrived: no suspension, no snapshot, '
      + 'and the next start drew the list of the day before. A crash, a force close and a battery '
      + 'pulled out all end the same way');
  }

  // The list on disk is all a start draws, and WhatsApp names it again only at a link.
  const start = methodBody(source, 'LoadCachedChatsAsync');
  if (!start) {
    problems.push(where + ': no LoadCachedChatsAsync - the copy on the phone is what the first '
      + 'screen is drawn from');
  } else if (!/\bRestoreRowsFromTheirTailsAsync\s*\(/.test(start)) {
    problems.push(where + ': the cached list is loaded without asking the conversations\' tails '
      + 'for the rows it does not name (RestoreRowsFromTheirTailsAsync). chats.json is the whole '
      + 'of what the next start draws and WhatsApp names the list again only at a link: on '
      + '24 October 2026 it held 2 rows against 56 messages_*.json, so 54 conversations were on '
      + 'the phone and not on the list, with no way back on screen');
  }

  // A tail written back from a copy taken before the deletion it is racing. The
  // rows are snapshotted when SaveSnapshotAsync begins and the loop writes one
  // file at a time, so a chat deleted (or a message removed) mid-loop must be
  // skipped - AppendAsync merges by id and would put the deleted rows back.
  const appendAt = body.indexOf('MessageCache.AppendAsync');
  const loopAt = appendAt < 0 ? -1 : body.lastIndexOf('for (int i = 0; i < tails.Count', appendAt);
  if (appendAt >= 0 && loopAt >= 0) {
    const region = body.slice(loopAt, appendAt);
    const checksLive = /_chatMessages/.test(region) && /\bcontinue\s*;/.test(region);
    const checksReset = /_resettingAccount/.test(region);

    if (!checksLive) {
      problems.push(where + ': the tail loop writes every tail of the snapshot without asking '
        + 'whether the conversation still holds those rows (_chatMessages, skipped with continue). '
        + 'The rows were copied when the snapshot began and the loop writes one file at a time - '
        + 'minutes on the phone - so a chat deleted, or a message removed, while it runs is '
        + 'written back by the copy taken before the deletion: MessageCache.AppendAsync merges by '
        + 'id and the deleted conversation returns at the next open-and-close');
    }

    if (!checksReset) {
      problems.push(where + ': the tail loop does not stop when the account is being replaced '
        + '(_resettingAccount). The pairing wipes the tails of the previous identity '
        + '(MessageCache.DeleteAllAsync) while a snapshot may still be writing them, and a tail '
        + 'written after the wipe is the old account\'s conversation on the new account\'s phone');
    }
  }

  if (typeof cache === 'string') {
    const cacheSource = stripComments(cache);

    for (const name of ['DeleteAsync', 'DeleteAllAsync']) {
      const del = methodBody(cacheSource, name);
      if (!del) {
        problems.push('WhatsappApp/Services/MessageCache.cs: no ' + name
          + ' - a deleted conversation loses its copy on the phone there');
        continue;
      }

      if (!/Writes\.RunAsync/.test(del)) {
        problems.push('WhatsappApp/Services/MessageCache.cs: ' + name + ' does not run through the '
          + 'write queue (Writes.RunAsync). Every write of a tail is queued as one '
          + 'load-merge-write step; a delete that runs beside a step still in flight is undone by '
          + 'it, and the conversation the reader just deleted is back on the phone');
      }
    }
  }

  return problems;
}

function main() {
  const problems = problemsFor(
    fs.readFileSync(SERVICE, 'utf8'),
    fs.readFileSync(CACHE, 'utf8'));

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} snapshot problem(s).`);
    process.exit(1);
  }
  console.log('OK: the chat list is written before the conversations\' tails.');
}

if (require.main === module) main();

module.exports = { problemsFor, methodBody, stripComments };
