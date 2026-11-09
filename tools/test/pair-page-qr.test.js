'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (file) => fs.readFileSync(path.join(ROOT, file), 'utf8');
const xaml = read('WhatsappApp/Pages/PairPage.xaml');
const code = read('WhatsappApp/Pages/PairPage.xaml.cs');

function bodyAt(source, signature) {
  const start = source.indexOf(signature);
  if (start < 0) return '';
  const open = source.indexOf('{', start);
  let depth = 0;
  for (let i = open; i < source.length; i++) {
    if (source[i] === '{') depth++;
    if (source[i] === '}' && --depth === 0) return source.slice(open, i + 1);
  }
  return '';
}

test('pair page keeps the QR hidden until a real server code is drawn', () => {
  const frame = xaml.match(/<Border\s+x:Name="QrFrame"[\s\S]*?>/);
  assert.ok(frame, 'QR frame exists');
  assert.match(frame[0], /Visibility="Collapsed"/);
  const run = bodyAt(code, 'private async Task RunAsync()');
  assert.match(run, /QrFrame\.Visibility = Visibility\.Collapsed/);
  assert.match(run, /IList<string> codes = await _session\.StartAsync\(\)/);
  assert.match(run, /Draw\(codes\[0\]\)/);
  assert.ok(run.indexOf('Draw(codes[0])') > run.indexOf('_session.StartAsync()'));
});

test('pair page shows the scan state only with a QR and reports network failure with retry', () => {
  const draw = bodyAt(code, 'private void Draw(string text)');
  assert.match(draw, /QrImage\.Source = bitmap/);
  assert.match(draw, /QrFrame\.Visibility = Visibility\.Visible/);
  const run = bodyAt(code, 'private async Task RunAsync()');
  assert.match(run, /QrFrame\.Visibility = Visibility\.Collapsed/);
  assert.match(run, /ErrorText\.Visibility = Visibility\.Visible/);
  assert.match(run, /RetryButton\.Visibility = Visibility\.Visible/);
  assert.match(run, /PairPage\/pairing/);
});
