'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const interop = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Services', 'NativeInterop.cs'), 'utf8');
const check = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Services', 'AudioCodecCheck.cs'), 'utf8');

function slice(text, startMarker, size) {
  const at = text.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return text.slice(at, at + size);
}

test('native interop is answered by the phone and not by the application model', () => {
  // The old answer here was `#if SILVERLIGHT return false` - an assumption that
  // Silverlight cannot P/Invoke, and it switched every codec and call-audio path
  // off without asking. The phone's own answers say the marshaler runs: a probe
  // came back `DllNotFoundException 0x80131524` (the loader refused the image)
  // and `CallAudioProbe`'s `waaudio.dll` came back `0x8007007F` (the DLL loaded,
  // one export was missing). What failed then was the image's imports, fixed by
  // `tools/opus/waheap.def` - and a build carrying the fix was never asked,
  // because this switch answered for the phone. The question is a real P/Invoke
  // now, asked once, and whatever the phone answers is what every caller gets.
  assert.match(interop, /\[DllImport\("waopus\.dll"/,
    'the probe reaches the loader through a real P/Invoke');
  assert.doesNotMatch(interop, /^\s*#if\s+SILVERLIGHT/m,
    'the application model must not decide the answer');
  assert.match(interop, /catch\s*\(/,
    'the loader refusal is recorded, not thrown at the caller');
  assert.match(interop, /UnavailableReason/,
    'callers still read the reason the phone gave');
});

test('the codec check still asks its two questions when the probe fails', () => {
  // "Is the file in the package" and "will Windows load it" have two different
  // fixes. A failed probe must not skip them - the answer it records is exactly
  // the second question's evidence, and the header line says which image the
  // package carries.
  const run = slice(check, 'public static async Task RunAsync()', 700);
  assert.doesNotMatch(run, /NativeInterop\.IsSupported/,
    'a refused probe falls through to the two questions');
  assert.match(run, /DescribeAsync|inPackage/,
    'the check still reports what is in the package');
});