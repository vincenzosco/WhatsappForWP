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

test('la DLL spedita passa: nessun KERNEL32, nessuna console', () => {
  const found = waopus.problems(waopus.peInfo(shipped()));

  assert.deepStrictEqual(found, []);
});

test('i moduli importati dalla DLL spedita sono i due api set del runtime', () => {
  const names = waopus.peInfo(shipped()).imports.map((entry) => entry.name.toLowerCase());

  assert.deepStrictEqual(names,
    ['api-ms-win-core-heap-l1-2-0.dll', 'api-ms-win-core-errorhandling-l1-1-1.dll']);
});

test('ogni funzione importata e una che WhatsAppNative importa dallo stesso modulo', (t) => {
  const nativePath = path.join(__dirname, '..', '..', '.tools', 'xap', 'WhatsAppNative.dll');

  if (!fs.existsSync(nativePath)) {
    // Il pacchetto estratto e in .tools, che non e nel repository.
    t.skip('serve .tools/xap/WhatsAppNative.dll');
    return;
  }

  const native = waopus.peInfo(fs.readFileSync(nativePath));
  const known = new Set();
  for (const entry of native.imports) {
    for (const name of entry.functions || []) known.add(entry.name.toLowerCase() + '!' + name);
  }

  for (const entry of waopus.peInfo(shipped()).imports) {
    for (const name of entry.functions || []) {
      assert.ok(known.has(entry.name.toLowerCase() + '!' + name),
        'il telefono non risolve ' + entry.name + '!' + name);
    }
  }
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
