'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const srtp = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Protocol/Crypto/Srtp.cs'), 'utf8');

test('the counter block carries the whole 48-bit packet index', () => {
  // RFC 3711 4.1.1: counter bytes 8..13 are the packet index big-endian. A `uint`
  // index is shifted by 40 and 32 as >>8 and >>0 (C# masks shift counts), which
  // copied low index bytes into 8-9 where the top of the 48-bit index belongs.
  assert.match(srtp, /private byte\[\] Keystream\(ulong index/,
    'the index is 48 bits wide');
  assert.doesNotMatch(srtp, /Keystream\(uint index/);
  assert.match(srtp, /\(\(ulong\)[A-Za-z_]+ << 16\) \|\s*\(uint\)sequence/,
    'and the rollover counter sits above the sequence, not inside its 32 bits');
});

test('the tag covers the rollover counter of the epoch', () => {
  // RFC 3711 4.2: the authenticator covers the packet and, appended four bytes,
  // the rollover counter. Without it the tags this side makes are not the tags a
  // standard peer checks - no call ever interoperated.
  const uses = srtp.match(/AuthenticatedBytes\(/g) || [];
  assert.ok(uses.length >= 3, 'one helper, called from Seal and Open');
  assert.match(srtp, /Hmac\.ComputeSha1\(_authKey, AuthenticatedBytes\(/,
    'and the tag is computed over it');
});

test('the index estimation follows RFC 3711 section 3.3.1', () => {
  // A packet delayed across the 16-bit wrap belongs to the epoch before it, and a
  // packet past the half-space of a highest-seen in the upper half is already in
  // the next. Estimating only the wrap read a duplicated or reordered packet as a
  // wrap and moved every later receive one epoch away for the rest of the call.
  assert.match(srtp, /private bool TryEstimate\(byte high, byte low, out ulong index, out uint epoch\)/);
  assert.match(srtp, /_highestSequence < 32768/, 'the lower half-space branch');
  assert.match(srtp, /_highestSequence - 32768 > sequence/, 'and the upper one');
  assert.match(srtp, /private void CommitIndex\(byte high, byte low, uint epoch\)/);
  assert.match(srtp, /epoch > _rollover \|\| \(epoch == _rollover && sequence > _highestSequence\)/,
    'the state only ever moves forward');
});

test('an unauthenticated packet never moves the receive state', () => {
  const open = srtp.slice(srtp.indexOf('public byte[] Open'));
  const verified = open.indexOf('FixedTimeEquals');
  const committed = open.indexOf('CommitIndex');
  assert.ok(verified > 0 && committed > verified,
    'the commit sits after the tag check, so a packet nobody vouches for cannot walk the rollover counter');
});
