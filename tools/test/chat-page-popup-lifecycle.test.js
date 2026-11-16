'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const code = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Pages/ChatPage.xaml.cs'), 'utf8');

function bodyAt(source, signature) {
  const start = source.indexOf(signature);
  if (start < 0) return '';
  const open = source.indexOf('{', start);
  let depth = 0;
  for (let i = open; i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}' && --depth === 0) return source.slice(open, i + 1);
  }
  return '';
}

test('the message menu and the prompt close when the chat page leaves', () => {
  // A Popup is hosted in the root layer, not in the page: one left open floats the
  // dead chat's menu over the next screen, with its buttons still wired to the page
  // that left. Only an explicit close on navigation keeps that from happening.
  const leaving = bodyAt(code, 'protected override void OnNavigatedFrom');
  assert.match(leaving, /_actionPopup\.IsOpen = false/,
    'the shared popup field is closed on the way out');
});
