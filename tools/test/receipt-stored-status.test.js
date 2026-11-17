'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const cache = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/MessageCache.cs'), 'utf8');
const data = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/DataService.cs'), 'utf8');

test('a stored row can carry the status a receipt brings', () => {
  // The receipts of the offline queue arrive at the start, before a single
  // conversation has been read off the phone: a lookup in memory alone misses
  // every one of them and the ticks stay on "sent" for ever. The stored tail is
  // the conversation that answers then.
  assert.match(cache, /UpdateStatusAsync\(string chatId, string messageId, MessageStatus status\)/,
    'the cache takes a status for one stored row');
  assert.match(cache, /Writes\.RunAsync[\s\S]{0,200}UpdateStatus/,
    'the update goes through the write queue like every other change to a tail');
  assert.match(cache, /StatusLadder\.IsForward/,
    'a status is only ever written forwards');
});

test('the receipt lookup asks the stored tail before giving up', () => {
  const method = data.slice(data.indexOf('public void SetMessageStatus'));
  assert.ok(method.length > 0, 'SetMessageStatus exists');
  assert.match(method, /MessageCache\.UpdateStatusAsync/,
    'a miss in memory falls through to the copy on the phone');
  assert.match(method, /SetMessageStatusEverywhere/,
    'the in-memory conversations are still searched first');
  // The honest line comes after the stored lookup too: it says no bubble when
  // the phone has none, not when the app has none loaded.
  const fallthrough = method.indexOf('MessageCache.UpdateStatusAsync');
  const logged = method.indexOf('receipt: no bubble');
  assert.ok(fallthrough >= 0 && logged > fallthrough,
    'the "no bubble" line is written only after the stored tail was consulted');
});
