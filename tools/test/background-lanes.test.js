'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const sync = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Services', 'BackgroundSync.cs'), 'utf8');

function slice(text, startMarker, size) {
  const at = text.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return text.slice(at, at + size);
}

test('the check gets both lanes the system offers', () => {
  // The periodic check answers about every half hour, and that is the whole
  // promise of the lane. The second lane the scheduler has - the one that runs
  // while the phone is charging and has room - is the same check without the
  // wait, and registering it costs nothing when the phone keeps it off.
  assert.match(sync, /new ResourceIntensiveTask\(IntensiveTaskName\)/,
    'the charger lane is registered beside the periodic one');
  assert.match(sync, /intensive\.Description\s*=/,
    'the scheduler requires and gets its own description');
});

test('the charger lane counts in the three places the periodic one does', () => {
  // An "already registered" answer that skips re-registering would leave the
  // second lane missing forever after the first build that adds it; a removal
  // that forgets it leaves the phone holding a task this build no longer makes;
  // and the read-back exists because Add returning is not the system promising
  // anything - the same truth is owed for both lanes.
  const again = slice(sync, 'if (IsRegistered() && RegisteredInterval()', 320);
  assert.match(again, /Intensive(Registered\(\)|TaskName)/,
    'a registration is repeated while either lane is missing');

  const unregister = slice(sync, 'private static void Unregister()', 700);
  assert.match(unregister, /IntensiveTaskName/,
    'unregistering takes both lanes off the phone');

  const readback = slice(sync, 'ScheduledAction scheduled = ScheduledActionService.Find(TaskName);', 900);
  assert.match(readback, /Find\(IntensiveTaskName\)/,
    'the log says what the phone will really do about the second lane too');
});