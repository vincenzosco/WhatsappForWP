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

test('an alert waits for the call screen and nothing else', () => {
  // What the reader is looking at decides, and only one thing on the phone IS
  // the news already: the call screen arriving for the call that is ringing.
  // Every other alert must speak - the chat on screen and muted chats are
  // excluded where the message arrives, not here.
  const call = slice('public static void ShowIncomingCall(string name)', 420);
  assert.match(call, /Silenced\(\)/,
    'the call screen is its own alert');

  for (const method of [
    'public static void ShowMessage(string chatId, string title, string body)',
    'public static void ShowStatus(string name, DateTime postedAt)',
    'public static void ShowMissedCall(string name, DateTime arrivedAt)'
  ]) {
    const body = slice(method, 420);
    assert.doesNotMatch(body, /Silenced\(\)/,
      method + ' speaks even while the app is open');
  }
});

test('the rule is one method that remembers the app state', () => {
  const rule = slice('private static bool Silenced()', 500);
  assert.match(rule, /_foreground/,
    'the rule reads whether the app is on screen');
  assert.match(rule, /Diag\.Ok\("toast: /,
    'and says in the log why nothing was said');
});

test('a toast always carries words for the shell', () => {
  // A voice note or a photo without a caption arrives with no text at all,
  // and a toast with an empty body is refused by the shell: every alert of a
  // closed app fell silent on those messages. There is always something to
  // read - the fallback name of "a message arrived".
  const toast = slice('private static void Toast(string title, string body)', 1700);
  assert.match(toast, /Loc\.Get\("Notification_NewMessage", "New message"\)/,
    'the empty body falls back to words');
  assert.match(toast, /string\.IsNullOrEmpty\(body\)/, 'the body is checked');
  assert.match(toast, /string\.IsNullOrEmpty\(title\)/, 'the title is checked too');
});

test('every toast decision is in the log', () => {
  const toast = slice('private static void Toast(string title, string body)', 1700);
  assert.match(toast, /Diag\.Ok\("toast: shown/,
    'a toast that went to the shell says so');
  assert.match(toast, /Diag\.Ok\("toast: suppressed/,
    'and one the settings stopped is named too');
});

test('the fallback words exist in both languages', () => {
  const en = fs.readFileSync(
    path.join(ROOT, 'WhatsappApp.Core', 'Strings', 'Resources.resx'), 'utf8');
  const it = fs.readFileSync(
    path.join(ROOT, 'WhatsappApp.Core', 'Strings', 'Resources.it-IT.resx'), 'utf8');
  assert.match(en, /<data name="Notification_NewMessage"/);
  assert.match(it, /<data name="Notification_NewMessage"/);
});

test('a switch that turns an alert off says so in the log', () => {
  // The settings that mute a kind of alert returned in silence: a device log
  // could not tell "the switch was off" from "the code never ran".
  for (const method of [
    'public static void ShowMessage(string chatId, string title, string body)',
    'public static void ShowStatus(string name, DateTime postedAt)',
    'public static void ShowIncomingCall(string name)',
    'public static void ShowMissedCall(string name, DateTime arrivedAt)'
  ]) {
    const body = slice(method, 560);
    assert.match(body, /Diag\.Ok\("toast: suppressed/,
      method + ' names the switch that silenced it');
  }
});

test('the one rule reads the app state as it now is', () => {
  // The flag the rule reads is written from the screen thread and read from
  // the network dispatch: a plain field can be seen stale for a moment.
  assert.match(service, /private static volatile bool _foreground/);
});