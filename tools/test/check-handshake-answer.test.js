'use strict';

// I test del guard sull'attesa dell'handshake: il sorgente buono viene dal
// fixture, cosi' problemsFor si prova senza compilare l app.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');
const { problemsFor } = require('../check-handshake-answer.js');

const fixture = (name) => fs.readFileSync(
  path.join(__dirname, '..', 'handshake-fixtures', name), 'utf8');

const communication = fixture('communication-service-good.cs');

test('il buon sorgente non ha problemi', () => {
  assert.deepStrictEqual(problemsFor({ communication }).problems, []);
});

test('un attesa piu corta della risposta dell adapter e un problema', () => {
  const broken = { communication: communication.replace('HandshakeAnswerMs = 20000', 'HandshakeAnswerMs = 5000') };
  assert.match(problemsFor(broken).problems.join('\n'), /HandshakeAnswerMs/);
});

test('annunciare la connessione prima della risposta e un problema', () => {
  const broken = { communication: communication.replace('await WaitForServerAnswerAsync(', 'SkipThisWait(') };
  assert.match(problemsFor(broken).problems.join('\n'), /RaiseConnectionEstablished/);
});

test('leggere la risposta senza guardare il tentativo e un problema', () => {
  const broken = { communication: communication.replace('if (attempt != _connectionId) return false;', '') };
  assert.match(problemsFor(broken).problems.join('\n'), /_connectionId/);
});

test('un fallimento senza dire perche e un problema', () => {
  const broken = { communication: communication.replace('Diag.Failed("ConnectToServerAsync/no answer"', 'Diag.Failed("ConnectToServerAsync"') };
  assert.match(problemsFor(broken).problems.join('\n'), /no answer/);
});

test('un lettore avviato dopo l attesa e un problema', () => {
  const broken = { communication: communication.replace('Task.Run(() => ListenForMessagesAsync(attempt, reader));', '') };
  assert.match(problemsFor(broken).problems.join('\n'), /ListenForMessagesAsync/);
});
