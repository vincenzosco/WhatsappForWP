'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const view = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/ConversationView.cs'), 'utf8');

test('the newest message is kept at the bottom, not scrolled to the top', () => {
  // LongListSelector.ScrollTo aligns its item to the TOP of the viewport, so a message
  // just sent landed in empty space above the text box instead of against it - the list
  // read as not updating and the rows looked stuck at the top. When the target is the
  // newest row the bar is scrolled to its end instead, which is where a conversation
  // keeps its latest message.
  const flush = view.slice(view.indexOf('private void FlushScroll()'));
  const body = flush.slice(0, flush.indexOf('private void WaitForScrollLayout()'));

  assert.match(body, /bool newest = .*target == _items\[_items\.Count - 1\]/,
    'the newest row is recognised');
  assert.match(body, /bar\.Value = bar\.Maximum/,
    'the newest row is taken to the bottom of the list');
  assert.match(body, /_list\.ScrollTo\(target\)/,
    'a jump to an older row still scrolls straight to it');
});
