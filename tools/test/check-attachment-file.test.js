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

test('ogni estensione che KindName sa classificare ha il suo MIME', () => {
  // Il file vero: se questa riga si rompe, un tipo di file esce con
  // application/octet-stream e il telefono che lo riceve non lo disegna.
  assert.deepStrictEqual(attachmentFileProblems(read(INBOX), INBOX), []);
});

test('una foto .jpg senza il suo MIME e un problema', () => {
  const broken = read(INBOX).replace(/if \(value == "\.jpg"\) return "image\/jpeg";/, '');
  const found = attachmentFileProblems(broken, INBOX).join('\n');
  assert.match(found, /\.jpg/);
});

test('un MIME di un altra famiglia e un problema', () => {
  const broken = read(INBOX).replace(/(if \(value == "\.mp4"\) return )"video\/mp4"/, '$1"image/jpeg"');
  const found = attachmentFileProblems(broken, INBOX).join('\n');
  assert.match(found, /\.mp4/);
});

test('un Downloadable che rifiuta ogni messaggio in uscita e un problema', () => {
  const broken = read(CHAT)
    .replace(/!\s*message\.IsIncoming\s*&&\s*!\s*message\.IsHistory/, '!message.IsIncoming');
  const found = attachmentFileProblems(broken, CHAT).join('\n');
  assert.match(found, /IsHistory/);
});
