'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');

test('il file del registro chiamate e scritto accanto a se e poi spostato', () => {
  const source = fs.readFileSync(
    path.join(ROOT, 'WhatsappApp', 'Services', 'CallLogStore.cs'), 'utf8');

  const at = source.indexOf('public async Task SaveAsync()');
  assert.ok(at > 0, 'CallLogStore.SaveAsync esiste');
  const body = source.slice(at, source.indexOf('\n        }', at));

  assert.match(body, /CreateFileAsync\(\s*TempName/,
    'il file e scritto sotto il nome temporaneo (TempName), non sotto quello vero');
  assert.match(body, /MoveAndReplaceAsync\(|RenameAsync\(/,
    'il passo finale e uno spostamento: un write interrotto lascia un file che non si '
    + 'analizza, e il registro delle chiamate va perso');
  assert.doesNotMatch(body, /WriteTextAsync\(target/,
    'scrivere direttamente nel file vero e il write che una sospensione tronca');
});
