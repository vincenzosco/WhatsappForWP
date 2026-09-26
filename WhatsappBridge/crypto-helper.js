/**
 * ============================================================================
 *  Encryption helper (AES-256-CBC + HMAC-SHA256, AES-256-GCM accepted)
 * ============================================================================
 *  Cifra e autentica il payload scambiato con l'app WP8 con una chiave
 *  condivisa derivata (HMAC-SHA256) da una passphrase.
 *
 *  Formato del payload (dopo il prefisso di 4 byte con la lunghezza):
 *    [1 byte tag cifrario][corpo]
 *      tag 1 -> corpo = [12 byte IV][AES-256-GCM ciphertext || 16 byte tag]
 *      tag 2 -> corpo = [16 byte IV][AES-256-CBC ciphertext
 *                                    || 32 byte HMAC-SHA256(IV || ciphertext)]
 *
 *  Perche' due cifrari: AES-GCM e' il piu' comodo, ma su Windows Phone 8.1 a
 *  runtime risponde NotImplementedException (0x80004001) anche se il membro
 *  esiste nella proiezione WinRT. L'app scrive quindi sempre con il tag 2;
 *  l'adapter accetta entrambi i tag e risponde a ciascun client con il
 *  cifrario che quel client ha usato (vedi server.js). Finche' un client non
 *  ha scritto niente, l'adapter usa il tag 2, che tutti sanno leggere.
 *
 *  Chiavi (devono combaciare con WhatsappApp/Services/CryptoHelper.cs):
 *    master = SHA-256(passphrase)
 *    encKey = HMAC-SHA256(master, "wp8-adapter enc")
 *    macKey = HMAC-SHA256(master, "wp8-adapter mac")
 *  La passphrase e' BRIDGE_KEY, altrimenti quella predefinita qui sotto.
 *
 *  Set BRIDGE_ENCRYPTION=off to disable encryption (plaintext payloads,
 *  no cipher tag), matching the old unencrypted protocol.
 * ============================================================================
 */

const crypto = require('crypto');

const DEFAULT_PASSPHRASE = 'WhatsAppCommunityWP8-2026';

/** Tag del cifrario, primo byte del payload. */
const CIPHER_GCM = 1;
const CIPHER_CBC_HMAC = 2;

/** Cifrario usato verso un client che non ha ancora scritto niente. */
const DEFAULT_CIPHER_TAG = CIPHER_CBC_HMAC;

const GCM_IV_LENGTH = 12;
const GCM_TAG_LENGTH = 16;
const CBC_IV_LENGTH = 16;
const MAC_LENGTH = 32;

const ENCRYPTION_ENABLED = process.env.BRIDGE_ENCRYPTION !== 'off';

const MODE_DESCRIPTION = 'AES-256-CBC + HMAC-SHA256 (AES-256-GCM accepted)';

const MASTER_KEY = crypto
  .createHash('sha256')
  .update(process.env.BRIDGE_KEY || DEFAULT_PASSPHRASE)
  .digest();

const ENC_KEY = crypto.createHmac('sha256', MASTER_KEY).update('wp8-adapter enc').digest();
const MAC_KEY = crypto.createHmac('sha256', MASTER_KEY).update('wp8-adapter mac').digest();

/** [tag][IV][CBC ciphertext][HMAC(IV || ciphertext)] */
function encryptCbc(plaintext) {
  const iv = crypto.randomBytes(CBC_IV_LENGTH);
  const cipher = crypto.createCipheriv('aes-256-cbc', ENC_KEY, iv);
  const body = Buffer.concat([cipher.update(plaintext), cipher.final()]);
  const mac = crypto.createHmac('sha256', MAC_KEY).update(iv).update(body).digest();
  return Buffer.concat([Buffer.from([CIPHER_CBC_HMAC]), iv, body, mac]);
}

/** Verifica la firma e solo dopo decifra (encrypt-then-MAC). */
function decryptCbc(payload) {
  if (payload.length < CBC_IV_LENGTH + 16 + MAC_LENGTH) {
    throw new Error('Invalid CBC payload (too short)');
  }

  const iv = payload.slice(0, CBC_IV_LENGTH);
  const body = payload.slice(CBC_IV_LENGTH, payload.length - MAC_LENGTH);
  const mac = payload.slice(payload.length - MAC_LENGTH);
  const expected = crypto.createHmac('sha256', MAC_KEY).update(iv).update(body).digest();

  if (!crypto.timingSafeEqual(mac, expected)) {
    throw new Error('Invalid HMAC signature');
  }

  const decipher = crypto.createDecipheriv('aes-256-cbc', ENC_KEY, iv);
  return Buffer.concat([decipher.update(body), decipher.final()]).toString('utf8');
}

/** [tag][12 byte IV][GCM ciphertext || tag di autenticazione] */
function encryptGcm(plaintext) {
  const iv = crypto.randomBytes(GCM_IV_LENGTH);
  const cipher = crypto.createCipheriv('aes-256-gcm', ENC_KEY, iv);
  const body = Buffer.concat([cipher.update(plaintext), cipher.final(), cipher.getAuthTag()]);
  return Buffer.concat([Buffer.from([CIPHER_GCM]), iv, body]);
}

function decryptGcm(payload) {
  if (payload.length < GCM_IV_LENGTH + GCM_TAG_LENGTH) {
    throw new Error('Invalid GCM payload (too short)');
  }

  const iv = payload.slice(0, GCM_IV_LENGTH);
  const data = payload.slice(GCM_IV_LENGTH);
  const tag = data.slice(data.length - GCM_TAG_LENGTH);
  const body = data.slice(0, data.length - GCM_TAG_LENGTH);

  const decipher = crypto.createDecipheriv('aes-256-gcm', ENC_KEY, iv);
  decipher.setAuthTag(tag);
  return Buffer.concat([decipher.update(body), decipher.final()]).toString('utf8');
}

/**
 * Cifra una stringa JSON nel payload v3. Il tag dice al destinatario con quale
 * cifrario e' stato scritto; senza indicazione si usa quello che tutti leggono.
 */
function encryptPayload(jsonStr, tag) {
  if (!ENCRYPTION_ENABLED) {
    return Buffer.from(jsonStr, 'utf8');
  }

  const chosen = tag || DEFAULT_CIPHER_TAG;
  if (chosen === CIPHER_CBC_HMAC) return encryptCbc(Buffer.from(jsonStr, 'utf8'));
  if (chosen === CIPHER_GCM) return encryptGcm(Buffer.from(jsonStr, 'utf8'));
  throw new Error('Unknown cipher tag to write: ' + chosen);
}

/**
 * Decifra un payload v3 leggendo il tag dal primo byte.
 * Lancia su payload troncato, firma non valida o tag sconosciuto.
 */
function decodePayload(payload) {
  if (!ENCRYPTION_ENABLED) {
    return payload.toString('utf8');
  }

  if (payload.length < 1) {
    throw new Error('Empty encrypted payload');
  }

  const tag = cipherTagOf(payload);
  if (tag === CIPHER_CBC_HMAC) return decryptCbc(payload.slice(1));
  if (tag === CIPHER_GCM) return decryptGcm(payload.slice(1));
  throw new Error('Unknown cipher tag: ' + payload[0]);
}

/**
 * Il tag cifrario di un payload, 0 se non e' cifrato o non si riconosce.
 * Serve al server per rispondere a ogni client con il cifrario del client.
 */
function cipherTagOf(payload) {
  if (!ENCRYPTION_ENABLED) return 0;
  if (!payload || payload.length < 1) return 0;
  const tag = payload[0];
  return tag === CIPHER_CBC_HMAC || tag === CIPHER_GCM ? tag : 0;
}

/** Un frame TCP completo: [4-byte UInt32LE lunghezza][payload]. */
function buildFrame(jsonStr, tag) {
  const payload = encryptPayload(jsonStr, tag);
  const lenBuf = Buffer.alloc(4);
  lenBuf.writeUInt32LE(payload.length, 0);
  return Buffer.concat([lenBuf, payload]);
}

module.exports = {
  encryptPayload,
  decodePayload,
  buildFrame,
  cipherTagOf,
  ENCRYPTION_ENABLED,
  CIPHER_GCM,
  CIPHER_CBC_HMAC,
  DEFAULT_CIPHER_TAG,
  ModeDescription: MODE_DESCRIPTION,
  GCM_IV_LENGTH,
  GCM_TAG_LENGTH,
  CBC_IV_LENGTH,
  MAC_LENGTH
};
