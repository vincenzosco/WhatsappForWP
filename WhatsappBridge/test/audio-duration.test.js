'use strict';

const test = require('node:test');
const assert = require('node:assert');

const { durationSecondsOf, formatDuration } = require('../audio-duration');

/** A page OggS with one segment, for the parser tests. */
function oggPage(granule, payload) {
  const segments = [payload.length];
  const header = Buffer.alloc(27 + segments.length);
  header.write('OggS', 0, 'ascii');
  header.writeUInt8(0, 4);
  header.writeUInt8(4, 5);
  // -1 is the granule of a page no packet ends on, and it is not a valid
  // unsigned value: it is written as all bits set, which is what Ogg means.
  header.writeBigUInt64LE(granule < 0 ? 0xffffffffffffffffn : BigInt(granule), 6);
  header.writeUInt32LE(1, 14);
  header.writeUInt32LE(0, 18);
  header.writeUInt8(segments.length, 26);
  header.writeUInt8(segments[0], 27);
  return Buffer.concat([header, payload]);
}

function opusHead(sampleRate) {
  const head = Buffer.alloc(19);
  head.write('OpusHead', 0, 'ascii');
  head.writeUInt8(1, 8);
  head.writeUInt8(2, 9);
  head.writeUInt16LE(312, 10);
  head.writeUInt32LE(sampleRate, 12);
  return head;
}

test('un Ogg/Opus prende la durata dalla granula finale', () => {
  const bytes = Buffer.concat([
    oggPage(0, opusHead(48000)),
    oggPage(480000, Buffer.from('audio'))
  ]);
  assert.strictEqual(durationSecondsOf(bytes, 'audio/ogg', 'voce.ogg'), 10);
});

test('una pagina non completata non conta', () => {
  const bytes = Buffer.concat([
    oggPage(0, opusHead(48000)),
    oggPage(480000, Buffer.from('audio')),
    oggPage(-1, Buffer.from('mezza'))
  ]);
  assert.strictEqual(durationSecondsOf(bytes, 'audio/ogg', 'voce.ogg'), 10);
});

test('un Ogg/Vorbis usa la frequenza del suo header', () => {
  const vorbis = Buffer.alloc(16);
  vorbis.write('\u0001vorbis', 0, 'ascii');
  vorbis.writeUInt32LE(44100, 12);
  const bytes = Buffer.concat([
    oggPage(0, vorbis),
    oggPage(441000, Buffer.from('audio'))
  ]);
  assert.strictEqual(durationSecondsOf(bytes, 'audio/ogg', 'voce.ogg'), 10);
});

test('un MP4/M4A prende la durata dal moov/mvhd', () => {
  const mvhd = Buffer.alloc(8 + 20);
  mvhd.writeUInt32BE(mvhd.length, 0);
  mvhd.write('mvhd', 4, 'ascii');
  mvhd.writeUInt8(0, 8);
  mvhd.writeUInt32BE(600, 20);
  mvhd.writeUInt32BE(6000, 24);
  const moov = Buffer.alloc(8);
  moov.writeUInt32BE(8 + mvhd.length, 0);
  moov.write('moov', 4, 'ascii');
  const ftyp = Buffer.alloc(8);
  ftyp.writeUInt32BE(8, 0);
  ftyp.write('ftyp', 4, 'ascii');
  assert.strictEqual(durationSecondsOf(Buffer.concat([ftyp, moov, mvhd]), 'audio/mp4', 'voce.m4a'), 10);
});

test('un MP3 conta i frame', () => {
  // MPEG1 Layer III, 128 kbit/s, 44100 Hz: 417 byte per frame, 1152 campioni.
  const frame = Buffer.alloc(417);
  frame.writeUInt8(0xff, 0);
  frame.writeUInt8(0xfb, 1);
  frame.writeUInt8(0x90, 2);
  frame.writeUInt8(0x00, 3);
  // 30 frame * 1152 / 44100 = 0.78 s -> 1
  const frames = [];
  for (let i = 0; i < 30; i++) frames.push(frame);
  assert.strictEqual(durationSecondsOf(Buffer.concat(frames), 'audio/mpeg', 'voce.mp3'), 1);
});

test('un solo frame non e una durata', () => {
  const frame = Buffer.alloc(417);
  frame.writeUInt8(0xff, 0);
  frame.writeUInt8(0xfb, 1);
  frame.writeUInt8(0x90, 2);
  frame.writeUInt8(0x00, 3);
  assert.strictEqual(durationSecondsOf(frame, 'audio/mpeg', 'voce.mp3'), null);
});

test('i byte che non sono audio non danno una durata', () => {
  assert.strictEqual(durationSecondsOf(Buffer.from('non e audio'), 'audio/ogg', 'x.ogg'), null);
  assert.strictEqual(durationSecondsOf(Buffer.alloc(0), 'audio/ogg', 'x.ogg'), null);
  assert.strictEqual(durationSecondsOf(null, 'audio/ogg', 'x.ogg'), null);
});

test('formatDuration scrive minuti e secondi', () => {
  assert.strictEqual(formatDuration(10), '0:10');
  assert.strictEqual(formatDuration(65), '1:05');
  assert.strictEqual(formatDuration(720), '12:00');
  assert.strictEqual(formatDuration(0), '0:00');
});
