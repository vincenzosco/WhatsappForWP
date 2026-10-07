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

test('framePassphraseFor deriva una chiave stabile da un device id', () => {
  const id = '01009f100200899703000fdf0400cd1705004cb40700609608007bd10900712b';
  assert.strictEqual(cryptoHelper.framePassphraseFor(id), cryptoHelper.framePassphraseFor(id),
    'lo stesso device id da la stessa chiave: e cio che rende innocua una reinstallazione');
  assert.notStrictEqual(cryptoHelper.framePassphraseFor(id),
    cryptoHelper.framePassphraseFor('un-altro-device-id'));
  assert.strictEqual(cryptoHelper.framePassphraseFor(id).indexOf(cryptoHelper.DEFAULT_PASSPHRASE), 0,
    'la passphrase compilata e il prefisso della derivazione');
  assert.strictEqual(cryptoHelper.framePassphraseFor(''), cryptoHelper.DEFAULT_PASSPHRASE + ':');

  // Un frame scritto con la chiave di un device non si rilegge con quella di un
  // altro: e la prova che le due chiavi sono davvero distinte.
  const payload = cryptoHelper.encryptPayloadWith(cryptoHelper.framePassphraseFor(id), PLAIN);
  assert.strictEqual(
    cryptoHelper.decodePayloadWith(cryptoHelper.framePassphraseFor(id), payload), PLAIN);
  assert.throws(() => cryptoHelper.decodePayloadWith(
    cryptoHelper.framePassphraseFor('un-altro-device-id'), payload), /Invalid HMAC signature/);
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
