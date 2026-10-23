#!/usr/bin/env node
/**
 * tools/check-history-sync.js
 *
 * Guard per le due righe della cronologia che nessun test del protocollo puo'
 * vedere, perche' vivono nell'app e non nel core.
 *
 * Perche' esiste: `NativeChats.ApplyHistory` risponde i messaggi che il sync ha
 * aggiunto (il core li marca `IsHistory` e lascia fuori cio' che non si disegna),
 * e perche' quel lavoro arrivi a schermo servono due cablaggi:
 *
 *  1. `NativeConnection.ApplySync` deve alzare `MessageArrived` per ogni messaggio
 *     di quella risposta. Senza quelle righe il sync riempie solo la lista: ogni
 *     conversazione si apre vuota, e la pagina aspetta invano un burst che non si
 *     chiude (`HistoryCompleted` si alza sui chat che hanno ricevuto righe);
 *  2. `DataService.OnNativeMessage` deve copiare `incoming.IsHistory` sulla riga
 *     che costruisce. Senza quel bit ogni messaggio vecchio arriva come nuovo: un
 *     avviso per messaggio e un contatore unread in piu' a ogni sync.
 *
 * E una terza, che e' di sicurezza e non di schermo: un sync si applica solo se
 * la stanza e' di questo account. `historySyncNotification` e' un protocol message
 * self-only, e il riferimento lo scarta quando la stanza non e' nostra ("an
 * attacker could otherwise spoof any of these to manipulate local state",
 * `Utils/process-message.js`): da chiunque altro, la stessa stanza lascia a un
 * contatto la chat list, il registro delle chiamate e il feed degli stati di
 * questo telefono.
 *
 * E una quarta, che e' l'intera chat list di un telefono: l'ack di un sync deve
 * partire **dopo** che il sync e' nella lista. L'ack e' cio' che dice al server
 * "questa storia e' arrivata", e WhatsApp la manda una volta per link: un ack in
 * cima alla stanza butta via il resto della lista appena il blob non si scarica
 * (o il processo finisce prima). E' il telefono del 2026-10-23: bootstrap da
 * cinque conversazioni ackato, il chunk dopo mai arrivato, e una lista di cinque
 * righe che nessuna riconnessione ripara. Un sync che non atterra si richiede con
 * un retry receipt, come qualunque stanza che il telefono non e' riuscito a
 * leggere.
 *
 * Regole:
 *  1. `NativeConnection.cs` alza `MessageArrived` sui messaggi risposti da
 *     `ApplyHistory` (e non li tiene solo nelle righe);
 *  2. `DataService.cs` copia `incoming.IsHistory` sulla riga della cronologia;
 *  3. `NativeConnection.cs` applica il sync solo dopo `IncomingMessage.IsMine`;
 *  4. l'ack di una stanza esce solo dopo `ApplyHistoryAsync`, e un sync fallito
 *     chiede di essere rimandato (`AskAgainAsync`) invece di essere ackato.
 *
 * Usage:
 *   node tools/check-history-sync.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const CONNECTION = 'WhatsappApp/Services/NativeConnection.cs';
const DATA_SERVICE = 'WhatsappApp/Services/DataService.cs';

/**
 * I problemi dei due file. Un array di frasi, vuoto quando non c'e' niente di
 * storto.
 */
function historySyncProblems(files) {
  const problems = [];
  const connection = String((files && files.connection) || '');
  const dataService = String((files && files.dataService) || '');

  if (!connection.includes('IList<HistoryMessage> fromTheSync = _chats.ApplyHistory(sync);')) {
    problems.push(`${CONNECTION}: the answer of _chats.ApplyHistory must be kept ` +
      '(`IList<HistoryMessage> fromTheSync = ...`): the rows are not the conversation, and ' +
      'throwing that answer away is the chat list that opens empty chat by chat');
  }
  if (!connection.includes('MessageArrived(this, fromTheSync[')) {
    problems.push(`${CONNECTION}: every message the sync added must be raised as ` +
      'MessageArrived: the app draws a conversation from those events, and a sync that only ' +
      'filled the rows leaves the page waiting for a burst that never closes');
  }

  if (!dataService.includes('message.IsHistory = incoming.IsHistory;')) {
    problems.push(`${DATA_SERVICE}: OnNativeMessage must copy incoming.IsHistory onto the row ` +
      'it builds: without it a message of the sync arrives as a new one, with a toast each and ' +
      'a chat row counted once more');
  }

  if (!connection.includes('IncomingMessage.IsMine(incoming.From, account)')) {
    problems.push(`${CONNECTION}: a history sync must be applied only when ` +
      'IncomingMessage.IsMine answers for the stanza, because historySyncNotification is a ' +
      'self-only protocol message (the reference drops it when it is not ours): applied from ' +
      'anybody, a contact writes this phone\'s chat list, call log and status feed');
  }

  const readMessage = methodBody(connection, 'private async Task ReadMessageAsync(');
  const historyAt = readMessage.indexOf('HistorySyncNotification.From(incoming.Content)');
  const ackAt = readMessage.indexOf('IncomingMessage.Ack(node)');

  if (!readMessage.includes('if (await ApplyHistoryAsync(history))')) {
    problems.push(`${CONNECTION}: the acknowledgement of a history sync must depend on ` +
      'ApplyHistoryAsync having applied it (`if (await ApplyHistoryAsync(history))`): the ' +
      'ack is what tells the server the sync is delivered, and WhatsApp sends it once per ' +
      'link, so an ack over a sync that never landed is the rest of the chat list lost');
  }
  if (!/if \(await ApplyHistoryAsync\(history\)\)[^\n]*\r?\n\s*else await AskAgainAsync\(session, node, account\);/.test(readMessage)) {
    problems.push(`${CONNECTION}: a history sync that did not land must be asked for again ` +
      '(`else await AskAgainAsync(session, node, account)`), not acknowledged: a retry ' +
      'receipt leaves the stanza unread, while an ack tells the server it may forget it');
  }
  if (historyAt < 0 || ackAt < 0 || ackAt < historyAt) {
    problems.push(`${CONNECTION}: the ack of a message must not go out before the history ` +
      'sync is looked at: the sync is applied (and acked only when it landed) after the ' +
      'read, and an ack above that line is the one-shot history of this device thrown away');
  }

  return problems;
}

/**
 * Il corpo di un metodo: dalla sua firma fino alla riga che apre il membro dopo,
 * cioe' una riga che comincia con otto spazi e `private` o `public`. Serve a
 * guardare l'ordine delle righe di un metodo e non di tutto il file, dove la
 * prima `Ack` e' quella di un altro metodo e non dice niente.
 */
function methodBody(text, signature) {
  const start = text.indexOf(signature);
  if (start < 0) return '';

  const rest = text.slice(start + signature.length);
  const next = rest.search(/\r?\n        (?:private|public|internal) /);

  return next < 0 ? rest : rest.slice(0, next);
}

function main() {
  const problems = historySyncProblems({
    connection: fs.readFileSync(path.join(ROOT, CONNECTION), 'utf8'),
    dataService: fs.readFileSync(path.join(ROOT, DATA_SERVICE), 'utf8')
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} history sync problem(s).`);
    process.exit(1);
  }
  console.log('OK: the messages of the sync reach the app marked as history, and a sync from '
    + 'anybody else is left alone.');
}

if (require.main === module) main();

module.exports = { historySyncProblems };
