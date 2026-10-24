#!/usr/bin/env node
/**
 * tools/check-chat-list-refresh.js
 *
 * Guard for the chat list's own maintenance: one spelling per chat, the rows re-read while
 * the app is open, and the group subjects the server was never asked for.
 *
 * Why it exists: four things a person sees on the screen and no test can reach, all of them
 * in the app and none of them a compile error.
 *
 *  1. **The refresh.** A stanza off the socket was the only writer the list had. A name
 *     learned after its row (`NameGroupsAsync`), a `@lid` pair learned after the row it
 *     would fold, a row the server has and this list never heard of - each stayed wrong
 *     until the app was restarted. `HomePage` now re-reads the list every 30 s
 *     (`DataService.ListRefreshSeconds`) while the chat tab is in front, and stops when it
 *     leaves. The refresh is **local**: it walks rows already in memory and asks WhatsApp
 *     nothing, which is what makes it safe on a phone with no data.
 *  2. **One spelling per chat.** A message that arrives under a `@lid` (or with a device)
 *     belongs to the row filed under the number. Every path that keys a chat - `ApplyChat`,
 *     `AddMessage`, `GetMessages`, `FindContact` - goes through `DataService.ChatIdFor`
 *     (`ChatListMerge.CanonicalId`), or the message creates a second row: the old one keeps
 *     yesterday's preview and the new one is the conversation nobody opens.
 *  3. **A sent message lands where it is read.** `ChatPage` sends on
 *     `DataService.Instance.ChatIdFor(_contact.Id)`: a page opened from a row spelled the
 *     other way wrote its own messages into a conversation nothing showed, which is the
 *     group that displayed only what the other members wrote.
 *  4. **The groups' names.** `NameTheGroups` ran only at the end of a history sync and marked
 *     a group as asked *before* the query, so a device linked for days never asked and a
 *     failed query was never retried: every group stayed `Gruppo 1203...`. It now runs after
 *     the login too, and a group is marked asked only when the server answered.
 *
 * Usage:
 *   node tools/check-chat-list-refresh.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const DATA = 'WhatsappApp/Services/DataService.cs';
const MERGE = 'WhatsappApp/Services/ChatListMerge.cs';
const HOME = 'WhatsappApp/Pages/HomePage.xaml.cs';
const CHAT = 'WhatsappApp/Pages/ChatPage.xaml.cs';
const CONNECTION = 'WhatsappApp/Services/NativeConnection.cs';

const REFRESH_CALL = 'DataService.Instance.RefreshChatList();';
const TIMER_INTERVAL = 'TimeSpan.FromSeconds(ListRefreshSeconds)';

/** Il corpo di un metodo, dalla firma alla graffa che lo chiude (le stringhe saltate). */
function methodBody(text, signature) {
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
 * I problemi dei cinque file. Un array vuoto quando la lista si mantiene da sola e una chat
 * ha una sola forma.
 */
function chatListRefreshProblems(files) {
  const problems = [];
  const data = String((files && files.data) || '');
  const merge = String((files && files.merge) || '');
  const home = String((files && files.home) || '');
  const chat = String((files && files.chat) || '');
  const connection = String((files && files.connection) || '');

  // 1. The refresh, and its ceiling.
  const seconds = /public const int ListRefreshSeconds = (\d+);/.exec(data);
  if (!seconds) {
    problems.push(`${DATA}: no ListRefreshSeconds: the list has no refresh interval to run at`);
  } else if (Number(seconds[1]) > 30 || Number(seconds[1]) < 5) {
    problems.push(`${DATA}: ListRefreshSeconds is ${seconds[1]} s: it must be at most 30 (the ` +
      'interval asked for) and long enough to be a net rather than a second loop');
  }

  if (!home.includes(TIMER_INTERVAL)) {
    problems.push(`${HOME}: the refresh timer does not use TimeSpan.FromSeconds(ListRefreshSeconds): ` +
      'the interval the app runs at has to be the one the guard reads');
  }
  if (!home.includes(REFRESH_CALL)) {
    problems.push(`${HOME}: nothing calls ${REFRESH_CALL}: the timer ticks and the list is never re-read`);
  }

  const activate = methodBody(home, 'private void ActivateChats()');
  if (activate == null || !activate.includes('StartListRefresh();')) {
    problems.push(`${HOME}: ActivateChats does not start the refresh (StartListRefresh();): ` +
      'the timer is built and never started, and the list goes stale exactly as before');
  }
  const deactivate = methodBody(home, 'private void DeactivateChats()');
  if (deactivate == null || !deactivate.includes('StopListRefresh();')) {
    problems.push(`${HOME}: DeactivateChats does not stop the refresh (StopListRefresh();): ` +
      'a timer left running walks the list on a tab nobody is looking at');
  }

  const refresh = methodBody(data, 'public void RefreshChatList()');
  if (refresh == null) {
    problems.push(`${DATA}: RefreshChatList is missing: the timer has nothing to call`);
  } else {
    if (!refresh.includes('NativeConnection.Instance.ChatRows')) {
      problems.push(`${DATA}: RefreshChatList does not read the client's rows ` +
        '(NativeConnection.Instance.ChatRows): it has nothing to re-apply');
    }
    if (!refresh.includes('FoldDuplicateChats()')) {
      problems.push(`${DATA}: RefreshChatList does not fold the rows that are one chat ` +
        '(FoldDuplicateChats()): a LID row and its number would stay side by side for ever');
    }
    // Local, and nothing else: a refresh that queries the server every 30 s is a poll.
    const network = ['SendAsync(', 'RequestAsync(', 'DownloadMediaAsync', 'SendTextAsync', 'AppStateSync.Query'];
    for (const call of network) {
      if (refresh.includes(call)) {
        problems.push(`${DATA}: RefreshChatList calls ${call}: the refresh must be local - it ` +
          'walks rows already in memory - or every 30 s is a question to WhatsApp');
      }
    }
  }

  // 2. One spelling per chat, on every path that keys one.
  const lookups = count(data, 'ChatIdFor(');
  if (lookups < 5) {
    problems.push(`${DATA}: ChatIdFor is used ${lookups} time(s): ApplyChat, AddMessage, GetMessages, ` +
      'FindContact and the refresh all key a chat on its id, and a path that skips it creates the ' +
      'second row this whole refresh exists to fold');
  }
  const applyChat = methodBody(data, 'private void ApplyChat(ChatMessage message, bool remember)');
  if (applyChat == null || !applyChat.includes('message.ChatId = ChatIdFor(message.ChatId);')) {
    problems.push(`${DATA}: ApplyChat does not canonicalise the id it is keying ` +
      '(message.ChatId = ChatIdFor(message.ChatId);): a row arriving under the other spelling ' +
      'becomes a second chat');
  }

  if (!merge.includes('public static string CanonicalId(')) {
    problems.push(`${MERGE}: CanonicalId is gone: the rule that decides which ids are one chat ` +
      'lives there (and is tested in ProtocolTests)');
  }

  // 3. A message this phone sends goes into the conversation it is read in.
  const sent = count(chat, 'ChatIdFor(');
  if (sent < 3) {
    problems.push(`${CHAT}: ChatIdFor is used ${sent} time(s) and must be used for the active chat ` +
      'and for both outgoing messages (text and attachment): a message written from a row spelled ' +
      'the other way is saved where nothing reads it - the group that showed only what the others wrote');
  }

  // 4. The groups' subjects.
  const nameCalls = count(connection, 'NameTheGroups();');
  if (nameCalls < 2) {
    problems.push(`${CONNECTION}: NameTheGroups(); appears ${nameCalls} time(s) and must appear at ` +
      'least twice - after the login and at the end of a sync: with only the sync, a device linked ' +
      'for days has no sync left to wait for and keeps "Gruppo 1203..." for ever');
  }

  const namer = methodBody(connection, 'private void NameTheGroups()');
  if (namer == null) {
    problems.push(`${CONNECTION}: NameTheGroups is missing: nothing asks the server for a subject`);
  } else if (namer.includes('IsSyncComplete')) {
    problems.push(`${CONNECTION}: NameTheGroups still gates on _chats.IsSyncComplete: that gate is ` +
      'why the names were never asked for on a phone whose sync had finished in an earlier run');
  }

  const pass = methodBody(connection, 'private async Task NameGroupsAsync(NativeSession session)');
  if (pass == null) {
    problems.push(`${CONNECTION}: NameGroupsAsync is missing: the pass that asks for the subjects`);
  } else {
    const asked = pass.indexOf('MarkGroupAsked(');
    const query = pass.indexOf('await RequestAsync(');
    if (asked < 0) {
      problems.push(`${CONNECTION}: NameGroupsAsync never marks a group asked (MarkGroupAsked): ` +
        'every pass would ask the same fifteen for ever');
    } else if (query < 0 || asked < query) {
      problems.push(`${CONNECTION}: a group is marked asked before the server answered: a query ` +
        'that failed (no answer, a timeout) would then never be retried, and that row keeps its ' +
        'fallback for the life of the run');
    }

    if (count(connection, '_groupsAsked.Add(') !== 1) {
      problems.push(`${CONNECTION}: _groupsAsked is written in more than one place: the mark belongs ` +
        'only in MarkGroupAsked, after the answer');
    }
  }

  return problems;
}

function main() {
  const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

  const problems = chatListRefreshProblems({
    data: read(DATA),
    merge: read(MERGE),
    home: read(HOME),
    chat: read(CHAT),
    connection: read(CONNECTION)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} chat-list problem(s).`);
    process.exit(1);
  }

  console.log('OK: the list is re-read every 30 s at most, one chat has one spelling on every path, ' +
    'a sent message lands in the conversation it is read in, and the groups are asked for their ' +
    'subject after the login.');
}

if (require.main === module) main();

module.exports = {
  chatListRefreshProblems,
  methodBody,
  DATA,
  MERGE,
  HOME,
  CHAT,
  CONNECTION,
  REFRESH_CALL,
  TIMER_INTERVAL
};
