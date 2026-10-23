#!/usr/bin/env node
/**
 * tools/check-chat-snapshot.js
 *
 * Guard for the order the snapshot is written in: **the chat list first, the
 * conversations after it**.
 *
 * Perche' esiste: la sera del 23 ottobre 2026 l'app e' stata chiusa e riaperta e
 * la lista era vuota, finche' non e' arrivato un messaggio - e allora in lista
 * c'era solo quella chat. Lo store del telefono dice perche': `chats.json` non
 * c'era affatto, mentre c'erano 57 `messages_*.json`. `SaveSnapshotAsync`
 * scriveva i tail di ogni conversazione (un file, letto e riscritto, uno alla
 * volta) e solo alla fine l'elenco: su un telefono con 169 righe quel giro non
 * arriva in fondo prima che l'app venga chiusa, e la chiusura e' il caso
 * normale, non l'eccezione. Una riga che c'e' sul disco e una tail in meno e'
 * una lista che si apre vuota la volta dopo; il contrario e' solo una
 * conversazione che si apre con un messaggio in meno.
 *
 * Regola: dentro `DataService.SaveSnapshotAsync` la chiamata a
 * `ChatCache.SaveAsync` viene prima del ciclo che chiama
 * `MessageCache.AppendAsync`.
 *
 * Usage:
 *   node tools/check-chat-snapshot.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { stripComments } = require('./csharp');

const ROOT = path.resolve(__dirname, '..');
const SERVICE = path.join(ROOT, 'WhatsappApp', 'Services', 'DataService.cs');

/** Il corpo di un metodo, dalla sua dichiarazione alla graffa a otto spazi che lo chiude. */
function methodBody(source, name) {
  const at = source.search(new RegExp('(?:private|public|internal)[^;{]*\\b' + name + '\\s*\\('));
  if (at < 0) return '';

  const rest = source.slice(at);
  const end = rest.indexOf('\n        }');

  return end < 0 ? rest : rest.slice(0, end);
}

/** I problemi del salvataggio: vuoto quando la lista e' scritta per prima. */
function problemsFor(service) {
  const problems = [];
  const where = 'WhatsappApp/Services/DataService.cs';
  const body = methodBody(stripComments(service), 'SaveSnapshotAsync');

  if (!body) {
    problems.push(where + ': no SaveSnapshotAsync - the list and the conversations the next '
      + 'start draws are written from there');
    return problems;
  }

  const list = body.indexOf('ChatCache.SaveAsync');
  const tails = body.indexOf('MessageCache.AppendAsync');

  if (list < 0) {
    problems.push(where + ': the snapshot never writes the chat list (ChatCache.SaveAsync): the '
      + 'next start opens on an empty screen until a message brings a row of its own');
  }

  if (list >= 0 && tails >= 0 && list > tails) {
    problems.push(where + ': the chat list is written after the conversations\' tails. Each tail '
      + 'is a read and a write of its own file, one at a time: on the phone of 23 October 2026 '
      + '56 of them took the better part of two minutes against 169 rows, the app was closed in '
      + 'the middle and chats.json was never written - the list was empty at the next start '
      + '(57 messages_*.json, no chats.json). The list is what that screen draws; write it first');
  }

  return problems;
}

function main() {
  const problems = problemsFor(fs.readFileSync(SERVICE, 'utf8'));

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} snapshot problem(s).`);
    process.exit(1);
  }
  console.log('OK: the chat list is written before the conversations\' tails.');
}

if (require.main === module) main();

module.exports = { problemsFor, methodBody, stripComments };
