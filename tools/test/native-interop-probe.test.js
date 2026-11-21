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

test('native interop is answered by the phone and not by the application model', () => {
  // The answer used to be `#if SILVERLIGHT return false` - an assumption that switched
  // every codec and call-audio path off before the loader was reached. The phone's own
  // answers say the marshaler runs and the loader decides: a probe came back
  // `DllNotFoundException 0x80131524` (the image was in the package, its imports were
  // not on the phone - fixed by `tools/opus/waheap.def`) and, on this model,
  // `NotSupportedException: DllImport cannot be used on user-defined methods`, which is
  // the platform's last word. The question is one real call into `waopus.dll` now, and
  // whatever the loader answers is what every caller gets - which is what moved the
  // codec to managed code in the first place.
  assert.match(interop, /\[DllImport\("waopus\.dll"/,
    'the probe reaches the loader through a real P/Invoke');
  assert.doesNotMatch(interop, /^\s*#if\s+SILVERLIGHT/m,
    'the application model must not decide the answer');
  assert.match(interop, /catch\s*\(/,
    'the loader refusal is recorded, not thrown at the caller');
  assert.match(interop, /UnavailableReason/,
    'callers still read the reason the phone gave');
});

test('the codec check reports the codec that is actually in the package', () => {
  // The codec is managed code now (`WhatsappApp/Opus`), so there is no DLL to find and
  // no header to describe: the check owes the reader one line saying what carries a
  // note, and that line has to name the managed codec and not the native one.
  assert.match(check, /Diag\.Ok\("voice: /, 'the check speaks once, to the log');
  assert.match(check, /VoiceNoteEncoder\.IsAvailable/,
    'the answer comes from the codec itself');
  assert.doesNotMatch(check, /TryGetItemAsync|\[DllImport|static\s+extern/,
    'there is no native file left to look for or to call');
});