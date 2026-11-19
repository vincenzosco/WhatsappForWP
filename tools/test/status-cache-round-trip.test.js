'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

const cache = () => read('WhatsappApp/Services/StatusCache.cs');
const slice = (src, name, size) => {
  const at = src.indexOf(name);
  return src.slice(at, at + size);
};

test('the load keeps a row it cannot date', () => {
  // A row with no date is not an old row: it is a row an older build wrote without
  // one, and the cutoff dropped it before the restore could give it the date it
  // comes back with - the status tab opened empty at every start, which is the
  // complaint this cache exists to answer. The day still decides on rows that
  // have one.
  const load = slice(cache(), 'public static async Task<List<ChatMessage>> LoadAsync', 2200);
  const undated = load.indexOf('default(DateTime)');
  const day = load.search(/PostedAt\s*<\s*cutoff/);
  assert.ok(undated >= 0, 'the load knows a row with no date is not an old row');
  assert.ok(day > undated, 'and the cutoff speaks only after that');
});

test('the save keeps a row it cannot date', () => {
  // The mirror of the load: a live row whose wire date was out of range has no
  // date, and the save used to drop it - a row with content no path ever persisted.
  const save = slice(cache(), 'public static async Task SaveAsync', 1400);
  const undated = save.indexOf('default(DateTime)');
  const day = save.search(/row\.Timestamp\s*<\s*cutoff/);
  assert.ok(undated >= 0, 'the save keeps the rows without a date');
  assert.ok(day > undated, 'and ages out only the rows that have one');
});

test('the restore runs the day again once its rows land', () => {
  // The pane prunes when the tab opens, before this restore can land (it is
  // fire-and-forget from the app start). A row the file read admitted can already
  // be out of its day by then; with the tab left open no later prune runs.
  const data = read('WhatsappApp/Services/DataService.cs');
  const restore = slice(data, 'private async System.Threading.Tasks.Task LoadCachedStatusesAsync', 2200);
  const insert = restore.indexOf('InsertStatusInOrder');
  const prune = restore.indexOf('PruneExpiredStatuses');
  const raise = restore.indexOf('RaiseStatusesChanged');
  assert.ok(insert >= 0 && prune > insert, 'the rows land first');
  assert.ok(raise > prune, 'and the day speaks before the feed is announced');
});