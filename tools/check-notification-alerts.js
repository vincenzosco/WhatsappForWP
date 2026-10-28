#!/usr/bin/env node
/**
 * tools/check-notification-alerts.js
 *
 * Guard per gli avvisi che il telefono mostra quando qualcosa arriva e per il numero
 * che smette di crescere quando lo si guarda.
 *
 * Perche' esiste: un aggiornamento e una chiamata persa non producono un messaggio, e
 * per due build non hanno prodotto niente affatto - l'area notifiche parlava solo di
 * chat, e la scheda Chiamate mostrava un badge che contava tutte le chiamate perse del
 * log di WhatsApp, per sempre. Le due cose si sbagliano nello stesso modo silenzioso:
 * il codice compila, il telefono non dice niente, e nessuno se ne accorge finche' non
 * e' un utente a scriverlo.
 *
 * Le trappole che questo file sorveglia:
 *
 *  - un toast costruito fuori da `Toast()` salta il permesso (`NotificationsEnabled`) e
 *    il `catch` che impedisce a un telefono che rifiuta la notifica di portarsi via la
 *    ricezione del messaggio;
 *  - un avviso senza marchio temporale (`IsToastNews`) suona a ogni login: WhatsApp
 *    rimanda l'intera giornata di stati e l'intero log delle chiamate, e senza il
 *    confronto sarebbe un toast per riga a ogni risveglio del task;
 *  - il primo avvio su un telefono non deve annunciare niente: quello che il login ha
 *    portato la prima volta e' storia, non notizia;
 *  - un annuncio di stato fuori dal ramo `inserted` annuncia anche la rilettura della
 *    copia locale e i chunk ripetuti della history;
 *  - `MissedCalls()` che conta tutto il log non scende mai, e un badge che dice sempre
 *    lo stesso numero e' un badge che nessuno guarda.
 *
 * Rules:
 *  1. ogni toast nasce in `Toast()`, e `ShowMessage`, `ShowStatus` e `ShowMissedCall`
 *     passano da li';
 *  2. `ShowStatus` e `ShowMissedCall` filtrano con `IsToastNews`, con due chiavi diverse;
 *  3. `DataService` annuncia uno stato solo quando la riga e' nuova, e non per i propri;
 *  4. `DataService` annuncia le chiamate perse quando il log e' completo
 *     (`RaiseCallsScanCompleted`);
 *  5. `MissedCalls()` conta solo le chiamate piu' recenti dell'ultima volta che la
 *     scheda Chiamate e' stata davanti, e la home la segna (`MarkOpened`).
 *
 * Usage:
 *   node tools/check-notification-alerts.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const NOTIFICATIONS = 'WhatsappApp/Services/NotificationService.cs';
const DATA = 'WhatsappApp/Services/DataService.cs';
const HOME = 'WhatsappApp/Pages/HomePage.xaml.cs';

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

/** I problemi, dati i testi dei file. Puro: i test gli passano copie manomesse. */
function alertProblems(files) {
  const problems = [];

  // 1. Un solo posto costruisce un toast.
  const toast = methodBody(files.notifications, 'private static void Toast(');
  if (!toast) {
    problems.push('WhatsappApp/Services/NotificationService.cs: no Toast() - ' +
      'the permission and the refusal of the phone would be handled once per caller');
  }

  const shows = files.notifications.match(/CreateToastNotifier\(\)\s*\.\s*Show\s*\(/g) || [];
  if (shows.length > 1) {
    problems.push('WhatsappApp/Services/NotificationService.cs: ' + shows.length +
      ' places build a toast - every alert goes through Toast(), which is the only one ' +
      'that asks SettingsService.NotificationsEnabled and swallows a refusing phone');
  }

  const publicShows = ['ShowMessage', 'ShowStatus', 'ShowMissedCall'];
  for (const name of publicShows) {
    const body = methodBody(files.notifications, 'public static void ' + name + '(');
    if (!body) {
      problems.push('WhatsappApp/Services/NotificationService.cs: no ' + name + '()');
    } else if (!/\bToast\s*\(/.test(body)) {
      problems.push('WhatsappApp/Services/NotificationService.cs: ' + name + '() does not call Toast()');
    }
  }

  // 2. Gli avvisi nuovi hanno un marchio temporale, e ognuno il suo.
  for (const name of ['ShowStatus', 'ShowMissedCall']) {
    const body = methodBody(files.notifications, 'public static void ' + name + '(') || '';
    if (!/IsToastNews\s*\(/.test(body)) {
      problems.push('WhatsappApp/Services/NotificationService.cs: ' + name + '() has no IsToastNews() - ' +
        'the sync re-sends the whole day on every login and each row would be a toast');
    }
  }
  const keyStatus = /KeyStatusSeenAt\s*=\s*"([^"]+)"/.exec(files.notifications);
  const keyCalls = /KeyMissedCallSeenAt\s*=\s*"([^"]+)"/.exec(files.notifications);
  if (!keyStatus || !keyCalls) {
    problems.push('WhatsappApp/Services/NotificationService.cs: the two watermarks are not both ' +
      'named - one key would make an update silence a missed call');
  } else if (keyStatus[1] === keyCalls[1]) {
    problems.push('WhatsappApp/Services/NotificationService.cs: the two watermarks share the key "' +
      keyStatus[1] + '" - each alert has to move its own mark');
  }
  const news = methodBody(files.notifications, 'private static bool IsToastNews(') || '';
  if (!/ticks\s*<=\s*0/.test(news)) {
    problems.push('WhatsappApp/Services/NotificationService.cs: IsToastNews() has no first-run case - ' +
      'a new install would be greeted with the whole history that came with the login');
  }

  // 3. Uno stato si annuncia solo se e' nuovo, e mai per se stessi.
  const post = methodBody(files.data, 'private void OnNativeStatusPost(') || '';
  const insertedBranch = /if\s*\(inserted\)\s*\{([\s\S]*?)\n            \}/.exec(post);
  if (!/NotificationService\.ShowStatus\s*\(/.test(post)) {
    problems.push('WhatsappApp/Services/DataService.cs: a new status is never announced - ' +
      'nothing about an update ever reaches the notification area');
  } else if (!insertedBranch || !/NotificationService\.ShowStatus\s*\(/.test(insertedBranch[1])) {
    problems.push('WhatsappApp/Services/DataService.cs: the status toast is outside the ' +
      '"inserted" branch - the cache reload and the repeated history chunks would announce again');
  }
  if (!/NativeConnection\.Instance\.Jid/.test(post)) {
    problems.push('WhatsappApp/Services/DataService.cs: the status toast does not check whether the ' +
      'author is the user - the phone would announce the user\'s own update');
  }

  // 4. Le chiamate perse si annunciano a log completo.
  const completed = methodBody(files.data, 'private void RaiseCallsScanCompleted(') || '';
  if (!/AnnounceMissedCalls\s*\(/.test(completed)) {
    problems.push('WhatsappApp/Services/DataService.cs: RaiseCallsScanCompleted does not call ' +
      'AnnounceMissedCalls() - a missed call arrives with the log and nowhere else');
  }
  const announce = methodBody(files.data, 'private void AnnounceMissedCalls(') || '';
  if (!/NotificationService\.ShowMissedCall\s*\(/.test(announce)) {
    problems.push('WhatsappApp/Services/DataService.cs: AnnounceMissedCalls() never calls ' +
      'NotificationService.ShowMissedCall()');
  }

  // 5. Il badge scende quando la scheda si guarda.
  const badge = methodBody(files.data, 'public int MissedCalls(') || '';
  if (!/SessionService\.OpenedAt\s*\(\s*AppSection\.Calls\s*\)/.test(badge)) {
    problems.push('WhatsappApp/Services/DataService.cs: MissedCalls() does not read ' +
      'SessionService.OpenedAt(AppSection.Calls) - it counts the whole log, so the number ' +
      'never goes down and the badge says nothing about what is new');
  }
  if (!/Timestamp\s*>\s*seen/.test(badge)) {
    problems.push('WhatsappApp/Services/DataService.cs: MissedCalls() reads the last visit and does ' +
      'not compare the rows with it');
  }
  if (!/SessionService\.MarkOpened\s*\(/.test(files.home)) {
    problems.push('WhatsappApp/Pages/HomePage.xaml.cs: the home page never marks a section opened - ' +
      'the badge and the tap route both stop moving forward');
  }

  return problems;
}

function main() {
  const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
  const problems = alertProblems({
    notifications: read(NOTIFICATIONS),
    data: read(DATA),
    home: read(HOME)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} problem(s) with the alerts and the badge.`);
    process.exit(1);
  }
  console.log('OK: updates and missed calls are announced once, and the Calls badge goes down when it is looked at.');
}

if (require.main === module) main();

module.exports = { alertProblems, methodBody };
