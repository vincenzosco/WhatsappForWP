'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const profile = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Protocol', 'Client', 'CallSignaling.cs'), 'utf8');
const native = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Services', 'NativeConnection.cs'), 'utf8');

function slice(text, startMarker, size) {
  const at = text.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return text.slice(at, at + size);
}

test('the offer leaves a line for everything this client does not model', () => {
  // A real call is the capture that shows what the far end sends, and the relay
  // dialog is read from one. An unknown child dropped in silence is a capture
  // lost - the log is where this client keeps what it cannot model yet.
  const from = slice(profile, 'public static CallWireProfile FromCallNode(BinaryNode call)', 1800);
  assert.match(from, /LogUnknown/,
    'the parse reports what it drops');

  const log = slice(profile, 'private static void LogUnknown', 1600);
  assert.match(log, /Say\("call wire: /, 'each unknown goes to the log seam');
  assert.match(profile, /internal static Action<string> WireLog/,
    'the log itself belongs to the app, through the seam');
  assert.match(native, /CallWireProfile\.WireLog = Diag\.Ok/,
    'and the app wires the seam to its own log');
  assert.match(log, /voip_settings/, 'the modelled children are the named ones kept');
  assert.doesNotMatch(log, /caller_pn/,
    'and `caller_pn` is no longer modelled, so it is logged like anything else unknown');
  assert.match(log, /Children\(\)/, 'the children are walked');
  assert.match(log, /offer\.Attrs/, 'the attributes are walked');
});