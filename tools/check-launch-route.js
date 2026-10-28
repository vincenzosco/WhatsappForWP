#!/usr/bin/env node
/**
 * tools/check-launch-route.js
 *
 * Guard per la strada che porta da un tocco sulla tile alla sezione che ha qualcosa
 * da mostrare.
 *
 * Perche' esiste: su WP8.1 un tocco sulla tile dell'app - e sulla schermata di blocco,
 * che mostra la stessa tile - arriva come un avvio normale, con gli argomenti vuoti che
 * manda anche l'elenco delle app. Non c'e' niente nell'avvio che dica dove era il tocco:
 * l'unica cosa che l'app sa e' quello che il telefono stava mostrando, cioe' la rotta
 * che il livello delle notifiche ha scritto quando ha disegnato il badge. Ogni pezzo di
 * quella catena e' muto se manca:
 *
 *  - senza `RefreshLaunchRoute` la rotta non viene mai scritta, e il tocco apre sempre
 *    le chat; il telefono non se ne accorge e nessun log lo dice;
 *  - senza `TakeRoute` in `App.OnLaunched` la rotta resta sul telefono per sempre e
 *    nessun avvio la legge;
 *  - senza `PendingSection` nella home la rotta arriva e viene buttata via un rigo dopo;
 *  - senza `MarkOpened` la sezione non sa di essere stata guardata, e la stessa rotta
 *    riporta li' a ogni avvio, per sempre: e' il modo in cui una scorciatoia diventa un
 *    rapimento;
 *  - senza il `WinRT` fuori da `LaunchRoute.cs` il runner desktop non compila piu' e la
 *    regola resta senza test.
 *
 * Regole:
 *  1. `App.OnLaunched` prende la rotta e la traduce in una sezione pendente, per
 *     entrambi i casi (`LaunchRoute.Status`, `LaunchRoute.Calls`);
 *  2. `HomePage` legge la sezione pendente e la preferisce a quella dell'ultima volta;
 *  3. `HomePage` segna aperta la sezione che mostra;
 *  4. `DataService` scrive la rotta quando pubblica il badge, e la riscrive quando
 *     cambiano gli stati e quando finisce la scansione delle chiamate;
 *  5. `LaunchRoute.cs` non nomina `Windows.` - e' il file che il runner compila;
 *  6. un tocco sulla tile di un'app sospesa e' un resume e non naviga niente: la home
 *     deve avere `ShowPendingSection` e `App` deve chiamarla, o la rotta presa in
 *     `OnLaunched` resta in `PendingSection` e nessuno la mostra.
 *
 * Usage:
 *   node tools/check-launch-route.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = 'WhatsappApp/App.xaml.cs';
const HOME = 'WhatsappApp/Pages/HomePage.xaml.cs';
const DATA = 'WhatsappApp/Services/DataService.cs';
const ROUTE = 'WhatsappApp/Services/LaunchRoute.cs';

/** Il corpo di un metodo, dalla firma alla graffa che la chiude. */
function methodBody(text, signature) {
  const start = text.indexOf(signature);
  if (start < 0) return null;

  const open = text.indexOf('{', start);
  if (open < 0) return null;

  let depth = 0;
  for (let i = open; i < text.length; i++) {
    if (text[i] === '{') depth++;
    else if (text[i] === '}') {
      depth--;
      if (depth === 0) return text.slice(open, i + 1);
    }
  }
  return null;
}

/**
 * I problemi, dati i testi dei file. Puro: i test gli passano copie manomesse.
 */
function launchRouteProblems(files) {
  const problems = [];

  // 1. L'avvio legge la rotta e la traduce in una sezione.
  const launched = methodBody(files.app, 'protected override void OnLaunched(');
  if (!launched) {
    problems.push('WhatsappApp/App.xaml.cs: no OnLaunched - there is nowhere the route of a tap could arrive');
  } else {
    if (!/SessionService\.TakeRoute\s*\(/.test(launched)) {
      problems.push('WhatsappApp/App.xaml.cs: OnLaunched never calls SessionService.TakeRoute() - ' +
        'a tap on the tile arrives as a plain launch and the route it left is the only thing that says where it was');
    }
    if (!/LaunchRoute\.Status/.test(launched) || !/LaunchRoute\.Calls/.test(launched)) {
      problems.push('WhatsappApp/App.xaml.cs: OnLaunched does not name both LaunchRoute.Status and ' +
        'LaunchRoute.Calls - one of the two sections can never be opened from the tile');
    }
    if (!/SessionService\.PendingSection\s*=/.test(launched)) {
      problems.push('WhatsappApp/App.xaml.cs: OnLaunched reads the route and does nothing with it - ' +
        'SessionService.PendingSection is what the home page can act on');
    }
  }

  // 2. La home preferisce la sezione chiesta a quella dell'ultima volta.
  const navigatedTo = methodBody(files.home, 'protected override void OnNavigatedTo(') || '';
  if (!/SessionService\.TakePendingSection\s*\(/.test(navigatedTo)) {
    problems.push('WhatsappApp/Pages/HomePage.xaml.cs: the home page never calls SessionService.TakePendingSection() - ' +
      'the tap opens the tab the user left, not the one that has something in it');
  }

  // 3. ...e la sezione che mostra e' guardata.
  if (!/SessionService\.MarkOpened\s*\(/.test(files.home)) {
    problems.push('WhatsappApp/Pages/HomePage.xaml.cs: the section shown is never passed to ' +
      'SessionService.MarkOpened() - "what arrived since the last look" never moves forward, and the ' +
      'route opens the same tab at every start');
  }

  // 4. La rotta si scrive col badge, e si riscrive quando cambia qualcosa che non ha badge.
  const published = methodBody(files.data, 'private void PublishBadge(');
  if (!published) {
    problems.push('WhatsappApp/Services/DataService.cs: no PublishBadge() - ' +
      'the count and the route of a tap are published in one place, and that place is gone');
  } else if (!/RefreshLaunchRoute\s*\(\s*\)/.test(published)) {
    problems.push('WhatsappApp/Services/DataService.cs: PublishBadge() does not call RefreshLaunchRoute() - ' +
      'the badge changes and the phone still says the previous tap destination');
  }
  const badges = files.data.match(/NotificationService\.SetUnread\s*\(\s*TotalUnread\s*\(\s*\)\s*\)/g);
  if (badges && badges.length > 1) {
    problems.push('WhatsappApp/Services/DataService.cs: ' + badges.length + ' raw ' +
      'NotificationService.SetUnread(TotalUnread()) calls - the count and the route are the same fact ' +
      'and are published together by PublishBadge()');
  }
  if (!/void RaiseStatusesChanged\s*\(/.test(files.data)
    || !/RefreshLaunchRoute\s*\(\s*\)/.test(methodBody(files.data, 'private void RaiseStatusesChanged(') || '')) {
    problems.push('WhatsappApp/Services/DataService.cs: RaiseStatusesChanged does not refresh the route - ' +
      'a status that arrives has no badge, so nothing would ever point a tap at the Status tab');
  }
  if (!/RefreshLaunchRoute\s*\(\s*\)/.test(methodBody(files.data, 'private void RaiseCallsScanCompleted(') || '')) {
    problems.push('WhatsappApp/Services/DataService.cs: RaiseCallsScanCompleted does not refresh the route - ' +
      'a missed call arrives with the log and nothing else, so the Calls tab could never be reached from the tile');
  }

  // 6. La rotta presa su un resume ha qualcuno che la mostra.
  if (!/public void ShowPendingSection\s*\(/.test(files.home)) {
    problems.push('WhatsappApp/Pages/HomePage.xaml.cs: no ShowPendingSection() - a tap on the tile of a ' +
      'suspended app takes the route and then has nothing that can show it');
  }
  if (!/home\.ShowPendingSection\s*\(\s*\)/.test(files.app)) {
    problems.push('WhatsappApp/App.xaml.cs: OnLaunched never calls home.ShowPendingSection() - ' +
      'the resume case is the common one (the app is still alive behind the Start screen)');
  }

  // 5. La regola resta compilabile dal runner desktop.
  if (!/LaunchRoute/.test(files.route)) {
    problems.push('WhatsappApp/Services/LaunchRoute.cs: gone - the rule has no home');
  } else {
    const winrt = /^\s*using\s+Windows\./m.exec(files.route);
    if (winrt) {
      problems.push('WhatsappApp/Services/LaunchRoute.cs: uses ' + winrt[0].trim() + ' - ' +
        'ProtocolTests compiles this file for the desktop and the rule would stop being testable');
    }
  }

  return problems;
}

function main() {
  const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
  const problems = launchRouteProblems({
    app: read(APP),
    home: read(HOME),
    data: read(DATA),
    route: read(ROUTE)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} problem(s) with the route a tap on the tile takes.`);
    process.exit(1);
  }
  console.log('OK: the badge records where a tap lands, and the start acts on it once.');
}

if (require.main === module) main();

module.exports = { launchRouteProblems, methodBody };
