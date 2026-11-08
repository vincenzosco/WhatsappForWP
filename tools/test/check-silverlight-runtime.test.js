'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (file) => fs.readFileSync(path.join(ROOT, file), 'utf8');

test('Silverlight native fallbacks never invoke managed P/Invoke declarations', () => {
  const interop = read('WhatsappApp/Services/NativeInterop.cs');
  const probe = read('WhatsappApp/Services/CallAudioProbe.cs');
  const encoder = read('WhatsappApp/Services/VoiceNoteEncoder.cs');
  const decoder = read('WhatsappApp/Services/VoiceNoteDecoder.cs');
  const opus = read('WhatsappApp/Services/CallOpus.cs');

  assert.match(interop, /IsSupported/);
  assert.match(interop, /SILVERLIGHT/);
  assert.match(probe, /NativeInterop\.IsSupported/);
  assert.match(encoder, /if\s*\(!NativeInterop\.IsSupported\)/);
  assert.match(decoder, /NativeInterop\.IsSupported/);
  assert.match(opus, /if\s*\(!NativeInterop\.IsSupported\)/);
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
