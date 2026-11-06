'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const waaudio = require('../check-waudio');

const DLL = path.join(__dirname, '..', '..', 'WhatsappApp', 'waaudio.dll');

/** The committed file, read once: these tests never write to it. */
function shipped() {
  return fs.readFileSync(DLL);
}

/** A copy with one byte changed, to prove the guard notices. */
function patched(change) {
  const buffer = Buffer.from(shipped());
  change(buffer, waaudio.peInfo(buffer));

  return buffer;
}

test('the shipped DLL is ARMNT, for the ARMv7 phone', () => {
  const info = waaudio.peInfo(shipped());

  assert.ok(info, 'the file must be a readable PE');
  assert.strictEqual(info.machine, 0x01c4);
});

test('the shipped DLL passes: no KERNEL32, no console, both exports', () => {
  const found = waaudio.problems(waaudio.peInfo(shipped()));

  assert.deepStrictEqual(found, []);
});

test('the shipped DLL imports the two Media Foundation modules and one API set', () => {
  const names = waaudio.peInfo(shipped()).imports.map((entry) => entry.name.toLowerCase());

  assert.ok(names.indexOf('mfplat.dll') >= 0, names.join(', '));
  assert.ok(names.indexOf('mfreadwrite.dll') >= 0, names.join(', '));
  assert.ok(names.every((name) => /^api-ms-win-/.test(name) || waaudio.ALLOWED.indexOf(name) >= 0),
    names.join(', '));
});

test('kernel32 and ole32 are not among the imports', () => {
  const names = waaudio.peInfo(shipped()).imports.map((entry) => entry.name.toLowerCase());

  assert.strictEqual(names.indexOf('kernel32.dll'), -1);
  assert.strictEqual(names.indexOf('ole32.dll'), -1);
});

test('an x86 image is reported as the wrong machine', () => {
  const buffer = patched((bytes) => {
    const pe = bytes.readUInt32LE(0x3c);
    bytes.writeUInt16LE(0x014c, pe + 4);
  });

  const found = waaudio.problems(waaudio.peInfo(buffer));

  assert.ok(found.some((line) => /ARMNT/.test(line)), found.join('\n'));
});

test('a module the phone is not known to ship is reported', () => {
  const buffer = patched((bytes) => {
    // `ole32.dll` and `USER32.dll` are eight bytes like the API set name's start,
    // so the name changes without moving anything: enough for a guard that reads
    // the import table.
    const at = bytes.indexOf('api-ms-win-core-com', 'latin1');
    assert.ok(at > 0, 'the COM API set must be imported');
    bytes.write('USER32.dl', at, 'latin1');
  });

  const found = waaudio.problems(waaudio.peInfo(buffer));

  assert.ok(found.some((line) => /USER32\.dl/.test(line)), found.join('\n'));
});

test('a DLL without the probe exports is reported', () => {
  const buffer = patched((bytes) => {
    const at = bytes.indexOf('WaAudioProbe\0', 'latin1');
    assert.ok(at > 0, 'the export name must be in the image');
    bytes.write('WaAudioProbX', at, 'latin1');
  });

  const found = waaudio.problems(waaudio.peInfo(buffer));

  assert.ok(found.some((line) => /WaAudioProbe/.test(line)), found.join('\n'));
});

test('a file that is not a PE is not read as one', () => {
  assert.strictEqual(waaudio.peInfo(Buffer.from('this is not a PE, really')), null);
  assert.deepStrictEqual(waaudio.problems(null), ['the file is not a readable PE']);
});
