'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const { alertProblems, methodBody } = require('../check-notification-alerts');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
const files = () => ({
  notifications: read('WhatsappApp/Services/NotificationService.cs'),
  data: read('WhatsappApp/Services/DataService.cs'),
  home: read('WhatsappApp/Pages/HomePage.xaml.cs')
});

test('stati e chiamate perse si annunciano una volta sola, e il badge scende', () => {
  assert.deepStrictEqual(alertProblems(files()), []);
});

test('un toast costruito fuori dal posto unico salta il permesso', () => {
  const broken = files();
  broken.notifications = broken.notifications.replace(
    /private static void Toast\(string title, string body\)[\s\S]*?\n        \}/,
    'private static void Toast(string title, string body)\n        {\n        }');
  broken.notifications = broken.notifications.replace(
    /(public static void ShowMessage\(string title, string body\)\s*\{\s*)Toast\(title, body\);/,
    '$1var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);\n'
    + '                ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));');
  assert.match(alertProblems(broken).join('\n'), /build a toast|Toast\(\)/);
});

test('un avviso senza marchio temporale suona a ogni login', () => {
  const broken = files();
  broken.notifications = broken.notifications.replace(
    /(public static void ShowStatus\(string name, DateTime postedAt\)\s*\{\s*)if \(!IsToastNews\(KeyStatusSeenAt, postedAt\)\) return;/,
    '$1');
  assert.match(alertProblems(broken).join('\n'), /ShowStatus\(\) has no IsToastNews/);
});

test('due marchi con la stessa chiave si spengono a vicenda', () => {
  const broken = files();
  broken.notifications = broken.notifications.replace('"Notifications.MissedCallSeenAt"', '"Notifications.StatusSeenAt"');
  assert.match(alertProblems(broken).join('\n'), /share the key/);
});

test('un primo avvio senza caso speciale annuncia tutta la storia', () => {
  const broken = files();
  broken.notifications = broken.notifications.replace('if (ticks <= 0)', 'if (ticks < 0)');
  assert.match(alertProblems(broken).join('\n'), /first-run/);
});

test('uno stato annunciato fuori dal ramo inserted riannuncia la copia locale', () => {
  const broken = files();
  broken.data = broken.data.replace(/\n            if \(inserted\)\r?\n            \{/, '\n            if (false)\n            {');
  assert.match(alertProblems(broken).join('\n'), /outside the/);
});

test('annunciare il proprio stato e un toast che l utente non capisce', () => {
  const broken = files();
  broken.data = broken.data.replace('string mine = ChatIdFor(NativeConnection.Instance.Jid);', 'string mine = null;');
  assert.match(alertProblems(broken).join('\n'), /own update/);
});

test('una chiamata persa annunciata prima che il log sia completo parla a meta', () => {
  const broken = files();
  broken.data = broken.data.replace(/\s*AnnounceMissedCalls\(\);\r?\n/, '\n');
  assert.match(alertProblems(broken).join('\n'), /AnnounceMissedCalls/);
});

test('un badge che conta tutto il log non scende mai', () => {
  const broken = files();
  broken.data = broken.data.replace('DateTime seen = SessionService.OpenedAt(AppSection.Calls);',
    'DateTime seen = default(DateTime);');
  assert.match(alertProblems(broken).join('\n'), /OpenedAt/);
});

test('un badge che non confronta le righe con la visita non scende neanche', () => {
  const broken = files();
  broken.data = broken.data.replace('if (row.Timestamp > seen) total++;', 'total++;');
  assert.match(alertProblems(broken).join('\n'), /compare the rows/);
});

test('un corpo si legge dalla firma alla graffa che la chiude', () => {
  assert.match(methodBody('void A()\n{\n  if (x) { y(); }\n}\nvoid B() {}', 'void A('), /y\(\)/);
  assert.strictEqual(methodBody('void A()', 'void A('), null);
});
