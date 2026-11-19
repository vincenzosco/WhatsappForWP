'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
const view = () => read('WhatsappApp/Services/ConversationView.cs');
const slice = (name, size) => {
  const src = view();
  const at = src.indexOf(name);
  return src.slice(at, at + size);
};

test('the newest row is followed after the layout, not before it', () => {
  // The follow used to end at `bar.Value = bar.Maximum` in the same flush that
  // learned about the new row: the bar's end is the end of the layout that has
  // run, and a row that arrived a moment ago has not grown the extent yet -
  // assigning the old maximum to a value that is already there moved nothing.
  // The list read as stuck while the message sat below the fold.
  const flush = slice('private void FlushScroll()', 2600);
  assert.match(flush, /_followBottom = true/,
    'the follow is armed for the layouts to come');
  assert.match(flush, /WaitForLayout\(\)/,
    'and the layout is what finishes it');
});

test('every layout re-applies the follow while it is armed', () => {
  // A virtualized list realizes its items one frame at a time: the extent can
  // grow across several passes, and the end the reader is taken to is the end
  // after them, not the end before.
  const onLayout = slice('private void OnListLayoutUpdated', 1600);
  assert.match(onLayout, /if \(_followBottom\)/);
  assert.match(onLayout, /Value = .*\.Maximum/,
    'the bar is taken to its end again');
  assert.match(onLayout, /WaitForLayout\(\)/,
    'and the follow stays armed for the pass after this one');
});

test('the follow ends when the reader leaves the newest row', () => {
  // The follow is for messages arriving at the bottom: somebody reading
  // something older stays exactly where they are.
  const changed = slice('private void OnScrollBarValueChanged', 900);
  assert.match(changed, /_followBottom = false/,
    'a reader who scrolls away is not dragged back');
  assert.match(changed, /BottomFollowMargin/,
    'and only the reader who really left');
});

test('an explicit jump and a reset drop the follow too', () => {
  const flush = slice('private void FlushScroll()', 2600);
  assert.match(flush, /_followBottom = false/,
    'a jump to an older row is not a follow of the newest one');
  const reset = slice('public void Reset()', 700);
  assert.match(reset, /_followBottom = false/,
    'and a page that goes away leaves nothing armed');
});