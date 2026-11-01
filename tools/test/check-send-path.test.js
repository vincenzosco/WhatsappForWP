'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const { sendPathProblems } = require('../check-send-path');

const ROOT = path.join(__dirname, '..', '..');
const PAGE = 'WhatsappApp/Pages/ChatPage.xaml.cs';
const read = () => fs.readFileSync(path.join(ROOT, PAGE), 'utf8');

test('il percorso di invio vero non ha uscite mute', () => {
  assert.deepStrictEqual(sendPathProblems(read()), []);
});

test('un return muto nel percorso del testo e un problema', () => {
  const broken = read().replace(
    '            // Text-only message.',
    '            if (text == null) return;\n\n            // Text-only message.');

  assert.match(sendPathProblems(broken).join('\n'), /no DIAG line/);
});

test('un return muto nel percorso degli allegati e un problema', () => {
  const broken = read().replace(
    '            string fileName = _selectedMediaFileName;',
    '            if (fileName == null) return;\n            string fileName = _selectedMediaFileName;');

  assert.match(sendPathProblems(broken).join('\n'), /SendAttachmentAsync/);
});

test('un metodo del percorso che sparisce non passa per buono', () => {
  const broken = read().replace('private async void SendMessage()', 'private async void SendMessageX()');

  assert.match(sendPathProblems(broken).join('\n'), /no SendMessage\b/);
});
