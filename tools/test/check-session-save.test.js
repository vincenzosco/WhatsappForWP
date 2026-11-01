'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const { sessionSaveProblems } = require('../check-session-save');

const ROOT = path.join(__dirname, '..', '..');
const CONNECTION = 'WhatsappApp/Services/NativeConnection.cs';
// The guard reads lines, and the file on disk is CRLF: the same normalisation it does.
const read = () => fs.readFileSync(path.join(ROOT, CONNECTION), 'utf8').replace(/\r\n/g, '\n');

test('nessun problema nel file vero', () => {
  assert.deepStrictEqual(sessionSaveProblems({ connection: read() }), []);
});

test('un salvataggio fuori dal finally e un problema', () => {
  // The call stays where it is and the block around it stops being a `finally`: the store
  // is still written, but only on the path that gets there.
  const broken = {
    connection: read().replace(
      '            finally\n            {\n                // The chains moved while that stanza was being built',
      '            if (privacyToken != null)\n            {\n                // The chains moved while that stanza was being built'
    )
  };

  assert.match(sessionSaveProblems(broken).join('\n'), /SendTextAsync .*outside a finally/);
});

test('un percorso che non salva affatto e un problema', () => {
  const text = read();
  const at = text.indexOf('public async Task<string> SendMediaAsync(string jid, byte[] file');
  const end = text.indexOf('\n        }', at);

  const broken = {
    connection: text.slice(0, at) + text.slice(at, end).replace(/KeepSessions\([^;]*;\n/g, '') + text.slice(end)
  };

  assert.match(sessionSaveProblems(broken).join('\n'), /SendMediaAsync never writes its sessions back/);
});
