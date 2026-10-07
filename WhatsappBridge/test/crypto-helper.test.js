'use strict';
const test = require('node:test');
const assert = require('node:assert');
const cryptoHelper = require('../crypto-helper');

const PLAIN = JSON.stringify({ Type: 0, Text: 'ciao' });

// Vettore fisso con IV noto, calcolato con questo stesso algoritmo. Lo stesso
// vettore e' in WhatsappApp/Services/SelfCheck.cs: se le due derivazioni delle
// chiavi divergono, il telefono lo dice nel log invece di restare muto.
const VECTOR_IV_HEX = '000102030405060708090a0b0c0d0e0f';
const VECTOR_HEX =
  '02' + VECTOR_IV_HEX +
  '2fc17f6d19a9bea8e286ceebf69ca87c72cf5e563e0d09ee3d755fb40f87c336' +
  'e65a51262cff115a41eb866b8a82275d7d61dfb35bb0f5060ab9f1b316d45e2d';

test('encryptPayload scrive in CBC+HMAC e decodePayload lo rilegge', () => {
  const payload = cryptoHelper.encryptPayload(PLAIN);
  assert.strictEqual(payload[0], cryptoHelper.CIPHER_CBC_HMAC);
  // tag + IV + almeno un blocco + HMAC
  assert.ok(payload.length >= 1 + 16 + 16 + 32, 'payload troppo corto: ' + payload.length);
  assert.strictEqual(cryptoHelper.decodePayload(payload), PLAIN);
});

test('encryptPayload scrive in GCM quando glielo si chiede', () => {
  const payload = cryptoHelper.encryptPayload(PLAIN, cryptoHelper.CIPHER_GCM);
  assert.strictEqual(payload[0], cryptoHelper.CIPHER_GCM);
  assert.strictEqual(cryptoHelper.decodePayload(payload), PLAIN);
});

test('buildFrame antepone la lunghezza e porta il tag richiesto', () => {
  const frame = cryptoHelper.buildFrame(PLAIN, cryptoHelper.CIPHER_GCM);
  assert.strictEqual(frame.readUInt32LE(0), frame.length - 4);
  assert.strictEqual(frame[4], cryptoHelper.CIPHER_GCM);
});

test('il vettore di prova si decifra con le chiavi derivate', () => {
  const payload = Buffer.from(VECTOR_HEX, 'hex');
  assert.strictEqual(cryptoHelper.cipherTagOf(payload), cryptoHelper.CIPHER_CBC_HMAC);
  assert.strictEqual(cryptoHelper.decodePayload(payload), PLAIN);
});

test('un solo byte cambiato nel cifrato invalida la firma HMAC', () => {
  const payload = Buffer.from(VECTOR_HEX, 'hex');
  payload[30] ^= 0x01;
  assert.throws(() => cryptoHelper.decodePayload(payload), /Invalid HMAC signature/);
});

test('un tag cifrario sconosciuto viene rifiutato', () => {
  const payload = Buffer.from(VECTOR_HEX, 'hex');
  payload[0] = 7;
  assert.throws(() => cryptoHelper.decodePayload(payload), /Unknown cipher tag/);
});

test('cipherTagOf riconosce i due tag e ignora tutto il resto', () => {
  assert.strictEqual(cryptoHelper.cipherTagOf(Buffer.from([2, 0, 0])), cryptoHelper.CIPHER_CBC_HMAC);
  assert.strictEqual(cryptoHelper.cipherTagOf(Buffer.from([1, 0, 0])), cryptoHelper.CIPHER_GCM);
  assert.strictEqual(cryptoHelper.cipherTagOf(Buffer.from([9, 0, 0])), 0);
  assert.strictEqual(cryptoHelper.cipherTagOf(Buffer.alloc(0)), 0);
});

test('sealWith e openWith si annullano a vicenda con la stessa passphrase', () => {
  const blocco = JSON.stringify({ BridgeKey: 'chiave-generata-dal-telefono', SenderName: 'vincenzo' });
  const sealed = cryptoHelper.sealWith('ABCD-EFGH-JKLM-NPQR', blocco);
  assert.match(sealed, /^[A-Za-z0-9+/]+=*$/);
  assert.strictEqual(cryptoHelper.openWith('ABCD-EFGH-JKLM-NPQR', sealed), blocco);
});

test('openWith rifiuta una passphrase diversa da quella che ha sigillato', () => {
  const sealed = cryptoHelper.sealWith('ABCDEFGHJKLMNPQR', '{"BridgeKey":"x"}');
  assert.throws(() => cryptoHelper.openWith('ABCDEFGHJKLMNPQS', sealed), /Invalid sealed signature/);
});

test('un byte cambiato nel payload sigillato ne invalida la firma', () => {
  const sealed = cryptoHelper.sealWith('ABCDEFGHJKLMNPQR', '{"BridgeKey":"x"}');
  const bytes = Buffer.from(sealed, 'base64');
  bytes[20] ^= 0x01;
  assert.throws(() => cryptoHelper.openWith('ABCDEFGHJKLMNPQR', bytes.toString('base64')),
    /Invalid sealed signature/);
});

test('openWith rifiuta un payload troppo corto invece di leggere oltre', () => {
  assert.throws(() => cryptoHelper.openWith('ABCDEFGHJKLMNPQR', 'AAAA'), /too short/);
});

test('setPassphrase cambia le chiavi e usingDefaultKey segue la passphrase', () => {
  const originale = process.env.BRIDGE_KEY;
  try {
    delete process.env.BRIDGE_KEY;
    assert.strictEqual(cryptoHelper.usingDefaultKey(), true);

    cryptoHelper.setPassphrase('chiave-nuova-dal-telefono-0123456789ab');
    // Un frame scritto con la chiave nuova non si rilegge con la vecchia: e' la
    // prova che le chiavi sono davvero cambiate.
    const payload = cryptoHelper.encryptPayload(PLAIN);

    cryptoHelper.setPassphrase(cryptoHelper.DEFAULT_PASSPHRASE);
    assert.throws(() => cryptoHelper.decodePayload(payload), /Invalid HMAC signature/);

    process.env.BRIDGE_KEY = 'chiave-nuova-dal-telefono-0123456789ab';
    assert.strictEqual(cryptoHelper.usingDefaultKey(), false);
  } finally {
    if (originale === undefined) delete process.env.BRIDGE_KEY;
    else process.env.BRIDGE_KEY = originale;
    cryptoHelper.setPassphrase(cryptoHelper.DEFAULT_PASSPHRASE);
  }
});

/**
 * Il recupero: il telefono ha perso la sua chiave - una reinstallazione svuota
 * la memoria isolata dell'app - e scrive con la passphrase compilata nell'app
 * pubblico. Queste due funzioni sono meta' dello scambio che lo rimette in
 * pari: scrivere, e rileggere, un frame con una passphrase che non e' quella
 * corrente del server. Il tag e' quello CBC perche' il telefono non sa
 * scrivere in GCM su WP8.1.
 */
test('encryptPayloadWith scrive un frame che la stessa passphrase rilegge', () => {
  const payload = cryptoHelper.encryptPayloadWith('una-passphrase', PLAIN);
  assert.strictEqual(payload[0], cryptoHelper.CIPHER_CBC_HMAC, 'il tag e scritto nel payload');
  assert.strictEqual(cryptoHelper.decodePayloadWith('una-passphrase', payload), PLAIN);
});

test('decodePayloadWith rifiuta unaltra passphrase e un tag che non conosce', () => {
  const payload = cryptoHelper.encryptPayloadWith('una-passphrase', PLAIN);
  assert.throws(() => cryptoHelper.decodePayloadWith('unaltra-passphrase', payload),
    /Invalid HMAC signature/);

  // Il tag GCM non e' un payload valido per questa funzione: si ferma sul tag
  // prima di provare a decifrare, come decodePayload.
  const altroTag = Buffer.concat([Buffer.from([cryptoHelper.CIPHER_GCM]), payload.slice(1)]);
  assert.throws(() => cryptoHelper.decodePayloadWith('una-passphrase', altroTag),
    /Unknown cipher tag/);
});
