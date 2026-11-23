'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const home = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/HomePage.xaml.cs'), 'utf8');
const app = fs.readFileSync(path.join(ROOT, 'WhatsappApp/App.xaml.cs'), 'utf8');

test('the home banner does not open the call screen on every redraw', () => {
  // The call screen is opened when a call starts ringing, by the app itself, once. The
  // banner drew itself every second and on every return to the page, and each draw opened
  // it again: the hardware Back could not leave the call screen, it was pushed again
  // within a tick of arriving home.
  const showCall = home.slice(home.indexOf('private void ShowCall'),
    home.indexOf('private void StartCallTicker'));
  assert.doesNotMatch(showCall, /NavigateToPage\("CallPage"/,
    'drawing the banner is not the same thing as opening the call screen');
});

test('the app opens the call screen when the call starts ringing', () => {
  // What makes the rule above safe: the page does not have to push it, because the one
  // place that watches every call - the app - does, and only for a call that is ringing.
  assert.match(app, /private void OnCallChanged[\s\S]{0,400}call\.Phase != CallPhase\.Ringing\) return;/,
    'the app watches for the ring and says nothing about the rest');
  assert.match(app, /OnCallChanged[\s\S]{0,900}NavigateToCallPage/,
    'and opens the call screen from there');
});
