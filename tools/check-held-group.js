#!/usr/bin/env node
/**
 * tools/check-held-group.js
 *
 * Guard for a group message that waits for its sender key.
 *
 * Perche' esiste: un `skmsg` e la sender key del membro che l'ha scritto sono
 * due stanze, e l'ordine e' del server, non del mittente. Il messaggio che
 * arrivava per primo veniva rifiutato con "this phone holds no sender key for
 * ...", e la connessione rispondeva con un retry receipt: si vedeva solo se il
 * mittente lo rimandava, e la chat restava vuota fino ad allora. La chiave e'
 * quasi sempre un istante dietro, quindi il messaggio si tiene
 * (`HeldGroupMessages`) e si legge quando la chiave arriva.
 *
 * Nessun test copre quella cucitura: `ProtocolTests` compila il protocollo e
 * non `Services/NativeConnection.cs`, che e' dove il messaggio viene tenuto o
 * buttato. Questa guardia legge quel file.
 *
 * Regole:
 *  1. `IncomingMessage.cs` rifiuta un gruppo senza chiave con
 *     `SenderKeyMissingException`, non con un `ProtocolException` generico:
 *     il chiamante non puo' tenere cio' che non sa riconoscere;
 *  2. `NativeConnection.cs` ha un `catch (SenderKeyMissingException)` che mette
 *     la stanza da parte (`_held.Hold`), e in quel blocco non manda ne' un ack
 *     (butterebbe via il messaggio) ne' un retry receipt (metterebbe il
 *     messaggio in corsa con una chiave gia' partita);
 *  3. dopo `ReadMessageAsync` il giro chiama `RetryHeldAsync`: senza quella
 *     riga il messaggio tenuto non viene piu' letto da nessuno;
 *  4. `HeldGroupMessages.cs` ha i suoi due tetti: un deposito che aspetta per
 *     sempre e' una fuga di memoria con una buona scusa.
 *
 * Usage:
 *   node tools/check-held-group.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const INCOMING = 'WhatsappApp/Protocol/Client/IncomingMessage.cs';
const CONNECTION = 'WhatsappApp/Services/NativeConnection.cs';
const HELD = 'WhatsappApp/Protocol/Client/HeldGroupMessages.cs';

const CATCH = 'catch (SenderKeyMissingException';
const FAILURE = 'if (failure != null)';

/**
 * I problemi dei tre sorgenti: vuoto quando un messaggio senza chiave viene
 * tenuto e poi letto.
 */
function problemsFor(files) {
  const problems = [];

  if (files.incoming.indexOf('new SenderKeyMissingException(') < 0) {
    problems.push(`${INCOMING}: a group message with no sender key is refused without a ` +
      'SenderKeyMissingException (a plain ProtocolException will do): the connection cannot ' +
      'tell it from a message that will never open, so it asks the sender for it again ' +
      'instead of keeping it');
  }

  const code = files.connection;
  const at = code.indexOf(CATCH);
  if (at < 0) {
    problems.push(`${CONNECTION}: no ${CATCH}: a message that arrives before its member's ` +
      'sender key is answered with a retry receipt, and it appears only if the sender sends ' +
      'it a second time - the key is normally a stanza behind');
  } else {
    const end = code.indexOf(FAILURE, at);
    const block = end < 0 ? code.slice(at) : code.slice(at, end);

    if (block.indexOf('_held.Hold(') < 0) {
      problems.push(`${CONNECTION}: the SenderKeyMissingException is caught and the stanza is ` +
        'not held (`_held.Hold`): nothing will read it when the key arrives');
    }
    if (block.indexOf('IncomingMessage.Ack(') >= 0) {
      problems.push(`${CONNECTION}: a held message is acknowledged before it is read: the ` +
        'server then owns no copy of it, and a phone that ends before the key comes loses the ' +
        'message for good (this is the order every other ack in this file exists to keep)');
    }
    if (block.indexOf('AskAgainAsync(') >= 0) {
      problems.push(`${CONNECTION}: a held message is asked again as well as held: the retry ` +
        'puts the message in a race with a key that has already been sent, and it is the copy ' +
        'that arrives twice that gets read');
    }
  }

  const read = code.indexOf('await ReadMessageAsync(session, account, node);');
  const drain = code.indexOf('await RetryHeldAsync(');
  if (drain < 0) {
    problems.push(`${CONNECTION}: nothing calls RetryHeldAsync: the held messages are kept and ` +
      'never read, which is worse than the refusal they replaced');
  } else if (read >= 0 && drain < read) {
    problems.push(`${CONNECTION}: RetryHeldAsync runs before ReadMessageAsync: the key that the ` +
      'stanza just brought would be looked for before it is stored');
  }

  if (files.held.indexOf('MaxPerSender') < 0 || files.held.indexOf('MaxTotal') < 0) {
    problems.push(`${HELD}: no MaxPerSender/MaxTotal caps: a member whose key never arrives ` +
      'grows the store for the life of the app');
  }

  return problems;
}

function main() {
  const read = (file) => fs.readFileSync(path.join(ROOT, file), 'utf8');
  const problems = problemsFor({
    incoming: read(INCOMING),
    connection: read(CONNECTION),
    held: read(HELD)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} held-group problem(s).`);
    process.exit(1);
  }
  console.log('OK: a group message that arrives before its sender key is held unacknowledged ' +
    'and read when the key arrives.');
}

if (require.main === module) main();

module.exports = {
  problemsFor,
  INCOMING,
  CONNECTION,
  HELD
};
