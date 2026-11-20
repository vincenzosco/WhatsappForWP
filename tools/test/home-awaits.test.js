'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const page = fs.readFileSync(path.join(ROOT, 'WhatsappApp', 'Pages', 'HomePage.xaml.cs'), 'utf8');

test('the home page fires its guarded work inside the 4014 pragma, like the app does', () => {
  // `App.xaml.cs` marks every call that is fired without awaiting with the
  // pragma pair around one statement, and the home page's seven taps did not -
  // a deliberate fire-and-forget that the compiler and every analysis is free
  // to read as a forgotten await (Visual Studio's list showed exactly that).
  // The marker costs nothing and says the missing await is the point.
  const calls = page.split('Guarded.RunGuardedAsync(').length - 1;
  const disables = page.split('#pragma warning disable 4014').length - 1;
  assert.equal(disables, calls,
    'every RunGuardedAsync on the home page sits inside its own pragma region');
});

test('each pragma region holds the one call it names', () => {
  const region = /#pragma warning disable 4014\r?\n\s*Guarded\.RunGuardedAsync\([^;]+;\r?\n#pragma warning restore 4014/g;
  const wrapped = page.match(region) || [];
  const calls = page.split('Guarded.RunGuardedAsync(').length - 1;
  assert.equal(wrapped.length, calls,
    'disable and restore hug exactly one RunGuardedAsync each');
});