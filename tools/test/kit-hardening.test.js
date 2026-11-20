'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const kit = require(path.join(ROOT, 'tools', 'translation-kit.js'));

function makeRoot() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'kit-'));
  fs.mkdirSync(path.join(root, 'WhatsappApp.Core', 'Strings'), { recursive: true });
  fs.mkdirSync(path.join(root, 'traduzioni'), { recursive: true });
  fs.writeFileSync(
    path.join(root, 'WhatsappApp.Core', 'Strings', 'Resources.resx'),
    '<?xml version="1.0" encoding="utf-8"?>\n<root>\n'
    + '  <data name="One" xml:space="preserve">\n    <value>One</value>\n  </data>\n'
    + '  <data name="Two" xml:space="preserve">\n    <value>Two</value>\n  </data>\n'
    + '</root>\n');
  fs.writeFileSync(
    path.join(root, 'WhatsappApp.Core', 'Strings', 'Resources.it-IT.resx'),
    '<?xml version="1.0" encoding="utf-8"?>\n<root>\n'
    + '  <data name="One" xml:space="preserve">\n    <value>Uno</value>\n  </data>\n'
    + '  <data name="Two" xml:space="preserve">\n    <value>Due</value>\n  </data>\n'
    + '</root>\n');
  fs.writeFileSync(
    path.join(root, 'WhatsappApp.Core', 'WhatsappApp.Core.csproj'),
    '<Project>\n  <ItemGroup>\n'
    + '    <EmbeddedResource Include="Strings\\Resources.resx" />\n'
    + '    <EmbeddedResource Include="Strings\\Resources.it-IT.resx">\n'
    + '      <Culture>it-IT</Culture>\n      <WithCulture>true</WithCulture>\n'
    + '    </EmbeddedResource>\n  </ItemGroup>\n</Project>\n');
  return root;
}

test('a refused build leaves the repository as it was', () => {
  // The .resx was written before the project registration was validated: a
  // build the tool itself refused still left the file behind, and the guard
  // then failed on a language nobody had accepted.
  const root = makeRoot();
  const csproj = path.join(root, 'WhatsappApp.Core', 'WhatsappApp.Core.csproj');
  fs.writeFileSync(csproj, '<Project>\n</Project>\n');

  const result = kit.build({ root: root, culture: 'fr', text: 'One = Un\n' });

  assert.equal(result.written, false, 'the build is refused');
  assert.equal(
    fs.existsSync(path.join(root, 'WhatsappApp.Core', 'Strings', 'Resources.fr.resx')),
    false, 'and no resource file is left behind');
});

test('a build refreshes the numbers the site reads', () => {
  const root = makeRoot();
  const result = kit.build({ root: root, culture: 'fr', text: 'One = Un\n' });

  assert.equal(result.written, true);
  const status = JSON.parse(fs.readFileSync(path.join(root, 'site', 'status.json'), 'utf8'));
  assert.equal(status.neutral, 2);
  assert.ok(status.languages.some((language) => language.culture === 'fr'),
    'the new language is in the status');
});

test('a language registered as a self-closing line is registered', () => {
  // The check looked for the opening tag down to `">`; a project whose entry
  // was self-closing looked unregistered and earned a second, duplicate one.
  const csproj = '<Project>\n  <ItemGroup>\n'
    + '    <EmbeddedResource Include="Strings\\Resources.fr.resx" />\n'
    + '  </ItemGroup>\n</Project>\n';
  const result = kit.applyRegistration(csproj, 'fr');
  assert.equal(result.changed, false);
});

test('the catalog keeps the spaces a string is written with', () => {
  const xml = '<root>\n  <data name="Pad" xml:space="preserve">\n'
    + '    <value>  keep me  </value>\n  </data>\n</root>\n';
  const entries = kit.parseCatalog(xml);
  assert.equal(entries[0].value, '  keep me  ', 'the round trip keeps the padding');
});

test('the template on disk is the catalog, and drift is named', () => {
  const root = makeRoot();
  fs.writeFileSync(
    path.join(root, 'traduzioni', 'MODELLO.txt'),
    kit.templateFile(kit.parseCatalog(
      fs.readFileSync(path.join(root, 'WhatsappApp.Core', 'Strings', 'Resources.resx'), 'utf8'))));
  assert.equal(kit.modelloDrift(root), null, 'the fresh template is no drift');

  fs.writeFileSync(path.join(root, 'traduzioni', 'MODELLO.txt'), 'Old = stale\n');
  assert.match(String(kit.modelloDrift(root)), /MODELLO/, 'a stale one is named');
});

test('writes go through a temp file and a rename', () => {
  // The rule this repo writes by: a crash between the two leaves the old
  // file on the phone, never half of the new one.
  const source = fs.readFileSync(path.join(ROOT, 'tools', 'translation-kit.js'), 'utf8');
  assert.match(source, /function writeAtomic/);
  assert.match(source, /\.new/);
  assert.match(source, /renameSync/);
});

test('the guard sees cultures with digits and the template drift', () => {
  // `es-419` has digits: the pattern without them walked past the file and
  // an unregistered language passed the guard. The template drift check is
  // what keeps MODELLO.txt the catalog's own file.
  const source = fs.readFileSync(path.join(ROOT, 'tools', 'check-resw.js'), 'utf8');
  assert.match(source, /\[A-Za-z0-9-\]\+/, 'digits are part of a culture');
  assert.match(source, /modelloDrift/, 'and the template is compared');
});