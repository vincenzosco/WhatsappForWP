'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '../..');
const appXaml = fs.readFileSync(path.join(root, 'WhatsappApp/App.xaml'), 'utf8');
const accentSource = fs.readFileSync(path.join(root, 'WhatsappApp/Services/Accent.cs'), 'utf8');

test('the app does not shadow the Windows Phone accent resources', () => {
  assert.doesNotMatch(appXaml, /x:Key="PhoneAccent(?:Color|Brush)"/);
});

test('accent fallback is used only when the platform theme resource is unavailable', () => {
  assert.match(accentSource, /Fallback\s*=\s*Color\.FromArgb\(255,\s*37,\s*211,\s*102\)/);
  assert.match(accentSource, /resources\.Contains\("PhoneAccentColor"\)[\s\S]*?resources\["PhoneAccentColor"\]/);
  assert.match(accentSource, /private static Color _color = Fallback/);
});

test('the XAP bubbles use the phone accent and its 65-percent dark shade', () => {
  assert.match(accentSource, /SetBrush\("ChatIncomingBrush", _color\)/);
  assert.match(accentSource, /SetBrush\("ChatOutgoingBrush", Shade\(_color, 0\.65\)\)/);
  assert.match(accentSource, /SetBrush\("WhatsAppReceivedBubbleBrush", _color\)/);
  assert.match(accentSource, /SetBrush\("WhatsAppSentBubbleBrush", Shade\(_color, 0\.65\)\)/);
  assert.match(accentSource, /ChatBubbleMetaBrush", Color\.FromArgb\(153, 255, 255, 255\)\)/);
});

test('the app bar stays phone chrome instead of being recolored as the accent', () => {
  assert.doesNotMatch(accentSource, /SetBrush\("WhatsAppAppBarBrush", _color\)/);
  assert.doesNotMatch(fs.readFileSync(path.join(root, 'WhatsappApp/App.xaml.cs'), 'utf8'),
    /SetBrushColor\(app, "WhatsAppSentBubbleBrush", "WhatsAppSentBubbleColor"\)/);
});
