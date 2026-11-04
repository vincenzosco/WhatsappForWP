#!/usr/bin/env node
/**
 * tools/check-send-budget.js
 *
 * Guardiano delle domande che stanno davanti a un invio.
 *
 * Perche' esiste: il 4 novembre 2026 un messaggio impiegava circa un minuto a partire. Ogni
 * domanda davanti alla scrittura aspettava `NativeConnection.AnswerSeconds` (20 s) per conto
 * suo - la lista device, i bundle per device, i bundle per persona, il token di privacy due
 * volte - e nessuno limitava la somma: tre domande mute erano sessanta secondi di cerchio, e
 * il log del telefono ne portava la prova (`the per-device bundle query ... the server did not
 * answer`, tre volte). Il messaggio non ha bisogno di quasi nessuna di quelle risposte per
 * esistere: quella che non puo' saltare e' la chiave di un device, ed e' l'unica che tiene il
 * limite ordinario.
 *
 * Regole:
 *  1. `SendBudget` vale tre secondi, e la sua aritmetica sta nel core, dove il runner la prova;
 *  2. `RequestAsync` accetta una scadenza propria, invece di usare sempre `AnswerSeconds`;
 *  3. la lista device si chiede dentro il budget e, se non risponde, si usa l'ultima che il
 *     telefono ha (`LastDevices`): un invio non deve fallire per una domanda lenta;
 *  4. il token di privacy si salta quando il budget e' finito (il client ufficiale manda senza);
 *  5. ogni invio apre il budget (`SendClock()`), altrimenti il limite non c'e';
 *  6. i bundle tengono `AnswerSeconds` solo quando nessun device ha gia' una sessione: quella
 *     e' la chiave senza cui il messaggio non puo' esistere.
 *
 * Perche' un guardiano e non un test: `NativeConnection` e' codice WinRT e il runner desktop
 * non lo compila.
 *
 * Usage:
 *   node tools/check-send-budget.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { stripComments } = require('./csharp');

const ROOT = path.resolve(__dirname, '..');
const SERVICE = path.join(ROOT, 'WhatsappApp', 'Services', 'NativeConnection.cs');
const BUDGET = path.join(ROOT, 'WhatsappApp', 'Protocol', 'Client', 'SendBudget.cs');

const WHERE_SERVICE = 'WhatsappApp/Services/NativeConnection.cs';
const WHERE_BUDGET = 'WhatsappApp/Protocol/Client/SendBudget.cs';

/**
 * Il budget: tre secondi, e le due domande che il servizio gli fa (quanto resta, se e' finito).
 * `budget` e' il sorgente senza commenti; null quando il file non c'e'.
 */
function budgetProblems(budget) {
  const problems = [];

  if (budget == null) {
    problems.push(WHERE_BUDGET + ': missing - the send has no bound and every question in ' +
      'front of it waits its own twenty seconds');
    return problems;
  }

  if (!/const int Milliseconds = 3000;/.test(budget)) {
    problems.push(WHERE_BUDGET + ': the budget is not three seconds (`Milliseconds = 3000`)' +
      ' - a message that takes longer than that is the report of 4 November 2026');
  }

  if (!/public int SecondsLeft\(/.test(budget)) {
    problems.push(WHERE_BUDGET + ': no SecondsLeft - the deadline handed to RequestAsync');
  }

  if (!/public bool IsSpent\(/.test(budget)) {
    problems.push(WHERE_BUDGET + ': no IsSpent - nothing can decide to skip an optional question');
  }

  return problems;
}

/**
 * Le regole sul servizio: scadenza propria, lista device con ripiego, token facoltativo, budget
 * aperto a ogni invio, e i bundle che tengono il limite ordinario solo quando serve davvero.
 */
function serviceProblems(source) {
  const problems = [];
  const code = stripComments(source);

  if (!/RequestAsync\(NativeSession session, BinaryNode query, bool refusalIsAnswer, int seconds\)/.test(code)) {
    problems.push(WHERE_SERVICE + ': RequestAsync has no deadline of its own - every question ' +
      'uses AnswerSeconds, and four of them are the minute the send used to cost');
  }

  if (!/Task\.Delay\(TimeSpan\.FromSeconds\(seconds\)\)/.test(code)) {
    problems.push(WHERE_SERVICE + ': the deadline parameter is not what the wait uses ' +
      '(`Task.Delay(TimeSpan.FromSeconds(seconds))`)');
  }

  if (!/DevicesAsync\(NativeSession session, string jid, SendBudget budget\)/.test(code) ||
      !/ResolveDevicesAsync\(NativeSession session, string address, SendBudget budget\)/.test(code)) {
    problems.push(WHERE_SERVICE + ': the device list is asked without the send\'s budget - ' +
      'who is behind a chat is one of the questions that must not outlast the message');
  }

  if (!/int secondsForList = SecondsIn\(budget\)/.test(code)) {
    problems.push(WHERE_SERVICE + ': the device list has no deadline taken from the budget');
  }

  if (!/devicesAnswer = LastDevices\(address\)/.test(code)) {
    problems.push(WHERE_SERVICE + ': a device list that does not answer fails the send -' +
      ' it has to fall back to the last answer for that chat, or a slow server is a message ' +
      'that never leaves');
  }

  if (!/PrivacyTokenAsync\(NativeSession session, string jid, uint timestamp, SendBudget budget\)/.test(code)) {
    problems.push(WHERE_SERVICE + ': PrivacyTokenAsync does not take the budget - the token is ' +
      'optional on the wire and must not be waited for');
  }

  if (!/budget\.IsSpent\(DateTime\.UtcNow\.Ticks\)/.test(code)) {
    problems.push(WHERE_SERVICE + ': the privacy token is requested even with no budget left - ' +
      'the reference clients send without one');
  }

  const clocks = (code.match(/SendBudget budget = SendClock\(\);/g) || []).length;
  if (clocks < 3) {
    problems.push(WHERE_SERVICE + ': only ' + clocks + ' send path(s) open the budget - a text, ' +
      'a media message and a revoke each ask the same questions and each needs its own clock');
  }

  if (!/missing\.Count == targets\.Count \? AnswerSeconds : SecondsIn\(budget\)/.test(code)) {
    problems.push(WHERE_SERVICE + ': the bundles do not tell the two cases apart - when every ' +
      'device of the chat has no session the key is what the message cannot exist without, and ' +
      'only then does the question keep AnswerSeconds');
  }

  return problems;
}

/** Legge i due file e unisce le regole. Un file mancante e' un problema, non un'eccezione. */
function sendProblems(serviceSource, budgetSource) {
  const problems = budgetProblems(budgetSource == null ? null : stripComments(budgetSource));
  if (serviceSource != null) problems.push(...serviceProblems(serviceSource));

  return problems;
}

function main() {
  const budgetSource = fs.existsSync(BUDGET) ? fs.readFileSync(BUDGET, 'utf8') : null;
  const problems = sendProblems(fs.readFileSync(SERVICE, 'utf8'), budgetSource);

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} send-budget problem(s).`);
    process.exit(1);
  }

  console.log('OK: the questions in front of a send share a three-second budget.');
}

if (require.main === module) main();

module.exports = { budgetProblems, serviceProblems, sendProblems, SERVICE, BUDGET };
