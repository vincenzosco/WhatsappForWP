'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const { backgroundTaskProblems, taskType, constString } = require('../check-background-task');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
const files = () => ({
  manifest: read('WhatsappApp/Properties/WMAppManifest.xml'),
  task: read('WhatsappApp.Tasks/BackgroundSyncTask.cs'),
  taskProject: read('WhatsappApp.Tasks/WhatsappApp.Tasks.csproj'),
  sync: read('WhatsappApp/Services/BackgroundSync.cs'),
  settings: read('WhatsappApp/Pages/SettingsPage.xaml.cs'),
  settingsXaml: read('WhatsappApp/Pages/SettingsPage.xaml')
});

test('il task silverlight e dichiarato, compilato e registrato con lo stesso nome', () => {
  assert.deepStrictEqual(backgroundTaskProblems(files()), []);
});

test('il tipo agente si legge come namespace piu classe', () => {
  assert.strictEqual(taskType('namespace A.B\n{\n    public sealed class T : ScheduledTaskAgent\n'), 'A.B.T');
  assert.strictEqual(taskType('namespace A\n{\n    public sealed class T\n'), null);
});

test('la costante si legge dal sorgente', () => {
  assert.strictEqual(constString('public const string EntryPoint = "X.Y";', 'EntryPoint'), 'X.Y');
  assert.strictEqual(constString('public const int IntervalMinutes = 30;', 'EntryPoint'), null);
});

test('senza la dichiarazione Silverlight di background non c e task da schedulare', () => {
  const broken = files();
  broken.manifest = broken.manifest.replace(/<BackgroundServiceAgent\b[^>]*\/>/, '');
  assert.match(backgroundTaskProblems(broken).join('\n'), /BackgroundServiceAgent/);
});

test('un tipo agente diverso dal manifest non viene istanziato', () => {
  const broken = files();
  broken.manifest = broken.manifest.replace('Type="WhatsappApp.Tasks.BackgroundSyncTask"',
    'Type="WhatsappApp.Tasks.OtherTask"');
  assert.match(backgroundTaskProblems(broken).join('\n'), /WMAppManifest\.xml names/);
});

test('un task senza NotifyComplete non chiude la sua attivazione', () => {
  const broken = files();
  broken.task = broken.task.replace(/NotifyComplete\s*\(\s*\)\s*;/, '');
  assert.match(backgroundTaskProblems(broken).join('\n'), /NotifyComplete/);
});

test('una registrazione con il nome sbagliato non attiva il task previsto', () => {
  const broken = files();
  broken.sync = broken.sync.replace('public const string TaskName = "WhatsAppMessageCheck";',
    'public const string TaskName = "OtherTask";');
  assert.match(backgroundTaskProblems(broken).join('\n'), /TaskName/);
});

test('un file del task fuori dal progetto non finisce nell assembly agente', () => {
  const broken = files();
  broken.taskProject = broken.taskProject.replace(
    /\s*<Compile Include="BackgroundSyncTask\.cs" \/>/, '');
  assert.match(backgroundTaskProblems(broken).join('\n'), /not compiled/);
});

test('una classe che non deriva da ScheduledTaskAgent non e un agente Silverlight', () => {
  const broken = files();
  broken.task = broken.task.replace(/class BackgroundSyncTask\s*:\s*ScheduledTaskAgent/,
    'class BackgroundSyncTask');
  assert.match(backgroundTaskProblems(broken).join('\n'), /derive from ScheduledTaskAgent/);
});

test('una registrazione non controlla il permesso al riavvio dopo la revoca', () => {
  const broken = files();
  broken.sync = broken.sync.replace(/RegisteredEntryPoint\(\) == EntryPoint && IsPermitted\(\)/g,
    'RegisteredEntryPoint() == EntryPoint');
  assert.match(backgroundTaskProblems(broken).join('\n'), /without rechecking IsPermitted/);
});

test('una pagina impostazioni che non verifica il permesso mostra un interruttore che mente', () => {
  const broken = files();
  broken.settings = broken.settings.replace(/BackgroundSync\.IsPermitted\(\)/g, 'true');
  assert.match(backgroundTaskProblems(broken).join('\n'), /SettingsPage.xaml.cs/);
});

test('un rifiuto senza una riga che lo dica resta invisibile', () => {
  const broken = files();
  broken.settingsXaml = broken.settingsXaml.replace('x:Name="BackgroundState"', '');
  assert.match(backgroundTaskProblems(broken).join('\n'), /BackgroundState/);
});

test('la cadenza usa il minimo supportato dagli agenti Silverlight', () => {
  const broken = files();
  broken.sync = broken.sync.replace('public const int IntervalMinutes = 30;',
    'public const int IntervalMinutes = 15;');
  assert.match(backgroundTaskProblems(broken).join('\n'), /cadence must be 30 minutes/);
});
