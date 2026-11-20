'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const data = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Services', 'DataService.cs'), 'utf8');
const sync = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Services', 'BackgroundSync.cs'), 'utf8');

function slice(text, startMarker, size) {
  const at = text.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return text.slice(at, at + size);
}

test('the snapshot says whether it covered everything', () => {
  // The tail loop skips a conversation that changed while it was writing and
  // leaves it "to the next snapshot" - but the background run has no next
  // snapshot: the message was acknowledged, the server never sends it again,
  // and the skipped tail is the row that never reached the phone. The save
  // reports what it could not cover so the run can save again.
  const save = slice(data, 'public async Task<bool> SaveSnapshotAsync()', 3600);
  assert.match(save, /Task<bool> SaveSnapshotAsync/, 'the save reports completion');
  assert.match(save, /skipped/, 'the tails the guard skipped are counted');
  assert.match(save, /return /, 'and the answer reaches the caller');
});

test('the run saves again what the first pass could not cover', () => {
  const run = slice(sync, 'for (int pass', 2400);
  assert.match(run, /for \(int pass/, 'the save is a bounded set of passes');
  assert.match(run, /pass </, 'with a ceiling');
});

test('the run does not wait forever on the save', () => {
  // An unbounded wait is what the phone answers by killing the run: no
  // NotifyComplete, and an agent that overruns often enough is disabled by the
  // system - then no closed-app notification works at all. The wait is
  // bounded; whatever did not fit is covered by the next run.
  const run = slice(sync, 'for (int pass', 2400);
  assert.match(run, /\.Wait\([^)]/, 'the wait has a time bound');
});