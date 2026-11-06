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

test('the shipped DLL imports MFPlat and nothing the phone is not known to ship', () => {
  const names = waaudio.peInfo(shipped()).imports.map((entry) => entry.name.toLowerCase());

  assert.ok(names.indexOf('mfplat.dll') >= 0, names.join(', '));
  assert.ok(names.every((name) => /^api-ms-win-/.test(name) || waaudio.ALLOWED.indexOf(name) >= 0),
    names.join(', '));
});

test('the Media Foundation imports are the five functions the phone exports', () => {
  const info = waaudio.peInfo(shipped());
  const mfplat = info.imports.filter((entry) => entry.name.toLowerCase() === 'mfplat.dll');

  assert.strictEqual(mfplat.length, 1);
  assert.deepStrictEqual(mfplat[0].functions.slice().sort(), waaudio.MFPLAT.slice().sort());
});

test('kernel32 and ole32 are not among the imports', () => {
  const names = waaudio.peInfo(shipped()).imports.map((entry) => entry.name.toLowerCase());

  assert.strictEqual(names.indexOf('kernel32.dll'), -1);
  assert.strictEqual(names.indexOf('ole32.dll'), -1);
});

test('the source reader and the enumerator are not imported any more', () => {
  const info = waaudio.peInfo(shipped());
  const functions = [];
  for (const entry of info.imports) {
    for (const name of entry.functions || []) functions.push(name);
  }

  assert.strictEqual(functions.indexOf('MFEnumDeviceSources'), -1);
  assert.strictEqual(functions.indexOf('MFCreateSourceReaderFromMediaSource'), -1);
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
    // `MFPlat.dll` and `USER32.dll` are ten bytes: the name changes without
    // moving anything, which is enough for a guard that reads the import table.
    const at = bytes.indexOf('MFPlat.dll', 'latin1');
    assert.ok(at > 0, 'the Media Foundation module must be imported');
    bytes.write('USER32.dll', at, 'latin1');
  });

  const found = waaudio.problems(waaudio.peInfo(buffer));

  assert.ok(found.some((line) => /USER32\.dll/.test(line)), found.join('\n'));
});

test('a function the phone does not export from MFPlat is reported', () => {
  const buffer = patched((bytes) => {
    // The enumerator is the real answer the phone gave; this
    // puts the name back into the table to prove the guard would catch it.
    const at = bytes.indexOf('MFCreateSample\0', 'latin1');
    assert.ok(at > 0, 'MFCreateSample must be in the image');
    bytes.write('MFEnumDeviceS', at, 'latin1');
  });

  const found = waaudio.problems(waaudio.peInfo(buffer));

  assert.ok(found.some((line) => /MFPlat/.test(line)), found.join('\n'));
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
