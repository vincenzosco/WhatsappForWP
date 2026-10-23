'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const snapshot = require('../check-chat-snapshot');

const ROOT = path.resolve(__dirname, '..', '..');

const METHOD = (order) => {
  const tails = '            for (int i = 0; i < tails.Count; i++)\n'
    + '            {\n'
    + '                await MessageCache.AppendAsync(tails[i].Key, tails[i].Value);\n'
    + '            }\n';
  const list = '            await ChatCache.SaveAsync(rows, HistoryComplete);\n';

  return 'public async Task SaveSnapshotAsync()\n'
    + '        {\n'
    + '            var rows = new List<ChatMessage>();\n'
    + (order === 'list-first' ? list + '\n' + tails : tails + '\n' + list)
    + '        }\n';
};

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
    + '        }\n');
  assert.ok(problems.some((p) => /never writes the chat list/.test(p)));
});

test('il servizio vero scrive la lista prima dei tail', () => {
  const source = fs.readFileSync(
    path.join(ROOT, 'WhatsappApp', 'Services', 'DataService.cs'), 'utf8');
  assert.deepStrictEqual(snapshot.problemsFor(source), []);
});
