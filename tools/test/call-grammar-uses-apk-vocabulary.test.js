'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const signaling = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Protocol/Client/CallSignaling.cs'), 'utf8');
const vocabulary = new Set(fs.readFileSync(
  path.join(__dirname, 'fixtures', 'apk-call-tokens.txt'), 'utf8')
  .split('\n').filter(line => line && !line.startsWith('#')));

// The WABinary dictionary of the WhatsApp APK carries every name the protocol can say as
// a token. A name that is not in it cannot travel: it would have to be written out in full,
// and the far end has no idea what it means. So every attribute and tag this app puts on a
// call has to be in that vocabulary - the alternative is guessing, and a guess on the wire
// is what a silently dropped offer looks like.

function writtenNames() {
  const names = [];
  for (const match of signaling.matchAll(/Attrs\.Add\("([^"]+)"/g)) names.push(match[1]);
  for (const match of signaling.matchAll(/new BinaryNode\("([^"]+)"/g)) names.push(match[1]);
  return names;
}

test('every name on a call stanza is in the apk vocabulary', () => {
  const outside = writtenNames().filter(name => !vocabulary.has(name));
  assert.deepEqual(outside, [],
    'these names do not exist in the protocol dictionary: ' + outside.join(', '));
});

test('the offer does not name the caller twice', () => {
  // `call-creator` is the caller, and the apk asserts it is present both when it builds a
  // payload and when it reads one. There is no `caller_pn` and no `caller_country_code` in
  // any source at all - those two were this app's own invention, written on every offer.
  // Only the code counts here: the comments above explain why the names are gone.
  const code = signaling.replace(/\/\/.*$/gm, '');
  assert.doesNotMatch(code, /caller_pn|caller_country_code/,
    'the invented attributes are gone from the writer and the model');
});
