#!/usr/bin/env node
/**
 * tools/check-waopus.js
 *
 * Guard for the native DLL the phone loads for Opus: `WhatsappApp/waopus.dll`.
 *
 * Perche' esiste: il 4 novembre 2026 il telefono ha risposto
 * `Unable to load DLL 'waopus.dll'` (0x8007007E) per un file che era nel
 * pacchetto installato, firmato e con i soli import di KERNEL32. La causa non
 * era nel codice C# e non era nemmeno nel file: era un bit dell'header. Un
 * processo dentro un app container carica solo immagini che dichiarano
 * `IMAGE_DLLCHARACTERISTICS_APPCONTAINER`; senza quel bit il loader risponde
 * "modulo non trovato" - la stessa risposta di un file assente - e nessuna riga
 * di log dice il perche'.
 *
 * Il bit lo mette `/APPCONTAINER` nella riga di link di
 * `tools/build-opus-arm.bat`, ed e' una parola che si puo' perdere in una
 * ricostruzione senza che niente si rompa qui: la build passa, il pacchetto si
 * crea, e il guasto ricompare solo sul telefono, al primo vocale. Questo guard
 * legge il PE committato e lo dice subito.
 *
 * Regole:
 *  1. macchina ARMNT (0x1c4): il telefono e' ARMv7, e un'immagine x86 o ARMv4
 *     non si carica;
 *  2. `IMAGE_DLLCHARACTERISTICS_APPCONTAINER` (0x1000) acceso;
 *  3. import solo da KERNEL32.dll: una DLL che importa il CRT
 *     (`MSVCR120.dll`, `api-ms-win-crt-*`) non trova quelle funzioni in un app
 *     container, ed e' il secondo modo di finire in 0x8007007E;
 *  4. le sette funzioni che `VoiceNoteEncoder` e `VoiceNoteDecoder` chiamano
 *     devono essere esportate: un DLL ricostruito senza il wrapper e' un file
 *     valido che risponde "entry point not found".
 */
'use strict';

const fs = require('fs');
const path = require('path');

const DLL = path.join(__dirname, '..', 'WhatsappApp', 'waopus.dll');

const ARMNT = 0x01c4;
const APPCONTAINER = 0x1000;

const EXPORTS = [
  'wa_opus_api',
  'wa_opus_create',
  'wa_opus_frame',
  'wa_opus_destroy',
  'wa_opus_decode_create',
  'wa_opus_decode',
  'wa_opus_decode_destroy'
];

/** Il PE di un buffer, o null se non e' un PE leggibile. */
function peInfo(buffer) {
  if (!buffer || buffer.length < 0x40) return null;
  if (buffer.readUInt16LE(0) !== 0x5a4d) return null;

  const pe = buffer.readUInt32LE(0x3c);
  if (pe + 24 > buffer.length) return null;
  if (buffer.toString('latin1', pe, pe + 4) !== 'PE\0\0') return null;

  const machine = buffer.readUInt16LE(pe + 4);
  const sections = buffer.readUInt16LE(pe + 6);
  const optionalSize = buffer.readUInt16LE(pe + 20);
  const optional = pe + 24;

  if (buffer.readUInt16LE(optional) !== 0x10b) return null;

  const dllCharacteristics = buffer.readUInt16LE(optional + 70);
  const directories = optional + 96;

  const table = optional + optionalSize;
  const blocks = [];
  for (let i = 0; i < sections; i++) {
    const at = table + i * 40;
    if (at + 40 > buffer.length) return null;
    blocks.push({
      virtualAddress: buffer.readUInt32LE(at + 12),
      virtualSize: buffer.readUInt32LE(at + 8),
      rawPointer: buffer.readUInt32LE(at + 20),
      rawSize: buffer.readUInt32LE(at + 16)
    });
  }

  const offset = (rva) => {
    for (const block of blocks) {
      const size = Math.max(block.virtualSize, block.rawSize);
      if (rva >= block.virtualAddress && rva < block.virtualAddress + size) {
        return block.rawPointer + (rva - block.virtualAddress);
      }
    }
    return null;
  };

  const imports = [];
  const importRva = buffer.readUInt32LE(directories + 8);
  if (importRva) {
    let at = offset(importRva);
    while (at && at + 20 <= buffer.length) {
      const nameRva = buffer.readUInt32LE(at + 12);
      if (!nameRva) break;
      const nameAt = offset(nameRva);
      if (nameAt === null) break;
      const end = buffer.indexOf(0, nameAt);
      imports.push({ name: buffer.toString('latin1', nameAt, end), at: nameAt });
      at += 20;
    }
  }

  const exported = [];
  const exportRva = buffer.readUInt32LE(directories);
  if (exportRva) {
    const at = offset(exportRva);
    if (at && at + 40 <= buffer.length) {
      const count = buffer.readUInt32LE(at + 24);
      const namesRva = buffer.readUInt32LE(at + 32);
      const namesAt = offset(namesRva);
      if (namesAt !== null) {
        for (let i = 0; i < count; i++) {
          const nameAt = offset(buffer.readUInt32LE(namesAt + i * 4));
          if (nameAt === null) continue;
          const end = buffer.indexOf(0, nameAt);
          exported.push({ name: buffer.toString('latin1', nameAt, end), at: nameAt });
        }
      }
    }
  }

  return {
    machine,
    dllCharacteristics,
    imports,
    exports: exported,
    offsets: { machine: pe + 4, dllCharacteristics: optional + 70 }
  };
}

/** I problemi di una DLL per Windows Phone 8.1: vuoto quando va bene. */
function problems(info) {
  if (!info) return ['il file non e un PE leggibile'];

  const found = [];

  if (info.machine !== ARMNT) {
    found.push(`macchina 0x${info.machine.toString(16)}: il telefono e ARMv7 e carica solo ARMNT (0x1c4)`);
  }

  if (!(info.dllCharacteristics & APPCONTAINER)) {
    found.push('manca /APPCONTAINER in DllCharacteristics: un processo in app container rifiuta ' +
      'l immagine con 0x8007007E anche quando il file e nel pacchetto');
  }

  const others = info.imports.filter((entry) => entry.name.toLowerCase() !== 'kernel32.dll');
  if (others.length) {
    found.push('importa ' + others.map((entry) => entry.name).join(', ') +
      ': in un app container quelle funzioni non ci sono, e il loader risponde 0x8007007E');
  }

  const names = new Set(info.exports.map((entry) => entry.name));
  const missing = EXPORTS.filter((name) => !names.has(name));
  if (missing.length) {
    found.push('non esporta ' + missing.join(', ') + ': e la DLL sbagliata o il wrapper non e stato compilato');
  }

  return found;
}

function main() {
  let buffer;
  try {
    buffer = fs.readFileSync(DLL);
  } catch (error) {
    console.log(`manca ${DLL}: la build non ha un waopus.dll da spedire.`);
    process.exit(1);
    return;
  }

  const info = peInfo(buffer);
  const found = problems(info);

  if (found.length) {
    console.log(found.join('\n'));
    console.log(`\n${found.length} problema(i) in waopus.dll.`);
    process.exit(1);
    return;
  }

  const imports = info.imports.map((entry) => entry.name).join(', ');
  console.log(`OK: waopus.dll e ARMNT, dichiara /APPCONTAINER, importa solo ${imports}, ` +
    `ed esporta le ${EXPORTS.length} funzioni del codec.`);
}

if (require.main === module) main();

module.exports = { peInfo, problems, EXPORTS, ARMNT, APPCONTAINER };
