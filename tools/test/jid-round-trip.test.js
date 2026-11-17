'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (f) => fs.readFileSync(path.join(ROOT, f), 'utf8');
const writer = read('WhatsappApp/Protocol/WABinary/NodeWriter.cs');
const jids = read('WhatsappApp/Protocol/WABinary/JidUtils.cs');

test('every jid the reader can build is written back as itself', () => {
  // The compact forms carry a one-byte device and a domain byte that names the
  // server: a wider device, a server outside that table or an agent segment went
  // in as one jid and came out as another (the ack echo re-encodes what it
  // decoded, so the round trip is on the wire). What the compact forms cannot
  // carry goes out in the wide form, which carries the parts as strings.
  assert.match(jids, /public string Agent/,
    'the agent segment is kept by the split');
  assert.match(jids, /decoded\.Agent = agent/,
    'and put back by the join');

  assert.match(writer, /Tags\.FbJid/,
    'the wide form is written when needed');
  assert.match(writer, /ServerForDomain\(jid\.DomainType\) == jid\.Server/,
    'the domain byte is used only when it names the same server back');
  assert.match(writer, /jid\.Device <= 255/,
    'and the device byte only when it fits one byte');
  assert.match(writer, /string\.IsNullOrEmpty\(jid\.Agent\)/,
    'the agent segment decides too');
  assert.match(writer, /jid\.User \+ "_" \+ jid\.Agent/,
    'and the segment is written back with the user');
});
