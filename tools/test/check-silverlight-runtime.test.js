'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (file) => fs.readFileSync(path.join(ROOT, file), 'utf8');

test('the codec path carries no P/Invoke, because the platform refuses it', () => {
  // The phone answered `DllImport cannot be used on user-defined methods` - the model
  // every Silverlight app on this platform runs under - and that ended the native codec.
  // What runs now is managed code (`WhatsappApp/Opus`), so a `DllImport` anywhere in the
  // codec path is a call that dies on the first frame. The one place still allowed to
  // ask the platform is `CallAudioProbe`, which only asks and reports the answer.
  const encoder = read('WhatsappApp/Services/VoiceNoteEncoder.cs');
  const decoder = read('WhatsappApp/Services/VoiceNoteDecoder.cs');
  const opus = read('WhatsappApp/Services/CallOpus.cs');

  for (const pair of [['VoiceNoteEncoder', encoder], ['VoiceNoteDecoder', decoder],
    ['CallOpus', opus]]) {
    assert.doesNotMatch(pair[1], /\[DllImport|static\s+extern/,
      pair[0] + ' is managed end to end');
  }

  assert.match(opus, /OpusEncoder\.Create|OpusDecoder\.Create/,
    'the frames go through the managed codec');
  assert.match(read('WhatsappApp/Services/CallAudioProbe.cs'), /NativeInterop\.IsSupported/,
    'the probe keeps asking the platform and reporting what it says');
});

test('normal first-run file absence is found by listing instead of exception control flow', () => {
  const lookup = read('WhatsappApp/Services/StorageLookup.cs');
  assert.match(lookup, /GetFilesAsync\s*\(/);
  assert.doesNotMatch(lookup, /GetFileAsync\s*\(/);
  assert.doesNotMatch(lookup, /catch\s*\(\s*FileNotFoundException/);
});

test('a Silverlight launch maps an unpaired session to the pairing route', () => {
  const app = read('WhatsappApp/App.xaml.cs');
  assert.match(app, /private Uri MapInitialUri\s*\(/);
  assert.match(app, /StartDecision\.Decide\s*\(/);
  assert.match(app, /StartTarget\.Pair\s*\?\s*PairPageUri\s*:\s*HomePageUri/);
  assert.match(app, /_rootFrame\.UriMapper\s*=\s*new AppUriMapper\(this\)/);
  assert.match(app, /service\.Launching\s*\+=\s*OnLaunching/);
  const launching = app.slice(app.indexOf('private void OnLaunching('), app.indexOf('private void OnActivated('));
  assert.match(launching, /EnsureStartupPage\s*\(\s*\)/);
});

test('Silverlight resources use the embedded neutral and Italian resx files', () => {
  const project = read('WhatsappApp.Core/WhatsappApp.Core.csproj');
  assert.match(project, /<EmbeddedResource Include="Strings\\Resources\.resx">/);
  assert.match(project, /<EmbeddedResource Include="Strings\\Resources\.it-IT\.resx">[\s\S]*?<Culture>it-IT<\/Culture>/);
  assert.doesNotMatch(project, /<PRIResource\s/);
});
