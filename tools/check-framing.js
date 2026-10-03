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
 * The contract lives in one file, `WhatsappApp/Services/FrameCodec.cs`: it used
 * to be scattered inside `CommunicationService.cs`, mixed in with the socket,
 * the cipher and the dispatcher. This guard follows the code, so the byte order
 * is pinned where the framing actually is. Rule A scans that one file on
 * purpose: the app has other `DataReader`/`DataWriter` uses (files, images,
 * incoming media), and the byte order means nothing for those - only the socket
 * frame contract needs it.
 *
 * Rules:
 *   A. every `new DataReader(...)` / `new DataWriter(...)` in
 *      `WhatsappApp/Services/FrameCodec.cs` sets
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
const FRAME_CODEC_REL = 'WhatsappApp/Services/FrameCodec.cs';
const FRAME_CODEC = path.join(ROOT, FRAME_CODEC_REL);
const CRYPTO = path.join(ROOT, 'WhatsappBridge', 'crypto-helper.js');
const SERVER = path.join(ROOT, 'WhatsappBridge', 'server.js');

const BYTE_ORDER = /ByteOrder\s*=\s*ByteOrder\.LittleEndian/;
const CONSTRUCTION = /new\s+(DataReader|DataWriter)\s*\(/;

/**
 * The problems of a set of sources: empty when the socket frame contract is
 * intact. `input.frameCodec` is the text of FrameCodec.cs.
 */
function problemsFor(input) {
  const problems = [];

  // -------------------------------------------------------------------------
  // A. the byte order of every socket reader and writer
  // -------------------------------------------------------------------------
  const lines = input.frameCodec.split(/\r?\n/);
  let sites = 0;
  lines.forEach((line, i) => {
    if (!CONSTRUCTION.test(line)) return;
    sites++;
    const nearby = lines.slice(i, i + 5).join('\n');
    if (!BYTE_ORDER.test(nearby)) {
      problems.push(FRAME_CODEC_REL + ':' + (i + 1) + ': ' + line.trim() +
        ' without "ByteOrder = ByteOrder.LittleEndian" nearby - the WinRT default ' +
        'byte order turns the frame length into a number that does not exist');
    }
  });
  if (sites === 0) {
    problems.push(FRAME_CODEC_REL + ': no DataReader or DataWriter found ' +
      '(did the framing move? then this guard must move too)');
  }

  // -------------------------------------------------------------------------
  // B. the adapter side of the same contract
  // -------------------------------------------------------------------------
  if (!/writeUInt32LE/.test(input.crypto)) {
    problems.push('WhatsappBridge/crypto-helper.js: buildFrame no longer writes the ' +
      'frame length with writeUInt32LE');
  }
  if (!/readUInt32LE/.test(input.server)) {
    problems.push('WhatsappBridge/server.js: the frame reader no longer reads the ' +
      'frame length with readUInt32LE');
  }

  // -------------------------------------------------------------------------
  // C. one ceiling, two sides
  // -------------------------------------------------------------------------
  function ceiling(text, pattern, where) {
    const m = pattern.exec(text);
    if (!m) {
      problems.push(where + ': not found (the ceiling moved or was renamed)');
      return null;
    }
    return m[1].replace(/\s+/g, '');
  }
  const appCeiling = ceiling(input.frameCodec, /MaxFrameLength\s*=\s*([^;]+);/, 'MaxFrameLength');
  const adapterCeiling = ceiling(input.server, /MAX_FRAME_LENGTH\s*=\s*([^;]+);/, 'MAX_FRAME_LENGTH');
  if (appCeiling && adapterCeiling && appCeiling !== adapterCeiling) {
    problems.push('the frame ceiling differs: MaxFrameLength = ' + appCeiling +
      ' in the app, MAX_FRAME_LENGTH = ' + adapterCeiling + ' in the adapter - one ' +
      'side will drop what the other sends');
  }

  return { problems, sites, appCeiling };
}

function main() {
  const input = {
    frameCodec: fs.readFileSync(FRAME_CODEC, 'utf8'),
    crypto: fs.readFileSync(CRYPTO, 'utf8'),
    server: fs.readFileSync(SERVER, 'utf8'),
  };
  const { problems, sites, appCeiling } = problemsFor(input);

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log('\n' + problems.length + ' problem(s).');
    process.exit(1);
  }
  console.log('OK: ' + sites + ' socket reader/writer site(s) in ' + FRAME_CODEC_REL +
    ', all pinned to little-endian; the adapter reads and writes the length ' +
    'little-endian, one frame ceiling (' + appCeiling + ').');
}

if (require.main === module) main();

module.exports = { problemsFor, FRAME_CODEC_REL };
