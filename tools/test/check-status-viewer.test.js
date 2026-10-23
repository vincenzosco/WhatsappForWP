'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const { statusViewerProblems } = require('../check-status-viewer');

const ROOT = path.join(__dirname, '..', '..');
const PANE = 'WhatsappApp/Controls/StatusPane.xaml.cs';
const PANE_XAML = 'WhatsappApp/Controls/StatusPane.xaml';
const ROW_MODEL = 'WhatsappApp/Models/ChatMessage.cs';
const VIEWER = 'WhatsappApp/Pages/StatusViewerPage.xaml.cs';
const FEED = 'WhatsappApp/Protocol/Client/StatusFeed.cs';
const MESSAGE = 'WhatsappApp/Protocol/Client/IncomingMessage.cs';
const DATA = 'WhatsappApp/Services/DataService.cs';
const CACHE = 'WhatsappApp/Services/StatusCache.cs';
const PAIRING = 'WhatsappApp/Pages/PairPage.xaml.cs';

const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
const files = () => ({
  pane: read(PANE),
  viewer: read(VIEWER),
  feed: read(FEED),
  message: read(MESSAGE),
  data: read(DATA),
  cache: read(CACHE),
  pairing: read(PAIRING),
  template: read(PANE_XAML),
  row: read(ROW_MODEL)
});

test('il tocco apre lo stato, la pagina lo guarda e chiede i byte, la ricevuta va alla lista', () => {
  assert.deepStrictEqual(statusViewerProblems(files()), []);
});

test('un tocco che non apre niente lascia lo stato solo elencato', () => {
  const broken = files();
  broken.pane = broken.pane.replace('Navigate(typeof(StatusViewerPage), row)', 'MarkStatusViewed(row)');
  assert.match(statusViewerProblems(broken).join('\n'), /StatusViewerPage/);
});

test('una pagina che non guarda lo stato non fa riempire l anello', () => {
  const broken = files();
  broken.viewer = broken.viewer.replace(/MarkStatusViewed\(_status\);/, '');
  assert.match(statusViewerProblems(broken).join('\n'), /MarkStatusViewed/);
});

test('una pagina che non chiede i byte mostra niente per gli stati della history', () => {
  const broken = files();
  broken.viewer = broken.viewer.replace(/RequestStatusMedia\(_status\);/, '');
  assert.match(statusViewerProblems(broken).join('\n'), /RequestStatusMedia/);
});

test('una ricevuta indirizzata all autore torna a non dire niente', () => {
  const broken = files();
  broken.feed = broken.feed.replace(/IncomingMessage\.Receipt\(BroadcastJid,[\s\S]{0,160}?author\)/,
    'IncomingMessage.Receipt(author, new List<string> { id }, "read", 0)');
  assert.match(statusViewerProblems(broken).join('\n'), /broadcast list/);
});

test('una ricevuta senza il participant non dice di chi e lo stato', () => {
  const broken = files();
  broken.feed = broken.feed.replace(
    /IncomingMessage\.Receipt\(BroadcastJid, new List<string> \{ id \}, "read", 0, author\)/,
    'IncomingMessage.Receipt(BroadcastJid, new List<string> { id }, "read", 0)');
  assert.match(statusViewerProblems(broken).join('\n'), /participant/);
});

test('un feed che non si legge piu al riavvio torna vuoto a ogni avvio', () => {
  const broken = files();
  broken.data = broken.data.replace('StatusCache.LoadAsync()', 'StatusCache.SaveAsync(_statuses)');
  assert.match(statusViewerProblems(broken).join('\n'), /LoadAsync/);
});

test('un feed che non viene scritto non sopravvive alla chiusura', () => {
  const broken = files();
  broken.data = broken.data.replace('StatusCache.SaveAsync(snapshot)', 'Task.FromResult(0)');
  assert.match(statusViewerProblems(broken).join('\n'), /SaveAsync/);
});

test('una scrittura per ogni stato riscrive tutto il feed a ogni riga della burst', () => {
  const broken = files();
  // The shape this guard exists for: the snapshot is taken and written by the caller.
  broken.data = broken.data.replace(/private void SaveStatusSnapshot\(\)\r?\n        \{\r?\n[\s\S]*?\r?\n        \}/,
    'private void SaveStatusSnapshot()\n        {\n            var snapshot = new List<ChatMessage>(_statuses);\n'
    + '            Guarded.RunGuardedAsync("DataService/status snapshot", StatusCache.SaveAsync(snapshot));\n        }');
  assert.match(statusViewerProblems(broken).join('\n'), /writes the file itself/);
});

test('uno scrittore che non aspetta il silenzio del feed scrive una volta per riga', () => {
  const broken = files();
  broken.data = broken.data.replace(/await Task\.Delay\(StatusSnapshotQuietMilliseconds\);/, 'await Task.Delay(0);');
  assert.match(statusViewerProblems(broken).join('\n'), /does not wait for the feed to settle/);
});

test('uno scrittore che non si riarma perde la riga arrivata durante la scrittura', () => {
  const broken = files();
  broken.data = broken.data.replace(/while \(version != _statusSnapshotRequested\);/, 'while (false);');
  assert.match(statusViewerProblems(broken).join('\n'), /does not re-arm/);
});

test('uno stato inserito in cima mostra ieri sopra un minuto fa', () => {
  const broken = files();
  broken.data = broken.data.replace(/InsertStatusInOrder\(row\);/, '_statuses.Insert(0, row);');
  assert.match(statusViewerProblems(broken).join('\n'), /inserted at the top of the feed/);
});

test('un feed senza inserimento ordinato legge nell ordine di arrivo', () => {
  const broken = files();
  broken.data = broken.data.replace(/InsertStatusInOrder\(row\);/g, '_statuses.Add(row);');
  assert.match(statusViewerProblems(broken).join('\n'), /no ordered insertion/);
});

test('un collegamento che tiene gli stati del vecchio account mostra gli stati sbagliati', () => {
  const broken = files();
  broken.pairing = broken.pairing.replace(/\s*await StatusCache\.DeleteAsync\(\);/, '');
  assert.match(statusViewerProblems(broken).join('\n'), /DeleteAsync/);
});

test('una copia che tiene piu di un giorno mostra stati che WhatsApp ha gia dimenticato', () => {
  const broken = files();
  broken.cache = broken.cache.replace(/AddHours\(-LifetimeHours\)/g, 'AddHours(0)');
  assert.match(statusViewerProblems(broken).join('\n'), /LifetimeHours/);
});

test('una riga che chiede una proprieta che il modello non ha non disegna niente', () => {
  const broken = files();
  // The shape of the run of 2026-10-22: the template asked for Initials and the row
  // model did not have one, so every realised row drew an empty circle and the Output
  // window said `'Initials' property not found on 'WhatsappApp.Models.ChatMessage'`.
  broken.row = broken.row.replace(/public string Initials\r?\n[\s\S]*?\r?\n        \}/, '');
  assert.doesNotMatch(broken.row, /public string Initials/);
  assert.match(statusViewerProblems(broken).join('\n'), /binds Initials, which/);
});

test('un modello che la guardia non sa piu leggere lo dice invece di approvare', () => {
  const broken = files();
  broken.row = 'public class ChatMessage { }';
  assert.match(statusViewerProblems(broken).join('\n'), /too few/);
});

test('un pannello senza il modello della riga non e un pannello controllato', () => {
  const broken = files();
  broken.template = '<UserControl/>';
  assert.match(statusViewerProblems(broken).join('\n'), /template is gone/);
});

test('un costruttore di ricevute che non sa portare il participant non puo dire di chi e', () => {
  const broken = files();
  // The add inside the overload that takes a participant, not the ones the other
  // receipts already have.
  broken.message = broken.message.replace(
    /(string participant\)[\s\S]{0,900}?)Attrs\.Add\("participant", participant\);/,
    '$1Attrs.Add("type", type);');
  assert.match(statusViewerProblems(broken).join('\n'), /participant/);
});
