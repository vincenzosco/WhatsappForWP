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

test('a tile tap routes the frame straight to the chat, with no stop at home', () => {
  // The tap names the conversation; showing the chat list first is a page the
  // user never asked to see, and it flashes before the chat opens.
  assert.match(app, /private const string ChatPageUri = "\/Pages\/ChatPage\.xaml";/);
  const mapper = slice(app, 'private Uri MapInitialUri(', 2400);
  assert.match(mapper, /return new Uri\(ChatPageUri/,
    'the tile branch names the chat page');

  // Without an account the chat in the tile means nothing: the launch still
  // goes to the pair page.
  assert.match(mapper, /StartTarget\.Pair/, 'the unpaired launch keeps the pair page');
});

test('the tile route is served once: a fresh arrival opens it, a replay drops it', () => {
  // The frame replays the tile's launch URI when the user comes back from the
  // chat, and the mapper arms the request again. A landing that is not new is
  // that replay, not a tap: the request must be dropped, or the back key
  // reopens the chat it just left.
  const landed = slice(chat, 'protected override void OnNavigatedTo(', 1700);
  assert.match(landed, /SessionService\.PendingChatId/, 'the chat takes the tile\'s request');
  assert.match(landed, /NavigationMode\.New/,
    'the page tells a new arrival from a way back');
  assert.match(landed, /ClearPendingChat\(\)/, 'a replay drops the request');
});

test('the way back from a tile-opened chat leads to the chats, and cannot replay', () => {
  const back = slice(chat, 'protected override void OnBackKeyPress(', 900);
  assert.match(back, /e\.Cancel = true/, 'the back key is taken over');
  assert.match(back, /HomePage\.xaml/, 'and goes to the chats');
  assert.match(back, /RemoveBackEntry\(\)/,
    'the tile route leaves the journal, or coming back to it reopens the chat');
});

test('the activation path still serves a tapped chat with no landing event', () => {
  const activated = slice(app, 'private void OnActivated(', 500);
  assert.match(activated, /OpenPendingChat\(/, 'the tap is served on activation too');
  const served = slice(app, 'private void OpenPendingChat(', 1200);
  assert.match(served, /NavigateToPage\("ChatPage"/,
    'and opens the chat directly, not through the home page');
});
