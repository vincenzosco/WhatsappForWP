'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const { clientStateProblems } = require('../check-client-state');

const ROOT = path.join(__dirname, '..', '..');
const CONNECTION = 'WhatsappApp/Services/NativeConnection.cs';
const SOCKET = 'WhatsappApp/Protocol/Client/WhatsAppSocket.cs';
const SESSION = 'WhatsappApp/Protocol/Client/NativeSession.cs';
const PAIRING = 'WhatsappApp/Pages/PairPage.xaml.cs';
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
const files = () => ({
  connection: read(CONNECTION),
  socket: read(SOCKET),
  session: read(SESSION),
  pairing: read(PAIRING)
});

test('il login si completa, e il canale delle notifiche si legge', () => {
  assert.deepStrictEqual(clientStateProblems(files()), []);
});

test('un login che tace resta passivo', () => {
  const broken = files();
  broken.connection = broken.connection
    .replace(/await AnnounceClientStateAsync\(session\);\r?\n/, '');
  assert.match(clientStateProblems(broken).join('\n'), /AnnounceClientStateAsync/);
});

test('senza la passive iq active non si dice di essere attivi', () => {
  const broken = files();
  broken.connection = broken.connection
    .replace(/ClientState\.PassiveIq\(ClientState\.Active, _random\)/, 'ClientState.PassiveIq("")');
  assert.match(clientStateProblems(broken).join('\n'), /PassiveIq/);
});

test('una notifica solo nominata torna a essere illeggibile', () => {
  const broken = files();
  broken.connection = broken.connection
    .replace(/await ReadNotificationAsync\(session, node\);\r?\n/, '');
  assert.match(clientStateProblems(broken).join('\n'), /ReadNotificationAsync/);
});

test('un gestore che non nomina il figlio non dice cosa e arrivato', () => {
  const broken = files();
  broken.connection = broken.connection
    .replace(/InboundRouter\.NotificationType\(node\)/, 'node.Tag');
  assert.match(clientStateProblems(broken).join('\n'), /NotificationType/);
});

test('un rifiuto del client ignorato si riprova per sempre', () => {
  const broken = files();
  broken.connection = broken.connection
    .replace(/if \(ClientState\.IsClientRefusal\(node\)\)/, 'if (false)');
  assert.match(clientStateProblems(broken).join('\n'), /IsClientRefusal/);
});

test('un login che legge un nodo solo perde il bootstrap', () => {
  const broken = files();
  broken.session = broken.session
    .replace(/answer\.Tag == "success"/, 'false');
  assert.match(clientStateProblems(broken).join('\n'), /success/);
});

test('un frame arrivato prima del login non va buttato', () => {
  const broken = files();
  broken.session = broken.session
    .replace(/\s*_beforeLogin\.Enqueue\(answer\);/, '');
  assert.match(clientStateProblems(broken).join('\n'), /_beforeLogin/);
});

test('un ack prima della lettura rende definitiva una perdita', () => {
  const broken = files();
  const ack = '            await session.SendAsync(IncomingMessage.Ack(node));\r\n';
  broken.connection = broken.connection
    .replace(ack, '')
    .replace(/\n\s*incoming = IncomingMessage\.Read\(node/,
      '\n' + ack + 'incoming = IncomingMessage.Read(node');
  assert.match(clientStateProblems(broken).join('\n'), /acknowledged only after/);
});

test('un login che non adotta la signed pre-key dell account non legge nessun pkmsg', () => {
  const broken = files();
  broken.connection = broken.connection
    .replace(/\s*await AdoptSignedPreKeyAsync\(account\);/, '');
  assert.match(clientStateProblems(broken).join('\n'), /AdoptSignedPreKeyAsync/);
});

test('una lettura fallita che non chiede il reinvio perde il messaggio', () => {
  const broken = files();
  broken.connection = broken.connection
    .replace(/IncomingMessage\.RetryReceipt\(node, NowSeconds\(\),[\s\S]*?\)\);/,
      'IncomingMessage.Ack(node));');
  assert.match(clientStateProblems(broken).join('\n'), /RetryReceipt/);
});

test('un pairing che tiene le sessioni vecchie non legge piu niente', () => {
  const broken = files();
  broken.pairing = broken.pairing
    .replace(/\s*await FileSessionStorage\.DeleteAllAsync\(\);/, '');
  assert.match(clientStateProblems(broken).join('\n'), /DeleteAllAsync/);
});

test('una pulizia dopo il salvataggio e una pulizia che non serve', () => {
  const broken = files();
  const wipe = '                await FileSessionStorage.DeleteAllAsync();\r\n';
  broken.pairing = broken.pairing
    .replace(wipe, '')
    .replace('_store.Save(paired);', '_store.Save(paired);\r\n' + wipe);
  assert.match(clientStateProblems(broken).join('\n'), /DeleteAllAsync/);
});

test('una lettura che fallisce senza dire i byte lascia il log muto', () => {
  const broken = files();
  broken.socket = broken.socket
    .replace(/_accumulator\.PendingHex\(16\)/g, '""');
  assert.match(clientStateProblems(broken).join('\n'), /PendingHex/);
});
