'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const data = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/DataService.cs'), 'utf8');

test('a conversation is on the phone before its rows are dropped from memory', () => {
  // A row that only memory holds is a bug the tail caches exist to prevent: the
  // trim runs while a save is still inside its quiet window (or after a snapshot
  // that counted the chat out) and the rows it clears never reached the file.
  // The tail is written first - the merge keeps what is already there and adds
  // what is not - and only then is the collection emptied.
  const trim = data.slice(data.indexOf('public void TrimForMemory()'),
    data.indexOf('public void TrimForMemory()') + 1500);
  const cleared = trim.indexOf('pair.Value.Clear()');
  const persisted = trim.indexOf('MessageCache.AppendAsync');
  assert.ok(cleared > 0, 'the trim empties the collections');
  assert.ok(persisted >= 0 && persisted < cleared,
    'the rows are written to the tail before the collection is cleared');
  assert.match(trim, /Guarded\.RunGuardedAsync/,
    'the unowned write is guarded like every fire-and-forget task');
});
