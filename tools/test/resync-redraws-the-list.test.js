'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const data = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Services/DataService.cs'), 'utf8');

// The resync rebuilds the chat list and the log proves the work lands: "107 row(s) named by
// nothing left the list". The screen still did not change, because the rows of the list live
// in an ObservableCollection and the resync is async - removing rows from it on a worker
// thread is a change the binding never hears about. The rows the resync touches have to be
// touched on the UI thread, or the repair is invisible until the app restarts.

function method(name, lines) {
  const start = data.indexOf(name);
  assert.ok(start > 0, name + ' is in the file');
  return data.slice(start, start + lines);
}

test('the rows the resync drops go on the UI thread', () => {
  const drop = method('private async Task DropRowsNothingNamesAsync', 3200);
  const removal = drop.indexOf('_contacts.RemoveAt');
  assert.ok(removal > 0, 'the resync does remove rows');
  assert.match(drop.slice(Math.max(0, removal - 700), removal), /UiThread\.RunAsync/,
    'and the removal is marshalled to the thread that draws the list');
});

test('the rows the resync restores go on the UI thread', () => {
  const restore = method('private async Task RestoreRowsFromTheirTailsAsync', 3200);
  assert.match(restore, /UiThread\.RunAsync/,
    'the restored rows are applied where the binding can hear it');
  assert.match(restore, /SortContacts/,
    'and the order is redone too');
});
