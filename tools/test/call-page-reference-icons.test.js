'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const XAML = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/CallPage.xaml'), 'utf8');
const PROJECT = fs.readFileSync(path.join(ROOT, 'WhatsappApp/WhatsApp.csproj'), 'utf8');
const ASSET_DIR = path.join(ROOT, 'WhatsappApp/Assets/Reference/ThemeDark');

const icons = [
  ['SpeakerCallButton', 'speaker.png', 'bbd7c8fab1db39b24344ebdf714b22b7f71bf6d22143bf83a449a1c81c7a6780'],
  ['MuteCallButton', 'mute-call.png', '6be4f45fbdf4d33624a7e46311d2d8e83c7244ffa7a0f8931dced60e27e2478d'],
  ['MessageCallButton', 'message.png', 'e1ad8634872ee9b2175732db9237839f450aae7733bad0d7a69a60c17545489d']
];

function buttonMarkup(name) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const start = XAML.search(new RegExp(`<Button\\b(?=[^>]*x:Name="${escaped}")[^>]*>`));
  assert.notEqual(start, -1, `button ${name} exists`);
  const end = XAML.indexOf('</Button>', start);
  return XAML.slice(start, end + '</Button>'.length);
}

test('call action controls use the original packaged XAP icon assets', () => {
  for (const [button, file, digest] of icons) {
    const markup = buttonMarkup(button);
    const uri = `/Assets/Reference/ThemeDark/${file}`;
    const assetPath = path.join(ASSET_DIR, file);
    assert.match(markup, new RegExp(`<Image\\b[^>]*Source="${uri.replace(/[.*+?^${}()|[\\]\\\\]/g, '\\$&')}"`));
    assert.ok(fs.existsSync(assetPath), `${assetPath} exists`);
    const actual = crypto.createHash('sha256').update(fs.readFileSync(assetPath)).digest('hex');
    assert.equal(actual, digest, `${file} remains byte-identical to the extracted XAP asset`);
    assert.match(PROJECT, new RegExp(`<Content Include="Assets\\\\Reference\\\\ThemeDark\\\\${file}"\\s*/>`));
    assert.doesNotMatch(markup, /<Path\b/, `${button} no longer draws a substitute glyph`);
  }
});
