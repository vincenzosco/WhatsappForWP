'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const { launchRouteProblems, methodBody } = require('../check-launch-route');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
const files = () => ({
  app: read('WhatsappApp/App.xaml.cs'),
  home: read('WhatsappApp/Pages/HomePage.xaml.cs'),
  data: read('WhatsappApp/Services/DataService.cs'),
  route: read('WhatsappApp/Services/LaunchRoute.cs'),
  session: read('WhatsappApp/Services/SessionService.cs')
});

test('una chain from the badge to a start on the chats is whole', () => {
  assert.deepStrictEqual(launchRouteProblems(files()), []);
});

test('un corpo si legge dalla firma alla graffa che la chiude', () => {
  const body = methodBody('void A()\n{\n  if (x) { y(); }\n}\nvoid B() {}', 'void A(');
  assert.match(body, /y\(\)/);
  assert.doesNotMatch(body, /void B/);
  assert.strictEqual(methodBody('void A()', 'void A('), null);
});

test('un avvio che non prende la rotta apre sempre la stessa sezione', () => {
  const broken = files();
  broken.app = broken.app.replace(/SessionService\.TakeRoute\(/g, 'TakeNothing(');
  assert.match(launchRouteProblems(broken).join('\n'), /TakeRoute/);
});

test('una rotta che riappare come sezione in attesa riporta lapp su un altra tab', () => {
  const broken = files();
  broken.app = broken.app.replace('Diag.Ok("launch: the tile was showing "',
    'SessionService.PendingSection = AppSection.Status; Diag.Ok("launch: the tile was showing "');
  assert.match(launchRouteProblems(broken).join('\n'), /pending section/);
});

test('una rotta presa e mai nominata lascia il log muto su dove si e atterrati', () => {
  const broken = files();
  broken.app = broken.app.replace(/Diag\.Ok\("launch: the tile was showing " \+ route[^;]*;/,
    'Diag.Ok("launch");');
  assert.match(launchRouteProblems(broken).join('\n'), /names it/);
});

test('una home che apre lultima sezione invece delle chat riparte da dove era', () => {
  const broken = files();
  broken.home = broken.home.replace('Show(0, true);', 'Show(SectionIndex(SessionService.Section), true);');
  assert.match(launchRouteProblems(broken).join('\n'), /Show\(0, true\)/);
});

test('una sezione mai segnata aperta riporta sempre li', () => {
  const broken = files();
  broken.home = broken.home.replace(/SessionService\.MarkOpened\(/g, 'MarkSeen(');
  assert.match(launchRouteProblems(broken).join('\n'), /MarkOpened/);
});

test('un badge pubblicato senza la rotta non dice dove andare', () => {
  const broken = files();
  broken.data = broken.data.replace(/private void PublishBadge\(\)\r?\n[\s\S]*?\r?\n        \}/,
    'private void PublishBadge()\r\n        {\r\n            NotificationService.SetUnread(TotalUnread());\r\n        }');
  assert.match(launchRouteProblems(broken).join('\n'), /PublishBadge|RefreshLaunchRoute/);
});

test('un SetUnread sparso per il file divide il numero dalla rotta', () => {
  const broken = files();
  broken.data = broken.data.replace(/PublishBadge\(\);\r?\n\r?\n            \/\/ And the tiles/,
    'NotificationService.SetUnread(TotalUnread());\r\n\r\n            // And the tiles');
  assert.match(launchRouteProblems(broken).join('\n'), /raw/);
});

test('uno stato che arriva senza toccare la rotta non punta mai alla scheda Stato', () => {
  const broken = files();
  broken.data = broken.data.replace(/private void RaiseStatusesChanged\(\)\r?\n        \{\r?\n[\s\S]*?\r?\n        \}/,
    'private void RaiseStatusesChanged()\r\n        {\r\n            var handler = StatusesChanged;\r\n'
    + '            if (handler != null) handler(this, EventArgs.Empty);\r\n        }');
  assert.match(launchRouteProblems(broken).join('\n'), /RaiseStatusesChanged/);
});

test('una chiamata persa che arriva senza rotta non e raggiungibile', () => {
  const broken = files();
  broken.data = broken.data.replace(/private void RaiseCallsScanCompleted\(\)\r?\n        \{\r?\n[\s\S]*?\r?\n        \}/,
    'private void RaiseCallsScanCompleted()\r\n        {\r\n            var handler = CallsScanCompleted;\r\n'
    + '            if (handler != null) handler(this, EventArgs.Empty);\r\n        }');
  assert.match(launchRouteProblems(broken).join('\n'), /RaiseCallsScanCompleted/);
});

test('una tile di chat che nessuno apre resta una tile che non fa niente', () => {
  const broken = files();
  broken.app = broken.app.replace('home.OpenPinnedChat();', ';');
  assert.match(launchRouteProblems(broken).join('\n'), /OpenPinnedChat/);
});

test('una home senza il metodo della chat pinnata non puo aprirla', () => {
  const broken = files();
  broken.home = broken.home.replace('public void OpenPinnedChat()', 'private void NeverCalled()');
  assert.match(launchRouteProblems(broken).join('\n'), /OpenPinnedChat/);
});

test('una regola che nomina WinRT non e piu compilabile dal runner', () => {
  const broken = files();
  broken.route = 'using Windows.Storage;\n' + broken.route;
  assert.match(launchRouteProblems(broken).join('\n'), /desktop/);
});
