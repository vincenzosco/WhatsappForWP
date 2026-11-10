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
  ['AttachButton', 'inputbar-attach.png', '620f57e056fa069c24939c576efd8e608293602ab33ee5bca4a94b57ed9263e3'],
  ['RecordButton', 'inputbar-mic.png', '474aeae3d63e202c26246454a99e38a109d7c4dfc18eb1447223ee4493cfb9d0'],
  ['SendButton', 'inputbar-send.png', 'cf071fcaf9678f18eec00154815c7816fdac85dc75b9e56482413133182f00db'],
  ['CallHeaderButton', 'phone-solid.png', 'c8507249ee6aecc1926dda0a6c9bed0a51dd5443e5df2220a85cad328b8dc9c6']
];

function buttonMarkup(name) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
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
    assert.ok(markup.includes(`<Image Source="${uri}"`), `${button} renders ${file}`);
    assert.ok(fs.existsSync(assetPath), `${assetPath} exists`);
    const actual = crypto.createHash('sha256').update(fs.readFileSync(assetPath)).digest('hex');
    assert.equal(actual, digest, `${file} remains byte-identical to the extracted XAP asset`);
    assert.ok(projectContainsAsset(file), `${file} is packaged by the app project`);
    assert.doesNotMatch(markup, /<Path\b/, `${button} no longer draws substitute artwork`);
  }
});

test('the unsupported emoji panel is not implied by changing the existing button', () => {
  const smiley = buttonMarkup('SmileyButton');
  assert.match(smiley, /Click="SmileyButton_Click"/);
  assert.match(smiley, /<Path\b/);
  assert.match(CODE, /private void SmileyButton_Click[\s\S]*MessageTextBox\.Focus\(\)/);
  assert.match(XAML, /<InputScopeName NameValue="Chat"\s*\/>/);
});
