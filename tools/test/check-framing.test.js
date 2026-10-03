'use strict';
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');
const fs = require('node:fs');

const guard = require('../check-framing');

const CRYPTO = 'function buildFrame(payload) { const h = Buffer.alloc(4); h.writeUInt32LE(payload.length, 0); }';
const SERVER = 'const MAX_FRAME_LENGTH = 8 * 1024 * 1024;\nconst n = buf.readUInt32LE(0);';
const CODEC = 'public const uint MaxFrameLength = 8 * 1024 * 1024;\n'
  + 'var reader = new DataReader(stream);\n'
  + 'reader.ByteOrder = ByteOrder.LittleEndian;';

/** Input with the codec text and the adapter halves the guard needs. */
function inputWith(frameCodec, crypto, server) {
  return {
    frameCodec: frameCodec === undefined ? CODEC : frameCodec,
    crypto: crypto === undefined ? CRYPTO : crypto,
    server: server === undefined ? SERVER : server,
  };
}

test('il codec con la byte order non e un problema', () => {
  assert.deepStrictEqual(guard.problemsFor(inputWith()).problems, []);
});

test('un lettore nel codec senza la byte order e un problema', () => {
  const codec = 'public const uint MaxFrameLength = 8 * 1024 * 1024;\n'
    + 'var reader = new DataReader(stream);\nreturn reader;';
  const { problems } = guard.problemsFor(inputWith(codec));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /ByteOrder\.LittleEndian/);
});

test('senza lettori nel codec la guardia dice che il framing si e spostato', () => {
  const { problems } = guard.problemsFor(inputWith('public const uint MaxFrameLength = 8 * 1024 * 1024;'));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /did the framing move/);
});

test('un tetto diverso sui due lati e un problema', () => {
  const { problems } = guard.problemsFor(inputWith(
    CODEC,
    CRYPTO,
    'const MAX_FRAME_LENGTH = 4 * 1024 * 1024;\nconst n = buf.readUInt32LE(0);',
  ));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /frame ceiling differs/);
});

test('i sorgenti del progetto seguono il contratto', () => {
  const projectRoot = path.join(__dirname, '..', '..');
  const input = inputWith(
    fs.readFileSync(path.join(projectRoot, 'WhatsappApp', 'Services', 'FrameCodec.cs'), 'utf8'),
    fs.readFileSync(path.join(projectRoot, 'WhatsappBridge', 'crypto-helper.js'), 'utf8'),
    fs.readFileSync(path.join(projectRoot, 'WhatsappBridge', 'server.js'), 'utf8'),
  );
  assert.deepStrictEqual(guard.problemsFor(input).problems, []);
});
