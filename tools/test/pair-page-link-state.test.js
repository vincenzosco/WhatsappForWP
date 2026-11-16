'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (file) => fs.readFileSync(path.join(ROOT, file), 'utf8');
const code = read('WhatsappApp/Pages/PairPage.xaml.cs');
const connection = read('WhatsappApp/Pages/ConnectionPage.xaml.cs');

function bodyAt(source, signature) {
  const start = source.indexOf(signature);
  if (start < 0) return '';
  const open = source.indexOf('{', start);
  let depth = 0;
  for (let i = open; i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}' && --depth === 0) return source.slice(open, i + 1);
  }
  return '';
}

test('an account that is already linked draws no QR over it', () => {
  const decide = bodyAt(code, 'private bool WantsPairing(');
  assert.match(decide, /_store\.HasSession/);
  assert.match(decide, /SessionState\.Expired/);
  const nav = bodyAt(code, 'protected override void OnNavigatedTo');
  const decision = nav.indexOf('WantsPairing(e)');
  const attempt = nav.indexOf('Guarded.RunGuardedAsync("PairPage/pairing"');
  assert.ok(decision >= 0 && attempt > decision,
    'the arrival decides whether a pairing is wanted before the attempt starts');
  assert.match(nav, /NavigateHomeAfterPairing\(\)/,
    'a pointless pairing leaves for the chats instead of drawing a code');
});

test('only an explicit link-again entry re-links a stored account', () => {
  const decide = bodyAt(code, 'private bool WantsPairing(');
  assert.match(decide, /NavigationContext\.QueryString/);
  assert.match(decide, /relink/);
  assert.match(decide, /NavigationMode\.Back/);
  assert.match(connection, /PairPage\.xaml\?relink=1/,
    'the account screen is the one entry that asks to link again');
});

test('the handoff is verified by the landing, not by the accepted answer', () => {
  const navigation = bodyAt(code, 'private void NavigateHomeAfterPairing()');
  assert.match(navigation, /pair: HomePage navigation accepted:/);
  assert.match(navigation, /WatchForHomeLanding\(/,
    'an accepted request is followed to its landing');
  const landing = bodyAt(code, 'private bool HomeHasLanded(');
  assert.match(landing, /HomePage/);
  const tick = bodyAt(code, 'private void LandingWatch_Tick(');
  assert.match(tick, /HomeHasLanded\(\)/);
  assert.match(tick, /pair: the frame landed on HomePage/);
  assert.match(tick, /ShowNavigationFailure\(\)/,
    'a handoff that never lands is shown as a failure, not cleared');
});

test('the pairing page is out of the journal once the chats are up', () => {
  const tick = bodyAt(code, 'private void LandingWatch_Tick(');
  assert.match(tick, /ForgetPairPageInJournal\(\)/);
  const forget = bodyAt(code, 'private void ForgetPairPageInJournal(');
  assert.match(forget, /RemoveBackEntry/);
  assert.match(forget, /\/Pages\/PairPage\.xaml/,
    'only the pairing page entry is removed from the journal');
});

test('the success log names the saved account and the restarted login', () => {
  const run = bodyAt(code, 'private async Task RunAsync()');
  const save = run.indexOf('_store.Save(paired);');
  const saved = run.indexOf('pair: the account is saved');
  const dispatch = run.indexOf('Dispatcher.BeginInvoke(NavigateHomeAfterPairing)');
  const restart = run.indexOf('pair: the login restart began');
  assert.ok(save >= 0 && saved > save && dispatch > saved && restart > dispatch,
    'the log carries the chain: saved account, moved to the chats, login restarted');
});
