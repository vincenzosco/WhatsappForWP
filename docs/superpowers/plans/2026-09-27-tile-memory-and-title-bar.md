# Tile, 512 MB and title bar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** make the live tile actually draw the app icon next to the badge, keep the app inside the memory budget of a 512 MB phone, and make the two title-bar actions impossible to invert.

**Architecture:** three independent faults, three independent fixes. The tile payload never carried the image the `IconWithBadge` template requires, so the tile rendered without an icon and without any error — the fix is a dedicated unpadded asset plus a guard that reads the C# and the PNG. The 512 MB work is runtime, not manifest: WP8.1 has no memory declaration in its schema, so it is avatars decoded at their display size plus a `MemoryWatcher` that releases the decoded bitmaps when `MemoryManager` says the app is under pressure. The title bar was verified correct in the source and in the compiled output, so the third fix is a guard that makes an inverted pairing impossible to ship plus a naming that answers on the device.

**Tech Stack:** C# 5 / XAML on Windows Phone 8.1 (WinRT), `Windows.UI.Notifications`, `Windows.System.MemoryManager`, Node.js guards in `tools/` (`node:test` for the tool tests), ImageMagick 7 for the artwork.

## Global Constraints

- Windows Phone 8.1 **XAML (WinRT)** app. **C# 5 only** — no `nameof`, no string interpolation, no expression-bodied members. Guard: `node tools/check-csharp5.js`.
- Icons are inline vector `Path` geometries preceded by a `<!-- IconX -->` comment; no `Segoe MDL2 Assets`; a `Geometry` must not travel through a `ResourceDictionary`; figures in element form only. Guard: `node tools/check-icons.js`.
- Assets live in `WhatsappApp/Assets/` as `Name.scale-240.png` at the **exact** 240 % dimensions the manifest asks for, and every one of them is listed in `WhatsappApp.csproj` as `<Content Include="Assets\Name.png" />` (the manifest path drops the qualifier, MRT adds it back).
- Tile and toast images: `.png`/`.jpg`/`.jpeg`/`.gif`, **≤ 1024×1024 px and ≤ 200 KB**. The iconic template wants an **unpadded, transparent** icon, **at least 200×200** ("create a 200x200 pixel icon, which works for both small and medium tiles... No padding is needed on these assets", Special tile templates).
- `TileWide310x150IconWithBadge` **does not exist on WP8.1** (`CS0117`, verified by build): the wide tile keeps the manifest default. `TileSquare150x150IconWithBadge` and `TileSquare71x71IconWithBadge` do exist.
- **No manifest declaration exists for memory** on WP8.1: `AppxManifestSchema2010_v2.xsd`, `AppxManifestSchema2013.xsd` and `AppxManifestSchema.xsd` have no memory element or attribute (`findstr /i memory` is empty). The 512 MB work is runtime, through `Windows.System.MemoryManager` (`AppMemoryUsageIncreased` and `AppMemoryUsageLevel` are present in the WP8.1 reference `Windows.winmd`).
- No new capability in `Package.appxmanifest`.
- Every user-visible string goes in **both** `.resw` files (guard: `node tools/check-resw.js --strict`); the docs come in pairs, `README.md` + `README.it.md` (guard: `node tools/check-docs.js`).
- Fast gate, all of it green before every commit:

  ```bash
  node tools/check-csharp5.js && node tools/check-icons.js \
    && node tools/check-resw.js --strict && node tools/check-docs.js \
    && node tools/check-framing.js && node --test "tools/test/**/*.test.js"
  cd WhatsappBridge && npm test
  ```

  (`node --test "tools/test/**/*.test.js"` — the quoted glob is required, plain `node --test tools/test` does not work on this Node build.) The C# gate is the build on Windows.

---

### Task 1: the live tile draws the app icon, and a guard keeps it that way

**Files:**
- Create: `tools/check-tile.js`
- Create: `tools/test/check-tile.test.js`
- Modify: `tools/make-brand-assets.js`
- Create (generated): `WhatsappApp/Assets/TileIcon.png`, `WhatsappApp/Assets/TileIcon.scale-240.png`
- Modify: `WhatsappApp/WhatsappApp.csproj` (the two new `<Content Include>` lines, after line 149)
- Modify: `WhatsappApp/Services/NotificationService.cs` (`SetTileBadge` and `AppendBinding`)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `TileIconUri = "ms-appx:///Assets/TileIcon.png"` (the image every tile binding carries) and, for the tests, `module.exports = { pngSize, declaredImages, bindingProblems, assetProblems }` from `tools/check-tile.js`.

**Why the tile was blank.** `TileSquare150x150IconWithBadge` does **not** take the icon from the manifest: the payload must carry `<image id="1" src="..."/>` pointing at a dedicated iconic asset. `SetTileBadge` sent the template untouched, so the binding's image had an empty `src` and the tile drew no icon — silently, because an empty `src` is not an error. The comment that used to sit on that method ("senza di essa il modello usa il logo dell'app") was wrong.

- [ ] **Step 1: Write the failing test**

Create `tools/test/check-tile.test.js`:

```js
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

test('il modello largo che WP8.1 non ha si segnala', () => {
  const problems = tile.bindingProblems(
    'var t = TileTemplateType.TileWide310x150IconWithBadge;');
  assert.ok(problems.some((p) => /TileWide310x150IconWithBadge/.test(p)));
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `node --test "tools/test/**/*.test.js" 2>&1 | grep -A3 check-tile`
Expected: FAIL — `Cannot find module '../check-tile'`.

- [ ] **Step 3: Write the guard**

Create `tools/check-tile.js`:

```js
#!/usr/bin/env node
/**
 * tools/check-tile.js
 *
 * Guard for the live tile of the WP8.1 app.
 *
 * Perche' esiste: la tile non disegnava l'icona e non lo diceva a nessuno. Il
 * modello TileSquare150x150IconWithBadge NON prende l'icona dal manifest: vuole
 * un <image src="..."> nel payload, che punti a un'icona dedicata (Special tile
 * templates, passo 3). Con src vuoto la tile resta senza icona e non solleva
 * nessuna eccezione: un guasto che nessun log mostra.
 *
 * Regole:
 *  1. il codice della tile deve impostare src su ogni binding che manda;
 *  2. l'immagine referenziata deve esistere in Assets/, essere un PNG dentro i
 *     limiti che il sistema accetta (<=1024x1024 px, <=200 KB) e avere almeno
 *     la misura minima dell'icona (200x200);
 *  3. il modello TileWide310x150IconWithBadge non esiste su WP8.1: se compare,
 *     e' un errore di compilazione che vale la pena spiegare qui.
 *
 * Usage:
 *   node tools/check-tile.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const SERVICE = path.join(ROOT, 'WhatsappApp', 'Services', 'NotificationService.cs');
const ASSETS_DIR = path.join(ROOT, 'WhatsappApp', 'Assets');

const IMAGE_PREFIX = 'ms-appx:///Assets/';
const MIN_SIDE = 200;
const MAX_SIDE = 1024;
const MAX_BYTES = 200 * 1024;
const ABSENT_ON_WP81 = 'TileWide310x150IconWithBadge';

/** Dimensioni dal chunk IHDR del PNG. null se i byte non sono un PNG. */
function pngSize(bytes) {
  if (!bytes || bytes.length < 24) return null;
  if (bytes[0] !== 0x89 || bytes[1] !== 0x50 || bytes[2] !== 0x4e || bytes[3] !== 0x47) return null;
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

/** Le immagini che la tile dichiara, come percorsi di pacchetto. */
function declaredImages(source) {
  const found = [];
  const pattern = new RegExp('"(' + IMAGE_PREFIX.replace(/[/.]/g, '\\$&') + '[^"]+)"', 'g');
  for (const m of source.matchAll(pattern)) found.push(m[1]);
  return found;
}

/**
 * Problemi del payload. Ogni image presa da un modello deve ricevere il suo
 * src: un'image con src vuoto e' una tile senza icona, e nessuno lo dice.
 */
function bindingProblems(source) {
  const problems = [];

  if (source.indexOf(ABSENT_ON_WP81) >= 0) {
    problems.push(`NotificationService.cs: ${ABSENT_ON_WP81} non esiste su WP8.1 ` +
      '(CS0117): la tile larga resta quella del manifest');
  }

  const images = (source.match(/SelectSingleNode\([^;]*image"/g) || []).length;
  const srcs = (source.match(/SetAttribute\("src"/g) || []).length;

  if (srcs === 0) {
    problems.push('NotificationService.cs: no binding sets src: the iconic template does ' +
      'not take the icon from the manifest, so the tile renders without one and without ' +
      'an exception');
  } else if (images !== srcs) {
    problems.push(`NotificationService.cs: ${images} image(s) read from a template, ` +
      `${srcs} src set: an image that goes into the payload needs its own src`);
  }

  if (srcs > 0 && declaredImages(source).length === 0) {
    problems.push(`NotificationService.cs: the tile references no image under ${IMAGE_PREFIX}`);
  }

  return problems;
}

/** Problemi di un asset: misura minima, limiti del sistema, esistenza. */
function assetProblems(asset) {
  const problems = [];
  const where = 'WhatsappApp/' + asset.name;
  if (!asset.exists) {
    problems.push(`${where}: does not exist`);
    return problems;
  }
  if (asset.width == null) {
    problems.push(`${where}: not a PNG`);
    return problems;
  }
  if (asset.width < MIN_SIDE || asset.height < MIN_SIDE) {
    problems.push(`${where}: ${asset.width}x${asset.height}, the iconic template needs at ` +
      `least ${MIN_SIDE}x${MIN_SIDE}`);
  }
  if (asset.width > MAX_SIDE || asset.height > MAX_SIDE) {
    problems.push(`${where}: ${asset.width}x${asset.height} is over the ${MAX_SIDE}x${MAX_SIDE} ` +
      'the system accepts for a tile image');
  }
  if (asset.bytes > MAX_BYTES) {
    problems.push(`${where}: ${Math.round(asset.bytes / 1024)} KB is over the 200 KB the ` +
      'system accepts for a tile image');
  }
  return problems;
}

/** Legge un asset dichiarato dalla tile e ne descrive la misura. */
function readAsset(packagePath) {
  const name = packagePath.slice(IMAGE_PREFIX.length).replace(/\//g, path.sep);
  const file = path.join(ASSETS_DIR, name);
  const exists = fs.existsSync(file);
  if (!exists) return { name, exists: false, width: null, height: null, bytes: 0 };
  const bytes = fs.readFileSync(file);
  const size = pngSize(bytes);
  return {
    name,
    exists: true,
    width: size ? size.width : null,
    height: size ? size.height : null,
    bytes: bytes.length
  };
}

function main() {
  const source = fs.readFileSync(SERVICE, 'utf8');
  const problems = bindingProblems(source);

  const images = declaredImages(source);
  if (images.length === 0) {
    problems.push(`NotificationService.cs: the tile declares no image under ${IMAGE_PREFIX}`);
  }

  for (const image of images) {
    problems.push(...assetProblems(readAsset(image)));
    // La sorgente dichiara il percorso senza qualificatore ("Assets\TileIcon.png"):
    // MRT aggiunge .scale-240 in fase di risoluzione, quindi il file qualificato
    // deve esserci, altrimenti sul telefono l'immagine non si trova.
    const qualified = image.replace(/\.png$/, '.scale-240.png');
    if (qualified !== image) problems.push(...assetProblems(readAsset(qualified)));
  }

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} tile problem(s).`);
    process.exit(1);
  }

  const first = readAsset(images[0]);
  console.log(`OK: ${images[0]} (${first.width}x${first.height}, ` +
    `${Math.round(first.bytes / 1024)} KB), src on every binding.`);
}

if (require.main === module) main();

module.exports = { pngSize, declaredImages, bindingProblems, assetProblems };
```

- [ ] **Step 4: Run the guard against the current service**

Run: `node tools/check-tile.js`
Expected: FAIL with `NotificationService.cs: no binding sets src: the iconic template does not take the icon from the manifest, so the tile renders without one and without an exception`. That is the tile bug, written down.

- [ ] **Step 5: Generate the iconic asset**

In `tools/make-brand-assets.js`, inside `main()`, right after `const glyph = buildGlyph();`, add:

```js
  // L'icona della tile (modello IconWithBadge): il marchio bianco su sfondo
  // trasparente e SENZA padding, alla misura che il modello chiede (almeno
  // 200x200) e alla sua versione a 240% per il telefono. Non e' un logo da
  // manifest: quella e' l'icona con il padding intorno, e su una tile larga
  // 150 px il padding si mangia il disegno.
  const iconSizes = [['TileIcon.png', 200], ['TileIcon.scale-240.png', 480]];
  for (const [name, size] of iconSizes) {
    magick([glyph, '-resize', `${size}x${size}`, '-depth', '8',
      path.join(OUT_DIR, name)]);
  }
```

Then, in `WhatsappApp/WhatsappApp.csproj`, after line 149 (`<Content Include="Assets\WideLogo.scale-240.png" />`), add:

```xml
    <Content Include="Assets\TileIcon.png" />
    <Content Include="Assets\TileIcon.scale-240.png" />
```

Run: `node tools/make-brand-assets.js --preview`
Expected: the file list includes `TileIcon.png` and `TileIcon.scale-240.png`, and the ASCII preview shows the white bubble with the handset cut out, filling the frame.

- [ ] **Step 6: Put the icon in the payload**

In `WhatsappApp/Services/NotificationService.cs`, add the constant and the two helpers, and rewrite `SetTileBadge`:

```csharp
        /// <summary>
        /// L'immagine della tile. Il modello IconWithBadge NON prende l'icona dal
        /// manifest: la vuole nel payload, quindi va scritta sull'image di ogni
        /// binding che parte. E' un PNG trasparente senza padding, alla misura che
        /// il modello chiede.
        /// </summary>
        private const string TileIconUri = "ms-appx:///Assets/TileIcon.png";

        /// <summary>
        /// Mette l'icona dell'app sulla tile, nelle due misure che WP8.1 sa
        /// aggiornare con un'icona: 150x150 e 71x71 (il modello IconWithBadge).
        ///
        /// Da notare, perche' e' il punto: **il numero lo disegna il badge, non
        /// la tile.** Questo aggiornamento serve a dare alla tile l'icona giusta
        /// mentre il badge e' attivo, e a riportarla a quella del manifest quando
        /// non c'e' piu' niente da leggere (Clear).
        ///
        /// La misura larga non si tocca: su WP8.1 il modello
        /// `TileWide310x150IconWithBadge` non esiste, e comunque il badge viene
        /// disegnato anche sulla tile larga, quindi il numero si vede lo stesso.
        /// </summary>
        private static void SetTileBadge(int count)
        {
            try
            {
                var updater = TileUpdateManager.CreateTileUpdaterForApplication();
                if (count <= 0)
                {
                    updater.Clear();
                    return;
                }

                var xml = TileUpdateManager.GetTemplateContent(
                    TileTemplateType.TileSquare150x150IconWithBadge);
                var visual = (XmlElement)xml.SelectSingleNode("/tile/visual");
                if (visual == null) return;

                SetTileIcon(xml, visual);
                AppendBinding(xml, visual, TileTemplateType.TileSquare71x71IconWithBadge);

                updater.Update(new TileNotification(xml));
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.SetTileBadge", ex);
            }
        }

        /// <summary>L'icona sul binding che sta dentro il visual del modello.</summary>
        private static void SetTileIcon(XmlDocument xml, XmlElement visual)
        {
            var binding = visual.SelectSingleNode("binding") as XmlElement;
            if (binding == null) return;
            SetImage(xml, binding);
        }

        /// <summary>
        /// Scrive l'icona sull'image del binding, creandola se il modello non ne
        /// ha una: creare con il documento di destinazione, non con quello del
        /// modello, e' l'unica forma che non solleva un'eccezione.
        /// </summary>
        private static void SetImage(XmlDocument xml, XmlElement binding)
        {
            var image = binding.SelectSingleNode("image") as XmlElement;
            if (image == null)
            {
                image = xml.CreateElement("image");
                image.SetAttribute("id", "1");
                binding.AppendChild(image);
            }
            image.SetAttribute("src", TileIconUri);
        }

        /// <summary>
        /// Copia il binding di un altro modello dentro il documento della tile.
        /// Un nodo appartiene al suo documento, quindi va importato: appenderlo
        /// cosi' com'e' solleva un'eccezione. Il binding importato e' un binding
        /// che parte, quindi vuole la sua icona come l'altro.
        /// </summary>
        private static void AppendBinding(XmlDocument xml, XmlElement visual, TileTemplateType template)
        {
            var other = TileUpdateManager.GetTemplateContent(template);
            var binding = other.SelectSingleNode("/tile/visual/binding");
            if (binding == null) return;

            var imported = xml.ImportNode(binding, true) as XmlElement;
            if (imported == null) return;

            SetImage(xml, imported);
            visual.AppendChild(imported);
        }
```

One method for both call sites, and it **creates** the `image` when the template does not ship one: whether `GetTemplateContent` returns an `image` node for the iconic templates is not something this side of the wire can be sure of, and the guard counts images against `src` assignments, so a silent no-op would be invisible.

- [ ] **Step 7: Run the guard and the tool tests**

Run: `node tools/check-tile.js && node --test "tools/test/**/*.test.js"`
Expected: `OK: ms-appx:///Assets/TileIcon.png (200x200, N KB), src on every binding.` and all tool tests passing. (The guard checks the `TileIcon.scale-240.png` sibling too — the asset the phone actually resolves — while the line quotes the unqualified one.)

- [ ] **Step 8: Build on Windows**

Run (on the VM, as always): copy the repo to `C:\Temp\wp81` and `msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86`
Expected: `0 Error(s)`, `Your package has been successfully created`.

- [ ] **Step 9: Commit**

```bash
git add tools/check-tile.js tools/test/check-tile.test.js tools/make-brand-assets.js \
  WhatsappApp/Assets/TileIcon.png WhatsappApp/Assets/TileIcon.scale-240.png \
  WhatsappApp/WhatsappApp.csproj WhatsappApp/Services/NotificationService.cs
git commit -m "fix: give the live tile the icon its template requires"
```

---

### Task 2: avatars decode at the size they are shown

**Files:**
- Create: `tools/check-memory.js`
- Create: `tools/test/check-memory.test.js`
- Modify: `WhatsappApp/Services/ImageHelper.cs`
- Modify: `WhatsappApp/Models/Contact.cs:89-101`
- Modify: `WhatsappApp/Models/ChatMessage.cs:333`
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs:326`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `ImageHelper.FromBase64Async(string base64, int decodePixelWidth)`, `ImageHelper.FromBytesAsync(byte[] bytes, int decodePixelWidth)` — two arguments, no one-argument overloads; `Contact.AvatarDecodePixels = 52`.

**Why this is the 512 MB work.** `BitmapImage` decodes at the file's own size unless `DecodePixelWidth` says otherwise. A WhatsApp profile picture arrives at 640×640 or so: at full decode that is ~1.6 MB of bitmap **each**, and the chat list holds one per conversation. Twenty-five conversations is tens of megabytes of pixels to draw a 52 px circle. The same argument applies to an image inside a bubble and to the attachment preview. Setting `DecodePixelWidth` costs one line per call site and is the difference between fitting on a 512 MB phone and not.

- [ ] **Step 1: Write the failing test**

Create `tools/test/check-memory.test.js`:

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert');

const memory = require('../check-memory');

const FILE = 'WhatsappApp/Models/Contact.cs';

test('una chiamata senza misura di decodifica si segnala', () => {
  const problems = memory.decodeProblems(
    'Avatar = await ImageHelper.FromBase64Async(_avatarData);', FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /two arguments/);
});

test('una chiamata con la sua misura passa', () => {
  const problems = memory.decodeProblems(
    'Avatar = await ImageHelper.FromBase64Async(_avatarData, AvatarDecodePixels);', FILE);
  assert.deepStrictEqual(problems, []);
});

test('una misura oltre lo schermo si segnala', () => {
  const problems = memory.decodeProblems(
    'var b = await ImageHelper.FromBase64Async(x, 4096);', FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /wider than 720/);
});

test('una chiamata su piu righe si legge lo stesso', () => {
  const source = 'var b = await ImageHelper.FromBytesAsync(\n    bytes,\n    320);';
  assert.deepStrictEqual(memory.decodeProblems(source, FILE), []);
});

test('DecodePixelWidth viene assegnato prima del SetSource', () => {
  const good = 'var bitmap = new BitmapImage();\n' +
    'if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;\n' +
    'await bitmap.SetSourceAsync(stream);';
  assert.deepStrictEqual(memory.sourceShapeProblems(good, 'WhatsappApp/Services/ImageHelper.cs'), []);

  const bad = 'var bitmap = new BitmapImage();\nawait bitmap.SetSourceAsync(stream);';
  const problems = memory.sourceShapeProblems(bad, 'WhatsappApp/Services/ImageHelper.cs');
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /DecodePixelWidth/);
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `node --test "tools/test/**/*.test.js" 2>&1 | grep -A3 check-memory`
Expected: FAIL — `Cannot find module '../check-memory'`.

- [ ] **Step 3: Write the guard**

Create `tools/check-memory.js`:

```js
#!/usr/bin/env node
/**
 * tools/check-memory.js
 *
 * Guard for the memory budget of a 512 MB phone.
 *
 * Perche' esiste: BitmapImage decodifica alla misura del file. Un'immagine del
 * profilo da 640x640 sono ~1,6 MB di pixel per disegnare un cerchio da 52 px, e
 * l'elenco chat ne tiene una per conversazione. Non c'e' nessuna eccezione e
 * nessun log: si vede solo un telefono che chiude l'app. Su WP8.1 non esiste una
 * dichiarazione di memoria nel manifest (lo schema non la prevede), quindi la
 * difesa e' questa: nessuna bitmap decodificata piu' grande di quanto viene
 * mostrata.
 *
 * Regole:
 *  1. ogni chiamata a ImageHelper.From*Async passa la misura di decodifica;
 *  2. nessuna misura di decodifica supera la larghezza dello schermo (720 copre
 *     un 480 px a 1,5x);
 *  3. ImageHelper imposta DecodePixelWidth prima di SetSourceAsync (dopo non ha
 *     effetto).
 *
 * Usage:
 *   node tools/check-memory.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');
const HELPER = 'WhatsappApp/Services/ImageHelper.cs';

const CALL = /ImageHelper\.From(Base64|Bytes)Async\(/;
const MAX_DECODE = 720;

/** Il testo fra la parentesi aperta a `open` e la sua chiusa. */
function argumentsOf(source, open) {
  let depth = 0;
  for (let i = open; i < source.length; i++) {
    const ch = source[i];
    if (ch === '(') depth++;
    else if (ch === ')') {
      depth--;
      if (depth === 0) return source.slice(open + 1, i);
    }
  }
  return null;
}

/** Divide gli argomenti di una chiamata sulle virgole di primo livello. */
function splitArguments(text) {
  const parts = [];
  let depth = 0;
  let current = '';
  for (const ch of text) {
    if (ch === '(' || ch === '<') depth++;
    else if (ch === ')' || ch === '>') depth--;
    if (ch === ',' && depth === 0) {
      parts.push(current.trim());
      current = '';
      continue;
    }
    current += ch;
  }
  if (current.trim() !== '') parts.push(current.trim());
  return parts;
}

/** Problemi delle chiamate di decodifica in un sorgente. */
function decodeProblems(source, file) {
  const problems = [];
  for (const match of source.matchAll(new RegExp(CALL.source, 'g'))) {
    const open = match.index + match[0].length - 1;
    const args = argumentsOf(source, open);
    if (args === null) continue;
    const parts = splitArguments(args);
    if (parts.length !== 2) {
      problems.push(`${file}: ImageHelper.From${match[1]}Async takes two arguments ` +
        '(the encoded image and the width it is shown at): without the second one the ' +
        'bitmap is decoded at the size of the file');
      continue;
    }
    const width = parts[1];
    if (/^\d+$/.test(width) && Number(width) > MAX_DECODE) {
      problems.push(`${file}: decodes at ${width}, wider than ${MAX_DECODE}: the image is ` +
        'shown on a 480 px screen, so the extra pixels are only memory');
    }
  }
  return problems;
}

/** Problemi della forma di ImageHelper: l'ordine delle due chiamate conta. */
function sourceShapeProblems(source, file) {
  const problems = [];
  if (source.indexOf('BitmapImage') < 0) return problems;
  if (source.indexOf('DecodePixelWidth') < 0) {
    problems.push(`${file}: creates a BitmapImage without a DecodePixelWidth: it will be ` +
      'decoded at the size of the file');
    return problems;
  }
  const decode = source.indexOf('DecodePixelWidth');
  const setSource = source.indexOf('SetSourceAsync');
  if (setSource >= 0 && decode > setSource) {
    problems.push(`${file}: DecodePixelWidth is set after SetSourceAsync, where it has no effect`);
  }
  return problems;
}

function walk(dir, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'obj' || entry.name === 'bin') continue;
      walk(path.join(dir, entry.name), out);
    } else if (entry.name.endsWith('.cs')) {
      out.push(path.join(dir, entry.name));
    }
  }
  return out;
}

function main() {
  const problems = [];
  for (const file of walk(APP, [])) {
    const rel = path.relative(ROOT, file).replace(/\\/g, '/');
    const source = fs.readFileSync(file, 'utf8');
    problems.push(...decodeProblems(source, rel));
    if (rel === HELPER) problems.push(...sourceShapeProblems(source, rel));
  }

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} memory problem(s).`);
    process.exit(1);
  }
  console.log('OK: every decoded bitmap asks for the width it is shown at.');
}

if (require.main === module) main();

module.exports = { decodeProblems, sourceShapeProblems, splitArguments, MAX_DECODE };
```

- [ ] **Step 4: Run the guard against the current app**

Run: `node tools/check-memory.js`
Expected: FAIL — three lines, one per call site (`Contact.cs`, `ChatMessage.cs`, `ChatPage.xaml.cs`), each saying the call takes two arguments, plus the `ImageHelper.cs` line for the missing `DecodePixelWidth`.

- [ ] **Step 5: Decode at the shown size**

In `WhatsappApp/Services/ImageHelper.cs`, replace both methods:

```csharp
        /// <summary>
        /// base64 -> bitmap, decodificata a `decodePixelWidth` px di larghezza.
        /// </summary>
        public static async Task<BitmapImage> FromBase64Async(string base64, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            return await FromBytesAsync(Convert.FromBase64String(base64), decodePixelWidth);
        }

        /// <summary>
        /// bytes -> bitmap. `decodePixelWidth` e' la larghezza a cui l'immagine
        /// viene mostrata: BitmapImage decodifica alla misura chiesta invece che a
        /// quella del file. Un'immagine del profilo da 640x640 decodificata a 52
        /// pesa qualche decina di KB invece di un paio di MB, e su un telefono da
        /// 512 MB con venticinque conversazioni la differenza si vede.
        ///
        /// Va impostato PRIMA di SetSourceAsync: dopo la decodifica non ha piu'
        /// effetto.
        /// </summary>
        public static async Task<BitmapImage> FromBytesAsync(byte[] bytes, int decodePixelWidth)
        {
            if (bytes == null || bytes.Length == 0) return null;

            using (var stream = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                }

                var bitmap = new BitmapImage();
                if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
        }
```

In `WhatsappApp/Models/Contact.cs`, add the constant above `LoadAvatarAsync` and pass it:

```csharp
        /// <summary>
        /// La misura a cui la XAML disegna l'avatar: il riquadro della riga e' un
        /// quadrato da 52 px. Decodificare piu' grande e' memoria buttata.
        /// </summary>
        public const int AvatarDecodePixels = 52;
```

```csharp
                Avatar = await ImageHelper.FromBase64Async(_avatarData, AvatarDecodePixels);
```

In `WhatsappApp/Models/ChatMessage.cs`, above the line that fills `MediaImage`, add a constant to the class and pass it:

```csharp
        /// <summary>
        /// Un'immagine dentro un fumetto: la bolla e' larga al massimo ~250 px,
        /// quindi 320 copre anche i margini.
        /// </summary>
        private const int MediaDecodePixels = 320;
```

```csharp
                MediaImage = await ImageHelper.FromBase64Async(MediaData, MediaDecodePixels);
```

In `WhatsappApp/Pages/ChatPage.xaml.cs` (line 326), add a constant to the class and pass it:

```csharp
        /// <summary>
        /// L'anteprima dell'immagine da mandare: la pagina e' larga 480 px, quindi
        /// 720 la copre anche a 1,5x senza decodificare il file intero.
        /// </summary>
        private const int PreviewDecodePixels = 720;
```

```csharp
            SelectedImagePreview.Source = await ImageHelper.FromBase64Async(base64, PreviewDecodePixels);
```

Note: `WhatsappApp/Pages/ConnectionPage.xaml.cs:477` has its own `BitmapFromBase64Async` and **stays as it is**: that one decodes the WhatsApp login QR, which only scans at 1:1 pixels, and it is a single image.

- [ ] **Step 6: Run the guard**

Run: `node tools/check-memory.js && node --test "tools/test/**/*.test.js"`
Expected: `OK: every decoded bitmap asks for the width it is shown at.` and all tool tests passing.

- [ ] **Step 7: Build on Windows**

Expected: `0 Error(s)`.

- [ ] **Step 8: Commit**

```bash
git add tools/check-memory.js tools/test/check-memory.test.js WhatsappApp/Services/ImageHelper.cs \
  WhatsappApp/Models/Contact.cs WhatsappApp/Models/ChatMessage.cs WhatsappApp/Pages/ChatPage.xaml.cs
git commit -m "perf: decode every bitmap at the size it is drawn"
```

---

### Task 3: the app lets go of memory when the phone asks

**Files:**
- Create: `WhatsappApp/Services/MemoryWatcher.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj` (add the new file next to the other services)
- Modify: `WhatsappApp/App.xaml.cs:206-215` (`StartServicesOnce`)
- Modify: `WhatsappApp/Services/DataService.cs` (add `TrimForMemory`)
- Modify: `WhatsappApp/Models/Contact.cs` (the pressure gate in `LoadAvatarAsync`)
- Modify: `WhatsappApp/Models/ChatMessage.cs` (the pressure gate in the media decode)
- Modify: `WhatsappBridge/test/config.test.js` (the adapter-side budget)

**Interfaces:**
- Consumes: `ImageHelper.FromBytesAsync(byte[], int)` and `Contact.AvatarDecodePixels` from Task 2.
- Produces: `MemoryWatcher.Instance.Start()`, `MemoryWatcher.Instance.IsUnderPressure`, `DataService.Instance.TrimForMemory()`.

**Why this exists and what it does.** WP8.1 makes no promise about a 512 MB phone: the process gets a hard limit (roughly 185 MB there, 380 MB on a 1 GB device) and the OS suspends or terminates an app that keeps growing. `Windows.System.MemoryManager` is the documented way to hear about it: `AppMemoryUsageLimitChanging` and `AppMemoryUsageIncreased` are the events (the first is Windows 10 1607 and later — on WP8.1 only the second exists), `AppMemoryUsageLevel` is the state, and the guidance is to free what you can when the level reaches `High`. What is heavy in this app is one thing: decoded avatars. So the pressure path drops them, and the app falls back to the initials it already draws.

- [ ] **Step 1: Write the failing test (adapter side)**

`WhatsappBridge/test/config.test.js` — add, after the history-limit test:

```js
test('i default dell adapter stanno nel budget di un telefono da 512 MB', () => {
  // Su un telefono da 512 MB l'app riceve ogni conversazione come un frame e
  // ne decodifica l'immagine: il tetto e' quante righe l'elenco puo' mostrare,
  // non quante il server saprebbe mandarne.
  const defaults = loadConfig({});
  assert.ok(defaults.chats.limit <= 30, `CHATS_LIMIT=${defaults.chats.limit}`);
  assert.ok(defaults.messages.limit <= 60, `MESSAGES_LIMIT=${defaults.messages.limit}`);
});
```

- [ ] **Step 2: Run it**

Run: `cd WhatsappBridge && npm test 2>&1 | grep -B2 -A6 "512 MB"`
Expected: PASS (25 ≤ 30 and 50 ≤ 60) — this test is a **ratchet**, not a bug: it is what stops someone raising a default later without thinking about the phone.

- [ ] **Step 3: Write the watcher**

Create `WhatsappApp/Services/MemoryWatcher.cs`:

```csharp
using System;
using Windows.System;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Il budget di memoria di questo telefono, e cosa l'app fa quando lo
    /// avvicina.
    ///
    /// Perche' esiste: su WP8.1 la memoria non si dichiara da nessuna parte - lo
    /// schema della manifest non la prevede - e non si vede. Il sistema avvisa e
    /// poi sospende o termina; il modo documentato di ascoltarlo e'
    /// `MemoryManager`. La chiamata utile qui e' `AppMemoryUsageIncreased`: quando
    /// il livello passa a High o OverLimit l'app deve liberare quello che puo'
    /// subito.
    ///
    /// Cosa libera: le bitmap degli avatar decodificate (la cosa pesante: una per
    /// conversazione), e la cronologia delle chat che nessuno sta leggendo. Non
    /// libera quello che l'utente sta guardando: la chat aperta resta intera.
    ///
    /// Finche' la pressione c'e', non si decodifica niente di nuovo: senza questo
    /// il primo frame di conversazioni rimette dentro tutto quello appena buttato.
    /// Quando il livello riscende, si riprende a decodificare.
    /// </summary>
    public sealed class MemoryWatcher
    {
        private static readonly MemoryWatcher InstanceHolder = new MemoryWatcher();

        private bool _hooked;
        private bool _underPressure;

        public static MemoryWatcher Instance
        {
            get { return InstanceHolder; }
        }

        /// <summary>Vero finche' il livello di memoria e' High o OverLimit.</summary>
        public bool IsUnderPressure
        {
            get { return _underPressure; }
        }

        /// <summary>
        /// Una volta per processo. Chiamato da App.StartServicesOnce: un app
        /// avviata da una condivisione non passa da OnLaunched, e un secondo
        /// aggancio raddoppierebbe i gestori. Ogni chiamata e' protetta: un
        /// telefono che rifiuta l'evento non deve far cadere l'avvio dell'app.
        /// </summary>
        public void Start()
        {
            if (_hooked) return;
            _hooked = true;

            try
            {
                MemoryManager.AppMemoryUsageIncreased += OnUsageIncreased;
                MemoryManager.AppMemoryUsageDecreased += OnUsageDecreased;

                // Il limite del telefono, una volta sola nel log: e' l'unico posto
                // dove si distingue un dispositivo da 512 MB da uno da 1 GB.
                Diag.Ok("memory budget " + Mbytes(MemoryManager.AppMemoryUsageLimit) + " MB");

                Apply(MemoryManager.AppMemoryUsageLevel);
            }
            catch (Exception ex)
            {
                _hooked = false;
                Diag.Failed("MemoryWatcher.Start", ex);
            }
        }

        private void OnUsageIncreased(object sender, object e)
        {
            try
            {
                Apply(MemoryManager.AppMemoryUsageLevel);
            }
            catch (Exception ex)
            {
                Diag.Failed("MemoryWatcher.OnUsageIncreased", ex);
            }
        }

        private void OnUsageDecreased(object sender, object e)
        {
            try
            {
                Apply(MemoryManager.AppMemoryUsageLevel);
            }
            catch (Exception ex)
            {
                Diag.Failed("MemoryWatcher.OnUsageDecreased", ex);
            }
        }

        /// <summary>
        /// Applica un livello: si libera entrando in pressione, si smette di
        /// liberare uscendone. La liberazione avviene una volta per transizione,
        /// non a ogni evento: sotto pressione gli eventi si susseguono.
        /// </summary>
        private void Apply(AppMemoryUsageLevel level)
        {
            bool pressure = level == AppMemoryUsageLevel.High
                || level == AppMemoryUsageLevel.OverLimit;
            if (pressure == _underPressure) return;

            _underPressure = pressure;
            if (pressure)
            {
                Diag.Ok("memory under pressure: releasing decoded images");
                DataService.Instance.TrimForMemory();
            }
        }

        private static ulong Mbytes(ulong bytes)
        {
            return bytes / (1024UL * 1024UL);
        }
    }
}
```

In `WhatsappApp/WhatsappApp.csproj`, next to the other `<Compile Include="Services\...">` entries, add:

```xml
    <Compile Include="Services\MemoryWatcher.cs" />
```

- [ ] **Step 4: Add the release path to DataService**

In `WhatsappApp/Services/DataService.cs`, add near `ClearUnread`:

```csharp
        /// <summary>
        /// Libera quello che si puo' rifare: le bitmap degli avatar decodificate
        /// (una per conversazione, la cosa pesante di questa app) e la cronologia
        /// delle chat che nessuno sta leggendo.
        ///
        /// La chat aperta non si tocca: quella la sta guardando l'utente, e
        /// svuotarla sotto gli occhi sarebbe peggio della memoria che libera.
        ///
        /// La collezione di una chat si svuota, non si butta via: la pagina della
        /// chat ha in mano quella istanza, e sostituirla la lascerebbe agganciata
        /// a una lista che non riceve piu' niente. Si dimentica invece di aver
        /// gia' chiesto la cronologia, cosi' riaprendola si richiede.
        /// </summary>
        public void TrimForMemory()
        {
            foreach (var contact in _contacts)
            {
                if (contact != null) contact.Avatar = null;
            }

            var empty = new List<string>();
            foreach (var pair in _chatMessages)
            {
                if (pair.Key == _activeChatId) continue;
                pair.Value.Clear();
                empty.Add(pair.Key);
            }

            foreach (var chatId in empty)
            {
                // Riaprendo la chat la cronologia si richiede: e' l'unico modo
                // perche' una chat svuotata non resti vuota per sempre.
                _historyRequested.Remove(chatId);
            }
        }
```

(`System.Collections.Generic` is already imported in that file — `List<string>` is used elsewhere.)

- [ ] **Step 5: Stop decoding while under pressure**

In `WhatsappApp/Models/Contact.cs`, in `LoadAvatarAsync`, after the existing early return:

```csharp
        public async Task LoadAvatarAsync()
        {
            if (_avatar != null || string.IsNullOrEmpty(_avatarData)) return;
            // Sotto pressione non si decodifica: senza questa riga il primo frame
            // di conversazioni rimette dentro tutto quello appena liberato.
            if (MemoryWatcher.Instance.IsUnderPressure) return;
            try
            {
                Avatar = await ImageHelper.FromBase64Async(_avatarData, AvatarDecodePixels);
            }
```

In `WhatsappApp/Models/ChatMessage.cs`, in the method that fills `MediaImage`, before the decode:

```csharp
            // Un'immagine dentro un fumetto e' la cosa piu' pesante che si possa
            // decodificare sotto pressione: si rimanda a quando il telefono
            // respira, e nel frattempo resta il segnaposto.
            if (MemoryWatcher.Instance.IsUnderPressure) return;
```

- [ ] **Step 6: Start the watcher once per process**

In `WhatsappApp/App.xaml.cs`, inside `StartServicesOnce()` (line 206), after the existing `servicesStarted` guard and next to the other one-time starts:

```csharp
            MemoryWatcher.Instance.Start();
```

- [ ] **Step 7: Guards and the C# gate**

Run: `node tools/check-csharp5.js && node tools/check-memory.js && cd WhatsappBridge && npm test 2>&1 | grep -E "^# (pass|fail)"`
Expected: all green (`AppMemoryUsageLevel`, `AppMemoryUsageIncreased`, `AppMemoryUsageDecreased` and `AppMemoryUsageLimit` are all present in the WP8.1 reference `Windows.winmd`; if the build says otherwise for any of them, that member does not exist on this platform and the whole pressure path must move to `App.OnSuspending`, where `MemoryManager.AppMemoryUsage` alone is still available).

- [ ] **Step 8: Build on Windows**

Expected: `0 Error(s)`, and no new warning.

- [ ] **Step 9: Commit**

```bash
git add WhatsappApp/Services/MemoryWatcher.cs WhatsappApp/Services/DataService.cs \
  WhatsappApp/Models/Contact.cs WhatsappApp/Models/ChatMessage.cs WhatsappApp/App.xaml.cs \
  WhatsappApp/WhatsappApp.csproj WhatsappBridge/test/config.test.js
git commit -m "perf: release decoded images when the phone runs out of memory"
```

---

### Task 4: the two title-bar actions cannot be swapped

**Files:**
- Create: `tools/check-actions.js`
- Create: `tools/test/check-actions.test.js`
- Modify: `WhatsappApp/Pages/ChatsPage.xaml:48-50` (a gap between the two buttons)
- Modify: `WhatsappApp/Pages/ChatsPage.xaml.cs:27-31` (the accessibility names)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `module.exports = { buttonProblems, TITLE_BAR }` from `tools/check-actions.js`.

**What was checked before writing this.** The report was "tapping settings opens new chat instead". The source says the opposite of a swap: `NewChatButton` (`Grid.Column="1"`) carries `Click="NewChatButton_Click"` and the bubble-with-plus geometry, `SettingsButton` (`Grid.Column="2"`) carries `Click="SettingsButton_Click"` and the sliders geometry — and it has said so in **every** commit that touches that file. The compiled page agrees: `obj/x86/Debug/Pages/ChatsPage.g.cs` wires `NewChatButton.Click += this.NewChatButton_Click` and `SettingsButton.Click += this.SettingsButton_Click`. There is also no second pair of icons anywhere in the app and no `FlowDirection`. So there is nothing here to repair blindly, and rewriting a correct layout would only hide the question. What this task does instead: make the inversion **unshippable** (a guard over every button in the app), and make the device answer on the spot (a name under the finger), and give the two targets a gap so a fingertip cannot straddle them.

- [ ] **Step 1: Write the failing test**

Create `tools/test/check-actions.test.js`:

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert');

const actions = require('../check-actions');

const FILE = 'Pages/ChatsPage.xaml';

test('un gestore che non porta il nome del pulsante si segnala', () => {
  const xaml = '<Button x:Name="NewChatButton" Click="SettingsButton_Click">\n</Button>';
  const problems = actions.buttonProblems(xaml, FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /NewChatButton_Click/);
});

test('una coppia coerente passa', () => {
  const xaml = '<Button x:Name="NewChatButton" Click="NewChatButton_Click">\n' +
    '  <!-- IconNewChat -->\n</Button>';
  assert.deepStrictEqual(actions.buttonProblems(xaml, FILE), []);
});

test('due azioni scambiate si segnalano', () => {
  const xaml = '<Button x:Name="NewChatButton" Click="SettingsButton_Click">\n' +
    '  <!-- IconNewChat -->\n</Button>\n' +
    '<Button x:Name="SettingsButton" Click="NewChatButton_Click">\n' +
    '  <!-- IconSettings -->\n</Button>';
  const problems = actions.buttonProblems(xaml, FILE);
  assert.strictEqual(problems.length, 2);
});

test("l'icona scambiata sulla barra del titolo si segnala", () => {
  const xaml = '<Button x:Name="NewChatButton" Click="NewChatButton_Click">\n' +
    '  <!-- IconSettings -->\n</Button>';
  const problems = actions.titleBarProblems(xaml, FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /IconNewChat/);
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `node --test "tools/test/**/*.test.js" 2>&1 | grep -A3 check-actions`
Expected: FAIL — `Cannot find module '../check-actions'`.

- [ ] **Step 3: Write the guard**

Create `tools/check-actions.js`:

```js
#!/usr/bin/env node
/**
 * tools/check-actions.js
 *
 * Guard for the buttons: nome, gestore e icona devono raccontare la stessa
 * azione.
 *
 * Perche' esiste: due pulsanti con la sola icona, uno accanto all'altro,
 * possono essere scambiati con una modifica di una riga - basta spostare un
 * `Click="..."` - e il risultato e' che toccando un'icona se ne apre un'altra.
 * La regola non e' stilistica: e' il modo per rendere quello scambio
 * impossibile da spedire.
 *
 * Regole:
 *  1. un pulsante chiamato X e' cablato solo a X_Click;
 *  2. i pulsanti della barra del titolo disegnano l'icona dichiarata qui sotto.
 *
 * Usage:
 *   node tools/check-actions.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');

/**
 * I due pulsanti con la sola icona che stanno uno accanto all'altro.
 * Se qualcuno ne scambia i Click o i commenti delle icone, questa tabella lo
 * dice invece di lasciarlo arrivare sul telefono.
 */
const TITLE_BAR = {
  'Pages/ChatsPage.xaml': {
    NewChatButton: 'IconNewChat',
    SettingsButton: 'IconSettings'
  }
};

function attribute(tag, name) {
  const m = tag.match(new RegExp(`\\b${name}="([^"]*)"`));
  return m ? m[1] : null;
}

/** Problemi di tutti i pulsanti di un file XAML. */
function buttonProblems(xaml, file) {
  const problems = [];
  for (const m of xaml.matchAll(/<Button\b([^>]*)>([\s\S]*?)<\/Button>/g)) {
    const name = attribute(m[1], 'x:Name');
    const click = attribute(m[1], 'Click');
    if (!name || !click) continue;
    if (click !== name + '_Click') {
      problems.push(`${file}: ${name} is wired to ${click}, and it must be ${name}_Click: ` +
        'the handler of a button carries its name, so an icon cannot open another ' +
        "button's action");
    }
  }
  return problems;
}

/** Problemi della barra del titolo: l'icona dichiarata per ogni pulsante. */
function titleBarProblems(xaml, file) {
  const declared = TITLE_BAR[file];
  if (!declared) return [];

  const problems = [];
  for (const m of xaml.matchAll(/<Button\b([^>]*)>([\s\S]*?)<\/Button>/g)) {
    const name = attribute(m[1], 'x:Name');
    if (!name || !declared[name]) continue;
    const icon = (m[2].match(/<!--\s*(Icon[A-Za-z]+)\s*-->/) || [])[1];
    if (icon !== declared[name]) {
      problems.push(`${file}: ${name} draws ${icon || 'no icon'}, and it must be ` +
        `${declared[name]}: the two title-bar buttons are one next to the other`);
    }
  }
  return problems;
}

function walk(dir, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'obj' || entry.name === 'bin') continue;
      walk(path.join(dir, entry.name), out);
    } else if (entry.name.endsWith('.xaml')) {
      out.push(path.join(dir, entry.name));
    }
  }
  return out;
}

function main() {
  const problems = [];
  let buttons = 0;

  for (const file of walk(APP, [])) {
    const rel = path.relative(APP, file).replace(/\\/g, '/');
    const xaml = fs.readFileSync(file, 'utf8');
    buttons += (xaml.match(/<Button\b[^>]*Click="/g) || []).length;
    problems.push(...buttonProblems(xaml, rel));
    problems.push(...titleBarProblems(xaml, rel));
  }

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} action problem(s).`);
    process.exit(1);
  }
  console.log(`OK: ${buttons} button(s), name/handler/icon agree.`);
}

if (require.main === module) main();

module.exports = { buttonProblems, titleBarProblems, TITLE_BAR };
```

- [ ] **Step 4: Run the guard against the app**

Run: `node tools/check-actions.js`
Expected: PASS — `OK: 17 button(s), name/handler/icon agree.` (every `Click="…"` in the app already follows `<name>_Click`). **If it reports a button in another page whose handler is named differently, that is a finding: rename that handler to `<name>_Click`. Do not weaken the rule** — the rule is the whole point of the task.

- [ ] **Step 5: Make the targets not touch**

In `WhatsappApp/Pages/ChatsPage.xaml`, the two buttons are adjacent (the settings one is 48 px wide with a 4 px right margin, the new-chat one 48 px with none), so a fingertip centred across the border can land on the neighbour:

```xml
            <Button x:Name="NewChatButton" Grid.Column="1"
                    Background="Transparent" BorderThickness="0" Padding="0"
                    Width="48" Height="48" Margin="0,0,8,0" Click="NewChatButton_Click">
```

- [ ] **Step 6: Name the action under the finger**

In `WhatsappApp/Pages/ChatsPage.xaml.cs`, after the two existing `ToolTipService.SetToolTip` lines (the tooltip stays as it is), add:

```csharp
            // Il tooltip lo vede chi tiene premuto; il nome lo legge il lettore di
            // schermo e lo usa anche l'assistente vocale. Stesso testo, quindi
            // stessa chiave: se un'icona apre l'azione sbagliata, la si sente
            // dire invece di doverla indovinare.
            AutomationProperties.SetName(NewChatButton,
                Loc.Get("ChatsPage_NewChatTooltip", "New chat"));
            AutomationProperties.SetName(SettingsButton,
                Loc.Get("ChatsPage_SettingsTooltip", "Settings"));
```

Add `using Windows.UI.Xaml.Automation;` to the file's usings.

- [ ] **Step 7: Guards and the C# gate**

Run: `node tools/check-actions.js && node tools/check-resw.js --strict && node tools/check-csharp5.js`
Expected: all green (both `.resw` keys already exist, and `AutomationProperties` is a WP8.1 API).

- [ ] **Step 8: Build on Windows**

Expected: `0 Error(s)`.

- [ ] **Step 9: Commit**

```bash
git add tools/check-actions.js tools/test/check-actions.test.js \
  WhatsappApp/Pages/ChatsPage.xaml WhatsappApp/Pages/ChatsPage.xaml.cs
git commit -m "guard: make a swapped title-bar action impossible to ship"
```

---

### Task 5: the guards are the gate, the docs and the skills say so

**Files:**
- Modify: `README.md` (`### Icons, tiles and splash screen`, a new `### Memory on a 512 MB device` section, the gate in `## Contributing`, `## Limitations`)
- Modify: `README.it.md` (the same three places, in Italian)
- Modify: `.agents/skills/maintain-the-app/SKILL.md`
- Modify: `.agents/skills/test-the-app/SKILL.md`
- Modify: `.agents/skills/update-the-app/SKILL.md`
- Modify: `docs/superpowers/plans/2026-09-27-tile-memory-and-title-bar.md` (this file)

**Interfaces:**
- Consumes: everything from Tasks 1-4.
- Produces: the documented gate.

- [ ] **Step 1: Add the guards to the gate in both READMEs**

In `README.md`, the gate inside `## Contributing` (line 196):

```bash
   node tools/check-csharp5.js && node tools/check-icons.js \
     && node tools/check-resw.js --strict && node tools/check-docs.js \
     && node tools/check-framing.js && node tools/check-tile.js \
     && node tools/check-memory.js && node tools/check-actions.js \
     && node --test "tools/test/**/*.test.js"
   cd WhatsappBridge && npm test
```

The same edit in `README.it.md`, inside `## Contribuire` (line 199).

- [ ] **Step 2: Document the tile icon**

In `README.md`, in `### Icons, tiles and splash screen`, after the `node tools/check-icons.js --preview  # + ASCII preview (needs ImageMagick)` block, add:

```markdown
The live tile has its own asset, `Assets/TileIcon.png` (and its 240 % version,
`TileIcon.scale-240.png`, 480×480): a transparent PNG with **no padding**, written
by the same `make-brand-assets.js`. It is not a duplicate of `Logo.png` and it is
not interchangeable with it — the manifest logos carry the padding the system
expects, and the iconic tile template wants the opposite.

`tools/check-tile.js` guards it, because the failure it catches is invisible: the
`TileSquare150x150IconWithBadge` template does **not** take the icon from the
manifest, it wants `<image src="…"/>` in the payload. With an empty `src` the tile
renders without an icon and raises no exception at all.
```

The same paragraph, in Italian, in `README.it.md` in `### Icone, tile e splash screen`, after the corresponding `check-icons.js --preview` block, with `Scala`/`Nessun padding` phrasing consistent with that file.

- [ ] **Step 3: Document the 512 MB work**

In `README.md`, add a section after `### Icons, tiles and splash screen` (before `### App language`):

```markdown
### Memory on a 512 MB device

A 512 MB phone gives the app a hard memory limit (roughly 185 MB; a 1 GB device
allows about twice that) and suspends or terminates it when the app keeps growing.
WP8.1 has **no manifest declaration for this** — `AppxManifestSchema2010_v2.xsd` and
friends have no memory element at all — so this is runtime work, in two halves:

- **decode at the size you draw**: `ImageHelper` takes the width the image is shown
  at and sets `DecodePixelWidth` before `SetSourceAsync`. A 640×640 profile picture
  drawn as a 52 px circle costs a few tens of KB instead of ~1.6 MB, times one per
  conversation;
- **let go when asked**: `MemoryWatcher` listens to
  `MemoryManager.AppMemoryUsageIncreased` and, at `High`/`OverLimit`, drops the
  decoded avatars and clears the history of every chat that is not open (the open
  one is left alone — it is the one being read). While the pressure lasts nothing
  new is decoded. The device's own limit is logged once, as
  `DIAG ok: memory budget N MB`.

`tools/check-memory.js` fails the build if a call site forgets the decode width, if
one asks for more than the screen can show, or if `ImageHelper` sets
`DecodePixelWidth` after the decode. `WhatsappBridge/test/config.test.js` holds the
adapter side of the same budget (`CHATS_LIMIT` ≤ 30, `MESSAGES_LIMIT` ≤ 60).
```

The same section in Italian in `README.it.md` (`### Memoria su un telefono da 512 MB`) after `### Icone, tile e splash screen`.

- [ ] **Step 4: Say the honest limit**

In `README.md`, in `## Limitations`, add:

```markdown
- Notifications and the tile badge only change while the app is running: WP8.1
  suspends the app and closes its socket, and a real push would need a cloud
  service this project does not have. The unread count on the tile is drawn by the
  badge — a tile notification sets the icon, the badge sets the number.
- Under memory pressure the app drops decoded avatars and reloads them later; on a
  phone that stays under pressure the chat list can show initials for a while.
```

The same two bullets in Italian in `README.it.md` `## Limiti`.

- [ ] **Step 5: Put the rules in the skills**

In `.agents/skills/maintain-the-app/SKILL.md`:

- after the line `   Gate: \`node tools/check-icons.js\` (add \`--preview\` for an ASCII render).`, add a numbered item in the same style: the live tile needs the iconic asset and an `src` on **every** binding, `TileWide310x150IconWithBadge` does not exist on WP8.1, and the gate is `node tools/check-tile.js`;
- after the line `   Gate: \`node tools/check-resw.js --strict\`.` add: every bitmap decode goes through `ImageHelper` with the width it is drawn at, and the gate is `node tools/check-memory.js`;
- after the framing item (line 231) add: a button named `X` is wired only to `X_Click` and draws the icon the guard declares for it, gate `node tools/check-actions.js`;
- in the "before every commit" list (line 147, "Run all four guards") change the count and the command to the full gate, including `node --test "tools/test/**/*.test.js"`.

In `.agents/skills/test-the-app/SKILL.md`: extend the command block at line 15-19 with the three new guards, update the counts in the line that says "Paths (9 distinct icons), 83 adapter tests, 17 tests in `tools/test`" (the three new test files take it to 32), and add to the on-device checklist:

```markdown
- the tile on the Start screen shows the app icon **with the number** while there
  are unread messages, and goes back to the manifest tile when they are zero;
- the avatars in the chat list decode small: on a 512 MB phone the list scrolls
  without the app being closed, and `DIAG ok: memory budget N MB` says `185 MB`-ish
  rather than `380 MB`-ish;
- tapping the new-chat icon opens the new-chat dialog and tapping the sliders icon
  opens the settings — the two are 8 px apart and each says its name on a long press.
```

In `.agents/skills/update-the-app/SKILL.md`: add the three new guards to the gate list at line 139-140.

- [ ] **Step 6: Run the documentation guard**

Run: `node tools/check-docs.js`
Expected: PASS (the two languages are in step; no emoji).

- [ ] **Step 7: Record what execution changed**

Append a `## What execution changed about this plan` section to **this file** (`docs/superpowers/plans/2026-09-27-tile-memory-and-title-bar.md`), before whatever closing section it has, listing every divergence from what is written above: a signature that had to move, an API the WP8.1 compiler rejected, a step that turned out to be unnecessary. Do not rewrite the tasks above — the record is the point.

- [ ] **Step 8: The full gate**

Run:

```bash
node tools/check-csharp5.js && node tools/check-icons.js \
  && node tools/check-resw.js --strict && node tools/check-docs.js \
  && node tools/check-framing.js && node tools/check-tile.js \
  && node tools/check-memory.js && node tools/check-actions.js \
  && node --test "tools/test/**/*.test.js"
cd WhatsappBridge && npm test && cd ..
```

Expected: all green — 31 C# files, 9 icons, 104 `.resw` keys in both languages, 4 framing checks, 1 tile asset, 0 memory problems, 17 buttons, **32** tool tests (17 + 6 + 5 + 4), 98 adapter tests (97 plus the new config one).

- [ ] **Step 9: Build on Windows, again, from a clean copy**

Expected: `COPIA=0`, `0 Error(s)`, `0 Warning(s)` (and if `check-memory` or the compiler disagrees with any WP8.1 API used here, fix the code — never the guard).

- [ ] **Step 10: Commit and push**

```bash
git add README.md README.it.md .agents/skills docs/superpowers/plans
git commit -m "docs: the tile icon, the 512 MB budget and the button guard"
git push
```

---

## What only the phone can prove

The three fixes were built and verified against the WP8.1 compiler and the guards, but the last metre is a device:

1. **The tile**: pin the app, receive a message with the app in the background so the count goes up — the Start tile must show **the app icon with the number**, and go back to the manifest tile when the number returns to zero. On WP8.1 the number on the tile is drawn by the badge, and the tile notification is what gives the icon; both halves are needed.
2. **512 MB**: the first line of the debug output is now `DIAG ok: memory budget N MB`. On a 512 MB phone that number is what tells whether the pressure path can ever fire; on a 1 GB one it likely never will, and the decode-size change is the part that matters.
3. **The title bar**: tap the plus-bubble (new chat) and the sliders (settings) in both orientations, and long-press each to read its name. If an icon still opens the other action, the built package is not the one in this tree — `node tools/check-actions.js` on the source and `Pages/ChatsPage.g.cs` in the build output both say which pairing the code has.
