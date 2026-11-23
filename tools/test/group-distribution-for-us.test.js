'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const incoming = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Protocol/Client/IncomingMessage.cs'), 'utf8');

test('the sender key distribution is opened with the copy sealed for this device', () => {
  // A group stanza carries one `to[jid]` per device of every member. The one that can be
  // opened here is the copy sealed for THIS account; a reader that takes the first `to`
  // on faith opens somebody else's copy, the decrypt throws, the throw is swallowed as
  // "nothing that came with a group message is worth losing the message for", the sender
  // key is never stored, and every later message of that member is dropped as one that
  // waits for a key that never comes.
  const pairwise = incoming.slice(incoming.indexOf('private static BinaryNode PairwiseEnc'),
    incoming.indexOf('private static BinaryNode CipherEnc'));
  assert.match(pairwise, /EncNamed\(/,
    'the copy is picked by the account it was sealed for');
  assert.doesNotMatch(pairwise, /DeviceEnc\(/,
    'not by being first in the list');
});

test('the group path is told who this account is', () => {
  // The filter needs the account's two addresses - the number and the LID - and they
  // come with the credentials. A sender-key reader with no credentials has no way to
  // tell its own copy from the other devices' and falls back to guessing.
  assert.match(incoming, /PairwiseEnc\(BinaryNode message, Credentials credentials\)/,
    'the distribution is read with the account in hand');
  assert.match(incoming, /PairwiseEnc\(message, credentials\)/,
    'and the group reader passes what it was given');
});