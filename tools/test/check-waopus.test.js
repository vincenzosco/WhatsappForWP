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

test('la DLL spedita e ARMNT, per il telefono ARMv7', () => {
  const info = waopus.peInfo(shipped());

  assert.ok(info, 'il file deve essere un PE leggibile');
  assert.strictEqual(info.machine, 0x01c4);
});

test('la DLL spedita e ancora quella del 5 novembre, e il guard lo dice', () => {
  const found = waopus.problems(waopus.peInfo(shipped()));

  // Sei il telefono la rifiuta, la guardia non puo dirla a posto: la DLL
  // committata importa KERNEL32 e porta il CRT desktop, che e quanto il
  // confronto con i tre moduli caricabili ha messo in chiaro.
  assert.ok(found.length > 0, 'un immagine che il telefono rifiuta non passa');
  assert.ok(found.some((line) => /KERNEL32\.dll/.test(line)), found.join('\n'));
  assert.ok(found.some((line) => /console/.test(line)), found.join('\n'));
});

test('i moduli importati dalla DLL spedita sono api set, tranne KERNEL32', () => {
  const names = waopus.peInfo(shipped()).imports.map((entry) => entry.name.toLowerCase());

  assert.deepStrictEqual(names.filter((name) => !/^api-ms-win-/.test(name)), ['kernel32.dll']);
});

test('le funzioni che il CRT desktop porta con se si leggono nell import table', () => {
  const functions = waopus.peInfo(shipped()).imports
    .reduce((all, entry) => all.concat(entry.functions || []), []);

  assert.ok(functions.indexOf('WriteConsoleW') >= 0, 'la console del CRT');
  assert.ok(functions.indexOf('wa_opus_frame') < 0, 'le funzioni del codec sono export, non import');
});

test('un immagine x86 si segnala come macchina sbagliata', () => {
  const buffer = patched((bytes, info) => bytes.writeUInt16LE(0x014c, info.offsets.machine));
  const found = waopus.problems(waopus.peInfo(buffer));

  assert.ok(found.some((line) => /ARMNT/.test(line)), found.join('\n'));
});

test('un modulo fuori dagli api set si segnala', () => {
  const buffer = patched((bytes, info) => {
    // "kernel32.dll" e "USER32.dll\0\0" sono dodici byte: il nome cambia senza
    // spostare niente, che e quanto basta a una guardia che legge il PE.
    bytes.write('USER32.dll\0\0', info.imports[0].at, 'latin1');
  });
  const found = waopus.problems(waopus.peInfo(buffer));

  assert.ok(found.some((line) => /USER32\.dll/.test(line)), found.join('\n'));
});

test('una DLL senza le funzioni del codec si segnala', () => {
  const buffer = patched((bytes, info) => {
    const entry = info.exports.find((candidate) => candidate.name === 'wa_opus_frame');
    bytes.write('wa_opus_framX', entry.at, 'latin1');
  });
  const found = waopus.problems(waopus.peInfo(buffer));

  assert.ok(found.some((line) => /wa_opus_frame/.test(line)), found.join('\n'));
});

test('un file che non e un PE non si legge come uno', () => {
  assert.strictEqual(waopus.peInfo(Buffer.from('questo non e un PE, davvero')), null);
  assert.deepStrictEqual(waopus.problems(null), ['il file non e un PE leggibile']);
});
