#!/usr/bin/env node
/**
 * tools/check-notification-ack.js
 *
 * Guard for an inbound notification that is never answered.
 *
 * Perche' esiste: un `<notification>` con un `id` e' una domanda, e la risposta
 * e' un `<ack class="notification">`. Senza, il server tiene il nodo e lo
 * riconsegna al login successivo: il `privacy_token` del 1 novembre 2026
 * (`id="535878910"`) e' arrivato cinque volte, `offline="1"`, `"2"`, `"4"`,
 * `"11"`, `"12"`, sempre lo stesso, perche' nessuno ha mai risposto.
 *
 * Le due regole che questo guard tiene, e il perche' di ciascuna:
 *
 * - l'`ack` parte **prima** di leggere la notifica: C# 5 non ammette `await`
 * dentro un `catch`, quindi una notifica che lancia mentre viene letta non
 * potrebbe piu' essere riconosciuta, e resterebbe in coda per sempre;
 * - solo i nodi con un `id` si riconoscono: `ib` (che porta `offline`,
 * `edge_routing`, `dirty`) non chiede niente, e un `ack` senza id non
 * risponde a nessuno;
 * - l'`ack` si nomina nel log (`native: acked notification <id>`): il log del
 * telefono scrive le stanze in arrivo e niente di quelle in partenza, quindi
 * senza questa riga "l'ack e' partito" non e' verificabile in nessun run.
 *
 * Usage:
 * node tools/check-notification-ack.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { stripComments } = require('./csharp');

const ROOT = path.resolve(__dirname, '..');
const SOURCE = path.join(ROOT, 'WhatsappApp', 'Services', 'NativeConnection.cs');
const SIGNATURE = 'private async Task ReadNotificationAsync(';

/** Il corpo di un metodo: dalla firma alla `/// <summary>` del successivo. */
function bodyOf(source, signature) {
  const start = source.indexOf(signature);
  if (start < 0) return null;

  const next = source.indexOf('\n        /// <summary>', start + signature.length);
  return source.slice(start, next < 0 ? source.length : next);
}

/** I problemi di un sorgente C#: vuoto quando ogni notifica con id viene riconosciuta. */
function problemsFor(source) {
  const code = stripComments(source);
  const body = bodyOf(code, SIGNATURE);
  if (body == null) {
    return ['WhatsappApp/Services/NativeConnection.cs: ReadNotificationAsync is gone, so no '
      + 'inbound notification can be acknowledged'];
  }

  const problems = [];
  const ack = body.indexOf('IncomingMessage.Ack(node)');
  if (ack < 0) {
    problems.push('WhatsappApp/Services/NativeConnection.cs: ReadNotificationAsync never sends '
      + 'IncomingMessage.Ack, so the server hands every notification back at the next login '
      + '(the privacy_token of 2026-11-01 arrived five times for exactly this)');
    return problems;
  }

  const idGuard = body.indexOf('Attrs["id"]');
  if (idGuard < 0 || idGuard > ack) {
    problems.push('WhatsappApp/Services/NativeConnection.cs: the notification ack is sent '
      + 'without first asking whether the node has an `id`; an `ib` (offline, edge_routing) has '
      + 'none, and an ack with no id answers nothing');
  }

  const reading = body.indexOf('ClientState.IsClientRefusal(node)');
  if (reading < 0 || ack > reading) {
    problems.push('WhatsappApp/Services/NativeConnection.cs: the notification ack is sent after '
      + 'the notification is read; C# 5 cannot await in a catch, so a read that throws would '
      + 'leave it unacknowledged forever - the ack goes first');
  }

  if (body.indexOf('NativeConnection/notification-ack') < 0) {
    problems.push('WhatsappApp/Services/NativeConnection.cs: the notification ack is not '
      + 'protected; a send that fails on a closing socket must not take the notification down '
      + 'with it');
  }

  const named = body.indexOf('native: acked notification');
  if (named < 0 || named < ack) {
    problems.push('WhatsappApp/Services/NativeConnection.cs: the notification ack is not named '
      + 'in the log (`native: acked notification <id>`); the read of a stanza is logged and the '
      + 'answer is not, so a device run cannot say whether the ack left');
  }

  return problems;
}

function main() {
  const problems = problemsFor(fs.readFileSync(SOURCE, 'utf8'));

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} unacknowledged notification path(s).`);
    process.exit(1);
  }
  console.log('OK: every notification that carries an id is acknowledged before it is read.');
}

if (require.main === module) main();

module.exports = { problemsFor };
