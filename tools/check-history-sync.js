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
 * Regole:
 *  1. `NativeConnection.cs` alza `MessageArrived` sui messaggi risposti da
 *     `ApplyHistory` (e non li tiene solo nelle righe);
 *  2. `DataService.cs` copia `incoming.IsHistory` sulla riga della cronologia;
 *  3. `NativeConnection.cs` applica il sync solo dopo `IncomingMessage.IsMine`.
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

  return problems;
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
