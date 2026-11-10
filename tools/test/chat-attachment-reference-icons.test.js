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
const ASSET_DIR = path.join(ROOT, 'WhatsappApp/Assets/Reference/ThemeNeutral');
const buttons = [
  ['AttachmentAlbumsButton', 'attach-picture.png', '9134439ff3601eb4b92617c0ff96ddf29831ae285171a2fc71fe920fccc3f77c'],
  ['AttachmentPhotoButton', 'attach-picture.png', '9134439ff3601eb4b92617c0ff96ddf29831ae285171a2fc71fe920fccc3f77c'],
  ['AttachmentVideoButton', 'attach-video.png', '8cc950b626f646ff3bbeb8219e1ed33b3da39479b8dc95ef390b6a2e7d825bf7'],
  ['AttachmentAudioButton', 'attach-audio.png', 'fb2daa66cdfcb2a2558e5b60241f6e49bfbcec6774fd0c0376c9ebadab5f5ea9'],
  ['AttachmentLocationButton', 'attach-location.png', 'f6bfa7817f45692237d0e77b37c14dcb1cbcf6715743a84c2b9758e377d361b5'],
  ['AttachmentContactButton', 'attach-contact.png', '5b5c93622024c1e8e03eac195c190e60db609d728a65e8333a861ff7a783ed3e']
];

function buttonMarkup(name) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const start = XAML.search(new RegExp(`<Button\\b(?=[^>]*x:Name="${escaped}")[^>]*>`));
  assert.notEqual(start, -1, `button ${name} exists`);
  const end = XAML.indexOf('</Button>', start);
  return XAML.slice(start, end + '</Button>'.length);
}

test('attachment picker tiles use the original XAP artwork and keep their current actions', () => {
  for (const [button, file, expectedDigest] of buttons) {
    const markup = buttonMarkup(button);
    const uri = `/Assets/Reference/ThemeNeutral/${file}`;
    const assetPath = path.join(ASSET_DIR, file);
    assert.ok(markup.includes(`<Image Source="${uri}"`), `${button} renders ${file}`);
    assert.ok(fs.existsSync(assetPath), `${assetPath} exists`);
    const actual = crypto.createHash('sha256').update(fs.readFileSync(assetPath)).digest('hex');
    assert.equal(actual, expectedDigest, `${file} remains byte-identical to the extracted XAP asset`);
    assert.ok(PROJECT.replace(/\\/g, '/').includes(`Assets/Reference/ThemeNeutral/${file}`), `${file} is packaged`);
    assert.doesNotMatch(markup, /<Path\b/, `${button} no longer draws substitute artwork`);
  }

  for (const handler of [
    'AttachmentAlbumsButton_Click', 'AttachmentPhotoButton_Click', 'AttachmentVideoButton_Click',
    'AttachmentAudioButton_Click', 'AttachmentLocationButton_Click', 'AttachmentContactButton_Click'
  ]) {
    assert.match(XAML, new RegExp(`Click="${handler}"`), `${handler} remains wired`);
    assert.match(CODE, new RegExp(`private void ${handler}\\(`), `${handler} remains implemented`);
  }
  assert.match(CODE, /attachment: contact sharing is not available on this client/);
  assert.match(CODE, /attachment: location sharing is not available on this client/);
});
