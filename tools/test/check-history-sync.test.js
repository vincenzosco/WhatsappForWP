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
