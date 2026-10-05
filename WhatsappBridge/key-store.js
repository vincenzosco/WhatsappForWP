'use strict';

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const { applyDotEnv, DEFAULTS } = require('./config');

// Where the frame key comes from, in order: BRIDGE_KEY in the environment, the
// file BRIDGE_KEY_FILE points at, otherwise nothing - which means the
// passphrase compiled into the public app.
//
// The file exists for the phone-generated key: pairing writes the key the phone
// drew there, and the next start reads it back. Keeping it on the volume (the
// same one as the WhatsApp session) means a restart does not ask the phone to
// pair again, and the key never has to be typed on the server by hand.
//
// This module must be resolved BEFORE crypto-helper is required: crypto-helper
// reads the passphrase once, when it is loaded, so a key found here has to be in
// the environment by then. server.js does exactly that, and store.resolve() is
// the call that puts it there.

/** The passphrase of a phone-generated key: 32 random bytes, in base64url. */
function newBridgeKey() {
  return crypto.randomBytes(32).toString('base64url');
}

// No 0/O/1/I: the code is read off a screen and typed by hand, and those four
// are what a hand gets wrong. 32 symbols, so 16 characters are 80 bits.
const CODE_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
const CODE_LENGTH = 16;

/**
 * A one-time pairing code. 80 random bits: the code is the only thing that
 * protects the key the phone is about to send, so it must not be guessable, and
 * 16 characters is what a person can copy from the log once.
 */
function newPairingCode(randomBytes) {
  const draw = randomBytes || crypto.randomBytes;
  const bytes = draw(CODE_LENGTH);
  let code = '';
  for (let i = 0; i < CODE_LENGTH; i++) code += CODE_ALPHABET[bytes[i] % CODE_ALPHABET.length];
  return code;
}

/**
 * The code as it is compared: uppercase, with the dashes and spaces of a
 * hand-typed copy removed. Without it, "abcd-efgh..." and "ABCD EFGH..." would
 * be two different passphrases and one of them always fails.
 */
function normalizeCode(code) {
  return String(code || '').toUpperCase().replace(/[^A-Z0-9]/g, '');
}

/** The code in the groups the log shows and the app asks to be typed back. */
function formatCode(code) {
  const clean = normalizeCode(code);
  return clean.replace(/(.{4})(?=.)/g, '$1-');
}

/**
 * Reads the key. It applies .env first, so a key set there is seen even though
 * this runs before loadConfig. When the file holds one it is copied into
 * process.env.BRIDGE_KEY, which is what crypto-helper reads at load.
 */
function resolve(env = process.env) {
  applyDotEnv(env);

  const configured = env.BRIDGE_KEY && env.BRIDGE_KEY !== DEFAULTS.BRIDGE_KEY ? env.BRIDGE_KEY : '';
  if (configured) return { key: configured, source: 'env' };

  const file = env.BRIDGE_KEY_FILE || '';
  if (file) {
    let fromFile = '';
    try {
      fromFile = fs.readFileSync(file, 'utf8').trim();
    } catch (err) {
      // A file that is not there is a server that has never paired: it is the
      // normal first start, not a fault. Anything else is not hidden.
      if (err && err.code !== 'ENOENT') throw err;
    }
    if (fromFile) {
      env.BRIDGE_KEY = fromFile;
      return { key: fromFile, source: 'file' };
    }
  }

  return { key: '', source: 'none' };
}

/**
 * Writes the key where the next start will read it, owner-readable only: a copy
 * of this file is a copy of the key, so it must not sit in the world-readable
 * part of the volume.
 */
function save(key, file) {
  if (!file) return false;
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = file + '.tmp';
  fs.writeFileSync(tmp, String(key), { mode: 0o600 });
  fs.renameSync(tmp, file);
  return true;
}

module.exports = { resolve, save, newBridgeKey, newPairingCode, normalizeCode, formatCode, CODE_LENGTH };
