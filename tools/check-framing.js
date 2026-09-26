#!/usr/bin/env node
/**
 * tools/check-framing.js
 *
 * Guard for the TCP frame contract between the app and the adapter.
 *
 * Why it exists: on the device the app read a frame length of 553713664 where
 * the wire carried 289. 553713664 is 0x21010000, and 0x00000121 is 289: a
 * byte-swapped 32-bit read. The adapter writes the length with
 * `writeUInt32LE`; WinRT's DataReader/DataWriter are not little-endian by
 * default. Nothing else in this repo can catch that, because the C# has no test
 * host, the adapter suite runs Node on both ends, and each side is
 * self-consistent.
 *
 * Rules:
 *   A. every `new DataReader(...)` / `new DataWriter(...)` in
 *      WhatsappApp/Services/CommunicationService.cs sets
 *      `ByteOrder = ByteOrder.LittleEndian` within the next four lines;
 *   B. the adapter's two halves use the little-endian helpers -
 *      `writeUInt32LE` in crypto-helper.js, `readUInt32LE` in server.js;
 *   C. the frame ceiling is the same expression on both sides:
 *      `MaxFrameLength` in the app and `MAX_FRAME_LENGTH` in the adapter.
 *
 * Usage: node tools/check-framing.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP_SOCKET = path.join(ROOT, 'WhatsappApp', 'Services', 'CommunicationService.cs');
const CRYPTO = path.join(ROOT, 'WhatsappBridge', 'crypto-helper.js');
const SERVER = path.join(ROOT, 'WhatsappBridge', 'server.js');

const problems = [];
const read = (file) => fs.readFileSync(file, 'utf8');

// ---------------------------------------------------------------------------
// A. the byte order of every socket reader and writer
// ---------------------------------------------------------------------------
const appLines = read(APP_SOCKET).split(/\r?\n/);
let sites = 0;
appLines.forEach((line, i) => {
  if (!/new\s+(DataReader|DataWriter)\s*\(/.test(line)) return;
  sites++;
  const nearby = appLines.slice(i, i + 5).join('\n');
  if (!/ByteOrder\s*=\s*ByteOrder\.LittleEndian/.test(nearby)) {
    problems.push('WhatsappApp/Services/CommunicationService.cs:' + (i + 1) + ': ' +
      line.trim() + ' without "ByteOrder = ByteOrder.LittleEndian" nearby - the ' +
      'WinRT default byte order turns the frame length into a number that does ' +
      'not exist');
  }
});
if (sites === 0) {
  problems.push('WhatsappApp/Services/CommunicationService.cs: no DataReader or ' +
    'DataWriter found (did the framing move? then this guard must move too)');
}

// ---------------------------------------------------------------------------
// B. the adapter side of the same contract
// ---------------------------------------------------------------------------
if (!/writeUInt32LE/.test(read(CRYPTO))) {
  problems.push('WhatsappBridge/crypto-helper.js: buildFrame no longer writes the ' +
    'frame length with writeUInt32LE');
}
const serverText = read(SERVER);
if (!/readUInt32LE/.test(serverText)) {
  problems.push('WhatsappBridge/server.js: the frame reader no longer reads the ' +
    'frame length with readUInt32LE');
}

// ---------------------------------------------------------------------------
// C. one ceiling, two sides
// ---------------------------------------------------------------------------
function ceiling(text, pattern, where) {
  const m = pattern.exec(text);
  if (!m) {
    problems.push(where + ': not found (the ceiling moved or was renamed)');
    return null;
  }
  return m[1].replace(/\s+/g, '');
}
const appCeiling = ceiling(read(APP_SOCKET), /MaxFrameLength\s*=\s*([^;]+);/, 'MaxFrameLength');
const adapterCeiling = ceiling(serverText, /MAX_FRAME_LENGTH\s*=\s*([^;]+);/, 'MAX_FRAME_LENGTH');
if (appCeiling && adapterCeiling && appCeiling !== adapterCeiling) {
  problems.push('the frame ceiling differs: MaxFrameLength = ' + appCeiling +
    ' in the app, MAX_FRAME_LENGTH = ' + adapterCeiling + ' in the adapter - one ' +
    'side will drop what the other sends');
}

if (problems.length) {
  console.log(problems.join('\n'));
  console.log('\n' + problems.length + ' problem(s).');
  process.exit(1);
}

console.log('OK: ' + sites + ' socket reader/writer site(s) pinned to little-endian, ' +
  'the adapter reads and writes the length little-endian, one frame ceiling (' +
  appCeiling + ').');
