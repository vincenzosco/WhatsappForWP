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
 * Come risposta era un `SenderKeyMissingException`, e il 24 ottobre 2026 quella
 * eccezione era una riga di `Eccezione generata` nel debugger per ogni gruppo
 * che scriveva prima della chiave - cioe' il caso normale. Adesso e' un valore
 * (`IncomingRefusal.WaitsForSenderKey`) e la connessione tiene la stanza
 * guardando quello. Questa guardia pinna la proprieta', non il tipo: il
 * messaggio si tiene, non si ack, non si richiede.
 *
 * Regole:
 *  1. `IncomingMessage.cs` non lancia piu' `SenderKeyMissingException`: con quel
 *     `throw` torna una eccezione per ogni messaggio di gruppo senza chiave;
 *  2. `ReadGroup` risponde `IncomingRefusal.WaitsForSenderKey` quando il record
 *     della chiave non c'e';
 *  3. `NativeConnection.cs` guarda quel valore (`incoming.Refused ==`) e in quel
 *     ramo mette la stanza da parte (`_held.Hold`), senza ack (butterebbe via il
 *     messaggio) e senza retry (metterebbe il messaggio in corsa con una chiave
 *     gia' partita);
 *  4. dopo `ReadMessageAsync` il giro chiama `RetryHeldAsync`: senza quella riga
 *     il messaggio tenuto non viene piu' letto da nessuno;
 *  5. `HeldGroupMessages.cs` ha i suoi due tetti: un deposito che aspetta per
 *     sempre e' una fuga di memoria con una buona scusa;
 *  6. un payload che la chiave apre ma che non e' un messaggio risponde
 *     `IncomingRefusal.Unreadable`, e la connessione lo ack invece di richiederlo:
 *     gli stessi byte richiesti di nuovo sono gli stessi byte. La corsa del 24
 *     ottobre 2026 ha richiesto quattro messaggi danneggiati su ogni connessione.
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

const WAITING = 'IncomingRefusal.WaitsForSenderKey';
const WAITS_BRANCH = 'if (incoming != null && incoming.Refused == IncomingRefusal.WaitsForSenderKey)';

/// Il blocco che viene dopo: il ramo del tenuto finisce dove comincia il guasto.
const FAILURE = 'if (failure != null)';

/**
 * Il sorgente senza i commenti: questa guardia legge il codice, e la prosa accanto
 * racconta l'eccezione che e' stata tolta.
 */
function stripComments(text) {
  return text
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/\/\/[^\n]*/g, '');
}

/**
 * I problemi dei tre sorgenti: vuoto quando un messaggio senza chiave viene
 * tenuto, non ackato, non richiesto, e letto quando la chiave arriva.
 */
function problemsFor(files) {
  const problems = [];

  if (files.incoming.indexOf('SenderKeyMissingException') >= 0) {
    problems.push(`${INCOMING}: a SenderKeyMissingException is back: a group message that arrives ` +
      'before its member\'s sender key is the ordinary way a group starts, and a first-chance ' +
      'exception per message is what the debugger printed on 24 October 2026 - the answer is ' +
      `the value ${WAITING}`);
  }

  if (files.incoming.indexOf(WAITING) < 0) {
    problems.push(`${INCOMING}: nothing answers ${WAITING}: a group message without its key ` +
      'has nowhere to say it is waiting instead of broken');
  }

  const at = files.connection.indexOf(WAITS_BRANCH);
  if (at < 0) {
    problems.push(`${CONNECTION}: no branch on ${WAITING}: a message that waits for its key is ` +
      'read as an ordinary one, and nothing holds it');
  } else {
    const end = files.connection.indexOf(FAILURE, at);
    const block = end < 0 ? files.connection.slice(at) : files.connection.slice(at, end);

    if (block.indexOf('_held.Hold(') < 0) {
      problems.push(`${CONNECTION}: the waiting stanza is not held ('_held.Hold'): nothing will ` +
        'read it when the key arrives');
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

  const read = files.connection.indexOf('await ReadMessageAsync(session, account, node);');
  const drain = files.connection.indexOf('await RetryHeldAsync(');
  if (drain < 0) {
    problems.push(`${CONNECTION}: nothing calls RetryHeldAsync: the held messages are kept and ` +
      'never read, which is worse than the refusal they replaced');
  } else if (read >= 0 && drain < read) {
    problems.push(`${CONNECTION}: RetryHeldAsync runs before ReadMessageAsync: the key that the ` +
      'stanza just brought would be looked for before it is stored');
  }

  // 6. A group payload that opens to bytes that are not a message.
  const unreadable = 'IncomingRefusal.Unreadable';
  if (files.incoming.indexOf(unreadable) < 0) {
    problems.push(`${INCOMING}: nothing answers ${unreadable}: a payload the key opens but that ` +
      'is not a message is a permanent refusal, and asked for again it is a retry per ' +
      'connection for ever (four of them on 24 October 2026)');
  } else {
    const groupAt = files.incoming.indexOf('private static IncomingMessage ReadGroup(');
    const group = groupAt < 0 ? '' : files.incoming.slice(groupAt);

    if (group.indexOf(unreadable) < 0) {
      problems.push(`${INCOMING}: ReadGroup never answers ${unreadable}: the decode that fails is ` +
        'thrown up as a fault and the connection asks for the same bytes again');
    } else if (group.indexOf('catch (ProtocolException') < 0) {
      problems.push(`${INCOMING}: ReadGroup answers ${unreadable} but not from a catch around the ` +
        'decode: the refusal has to name the bytes that did not read, not the whole stanza');
    }
  }

  const already = files.connection.indexOf('if (incoming.Refused == IncomingRefusal.AlreadyRead)');
  const unreadableAt = files.connection.indexOf(`if (incoming.Refused == ${unreadable})`);
  if (unreadableAt < 0) {
    problems.push(`${CONNECTION}: no branch on ${unreadable}: a damaged copy is read as an ` +
      'ordinary failure and asked for again, which is the loop it exists to end');
  } else {
    if (already >= 0 && unreadableAt < already) {
      problems.push(`${CONNECTION}: the unreadable branch sits above the duplicate one: both are ` +
        'answered after the ack, and an ack above the read is the order this file exists to keep');
    }

    const slice = files.connection.slice(unreadableAt, unreadableAt + 700);
    if (slice.indexOf('AskAgainAsync(') >= 0) {
      problems.push(`${CONNECTION}: a damaged copy is asked for again: the same bytes come back, ` +
        'and the group is asked for them on every connection');
    }
    if (slice.indexOf('Diag.Ok(') < 0) {
      problems.push(`${CONNECTION}: a damaged copy is dropped without a line (no Diag.Ok): the ` +
        'message is then missing from the chat and the log says nothing about it');
    }
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
    incoming: stripComments(read(INCOMING)),
    connection: stripComments(read(CONNECTION)),
    held: read(HELD)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} held-group problem(s).`);
    process.exit(1);
  }
  console.log('OK: a group message that arrives before its sender key is held unacknowledged, ' +
    'not asked again, and read when the key arrives.');
}

if (require.main === module) main();

module.exports = {
  problemsFor,
  stripComments,
  WAITING,
  WAITS_BRANCH,
  FAILURE,
  INCOMING,
  CONNECTION,
  HELD
};
