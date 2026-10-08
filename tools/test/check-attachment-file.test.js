'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const { attachmentFileProblems } = require('../check-attachment-file');

const ROOT = path.join(__dirname, '..', '..');
const INBOX = 'WhatsappApp/Services/AttachmentInbox.cs';
const CHAT = 'WhatsappApp/Pages/ChatPage.xaml.cs';
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

test('la copia di un allegato ha un nome suo e non uno condiviso', () => {
  assert.deepStrictEqual(attachmentFileProblems(read(INBOX), INBOX), []);
});

test('un nome condiviso e un problema, non un silenzio', () => {
  const broken = read(INBOX).replace(/"outgoing_attachment_"/, '"outgoing_attachment"');
  const found = attachmentFileProblems(broken, INBOX).join('\n');
  assert.match(found, /outgoing_attachment/);
});

test('il lettore carica il file della bolla che ha chiesto', () => {
  assert.deepStrictEqual(attachmentFileProblems(read(CHAT), CHAT), []);
});

test('un lettore che si fida del solo nome e un problema', () => {
  const broken = read(CHAT)
    .replace(/bool\s+anotherMessage\s*=\s*_voiceMessage\s*!=\s*message;/, 'bool anotherMessage = false;');
  const found = attachmentFileProblems(broken, CHAT).join('\n');
  assert.match(found, /anotherMessage/);
});

test('un Downloadable che rifiuta ogni messaggio in uscita e un problema', () => {
  const broken = read(CHAT)
    .replace(/!\s*message\.IsIncoming\s*&&\s*!\s*message\.IsHistory/, '!message.IsIncoming');
  const found = attachmentFileProblems(broken, CHAT).join('\n');
  assert.match(found, /IsHistory/);
});
