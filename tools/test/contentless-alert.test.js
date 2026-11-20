'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const native = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Services', 'NativeConnection.cs'), 'utf8');
const data = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Services', 'DataService.cs'), 'utf8');

function slice(text, startMarker, size) {
  const at = text.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return text.slice(at, at + size);
}

test('a message this build cannot draw still raises an alert', () => {
  // A sticker, a poll or a contact card has no bubble here: the row takes the
  // time and the place and says nothing else. But "nothing else" was also
  // silence - no unread, no toast, nothing on the tile. The conversation
  // carries the news even when the bubble cannot.
  const note = slice(native, 'private void NoteChatOf(IncomingMessage incoming)', 1600);
  assert.match(note, /AlertArrived/, 'the alert goes out with the row');
  assert.match(note, /IsOurEcho/, 'and only for what somebody else wrote');
  assert.match(note, /UnreadCount/, 'the row counts it as unread');
});

test('the alert is an event the data layer hears', () => {
  assert.match(native, /event .*AlertArrived/, 'the event is declared');
  assert.match(data, /AlertArrived \+= /, 'and subscribed where alerts are raised');
});

test('the alert follows the same rules as a message alert', () => {
  const handler = slice(data, 'private void OnNativeAlert', 1600);
  assert.match(handler, /NotificationService\.ShowMessage/, 'it notifies');
  assert.match(handler, /NotificationService\.RememberSender/, 'and faces the tile');
  assert.match(handler, /ChatPreferences\.IsMuted/, 'a muted chat stays muted');
  assert.match(handler, /_activeChatId/, 'and the open chat is already on screen');
});