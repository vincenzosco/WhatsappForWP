'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

const data = () => read('WhatsappApp/Services/DataService.cs');
const method = (name, size) => {
  const src = data();
  const at = src.indexOf(name);
  return src.slice(at, at + size);
};

test('a cached status with no date gets one before it is shown', () => {
  // `Moment(0)` is "the wire did not say": a live arrival stamps it with the arrival
  // second (DataService.Moment), but a row restored from the cache carries the
  // DateTime.MinValue a wire-less row was given instead. Such a row sorts below
  // every other one, and the pane's prune drops it the moment the tab opens: the
  // feed the cache exists to show was thrown away before the screen drew it.
  const restore = method('private async System.Threading.Tasks.Task LoadCachedStatusesAsync', 1600);
  const stamp = restore.search(/Timestamp\s*==\s*default\(DateTime\)/);
  const insert = restore.indexOf('InsertStatusInOrder');
  assert.ok(stamp >= 0, 'the restore checks the date a row came with');
  assert.ok(insert > stamp, 'and stamps it before the row is inserted');
});

test('the prune keeps an undated row and drops every empty one', () => {
  // An empty row has nothing to draw and goes, the way the prune's own comment
  // says. A row with no date is not an old row: it is a row the wire did not
  // date, and dropping it empties the feed the cache was built to restore.
  const prune = method('public void PruneExpiredStatuses', 1400);
  assert.match(prune, /default\(DateTime\)/,
    'the prune knows a row with no date is not an old row');
  assert.match(prune, /row\.Timestamp\s*<\s*cutoff/,
    'and the day decides only on rows that have one');
  const undated = prune.indexOf('default(DateTime)');
  const day = prune.search(/row\.Timestamp\s*<\s*cutoff/);
  assert.ok(undated >= 0 && day > undated,
    'the undated row is kept before the day ever speaks');
});

test('the stamp is local time like every other row', () => {
  const restore = method('private async System.Threading.Tasks.Task LoadCachedStatusesAsync', 1600);
  assert.match(restore, /DateTime\.Now/,
    'a row without a moment is treated the way Moment(0) treats a wire without one');
});