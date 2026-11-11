'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const xaml = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/SettingsPage.xaml'), 'utf8');
const code = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/SettingsPage.xaml.cs'), 'utf8');

function element(name) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = new RegExp(`<([\\w:.]+)\\b(?=[^>]*x:Name="${escaped}")[^>]*>`).exec(xaml);
  assert.ok(match, `XAML element ${name} exists`);
  return match[0];
}

test('settings typography follows the reference title and section hierarchy', () => {
  const title = element('TitleText');
  assert.match(title, /FontSize="54"/);
  assert.match(title, /FontFamily="Segoe WP Semilight"/);
  assert.match(element('BrandText'), /FontSize="15"/);
  for (const name of ['ThemeLabel', 'AlertsLabel', 'BackgroundLabel', 'AccountLabel']) {
    const heading = element(name);
    assert.match(heading, /FontSize="15"/, `${name} uses the reference section header size`);
    assert.match(heading, /FontWeight="SemiBold"/, `${name} uses the reference section header weight`);
  }
  for (const name of ['MessagesLabel', 'StatusesLabel', 'CallsLabel']) {
    assert.match(element(name), /FontSize="24"/, `${name} uses the reference row label size`);
  }
  for (const name of ['ThemeHint', 'AlertsHint', 'BackgroundHint', 'AccountHint']) {
    assert.match(element(name), /FontSize="20"/, `${name} uses the reference descriptive size`);
  }
});

test('settings relies on the hardware back key, like the reference page', () => {
  assert.doesNotMatch(xaml, /x:Name="BackButton"/);
  assert.doesNotMatch(code, /BackButton_Click/);
});

test('settings retains the existing theme, alert, background and account controls', () => {
  for (const name of ['ThemeGrid', 'MessagesToggle', 'StatusesToggle', 'CallsToggle', 'BackgroundToggle', 'AccountButton']) {
    assert.ok(xaml.includes(`x:Name="${name}"`), `${name} remains in the settings page`);
  }
  for (const handler of ['ThemeGrid_ItemClick', 'MessagesToggle_Toggled', 'StatusesToggle_Toggled',
    'CallsToggle_Toggled', 'BackgroundToggle_Toggled', 'AccountButton_Click']) {
    assert.match(code, new RegExp(`private void ${handler}\\(`), `${handler} remains implemented`);
  }
});
