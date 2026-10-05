'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const keyStore = require('../key-store');

function tempDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'key-store-'));
}

test('resolve preferisce BRIDGE_KEY nell ambiente al file', () => {
  const dir = tempDir();
  const file = path.join(dir, 'bridge-key');
  fs.writeFileSync(file, 'chiave-dal-file-0123456789abcdefghij');

  const env = { BRIDGE_KEY: 'chiave-dall-ambiente-0123456789abcd', BRIDGE_KEY_FILE: file };
  const info = keyStore.resolve(env);
  assert.strictEqual(info.source, 'env');
  assert.strictEqual(info.key, 'chiave-dall-ambiente-0123456789abcd');
});

test('resolve legge la chiave dal file e la mette nell ambiente', () => {
  const dir = tempDir();
  const file = path.join(dir, 'bridge-key');
  const key = 'chiave-generata-dal-telefono-0123456789ab';
  fs.writeFileSync(file, key + '\n');

  // Un ambiente senza BRIDGE_KEY: e' il caso di un server appena accoppiato.
  const env = { BRIDGE_KEY_FILE: file };
  const info = keyStore.resolve(env);

  assert.strictEqual(info.source, 'file');
  assert.strictEqual(info.key, key);
  assert.strictEqual(env.BRIDGE_KEY, key,
    'crypto-helper legge process.env: la chiave deve essere li prima del require');
});

test('resolve non prende per chiave il default pubblico', () => {
  const env = { BRIDGE_KEY: 'WhatsAppCommunityWP8-2026' };
  const info = keyStore.resolve(env);
  assert.strictEqual(info.source, 'none');
  assert.strictEqual(info.key, '');
});

test('resolve senza file e senza ambiente non trova nulla e non solleva', () => {
  const dir = tempDir();
  const info = keyStore.resolve({ BRIDGE_KEY_FILE: path.join(dir, 'non-esiste') });
  assert.deepStrictEqual(info, { key: '', source: 'none' });
});

test('save scrive la chiave in modo atomico e leggibile solo dal proprietario', () => {
  const dir = tempDir();
  const file = path.join(dir, 'nested', 'bridge-key');
  const key = 'chiave-generata-dal-telefono-0123456789ab';

  assert.strictEqual(keyStore.save(key, file), true);
  assert.strictEqual(fs.readFileSync(file, 'utf8'), key);
  assert.strictEqual(fs.existsSync(file + '.tmp'), false, 'il file temporaneo non resta in giro');

  if (process.platform !== 'win32') {
    assert.strictEqual(fs.statSync(file).mode & 0o777, 0o600);
  }
});

test('senza un file configurato save non scrive nulla', () => {
  assert.strictEqual(keyStore.save('x', ''), false);
});

test('newBridgeKey e casuale e abbastanza lungo', () => {
  const a = keyStore.newBridgeKey();
  const b = keyStore.newBridgeKey();
  assert.notStrictEqual(a, b);
  // 32 byte in base64url: 43 caratteri senza riempimento.
  assert.ok(a.length >= 40, 'chiave troppo corta: ' + a.length);
});

test('il codice di pairing ha la lunghezza e l alfabeto attesi', () => {
  const code = keyStore.newPairingCode();
  assert.strictEqual(code.length, keyStore.CODE_LENGTH);
  assert.match(code, /^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]+$/);
  assert.strictEqual(code.includes('0'), false);
  assert.strictEqual(code.includes('I'), false);
});

test('due codici estratti non coincidono', () => {
  const seen = new Set();
  for (let i = 0; i < 50; i++) seen.add(keyStore.newPairingCode());
  assert.strictEqual(seen.size, 50);
});

test('normalizeCode toglie trattini, spazi e maiuscole, formatCode li rimette', () => {
  assert.strictEqual(keyStore.normalizeCode('abcd-efgh jklm-npqr'), 'ABCDEFGHJKLMNPQR');
  assert.strictEqual(keyStore.formatCode('ABCDEFGHJKLMNPQR'), 'ABCD-EFGH-JKLM-NPQR');
  // Un codice copiato con i trattini si normalizza allo stesso valore.
  assert.strictEqual(keyStore.normalizeCode('ABCD-EFGH-JKLM-NPQR'), 'ABCDEFGHJKLMNPQR');
});
