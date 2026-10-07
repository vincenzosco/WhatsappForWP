'use strict';

// I test del guard sulle diagnostiche: le sorgenti buone vengono dai fixture,
// cosi' problemsFor si prova senza compilare l app.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');
const { problemsFor } = require('../check-diagnostics.js');

const fixture = (name) => fs.readFileSync(
  path.join(__dirname, '..', 'diagnostics-fixtures', name), 'utf8');

const diag = fixture('diag-good.cs');
const app = fixture('app-good.cs');
const chatPage = fixture('chatpage-good.cs');

test('il buon sorgente non ha problemi', () => {
  assert.deepStrictEqual(problemsFor({ diag, app, chatPage }).problems, []);
});

test('una scrittura senza SerialQueue e un problema', () => {
  const broken = { diag: diag.replace('Writes = new SerialQueue()', ''), app, chatPage };
  assert.match(problemsFor(broken).problems.join('\n'), /SerialQueue/);
});

test('un handler che non scrive il log prima di morire e un problema', () => {
  const broken = { diag, app: app.replace('Diag.Flush(true);', ''), chatPage };
  assert.match(problemsFor(broken).problems.join('\n'), /App\/unhandled/);
});

test('un tetto al file mancante e un problema', () => {
  const broken = { diag: diag.replace('MaxBytes = 65536', ''), app, chatPage };
  assert.match(problemsFor(broken).problems.join('\n'), /MaxBytes/);
});

test('una fine corsa senza marcatore rimosso e un problema', () => {
  const broken = { diag: diag.replace('file.DeleteAsync()', ''), app, chatPage };
  assert.match(problemsFor(broken).problems.join('\n'), /DeleteAsync/);
});

test('un attesa della storia piu corta della lettura dell adapter e un problema', () => {
  const broken = { diag, app, chatPage: chatPage.replace('20000', '2000') };
  assert.match(problemsFor(broken).problems.join('\n'), /HistoryWaitMaxMilliseconds/);
});

test('il report del crash parte dal servizio e non dalla pagina', () => {
  const crashReport = fixture('crashreport-good.cs');
  assert.deepStrictEqual(problemsFor({ diag, app, chatPage, crashReport }).problems, []);

  const broken = { diag, app, chatPage, crashReport: crashReport.replace('SendControlAsync', '') };
  assert.match(problemsFor(broken).problems.join('\n'), /CrashReport/);
});

test('senza il servizio il guard non se ne occupa', () => {
  assert.deepStrictEqual(problemsFor({ diag, app, chatPage }).problems, []);
});

test('una riga che non arriva al debugger e un problema', () => {
  const broken = { diag: fixture('diag-no-debugger-sink.cs'), app, chatPage };
  assert.match(problemsFor(broken).problems.join('\n'), /EmitToDebugger/);
});
