'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (file) => fs.readFileSync(path.join(ROOT, file), 'utf8');
const inbox = read('WhatsappApp/Services/AttachmentInbox.cs');
const chat = read('WhatsappApp/Pages/ChatPage.xaml');
const chatCode = read('WhatsappApp/Pages/ChatPage.xaml.cs');
const app = read('WhatsappApp/App.xaml.cs');

test('the inbox can list the whole queue and drop one item from it', () => {
  // Several picked files are several waiting items. The composer has to see the
  // whole queue without taking it apart, and the send loop still drains it one
  // at a time - so the queue gains a read view and a single-item remove beside
  // the existing TakeNext/Clear, rather than being emptied to be counted.
  assert.match(inbox, /public static IList<PendingAttachment> Snapshot\(\)/,
    'a snapshot lists every waiting item without dequeuing it');
  assert.match(inbox, /public static bool Remove\(string localFileName\)/,
    'one waiting item can be removed by the file it names');
  assert.match(inbox, /public static PendingAttachment TakeNext\(\)/,
    'the send path still drains the queue one file at a time');
});

test('the composer draws every waiting item and counts them', () => {
  // The single preview became a strip: one tile per queued file, each removable,
  // and a count of how many are waiting. This is what makes "3 photos and a
  // video" legible instead of showing one of the four and sending all of them.
  assert.match(chat, /x:Name="PendingItems"/,
    'a named container holds one tile per queued attachment');
  assert.match(chatCode, /AttachmentInbox\.Snapshot\(\)/,
    'the composer builds its tiles from the queue snapshot');
  assert.match(chatCode, /AttachmentInbox\.Remove\(/,
    'a tile can take its one attachment out of the queue');
});

test('a shared batch keeps every file, not just the first', () => {
  // The picker already queues every chosen file; sharing into the chat has to do
  // the same. A share of 3 photos and a video is four items, and the loop that
  // deposits them must not stop at the first one.
  const share = app.slice(app.indexOf('AcceptShareAsync'));
  const items = share.slice(0, share.indexOf('ReportDataRetrieved'));
  assert.ok(items.indexOf('for (') > -1, 'shared items are visited in a loop');
  assert.doesNotMatch(items, /break\s*;/,
    'the loop does not drop every shared file after the first');
});
