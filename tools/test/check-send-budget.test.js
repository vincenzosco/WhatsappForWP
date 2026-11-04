'use strict';

/**
 * I test del guardiano delle domande davanti a un invio.
 *
 * Perche' esiste il guardiano: il 4 novembre 2026 un messaggio impiegava circa un minuto a
 * partire, perche' ogni domanda davanti alla scrittura aspettava i suoi venti secondi e nessuno
 * limitava la somma. `NativeConnection` e' codice WinRT che il runner desktop non compila, quindi
 * la regola sta qui e questi test dicono che la regola sa scattare.
 */

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const budget = require('../check-send-budget.js');

const ROOT = path.resolve(__dirname, '..', '..');

/** Un budget finto, con le due domande che il servizio gli fa. */
const GOOD_BUDGET = [
  'public struct SendBudget',
  '{',
  '    public const int Milliseconds = 3000;',
  '    public int SecondsLeft(long ticks)',
  '    public bool IsSpent(long ticks)',
  '}'
].join('\n');

/** Un servizio finto con tutte le regole rispettate: da qui si toglie una riga per volta. */
const GOOD_SERVICE = [
  'private async Task<BinaryNode> RequestAsync(NativeSession session, BinaryNode query, bool refusalIsAnswer, int seconds)',
  '        {',
  '            Task finished = await Task.WhenAny(answer, Task.Delay(TimeSpan.FromSeconds(seconds)));',
  '            int secondsForList = SecondsIn(budget);',
  '            devicesAnswer = LastDevices(address);',
  '            int secondsForBundles = missing.Count == targets.Count ? AnswerSeconds : SecondsIn(budget);',
  '            if (budget.IsSpent(DateTime.UtcNow.Ticks)) return null;',
  '        }',
  'private async Task<DirectTarget> DevicesAsync(NativeSession session, string jid, SendBudget budget)',
  'private async Task<DirectTarget> ResolveDevicesAsync(NativeSession session, string address, SendBudget budget)',
  'private async Task<PrivacyToken> PrivacyTokenAsync(NativeSession session, string jid, uint timestamp, SendBudget budget)',
  'public async Task<string> SendTextAsync(string jid, string body)',
  '        {',
  '            SendBudget budget = SendClock();',
  '        }',
  'public async Task<string> SendMediaAsync(string jid)',
  '        {',
  '            SendBudget budget = SendClock();',
  '        }',
  'public async Task<string> RevokeAsync(string jid, string messageId)',
  '        {',
  '            SendBudget budget = SendClock();',
  '        }'
].join('\n');

test('il servizio vero e il budget vero rispettano le regole', () => {
  const service = fs.readFileSync(budget.SERVICE, 'utf8');
  const file = fs.readFileSync(budget.BUDGET, 'utf8');

  assert.deepStrictEqual(budget.sendProblems(service, file), [],
    'le regole del 4 novembre 2026 sul percorso di invio vero');
});

test('un RequestAsync senza scadenza propria si segnala', () => {
  const broken = GOOD_SERVICE.replace(
    'RequestAsync(NativeSession session, BinaryNode query, bool refusalIsAnswer, int seconds)',
    'RequestAsync(NativeSession session, BinaryNode query, bool refusalIsAnswer)');

  assert.match(budget.sendProblems(broken, GOOD_BUDGET).join('\n'), /no deadline of its own/,
    'con AnswerSeconds per ogni domanda, quattro domande sono il minuto che il messaggio costava');
});

test('una lista device senza il ripiego si segnala', () => {
  const broken = GOOD_SERVICE.replace('            devicesAnswer = LastDevices(address);', '');

  assert.match(budget.sendProblems(broken, GOOD_BUDGET).join('\n'), /does not answer fails the send/,
    'un server lento non deve essere un messaggio che non parte');
});

test('un token di privacy chiesto con il budget finito si segnala', () => {
  const broken = GOOD_SERVICE.replace('            if (budget.IsSpent(DateTime.UtcNow.Ticks)) return null;', '');

  assert.match(budget.sendProblems(broken, GOOD_BUDGET).join('\n'), /even with no budget left/,
    'il client ufficiale manda senza token quando non c e');
});

test('un invio che non apre il budget si segnala', () => {
  const broken = GOOD_SERVICE.replace(
    'public async Task<string> RevokeAsync(string jid, string messageId)\n        {\n            SendBudget budget = SendClock();',
    'public async Task<string> RevokeAsync(string jid, string messageId)\n        {');

  assert.match(budget.sendProblems(broken, GOOD_BUDGET).join('\n'), /only 2 send path/,
    'un percorso senza orologio e un percorso senza limite');
});

test('i bundle senza la distinzione dei due casi si segnalano', () => {
  const broken = GOOD_SERVICE.replace(
    'missing.Count == targets.Count ? AnswerSeconds : SecondsIn(budget)',
    'SecondsIn(budget)');

  assert.match(budget.sendProblems(broken, GOOD_BUDGET).join('\n'), /do not tell the two cases apart/,
    'la chiave di un device e quello che il messaggio non puo saltare');
});

test('un budget di venti secondi si segnala, e un file mancante anche', () => {
  assert.match(
    budget.sendProblems(GOOD_SERVICE, GOOD_BUDGET.replace('Milliseconds = 3000', 'Milliseconds = 20000')).join('\n'),
    /not three seconds/);
  assert.match(budget.budgetProblems(null).join('\n'), /missing/);
});
