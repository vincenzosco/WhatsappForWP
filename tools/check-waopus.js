#!/usr/bin/env node
/**
 * tools/check-waopus.js
 *
 * Guard for the native DLL the phone loads for Opus: `WhatsappApp/waopus.dll`.
 *
 * Perche' esiste: il 4 novembre 2026 il telefono ha risposto
 * `Unable to load DLL 'waopus.dll'` (0x8007007E) per un file che era nel
 * pacchetto installato. Il primo sospetto fu il bit app container dell'header,
 * e `/APPCONTAINER` entro' nella riga di link; il 5 novembre il telefono rispose
 * lo stesso 0x8007007E, con l'immagine installata identica a quella del repo.
 *
 * Cio' che il guard sorveglia da allora e' il fatto che quella spiegazione non
 * regge: i tre moduli ARM che su quel telefono si caricano (`WhatsAppNative.dll`,
 * `WhatsAppProxyStub.dll`, `dbghelp.dll`) hanno `dllCharacteristics` 0x0140,
 * cioe' senza il bit, e nessuno di loro nomina `KERNEL32.dll`: importano
 * `api-ms-win-core-*`. La nostra lo nomina, e quello e' il difetto - l'immagine
 * e' costruita sulla superficie desktop di ARM, e il loader risolve i moduli
 * prima dei simboli, percio' risponde "modulo non trovato" (0x8007007E) e non
 * "entry point non trovato" (0x8007007F).
 *
 * Il guard legge il PE committato: la macchina, i moduli importati, le funzioni
 * di console che rivelano il CRT desktop, e le sette funzioni esportate.
 *
 * Regole:
 * 1. macchina ARMNT (0x1c4): il telefono e' ARMv7, e un'immagine x86 o ARMv4
 * non si carica;
 * 2. nessun import da `KERNEL32.dll`: e' il nome desktop, che nessuno dei
 * moduli caricabili nomina. Non e' una preferenza di stile - e' la
 * dipendenza che il loader del telefono non risolve;
 * 3. ogni dipendenza da un API set `api-ms-win-*`: il pacchetto non spedisce un
 * CRT, e ogni altra DLL sarebbe un'altra cosa da far trovare al telefono;
 * 4. nessuna funzione di console o argv (`WriteConsoleW`, `SetStdHandle`,
 * `GetCommandLineW`, `GetEnvironmentStringsW`,.): sono l'inizio di stdio,
 * locale e argv del CRT desktop, e una DLL che non stampa nulla non ne ha
 * bisogno;
 * 5. le sette funzioni che `VoiceNoteEncoder` e `VoiceNoteDecoder` chiamano
 * devono essere esportate: un DLL ricostruito senza il wrapper e' un file
 * valido che risponde "entry point not found".
 *
 * `IMAGE_DLLCHARACTERISTICS_APPCONTAINER` non e' una regola, in nessuna delle
 * due direzioni: i moduli che si caricano non lo dichiarano, e quello che viene
 * rifiutato lo dichiara. Il bit si legge e si scrive, ma non decide.
 */
'use strict';

const fs = require('fs');
const path = require('path');

const DLL = path.join(__dirname, '..', 'WhatsappApp', 'waopus.dll');

const ARMNT = 0x01c4;

/**
 * L'inizio di stdio, della tabella locale e di argv/envp del CRT desktop: sono
 * le funzioni che una DLL di sole funzioni pure non usa, e la loro presenza
 * nell'import table dice da quale CRT e' stata linkata.
 */
const CONSOLE = [
  'WriteConsoleW',
  'GetConsoleCP',
  'GetConsoleMode',
  'SetStdHandle',
  'GetStdHandle',
  'GetCommandLineA',
  'GetCommandLineW',
  'GetEnvironmentStringsW',
  'FreeEnvironmentStringsW'
];

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
  };      const imports = [];
  const importRva = buffer.readUInt32LE(directories + 8);
  if (importRva) {
    let at = offset(importRva);
    while (at && at + 20 <= buffer.length) {
      const nameRva = buffer.readUInt32LE(at + 12);
      const thunkRva = buffer.readUInt32LE(at) || buffer.readUInt32LE(at + 16);
      if (!nameRva) break;
      const nameAt = offset(nameRva);
      if (nameAt === null) break;
      const end = buffer.indexOf(0, nameAt);

      // Le funzioni, non solo il modulo: e' una di queste a dire se l'immagine
      // porta l'inizializzazione del CRT desktop. I thunk sono a 4 byte perche'
      // questo e' un PE32, come controlla la magic piu' sopra.
      const functions = [];
      let thunkAt = offset(thunkRva);
      if (thunkAt !== null) {
        for (let i = 0; i < 512; i++) {
          const value = buffer.readUInt32LE(thunkAt + i * 4);
          if (!value) break;

          if (value & 0x80000000) {
            functions.push('#' + (value & 0x7fffffff));
            continue;
          }

          const hintAt = offset(value);
          if (hintAt === null) break;

          const nameEnd = buffer.indexOf(0, hintAt + 2);
          functions.push(buffer.toString('latin1', hintAt + 2, nameEnd));
        }
      }

      imports.push({
        name: buffer.toString('latin1', nameAt, end),
        at: nameAt,
        functions: functions
      });
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

  if (info.imports.some((entry) => entry.name.toLowerCase() === 'kernel32.dll')) {
    found.push('importa KERNEL32.dll: e il nome desktop, e i tre moduli ARM che il telefono carica ' +
      'non lo nominano mai - importano api-ms-win-core-*. Il loader risolve i moduli prima dei ' +
      'simboli, quindi la risposta e 0x8007007E, non 0x8007007F');
  }

  const foreign = info.imports.filter((entry) =>
    entry.name.toLowerCase() !== 'kernel32.dll' && !/^api-ms-win-/i.test(entry.name));
  if (foreign.length) {
    found.push('importa ' + foreign.map((entry) => entry.name).join(', ') +
      ': il pacchetto non spedisce un CRT e ogni modulo fuori dagli api set e un altra ' +
      'dipendenza da far trovare al telefono');
  }

  const console = [];
  for (const entry of info.imports) {
    for (const name of entry.functions || []) {
      if (CONSOLE.indexOf(name) >= 0) console.push(name);
    }
  }

  if (console.length) {
    found.push('importa funzioni di console e argv (' + console.join(', ') +
      '): sono l inizializzazione del CRT desktop, e una DLL che non stampa nulla non le usa');
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
  console.log(`OK: waopus.dll e ARMNT, importa solo ${imports}, ` +
    `ed esporta le ${EXPORTS.length} funzioni del codec.`);
}

if (require.main === module) main();

module.exports = { peInfo, problems, EXPORTS, ARMNT, CONSOLE };
