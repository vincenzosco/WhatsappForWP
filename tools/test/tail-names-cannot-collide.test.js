'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const cache = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/MessageCache.cs'), 'utf8');

test('a tail name says which chat it is, punctuation and all', () => {
  // The name kept only letters, digits, `-` and `_`, so `393401234567:12@s.whatsapp.net`
  // and `39340123456712@s.whatsapp.net` named one file between them - and the two
  // conversations then read and wrote each other's rows. What is dropped has to be
  // written out, not thrown away.
  const nameFor = cache.slice(cache.indexOf('private static string Encode'),
    cache.indexOf('private static string Encode') + 1400);
  assert.match(nameFor, /ToString\("X4"\)/,
    'a character that cannot go in a file name is written out as four hex digits');
  assert.match(nameFor, /c == '-'\)/,
    'and only letters, digits and - are taken as they are');
});

test('a tail written by the old scheme is still read', () => {
  // Every tail on the phone is named the old way. Reading only the new name would have
  // orphaned all of them at once - the conversations would have gone empty until each one
  // was fetched again.
  assert.match(cache, /LegacyFileNameFor/,
    'the name the old build wrote is still looked for');
});
