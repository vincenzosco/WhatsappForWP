'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (file) => fs.readFileSync(path.join(ROOT, file), 'utf8');
const inbox = read('WhatsappApp/Services/AttachmentInbox.cs');
const store = read('WhatsappApp/Services/IncomingMediaStore.cs');
const microphone = read('WhatsappApp/Services/CallMicrophone.cs');

test('il tipo MIME e il tipo di media non seguono la cultura del telefono', () => {
  for (const [name, source] of [['AttachmentInbox', inbox], ['IncomingMediaStore', store]]) {
    assert.doesNotMatch(source, /\.ToLower\(\)/,
      `${name}: un ToLower() dipendente dalla cultura trasforma "IMAGE" in "ımage" in turco`);
    assert.match(source, /\.ToLowerInvariant\(\)/,
      `${name}: il confronto dei prefissi usa la cultura invariante`);
  }

  const kind = inbox.slice(inbox.indexOf('public static string KindName'));
  assert.match(kind, /mimeType \?\? ""\)\.ToLowerInvariant\(\)/);
  assert.match(kind, /fileName \?\? ""\)\.ToLowerInvariant\(\)/);
});

test('il microfono chiude il lettore anche quando la lettura fallisce', () => {
  const start = microphone.indexOf('var reader = new DataReader(');
  assert.ok(start > 0, 'il pump apre un DataReader');
  const pump = microphone.slice(start, start + 1200);

  assert.match(pump, /try\s*\{[\s\S]*?finally\s*\{[\s\S]*?reader\.Dispose\(\)/,
    'il Dispose sta in un finally, o un LoadAsync che lancia lascia un lettore per tentativo');
});

test('una durata che il telefono non legge resta nel diario', () => {
  const start = inbox.indexOf('private static async Task<uint> SecondsOfAsync(');
  assert.ok(start > 0, 'SecondsOfAsync esiste');
  const seconds = inbox.slice(start, start + 900);

  assert.match(seconds, /catch \(Exception ex\)/);
  assert.match(seconds, /Diag\.Failed\("AttachmentInbox\/seconds", ex\)/,
    'un catch silenzioso e un bug: la durata sconosciuta si nomina');
});
