#!/usr/bin/env node
/**
 * tools/check-device-identity.js
 *
 * Guard for a message that opens a session and leaves without the account's
 * signature over this device.
 *
 * Perche' esiste: un `pkmsg` dice a un device che non ci ha mai visto chi
 * siamo, e la prova e' `<device-identity>`, la firma che l'account ha fatto su
 * questo device al momento del link. Il server prende una stanza senza quel
 * nodo e non la consegna a nessuno: l'`ack` arriva, nessun telefono di nessuno
 * dei due account disegna niente, e nessun `retry` torna indietro - il testo del
 * 1 novembre 2026, invisibile anche sul telefono dello stesso account.
 *
 * Le quattro regole che questo guard tiene, e il perche' di ciascuna:
 *
 *  - il nodo si attacca quando una copia e' un `pkmsg` (`preKey`), in
 *    entrambi i costruttori - uno solo dei due e' la meta' dei messaggi
 *    sbagliata: una chat di gruppo non ha un destinatario unico, e un guard che
 *    ne controlla uno lascia l'altro scoperto;
 *  - il pairing tiene la firma (`_credentials.DeviceIdentity`): il server la
 *    manda solo dentro `pair-success`, quindi un device che non la scrive in
 *    quel momento non la puo' piu' avere;
 *  - l'account la porta (`OurIdentity()` la legge da `_account.DeviceIdentity`),
 *    perche' e' quella che ogni percorso di invio deve passare;
 *  - **ogni** chiamata di `OutgoingMessage.Node`/`Group` finisce con
 *    `OurIdentity()`: un percorso nuovo che dimentica l'ultimo argomento
 *    compila, manda, e non arriva.
 *
 * Usage:
 *   node tools/check-device-identity.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { stripComments } = require('./csharp');

const ROOT = path.resolve(__dirname, '..');

const SERVICE = path.join(ROOT, 'WhatsappApp', 'Services', 'NativeConnection.cs');
const BUILDER = path.join(ROOT, 'WhatsappApp', 'Protocol', 'Client', 'OutgoingMessage.cs');
const PAIRING = path.join(ROOT, 'WhatsappApp', 'Protocol', 'Client', 'PairingSession.cs');

const SERVICE_NAME = 'WhatsappApp/Services/NativeConnection.cs';
const BUILDER_NAME = 'WhatsappApp/Protocol/Client/OutgoingMessage.cs';
const PAIRING_NAME = 'WhatsappApp/Protocol/Client/PairingSession.cs';

/** L'indice della parentesi che chiude quella aperta in `open`, o -1. */
function matchingParen(code, open) {
  let depth = 0;
  for (let i = open; i < code.length; i++) {
    const ch = code[i];
    if (ch === '"') {
      i = skipString(code, i);
      continue;
    }
    if (ch === '(') depth++;
    else if (ch === ')') {
      depth--;
      if (depth === 0) return i;
    }
  }
  return -1;
}

/** L'indice della virgoletta che chiude quella aperta in `at`. */
function skipString(text, at) {
  for (let i = at + 1; i < text.length; i++) {
    if (text[i] === '\\') {
      i++;
      continue;
    }
    if (text[i] === '"') return i;
  }
  return text.length;
}

/** Gli argomenti di una lista, divisa sulle virgole di primo livello. */
function splitArguments(text) {
  const args = [];
  let depth = 0;
  let current = '';

  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (ch === '"') {
      const end = skipString(text, i);
      current += text.slice(i, end + 1);
      i = end;
      continue;
    }
    if (ch === '(' || ch === '[' || ch === '{') depth++;
    else if (ch === ')' || ch === ']' || ch === '}') depth--;

    if (ch === ',' && depth === 0) {
      args.push(current.trim());
      current = '';
      continue;
    }
    current += ch;
  }

  const last = current.trim();
  if (last.length || args.length) args.push(last);
  return args;
}

/** Ogni chiamata di `marker` (che finisce con la parentesi aperta). */
function callsTo(code, marker) {
  const calls = [];
  let from = 0;

  for (;;) {
    const at = code.indexOf(marker, from);
    if (at < 0) break;

    const open = at + marker.length - 1;
    const close = matchingParen(code, open);
    from = close < 0 ? open + 1 : close + 1;
    if (close < 0) continue;

    calls.push({ at, args: splitArguments(code.slice(open + 1, close)) });
  }

  return calls;
}

/** La riga di un indice, perche' un problema senza riga non si trova. */
function lineOf(code, index) {
  let line = 1;
  for (let i = 0; i < index && i < code.length; i++) {
    if (code[i] === '\n') line++;
  }
  return line;
}

/** I problemi dei tre sorgenti: vuoto quando every pkmsg porta la firma. */
function problemsFor(files) {
  const problems = [];

  const builder = stripComments(files.builder);
  const attached = (builder.match(/preKey \? DeviceIdentityNode\(/g) || []).length;
  if (attached < 2) {
    problems.push(BUILDER_NAME + ': only ' + attached + ' of the two stanza builders attaches '
      + '`device-identity` when a copy is a `pkmsg`; the server takes such a message and '
      + 'delivers it to nobody (2026-11-01)');
  }

  const pairing = stripComments(files.pairing);
  if (pairing.indexOf('_credentials.DeviceIdentity =') < 0) {
    problems.push(PAIRING_NAME + ': the signed device identity of `pair-success` is not kept '
      + 'with the account; the server sends it once, at the pairing, so a device that drops '
      + 'it can never put it on a message again');
  }

  const service = stripComments(files.service);

  const identityMethod = service.indexOf('private byte[] OurIdentity()');
  if (identityMethod < 0 || service.indexOf('_account.DeviceIdentity', identityMethod) < 0) {
    problems.push(SERVICE_NAME + ': OurIdentity does not hand back the account\'s '
      + '`DeviceIdentity`, so every send path passes null and every `pkmsg` opens a session '
      + 'nobody can trust');
  }

  ['OutgoingMessage.Node(', 'OutgoingMessage.Group('].forEach(function (marker) {
    callsTo(service, marker).forEach(function (call) {
      const last = call.args[call.args.length - 1];
      if (last !== 'OurIdentity()') {
        problems.push(SERVICE_NAME + ':' + lineOf(service, call.at) + ': ' + marker.slice(0, -1)
          + ' is called with `' + last + '` as its last argument instead of `OurIdentity()`, '
          + 'so this path can put a `pkmsg` on the wire with no device identity - the server '
          + 'acks it and no device of the recipient shows anything');
      }
    });
  });

  return problems;
}

function read(file) {
  return fs.readFileSync(file, 'utf8');
}

function main() {
  const problems = problemsFor({
    service: read(SERVICE),
    builder: read(BUILDER),
    pairing: read(PAIRING)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log('\n' + problems.length + ' send path(s) that can open a session without the '
      + 'account\'s signature over this device.');
    process.exit(1);
  }

  console.log('OK: every message that opens a session carries the account\'s signature over '
    + 'this device, on every send path.');
}

if (require.main === module) main();

module.exports = { problemsFor };
