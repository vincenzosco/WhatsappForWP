'use strict';
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');
const fs = require('node:fs');

const guard = require('../check-notification-ack');

const SIGNATURE = 'private async Task ReadNotificationAsync(NativeSession session, BinaryNode node)';

/** Un corpo di metodo con le parti che il guard cerca, una per caso. */
function method(parts) {
  return '        ' + SIGNATURE + '\n'
    + '        {\n'
    + parts.join('\n')
    + '\n        }\n'
    + '\n        /// <summary>il metodo dopo</summary>\n';
}

/** L'ack come lo scrive il progetto: protetto, nominato, e dopo il controllo dell'id. */
const GUARDED_ACK = [
  '            string ackId = node == null ? null : node.Attrs["id"];',
  '            if (!string.IsNullOrEmpty(ackId))',
  '            {',
  '                try',
  '                {',
  '                    await session.SendAsync(IncomingMessage.Ack(node));',
  '                    Diag.Step("native: acked notification " + ackId);',
  '                }',
  '                catch (Exception error)',
  '                {',
  '                    Diag.Failed("NativeConnection/notification-ack", error);',
  '                }',
  '            }'
];

test('una notifica riconosciuta prima di leggerla non e un problema', () => {
  const source = method(GUARDED_ACK.concat(['            if (ClientState.IsClientRefusal(node)) return;']));
  assert.deepStrictEqual(guard.problemsFor(source), []);
});

test('un handler che non risponde mai e un problema', () => {
  const source = method(['            if (ClientState.IsClientRefusal(node)) return;']);
  const problems = guard.problemsFor(source);

  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /never sends IncomingMessage\.Ack/);
});

test('un ack mandato dopo la lettura e un problema, perche C# 5 non ammette await in un catch', () => {
  const source = method(['            if (ClientState.IsClientRefusal(node)) return;']
    .concat(GUARDED_ACK));

  const problems = guard.problemsFor(source);

  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /after the notification is read/);
});

test('un ack senza il controllo dell id e un problema', () => {
  const source = method([
    '            try',
    '            {',
    '                await session.SendAsync(IncomingMessage.Ack(node));',
    '                Diag.Step("native: acked notification " + node.Attrs["id"]);',
    '            }',
    '            catch (Exception error)',
    '            {',
    '                Diag.Failed("NativeConnection/notification-ack", error);',
    '            }',
    '            if (ClientState.IsClientRefusal(node)) return;'
  ]);

  const problems = guard.problemsFor(source);

  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /whether the node has an `id`/);
});

test('un ack non protetto e un problema', () => {
  const source = method([
    '            string ackId = node.Attrs["id"];',
    '            await session.SendAsync(IncomingMessage.Ack(node));',
    '            Diag.Step("native: acked notification " + ackId);',
    '            if (ClientState.IsClientRefusal(node)) return;'
  ]);

  const problems = guard.problemsFor(source);

  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /not .*protected|is not/);
});

test('un ack che non si nomina nel log e un problema', () => {
  const source = method([
    '            string ackId = node.Attrs["id"];',
    '            if (!string.IsNullOrEmpty(ackId))',
    '            {',
    '                try',
    '                {',
    '                    await session.SendAsync(IncomingMessage.Ack(node));',
    '                }',
    '                catch (Exception error)',
    '                {',
    '                    Diag.Failed("NativeConnection/notification-ack", error);',
    '                }',
    '            }',
    '            if (ClientState.IsClientRefusal(node)) return;'
  ]);

  const problems = guard.problemsFor(source);

  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /not named in the log/);
});

test('un commento che nomina l ack non conta come un ack', () => {
  const source = method([
    '            // await session.SendAsync(IncomingMessage.Ack(node));',
    '            if (ClientState.IsClientRefusal(node)) return;'
  ]);

  assert.match(guard.problemsFor(source)[0], /never sends IncomingMessage\.Ack/);
});

test('il NativeConnection.cs del progetto riconosce le notifiche', () => {
  const file = path.join(__dirname, '..', '..', 'WhatsappApp', 'Services', 'NativeConnection.cs');
  assert.deepStrictEqual(guard.problemsFor(fs.readFileSync(file, 'utf8')), []);
});
