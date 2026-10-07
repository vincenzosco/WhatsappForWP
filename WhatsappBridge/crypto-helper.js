/**
 * ============================================================================
 *  Encryption helper (AES-256-CBC + HMAC-SHA256, AES-256-GCM accepted)
 * ============================================================================
 *  Encrypts and authenticates the payload exchanged with the WP8 app with a
 *  shared key derived (HMAC-SHA256) from a passphrase.
 *
 *  Payload format (after the 4-byte length prefix):
 *    [1-byte cipher tag][body]
 *      tag 1 -> body = [12-byte IV][AES-256-GCM ciphertext || 16-byte tag]
 *      tag 2 -> body = [16-byte IV][AES-256-CBC ciphertext
 *                                   || 32-byte HMAC-SHA256(IV || ciphertext)]
 *
 *  Why two ciphers: AES-GCM is the more convenient one, but on Windows Phone
 *  8.1 it answers NotImplementedException (0x80004001) at run time even though
 *  the member exists in the WinRT projection. The app therefore always writes
 *  with tag 2; the adapter accepts both tags and answers each client with the
 *  cipher that client used (see server.js). Until a client has written
 *  anything, the adapter uses tag 2, which everyone can read.
 *
 *  Keys (they must match WhatsappApp/Services/CryptoHelper.cs):
 *    master = SHA-256(passphrase)
 *    encKey = HMAC-SHA256(master, "wp8-adapter enc")
 *    macKey = HMAC-SHA256(master, "wp8-adapter mac")
 *  The passphrase of a frame is the one derived from the device id it carries
 *  (framePassphraseFor): the default below plus the id. There is no global
 *  BRIDGE_KEY to keep in step any more, and no pairing: the id is a hardware
 *  token that survives an uninstall, so both sides recompute the same key.
 *
 *  Set BRIDGE_ENCRYPTION=off to disable encryption (plaintext payloads,
 *  no cipher tag), matching the old unencrypted protocol.
 * ============================================================================
 */

const crypto = require('crypto');
const { DEFAULTS } = require('./config');

// It must stay identical to the passphrase compiled into the app
// (WhatsappApp/Services/CryptoHelper.cs): it is the prefix every device key is
// derived from, and the key a frame written before any device key is known is
// reachable with.
const DEFAULT_PASSPHRASE = DEFAULTS.BRIDGE_KEY;

/**
 * The passphrase of one device's frames: the one compiled into the app, plus
 * the device id. The id is a hardware token that survives an uninstall, so a
 * phone whose storage a reinstall emptied recomputes the same key with nothing
 * stored and no operator action; the server derives the same one from the id it
 * already knows. This is what replaced the global BRIDGE_KEY and the pairing
 * that used to install it.
 */
function framePassphraseFor(deviceId) {
  return DEFAULT_PASSPHRASE + ':' + String(deviceId || '');
}

/** Cipher tag, the first byte of the payload. */
const CIPHER_GCM = 1;
const CIPHER_CBC_HMAC = 2;

/** Cipher used toward a client that has not written anything yet. */
const DEFAULT_CIPHER_TAG = CIPHER_CBC_HMAC;

const GCM_IV_LENGTH = 12;
const GCM_TAG_LENGTH = 16;
const CBC_IV_LENGTH = 16;
const MAC_LENGTH = 32;

const ENCRYPTION_ENABLED = process.env.BRIDGE_ENCRYPTION !== 'off';

const MODE_DESCRIPTION = 'AES-256-CBC + HMAC-SHA256 (AES-256-GCM accepted)';

/**
 * The two keys of a passphrase, the way both sides derive them:
 *   master = SHA-256(passphrase)
 *   encKey = HMAC-SHA256(master, "wp8-adapter enc")
 *   macKey = HMAC-SHA256(master, "wp8-adapter mac")
 * The server uses this per device: a frame written with a device's derived
 * passphrase is opened without touching the frame keys of any other device.
 */
function keysFor(passphrase) {
  const master = crypto.createHash('sha256').update(String(passphrase)).digest();
  return {
    encKey: crypto.createHmac('sha256', master).update('wp8-adapter enc').digest(),
    macKey: crypto.createHmac('sha256', master).update('wp8-adapter mac').digest()
  };
}

const initial = keysFor(process.env.BRIDGE_KEY || DEFAULT_PASSPHRASE);
const ENC_KEY = initial.encKey;
const MAC_KEY = initial.macKey;

/** [tag][IV][CBC ciphertext][HMAC(IV || ciphertext)], scritto con le chiavi date. */
function encryptCbcWith(keys, plaintext, tag) {
  const scelto = tag || CIPHER_CBC_HMAC;
  if (scelto !== CIPHER_CBC_HMAC) {
    throw new Error('Unknown cipher tag to write: ' + scelto);
  }

  const iv = crypto.randomBytes(CBC_IV_LENGTH);
  const cipher = crypto.createCipheriv('aes-256-cbc', keys.encKey, iv);
  const body = Buffer.concat([cipher.update(plaintext), cipher.final()]);
  const mac = crypto.createHmac('sha256', keys.macKey).update(iv).update(body).digest();
  return Buffer.concat([Buffer.from([CIPHER_CBC_HMAC]), iv, body, mac]);
}

/** Lo stesso, con le chiavi correnti del server. */
function encryptCbc(plaintext) {
  return encryptCbcWith({ encKey: ENC_KEY, macKey: MAC_KEY }, plaintext);
}

/**
 * Verifies the signature and only then decrypts (encrypt-then-MAC). The keys
 * arrive from outside because the same code serves the recovery: there the
 * frame was written with the passphrase compiled into the public app, while
 * this server uses one of its own.
 */
function decryptCbcWith(keys, payload) {
  if (payload.length < CBC_IV_LENGTH + 16 + MAC_LENGTH) {
    throw new Error('Invalid CBC payload (too short)');
  }

  const iv = payload.slice(0, CBC_IV_LENGTH);
  const body = payload.slice(CBC_IV_LENGTH, payload.length - MAC_LENGTH);
  const mac = payload.slice(payload.length - MAC_LENGTH);
  const expected = crypto.createHmac('sha256', keys.macKey).update(iv).update(body).digest();

  if (!crypto.timingSafeEqual(mac, expected)) {
    throw new Error('Invalid HMAC signature');
  }

  const decipher = crypto.createDecipheriv('aes-256-cbc', keys.encKey, iv);
  return Buffer.concat([decipher.update(body), decipher.final()]).toString('utf8');
}

/** Lo stesso, con le chiavi correnti del server. */
function decryptCbc(payload) {
  return decryptCbcWith({ encKey: ENC_KEY, macKey: MAC_KEY }, payload);
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
 * Encrypts a JSON string into the v3 payload. The tag tells the receiver which
 * cipher it was written with; without one, the cipher everyone can read is used.
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
 * Decrypts a v3 payload by reading the tag from the first byte.
 * Throws on a truncated payload, an invalid signature or an unknown tag.
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
 * The cipher tag of a payload, 0 if it is not encrypted or not recognized.
 * The server uses it to answer each client with that client's cipher.
 */
function cipherTagOf(payload) {
  if (!ENCRYPTION_ENABLED) return 0;
  if (!payload || payload.length < 1) return 0;
  const tag = payload[0];
  return tag === CIPHER_CBC_HMAC || tag === CIPHER_GCM ? tag : 0;
}

/** A complete TCP frame: [4-byte UInt32LE length][payload]. */
function buildFrame(jsonStr, tag) {
  const payload = encryptPayload(jsonStr, tag);
  const lenBuf = Buffer.alloc(4);
  lenBuf.writeUInt32LE(payload.length, 0);
  return Buffer.concat([lenBuf, payload]);
}

/**
 * A payload written with a passphrase that is not the current one: what the
 * recovery needs, because the phone that lost its key writes with the
 * passphrase compiled into the public app. Same shape as encryptPayload (the
 * cipher tag leads the payload) and CBC only: the header above says why the
 * app on WP8.1 cannot read or write AES-GCM.
 */
function encryptPayloadWith(passphrase, jsonStr, tag) {
  return encryptCbcWith(keysFor(passphrase), Buffer.from(String(jsonStr), 'utf8'), tag);
}

/** The inverse of encryptPayloadWith. Refuses any tag but the one it writes. */
function decodePayloadWith(passphrase, payload) {
  if (!payload || payload.length < 1) {
    throw new Error('Empty encrypted payload');
  }

  const tag = payload[0];
  if (tag !== CIPHER_CBC_HMAC) {
    throw new Error('Unknown cipher tag: ' + tag);
  }

  return decryptCbcWith(keysFor(passphrase), payload.slice(1));
}

module.exports = {
  encryptPayload,
  encryptPayloadWith,
  decodePayloadWith,
  decodePayload,
  buildFrame,
  cipherTagOf,
  keysFor,
  framePassphraseFor,
  ENCRYPTION_ENABLED,
  DEFAULT_PASSPHRASE,
  CIPHER_GCM,
  CIPHER_CBC_HMAC,
  DEFAULT_CIPHER_TAG,
  ModeDescription: MODE_DESCRIPTION,
  GCM_IV_LENGTH,
  GCM_TAG_LENGTH,
  CBC_IV_LENGTH,
  MAC_LENGTH
};
