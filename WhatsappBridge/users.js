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
//
// The token is not random any more: it is derived from the device id and a
// secret this store keeps (`deviceToken`). The phone presents one stable device
// id for the life of the install, so it is handed the same token every time it
// connects, instead of being registered as a new user on each connection - which
// is what a phone with no stored token used to do, once per connect.

function hashToken(token, salt, scryptSync) {
  return scryptSync(String(token), salt, 64).toString('hex');
}

/** A new token: 32 random bytes, in a form that can be copied and pasted. */
function newToken(randomBytes) {
  return randomBytes(32).toString('base64url');
}

/**
 * The token of a device: the same device id always yields the same token, for as
 * long as the secret does not change. This is what makes the token constant
 * across a reconnect, an app restart and a server restart.
 *
 * The trade-off is deliberate and belongs to the operator: the file now holds a
 * secret from which every device's token can be derived, where before it held
 * only hashes. The secret must be treated as a credential - it is what a copy of
 * `users.json` would hand over.
 */
function deviceToken(secret, deviceId, createHmac) {
  const hmac = (createHmac || crypto.createHmac)('sha256', String(secret));
  hmac.update('device:' + String(deviceId));
  return hmac.digest('base64url');
}

function createUserStore(options) {
  const opts = options || {};
  const file = opts.file;
  const scryptSync = opts.scryptSync || crypto.scryptSync;
  const randomBytes = opts.randomBytes || crypto.randomBytes;
  const now = opts.now || Date.now;
  const timingSafeEqual = opts.timingSafeEqual || crypto.timingSafeEqual;
  const createHmac = opts.createHmac || crypto.createHmac;

  let users = [];
  let secret = '';

  function load() {
    if (!file) {
      users = [];
      secret = newToken(randomBytes);
      return users;
    }
    try {
      const raw = JSON.parse(fs.readFileSync(file, 'utf8'));
      users = Array.isArray(raw.users) ? raw.users : [];
      // A file written before the token was derived has no secret: one is made
      // now, and the tokens already stored keep working, because verify() only
      // ever looks at the hash. Their owners are handed a derived token the next
      // time they register, which is a new row, not a broken one.
      secret = typeof raw.secret === 'string' && raw.secret ? raw.secret : '';
    } catch (err) {
      // A file that is not there is a new service: no users. A file that is
      // there but cannot be read is another matter, and is not hidden.
      if (err && err.code === 'ENOENT') {
        users = [];
      } else {
        throw err;
      }
    }
    if (!secret) secret = newToken(randomBytes);
    return users;
  }

  function save() {
    if (!file) return;
    fs.mkdirSync(path.dirname(file), { recursive: true });
    // Atomic write: a file written halfway because the process fell over must
    // not be able to replace a valid list.
    const tmp = file + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify({ secret, users }, null, 2));
    fs.renameSync(tmp, file);
  }

  /** The user this device id belongs to, if the service has seen it before. */
  function findByClientId(deviceId) {
    const id = String(deviceId || '');
    if (!id) return null;
    return users.find((u) => u && u.clientId === id) || null;
  }

  /**
   * The user of a device, created on first sight and returned on every later
   * sight. The token is derived from the device id, so a device that connects
   * again is handed the same token it had before instead of a new user being
   * appended - which is what made the file grow one row per connection.
   *
   * With no device id (an app build that does not send one) the token is random,
   * as it always was: the old behaviour is kept rather than guessed at.
   *
   * `existing` tells the caller whether this was a new device: only a new one is
   * told its token, because a returning one already has it.
   */
  function register(deviceId, name) {
    const id = String(deviceId || '');
    const known = findByClientId(id);
    if (known) {
      if (name) known.name = String(name);
      known.lastSeenAt = now();
      save();
      return { token: deviceToken(secret, id, createHmac), user: known, existing: true };
    }

    const token = id ? deviceToken(secret, id, createHmac) : newToken(randomBytes);
    const salt = randomBytes(16).toString('hex');
    const user = {
      id: randomBytes(8).toString('hex'),
      name: String(name || ''),
      clientId: id,
      salt,
      tokenHash: hashToken(token, salt, scryptSync),
      deviceId: '',
      createdAt: now(),
      lastSeenAt: now()
    };
    users.push(user);
    save();
    return { token, user, existing: false };
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

  return {
    register,
    verify,
    setDevice,
    findByClientId,
    all,
    load,
    save,
    count: () => users.length
  };
}

module.exports = { createUserStore, hashToken, newToken, deviceToken };
