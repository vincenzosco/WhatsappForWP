'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');

const pi = require('../check-project-includes');
const ROOT = path.join(__dirname, '..', '..');

test('includes reads the file-based items and skips assembly references', () => {
  const project = '<Project>\n' +
    '  <Reference Include="Zlib.Portable, Version=1.11.0.0, Culture=neutral, PublicKeyToken=431cba815f6a8b5b" />\n' +
    '  <Content Include="Assets\\SplashScreen.scale-240.png" />\n' +
    '  <Compile Include="Services\\Guarded.cs" />\n' +
    '  <Page Include="Pages\\ChatPage.xaml" />\n' +
    '</Project>';
  const found = pi.includes(project);
  assert.ok(found.includes('Assets\\SplashScreen.scale-240.png'), 'reads a Content include');
  assert.ok(found.includes('Services\\Guarded.cs'), 'reads a Compile include');
  assert.ok(found.includes('Pages\\ChatPage.xaml'), 'reads a Page include');
  assert.ok(!found.some((i) => i.indexOf('Zlib.Portable') === 0),
    'an assembly identity is not a file the project packages');
});

test('a wildcard is a folder contract, not one file to look up', () => {
  assert.equal(pi.isWildcard('Services\\**\\*.cs'), true);
  assert.equal(pi.isWildcard('Assets\\SplashScreen.scale-240.png'), false);
});

test('missingIncludes names a reference that resolves to no file', () => {
  // The exact shape of the bug: the project packaged a splash PNG that a
  // commit had already converted to .jpg, so the reference named nothing.
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'wa-includes-'));
  fs.writeFileSync(path.join(dir, 'SplashScreen.scale-240.jpg'), 'x');
  const project = '<Project>\n' +
    '  <Content Include="Assets\\SplashScreen.scale-240.png" />\n' +
    '  <Content Include="SplashScreen.scale-240.jpg" />\n' +
    '</Project>';
  const missing = pi.missingIncludes(project, dir);
  assert.deepEqual(missing, ['Assets\\SplashScreen.scale-240.png'],
    'the renamed-away reference is reported and the file that exists is not');
  fs.rmSync(dir, { recursive: true, force: true });
});

test('every file the app, core and tasks projects list is on the tree', () => {
  const projects = [
    'WhatsappApp/WhatsApp.csproj',
    'WhatsappApp.Core/WhatsappApp.Core.csproj',
    'WhatsappApp.Tasks/WhatsappApp.Tasks.csproj',
  ];
  for (const rel of projects) {
    const project = path.join(ROOT, rel);
    const missing = pi.missingIncludes(fs.readFileSync(project, 'utf8'), path.dirname(project));
    assert.deepEqual(missing, [], `${rel} lists only files the tree holds`);
  }
});
