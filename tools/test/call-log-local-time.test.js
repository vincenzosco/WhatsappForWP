'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const data = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/DataService.cs'), 'utf8');
const session = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/CallSession.cs'), 'utf8');

test('a call row carries local time like every other row the phone shows', () => {
  // The model stores local time: the wire carries seconds and the rows are
  // built with ToLocalTime. StartedAt is UTC (the session measures its own age
  // in UTC and must keep doing so), so the row takes it converted: writing the
  // raw ticks put the call an hour away from every synced row, and the plain
  // tick comparisons in InsertCall, MissedCalls and the toast watermark then
  // sorted and counted it wrong.
  const remember = data.slice(data.indexOf('private void RememberCall'),
    data.indexOf('private static string ReasonOf'));
  assert.match(remember, /row\.Timestamp = _call\.StartedAt\.ToLocalTime\(\)/,
    'the log row takes the session start converted to local time');
  assert.doesNotMatch(remember, /row\.Timestamp = _call\.StartedAt;/,
    'the raw UTC ticks do not reach the row');

  // The convention this follows: the wire model conversion is local too.
  assert.match(data, /Epoch\.AddSeconds\(seconds\)\.ToLocalTime\(\)/,
    'wire seconds become local rows');

  // The session itself stays in UTC on purpose: its own age is measured in UTC.
  assert.match(session, /StartedAt = DateTime\.UtcNow/,
    'the session keeps measuring itself in UTC');
});
