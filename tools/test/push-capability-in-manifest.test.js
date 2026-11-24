'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const manifest = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Properties/WMAppManifest.xml'), 'utf8');

// The push channel is what wakes an app whose socket died in the background, and the
// platform refuses to open one without the capability - the app logged
// `UnauthorizedAccessException 0x80070005 ... requires ID_CAP_PUSH_NOTIFICATION` on every
// start and then sat there collecting messages nobody could see until it restarted.

test('the manifest can open a push channel', () => {
  assert.match(manifest, /ID_CAP_PUSH_NOTIFICATION/,
    'ID_CAP_PUSH_NOTIFICATION is in WMAppManifest.xml');
});
