'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const kit = require('../translation-kit.js');

const ROOT = path.join(__dirname, '..', '..');
const NEUTRAL = path.join(ROOT, 'WhatsappApp.Core', 'Strings', 'Resources.resx');

/** A small fake checkout: the catalog, the csproj entry to mirror, and room for output. */
function fixture(keys) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'waptk-'));
  const strings = path.join(root, 'WhatsappApp.Core', 'Strings');
  fs.mkdirSync(strings, { recursive: true });
  const catalog = keys.map((pair, i) =>
    '  <data name="' + pair[0] + '" xml:space="preserve">\n    <value>' + pair[1] + '</value>\n  </data>').join('\n');
  fs.writeFileSync(path.join(strings, 'Resources.resx'),
    '<?xml version="1.0" encoding="utf-8"?>\n<root>\n' + catalog + '\n</root>\n');
  fs.writeFileSync(path.join(root, 'WhatsappApp.Core', 'WhatsappApp.Core.csproj'),
    '<Project>\n'
    + '  <ItemGroup>\n'
    + '    <EmbeddedResource Include="Strings\\Resources.resx">\n'
    + '      <WithCulture>false</WithCulture>\n'
    + '    </EmbeddedResource>\n'
    + '    <EmbeddedResource Include="Strings\\Resources.it-IT.resx">\n'
    + '      <Culture>it-IT</Culture>\n'
    + '      <WithCulture>true</WithCulture>\n'
    + '    </EmbeddedResource>\n'
    + '  </ItemGroup>\n'
    + '</Project>\n');
  return root;
}

test('the template lists every key of the neutral catalog, in order', () => {
  const catalog = kit.parseCatalog(fs.readFileSync(NEUTRAL, 'utf8'));
  assert.ok(catalog.length > 200, 'the app catalog has the real string set');

  const template = kit.templateFile(catalog);
  const lines = template.split('\n').filter((line) => line.length > 0 && !line.startsWith('#'));

  assert.equal(lines.length, catalog.length, 'one line per key, no extras');
  for (let i = 0; i < catalog.length; i++) {
    assert.equal(lines[i], catalog[i].key + ' = ' + catalog[i].value,
      'key ' + catalog[i].key + ' sits where the catalog puts it');
  }
});

test('a build keeps only the keys the translator wrote', () => {
  const root = fixture([
    ['Nav_Chats', 'Chats'],
    ['TileUnreadMany', '{0} unread messages'],
    ['Quit_Cancel', 'Cancel']
  ]);

  const result = kit.build({
    root: root,
    culture: 'fr',
    text: 'Nav_Chats = Chat\nTileUnreadMany = {0} messaggi non letti\n'
  });

  assert.deepEqual(result.errors, []);
  assert.equal(result.translated, 2);
  assert.equal(result.total, 3);
  assert.equal(result.coverage, 67, 'rounded percentage');

  const resx = fs.readFileSync(
    path.join(root, 'WhatsappApp.Core', 'Strings', 'Resources.fr.resx'), 'utf8');
  assert.match(resx, /<data name="Nav_Chats"[^>]*>\s*<value>Chat<\/value>/,
    'the translation is there');
  assert.match(resx, /<value>\{0\} messaggi non letti<\/value>/);
  assert.doesNotMatch(resx, /Quit_Cancel/,
    'a key the translator left out stays English by itself');
});

test('values are escaped on their way into the xml', () => {
  const root = fixture([['About_Text', 'a & b']]);

  const result = kit.build({
    root: root,
    culture: 'fr',
    text: 'About_Text = a & <b> piu di cosi\n'
  });

  assert.deepEqual(result.errors, []);
  const resx = fs.readFileSync(
    path.join(root, 'WhatsappApp.Core', 'Strings', 'Resources.fr.resx'), 'utf8');
  assert.match(resx, /<value>a &amp; &lt;b&gt; piu di cosi<\/value>/,
    'the ampersand and the brackets survive the round trip');
});

test('the file format keeps an equal sign inside a value', () => {
  const parsed = kit.parseTranslationFile('About_Legal = uno = due\n# nota\n\nFoo = bar\n');
  assert.deepEqual(parsed.errors, []);
  assert.equal(parsed.entries.length, 2);
  assert.equal(parsed.entries[0].value, 'uno = due');
});

test('an unknown key is refused with its line number', () => {
  const root = fixture([['Nav_Chats', 'Chats']]);
  const result = kit.build({
    root: root,
    culture: 'fr',
    text: 'Nav_Chats = Chat\nNo_Such_Key = x\n'
  });

  assert.equal(result.errors.length, 1);
  assert.match(result.errors[0].message, /No_Such_Key/);
  assert.equal(result.errors[0].line, 2);
  assert.equal(result.written, false, 'nothing is written when a key is wrong');
});

test('a key typed twice is refused', () => {
  const root = fixture([['Nav_Chats', 'Chats']]);
  const result = kit.build({
    root: root,
    culture: 'fr',
    text: 'Nav_Chats = Chat\nNav_Chats = Gespräch\n'
  });

  assert.equal(result.errors.length, 1);
  assert.equal(result.errors[0].line, 2);
});

test('a dropped placeholder is refused', () => {
  const root = fixture([['TileUnreadMany', '{0} unread messages']]);
  const result = kit.build({
    root: root,
    culture: 'fr',
    text: 'TileUnreadMany = messaggi non letti\n'
  });

  assert.equal(result.errors.length, 1);
  assert.match(result.errors[0].message, /\{0\}/);
  assert.equal(result.written, false);
});

test('the csproj entry is planted once and stays idempotent', () => {
  const root = fixture([['Nav_Chats', 'Chats']]);

  kit.build({ root: root, culture: 'fr', text: 'Nav_Chats = Chat\n' });
  const first = fs.readFileSync(
    path.join(root, 'WhatsappApp.Core', 'WhatsappApp.Core.csproj'), 'utf8');
  assert.match(first, /<EmbeddedResource Include="Strings\\Resources\.fr\.resx">/);
  assert.match(first, /<Culture>fr<\/Culture>/);

  kit.build({ root: root, culture: 'fr', text: 'Nav_Chats = Chat\n' });
  const second = fs.readFileSync(
    path.join(root, 'WhatsappApp.Core', 'WhatsappApp.Core.csproj'), 'utf8');
  const plantings = second.split('<EmbeddedResource Include="Strings\\Resources.fr.resx">').length - 1;
  assert.equal(plantings, 1, 'registered once however many times the build runs');
});

test('a culture reads like a culture, or the build refuses', () => {
  for (const culture of ['fr', 'fr-FR', 'pt-BR', 'zh-Hans']) {
    assert.equal(kit.cultureIsSane(culture), true, culture);
  }
  for (const culture of ['fra ce', '../x', 'fr.resx', '', 'FR_FR']) {
    assert.equal(kit.cultureIsSane(culture), false, JSON.stringify(culture));
  }
});

test('the status line-up counts what each language has', () => {
  const root = fixture([
    ['Nav_Chats', 'Chats'],
    ['Nav_Status', 'Status'],
    ['Nav_Calls', 'Calls']
  ]);
  fs.writeFileSync(
    path.join(root, 'WhatsappApp.Core', 'Strings', 'Resources.fr.resx'),
    '<root>\n  <data name="Nav_Chats" xml:space="preserve">\n    <value>Chat</value>\n  </data>\n</root>\n');

  const status = kit.languageStatus(root);
  assert.equal(status.neutral, 3);

  const fr = status.languages.filter((language) => language.culture === 'fr')[0];
  assert.equal(fr.translated, 1);
  assert.equal(fr.coverage, 33);

  const written = kit.writeStatus(root, status);
  const site = JSON.parse(fs.readFileSync(written, 'utf8'));
  assert.equal(site.neutral, 3);
  assert.equal(site.languages.length, status.languages.length);
});

test('a translated line keeps its placeholders in order to pass', () => {
  const root = fixture([['Hello', 'ciao {0} alle {1}']]);
  const ok = kit.build({
    root: root,
    culture: 'fr',
    text: 'Hello = salut {1} a {0}\n'
  });
  assert.deepEqual(ok.errors, [], 'the order may change: the slots must all stay');
});

test('the app catalog parses without a duplicate key', () => {
  const catalog = kit.parseCatalog(fs.readFileSync(NEUTRAL, 'utf8'));
  const seen = new Set();
  for (const entry of catalog) {
    assert.equal(seen.has(entry.key), false, entry.key + ' typed once');
    seen.add(entry.key);
  }
});