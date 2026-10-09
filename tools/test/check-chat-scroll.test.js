'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const { chatScrollProblems } = require('../check-chat-scroll');

const ROOT = path.join(__dirname, '..', '..');
const PAGE = 'WhatsappApp/Pages/ChatPage.xaml.cs';
const XAML = 'WhatsappApp/Pages/ChatPage.xaml';
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
const files = () => ({ page: read(PAGE), xaml: read(XAML) });

test('il salto all ultimo messaggio e la freccia sono a posto', () => {
  assert.deepStrictEqual(chatScrollProblems(files()), []);
});

test('una pagina che non segue il burst non arriva ai messaggi nuovi', () => {
  const broken = files();
  broken.page = broken.page
    .replace(/DataService\.Instance\.HistoryCompleted \+= OnHistoryCompleted;\r?\n/, '');
  assert.match(chatScrollProblems(broken).join('\n'), /HistoryCompleted/);
});

test('un burst senza la chat in primo piano non deve muovere un altra lista', () => {
  const broken = files();
  broken.page = broken.page
    .replace(/if \(DataService\.Instance\.ActiveChatId != _contact\.Id\) return;\r?\n/,
      '');
  assert.match(chatScrollProblems(broken).join('\n'), /ActiveChatId/);
});

test('una freccia senza il suo gestore non riporta ai messaggi recenti', () => {
  const broken = files();
  broken.xaml = broken.xaml
    .replace(/Click="RecentMessagesButton_Click"/, 'Click="RecentMessagesButtonClick"');
  assert.match(chatScrollProblems(broken).join('\n'), /RecentMessagesButton_Click/);
});

test('una pagina che decide da sola il fondo e un problema', () => {
  const broken = files();
  broken.page = broken.page + '\r\n// ScrollableHeight\r\n';
  assert.match(chatScrollProblems(broken).join('\n'), /ScrollableHeight/);
});
