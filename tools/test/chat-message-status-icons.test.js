'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const XAML = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ChatPage.xaml'), 'utf8');
const CONVERTERS = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Converters/Converters.cs'), 'utf8');
const PROJECT = fs.readFileSync(path.join(ROOT, 'WhatsappApp/WhatsApp.csproj'), 'utf8').replace(/\\/g, '/');

const ICONS = {
  Sending: { field: 'Sending', asset: 'ThemeDark/inline-100-clock.png' },
  Sent: { field: 'Sent', asset: 'ThemeDark/inline-100-check.png' },
  Delivered: { field: 'Delivered', asset: 'ThemeDark/inline-100-double-check.png' },
  Read: { field: 'Read', asset: 'ThemeNeutral/inline-blue-checks.png' },
  Failed: { field: 'Failed', asset: 'ThemeDark/inline-100-error.png' }
};

test('outgoing message footer uses the XAP delivery icon for every existing state', () => {
  assert.match(XAML, /<conv:MessageStatusToIconConverter x:Key="MsgStatusToIcon"\s*\/>/);
  assert.match(XAML, /<Image[^>]*Source="\{Binding Status, Converter=\{StaticResource MsgStatusToIcon\}\}"/);
  assert.doesNotMatch(XAML, /Text="\{Binding Status, Converter=\{StaticResource MsgStatusToString\}\}"/);
  for (const [status, icon] of Object.entries(ICONS)) {
    assert.match(CONVERTERS,
      new RegExp(`case Models\\.MessageStatus\\.${status}:\\s*return ${icon.field};`),
      `${status} maps to its cached icon`);
    assert.ok(CONVERTERS.includes(`BitmapImage ${icon.field} = Icon("${icon.asset}");`),
      `${status} cached icon uses the reference asset`);
    assert.ok(PROJECT.includes(`Assets/Reference/${icon.asset}`), `${icon.asset} is packaged`);
  }
});
