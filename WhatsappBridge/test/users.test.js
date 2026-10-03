'use strict';

const test = require('node:test');
const assert = require('node:assert');
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');

const { createUserStore, hashToken, newToken, deviceToken } = require('../users');
const { parseToken, authenticate } = require('../auth');

// scrypt e' lento di proposito, e in un test questo si paga a ogni token. Si
// inietta una funzione veloce e deterministica: quello che si verifica e' la
// logica del deposito, non la robustezza di scrypt.
function fastHash() {
  return (token, salt) => crypto.createHash('sha256').update(String(token) + '|' + salt).digest();
}

function tempFile(name) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'wp8-users-'));
  return path.join(dir, name || 'users.json');
}

function store(file) {
  return createUserStore({
    file,
    scryptSync: fastHash(),
    randomBytes: (n) => Buffer.alloc(n, 7),
    now: () => 1000
  });
}

test('un token appena creato verifica, uno inventato no', () => {
  const users = store(tempFile());
  const { token } = users.register('vincenzo');

  assert.ok(users.verify(token), 'il token appena creato deve verificare');
  assert.strictEqual(users.verify('non-e-un-token'), null);
  assert.strictEqual(users.verify(''), null);
  assert.strictEqual(users.verify(null), null);
});

test('il token in chiaro non finisce nel file', () => {
  const file = tempFile();
  const users = store(file);
  const { token } = users.register('vincenzo');

  const saved = fs.readFileSync(file, 'utf8');
  assert.ok(saved.indexOf(token) < 0, 'il file non deve contenere il token in chiaro');
  assert.ok(saved.indexOf('tokenHash') >= 0, 'il file deve contenere l hash');
  assert.ok(saved.indexOf('"salt"') >= 0, 'ogni utente deve avere il suo sale');
});

test('il token sopravvive a un riavvio del processo', () => {
  const file = tempFile();
  const first = store(file);
  const { token } = first.register('vincenzo');

  const second = store(file);
  assert.strictEqual(second.count(), 1);
  assert.ok(second.verify(token));
});

test('il device di un utente si ricorda e si ritrova', () => {
  const file = tempFile();
  const users = store(file);
  const { token, user } = users.register('vincenzo');

  assert.ok(users.setDevice(token, 'dev-1'));
  assert.strictEqual(users.verify(token).deviceId, 'dev-1');

  const reloaded = store(file);
  assert.strictEqual(reloaded.verify(token).deviceId, 'dev-1');
  assert.strictEqual(user.id.length, 16);
});

test('due utenti hanno sali diversi, e il token di uno non vale per l altro', () => {
  const users = createUserStore({
    file: tempFile(),
    scryptSync: fastHash(),
    randomBytes: crypto.randomBytes
  });

  const a = users.register('a');
  const b = users.register('b');

  assert.notStrictEqual(a.user.salt, b.user.salt);
  assert.strictEqual(users.verify(a.token).id, a.user.id);
  assert.strictEqual(users.verify(b.token).id, b.user.id);
});

test('un token nuovo e una stringa che si puo incollare', () => {
  const token = newToken(crypto.randomBytes);
  assert.match(token, /^[A-Za-z0-9_-]{40,}$/);
});

test('l hash dipende dal sale, non solo dal token', () => {
  const one = hashToken('abc', 'salt-1', fastHash());
  const two = hashToken('abc', 'salt-2', fastHash());
  assert.notStrictEqual(one, two);
});

test('authenticate lascia passare un istanza privata senza token', () => {
  const result = authenticate({ frame: {}, users: null, authRequired: false });
  assert.strictEqual(result.ok, true);
  assert.strictEqual(result.user, null);
});

test('authenticate rifiuta un token assente o sbagliato', () => {
  const users = store(tempFile());
  const { token } = users.register('vincenzo');

  assert.strictEqual(authenticate({ frame: {}, users, authRequired: true }).ok, false);
  assert.strictEqual(
    authenticate({ frame: { Token: 'sbagliato' }, users, authRequired: true }).ok, false);
  assert.strictEqual(authenticate({ frame: {}, users: null, authRequired: true }).ok, false);

  const good = authenticate({ frame: { Token: token }, users, authRequired: true });
  assert.strictEqual(good.ok, true);
  assert.strictEqual(good.user.id, users.verify(token).id);
});

test('lo stesso dispositivo ottiene sempre lo stesso token', () => {
  const users = store(tempFile());
  const first = users.register('dev-1', 'vincenzo');
  const again = users.register('dev-1', 'vincenzo');

  assert.strictEqual(again.token, first.token, 'il token deve restare lo stesso');
  assert.strictEqual(users.count(), 1, 'un dispositivo non deve creare due utenti');
  assert.strictEqual(again.existing, true, 'il secondo giro deve dire che il dispositivo c era gia');
});

test('due dispositivi diversi non condividono il token', () => {
  const users = store(tempFile());
  const a = users.register('dev-1', 'a');
  const b = users.register('dev-2', 'b');

  assert.notStrictEqual(a.token, b.token);
  assert.strictEqual(users.count(), 2);
  assert.strictEqual(users.verify(a.token).id, a.user.id);
  assert.strictEqual(users.verify(b.token).id, b.user.id);
});

test('il token derivato sopravvive a un riavvio del processo', () => {
  const file = tempFile();
  const first = store(file);
  const { token } = first.register('dev-1', 'vincenzo');

  const second = store(file);
  assert.strictEqual(second.register('dev-1', 'vincenzo').token, token);
  assert.ok(second.verify(token), 'il token derivato deve verificare');
});

test('findByClientId trova il dispositivo, e non un altro', () => {
  const users = store(tempFile());
  users.register('dev-1', 'vincenzo');

  assert.strictEqual(users.findByClientId('dev-1').name, 'vincenzo');
  assert.strictEqual(users.findByClientId('dev-2'), null);
  assert.strictEqual(users.findByClientId(''), null);
});

test('senza un device id il token resta casuale, come prima', () => {
  const users = createUserStore({
    file: tempFile(),
    scryptSync: fastHash(),
    randomBytes: crypto.randomBytes
  });
  const a = users.register('', 'device');
  const b = users.register('', 'device');

  assert.notStrictEqual(a.token, b.token);
  assert.strictEqual(users.count(), 2);
});

test('il segreto non finisce nel token, ma il token dipende da lui', () => {
  const one = deviceToken('secret-1', 'dev-1', crypto.createHmac);
  const two = deviceToken('secret-2', 'dev-1', crypto.createHmac);

  assert.notStrictEqual(one, two);
  assert.strictEqual(one, deviceToken('secret-1', 'dev-1', crypto.createHmac));
  assert.match(one, /^[A-Za-z0-9_-]{40,}$/);
});

test('parseToken legge solo una stringa, e la ripulisce', () => {
  assert.strictEqual(parseToken({ Token: '  abc  ' }), 'abc');
  assert.strictEqual(parseToken({ Token: 42 }), '');
  assert.strictEqual(parseToken(null), '');
});
