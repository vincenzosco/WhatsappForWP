'use strict';
const test = require('node:test');
const assert = require('node:assert');

const pf = require('../check-project-files');

test('un Include con i backslash diventa uno slash', () => {
  assert.strictEqual(pf.normalize('Services\\Guarded.cs'), 'Services/Guarded.cs');
  assert.strictEqual(pf.normalize('Pages/ChatPage.xaml'), 'Pages/ChatPage.xaml');
});

test('includes raccoglie Compile, Page e ApplicationDefinition', () => {
  const project = '<Project>\n' +
    '  <Compile Include="Services\\Guarded.cs" />\n' +
    '  <Page Include="Pages\\ChatPage.xaml" />\n' +
    '  <ApplicationDefinition Include="App.xaml" />\n' +
    '  <Content Include="Assets\\Logo.png" />\n' +
    '</Project>';
  const found = pf.includes(project);
  assert.strictEqual(found.compile.has('Services/Guarded.cs'), true);
  assert.strictEqual(found.pages.has('Pages/ChatPage.xaml'), true);
  assert.strictEqual(found.pages.has('App.xaml'), true);
  assert.strictEqual(found.compile.has('Assets/Logo.png'), false);
});

test('relative porta un file del progetto al percorso dell Include', () => {
  const file = require('path').join(__dirname, '..', '..', 'WhatsappApp', 'Services', 'Guarded.cs');
  assert.strictEqual(pf.relative(file), 'Services/Guarded.cs');
});

test('walk salta obj e bin', () => {
  const out = pf.walk(require('path').join(__dirname, '..', '..', 'WhatsappApp'), []);
  const rels = out.map((f) => f.replace(/\\/g, '/'));
  assert.strictEqual(rels.some((f) => f.includes('/obj/')), false);
  assert.strictEqual(rels.some((f) => f.includes('/bin/')), false);
  assert.strictEqual(rels.some((f) => f.endsWith('WhatsappApp.csproj')), true);
});
