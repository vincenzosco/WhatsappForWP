'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const guard = require('../check-avatar-refusal');

const ROOT = path.resolve(__dirname, '..', '..');

// La forma dei due rami che il guard legge, e il metodo che produce le due
// risposte. Ogni fixture cambia un punto solo.
function enricher(options) {
  const opts = options || {};

  const waiting = '                    if (answer != null && answer.NoSession)\n'
    + '                    {\n'
    + (opts.forgetWaiting === false ? '' : '                        Forget(id);\n')
    + (opts.restart === false ? '' : '                        index = 0;\n')
    + (opts.waitForLogin === false ? '' : '                        wait = NoSessionDelayMs;\n')
    + '                    }\n';

  const refused = opts.noRefused ? '' : '                    else if (answer != null && answer.Refused)\n'
    + '                    {\n'
    + (opts.forgetRefused ? '                        Forget(id);\n' : '')
    + (opts.restartRefused ? '                        index = 0;\n' : '')
    + (opts.waitRefused ? '                        wait = NoSessionDelayMs;\n' : '')
    + (opts.pause === false ? '' : '                        wait = DelayMs;\n')
    + '                    }\n';

  return 'private static async Task RunAsync(int generation)\n'
    + '        {\n'
    + waiting + refused
    + '                    if (wait > 0)\n'
    + '                    {\n'
    + '                    }\n'
    + (opts.noCap ? '' : '        private const int MaxRefusedLines = 5;\n')
    + '        }\n';
}

function connection(options) {
  const opts = options || {};

  return 'public async Task<AvatarAnswer> RequestAvatarAsync(string jid, bool preview)\n'
    + '        {\n'
    + '            var picture = new AvatarAnswer();\n'
    + (opts.noLoginCheck ? '' : '            if (!IsLoggedIn)\n'
      + '            {\n'
      + '                picture.NoSession = true;\n'
      + '                return picture;\n'
      + '            }\n')
    + (opts.noRefusal ? '' : '            if (answer.Attrs["type"] == "error")\n'
      + '            {\n'
      + '                picture.Refused = true;\n'
      + '                return picture;\n'
      + '            }\n')
    + (opts.throws ? '            throw new ProtocolException("no");\n' : '')
    + '            return picture;\n'
    + '        }\n';
}

test('la forma vera non si segnala', () => {
  assert.deepStrictEqual(guard.problemsFor({ enricher: enricher(), connection: connection() }), []);
});

test('una richiesta che lancia si segnala, ed e il rumore del 24 ottobre', () => {
  const problems = guard.problemsFor({ enricher: enricher(), connection: connection({ throws: true }) });
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /RequestAvatarAsync throws/);
});

test('una richiesta che non risponde NoSession si segnala', () => {
  const problems = guard.problemsFor({ enricher: enricher(), connection: connection({ noLoginCheck: true }) });
  assert.ok(problems.some((p) => /never answers NoSession/.test(p) || /does not ask IsLoggedIn/.test(p)));
});

test('una richiesta che non risponde Refused si segnala', () => {
  const problems = guard.problemsFor({ enricher: enricher(), connection: connection({ noRefusal: true }) });
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /never answers Refused/);
});

test('un metodo assente si segnala', () => {
  const problems = guard.problemsFor({ enricher: enricher(), connection: 'nothing here\n' });
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /no RequestAvatarAsync/);
});

test('un rifiuto che dimentica la chat si segnala', () => {
  const problems = guard.problemsFor({ enricher: enricher({ forgetRefused: true }), connection: connection() });
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /refused chat is forgotten/);
});

test('un rifiuto che riparte dalla prima riga si segnala', () => {
  const problems = guard.problemsFor({ enricher: enricher({ restartRefused: true }), connection: connection() });
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /restarts the list/);
  assert.match(problems[0], /169 rows/);
});

test('un rifiuto aspettato come un login mancante si segnala', () => {
  const problems = guard.problemsFor({ enricher: enricher({ waitRefused: true }), connection: connection() });
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /as if the account were missing/);
});

test('un rifiuto senza la pausa ordinaria si segnala', () => {
  const problems = guard.problemsFor({ enricher: enricher({ pause: false }), connection: connection() });
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /ordinary pause/);
});

test('l attesa del login dimentica la chat, riparte e aspetta', () => {
  const problems = guard.problemsFor({
    enricher: enricher({ forgetWaiting: false, restart: false, waitForLogin: false }),
    connection: connection()
  });
  assert.strictEqual(problems.length, 3);
  assert.ok(problems.some((p) => /is not forgotten/.test(p)));
  assert.ok(problems.some((p) => /does not restart the list/.test(p)));
  assert.ok(problems.some((p) => /is not waited for/.test(p)));
});

test('un ramo del rifiuto assente si segnala', () => {
  const problems = guard.problemsFor({ enricher: enricher({ noRefused: true }), connection: connection() });
  assert.match(problems[0], /does not branch on/);
});

test('i rifiuti nominati nel log hanno un tetto', () => {
  const problems = guard.problemsFor({ enricher: enricher({ noCap: true }), connection: connection() });
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /MaxRefusedLines/);
});

test('il servizio vero risponde con i due valori', () => {
  const read = (file) => fs.readFileSync(path.join(ROOT, file), 'utf8');
  assert.deepStrictEqual(guard.problemsFor({
    enricher: read(guard.ENRICHER),
    connection: read(guard.CONNECTION)
  }), []);
});
