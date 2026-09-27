'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const tile = require('../check-tile');

const ROOT = path.resolve(__dirname, '..', '..');
const ASSET = path.join(ROOT, 'WhatsappApp', 'Assets', 'TileIcon.scale-240.png');

test('pngSize legge le dimensioni vere del PNG committato', () => {
  const size = tile.pngSize(fs.readFileSync(ASSET));
  assert.deepStrictEqual(size, { width: 480, height: 480 });
});

test('pngSize non crede a un file che non e un PNG', () => {
  assert.strictEqual(tile.pngSize(Buffer.from('questo non e un png, davvero')), null);
});

test('un asset sotto la misura minima si segnala', () => {
  const problems = tile.assetProblems(
    { name: 'Assets/Finto.png', width: 40, height: 40, bytes: 1024 });
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /at least 200x200/);
});

test('un asset oltre i limiti del sistema si segnala due volte', () => {
  const problems = tile.assetProblems(
    { name: 'Assets/Finto.png', width: 2048, height: 2048, bytes: 300 * 1024 });
  assert.strictEqual(problems.length, 2);
  assert.ok(problems.some((p) => /1024x1024/.test(p)));
  assert.ok(problems.some((p) => /200 KB/.test(p)));
});

test('un payload di tile senza src si segnala', () => {
  const problems = tile.bindingProblems(
    'var xml = TileUpdateManager.GetTemplateContent(TileTemplateType.TileSquare150x150IconWithBadge);');
  assert.ok(problems.some((p) => /no binding sets src/.test(p)));
});

test('un commento che cita il modello largo non e codice', () => {
  const problems = tile.bindingProblems(
    '// su WP8.1 il modello TileWide310x150IconWithBadge non esiste\n' +
    'var xml = TileUpdateManager.GetTemplateContent(TileTemplateType.TileSquare150x150IconWithBadge);');
  assert.ok(!problems.some((p) => /TileWide310x150IconWithBadge/.test(p)));
});

test('il percorso dell immagine sopravvive al taglio dei commenti', () => {
  const code = tile.stripComments('var u = "ms-appx:///Assets/TileIcon.png"; // nota');
  assert.deepStrictEqual(tile.declaredImages(code), ['ms-appx:///Assets/TileIcon.png']);
});

test('il modello largo che WP8.1 non ha si segnala', () => {
  const problems = tile.bindingProblems(
    'var t = TileTemplateType.TileWide310x150IconWithBadge;');
  assert.ok(problems.some((p) => /TileWide310x150IconWithBadge/.test(p)));
});
