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

test('the follow is armed before the list has an extent to scroll', () => {
  // The rows are realized one frame at a time: right after the chat opens the
  // bar still reports no extent. Arming the bottom only when the bar already
  // had one left the newest row top-aligned with nothing to re-apply the
  // follow to. The newest row arms it whatever the bar says; the layouts to
  // come take the reader to the end as the extent appears.
  const flush = view.slice(view.indexOf('private void FlushScroll()'));
  const body = flush.slice(0, flush.indexOf('private void WaitForScrollLayout()'));

  assert.match(body, /if \(newest\)\s*\{[\s\S]*?_followBottom = true;/,
    'the newest row arms the follow unconditionally');
  assert.doesNotMatch(body, /if \(newest && bar/,
    'the follow does not wait for the extent');
});

test('finding the scroll bar does not disarm the follow\'s layout wait', () => {
  // FlushScroll arms a LayoutUpdated handler and then attaches the scroll bar.
  // The attach stopped the wait on the way out - the handler was gone before it
  // ever ran, and the follow had nothing left to re-apply it on a list that
  // grows its extent over several passes.
  const attach = view.slice(view.indexOf('private void AttachScrollBar()'));
  const body = attach.slice(0, attach.indexOf('private ScrollBar ScrollBar()'));

  assert.match(body, /if \(!_followBottom\) StopWaitingForLayout\(\);/,
    'the wait stays armed while the follow is on');
});
