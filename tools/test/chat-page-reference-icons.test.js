'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const XAML = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ChatPage.xaml'), 'utf8');
const CODE = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ChatPage.xaml.cs'), 'utf8');
const PROJECT = fs.readFileSync(path.join(ROOT, 'WhatsappApp/WhatsApp.csproj'), 'utf8');
const ASSET_DIR = path.join(ROOT, 'WhatsappApp/Assets/Reference/ThemeDark');
const icons = [
  // The three composer-scale glyphs were cut for an input bar of their own and
  // reached past the app-bar ring; they are trimmed inside their canvases so the
  // drawn pixels sit in the circle (tools/test/chat-bar-icons-fit.test.js holds
  // the rule). The handset already fitted and stays the extracted XAP asset.
  ['AttachButton', 'inputbar-attach.png', '277fa45824d37f1e152117d47b52ae3154d30e9713bfd1e3a7813bfada75cbc9'],
  ['RecordButton', 'inputbar-mic.png', 'f187b8b94c95e80c54c7478e975ed0dd19efdb7e85fdfe80221beafce4288ffa'],
  ['SendButton', 'inputbar-send.png', '8f314665536ced01d692f28e0aaa07e7ae97424f218b3e1e56e21ddaea719b11'],
  ['CallHeaderButton', 'phone-solid.png', 'c8507249ee6aecc1926dda0a6c9bed0a51dd5443e5df2220a85cad328b8dc9c6']
];

function buttonMarkup(name) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const appBarButton = XAML.match(new RegExp(
    `<shell:ApplicationBarIconButton\\b(?=[^>]*x:Name="${escaped}")[^>]*/>`
  ));
  if (appBarButton) return appBarButton[0];
  const start = XAML.search(new RegExp(`<Button\\b(?=[^>]*x:Name="${escaped}")[^>]*>`));
  assert.notEqual(start, -1, `button ${name} exists`);
  const end = XAML.indexOf('</Button>', start);
  return XAML.slice(start, end + '</Button>'.length);
}

function projectContainsAsset(file) {
  const normalized = PROJECT.replace(/\\/g, '/');
  return normalized.includes(`Assets/Reference/ThemeDark/${file}`);
}

test('supported chat actions render the original XAP icon artwork', () => {
  for (const [button, file, digest] of icons) {
    const markup = buttonMarkup(button);
    const uri = `/Assets/Reference/ThemeDark/${file}`;
    const assetPath = path.join(ASSET_DIR, file);
    const usesNativeIcon = markup.startsWith('<shell:ApplicationBarIconButton');
    assert.ok(markup.includes(usesNativeIcon ? `IconUri="${uri}"` : `<Image Source="${uri}"`),
      `${button} renders ${file}`);
    assert.ok(fs.existsSync(assetPath), `${assetPath} exists`);
    const actual = crypto.createHash('sha256').update(fs.readFileSync(assetPath)).digest('hex');
    assert.equal(actual, digest, `${file} matches the pinned artwork`);
    assert.ok(projectContainsAsset(file), `${file} is packaged by the app project`);
    assert.doesNotMatch(markup, /<Path\b/, `${button} no longer draws substitute artwork`);
  }
});

test('the unsupported emoji panel is not implied by changing the existing button', () => {
  const smiley = buttonMarkup('SmileyButton');
  assert.match(smiley, /Click="SmileyButton_Click"/);
  assert.match(smiley, /IconUri="\/Assets\/Reference\/ThemeDark\/inputbar-emoji\.png"/);
  assert.match(CODE, /private void SmileyButton_Click[\s\S]*MessageTextBox\.Focus\(\)/);
  assert.match(XAML, /<InputScopeName NameValue="Chat"\s*\/>/);
});
