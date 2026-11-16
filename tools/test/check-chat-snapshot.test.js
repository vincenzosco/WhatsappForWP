'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const snapshot = require('../check-chat-snapshot');

const ROOT = path.resolve(__dirname, '..', '..');

// The message path and the start: two of the rules live there, and every fixture
// needs them because the guard always looks for them.
const LIVE = (call) => 'private async void OnNetworkMessageReceived(object sender, ChatMessage message)\n'
  + '        {\n'
  + (call ? '            ScheduleListSave();\n' : '')
  + '        }\n';

const START = (call) => 'private async System.Threading.Tasks.Task LoadCachedChatsAsync()\n'
  + '        {\n'
  + (call ? '            await RestoreRowsFromTheirTailsAsync();\n' : '')
  + '        }\n';

// The tail loop the guard wants: the rows are checked against the live
// conversation (and the loop stops when the account is being replaced) before a
// tail is appended.
const TAILS_GUARDED = '            for (int i = 0; i < tails.Count; i++)\n'
  + '            {\n'
  + '                if (_resettingAccount) break;\n'
  + '                ObservableCollection<ChatMessage> live;\n'
  + '                if (!_chatMessages.TryGetValue(tails[i].Key, out live)) continue;\n'
  + '                if (live.Count != tails[i].Value.Length) continue;\n'
  + '                await MessageCache.AppendAsync(tails[i].Key, tails[i].Value);\n'
  + '            }\n';

const TAILS_BARE = '            for (int i = 0; i < tails.Count; i++)\n'
  + '            {\n'
  + '                if (_resettingAccount) break;\n'
  + '                await MessageCache.AppendAsync(tails[i].Key, tails[i].Value);\n'
  + '            }\n';

const TAILS_NO_RESET = '            for (int i = 0; i < tails.Count; i++)\n'
  + '            {\n'
  + '                ObservableCollection<ChatMessage> live;\n'
  + '                if (!_chatMessages.TryGetValue(tails[i].Key, out live)) continue;\n'
  + '                if (live.Count != tails[i].Value.Length) continue;\n'
  + '                await MessageCache.AppendAsync(tails[i].Key, tails[i].Value);\n'
  + '            }\n';

const METHOD = (order, live = true, start = true, tails = TAILS_GUARDED) => {
  const list = '            await ChatCache.SaveAsync(rows, HistoryComplete);\n';

  return 'public async Task SaveSnapshotAsync()\n'
    + '        {\n'
    + '            var rows = new List<ChatMessage>();\n'
    + (order === 'list-first' ? list + '\n' + tails : tails + '\n' + list)
    + '        }\n'
    + LIVE(live)
    + START(start);
};

// The delete methods of the tail cache: one fixture with the deletes inside the
// write queue and one with them running beside it.
const CACHE_QUEUED = 'public static async Task DeleteAsync(string chatId)\n'
  + '        {\n'
  + '            await Writes.RunAsync(delegate { return DeleteCoreAsync(chatId); });\n'
  + '        }\n'
  + '\n'
  + '        public static async Task DeleteAllAsync()\n'
  + '        {\n'
  + '            await Writes.RunAsync(delegate { return DeleteAllCoreAsync(); });\n'
  + '        }\n';

const CACHE_INLINE = 'public static async Task DeleteAsync(string chatId)\n'
  + '        {\n'
  + '            IStorageItem item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(chatId);\n'
  + '            if (item != null) await item.DeleteAsync(StorageDeleteOption.PermanentDelete);\n'
  + '        }\n'
  + '\n'
  + '        public static async Task DeleteAllAsync()\n'
  + '        {\n'
  + '            IReadOnlyList<StorageFile> files = await ApplicationData.Current.LocalFolder.GetFilesAsync();\n'
  + '            for (int i = 0; i < files.Count; i++) await files[i].DeleteAsync();\n'
  + '        }\n';

test('la lista scritta dopo i tail si segnala, ed e il difetto del 23 ottobre', () => {
  const problems = snapshot.problemsFor(METHOD('tails-first'));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /after the conversations/);
  assert.match(problems[0], /chats\.json was never written/);
});

test('la lista scritta per prima non si segnala', () => {
  assert.deepStrictEqual(snapshot.problemsFor(METHOD('list-first')), []);
});

test('un salvataggio che non scrive la lista si segnala', () => {
  const problems = snapshot.problemsFor(
    'public async Task SaveSnapshotAsync()\n        {\n'
    + '            await MessageCache.AppendAsync(chat, tail);\n'
    + '        }\n'
    + LIVE(true)
    + START(true));
  assert.ok(problems.some((p) => /never writes the chat list/.test(p)));
});

test('un percorso dei messaggi che non chiede di scrivere la lista si segnala', () => {
  const problems = snapshot.problemsFor(METHOD('list-first', false));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /ScheduleListSave/);
  assert.match(problems[0], /stopped from the debugger/);
});

test('un avvio che non chiede le righe alle code si segnala', () => {
  const problems = snapshot.problemsFor(METHOD('list-first', true, false));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /RestoreRowsFromTheirTailsAsync/);
  assert.match(problems[0], /2 rows against 56 messages_\*\.json/);
});

test('un tail scritto senza controllare la conversazione si segnala', () => {
  const problems = snapshot.problemsFor(METHOD('list-first', true, true, TAILS_BARE));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /_chatMessages/);
  assert.match(problems[0], /returns at the next open-and-close/);
});

test('un ciclo che non si ferma al cambio di account si segnala', () => {
  const problems = snapshot.problemsFor(METHOD('list-first', true, true, TAILS_NO_RESET));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /_resettingAccount/);
  assert.match(problems[0], /old account/);
});

test('una cancellazione fuori dalla coda di scrittura si segnala', () => {
  const problems = snapshot.problemsFor(METHOD('list-first'), CACHE_INLINE);
  assert.strictEqual(problems.length, 2);
  assert.match(problems[0], /DeleteAsync/);
  assert.match(problems[0], /Writes\.RunAsync/);
  assert.match(problems[1], /DeleteAllAsync/);
  assert.match(problems[1], /Writes\.RunAsync/);
});

test('le cancellazioni dentro la coda non si segnalano', () => {
  assert.deepStrictEqual(snapshot.problemsFor(METHOD('list-first'), CACHE_QUEUED), []);
});

test('il servizio vero e la cache vera passano', () => {
  const source = fs.readFileSync(
    path.join(ROOT, 'WhatsappApp', 'Services', 'DataService.cs'), 'utf8');
  const cache = fs.readFileSync(
    path.join(ROOT, 'WhatsappApp', 'Services', 'MessageCache.cs'), 'utf8');
  assert.deepStrictEqual(snapshot.problemsFor(source, cache), []);
});
