'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const page = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ChatPage.xaml.cs'), 'utf8');
const changed = page.slice(page.indexOf('private void OnMessagesChanged'));

test('an insertion lands in _shown where the conversation put it', () => {
  // The service inserts a row in the order it belongs in (InOrderIndex): an
  // echo from another device, a re-delivery, a history row can arrive with an
  // older timestamp than the newest bubble. Drawing every insertion at the tail
  // showed it as the last message of the chat and broke the window the paging
  // walks over.
  const addBranch = changed.slice(
    changed.indexOf('NotifyCollectionChangedAction.Add'),
    changed.indexOf('private async'));
  assert.match(addBranch, /_shown\.Insert\(/,
    'the add branch splices the row into the page instead of appending it');
  assert.match(addBranch, /e\.NewStartingIndex\s*\+\s*\w+\s*-\s*_shownFrom/,
    'the splice offset is the insertion index against the window start');
});

test('an insertion before the first visible row only moves the window start', () => {
  const addBranch = changed.slice(
    changed.indexOf('NotifyCollectionChangedAction.Add'),
    changed.indexOf('private async'));
  // Straddling insertions land before the first visible row too: a batch whose
  // rows start above the page and run into it still has every row older than
  // the anchor, so the window start takes them all and nothing is drawn.
  assert.match(addBranch, /e\.NewStartingIndex\s*<\s*_shownFrom/,
    'every new row before the anchor is counted into _shownFrom, straddle included');
  const above = addBranch.indexOf('e.NewStartingIndex < _shownFrom');
  const moved = addBranch.indexOf('_shownFrom +=', above);
  assert.ok(moved > above && moved < addBranch.indexOf('_shown.Insert('),
    'the window moves before any splice is attempted');
});
