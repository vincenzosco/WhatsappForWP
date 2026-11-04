'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const waopus = require('../check-waopus');

const DLL = path.join(__dirname, '..', '..', 'WhatsappApp', 'waopus.dll');

/** Il file committato, letto una volta: i test non lo modificano mai su disco. */
function shipped() {
  return fs.readFileSync(DLL);
}

/** Una copia con un byte cambiato, per provare che il guard lo vede. */
function patched(change) {
  const buffer = Buffer.from(shipped());
  change(buffer, waopus.peInfo(buffer));

  return buffer;
}

test('la DLL spedita si carica in un app container', () => {
  const info = waopus.peInfo(shipped());

  assert.ok(info, 'il file deve essere un PE leggibile');
  assert.deepStrictEqual(waopus.problems(info), []);
});

test('la DLL spedita e ARMNT, per il telefono ARMv7', () => {
  const info = waopus.peInfo(shipped());

  assert.strictEqual(info.machine, 0x01c4);
  assert.ok(info.dllCharacteristics & 0x1000, 'APPCONTAINER acceso');
});

test('la DLL spedita importa solo KERNEL32', () => {
  const names = waopus.peInfo(shipped()).imports.map((entry) => entry.name.toLowerCase());

  assert.deepStrictEqual(names, ['kernel32.dll']);
});

test('un immagine x86 si segnala come macchina sbagliata', () => {
  const buffer = patched((bytes, info) => bytes.writeUInt16LE(0x014c, info.offsets.machine));
  const found = waopus.problems(waopus.peInfo(buffer));

  assert.strictEqual(found.length, 1);
  assert.match(found[0], /ARMNT/);
});

test('un immagine senza APPCONTAINER si segnala, ed e il 0x8007007E', () => {
  const buffer = patched((bytes, info) => {
    const value = bytes.readUInt16LE(info.offsets.dllCharacteristics) & ~0x1000;
    bytes.writeUInt16LE(value, info.offsets.dllCharacteristics);
  });
  const found = waopus.problems(waopus.peInfo(buffer));

  assert.strictEqual(found.length, 1);
  assert.match(found[0], /APPCONTAINER/);
  assert.match(found[0], /0x8007007E/);
});

test('un import del CRT si segnala: quelle funzioni non ci sono', () => {
  const buffer = patched((bytes, info) => {
    const entry = info.imports[0];
    bytes.write('MSVCR120.dll', entry.at, 'latin1');
  });
  const found = waopus.problems(waopus.peInfo(buffer));

  assert.strictEqual(found.length, 1);
  assert.match(found[0], /MSVCR120\.dll/);
});

test('una DLL senza le funzioni del codec si segnala', () => {
  const buffer = patched((bytes, info) => {
    const entry = info.exports.find((candidate) => candidate.name === 'wa_opus_frame');
    bytes.write('wa_opus_framX', entry.at, 'latin1');
  });
  const found = waopus.problems(waopus.peInfo(buffer));

  assert.strictEqual(found.length, 1);
  assert.match(found[0], /wa_opus_frame/);
});

test('un file che non e un PE non si legge come uno', () => {
  assert.strictEqual(waopus.peInfo(Buffer.from('questo non e un PE, davvero')), null);
  assert.deepStrictEqual(waopus.problems(null), ['il file non e un PE leggibile']);
});
