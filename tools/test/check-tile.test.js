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

test('una tile senza coda non ruota i messaggi e si segnala', () => {
  const problems = tile.bindingProblems(
    'updater.EnableNotificationQueue(false);\n' +
    'var image = binding.SelectSingleNode("image") as XmlElement;\n' +
    'image.SetAttribute("src", "ms-appx:///Assets/TileIcon.png");');
  assert.ok(problems.some((p) => /EnableNotificationQueue\(false\)/.test(p)));
});

test('il servizio accende la coda, o la tile resta su un messaggio solo', () => {
  const source = fs.readFileSync(
    path.join(ROOT, 'WhatsappApp', 'Services', 'NotificationService.cs'), 'utf8');
  assert.ok(!tile.bindingProblems(source).some((p) => /EnableNotificationQueue/.test(p)),
    'nessuna regola della coda deve scattare sul servizio vero');
  assert.match(tile.stripComments(source), /EnableNotificationQueue\s*\(\s*true\s*\)/);
});

test('i byte del decoder senza misura si segnalano', () => {
  const problems = tile.decodeProblems(
    'var picture = new TilePicture();\n' +
    'picture.Bytes = data.DetachPixelData();\n' +
    'picture.Side = side;');
  assert.strictEqual(problems.length, 2);
  assert.ok(problems.some((p) => /never measured/.test(p)));
  assert.ok(problems.some((p) => /never scaled/.test(p)));
});

test('la decodifica vera misura il buffer e lo riporta alla misura del disegno', () => {
  const source = fs.readFileSync(
    path.join(ROOT, 'WhatsappApp', 'Services', 'TileImage.cs'), 'utf8');
  assert.deepStrictEqual(tile.decodeProblems(source), []);
});

test('una card scritta con il nome del chiamante e senza coda si segnala', () => {
  const problems = tile.writeProblems(
    'var f = await ApplicationData.Current.LocalFolder.CreateFileAsync(fileName, ' +
    'CreationCollisionOption.ReplaceExisting);');
  assert.ok(problems.some((p) => /TileCanvas.CardFileName/.test(p)),
    'un nome fisso e un URI che cambia contenuto');
  assert.ok(problems.some((p) => /TileCanvas.WideFileName/.test(p)));
  assert.ok(problems.some((p) => /not queued/.test(p)),
    'due aggiornamenti della stessa chat scrivono lo stesso file insieme');
  assert.ok(problems.some((p) => /caller's own name/.test(p)));
});

test('la scrittura vera della card porta la foto nel nome e un solo scrittore', () => {
  const source = fs.readFileSync(
    path.join(ROOT, 'WhatsappApp', 'Services', 'TileImage.cs'), 'utf8');
  assert.deepStrictEqual(tile.writeProblems(source), []);
});

test('la tile piccola con la card quadrata si segnala', () => {
  const problems = tile.smallProblems(
    'AddSmall(xml, visual, image ?? AppIcon);',
    'AddSmallBinding(xml, visual, entry.Image ?? TileIconUri);');
  assert.strictEqual(problems.length, 2);
  assert.ok(problems.some((p) => /card\.Small/.test(p)));
  assert.ok(problems.some((p) => /TileImage\.SmallOf/.test(p)));
});

test('i due servizi veri danno la card piccola al binding 71x71', () => {
  const root = path.join(ROOT, 'WhatsappApp', 'Services');
  const problems = tile.smallProblems(
    fs.readFileSync(path.join(root, 'PinnedTile.cs'), 'utf8'),
    fs.readFileSync(path.join(root, 'NotificationService.cs'), 'utf8'));
  assert.deepStrictEqual(problems, []);
});

test('la tile del contatto composta dalla preview si segnala', () => {
  // (il caso della preview, aggiornato alla card v5)
  const problems = tile.pictureProblems(
    'byte[] jpeg = await NativeConnection.Instance.RequestAvatarAsync(chatId, true);',
    'public const int CardVersion = 5;');
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /full-size picture/);
});

test('la card v4 che il telefono ha scritto grezza si segnala', () => {
  const problems = tile.pictureProblems(
    'RequestAvatarAsync(chatId, false)', 'public const int CardVersion = 4;');
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /below 5/);
  assert.match(problems[0], /raw picture/);
});

test('i file veri chiedono la foto grande e non riusano le card vecchie', () => {
  const root = path.join(ROOT, 'WhatsappApp');
  assert.deepStrictEqual(tile.pictureProblems(
    fs.readFileSync(path.join(root, 'Services', 'PinnedTile.cs'), 'utf8'),
    fs.readFileSync(path.join(root, 'Protocol', 'Media', 'TileCanvas.cs'), 'utf8')), []);
});

const ASKING =
  'public static void AskForPicture(string chatId, string name, int count, string body, bool countKnown)\n' +
  '{\n' +
  '    if (string.IsNullOrEmpty(chatId)) return;\n' +
  '\n' +
  '    if (!NativeConnection.Instance.IsLoggedIn) return;\n' +
  '\n' +
  '    Asking.Add(chatId);\n' +
  '}';

const STATUS =
  'private void OnNativeConnectionStatus(object sender, string status)\n' +
  '{\n' +
  '    if (_pinnedTilesAfterLogin || !NativeConnection.Instance.IsLoggedIn) return;\n' +
  '\n' +
  '    _pinnedTilesAfterLogin = true;\n' +
  '    PublishPinnedTiles();\n' +
  '}';

test('chiedere la foto grande solo a login fatto non e un problema', () => {
  assert.deepStrictEqual(tile.loginProblems(ASKING, STATUS), []);
});

test('una foto chiesta prima del login si segnala', () => {
  const found = tile.loginProblems(
    ASKING.replace('if (!NativeConnection.Instance.IsLoggedIn) return;', ''), STATUS).join('\n');
  assert.match(found, /IsLoggedIn/);
});

test('una scansione delle tile che non parte dopo il login si segnala', () => {
  const found = tile.loginProblems(ASKING, STATUS.replace('PublishPinnedTiles();', '')).join('\n');
  assert.match(found, /publishes the pinned tiles/);
});

test('i due servizi veri aspettano il login e poi chiedono la foto', () => {
  const root = path.join(ROOT, 'WhatsappApp', 'Services');
  assert.deepStrictEqual(tile.loginProblems(
    fs.readFileSync(path.join(root, 'PinnedTile.cs'), 'utf8'),
    fs.readFileSync(path.join(root, 'DataService.cs'), 'utf8')), []);
});

test('una card delle regole vecchie si segnala', () => {
  const problems = tile.pictureProblems(
    'RequestAvatarAsync(chatId, false)', 'public const int CardVersion = 3;');
  assert.ok(problems.some((p) => /CardVersion is below 5/.test(p)));
});

test('una decodifica che chiede al decoder il ritaglio scalato si segnala', () => {
  const problems = tile.requestProblems(
    'var transform = new BitmapTransform();\n' +
    'transform.Bounds = bounds;\n' +
    'transform.ScaledWidth = (uint)side;\n' +
    'transform.ScaledHeight = (uint)side;\n' +
    'transform.InterpolationMode = BitmapInterpolationMode.Fant;\n' +
    'var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, ' +
    'BitmapAlphaMode.Straight, transform);');
  assert.ok(problems.some((p) => /scaled crop/.test(p)),
    'la foto grande ha risposto E_INVALIDARG alla misura scalata');
  assert.ok(problems.some((p) => /interpolation mode/.test(p)));
  assert.ok(problems.some((p) => /CentreSquarePixels/.test(p)),
    'senza il secondo modo, il rifiuto del ritaglio lascia la foto grezza');
});

test('la decodifica vera chiede il solo ritaglio e sa tagliare il quadrato', () => {
  const source = fs.readFileSync(
    path.join(ROOT, 'WhatsappApp', 'Services', 'TileImage.cs'), 'utf8');
  assert.deepStrictEqual(tile.requestProblems(source), []);
});

test('un ripiego scritto con il nome della card si segnala', () => {
  const problems = tile.fallbackProblems(
    'private static async Task<TileCard> RawAsync(string fileName, string avatarData)\n' +
    '{\n' +
    '    string name = TileCanvas.CardFileName(fileName, avatarData);\n' +
    '}');
  assert.ok(problems.some((p) => /card name/.test(p)),
    'una card che contiene la foto grezza non si compone piu');
  assert.ok(problems.some((p) => /no name of its own/.test(p)));
});

test('il ripiego vero ha un nome suo', () => {
  const source = fs.readFileSync(
    path.join(ROOT, 'WhatsappApp', 'Services', 'TileImage.cs'), 'utf8');
  assert.deepStrictEqual(tile.fallbackProblems(source), []);
});

test('il corpo di un metodo si ferma alla sua graffa', () => {
  const source = 'private static int A()\n        {\n            return B();\n        }\n' +
    'private static int B()\n        {\n            return 2;\n        }';
  const body = tile.methodBody(source, 'B');
  assert.match(body, /private static int B/);
  assert.match(body, /return 2/);
  assert.match(tile.methodBody(source, 'A'), /return B\(\)/);
  assert.doesNotMatch(tile.methodBody(source, 'A'), /return 2/);
});

test('il modello largo che WP8.1 non ha si segnala', () => {
  const problems = tile.bindingProblems(
    'var t = TileTemplateType.TileWide310x150IconWithBadge;');
  assert.ok(problems.some((p) => /TileWide310x150IconWithBadge/.test(p)));
});
