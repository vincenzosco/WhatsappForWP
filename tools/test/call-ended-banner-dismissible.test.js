'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const home = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/HomePage.xaml.cs'), 'utf8');

test('the home banner keeps one button that dismisses an ended call', () => {
  // The banner that says a call is over had nothing to close it: Accept and Decline
  // belong to a ringing call, End to a live one, and with `Ended` all three collapsed -
  // so the banner and its one-second ticker stayed until the app was restarted. The call
  // screen solved the same case with `over`; the banner needs the same visibility and
  // nothing else, because its End handler already dismisses.
  const showCall = home.slice(home.indexOf('private void ShowCall'),
    home.indexOf('private void StartCallTicker'));
  assert.match(showCall, /bool over = call\.Phase == CallPhase\.Ended/,
    'the state it is kept for is named');
  assert.match(showCall, /EndCallButton\.Visibility = \(live \|\| over\)/,
    'and the end button stays up once the call is over');
});

test('the end button of the banner closes the call it shows', () => {
  // A button that is only there to be tapped had better do the one thing there is left
  // to do: take the banner down. Without this the visibility alone would leave a button
  // that ends a call that is already over.
  assert.match(home, /Phase == CallPhase\.Ended[\s\S]{0,160}DismissCall\(\)/,
    'the end handler dismisses an ended call');
});

test('the banner says what the button does, like the call screen', () => {
  // The call screen calls the same button "Close" once the call is over and "End" while
  // it is up; a banner that says the call ended and offers "End" is asking for the wrong
  // thing.
  const showCall = home.slice(home.indexOf('private void ShowCall'),
    home.indexOf('private void StartCallTicker'));
  assert.match(showCall, /Call_Close/,
    'the ended state names its button for what it does');
});
