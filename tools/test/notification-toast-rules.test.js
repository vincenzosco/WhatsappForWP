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

test('an alert is held back while the app is on screen', () => {
  // The phone speaks only to somebody who is not looking at it: what is on
  // screen already has the news, and a toast over the open chat is noise.
  // This is the rule for every kind of alert, calls included - the call
  // screen is its own alert.
  for (const method of [
    'public static void ShowMessage(string title, string body)',
    'public static void ShowStatus(string name, DateTime postedAt)',
    'public static void ShowIncomingCall(string name)',
    'public static void ShowMissedCall(string name, DateTime arrivedAt)'
  ]) {
    const body = slice(method, 420);
    assert.match(body, /Silenced\(\)/,
      method + ' asks the one rule before it speaks');
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
  const toast = slice('private static void Toast(string title, string body)', 1200);
  assert.match(toast, /Loc\.Get\("Notification_NewMessage", "New message"\)/,
    'the empty body falls back to words');
  assert.match(toast, /string\.IsNullOrEmpty\(body\)/, 'the body is checked');
  assert.match(toast, /string\.IsNullOrEmpty\(title\)/, 'the title is checked too');
});

test('every toast decision is in the log', () => {
  const toast = slice('private static void Toast(string title, string body)', 1200);
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