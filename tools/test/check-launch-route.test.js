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
  route: read('WhatsappApp/Services/LaunchRoute.cs')
});

test('la catena dal badge alla sezione e intera', () => {
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

test('una rotta letta e buttata via non sposta nessuna scheda', () => {
  const broken = files();
  broken.app = broken.app.replace(/SessionService\.PendingSection = AppSection\.(Status|Calls);/g, ';');
  assert.match(launchRouteProblems(broken).join('\n'), /PendingSection/);
});

test('una sola delle due sezioni nominate lascia l altra irraggiungibile', () => {
  const broken = files();
  broken.app = broken.app.replace(/LaunchRoute\.Calls/g, '"calls"');
  assert.match(launchRouteProblems(broken).join('\n'), /LaunchRoute\.Calls/);
});

test('una home che ignora la sezione chiesta torna all ultima dell utente', () => {
  const broken = files();
  broken.home = broken.home.replace(/SessionService\.TakePendingSection\(\)/, 'null');
  assert.match(launchRouteProblems(broken).join('\n'), /TakePendingSection/);
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

test('una rotta presa su un resume che nessuno mostra resta ferma in memoria', () => {
  const broken = files();
  broken.app = broken.app.replace('home.ShowPendingSection();', 'home.OpenPinnedChat();');
  assert.match(launchRouteProblems(broken).join('\n'), /ShowPendingSection/);
});

test('una home senza il metodo del resume lascia la rotta senza nessuno che la mostri', () => {
  const broken = files();
  broken.home = broken.home.replace('public void ShowPendingSection()', 'private void NeverCalled()');
  assert.match(launchRouteProblems(broken).join('\n'), /ShowPendingSection/);
});

test('una regola che nomina WinRT non e piu compilabile dal runner', () => {
  const broken = files();
  broken.route = 'using Windows.Storage;\n' + broken.route;
  assert.match(launchRouteProblems(broken).join('\n'), /desktop/);
});
