'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');

test('the name book is written beside the file and moved over it', () => {
  // Every durable cache here writes to `*.json.new` and moves it over the real
  // file in one step: a process frozen mid-write leaves the old file, not an
  // unparsable one. The name book held out - one torn write lost the whole book
  // and the LID pairs with it, and `ChatIdFor` then folded `@lid` rows into
  // nothing.
  const cache = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/NameCache.cs'), 'utf8');
  assert.doesNotMatch(cache, /CreateFileAsync\(\s*FileName,\s*CreationCollisionOption\.ReplaceExisting\)/,
    'the real file is never written in place');
  assert.match(cache, /\.new/,
    'the write lands beside the file');
  assert.match(cache, /MoveAndReplaceAsync|RenameAsync/,
    'and reaches the real name in one move');
});

test('the avatar cache follows the same move', () => {
  const cache = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/AvatarCache.cs'), 'utf8');
  assert.doesNotMatch(cache, /CreateFileAsync\(\s*FileName,\s*CreationCollisionOption\.ReplaceExisting\)/,
    'the real file is never written in place');
  assert.match(cache, /MoveAndReplaceAsync|RenameAsync/,
    'the write reaches the real name in one move');
});
