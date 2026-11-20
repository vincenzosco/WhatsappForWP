#!/usr/bin/env node
/**
 * tools/check-send-budget.js
 *
 * Guardian of the questions in front of a send.
 *
 * Why it exists: on 2026-11-04 a message took about a minute to leave. Every question in
 * front of the write waited `NativeConnection.AnswerSeconds` (20 s) on its own - the device
 * list, the bundles per device, the bundles per person, the privacy token twice - and nothing
 * bounded their sum: three silent questions were sixty seconds of a ring, and the phone's log
 * carried the proof (`the per-device bundle query. the server did not answer`, three times).
 * The message needs almost none of those answers to exist: the one it cannot skip is a key
 * for one device, and that is the only question that keeps the ordinary deadline.
 *
 * Rules:
 * 1. `SendBudget` is three seconds, and its arithmetic lives in the core, where the runner
 *    tests it;
 * 2. `RequestAsync` takes a deadline of its own instead of always using `AnswerSeconds`;
 * 3. the device list is asked within the budget and, when it does not answer, the last list
 *    this phone has is used (`LastDevices`): a send must not fail over a slow question. A
 *    chat with no remembered list has nothing to fall back on and its question gets the
 *    question's own deadline (`AnswerSeconds`) - the first message to a chat failed outright
 *    at the budget's end, and every message after it too;
 * 4. the privacy token is skipped when the budget is spent (the official client sends without
 *    one);
 * 5. every send opens the budget (`SendClock()`), or the bound is not there;
 * 6. bundles keep `AnswerSeconds` only when no device has a session yet: that is the key the
 *    message cannot exist without.
 *
 * Why a guardian and not a test: `NativeConnection` is WinRT code and the desktop runner does
 * not compile it.
 *
 * Usage:
 * node tools/check-send-budget.js
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

  if (!/int secondsForList = LastDevices\(address\) == null \? AnswerSeconds : SecondsIn\(budget\)/.test(code)) {
    problems.push(
      WHERE_SERVICE +
        ': the device list is asked with its own deadline when there is no remembered list',
    );
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
