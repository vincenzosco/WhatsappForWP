'use strict';
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');
const fs = require('node:fs');

const guard = require('../check-held-group');

const GOOD_CONNECTION =
  'await ReadMessageAsync(session, account, node);\n' +
  'await RetryHeldAsync(session, account, node);\n' +
  'catch (SenderKeyMissingException missing)\n' +
  '{\n' +
  '    waiting = missing;\n' +
  '}\n' +
  'if (waiting != null)\n' +
  '{\n' +
  '    if (_held.Hold(waiting.GroupJid, waiting.Participant, waiting.MessageId, node)) { Diag.Ok("waits"); }\n' +
  '    return;\n' +
  '}\n';

const GOOD = {
  incoming: 'throw new SenderKeyMissingException(id, groupJid, participant);',
  connection: GOOD_CONNECTION,
  held: 'public const int MaxPerSender = 20;\npublic const int MaxTotal = 60;'
};

test('tenere il messaggio e leggerlo quando arriva la chiave non e un problema', () => {
  assert.deepStrictEqual(guard.problemsFor(GOOD), []);
});

test('un rifiuto generico senza chiave si segnala', () => {
  const found = guard.problemsFor(Object.assign({}, GOOD, {
    incoming: 'throw new ProtocolException("this phone holds no sender key");'
  })).join('\n');
  assert.match(found, /SenderKeyMissingException/);
});

test('una connessione che non tiene il messaggio si segnala', () => {
  const found = guard.problemsFor(Object.assign({}, GOOD, {
    connection: GOOD_CONNECTION.replace('_held.Hold(', 'Nothing(')
  })).join('\n');
  assert.match(found, /not held/);
});

test('un messaggio tenuto e anche riconosciuto si segnala', () => {
  const found = guard.problemsFor(Object.assign({}, GOOD, {
    connection: GOOD_CONNECTION + 'await session.SendAsync(IncomingMessage.Ack(node));\n'
  })).join('\n');
  assert.match(found, /acknowledged before it is read/);
});

test('un messaggio tenuto e anche richiesto di nuovo si segnala', () => {
  const found = guard.problemsFor(Object.assign({}, GOOD, {
    connection: GOOD_CONNECTION + 'await AskAgainAsync(session, node, account);\n'
  })).join('\n');
  assert.match(found, /asked again/);
});

test('un deposito senza tetti si segnala', () => {
  const found = guard.problemsFor(Object.assign({}, GOOD, {
    held: 'private readonly Dictionary<string, List<Held>> _bySender = new Dictionary<string, List<Held>>();'
  })).join('\n');
  assert.match(found, /caps/);
});

test('una connessione che non rilegge il deposito si segnala', () => {
  const found = guard.problemsFor(Object.assign({}, GOOD, {
    connection: GOOD_CONNECTION.replace('await RetryHeldAsync(session, account, node);', '')
  })).join('\n');
  assert.match(found, /RetryHeldAsync/);
});

test('i tre sorgenti del progetto passano la guardia', () => {
  const read = (file) => fs.readFileSync(path.join(__dirname, '..', '..', file), 'utf8');
  assert.deepStrictEqual(guard.problemsFor({
    incoming: read(guard.INCOMING),
    connection: read(guard.CONNECTION),
    held: read(guard.HELD)
  }), []);
});
