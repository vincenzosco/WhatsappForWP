#!/usr/bin/env node
/**
 * tools/check-handshake-answer.js
 *
 * Guard for the one thing that made a wrong cipher key look like a connection:
 * the app called itself connected the moment it had *written* the handshake.
 *
 * Why it exists: on 2026-10-07 a reinstall emptied the phone's storage, so the
 * phone wrote its frames with the passphrase compiled into the public app while
 * the server held a key of its own. The adapter cannot decrypt a frame it has no
 * key for, so it logged "invalid frame from the app: Invalid HMAC signature" and
 * left the socket open; nothing came back and nothing was refused. The app, which
 * had already raised `CommService_Connected` and `ConnectionEstablished` after
 * writing a frame nobody could read, showed a connected app that was mute - the
 * user's report was "it does not connect by itself". No frame arriving is the
 * only observable difference between that state and a working connection, so the
 * wait for the server's first answer is the thing this guard pins.
 *
 * Rules:
 *   A. `CommunicationService.cs` has a `HandshakeAnswerMs` of at least 20000 --
 *      the adapter's own answer to `hello` is a state frame, and on a cold
 *      session it reads the account first (measured at about 15 s on 2026-10-07);
 *   B. the connection is announced (`RaiseConnectionEstablished`) only after
 *      `WaitForServerAnswerAsync` was awaited;
 *   C. the wait reads both `LastInboundUtc` (has a frame arrived) and
 *      `_connectionId` (is this attempt still the published one);
 *   D. the failure names itself: `Diag.Failed("ConnectToServerAsync/no answer")`;
 *   E. the reader is already running when the wait starts, or the answer it polls
 *      for is never seen.
 *
 * Usage: node tools/check-handshake-answer.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const COMMUNICATION_REL = 'WhatsappApp/Services/CommunicationService.cs';

/**
 * The floor of rule A. It is the adapter's cold read of the account, measured on
 * the device run of 2026-10-07: `reading up to 25 conversation(s)` at 05:03:36
 * and `Listed chats successfully` at 05:03:51. A shorter wait closes a
 * connection that was about to work.
 */
const HANDSHAKE_ANSWER_FLOOR = 20000;

/**
 * The moment the app may call the connection up. The call, not the declaration:
 * the definition of the event raiser sits near the top of the file and would
 * otherwise be found before the wait and read as "announced too early".
 */
const ANNOUNCE = 'RaiseConnectionEstablished();';

/** The wait itself, as it is called. */
const WAIT_CALL = 'await WaitForServerAnswerAsync(';

/** The reader that has to be running before the wait begins. */
const READER_START = 'Task.Run(() => ListenForMessagesAsync(';

/**
 * The body of a method, from its declaration to the next member. The line that
 * declares it is the one that starts with a modifier: the same name also appears
 * on the line that *calls* it, and starting the slice there yielded the rest of
 * the file instead of the body.
 */
const MEMBER_LINE = /^\s*(private|public|internal|protected)\s/;

function bodyOf(source, name) {
  const lines = source.split('\n');

  let start = -1;
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].indexOf(name + '(') >= 0 && MEMBER_LINE.test(lines[i])) { start = i; break; }
  }
  if (start < 0) return '';

  // Al livello delle graffe, non alla riga del membro successivo: la prima
  // versione si fermava al prossimo `private`, che nel file vero arriva dopo il
  // `catch` che segue il metodo, e il corpo finiva per contenere anche quello -
  // cosi' `_connectionId` risultava presente anche quando il metodo non lo
  // leggeva piu'. Il test del guard l'ha trovato, questa e' la correzione.
  let depth = 0;
  let opened = false;
  const body = [];
  for (let i = start; i < lines.length; i++) {
    body.push(lines[i]);
    for (const ch of lines[i]) {
      if (ch === '{') { depth += 1; opened = true; }
      else if (ch === '}') depth -= 1;
    }
    if (opened && depth === 0) break;
  }
  return body.join('\n');
}

/** Everything wrong with the handshake answer, one line per problem. */
function problemsFor(sources) {
  const source = sources && typeof sources.communication === 'string'
    ? sources.communication
    : fs.readFileSync(path.join(ROOT, COMMUNICATION_REL), 'utf8');

  const problems = [];

  // A. How long the answer is waited for.
  const constant = /HandshakeAnswerMs\s*=\s*(\d+)/.exec(source);
  if (!constant) {
    problems.push('HandshakeAnswerMs is missing: the app waits for the server for no stated time');
  } else if (Number(constant[1]) < HANDSHAKE_ANSWER_FLOOR) {
    problems.push('HandshakeAnswerMs is ' + constant[1] + ', below the ' + HANDSHAKE_ANSWER_FLOOR
      + ' ms the adapter cold read was measured at');
  }

  // B. Nothing is announced before the server has answered.
  const announced = source.indexOf(ANNOUNCE);
  const waited = source.indexOf(WAIT_CALL);
  if (announced < 0) {
    problems.push('RaiseConnectionEstablished is missing: nothing announces the connection');
  } else if (waited < 0) {
    problems.push('the handshake answer is never waited for (' + WAIT_CALL + ' is missing): '
      + 'RaiseConnectionEstablished runs on a frame the server may never have read');
  } else if (announced < waited) {
    problems.push('RaiseConnectionEstablished runs before ' + WAIT_CALL
      + ': the connection is announced on a frame the server may never have read');
  }

  // C. The wait knows what it is watching.
  const wait = bodyOf(source, 'WaitForServerAnswerAsync');
  if (!wait) {
    problems.push('WaitForServerAnswerAsync is missing: nothing waits for the server');
  } else {
    if (wait.indexOf('LastInboundUtc') < 0) {
      problems.push('WaitForServerAnswerAsync never reads LastInboundUtc: it cannot see an answer');
    }
    if (wait.indexOf('_connectionId') < 0) {
      problems.push('WaitForServerAnswerAsync never reads _connectionId: it cannot tell its own '
        + 'attempt from the one that replaced it');
    }
  }

  // D. The silence has to say what it means.
  if (source.indexOf('Diag.Failed("ConnectToServerAsync/no answer"') < 0) {
    problems.push('the no-answer failure is not named: Diag.Failed("ConnectToServerAsync/no answer" is missing');
  }

  // E. The reader is running while the wait polls.
  const reader = source.indexOf(READER_START);
  if (reader < 0) {
    problems.push('the reader is not started with ' + READER_START
      + '): the frames that would answer never reach LastInboundUtc');
  } else if (waited >= 0 && reader > waited) {
    problems.push('the reader starts after ' + WAIT_CALL
      + ': the answer it polls for is read only once the connection was given up on');
  }

  return { problems };
}

if (require.main === module) {
  const { problems } = problemsFor({});
  if (problems.length) {
    console.error('check-handshake-answer: ' + problems.length + ' problem(s)');
    problems.forEach((problem, index) => console.error('  ' + (index + 1) + '. ' + problem));
    process.exit(1);
  }
  console.log('OK: the connection is announced only after the server answered the handshake, '
    + 'in at least ' + HANDSHAKE_ANSWER_FLOOR + ' ms.');
}

module.exports = { problemsFor };
