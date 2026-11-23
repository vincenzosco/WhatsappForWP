'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const home = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/HomePage.xaml.cs'), 'utf8');

test('the home banner stops its ticker when the page is left', () => {
  // The one-second ticker re-shows the call banner, and a ringing call shows it by
  // opening the call screen. Left running after the page was left, it did exactly that
  // from the back stack: press hardware Back on the call screen and it was pushed again
  // within a tick, so there was no way out of a ringing call. The call screen stops its
  // own ticker on leave; this page kept its own running.
  const left = home.slice(home.indexOf('protected override void OnNavigatedFrom'),
    home.indexOf('private void OnCallChanged'));
  assert.match(left, /StopCallTicker\(\)/,
    'the ticker is stopped when the page is left');
});

test('the ticker comes back when the page comes back', () => {
  // Stopping it is only right if the banner has a way back up: the same page shows the
  // call again when it becomes visible, and showing it starts the ticker.
  assert.match(home, /override void OnNavigatedTo[\s\S]{0,1600}ShowCall\(\);/,
    'the page re-shows the call when it is returned to');
});
