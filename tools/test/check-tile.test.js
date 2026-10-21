'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const tile = require('../check-tile');

const ROOT = path.resolve(__dirname, '..', '..');
const ASSET = path.join(ROOT, 'WhatsappApp', 'Assets', 'TileIcon.scale-240.png');
const WIDE_ASSET = path.join(ROOT, 'WhatsappApp', 'Assets', 'TileIconWide.scale-240.png');

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

test('la tile larga ha un asset suo, della proporzione della tile', () => {
  const size = tile.pngSize(fs.readFileSync(WIDE_ASSET));
  assert.deepStrictEqual(size, { width: 744, height: 360 });
  assert.deepStrictEqual(
    tile.assetProblems({ name: 'Assets/TileIconWide.scale-240.png', ...size, bytes: 30000 }), []);
});

test('un immagine quadrata non va bene per la tile larga', () => {
  const problems = tile.assetProblems(
    { name: 'Assets/TileIcon.scale-240.png', width: 480, height: 480, bytes: 30000 });
  assert.strictEqual(problems.length, 0);
  const wrong = tile.assetProblems(
    { name: 'Assets/FintaWide.png', width: 480, height: 200, bytes: 30000 });
  assert.ok(wrong.some((p) => /the wide tile is 310x150/.test(p)));
});

test('un asset largo sotto la misura minima si segnala', () => {
  const problems = tile.assetProblems(
    { name: 'Assets/FintaWide.png', width: 200, height: 100, bytes: 1024 });
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /at least 310x150/);
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

test('le due immagini della tile sono dichiarate nel servizio', () => {
  const source = fs.readFileSync(
    path.join(ROOT, 'WhatsappApp', 'Services', 'NotificationService.cs'), 'utf8');
  const images = tile.declaredImages(source);
  assert.ok(images.includes('ms-appx:///Assets/TileIcon.png'));
  assert.ok(images.includes('ms-appx:///Assets/TileIconWide.png'));
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
