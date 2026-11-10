'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const xaml = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ConnectionPage.xaml'), 'utf8');
const code = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ConnectionPage.xaml.cs'), 'utf8');

function element(name) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = new RegExp(`<([\\w:.]+)\\b(?=[^>]*x:Name="${escaped}")[^>]*>`).exec(xaml);
  assert.ok(match, `XAML element ${name} exists`);
  return match[0];
}

test('account page uses the reference secondary-page title and section heading hierarchy', () => {
  const title = element('PageTitleText');
  assert.match(title, /FontSize="54"/);
  assert.match(title, /FontFamily="Segoe WP Semilight"/);
  for (const name of ['LinkedLabel', 'StatusLabel']) {
    const heading = element(name);
    assert.match(heading, /FontSize="24"/, `${name} uses the reference section heading size`);
    assert.match(heading, /FontWeight="SemiBold"/, `${name} uses the reference section heading weight`);
  }
});

test('the linked number is the primary left-aligned content rather than a centered card', () => {
  const panel = element('NumberPanel');
  const number = element('AccountNumberText');
  assert.doesNotMatch(panel, /Background=/);
  assert.match(number, /FontSize="30"/);
  assert.match(number, /FontFamily="Segoe WP Semilight"/);
  assert.doesNotMatch(number, /TextAlignment="Center"/);
});

test('account screen retains only the linked account, connection state and working link-device route', () => {
  assert.match(xaml, /x:Name="AccountNumberText"/);
  assert.match(xaml, /x:Name="StatusText"/);
  assert.match(xaml, /x:Name="LinkDeviceButton"[^>]*Click="LinkDeviceButton_Click"/);
  assert.match(code, /private void LinkDeviceButton_Click[\s\S]*?PairPage\.xaml/);
  assert.match(code, /private void ShowStatus\(\)[\s\S]*NativeConnection\.Instance\.Status/);
  assert.doesNotMatch(xaml, /Password|Server address|Access token|Adapter/);
});
