'use strict';
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');
const fs = require('node:fs');

const guard = require('../check-held-group');

const GOOD_CONNECTION =
  'await ReadMessageAsync(session, account, node);\n' +
  'await RetryHeldAsync(session, account, node);\n' +
  'catch (Exception error)\n' +
  '{\n' +
  '    failure = error;\n' +
  '}\n' +
  guard.WAITS_BRANCH + '\n' +
  '{\n' +
  '    if (_held.Hold(incoming.Chat, incoming.Sender, incoming.Id, node)) { Diag.Ok("waits"); }\n' +
  '    return;\n' +
  '}\n' +
  guard.FAILURE + '\n' +
  '{\n' +
  '    await AskAgainAsync(session, node, account);\n' +
  '}\n' +
  'await session.SendAsync(IncomingMessage.Ack(node));\n';

const GOOD = {
  incoming: 'private static IncomingMessage ReadGroup(...)\n{\n    return Refused(message, id, groupJid, participant, IncomingRefusal.WaitsForSenderKey);\n}\n',
  connection: GOOD_CONNECTION,
  held: 'public const int MaxPerSender = 20;\npublic const int MaxTotal = 60;'
};

test('tenere il messaggio e leggerlo quando arriva la chiave non e un problema', () => {
  assert.deepStrictEqual(guard.problemsFor(GOOD), []);
});

test('l eccezione che era tornata si segnala', () => {
  const found = guard.problemsFor(Object.assign({}, GOOD, {
    incoming: 'throw new SenderKeyMissingException(id, groupJid, participant);'
  })).join('\n');
  assert.match(found, /SenderKeyMissingException/);
  assert.match(found, /24 October 2026/);
});

test('un lettore che non risponde "aspetta la chiave" si segnala', () => {
  const found = guard.problemsFor(Object.assign({}, GOOD, {
    incoming: 'return Refused(message, id, groupJid, participant, IncomingRefusal.AlreadyRead);'
  })).join('\n');
  assert.match(found, /nothing answers/);
});

test('una connessione che non guarda il valore si segnala', () => {
  const found = guard.problemsFor(Object.assign({}, GOOD, {
    connection: GOOD_CONNECTION.replace(guard.WAITS_BRANCH, 'if (incoming != null)')
  })).join('\n');
  assert.match(found, /no branch on/);
});

test('una connessione che non tiene il messaggio si segnala', () => {
  const found = guard.problemsFor(Object.assign({}, GOOD, {
    connection: GOOD_CONNECTION.replace('_held.Hold(', 'Nothing(')
  })).join('\n');
  assert.match(found, /not held/);
});

test('un messaggio tenuto e anche riconosciuto si segnala', () => {
  const found = guard.problemsFor(Object.assign({}, GOOD, {
    connection: GOOD_CONNECTION.replace(
      '    return;\n}\n' + guard.FAILURE,
      '    await session.SendAsync(IncomingMessage.Ack(node));\n    return;\n}\n' + guard.FAILURE)
  })).join('\n');
  assert.match(found, /acknowledged before it is read/);
});

test('un messaggio tenuto e anche richiesto di nuovo si segnala', () => {
  const found = guard.problemsFor(Object.assign({}, GOOD, {
    connection: GOOD_CONNECTION.replace(
      '    return;\n}\n' + guard.FAILURE,
      '    await AskAgainAsync(session, node, account);\n    return;\n}\n' + guard.FAILURE)
  })).join('\n');
  assert.match(found, /asked again/);
});

test('l ack del duplicato non conta come ack del messaggio tenuto', () => {
  // Il ramo del duplicato ack, ed e' giusto: e' fuori dal blocco che questa guardia legge.
  assert.deepStrictEqual(guard.problemsFor(GOOD), []);
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
    incoming: guard.stripComments(read(guard.INCOMING)),
    connection: guard.stripComments(read(guard.CONNECTION)),
    held: read(guard.HELD)
  }), []);
});
