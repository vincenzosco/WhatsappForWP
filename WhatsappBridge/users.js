'use strict';

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

// Un utente del servizio: un token che l'app tiene, e il device GOWA che gli
// appartiene. Il token non si salva mai: si salva il suo hash scrypt, perche'
// un file di token in chiaro e' un file di chiavi, e una copia di quel file
// (un backup, un volume mal montato) darebbe l'account a chi la legge.
//
// scrypt e' lento di proposito: un token da 32 byte casuali non si indovina,
// ma se il file finisse in mano a qualcuno il costo di provare a indovinarlo
// deve restare alto. Il sale e' per utente, cosi' due token uguali non hanno lo
// stesso hash.

function hashToken(token, salt, scryptSync) {
  return scryptSync(String(token), salt, 64).toString('hex');
}

/** Un token nuovo: 32 byte casuali, in una forma che si puo' copiare e incollare. */
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
      // Un file che non c'e' e' un servizio nuovo: nessun utente. Un file che
      // c'e' ma non si legge e' un'altra cosa, e non si nasconde.
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
    // Scrittura atomica: un file scritto a meta' perche' il processo e' caduto
    // non deve poter sostituire un elenco valido.
    const tmp = file + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify({ users }, null, 2));
    fs.renameSync(tmp, file);
  }

  /**
   * Un utente nuovo, con il suo token. Il token in chiaro esce da qui una volta
   * sola: non e' recuperabile dopo, perche' non e' salvato.
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
   * Il token corrisponde a un utente? Il confronto e' a tempo costante, perche'
   * un confronto che esce al primo byte diverso si puo' misurare.
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
