// The tail the previous run left is carried into the next run's log so a run that
// died keeps its last words. The lines are written as they stood: re-levelling them
// through `Ok` gave each old line a second `ok:` and the tail read as a stutter.
const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..', '..');
const read = (p) => fs.readFileSync(path.join(root, p), 'utf8');

test('the carried tail keeps its own words and its own level', () => {
  const crash = read(path.join('WhatsappApp', 'Services', 'CrashReport.cs'));
  const replay = crash.slice(crash.indexOf('the previous run did not end'),
    crash.indexOf('Diag.Flush', crash.indexOf('the previous run did not end')));
  assert.doesNotMatch(replay, /Diag\.Ok\("  "/,
    'the replay does not add a level to a line that already has one');
  assert.match(replay, /Diag\.Raw\("  " \+ lines\[i\]\)/,
    'the line is written as it stood, indented as carried');

  const diag = read(path.join('WhatsappApp', 'Services', 'Diag.cs'));
  assert.match(diag, /public static void Raw\(string/,
    'the writer that adds no level exists');
});