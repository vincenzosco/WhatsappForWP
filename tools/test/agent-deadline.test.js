'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const sync = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Services', 'BackgroundSync.cs'), 'utf8');

function slice(startMarker, size) {
  const at = sync.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return sync.slice(at, at + size);
}

test('the save in the tail of a run stops at the run deadline', () => {
  // The system kills a scheduled agent that does not call NotifyComplete in
  // time and disables the task for good - which is how the check went
  // "unavailable" on the phone. The work budget is only real if every part of
  // the run respects it: the save passes must clip their slice to the time the
  // budget still has and stop when it is gone.
  const save = slice('for (int pass = 0; pass < 3; pass++)', 1400);
  assert.match(save, /RunBudget\s*-\s*watch\.Elapsed|watch\.Elapsed\s*<\s*RunBudget/,
    'the save looks at the run budget before each pass');
  assert.match(save, /break;/, 'and stops when there is no time left');
});

test('a registration is checked after it is made, not believed', () => {
  // ScheduledActionService.Add can return while the system keeps the task
  // disabled (battery saver, the phone settings): the run must read the state
  // back and say what it really is instead of announcing a cadence that never
  // comes.
  const register = slice('var task = new PeriodicTask(TaskName);', 1300);
  assert.match(register, /IsScheduled/, 'the state is read back after the add');
  assert.match(register, /Diag\.Ok\("background: /, 'and the truth goes to the log');
});
