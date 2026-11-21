'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const storage = fs.readFileSync(path.join(ROOT, 'WhatsappApp', 'Services', 'NativeCredentialStorage.cs'), 'utf8');
const chat = fs.readFileSync(path.join(ROOT, 'WhatsappApp', 'Pages', 'ChatPage.xaml.cs'), 'utf8');

function slice(text, startMarker, size) {
  const at = text.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return text.slice(at, at + size);
}

test('the account is also written to a file, which survives the app being killed', () => {
  // `ApplicationData.Current.LocalSettings` reaches disk when a WP8.1 Silverlight app
  // is suspended. An exit by the back button, a deploy and a memory kill all end the
  // process without one - the pairing looked saved and the next start found nothing.
  // The chats and the pre-keys are files and never had this problem.
  assert.match(storage, /IsolatedStorageFile|LocalFolder/,
    'the mirror lives in a file');
  const write = slice(storage, 'public void Write(', 1500);
  assert.match(write, /Mirror|mirror|session-settings|WriteMirror/,
    'every key is mirrored to it');
});

test('the mirror answers when the settings hold nothing, and repairs them', () => {
  const read = slice(storage, 'public string Read(', 1500);
  assert.match(read, /Settings\.Values\.TryGetValue/, 'the settings are asked first');
  assert.match(read, /Mirror|mirror|session-settings|ReadMirror/,
    'and the file is the fallback');
});

test('a removed key is removed from the mirror too', () => {
  const write = slice(storage, 'public void Write(', 1500);
  assert.match(write, /Remove|Delete/,
    'an empty value empties both places');
});

test('the call button says it was pressed, so a call that goes nowhere is visible', () => {
  // The run of 17:50 on 21 November 2026 held no call line at all: the press and its
  // failure were the same silence. One line at the handler separates them.
  const handler = slice(chat, 'private void CallHeaderButton_Click(', 900);
  assert.match(handler, /Diag\.Ok\(/, 'the press is logged');
});

test('the call button is enabled before the bar is put on screen', () => {
  // A `ApplicationBarIconButton` whose `IsEnabled` moves while the bar is on screen is a
  // button the phone keeps drawing grey - the press a person does not make leaves no
  // line in any log. The state is set first and the bar is shown after.
  const block = slice(chat, 'if (contact != null)', 2200);
  const enable = block.indexOf('_callHeaderButton.IsEnabled');
  const shown = block.indexOf('ApplicationBar = _chatAppBar;');
  assert.notEqual(enable, -1, 'the button gets its state');
  assert.ok(enable < shown, 'the state precedes the render');
});