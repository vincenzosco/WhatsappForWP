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
 *  - senza `RefreshLaunchRoute` la rotta non viene mai scritta;
 *  - senza `TakeRoute` in `App.OnLaunched` la rotta resta sul telefono per sempre;
 *  - senza `MarkOpened` la sezione non sa di essere stata guardata, e la stessa rotta
 *    resta vera per sempre: e' il modo in cui una scorciatoia diventa un rapimento.
 *
 * Regole:
 *  1. `App.OnLaunched` prende la rotta una volta e la **nomina nel log**;
 *  2. un avvio atterra sulle chat: `HomePage.OnNavigatedTo` mostra l'indice 0, e nessuno
 *     puo' portarlo su un'altra tab - `SessionService.PendingSection` non esiste piu'
 *     (28 ottobre 2026: ogni avvio apriva la tab Stato, e la richiesta e' stata
 *     "parte sulla tab stato e non su chat");
 *  3. `HomePage` segna aperta la sezione che mostra;
 *  4. `DataService` scrive la rotta quando pubblica il badge, e la riscrive quando
 *     cambiano gli stati e quando finisce la scansione delle chiamate;
 *  5. `LaunchRoute.cs` non nomina `Windows.` - e' il file che il runner compila, ed e'
 *     la regola che i test controllano (`ProtocolTests/LaunchRouteTests.cs`);
 *  6. un tocco sulla tile di una chat pinnata apre quella chat: `OpenPinnedChat` esiste
 *     ed e' chiamata da `App`, che e' l'unica cosa che una tile puo' chiedere a un avvio
 *     che trova la pagina gia' viva.
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
const SESSION = 'WhatsappApp/Services/SessionService.cs';

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

  // 1. L'avvio prende la rotta, la nomina, e non ne fa una sezione.
  const launched = methodBody(files.app, 'protected override void OnLaunched(');
  if (!launched) {
    problems.push('WhatsappApp/App.xaml.cs: no OnLaunched - there is nowhere the route of a tap could arrive');
  } else {
    if (!/SessionService\.TakeRoute\s*\(/.test(launched)) {
      problems.push('WhatsappApp/App.xaml.cs: OnLaunched never calls SessionService.TakeRoute() - ' +
        'a tap on the tile arrives as a plain launch and the route it left is the only thing that says where it was');
    }
    if (!/Diag\.Ok\s*\([^)]*route/.test(launched)) {
      problems.push('WhatsappApp/App.xaml.cs: OnLaunched takes the route and never names it - ' +
        'a tap that lands somewhere unexpected has to be readable in diag.log');
    }
    if (/SessionService\.PendingSection/.test(launched)) {
      problems.push('WhatsappApp/App.xaml.cs: OnLaunched turns the route into a pending section - ' +
        'a start is the chats (28 October 2026: every launch opened the Status tab)');
    }
  }

  // 2. Un avvio e' la lista chat, e non esiste piu' una sezione in attesa che lo scavalchi.
  const navigatedTo = methodBody(files.home, 'protected override void OnNavigatedTo(') || '';
  if (!/Show\s*\(\s*0\s*,\s*true\s*\)/.test(navigatedTo)) {
    problems.push('WhatsappApp/Pages/HomePage.xaml.cs: OnNavigatedTo does not show the chats (Show(0, true)) - ' +
      'a start has to land on the conversations, whatever tab the app was left on');
  }
  if (!/SessionService\.MarkOpened\s*\(/.test(files.home)) {
    problems.push('WhatsappApp/Pages/HomePage.xaml.cs: the section shown is never passed to ' +
      'SessionService.MarkOpened() - "what arrived since the last look" never moves forward, and the ' +
      'tile keeps advertising what was already read');
  }
  if (/PendingSection/.test(files.home) || /PendingSection/.test(files.session)) {
    problems.push('WhatsappApp/Pages/HomePage.xaml.cs: PendingSection is back - ' +
      'a section in waiting is a start that does not open on the chats');
  }

  // 3. La rotta si scrive col badge, e si riscrive quando cambia qualcosa che non ha badge.
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
  if (!/RefreshLaunchRoute\s*\(\s*\)/.test(methodBody(files.data, 'private void RaiseStatusesChanged(') || '')) {
    problems.push('WhatsappApp/Services/DataService.cs: RaiseStatusesChanged does not refresh the route - ' +
      'a status that arrives has no badge, so the tile would never point at the updates');
  }
  if (!/RefreshLaunchRoute\s*\(\s*\)/.test(methodBody(files.data, 'private void RaiseCallsScanCompleted(') || '')) {
    problems.push('WhatsappApp/Services/DataService.cs: RaiseCallsScanCompleted does not refresh the route - ' +
      'a missed call arrives with the log and nothing else');
  }

  // 4. La regola resta compilabile dal runner desktop.
  if (!/LaunchRoute/.test(files.route)) {
    problems.push('WhatsappApp/Services/LaunchRoute.cs: gone - the rule has no home');
  } else {
    const winrt = /^\s*using\s+Windows\./m.exec(files.route);
    if (winrt) {
      problems.push('WhatsappApp/Services/LaunchRoute.cs: uses ' + winrt[0].trim() + ' - ' +
        'ProtocolTests compiles this file for the desktop and the rule would stop being testable');
    }
  }

  // 5. Una tile che nomina una chat apre quella chat, anche sull'app gia' viva.
  if (!/public void OpenPinnedChat\s*\(/.test(files.home)) {
    problems.push('WhatsappApp/Pages/HomePage.xaml.cs: no OpenPinnedChat() - a pinned chat tile has ' +
      'nothing that can open the chat it names');
  }
  if (!/home\.OpenPinnedChat\s*\(\s*\)/.test(files.app)) {
    problems.push('WhatsappApp/App.xaml.cs: OnLaunched never calls home.OpenPinnedChat() - ' +
      'the resume case is the common one (the app is still alive behind the Start screen)');
  }

  return problems;
}

function main() {
  const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
  const problems = launchRouteProblems({
    app: read(APP),
    home: read(HOME),
    data: read(DATA),
    route: read(ROUTE),
    session: read(SESSION)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} problem(s) with the route a tap on the tile takes.`);
    process.exit(1);
  }
  console.log('OK: the badge records what the tile is showing, the route is named once at the launch, '
    + 'and every start lands on the chats.');
}

if (require.main === module) main();

module.exports = { launchRouteProblems, methodBody };
