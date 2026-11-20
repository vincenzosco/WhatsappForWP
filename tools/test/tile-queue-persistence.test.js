'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const service = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Services', 'NotificationService.cs'), 'utf8');

function slice(startMarker, size) {
  const at = service.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return service.slice(at, at + size);
}

test('the tile queue is restored when the process starts again', () => {
  // The queue of waiting conversations is what the tile draws. It lived only
  // in memory: every quiet background run and every app start redrew the tile
  // from an empty queue and wiped the face and the count the last run left.
  // The queue is written to the phone and read back, one entry per slot.
  const restore = slice('private static void RestoreLastMessage()', 1800);
  assert.match(restore, /KeyWaiting/, 'the queue slots are read');
  assert.match(restore, /_waiting = TileCycle\.Remember\(/,
    'and the waiting list is rebuilt from them');
  assert.match(restore, /entry\.ChatId/, 'the chat identity travels with the entry');
});

test('the queue is written when a message is remembered', () => {
  const remember = slice('private static async Task RememberSenderAsync', 1400);
  assert.match(remember, /RememberWaiting\(\)/, 'the queue is persisted per message');
  const entry = remember.indexOf('_waiting = TileCycle.Remember(_waiting, entry)');
  const persist = remember.indexOf('RememberWaiting()');
  assert.ok(entry !== -1 && persist > entry, 'after the entry is in the queue');
});

test('nothing persists a name nobody reads', () => {
  // The old state was three write-only fields: twelve occurrences of
  // assignments and restores, no reader anywhere. The queue is the state.
  assert.doesNotMatch(service, /private static string _senderName/);
  assert.doesNotMatch(service, /private static string _senderBody/);
  assert.doesNotMatch(service, /private static string _senderImage/);
});

test('forgetting clears what was persisted too', () => {
  // A queue dropped in memory but left on the phone came back with the next
  // restore - a face the reader had already read.
  const all = slice('public static void ForgetAll()', 400);
  assert.match(all, /RememberWaiting\(\)/, 'the persisted slots follow the memory');
  const reset = slice('public static void ResetForNewAccount()', 900);
  assert.match(reset, /RememberWaiting\(\)/, 'and so does the account reset');
});