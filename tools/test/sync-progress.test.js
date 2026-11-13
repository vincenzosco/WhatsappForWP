'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (file) => fs.readFileSync(path.join(ROOT, file), 'utf8');
const data = read('WhatsappApp/Services/DataService.cs');
const native = read('WhatsappApp/Services/NativeConnection.cs');
const home = read('WhatsappApp/Pages/HomePage.xaml');
const homeCode = read('WhatsappApp/Pages/HomePage.xaml.cs');

test('a history row leaves the chat preview and order current', () => {
  // The sync path used to add a message to its conversation and return, so a chat
  // that received its history kept an old preview in the old place until the next
  // live message. The history insert now refreshes both, like a live one.
  const add = data.slice(data.indexOf('private void AddHistoryMessage('));
  const body = add.slice(0, add.indexOf('private static int InOrderIndex'));
  assert.match(body, /RefreshPreview\(/, 'the insert refreshes the row preview');
  assert.match(body, /PlaceContact\(/, 'the insert puts the row where its time says');
});

test('the connection reports history sync progress as it lands', () => {
  assert.match(native, /event\s+EventHandler<int>\s+SyncProgress/,
    'a progress event carries the server percentage');
  const sync = native.slice(native.indexOf('private void ApplySync('));
  assert.match(sync, /SyncProgress != null.*SyncProgress\(this, sync\.Progress\)/,
    'ApplySync raises the progress with the sync percentage');
});

test('the data layer keeps the three sync numbers and raises a change', () => {
  assert.match(data, /public int SyncPercent/, 'the percentage is exposed');
  assert.match(data, /public int SyncedMessages/, 'the messages counted so far are exposed');
  assert.match(data, /public int SyncTotal/, 'the completion total is exposed');
  assert.match(data, /public bool SyncInProgress/, 'the bar knows whether sync is running');
  assert.match(data, /public event EventHandler SyncProgressChanged/);
});

test('the chats tab draws a native sync bar above the first chat', () => {
  assert.match(home, /x:Name="SyncProgressPanel"/, 'a named bar sits between the pivot and the list');
  assert.match(home, /x:Name="SyncProgressBar"/, 'a completion bar fills to the sync percentage');
  assert.match(home, /x:Name="SyncProgressText"/, 'a label carries the x of xx, percent');
  assert.match(homeCode, /SyncProgressChanged \+= /, 'the page follows the sync as it runs');
  const bar = homeCode.slice(homeCode.indexOf('private void UpdateSyncBar()'));
  const head = bar.slice(0, 700);
  assert.match(head, /SyncedMessages/, 'the label counts the messages');
  assert.match(head, /SyncTotal/, 'the label carries the completion total');
  assert.match(head, /SyncPercent/, 'the label carries the percentage');
});
