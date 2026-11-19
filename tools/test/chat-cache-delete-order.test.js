'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

test('the chat-list delete runs through the write queue', () => {
  // The same race MessageCache closed for its tails: a queued list write landing
  // after the delete recreated chats.json from the rows snapped before it - at a
  // re-link that is the previous account's list as the new account's photograph.
  // A delete chained in the queue runs after every write already in it, and no
  // write can run beside it.
  const cache = read('WhatsappApp/Services/ChatCache.cs');
  const del = cache.slice(cache.indexOf('public static async Task DeleteAsync'),
    cache.indexOf('public static async Task DeleteAsync') + 1200);
  assert.match(del, /Writes\.RunAsync/,
    'MessageCache.cs already spells why a delete beside an in-flight write was undone by it');
  const queued = del.indexOf('Writes.RunAsync');
  const direct = del.search(/TryGetItemAsync/);
  assert.ok(queued >= 0 && (direct < 0 || direct > queued),
    'the file ops belong inside the queued job, not beside it');
  assert.match(del, /TempName/,
    'and the stale temp goes in the same job, where nothing can be writing it');
});