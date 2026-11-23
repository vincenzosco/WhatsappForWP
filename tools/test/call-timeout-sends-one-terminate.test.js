'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const data = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/DataService.cs'), 'utf8');

test('the unanswered-call timeout sends one terminate, not one per tick', () => {
  // The one-second tick of both call screens calls this again while the first terminate
  // is still on the wire: the only guard was the call being occupied, and that stays true
  // until the send comes back. On a slow socket the peer was told the call was over once a
  // second, for as long as the send took.
  const method = data.slice(data.indexOf('public async Task EndUnansweredCallAsync'),
    data.indexOf('public async Task EndUnansweredCallAsync') + 900);
  assert.match(method, /if \(_endingCall\) return;/,
    'a terminate already on its way stops the next one');
  assert.match(method, /_endingCall = true;/,
    'and the flag goes up before the send, not after it');
});
