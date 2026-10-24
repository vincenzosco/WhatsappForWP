#!/usr/bin/env node
/**
 * tools/check-avatar-refusal.js
 *
 * Guard for the two ordinary answers to a picture request.
 *
 * Perche' esiste: `RequestAvatarAsync` rispondeva a "questa chat non ha una foto
 * da darmi" e a "non c'e' ancora un account" con due `ProtocolException`, e
 * `AvatarEnricher` le distingueva guardando `IsLoggedIn`. La prima versione
 * della guardia pinno' quella divisione dopo che il 24 ottobre 2026 una lista
 * di 169 righe resto' sulle iniziali: ogni rifiuto era letto come "aspetta il
 * login", il giro ripartiva dalla prima riga ogni tre secondi e non chiedeva
 * mai le righe dopo.
 *
 * Il secondo giro, quello che ha tolto il rumore dal debugger, ha portato le
 * due risposte **dentro il valore**: `AvatarAnswer.NoSession` e
 * `AvatarAnswer.Refused`. Una eccezione first-chance e stampata da ogni
 * debugger anche quando il chiamante la prende subito, e queste due non sono
 * guasti: sono la risposta.
 *
 * Regole:
 *  1. `RequestAvatarAsync` risponde `AvatarAnswer` e non lancia;
 *  2. `NoSession` e' deciso da `IsLoggedIn`, prima di chiedere: la lista parte
 *     prima del login e una eccezione per riga e' cio' che questo valore toglie;
 *  3. `Refused` e' deciso dall'`iq type="error"` del server;
 *  4. nell'enricher `NoSession` dimentica la chat (`Forget`), riparte dalla
 *     prima riga (`index = 0`) e aspetta `NoSessionDelayMs`;
 *  5. nell'enricher `Refused` **non** fa nessuna delle tre: la chat ha risposto
 *     per questo giro, e il giro va avanti con `DelayMs`;
 *  6. i rifiuti nominati nel log hanno un tetto (`MaxRefusedLines`).
 *
 * Usage:
 *   node tools/check-avatar-refusal.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const ENRICHER = 'WhatsappApp/Services/AvatarEnricher.cs';
const CONNECTION = 'WhatsappApp/Services/NativeConnection.cs';

const WAITING = 'answer.NoSession';
const REFUSED = 'answer.Refused';

/** The body of `signature`'s method, braces matched. Null when it is not there. */
function bodyAt(source, signature) {
  const at = source.indexOf(signature);
  if (at < 0) return null;

  const open = source.indexOf('{', at + signature.length);
  if (open < 0) return null;

  let depth = 0;
  for (let i = open; i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}') {
      depth--;
      if (depth === 0) return source.slice(open, i + 1);
    }
  }

  return null;
}

/**
 * I problemi dei due sorgenti: vuoto quando un rifiuto e' una risposta e un
 * login mancante e' un'attesa, senza eccezioni in mezzo.
 */
function problemsFor(files) {
  const problems = [];

  const ask = bodyAt(files.connection, 'public async Task<AvatarAnswer> RequestAvatarAsync(');
  if (!ask) {
    problems.push(`${CONNECTION}: no RequestAvatarAsync returning AvatarAnswer: the refusals are ` +
      'exceptions again, and a first-chance exception is printed by the debugger even when the ' +
      'caller catches it at once');
  } else {
    if (ask.indexOf('throw new') >= 0) {
      problems.push(`${CONNECTION}: RequestAvatarAsync throws: a chat with no picture and a ` +
        'login that has not happened are answers, and they are already in the AvatarAnswer it ' +
        'returns');
    }
    if (ask.indexOf('picture.NoSession = true') < 0) {
      problems.push(`${CONNECTION}: RequestAvatarAsync never answers NoSession: the chat list's ` +
        'pass asks before the login, and that is the one case it has to wait for');
    }
    if (ask.indexOf('picture.Refused = true') < 0) {
      problems.push(`${CONNECTION}: RequestAvatarAsync never answers Refused: an iq of type ` +
        'error is the server saying this chat has no picture for this account');
    }
    if (ask.indexOf('IsLoggedIn') < 0) {
      problems.push(`${CONNECTION}: RequestAvatarAsync does not ask IsLoggedIn before sending: ` +
        'a socket that is not there is what made one exception per row before the login');
    }
  }

  const at = files.enricher.indexOf(WAITING);
  const refusedAt = files.enricher.indexOf(REFUSED);
  if (at < 0 || refusedAt < 0) {
    problems.push(`${ENRICHER}: the pass does not branch on ${WAITING} and ${REFUSED}: the two ` +
      'answers are one again, and a refusal restarts the list every three seconds');
    return problems;
  }

  const waitingBlock = refusedAt > at ? files.enricher.slice(at, refusedAt) : '';
  const refusedEnd = files.enricher.indexOf('if (wait > 0)', refusedAt);
  const refusedBlock = refusedEnd < 0 ? files.enricher.slice(refusedAt) : files.enricher.slice(refusedAt, refusedEnd);

  if (waitingBlock.indexOf('Forget(id)') < 0) {
    problems.push(`${ENRICHER}: a chat asked with no account is not forgotten ('Forget'): the ` +
      'one question it gets is spent, and the login it waited for never draws it');
  }
  if (waitingBlock.indexOf('index = 0') < 0) {
    problems.push(`${ENRICHER}: a missing account does not restart the list ('index = 0'): the ` +
      'rows the pass stepped over keep their initials for the whole run');
  }
  if (waitingBlock.indexOf('NoSessionDelayMs') < 0) {
    problems.push(`${ENRICHER}: a missing account is not waited for ('NoSessionDelayMs'): the ` +
      'pass asks again at full speed against a socket that is not there');
  }

  if (refusedBlock.indexOf('Forget(') >= 0) {
    problems.push(`${ENRICHER}: a refused chat is forgotten: the pass asks the same chat again ` +
      'and again, and the row that answered no is asked on every restart of the list');
  }
  if (refusedBlock.indexOf('index = 0') >= 0) {
    problems.push(`${ENRICHER}: a refused chat restarts the list ('index = 0'): every row after ` +
      'the refusing one is never asked - the chat list of 24 October 2026 stayed on its ' +
      'initials with 169 rows and a working login');
  }
  if (refusedBlock.indexOf('NoSessionDelayMs') >= 0) {
    problems.push(`${ENRICHER}: a refusal is waited for as if the account were missing ` +
      "('NoSessionDelayMs'): three seconds per refusing chat, for a question that is answered");
  }
  if (refusedBlock.indexOf('DelayMs') < 0) {
    problems.push(`${ENRICHER}: a refusal does not take the ordinary pause ('DelayMs') between ` +
      'two chats: the pass either spins or stops on the first refusal');
  }

  if (files.enricher.indexOf('MaxRefusedLines') < 0) {
    problems.push(`${ENRICHER}: every refusal is named in the log: on this phone that can be ` +
      'one line per LID in the list (see MaxRefusedLines)');
  }

  return problems;
}

function main() {
  const read = (file) => fs.readFileSync(path.join(ROOT, file), 'utf8');
  const problems = problemsFor({ enricher: read(ENRICHER), connection: read(CONNECTION) });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} avatar-refusal problem(s).`);
    process.exit(1);
  }
  console.log('OK: a chat that refuses its picture is an answer, and a missing account is a ' +
    'wait - neither is an exception.');
}

if (require.main === module) main();

module.exports = {
  problemsFor,
  bodyAt,
  WAITING,
  REFUSED,
  ENRICHER,
  CONNECTION
};
