'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const { backgroundTaskProblems, taskType, constString } = require('../check-background-task');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
const files = () => ({
  manifest: read('WhatsappApp/Package.appxmanifest'),
  task: read('WhatsappApp.Tasks/BackgroundSyncTask.cs'),
  taskProject: read('WhatsappApp.Tasks/WhatsappApp.Tasks.csproj'),
  sync: read('WhatsappApp/Services/BackgroundSync.cs'),
  settings: read('WhatsappApp/Pages/SettingsPage.xaml.cs'),
  settingsXaml: read('WhatsappApp/Pages/SettingsPage.xaml')
});

test('il task e dichiarato, compilato e registrato con lo stesso nome', () => {
  assert.deepStrictEqual(backgroundTaskProblems(files()), []);
});

test('il tipo compilato e namespace piu classe', () => {
  const type = taskType('namespace A.B\n{\n    public sealed class T : IBackgroundTask\n');
  assert.strictEqual(type, 'A.B.T');
  assert.strictEqual(taskType('namespace A\n{\n    public sealed class T\n'), null);
});

test('la costante si legge dal sorgente', () => {
  assert.strictEqual(constString('public const string EntryPoint = "X.Y";', 'EntryPoint'), 'X.Y');
  assert.strictEqual(constString('public const int IntervalMinutes = 15;', 'EntryPoint'), null);
});

test('senza la dichiarazione della classe attivabile il task non parte mai', () => {
  const broken = files();
  broken.manifest = broken.manifest.replace(
    /<Extension Category="windows\.activatableClass\.inProcessServer">[\s\S]*?<\/Extension>/,
    ''
  );
  assert.match(backgroundTaskProblems(broken).join('\n'), /activatableClass\.inProcessServer/);
});

test('un Path che punta all eseguibile non istanzia nessuna classe', () => {
  const broken = files();
  broken.manifest = broken.manifest.replace('<Path>CLRHost.dll</Path>', '<Path>WhatsappApp.exe</Path>');
  assert.match(backgroundTaskProblems(broken).join('\n'), /CLRHost\.dll/);
});

test('due nomi diversi sono un task che il sistema non sa creare', () => {
  const broken = files();
  broken.manifest = broken.manifest.replace(
    /(ActivatableClassId=")[^"]+"/,
    '$1Altro.Nome"'
  );
  assert.match(backgroundTaskProblems(broken).join('\n'), /activatable class/);
});

test('un task di tipo diverso da timer non risponde a un TimeTrigger', () => {
  const broken = files();
  broken.manifest = broken.manifest.replace('<Task Type="timer" />', '<Task Type="systemEvent" />');
  assert.match(backgroundTaskProblems(broken).join('\n'), /Type="timer"/);
});

test('senza ToastCapable le toast dei messaggi vengono scartate dal sistema', () => {
  const broken = files();
  broken.manifest = broken.manifest.replace(' ToastCapable="true"', '');
  assert.match(backgroundTaskProblems(broken).join('\n'), /ToastCapable/);
});

test('una registrazione che chiede un altra classe non accende niente', () => {
  const broken = files();
  broken.sync = broken.sync.replace(
    /public const string EntryPoint = "[^"]+";/,
    'public const string EntryPoint = "WhatsappApp.BackgroundSyncTask";'
  );
  assert.match(backgroundTaskProblems(broken).join('\n'), /EntryPoint is/);
});

test('un file del task fuori dal progetto non finisce nel componente', () => {
  const broken = files();
  broken.taskProject = broken.taskProject.replace(
    /\s*<Compile Include="BackgroundSyncTask\.cs" \/>/,
    ''
  );
  assert.match(backgroundTaskProblems(broken).join('\n'), /Compile Include/);
});

test('un task che non implementa IBackgroundTask non e un task', () => {
  const broken = files();
  broken.task = broken.task.replace(/class BackgroundSyncTask : IBackgroundTask/, 'class BackgroundSyncTask');
  assert.match(backgroundTaskProblems(broken).join('\n'), /IBackgroundTask/);
});

test('una registrazione che non chiede il permesso crede al telefono per sempre', () => {
  const broken = files();
  // The shape this guard exists for: the task is there, so nothing is asked again,
  // while the phone has already taken background work away.
  broken.sync = broken.sync.replace(/BackgroundExecutionManager\.GetAccessStatus\(\)/g, '0');
  assert.match(backgroundTaskProblems(broken).join('\n'), /GetAccessStatus/);
});

test('un gia registrato che non ricontrolla il permesso non si riarma mai', () => {
  const broken = files();
  broken.sync = broken.sync.replace(/RegisteredEntryPoint\(\) == EntryPoint && IsPermitted\(\)/g,
    'RegisteredEntryPoint() == EntryPoint');
  assert.match(backgroundTaskProblems(broken).join('\n'), /IsPermitted/);
});

test('una pagina impostazioni che non chiede al telefono mostra un interruttore che mente', () => {
  const broken = files();
  broken.settings = broken.settings.replace(/BackgroundSync\.IsPermitted\(\)/g, 'true');
  assert.match(backgroundTaskProblems(broken).join('\n'), /SettingsPage\.xaml\.cs/);
});

test('un rifiuto senza una riga che lo dica resta invisibile', () => {
  const broken = files();
  broken.settingsXaml = broken.settingsXaml.replace('x:Name="BackgroundState"', '');
  assert.match(backgroundTaskProblems(broken).join('\n'), /BackgroundState/);
});
