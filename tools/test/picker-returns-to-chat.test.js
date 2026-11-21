'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

function slice(text, startMarker, size) {
  const at = text.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return text.slice(at, at + size);
}

const app = read('WhatsappApp/App.xaml.cs');
const chat = read('WhatsappApp/Pages/ChatPage.xaml.cs');
const session = read('WhatsappApp/Services/SessionService.cs');

test('the chat that opened the picker is remembered where it survives the phone', () => {
  // A FileOpenPicker continuation can bring the app back from termination: a
  // static field comes back empty. The return rides in LocalSettings, the same
  // store the badge route uses.
  assert.match(session, /SetPickerReturn\(/, 'the chat can be stored');
  assert.match(session, /TakePickerReturn\(/, 'and taken once');
  const store = slice(session, 'public static void SetPickerReturn(', 700);
  assert.match(store, /Settings\.Values\[KeyPickerChat\]/, 'the store is the persisted one');
});

test('every picker that leaves the chat remembers it first', () => {
  const filtered = slice(chat, 'private void RequestFilteredFiles(', 700);
  assert.match(filtered, /RememberChatForPicker\(\)/,
    'the filtered picker remembers the chat');
  const albums = slice(chat, 'private void AttachmentAlbumsButton_Click(', 700);
  assert.match(albums, /RememberChatForPicker\(\)/,
    'the unfiltered picker remembers it too');
});

test('the picker return routes the frame back to the chat, not to the chat list', () => {
  const mapped = slice(app, 'private Uri MapInitialUri(', 3200);
  assert.match(mapped, /TakePickerReturn\(/, 'the return is asked');
  assert.match(mapped, /return new Uri\(ChatPageUri/, 'and lands on the chat');
});

test('a return to a chat already in front is served and then dropped', () => {
  // The dormant case has no navigation at all: the chat is still on the screen
  // and the stored request would otherwise be honoured by a later navigation.
  const deposit = slice(app, 'private void DepositPickerContinuation(', 900);
  assert.match(deposit, /is ChatPage/, 'the page in front is checked');
  assert.match(deposit, /ClearPickerReturn\(\)/, 'the served request is dropped');
});
