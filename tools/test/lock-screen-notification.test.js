'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const manifest = fs.readFileSync(path.join(ROOT, 'WhatsappApp', 'Properties', 'WMAppManifest.xml'), 'utf8');
const appx = fs.readFileSync(path.join(ROOT, 'WhatsappApp', 'Package.appxmanifest'), 'utf8');

test('the app declares itself as a notification app for the lock screen', () => {
  // The phone's own list (Impostazioni > schermo di blocco > "scegli un'app per
  // mostrare lo stato dettagliato") is filled from the deployment manifest: an
  // app with no notification service on <App> is simply not offered, whatever
  // its tiles say. The official 2.17.52 carries `NotificationService="WNS"`
  // right there and appears in that list.
  assert.match(manifest, /<App [^>]*NotificationService="WNS"/,
    'the deployment manifest marks the app as a notification app');
});

test('the lock screen still has everything else it needs', () => {
  assert.match(appx, /<m3:LockScreen Notification="badgeAndTileText" BadgeLogo="[^"]+"\s*\/>/,
    'the package manifest asks for the badge and the tile text');
  assert.match(manifest, /<ExtendedTask Name="[^"]+">[\s\S]*?<BackgroundServiceAgent Specifier="ScheduledTaskAgent"/,
    'and a background agent is declared for it');
  assert.match(manifest, /<DeviceLockImageURI IsRelative="true" IsResource="false">[^"]+<\/DeviceLockImageURI>/,
    'and the badge picture is pointed at');
});