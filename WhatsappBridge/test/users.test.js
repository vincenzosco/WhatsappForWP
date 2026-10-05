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

test('un utente senza client id ne riceve uno al primo token valido', () => {
  // E' il record scritto prima che il token fosse derivato: il telefono si
  // autentica con il token che ha, e da quel momento la derivazione sa a chi
  // appartiene quel dispositivo.
  const users = store(tempFile());
  const { user } = users.register('', 'vincenzo');
  assert.strictEqual(user.clientId, '');

  users.bindClientId(user, 'phone-1');

  assert.strictEqual(users.findByClientId('phone-1').id, user.id);
  assert.strictEqual(users.count(), 1, 'non deve nascere un secondo utente');
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

test('un token sbagliato non esegue scrypt su tutti gli utenti', () => {
  // L'indice invalida l'amplificazione: con l'archivio indicizzato il costo di
  // un handshake sbagliato non cresce col numero di utenti.
  let calls = 0;
  const counting = (token, salt) => {
    calls++;
    return fastHash()(token, salt);
  };
  const users = createUserStore({
    file: tempFile(),
    scryptSync: counting,
    randomBytes: crypto.randomBytes
  });
  const tokens = [];
  for (let i = 0; i < 50; i++) tokens.push(users.register('dev-' + i, 'u' + i).token);

  calls = 0;
  assert.strictEqual(users.verify('token-che-non-esiste'), null);
  assert.strictEqual(calls, 0, 'un token sbagliato non deve calcolare scrypt');

  // Un token buono costa un solo confronto.
  calls = 0;
  assert.ok(users.verify(tokens[49]));
  assert.strictEqual(calls, 1);
});

test('un record scritto prima dell indice viene trovato e indicizzato', () => {
  const file = tempFile();
  const users = store(file);
  const { token, user } = users.register('vincenzo');

  // Simula un file vecchio: senza tokenIndex.
  delete user.tokenIndex;
  users.save();

  const reloaded = store(file);
  const found = reloaded.verify(token);
  assert.ok(found, 'il token del record vecchio deve ancora verificare');
  assert.ok(found.tokenIndex, 'il primo accesso deve aggiungere l indice');
  assert.ok(found.tokenIndex === reloaded.verify(token).tokenIndex);
});

test('parseToken legge solo una stringa, e la ripulisce', () => {
  assert.strictEqual(parseToken({ Token: '  abc  ' }), 'abc');
  assert.strictEqual(parseToken({ Token: 42 }), '');
  assert.strictEqual(parseToken(null), '');
});

test('un token generato dal telefono verifica, e uno diverso no', () => {
  const users = store(tempFile());
  const phone = 'token-generato-dal-telefono-abcdefghijklmnop';

  const created = users.registerWithToken(phone, 'phone-1', 'vincenzo');
  assert.ok(created);
  assert.strictEqual(created.existing, false);
  assert.strictEqual(created.user.name, 'vincenzo');

  assert.ok(users.verify(phone), 'il token del telefono deve entrare');
  assert.strictEqual(users.verify('un-altro-token-qualunque'), null);
  assert.strictEqual(users.verify(''), null);
});

test('registerWithToken senza token non crea nulla', () => {
  const users = store(tempFile());
  assert.strictEqual(users.registerWithToken('', 'phone-1', 'x'), null);
  assert.strictEqual(users.count(), 0);
});

test('il token del telefono non si ricava dal segreto del deposito', () => {
  const file = tempFile();
  const users = store(file);
  const phone = 'token-generato-dal-telefono-abcdefghijklmnop';
  users.registerWithToken(phone, 'phone-1', 'vincenzo');

  const secret = JSON.parse(fs.readFileSync(file, 'utf8')).secret;
  // La derivazione che il server usava prima: se il token del telefono fosse
  // ancora ricavabile da questo segreto, una copia del file sarebbe una copia
  // della credenziale.
  assert.notStrictEqual(deviceToken(secret, 'phone-1'), phone);
  assert.strictEqual(users.verify(deviceToken(secret, 'phone-1')), null);
});

test('un dispositivo noto che si riaccoppia occupa la sua riga, non una nuova', () => {
  const users = store(tempFile());
  const primo = 'token-generato-dal-telefono-aaaaaaaaaaaaaaaa';
  const secondo = 'token-generato-dal-telefono-bbbbbbbbbbbbbbbb';

  users.registerWithToken(primo, 'phone-1', 'vincenzo');
  const again = users.registerWithToken(secondo, 'phone-1', 'vincenzo');

  assert.strictEqual(users.count(), 1, 'non nasce una seconda riga');
  assert.strictEqual(again.existing, true);
  assert.strictEqual(users.verify(primo), null, 'il token vecchio non vale piu');
  assert.ok(users.verify(secondo), 'quello nuovo entra');
  assert.strictEqual(users.findByClientId('phone-1').id, again.user.id);
});

test('un token generato dal telefono sopravvive a un riavvio del processo', () => {
  const file = tempFile();
  const phone = 'token-generato-dal-telefono-abcdefghijklmnop';
  store(file).registerWithToken(phone, 'phone-1', 'vincenzo');

  const reloaded = store(file);
  assert.ok(reloaded.verify(phone));
  assert.strictEqual(reloaded.findByClientId('phone-1').name, 'vincenzo');
});
