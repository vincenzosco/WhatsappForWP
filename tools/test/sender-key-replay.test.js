'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const record = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Protocol/Signal/SenderKeyRecord.cs'), 'utf8');

test('a message under another sender key is not a duplicate of this record', () => {
  // The group rotates sender keys, and a skmsg under the NEW key lands while this
  // record still holds the old one. Counting it as already read made the connection
  // acknowledge it and drop it for good - the key-id refusal in `Decrypt` is the
  // right answer, and it leaves the sender free to redeliver under the right key.
  const alreadyRead = record.slice(record.indexOf('public bool AlreadyRead'),
    record.indexOf('public bool AlreadyRead') + 700);
  assert.match(alreadyRead, /if \(message\.KeyId != KeyId\) return false;/,
    'the key id is checked before the iteration is');
  const keyCheck = alreadyRead.indexOf('message.KeyId != KeyId');
  const iterationCheck = alreadyRead.indexOf('message.Iteration >= Iteration');
  assert.ok(keyCheck >= 0 && iterationCheck > keyCheck,
    'and it comes first: a foreign key never reaches the iteration comparison');
});
