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
  home: read('WhatsappApp/Pages/HomePage.xaml.cs'),
  settings: read('WhatsappApp/Services/SettingsService.cs'),
  settingsPage: read('WhatsappApp/Pages/SettingsPage.xaml.cs'),
  settingsXaml: read('WhatsappApp/Pages/SettingsPage.xaml')
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
    /\n            Toast\(title, body\);/,
    '\n            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);'
    + '\n            ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));');
  assert.match(alertProblems(broken).join('\n'), /build a toast|Toast\(\)/);
});

test('un avviso senza marchio temporale suona a ogni login', () => {
  const broken = files();
  broken.notifications = broken.notifications.replace(
    'if (!IsToastNews(KeyStatusSeenAt, postedAt)) return;', '');
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

test('un avviso che ignora il suo interruttore suona anche spento', () => {
  const broken = files();
  broken.notifications = broken.notifications.replace(
    'if (!SettingsService.StatusAlertsEnabled) return;', '');
  assert.match(alertProblems(broken).join('\n'), /StatusAlertsEnabled/);
});

test('un interruttore che nessuno salva torna spento al riavvio', () => {
  const broken = files();
  broken.settings = broken.settings.replace('public static bool CallAlertsEnabled', 'public static bool UnusedCallAlerts');
  assert.match(alertProblems(broken).join('\n'), /CallAlertsEnabled/);
});

test('una pagina che non scrive la scelta mostra un interruttore finto', () => {
  const broken = files();
  broken.settingsPage = broken.settingsPage.replace(
    'SettingsService.MessageAlertsEnabled = MessagesToggle.IsOn;', ';');
  assert.match(alertProblems(broken).join('\n'), /MessageAlertsEnabled/);
});

test('un interruttore che la pagina non ha non si puo spegnere', () => {
  const broken = files();
  broken.settingsXaml = broken.settingsXaml.replace('x:Name="CallsToggle"', '');
  assert.match(alertProblems(broken).join('\n'), /CallsToggle/);
});

test('un corpo si legge dalla firma alla graffa che la chiude', () => {
  assert.match(methodBody('void A()\n{\n  if (x) { y(); }\n}\nvoid B() {}', 'void A('), /y\(\)/);
  assert.strictEqual(methodBody('void A()', 'void A('), null);
});
