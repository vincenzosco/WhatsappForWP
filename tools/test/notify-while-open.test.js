'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const service = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Services', 'NotificationService.cs'), 'utf8');
const data = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Services', 'DataService.cs'), 'utf8');

function slice(text, startMarker, size) {
  const at = text.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return text.slice(at, at + size);
}

test('the app being open silences only the call, not the news', () => {
  // WhatsApp speaks to whoever is not reading: the chat on screen is the only
  // thing the reader already knows, and that case is decided where the message
  // arrives (the open chat and muted chats never reach the alert). Everything
  // else must say something even while the app is on screen - the shell toast
  // slides over the open app and is tapped to open the chat.
  for (const method of [
    'public static void ShowMessage(string chatId, string title, string body)',
    'public static void ShowStatus(string name, DateTime postedAt)',
    'public static void ShowMissedCall(string name, DateTime arrivedAt)'
  ]) {
    const body = slice(service, method, 420);
    assert.doesNotMatch(body, /Silenced\(\)/,
      method + ' speaks even while the app is open');
  }

  // A call is its own alert: the call screen is arriving to cover the phone.
  const call = slice(service, 'public static void ShowIncomingCall(string name)', 420);
  assert.match(call, /Silenced\(\)/,
    'the incoming call is the one alert the open call screen replaces');
});

test('the reader is spared where the message arrives', () => {
  // The one "what you are looking at" rule left: a message for the chat on
  // screen, or for a muted chat, never becomes an alert. This is in the caller,
  // just before the call it decides about.
  const at = data.indexOf('NotificationService.ShowMessage(message.ChatId');
  assert.notEqual(at, -1, 'the caller raises the message alert');
  const alert = data.slice(at - 300, at);
  assert.match(alert, /_activeChatId/, 'the open chat is excluded');
  assert.match(alert, /IsMuted/, 'a muted chat is excluded');
});

test('a message toast can be tapped into the chat', () => {
  // The shell toast over the open app must lead somewhere: the same route the
  // pinned tile taps, so the toast opens the conversation it announced.
  const message = slice(service, 'public static void ShowMessage(string chatId, string title, string body)', 700);
  assert.match(message, /PinnedTile\.NavigationUri/,
    'the message alert carries the chat route');
  const tiles = fs.readFileSync(
    path.join(ROOT, 'WhatsappApp', 'Services', 'PinnedTile.cs'), 'utf8');
  const uri = slice(tiles, 'public static Uri NavigationUri(', 400);
  assert.match(uri, /\?tile=/, 'the route is the tile route the mapper knows');
});
