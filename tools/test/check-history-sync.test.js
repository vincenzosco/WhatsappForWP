'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const { historySyncProblems } = require('../check-history-sync');

const ROOT = path.join(__dirname, '..', '..');
const CONNECTION = 'WhatsappApp/Services/NativeConnection.cs';
const DATA_SERVICE = 'WhatsappApp/Services/DataService.cs';
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
const files = () => ({ connection: read(CONNECTION), dataService: read(DATA_SERVICE) });

test('i messaggi del sync arrivano all app, e solo da questo account', () => {
  assert.deepStrictEqual(historySyncProblems(files()), []);
});

test('un sync che resta nelle righe lascia le chat vuote', () => {
  const broken = files();
  broken.connection = broken.connection
    .replace(/MessageArrived\(this, fromTheSync\[i\]\);\r?\n/, '');
  assert.match(historySyncProblems(broken).join('\n'), /MessageArrived/);
});

test('un sync che non dice alla lista delle righe rinominate non rinomina nessuno', () => {
  const broken = files();
  broken.connection = broken.connection
    .replace(/ChatArrived\(this, _chats\.Rows\[i\]\)/, 'ChatArrived(this, sync.Conversations[i].Row)');
  assert.match(historySyncProblems(broken).join('\n'), /push-name sync/);
});

test('una riga di cronologia senza il flag conta come nuova', () => {
  const broken = files();
  broken.dataService = broken.dataService
    .replace(/message\.IsHistory = incoming\.IsHistory;\r?\n/, '');
  assert.match(historySyncProblems(broken).join('\n'), /IsHistory/);
});

test('un sync da chiunque altro non deve scrivere la lista di questo telefono', () => {
  const broken = files();
  broken.connection = broken.connection
    .replace(/if \(!IncomingMessage\.IsMine\(incoming\.From, account\)\)/, 'if (false)');
  assert.match(historySyncProblems(broken).join('\n'), /IsMine/);
});

test('un ack prima del sync butta via la storia una volta sola', () => {
  const broken = files();

  // The shape the phone of 2026-10-23 ran: the ack at the top of the stanza, before
  // anything looks at what the stanza carries.
  const check = '            HistorySyncNotification history = incoming == null ? null : ' +
    'HistorySyncNotification.From(incoming.Content);';

  assert.ok(broken.connection.includes(check), 'the line the guard reads is still there');
  broken.connection = broken.connection.replace(check,
    '            await session.SendAsync(IncomingMessage.Ack(node));\n' + check);

  assert.match(historySyncProblems(broken).join('\n'), /before the history sync is looked at/);
});

test('un sync che non atterra si richiede invece di essere ackato', () => {
  const broken = files();
  broken.connection = broken.connection
    .replace(/if \(await ApplyHistoryAsync\(history\)\) await session\.SendAsync\(IncomingMessage\.Ack\(node\)\);\r?\n                else await AskAgainAsync\(session, node, account\);/,
      'await ApplyHistoryAsync(history);\n                await session.SendAsync(IncomingMessage.Ack(node));');

  assert.match(historySyncProblems(broken).join('\n'), /ApplyHistoryAsync/);
  assert.match(historySyncProblems(broken).join('\n'), /AskAgainAsync/);
});
