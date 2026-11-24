'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const native = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Services/NativeConnection.cs'), 'utf8');

// The confirmation protocol: a message is not delivered until the phone that got it says so.
// The `ack` answers the server, and the server then stops re-sending - but the person who
// wrote the message is told nothing, and neither is a server that is waiting to see this
// device take what it is owed. WhatsApp answers every message it can read with a `receipt`
// of type `delivery`, and that is what makes the next one arrive without a restart.

function body(from, lines) {
  const start = native.indexOf(from);
  assert.ok(start > 0, from + ' is in the file');
  return native.slice(start, start + lines);
}

test('a message that was read is confirmed to the one who sent it', () => {
  const read = body('private async Task ReadMessageAsync', 14000);
  assert.match(read, /ConfirmDeliveryAsync\(session, incoming\)/,
    'the receive path confirms what it read');
  const confirm = body('private async Task ConfirmDeliveryAsync', 2000);
  assert.match(confirm, /IncomingMessage\.Receipt\(/,
    'the confirmation is the reference receipt');
  assert.match(confirm, /"delivery"/,
    'and it says the message was delivered');
  assert.match(confirm, /incoming\.From/,
    'addressed to the one who sent it');
});

test('the confirmation is not sent for what this phone wrote itself', () => {
  const read = body('private async Task ReadMessageAsync', 14000);
  const at = read.indexOf('ConfirmDelivery');
  assert.ok(at > 0, 'the confirmation is its own step');
  assert.match(read.slice(Math.max(0, at - 600), at), /IsOurEcho/,
    'an echo of our own message is not confirmed back to ourselves');
});

test('the message that is still waiting for its key is not confirmed', () => {
  // A message held for its sender key is one the server still owns; confirming it here
  // would be the one thing that lets the phone lose it for good.
  const read = body('private async Task ReadMessageAsync', 14000);
  const held = read.indexOf('WaitsForSenderKey');
  const confirmed = read.indexOf('ConfirmDelivery');
  assert.ok(held > 0 && confirmed > held,
    'the hold returns before the confirmation is reached');
});
