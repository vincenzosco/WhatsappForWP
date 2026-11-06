#!/usr/bin/env node
/**
 * tools/check-waudio.js
 *
 * Guard for the second native DLL the phone loads: `WhatsappApp/waaudio.dll`,
 * the Media Foundation probe of `tools/audio/waaudio.c`.
 *
 * Why it exists: this image is the first one in this repository that imports
 * something other than an API set. `MFPlat.DLL` is the phone's own Media
 * Foundation module, and the probe exists because whether the phone resolves a
 * given function inside it is not a question the SDK answers. On 6 November 2026
 * the answer was `0x8007007F` for `MFEnumDeviceSources`, and the fix was to
 * import only what `WhatsAppNative.dll` proves the device exports. A guard cannot
 * run the loader - only the device can - but it can keep the decided things
 * decided:
 *
 *  1. the machine is ARMNT, because an image built for another machine is not
 *     even a candidate;
 *  2. no `KERNEL32.dll`, and no console or argv function: that is the desktop
 *     surface, and the 0x8007007E of 4-5 November 2026 came from it. `MFPlat.dll`
 *     is allowed and named, so the list stays an allowlist;
 *  3. the Media Foundation imports are exactly the five functions the phone is
 *     proven to export - a sixth name is how 0x8007007F happens;
 *  4. the two exported entry points are the ones the managed side calls.
 *
 * `APPCONTAINER` is not a rule, for the same reason as in `check-waopus.js`: the
 * modules that load on this phone have the bit clear and the one that was
 * refused had it set.
 */
'use strict';

const fs = require('fs');
const path = require('path');

const DLL = path.join(__dirname, '..', 'WhatsappApp', 'waaudio.dll');

const ARMNT = 0x01c4;

/** The desktop CRT start-up, which this DLL has no use for. */
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

/** Modules the phone ships for these calls. Everything else must be an API set. */
const ALLOWED = ['mfplat.dll'];

/**
 * The Media Foundation functions this phone's MFPlat.DLL is proven to export:
 * they are the ones `WhatsAppNative.dll` imports, read from the module itself.
 */
const MFPLAT = [
  'MFStartup',
  'MFShutdown',
  'MFCreateAttributes',
  'MFCreateMediaType',
  'MFCreateSample'
];

const EXPORTS = ['WaAudioProbe', 'WaAudioProbeVersion'];

/** The PE of a buffer, or null when the buffer is not a readable PE. */
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
      const thunkRva = buffer.readUInt32LE(at) || buffer.readUInt32LE(at + 16);
      if (!nameRva) break;

      const nameAt = offset(nameRva);
      if (nameAt === null) break;
      const end = buffer.indexOf(0, nameAt);

      const functions = [];
      const thunkAt = offset(thunkRva);
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
      const namesAt = offset(buffer.readUInt32LE(at + 32));
      if (namesAt !== null) {
        for (let i = 0; i < count; i++) {
          const nameAt = offset(buffer.readUInt32LE(namesAt + i * 4));
          if (nameAt === null) continue;
          const end = buffer.indexOf(0, nameAt);
          exported.push(buffer.toString('latin1', nameAt, end));
        }
      }
    }
  }

  return { machine, dllCharacteristics, imports, exports: exported };
}

/** What is wrong with this image for the phone: empty when it is a candidate. */
function problems(info) {
  if (!info) return ['the file is not a readable PE'];

  const found = [];

  if (info.machine !== ARMNT) {
    found.push('machine 0x' + info.machine.toString(16) +
      ': the phone is ARMv7 and loads only ARMNT (0x1c4)');
  }

  if (info.imports.some((entry) => entry.name.toLowerCase() === 'kernel32.dll')) {
    found.push('imports KERNEL32.dll: that is the desktop surface, and the load failure ' +
      'of 4-5 November 2026 came from it');
  }

  const foreign = info.imports.filter((entry) => {
    const name = entry.name.toLowerCase();
    return name !== 'kernel32.dll' && !/^api-ms-win-/.test(name) && ALLOWED.indexOf(name) < 0;
  });

  if (foreign.length) {
    found.push('imports ' + foreign.map((entry) => entry.name).join(', ') +
      ': the phone is not known to ship that module, and the package does not');
  }

  const mfplat = info.imports.filter((entry) => entry.name.toLowerCase() === 'mfplat.dll');
  for (const entry of mfplat) {
    const unknown = (entry.functions || []).filter((name) => MFPLAT.indexOf(name) < 0);
    if (unknown.length) {
      found.push('imports ' + unknown.join(', ') + ' from MFPlat: the phone exports seven ' +
        'functions from that module and these are not among them, which is the 0x8007007F ' +
        'of 6 November 2026');
    }
  }

  const console = [];
  for (const entry of info.imports) {
    for (const name of entry.functions || []) {
      if (CONSOLE.indexOf(name) >= 0) console.push(name);
    }
  }

  if (console.length) {
    found.push('imports console and argv functions (' + console.join(', ') +
      '): that is the desktop CRT start-up, and this DLL prints nothing');
  }

  const names = new Set(info.exports);
  const missing = EXPORTS.filter((name) => !names.has(name));
  if (missing.length) {
    found.push('does not export ' + missing.join(', ') +
      ': the managed side would get EntryPointNotFoundException');
  }

  return found;
}

function main() {
  let buffer;
  try {
    buffer = fs.readFileSync(DLL);
  } catch (error) {
    console.log('missing ' + DLL + ': the build has no waaudio.dll to ship.');
    process.exit(1);
    return;
  }

  const info = peInfo(buffer);
  const found = problems(info);

  if (found.length) {
    console.log(found.join('\n'));
    console.log('\n' + found.length + ' problem(s) in waaudio.dll.');
    process.exit(1);
    return;
  }

  console.log('OK: waaudio.dll is ARMNT, imports ' +
    info.imports.map((entry) => entry.name).join(', ') +
    ', and exports ' + EXPORTS.join(' and ') + '.');
}

if (require.main === module) main();

module.exports = { peInfo, problems, DLL, EXPORTS, ALLOWED, MFPLAT };
