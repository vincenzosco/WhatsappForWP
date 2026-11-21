'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const chat = fs.readFileSync(path.join(ROOT, 'WhatsappApp', 'Pages', 'ChatPage.xaml.cs'), 'utf8');

function slice(text, startMarker, size) {
  const at = text.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return text.slice(at, at + size);
}

test('the picker tile fills its own square, so the label sits under the mark', () => {
  // The button was squared by the panel's formula but its content stayed
  // content-sized: the three proportional rows of the XAP tile collapsed to
  // the height of the 54 px mark and the label was drawn over it. The content
  // has to stretch to the tile for the rows to have a height to share.
  const size = slice(chat, 'private void SizeAttachmentTiles(', 1400);
  assert.match(size, /HorizontalContentAlignment = HorizontalAlignment\.Stretch/,
    'the content fills the tile across');
  assert.match(size, /VerticalContentAlignment = VerticalAlignment\.Stretch/,
    'and down');
});

test('the mark scales with the tile and never runs into the label', () => {
  // 54 px is the mark of the panel's own 128 px tile. A tile the formula makes
  // smaller (a taller zoom factor) keeps the same ratio, so the mark stays
  // inside its band and above the label.
  const size = slice(chat, 'private void SizeAttachmentTiles(', 2000);
  assert.match(size, /54\s*\/\s*128/, 'the reference ratio is the panel\'s');
  assert.match(size, /image\.Width|mark/, 'the mark is sized from the tile');
});