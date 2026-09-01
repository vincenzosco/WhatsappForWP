/**
 * ============================================================================
 *  Encryption helper (AES-256-GCM)
 * ============================================================================
 *  Encrypts/decrypts the payload exchanged with the WP8 app using AES-256-GCM
 *  with a pre-shared key derived (SHA-256) from a passphrase.
 *
 *  Frame payload format (sent after the 4-byte UInt32LE length prefix):
 *    [12 bytes random IV][AES-256-GCM ciphertext || 16 bytes auth tag]
 *
 *  The passphrase MUST match the one in the WP8 app
 *  (WhatsappApp/Services/CryptoHelper.cs, constant "Passphrase").
 *  Change it with the BRIDGE_KEY environment variable, e.g.:
 *    BRIDGE_KEY="my-secret-key" npm start
 *
 *  Set BRIDGE_ENCRYPTION=off to disable encryption (plaintext payloads),
 *  matching the old unencrypted protocol.
 * ============================================================================
 */

const crypto = require('crypto');

const IV_LENGTH = 12;
const TAG_LENGTH = 16;
const DEFAULT_PASSPHRASE = 'WhatsAppCommunityWP8-2026';

const ENCRYPTION_ENABLED = process.env.BRIDGE_ENCRYPTION !== 'off';
const ENCRYPTION_KEY = crypto
  .createHash('sha256')
  .update(process.env.BRIDGE_KEY || DEFAULT_PASSPHRASE)
  .digest();

/**
 * Encrypts a JSON string into a payload: [iv][ciphertext || tag].
 * Returns the plaintext UTF-8 buffer when encryption is disabled.
 */
function encryptPayload(jsonStr) {
  if (!ENCRYPTION_ENABLED) {
    return Buffer.from(jsonStr, 'utf8');
  }

  const iv = crypto.randomBytes(IV_LENGTH);
  const cipher = crypto.createCipheriv('aes-256-gcm', ENCRYPTION_KEY, iv);
  const encrypted = Buffer.concat([
    cipher.update(jsonStr, 'utf8'),
    cipher.final(),
    cipher.getAuthTag()
  ]);

  return Buffer.concat([iv, encrypted]);
}

/**
 * Decrypts a payload ([iv][ciphertext || tag]) back to the JSON string.
 * Throws on tampered payloads / wrong key (auth tag mismatch).
 */
function decodePayload(payload) {
  if (!ENCRYPTION_ENABLED) {
    return payload.toString('utf8');
  }

  if (payload.length < IV_LENGTH + TAG_LENGTH) {
    throw new Error('Payload cifrato non valido (troppo corto)');
  }

  const iv = payload.slice(0, IV_LENGTH);
  const data = payload.slice(IV_LENGTH);
  const tag = data.slice(data.length - TAG_LENGTH);
  const ciphertext = data.slice(0, data.length - TAG_LENGTH);

  const decipher = crypto.createDecipheriv('aes-256-gcm', ENCRYPTION_KEY, iv);
  decipher.setAuthTag(tag);
  return Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString('utf8');
}

/**
 * Builds a complete TCP frame: [4-byte UInt32LE payload length][payload].
 */
function buildFrame(jsonStr) {
  const payload = encryptPayload(jsonStr);
  const lenBuf = Buffer.alloc(4);
  lenBuf.writeUInt32LE(payload.length, 0);
  return Buffer.concat([lenBuf, payload]);
}

module.exports = {
  encryptPayload,
  decodePayload,
  buildFrame,
  ENCRYPTION_ENABLED,
  IV_LENGTH,
  TAG_LENGTH
};
