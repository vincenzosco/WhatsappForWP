'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const home = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Pages', 'HomePage.xaml.cs'), 'utf8');
const app = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'App.xaml.cs'), 'utf8');

function slice(text, startMarker, size) {
  const at = text.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return text.slice(at, at + size);
}

test('the tile route is served once: only a fresh arrival opens the chat', () => {
  // The frame replays the tile's launch URI when the user comes back from the
  // chat, and the mapper arms the request again. A landing that is not new is
  // that replay, not a tap: the request must be dropped, or the back key
  // reopens the chat it just left.
  const open = slice(home, 'public void OpenPinnedChat(', 900);
  assert.match(open, /bool freshArrival|NavigationMode/, 'the arrival kind is an argument');
  assert.match(open, /ClearPendingChat\(\)/, 'a replay drops the request');

  const navigated = slice(home, 'protected override void OnNavigatedTo(', 1600);
  assert.match(navigated, /NavigationMode\.New/,
    'the page tells a new arrival from a way back');
});

test('the app hands the arrival kind down from its navigated handler', () => {
  const landed = slice(app, 'private void RootFrame_Navigated(', 700);
  assert.match(landed, /NavigationMode/,
    'the frame passes what kind of navigation just landed');

  // A tile tap that reactivates the app is a fresh arrival with no landing
  // event: the activation path opens the chat itself.
  const activated = slice(app, 'private void OnActivated(', 500);
  assert.match(activated, /NavigationMode\.New|OpenPendingChat\(true\)|OpenPendingChat\(\)/,
    'the activation path keeps opening the tapped chat');
});
