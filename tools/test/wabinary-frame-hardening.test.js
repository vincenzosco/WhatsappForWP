'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (f) => fs.readFileSync(path.join(ROOT, f), 'utf8');
const reader = read('WhatsappApp/Protocol/WABinary/NodeReader.cs');

test('a length is checked without adding to the index', () => {
  // `_index + length` is int arithmetic: a 32-bit length of 0x7FFFFFFF wrapped
  // the sum past the check and reached an allocation of two gigabytes from a
  // frame that merely announced it. Subtraction cannot wrap.
  assert.doesNotMatch(reader, /_index \+ length > _buffer\.Length/,
    'the check does not add');
  assert.match(reader, /length > _buffer\.Length - _index/,
    'the length is compared against what is left');
});

test('an absent optional server is an end of frame, not a rewind', () => {
  // The interop jid's server may be missing, and a frame that ends there is
  // legal. Catching every read failure and rewinding was broader than that: a
  // server field that started and then broke was put back and the rejected bytes
  // re-parsed as node structure - a length byte of 248 read as a list marker.
  const interop = reader.slice(reader.indexOf('private string ReadInteropJid'),
    reader.indexOf('private string ReadInteropJid') + 800);
  assert.doesNotMatch(interop, /catch \(ProtocolException\)/,
    'a broken server field is a broken frame');
  assert.match(interop, /if \(_index < _buffer\.Length\)/,
    'the field is read only when the frame carries it');
});
