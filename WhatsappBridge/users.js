'use strict';

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

// A user of the service: a token the app holds, and the GOWA device that
// belongs to it. The token is never stored: its scrypt hash is, because a file
// of plain tokens is a file of keys, and a copy of that file (a backup, a badly
// mounted volume) would hand the account to whoever reads it.
//
// scrypt is slow on purpose: a 32-byte random token cannot be guessed, but if
// the file fell into someone's hands the cost of trying to guess it must stay
// high. The salt is per user, so two identical tokens do not share a hash.

function hashToken(token, salt, scryptSync) {
  return scryptSync(String(token), salt, 64).toString('hex');
}

/** A new token: 32 random bytes, in a form that can be copied and pasted. */
function newToken(randomBytes) {
  return randomBytes(32).toString('base64url');
}

function createUserStore(options) {
  const opts = options || {};
  const file = opts.file;
  const scryptSync = opts.scryptSync || crypto.scryptSync;
  const randomBytes = opts.randomBytes || crypto.randomBytes;
  const now = opts.now || Date.now;
  const timingSafeEqual = opts.timingSafeEqual || crypto.timingSafeEqual;

  let users = [];

  function load() {
    if (!file) {
      users = [];
      return users;
    }
    try {
      const raw = JSON.parse(fs.readFileSync(file, 'utf8'));
      users = Array.isArray(raw.users) ? raw.users : [];
    } catch (err) {
      // A file that is not there is a new service: no users. A file that is
      // there but cannot be read is another matter, and is not hidden.
      if (err && err.code === 'ENOENT') {
        users = [];
      } else {
        throw err;
      }
    }
    return users;
  }

  function save() {
    if (!file) return;
    fs.mkdirSync(path.dirname(file), { recursive: true });
    // Atomic write: a file written halfway because the process fell over must
    // not be able to replace a valid list.
    const tmp = file + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify({ users }, null, 2));
    fs.renameSync(tmp, file);
  }

  /**
   * A new user, with the token. The plain token leaves here exactly once: it
   * is not recoverable afterwards, because it is not stored.
   */
  function register(name) {
    const token = newToken(randomBytes);
    const salt = randomBytes(16).toString('hex');
    const user = {
      id: randomBytes(8).toString('hex'),
      name: String(name || ''),
      salt,
      tokenHash: hashToken(token, salt, scryptSync),
      deviceId: '',
      createdAt: now(),
      lastSeenAt: now()
    };
    users.push(user);
    save();
    return { token, user };
  }

  /**
   * Does the token match a user? The comparison is constant-time, because a
   * comparison that exits at the first differing byte can be measured.
   */
  function verify(token) {
    if (!token || !users.length) return null;

    for (const user of users) {
      const expected = Buffer.from(hashToken(token, user.salt, scryptSync), 'hex');
      const stored = Buffer.from(user.tokenHash, 'hex');
      if (expected.length === stored.length && timingSafeEqual(expected, stored)) {
        user.lastSeenAt = now();
        return user;
      }
    }
    return null;
  }

  function setDevice(token, deviceId) {
    const user = verify(token);
    if (!user) return null;
    user.deviceId = String(deviceId || '');
    save();
    return user;
  }

  function all() {
    return users;
  }

  load();

  return { register, verify, setDevice, all, load, save, count: () => users.length };
}

module.exports = { createUserStore, hashToken, newToken };
