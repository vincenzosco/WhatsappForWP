'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

function slice(text, startMarker, size) {
  const at = text.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return text.slice(at, at + size);
}

const homeXaml = read('WhatsappApp/Pages/HomePage.xaml');
const connection = read('WhatsappApp/Pages/ConnectionPage.xaml.cs');
const data = read('WhatsappApp/Services/DataService.cs');

test('every home app bar menu carries the settings entry', () => {
  // The entry existed in the calls bar only: from the chats tab, where the app
  // opens, the "..." menu had no way to the settings at all.
  const menus = homeXaml.match(/<shell:ApplicationBar\.MenuItems>[\s\S]*?<\/shell:ApplicationBar\.MenuItems>/g) || [];
  assert.ok(menus.length >= 3, 'the home has its per-tab menus');
  for (const menu of menus) {
    assert.match(menu, /x:Uid="Home_SettingsAppBarMenuItem"/,
      'every one of them names the settings entry');
  }
});

test('the account page can disconnect this phone, after a word with the person', () => {
  const handler = slice(connection, 'private void DisconnectButton_Click(', 1200);
  assert.match(handler, /MessageBox\.Show\(/, 'the disconnect asks first');
  assert.match(handler, /ForgetAccount\(\)/, 'and then forgets the account');
  assert.match(handler, /PairPage\.xaml/, 'the next thing on screen is the pairing');
});

test('forgetting the account leaves nothing of it behind', () => {
  const forget = slice(data, 'public void ForgetAccount(', 1400);
  assert.match(forget, /DeleteChat\(/, 'the chats go');
  assert.match(forget, /NameCache\.Clear\(\)/, 'the saved names go');
  assert.match(forget, /CallLogStore\.Reset\(\)/, 'the call history goes');
  assert.match(forget, /Session\(\)\.Clear\(\)/, 'and the session goes');
});
