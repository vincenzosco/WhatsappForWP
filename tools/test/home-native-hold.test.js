'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

test('the home rows long-press is the platforms own holding gesture', () => {
  // The same event ChatPage takes its bubbles from: the platform decides what a
  // hold is. A timer standing in for it (650 ms on ManipulationStarted) fired on
  // slow scrolls and never agreed with the hold the rest of the phone recognizes.
  const xaml = read('WhatsappApp/Pages/HomePage.xaml');
  assert.match(xaml, /Hold="ChatRow_Holding"/, 'the native gesture wires the rows');
  assert.doesNotMatch(xaml, /ManipulationStarted="ChatRow_Holding"/);
  assert.doesNotMatch(xaml, /ManipulationCompleted="ChatRow_ManipulationCompleted"/);
});

test('the timer that stood in for holding is gone', () => {
  const page = read('WhatsappApp/Pages/HomePage.xaml.cs');
  assert.doesNotMatch(page, /LongPressMilliseconds/);
  assert.doesNotMatch(page, /_holdTimer/);
  assert.doesNotMatch(page, /OnLongPressTick/);
  assert.doesNotMatch(page, /ChatRow_ManipulationCompleted/);
  assert.match(page, /private void ChatRow_Holding\(object sender, System\.Windows\.Input\.GestureEventArgs e\)/,
    'and the handler takes the native event shape');
  assert.match(page, /e\.Handled = true;/,
    'the hold is taken and not left to scroll beside it');
});