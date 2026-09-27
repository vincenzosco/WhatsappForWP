# Audio, Voice Notes And Documents Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a received voice note or audio file play and a received document open, instead of both staying a word in the bubble. WhatsApp voice notes are Ogg/Opus and Windows Phone 8.1 has no Opus decoder, so the adapter converts them to MP3 with `ffmpeg` when it is available, and falls back to the original bytes when it is not.

**Architecture:** The app (`WhatsappApp/`, C# 5) talks over an encrypted TCP socket to a Node.js adapter (`WhatsappBridge/`) fronting GOWA. Media travels as base64 in `media` control frames, chunked because a frame is capped at 8 MiB. The adapter already sends a received video, a photo and a document; what changes is (a) an optional `ffmpeg` transcode step on the adapter for Ogg/Opus, (b) an `audio` / `document` kind the app can tell apart, and (c) the app persisting audio and documents to disk and offering a play box or an open action. This reuses the video path end to end: the same chunking, the same on-disk storage in `IncomingMediaStore`, the same `MediaElement` player.

**Tech Stack:** C# 5 / XAML (Windows Phone 8.1 WinRT), Node.js (no npm dependencies in the adapter; `ffmpeg` is an optional external binary), Node's built-in test runner for the adapter and the repo guards.

## Global Constraints

- C# 5 syntax only. No `nameof`, `?.`, string interpolation, expression-bodied members, auto-property initializers, inline `out var`, pattern matching. `node tools/check-csharp5.js` enforces this.
- WP8.1 API surface only. `ShareOperation` has no `GetDeferral()`; `Windows.Foundation.Deferral` does not exist; `ContentDialog` has no `CloseButtonText`; `ExceptionRoutedEventArgs` has no `ErrorException` (only `ErrorMessage`). `node tools/check-csharp5.js` enforces this.
- No `Segoe MDL2 Assets`. Icons are inline `<Path>` geometries, each named by a `<!-- IconX -->` comment above its `<Path.Data>`, and the *same* icon must be the *same geometry* everywhere. `node tools/check-icons.js` enforces this.
- Every user-visible string exists in BOTH `WhatsappApp/Strings/en-US/Resources.resw` and `WhatsappApp/Strings/it-IT/Resources.resw`, and `--strict` fails on an unused key. `node tools/check-resw.js --strict` enforces this.
- A `Border` takes exactly **one** child (WMC0035). Multiple children go inside a `<Grid>`.
- Base64 pieces are cut on multiples of 4 characters so they concatenate without re-encoding. The app sends 700000 base64 characters per chunk (`ChatPage.MediaChunkChars`); the adapter sends `MEDIA_CHUNK_CHARS = 700000`. Keep those equal.
- The frame ceiling is `8 * 1024 * 1024` on both sides. `node tools/check-framing.js` enforces this.
- README pairs stay in sync: `README.md` + `README.it.md`, and `WhatsappBridge/README.md` + `WhatsappBridge/README.it.md`. Same heading order, root `## Disclosure` last, no emoji in any `.md` file. `node tools/check-docs.js` enforces this.
- The adapter has **zero npm runtime dependencies**. `ffmpeg` is an optional external executable invoked through `child_process`; the adapter must work with it absent.
- Runtime text (adapter logs, error frames) stays English. Source comments and adapter test names stay Italian.
- Commit messages must not contain apostrophes. Commit footer: a `Generated with Codebuff` line prefixed with the robot emoji, then `Co-Authored-By: Codebuff <noreply@codebuff.com>`. The commands below write the footer without the emoji because this guard rejects emoji in a markdown file; add it when the commit is actually made.
- Every change ends with `git push` (see `.agents/skills/maintain-the-app/SKILL.md`).
- The fast gate, from the repo root, must stay green:
  `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"` and then `cd WhatsappBridge && npm test`.

## File Structure

- `WhatsappBridge/ffmpeg.js` (create) — the optional Ogg/Opus → MP3 transcoder, with the executable injected so tests need no `ffmpeg`.
- `WhatsappBridge/config.js` (modify) — `FFMPEG_ENABLED` and `FFMPEG_PATH`.
- `WhatsappBridge/server.js` (modify) — build the transcoder, probe it at startup, transcode a received audio before chunking it to the app, and give audio and documents their own kind.
- `WhatsappBridge/message-format.js` (modify) — a document declares itself even when its bytes are not downloaded.
- `WhatsappBridge/test/ffmpeg.test.js` (create), `test/server.test.js`, `test/config.test.js`, `test/message-format.test.js` (modify) — cover the new behaviour.
- `WhatsappApp/Services/IncomingMediaStore.cs` (modify) — persist audio and documents to disk, like a video, and learn their extensions.
- `WhatsappApp/Models/ChatMessage.cs` (modify) — `IsAudio` and `IsDocument`.
- `WhatsappApp/Services/DataService.cs` (modify) — a downloaded audio becomes `MessageType.Audio`, a document keeps its text.
- `WhatsappApp/Pages/ChatPage.xaml` (modify) — a play bar for audio and a document bar, in both bubbles.
- `WhatsappApp/Pages/ChatPage.xaml.cs` (modify) — play an audio from its file, open a document with the system, extend `Downloadable`.
- `WhatsappApp/Strings/en-US/Resources.resw`, `it-IT/Resources.resw` (modify) — the two new messages.
- `README.md` + `README.it.md`, `WhatsappBridge/README.md` + `WhatsappBridge/README.it.md` (modify) — the new behaviour and the `ffmpeg` dependency.
- `.agents/skills/maintain-the-app/SKILL.md`, `.agents/skills/test-the-app/SKILL.md` (modify) — the gotchas and the on-device checks.

No new C# file, so `WhatsappApp.csproj` is unchanged.

---

### Task 1: An Ogg/Opus transcoder on the adapter

WhatsApp voice notes are Ogg with the Opus codec. WP8.1 has no Opus decoder (Opus only arrives on Windows 10), so the bytes cannot play as they are. `ffmpeg` converts them to MP3, which the phone decodes. `ffmpeg` is optional: it is an external program on the adapter host, not an npm dependency, so the module takes the executable call as an injected function and every test runs without `ffmpeg` installed.

**Files:**
- Create: `WhatsappBridge/ffmpeg.js`
- Test: `WhatsappBridge/test/ffmpeg.test.js`

**Interfaces:**
- Consumes: `child_process.execFile(file, args, options, callback)` with `{ encoding: 'buffer' }`.
- Produces:
  - `createTranscoder({ enabled, path, log, run })` → an object with `isAvailable()`, `probe()` → `Promise<boolean>`, and `toPlayable(buffer, mimeType, fileName)` → `Promise<{ buffer, mimeType, fileName } | null>`.
  - `isOggOpus(mimeType, fileName)` → `boolean`.
  - `replaceExtension(fileName, extension)` → `string`.
  - `TRANSCODE_ARGS` → `string[]`.

- [ ] **Step 1: Write the failing tests**

Create `WhatsappBridge/test/ffmpeg.test.js`:

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { createTranscoder, isOggOpus, replaceExtension } = require('../ffmpeg');

const silent = () => {};

test('Ogg e Opus si riconoscono dal tipo o dal nome', () => {
  assert.ok(isOggOpus('audio/ogg', 'audio.ogg'));
  assert.ok(isOggOpus('audio/opus', null));
  assert.ok(isOggOpus(null, 'voce.opus'));
  assert.ok(!isOggOpus('audio/mpeg', 'canzone.mp3'));
  assert.ok(!isOggOpus('audio/mp4', 'voce.m4a'));
});

test('replaceExtension sostituisce solo l ultima estensione', () => {
  assert.strictEqual(replaceExtension('voce.ogg', '.mp3'), 'voce.mp3');
  assert.strictEqual(replaceExtension('audio', '.mp3'), 'audio.mp3');
  assert.strictEqual(replaceExtension('a.b.oga', '.mp3'), 'a.b.mp3');
});

test('un vocale Ogg diventa un MP3', async () => {
  const calls = [];
  const transcoder = createTranscoder({
    log: silent,
    run: async (args, input) => { calls.push({ args, input }); return Buffer.from('mp3-finto'); }
  });
  await transcoder.probe();
  const out = await transcoder.toPlayable(Buffer.from('ogg-finto'), 'audio/ogg', 'voce.ogg');

  assert.strictEqual(out.mimeType, 'audio/mpeg');
  assert.strictEqual(out.fileName, 'voce.mp3');
  assert.strictEqual(out.buffer.toString(), 'mp3-finto');
  assert.strictEqual(calls.length, 2);              // la prova e la conversione
  assert.ok(calls[1].args.includes('pipe:1'));
});

test('un audio che il telefono legge non si tocca', async () => {
  let ran = 0;
  const transcoder = createTranscoder({
    log: silent,
    run: async () => { ran++; return Buffer.alloc(0); }
  });
  await transcoder.probe();
  const before = ran;
  assert.strictEqual(await transcoder.toPlayable(Buffer.from('x'), 'audio/mp4', 'voce.m4a'), null);
  assert.strictEqual(ran, before);                  // nessuna chiamata in piu
});

test('senza ffmpeg si resta muti e non si prova per ogni vocale', async () => {
  let ran = 0;
  const transcoder = createTranscoder({
    log: silent,
    run: async () => { ran++; throw new Error('not found'); }
  });
  assert.strictEqual(await transcoder.probe(), false);
  assert.strictEqual(transcoder.isAvailable(), false);
  assert.strictEqual(await transcoder.toPlayable(Buffer.from('x'), 'audio/ogg', 'voce.ogg'), null);
  assert.strictEqual(ran, 1);                       // solo la prova
});

test('se ffmpeg fallisce si manda l originale', async () => {
  const transcoder = createTranscoder({
    log: silent,
    run: async (args) => {
      if (args[0] === '-version') return Buffer.alloc(0);
      throw new Error('boom');
    }
  });
  await transcoder.probe();
  assert.strictEqual(await transcoder.toPlayable(Buffer.from('x'), 'audio/ogg', 'voce.ogg'), null);
});

test('disabilitato non si prova nemmeno', async () => {
  let ran = 0;
  const transcoder = createTranscoder({ enabled: false, log: silent, run: async () => { ran++; return Buffer.alloc(0); } });
  assert.strictEqual(await transcoder.probe(), false);
  assert.strictEqual(ran, 0);
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd WhatsappBridge && node --test test/ffmpeg.test.js`
Expected: FAIL — `Cannot find module '../ffmpeg'`.

- [ ] **Step 3: Write the module**

Create `WhatsappBridge/ffmpeg.js`:

```js
'use strict';

// Trascodifica l'audio che Windows Phone 8.1 non sa decodificare.
//
// Perche' esiste: i messaggi vocali di WhatsApp sono Ogg con codec Opus, e
// WP8.1 non ha un decoder Opus (arriva solo da Windows 10). Senza questa
// conversione un vocale resta una parola che non si puo' toccare. L'adapter
// chiama ffmpeg, se c'e', e manda all'app un MP3, che il telefono legge.
//
// ffmpeg e' un programma esterno, non una dipendenza npm: l'adapter deve
// funzionare anche senza. La chiamata e' iniettabile, cosi' i test non hanno
// bisogno di ffmpeg installato.

const { execFile } = require('child_process');

// Il tetto dei byte in memoria. Lo stesso numero di server.js (MAX_MEDIA_BYTES):
// oltre non c'e' piu' un media ma un guasto.
const MAX_MEDIA_BYTES = 64 * 1024 * 1024;

// Mono, 16 kHz, 32 kbit/s: un vocale WhatsApp e' parlato, e questa e' la forma
// piu' piccola che resta intelligibile. Il file si scarica dal telefono.
const TRANSCODE_ARGS = [
  '-hide_banner',
  '-loglevel', 'error',
  '-i', 'pipe:0',
  '-vn',
  '-ac', '1',
  '-ar', '16000',
  '-b:a', '32k',
  '-f', 'mp3',
  'pipe:1'
];

/** Il tipo che WP8.1 non sa leggere: Ogg, Opus o il loro contenitore. */
function isOggOpus(mimeType, fileName) {
  const mime = String(mimeType || '').toLowerCase();
  const name = String(fileName || '').toLowerCase();
  if (mime === 'audio/ogg' || mime === 'audio/opus' || mime === 'audio/oga') return true;
  return name.endsWith('.ogg') || name.endsWith('.opus') || name.endsWith('.oga');
}

/** Il nome del file con un'altra estensione, o con quella se non ne ha. */
function replaceExtension(fileName, extension) {
  const name = String(fileName || 'audio');
  const dot = name.lastIndexOf('.');
  const base = dot > 0 ? name.slice(0, dot) : name;
  return `${base}${extension}`;
}

/** ffmpeg legge l'input da stdin e scrive l'output su stdout: nessun file di mezzo. */
function execFfmpeg(command, args, input) {
  return new Promise((resolve, reject) => {
    const child = execFile(command, args, { maxBuffer: MAX_MEDIA_BYTES, encoding: 'buffer' },
      (err, stdout) => {
        if (err) { reject(err); return; }
        resolve(stdout);
      });
    child.stdin.end(input || undefined);
  });
}

/**
 * Un transcodificatore ffmpeg. `run` e' iniettabile: i test non hanno ffmpeg
 * installato e non devono averlo.
 */
function createTranscoder(options) {
  const o = options || {};
  const command = o.path || 'ffmpeg';
  const logger = typeof o.log === 'function' ? o.log : function () {};
  const enabled = o.enabled !== false;
  const run = typeof o.run === 'function' ? o.run : (args, input) => execFfmpeg(command, args, input);

  // null finche' non si e' provato: un vocale non deve far partire ffmpeg una
  // volta per messaggio solo per scoprire che non c'e'.
  let available = null;

  return {
    isAvailable() { return available === true; },

    /** Si prova una volta, all'avvio, e si dice nel log com'e' andata. */
    async probe() {
      if (!enabled) {
        available = false;
        logger('INFO', 'ffmpeg disabled: Ogg/Opus voice notes will not be playable on WP8.1');
        return false;
      }
      try {
        await run(['-version'], null);
        available = true;
        logger('OK', 'ffmpeg found: voice notes will be transcoded to MP3 for WP8.1');
      } catch (err) {
        available = false;
        logger('WARN', `ffmpeg not found (${err.message}): Ogg/Opus voice notes will not be playable on WP8.1`);
      }
      return available;
    },

    /**
     * I byte da mandare all'app. Per un audio che WP8.1 non legge restituisce
     * l'MP3 e la sua identita'; per tutto il resto, o quando ffmpeg non c'e'
     * o fallisce, restituisce null e l'adapter manda l'originale.
     */
    async toPlayable(buffer, mimeType, fileName) {
      if (!enabled || available !== true) return null;
      if (!isOggOpus(mimeType, fileName)) return null;
      if (!buffer || buffer.length === 0) return null;

      try {
        const mp3 = await run(TRANSCODE_ARGS, buffer);
        if (!mp3 || mp3.length === 0) return null;
        return {
          buffer: mp3,
          mimeType: 'audio/mpeg',
          fileName: replaceExtension(fileName || 'audio.ogg', '.mp3')
        };
      } catch (err) {
        logger('WARN', `ffmpeg transcode failed: ${err.message}`);
        return null;
      }
    }
  };
}

module.exports = { createTranscoder, isOggOpus, replaceExtension, TRANSCODE_ARGS };
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd WhatsappBridge && node --test test/ffmpeg.test.js`
Expected: PASS, 7 tests.

- [ ] **Step 5: Run the whole adapter suite**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 125 tests (118 + 7).

- [ ] **Step 6: Commit**

```bash
git add WhatsappBridge/ffmpeg.js WhatsappBridge/test/ffmpeg.test.js
git commit -m "$(cat <<'EOF'
feat: transcode Ogg/Opus voice notes to MP3 on the adapter

WP8.1 has no Opus decoder, so a WhatsApp voice note cannot play as it is.
ffmpeg is optional and injected, so the adapter still runs without it.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
git push origin master
```

---

### Task 2: Audio and documents are their own kind, and the comporter calls the transcoder

Two places produce the `MediaType` the app reads: the webhook path (a message that just arrived, with bytes) and the `media.get` path (a history row whose bytes are downloaded on demand). Both must say `audio` for a voice note and `document` for a file, and both must run the transcoder before chunking an audio. `mediaKindOf` currently returns `file` for anything that is not image or video, and the adapter never arrives at a way to convert.

**Files:**
- Modify: `WhatsappBridge/config.js`
- Modify: `WhatsappBridge/server.js`
- Modify: `WhatsappBridge/message-format.js`
- Modify: `WhatsappBridge/test/config.test.js`
- Modify: `WhatsappBridge/test/server.test.js`
- Modify: `WhatsappBridge/test/message-format.test.js`

**Interfaces:**
- Consumes (from Task 1): `createTranscoder` from `./ffmpeg`.
- Produces: `createBridge({ config, gowa, log, debug, transcoder })` now also accepts an optional `transcoder` (an object with `probe()` and `toPlayable(...)`), and returns it as `bridge.probeFfmpeg()`.
- Produces: `mediaKindOf(mimeType, fileName)` returns `'video' | 'image' | 'audio' | 'document'`.

- [ ] **Step 1: Write the failing config test**

In `WhatsappBridge/test/config.test.js`, add:

```js
test('loadConfig espone la configurazione di ffmpeg', () => {
  const c = loadConfig({});
  assert.strictEqual(c.ffmpeg.enabled, true);
  assert.strictEqual(c.ffmpeg.path, 'ffmpeg');

  const off = loadConfig({ FFMPEG_ENABLED: 'off', FFMPEG_PATH: '/usr/local/bin/ffmpeg' });
  assert.strictEqual(off.ffmpeg.enabled, false);
  assert.strictEqual(off.ffmpeg.path, '/usr/local/bin/ffmpeg');
});
```

- [ ] **Step 2: Write the failing server tests**

In `WhatsappBridge/test/server.test.js`, add after the incoming-video test:

```js
test('un vocale Ogg in arrivo arriva come MP3', async () => {
  const sent = [];
  const gowa = {
    fetchBinary: async () => ({ buffer: Buffer.from('vocali-opus'), contentType: 'audio/ogg' })
  };
  const transcoder = {
    probe: async () => true,
    toPlayable: async () => ({ buffer: Buffer.from('mp3-convertito'), mimeType: 'audio/mpeg', fileName: 'voce.mp3' })
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {}, transcoder });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleWebhookEvent({
    event: 'message',
    payload: {
      id: 'A1', chat_id: 'a@s.whatsapp.net', from: 'a@s.whatsapp.net',
      audio: { path: 'statics/media/v.ogg' }, timestamp: '2026-09-27T08:00:00Z'
    }
  });

  assert.strictEqual(sent[0].Type, 2);
  assert.strictEqual(sent[0].MediaType, 'audio');

  const bytes = sent.filter((f) => f.Command === 'media');
  assert.strictEqual(bytes.length, 1);
  assert.strictEqual(bytes[0].MediaType, 'audio');
  assert.strictEqual(bytes[0].MediaMimeType, 'audio/mpeg');
  assert.strictEqual(bytes[0].MediaFileName, 'voce.mp3');
  assert.strictEqual(bytes[0].MediaData, Buffer.from('mp3-convertito').toString('base64'));
});

test('senza ffmpeg un vocale in arrivo resta quello che e', async () => {
  const sent = [];
  const gowa = {
    fetchBinary: async () => ({ buffer: Buffer.from('vocali-opus'), contentType: 'audio/ogg' })
  };
  const transcoder = { probe: async () => false, toPlayable: async () => null };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {}, transcoder });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleWebhookEvent({
    event: 'message',
    payload: {
      id: 'A2', chat_id: 'a@s.whatsapp.net', from: 'a@s.whatsapp.net',
      audio: { path: 'statics/media/v.ogg' }, timestamp: '2026-09-27T08:00:00Z'
    }
  });

  const bytes = sent.filter((f) => f.Command === 'media');
  assert.strictEqual(bytes[0].MediaType, 'audio');
  assert.strictEqual(bytes[0].MediaMimeType, 'audio/ogg');
});

test('un vocale scaricato a richiesta diventa MP3', async () => {
  const sent = [];
  const gowa = {
    downloadMedia: async () => ({ base64: Buffer.from('opus').toString('base64'), mimeType: 'audio/ogg', fileName: 'voce.ogg' })
  };
  const transcoder = {
    probe: async () => true,
    toPlayable: async () => ({ buffer: Buffer.from('mp3'), mimeType: 'audio/mpeg', fileName: 'voce.mp3' })
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {}, transcoder });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({ Type: 3, Command: 'media.get', Text: 'a@s.whatsapp.net', RelatedMessageId: 'A3' });

  assert.strictEqual(sent[0].MediaType, 'audio');
  assert.strictEqual(sent[0].MediaMimeType, 'audio/mpeg');
  assert.strictEqual(sent[0].MediaFileName, 'voce.mp3');
});

test('un documento scaricato si dichiara documento', async () => {
  const sent = [];
  const gowa = {
    downloadMedia: async () => ({ base64: Buffer.from('pdf').toString('base64'), mimeType: 'application/pdf', fileName: 'contratto.pdf' })
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({ Type: 3, Command: 'media.get', Text: 'a@s.whatsapp.net', RelatedMessageId: 'D1' });

  assert.strictEqual(sent[0].MediaType, 'document');
  assert.strictEqual(sent[0].MediaFileName, 'contratto.pdf');
});
```

- [ ] **Step 3: Write the failing message-format test**

In `WhatsappBridge/test/message-format.test.js`, add after `un documento senza nome resta una parola`:

```js
test('un documento non scaricato si dichiara documento lo stesso', () => {
  const f = mapWebhookMessage({ id: 'D3', chat_id: 'a@s.whatsapp.net', document: {} });
  assert.strictEqual(f.mediaType, 'document');
  assert.strictEqual(f.text, '[Document not downloaded]');
});
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `cd WhatsappBridge && node --test test/config.test.js test/server.test.js test/message-format.test.js`
Expected: FAIL — `c.ffmpeg` is undefined, the audio tests see `MediaType` `'audio'` but the transcode tests get the original mime, and `f.mediaType` is `''`.

- [ ] **Step 5: Add the configuration**

In `WhatsappBridge/config.js`, add to `DEFAULTS`:

```js
  FFMPEG_ENABLED: 'on',
  FFMPEG_PATH: ''
```

and to the object `loadConfig` returns, after `messages`:

```js
    ffmpeg: {
      // La conversione e' opzionale: senza ffmpeg l'app riceve l'audio
      // originale e non lo sa leggere, quindi il vocale resta muto.
      enabled: pick(env, 'FFMPEG_ENABLED').toLowerCase() !== 'off',
      path: pick(env, 'FFMPEG_PATH') || 'ffmpeg'
    }
```

- [ ] **Step 6: Give a document a kind even without bytes**

In `WhatsappBridge/message-format.js`, in `mediaFromPayload`, set the kind on the document branch. Replace:

```js
  } else if (p.document !== undefined) {
    result.fallbackText = '[Document]';
```

with:

```js
  } else if (p.document !== undefined) {
    // Un documento resta un documento anche quando non e' stato scaricato:
    // l'app deve sapere che puo' chiederlo.
    result.kind = 'document';
    result.fallbackText = '[Document]';
```

Then replace `mediaKind` with:

```js
/// La parola del tipo di media, o vuota per un messaggio di solo testo.
function mediaKind(media) {
  if (media && media.kind) return media.kind;
  const type = media && media.type;
  if (type === 1) return 'image';
  if (type === 2) return 'audio';
  if (type === 4) return 'video';
  return media && media.path ? 'document' : '';
}
```

- [ ] **Step 7: Build the transcoder and transcode in the two media paths**

In `WhatsappBridge/server.js`, add the require next to the other modules:

```js
const { createTranscoder } = require('./ffmpeg');
```

Change the signature of `createBridge` and the first lines of its body. Replace:

```js
function createBridge({ config, gowa, log, debug }) {
  const logger = typeof log === 'function' ? log : () => {};
  const dbg = typeof debug === 'function' ? debug : () => {};
```

with:

```js
function createBridge({ config, gowa, log, debug, transcoder }) {
  const logger = typeof log === 'function' ? log : () => {};
  const dbg = typeof debug === 'function' ? debug : () => {};

  // La conversione dei vocali: un ffmpeg trovato all'avvio, oppure quello che
  // i test iniettano. `enabled` viene dalla configurazione.
  const mediaTools = transcoder || createTranscoder({
    enabled: !config || !config.ffmpeg || config.ffmpeg.enabled !== false,
    path: config && config.ffmpeg ? config.ffmpeg.path : undefined,
    log: logger
  });
```

Add a helper after `sendMediaChunks`:

```js
  /**
   * I byte da mandare all'app per un media ricevuto. Un audio che WP8.1 non
   * legge (Ogg/Opus) diventa MP3; tutto il resto passa invariato, e cosi' fa
   * anche un vocale quando ffmpeg non c'e' o la conversione fallisce.
   */
  async function playableMedia(buffer, mediaType, mimeType, fileName) {
    if (mediaType !== 'audio') return { buffer, mimeType, fileName };
    const converted = await mediaTools.toPlayable(buffer, mimeType, fileName);
    if (!converted) return { buffer, mimeType, fileName };
    return converted;
  }
```

Replace the tail of `sendMedia` (the `sendMediaChunks` call and its log). Replace:

```js
      sendMediaChunks(chatId, messageId, mediaKindOf(media.mimeType, media.fileName),
        media.mimeType, media.fileName, media.base64);
      logger('INFO', `media downloaded for ${messageId} (${media.base64.length} chars)`);
```

with:

```js
      const kind = mediaKindOf(media.mimeType, media.fileName);
      const playable = await playableMedia(Buffer.from(media.base64, 'base64'), kind,
        media.mimeType, media.fileName);
      sendMediaChunks(chatId, messageId, kind, playable.mimeType, playable.fileName,
        playable.buffer.toString('base64'));
      logger('INFO', `media downloaded for ${messageId} (${media.base64.length} chars)`);
```

In `handleWebhookEvent`, after the `fetchBinary` block and before `const inlineMedia = ...`, insert the conversion:

```js
    // Un vocale arriva Ogg/Opus e il telefono non lo legge: si converte prima
    // di spezzarlo verso l'app.
    if (mediaBuffer && fields.mediaType === 'audio') {
      const playable = await playableMedia(mediaBuffer, 'audio', mediaMimeType, fields.mediaFileName);
      mediaBuffer = playable.buffer;
      mediaMimeType = playable.mimeType;
      fields.mediaFileName = playable.fileName;
    }
```

Replace `mediaKindOf` with:

```js
  /// La strada giusta per un allegato, dal tipo MIME (o dall'estensione quando
  /// il tipo non c'e'): image, video, audio, altrimenti document. Il tipo che
  /// ne esce viaggia anche verso l'app, che da esso decide come disegnare la
  /// bolla (vedi ChatMessage.IsAudio / IsDocument).
  function mediaKindOf(mimeType, fileName) {
    const mime = String(mimeType || '').toLowerCase();
    const name = String(fileName || '').toLowerCase();
    if (mime.indexOf('video/') === 0 || /\.(mp4|mov|3gp|avi|mkv|webm)$/.test(name)) return 'video';
    if (mime.indexOf('image/') === 0) return 'image';
    if (mime.indexOf('audio/') === 0 || /\.(ogg|opus|oga|mp3|m4a|aac|amr|wav)$/.test(name)) return 'audio';
    return 'document';
  }
```

Expose the probe in the returned object. In the `return { ... }` block, after `handleWebhookEvent,`:

```js
    probeFfmpeg: () => mediaTools.probe(),
```

Finally, in `main()`, after `const bridge = createBridge({ config, gowa, log, debug: dbg });`:

```js
  await bridge.probeFfmpeg();
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `cd WhatsappBridge && node --test test/config.test.js test/server.test.js test/message-format.test.js`
Expected: PASS.

- [ ] **Step 9: Run the whole adapter suite**

Run: `cd WhatsappBridge && npm test`
Expected: PASS. The image/unknown-attachment test still sends the PDF through `sendFile` because `mediaKindOf` returns `document` for it, and the routing only special-cases `video` and `image`.

- [ ] **Step 10: Commit**

```bash
git add WhatsappBridge/config.js WhatsappBridge/server.js WhatsappBridge/message-format.js \
  WhatsappBridge/test/config.test.js WhatsappBridge/test/server.test.js WhatsappBridge/test/message-format.test.js
git commit -m "$(cat <<'EOF'
feat: a voice note arrives as MP3 and a file as a document

Both media paths now name audio and documents, and the comporter runs the
transcoder before chunking an audio so the phone gets something it can play.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
git push origin master
```

---

### Task 3: Audio and documents are written to disk on the phone

`IncomingMediaStore` streams a video straight to a file in `LocalFolder` and holds a photo's base64 in memory. A document can be as large as a video and an audio must be handed to a `MediaElement` as a file, so both join the on-disk path. The store also needs an extension for each, because `ffmpeg`'s MP3 has no filename on the wire until now (it arrives as `voce.mp3`, so the extension comes from the name).

**Files:**
- Modify: `WhatsappApp/Services/IncomingMediaStore.cs`

**Interfaces:**
- Consumes: `StorageFile.CreateFileAsync`, `IRandomAccessStream`, `DataWriter` (already used).
- Produces: `IncomingMediaResult.LocalFileName` is now also set for `audio` and `document`; nothing else changes shape.

- [ ] **Step 1: Make the store persist every non-image kind**

In `WhatsappApp/Services/IncomingMediaStore.cs`, update the class comment to name the new kinds. Replace:

```csharp
    /// <summary>
    /// Un media ricevuto, completo. O i byte in base64 (immagine, si disegna
    /// subito) o il nome del file locale (video: i byte stanno su disco, perche'
    /// un video intero in memoria su un telefono da 512 MB non ci sta).
    /// </summary>
```

with:

```csharp
    /// <summary>
    /// Un media ricevuto, completo. O i byte in base64 (immagine, si disegna
    /// subito) o il nome del file locale (video, audio, documento: i byte stanno
    /// su disco, perche' un video o un documento interi in memoria su un telefono
    /// da 512 MB non ci stanno, e un lettore vuole un file).
    /// </summary>
```

Replace the `Pending.IsVideo` field with a general one. Replace:

```csharp
            public bool IsVideo;
```

with:

```csharp
            public bool ToDisk;
```

Replace the store comment's video sentence. Replace:

```csharp
    /// Un video si scrive su disco mentre arriva, un pezzo alla volta: tenere
    /// una base64 da decine di MB per poi decodificarla tutta insieme e' il modo
    /// piu' veloce per farsi chiudere l'app da un telefono da 512 MB. I pezzi
    /// sono multipli di 4 caratteri base64, quindi si decodificano da soli.
```

with:

```csharp
    /// Un video, un audio o un documento si scrivono su disco mentre arrivano,
    /// un pezzo alla volta: tenere una base64 da decine di MB per poi
    /// decodificarla tutta insieme e' il modo piu' veloce per farsi chiudere
    /// l'app da un telefono da 512 MB. I pezzi sono multipli di 4 caratteri
    /// base64, quindi si decodificano da soli.
```

In `AddChunkAsync`, replace both uses of `pending.IsVideo`. Replace:

```csharp
                if (pending.IsVideo)
                {
                    pending.Writer.WriteBytes(Convert.FromBase64String(frame.MediaData));
                    await pending.Writer.StoreAsync();
                }
```

with:

```csharp
                if (pending.ToDisk)
                {
                    pending.Writer.WriteBytes(Convert.FromBase64String(frame.MediaData));
                    await pending.Writer.StoreAsync();
                }
```

and replace:

```csharp
            if (pending.IsVideo)
            {
                await pending.Writer.FlushAsync();
```

with:

```csharp
            if (pending.ToDisk)
            {
                await pending.Writer.FlushAsync();
```

In `StartAsync`, replace:

```csharp
            bool video = string.Equals(frame.MediaType, "video", StringComparison.OrdinalIgnoreCase);

            var pending = new Pending
            {
                Total = total,
                // Il tipo dichiarato dal messaggio, quando c'e': un vecchio
                // media di un frame solo non lo dichiara, ed era un'immagine.
                MediaType = string.IsNullOrEmpty(frame.MediaType)
                    ? "image"
                    : frame.MediaType.ToLower(),
                MimeType = frame.MediaMimeType,
                IsVideo = video
            };

            if (!video)
            {
                pending.Base64 = new StringBuilder();
                return pending;
            }
```

with:

```csharp
            bool toDisk = ToDisk(frame.MediaType);

            var pending = new Pending
            {
                Total = total,
                // Il tipo dichiarato dal messaggio, quando c'e': un vecchio
                // media di un frame solo non lo dichiara, ed era un'immagine.
                MediaType = string.IsNullOrEmpty(frame.MediaType)
                    ? "image"
                    : frame.MediaType.ToLower(),
                MimeType = frame.MediaMimeType,
                ToDisk = toDisk
            };

            if (!toDisk)
            {
                pending.Base64 = new StringBuilder();
                return pending;
            }
```

Add `ToDisk` just before `StartAsync`:

```csharp
        /// <summary>
        /// I tipi che non stanno in memoria e vanno su un file: un video, un
        /// audio, un documento. Un'immagine si disegna subito da base64; uno
        /// sticker e' un'immagine.
        /// </summary>
        private static bool ToDisk(string mediaType)
        {
            return string.Equals(mediaType, "video", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "audio", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "document", StringComparison.OrdinalIgnoreCase);
        }
```

- [ ] **Step 2: Teach the extension map the new files**

In `ExtensionFor`, replace the fallback chain. Replace:

```csharp
            string mime = (mimeType ?? "").ToLower();
            if (mime.IndexOf("3gp") >= 0) return ".3gp";
            if (mime.IndexOf("quicktime") >= 0) return ".mov";
            if (mime.IndexOf("webm") >= 0) return ".webm";
            if (mime.IndexOf("matroska") >= 0) return ".mkv";
            if (mime.IndexOf("msvideo") >= 0) return ".avi";
            return ".mp4";
```

with:

```csharp
            string mime = (mimeType ?? "").ToLower();
            if (mime.IndexOf("3gp") >= 0) return ".3gp";
            if (mime.IndexOf("quicktime") >= 0) return ".mov";
            if (mime.IndexOf("webm") >= 0) return ".webm";
            if (mime.IndexOf("matroska") >= 0) return ".mkv";
            if (mime.IndexOf("msvideo") >= 0) return ".avi";
            // Audio: un vocale e' gia' un MP3 quando arriva qui (l'adapter lo
            // converte), ma il tipo si guarda lo stesso per gli altri.
            if (mime.IndexOf("mpeg") >= 0) return ".mp3";
            if (mime.IndexOf("audio/mp4") >= 0 || mime.IndexOf("mp4a") >= 0) return ".m4a";
            if (mime.IndexOf("amr") >= 0) return ".amr";
            if (mime.IndexOf("wav") >= 0) return ".wav";
            if (mime.IndexOf("ogg") >= 0 || mime.IndexOf("opus") >= 0) return ".ogg";
            if (mime.IndexOf("pdf") >= 0) return ".pdf";
            // Un documento porta sempre il suo nome, quindi qui ci arriva solo
            // un file senza nome: l'estensione la sceglie chi lo apre.
            return ".bin";
```

- [ ] **Step 3: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-memory.js && node tools/check-framing.js`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Services/IncomingMediaStore.cs
git commit -m "$(cat <<'EOF'
feat: receive an audio or a document to a file, like a video

A document can be as large as a video and the player wants a file, so both
join the on-disk path instead of holding their bytes in memory.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
git push origin master
```

---

### Task 4: A message knows it is an audio or a document

There is no `MessageType` for a document on the wire (the adapter sends `type` 0 with `MediaType` `"document"`), and adding one would change the deserialized contract for no gain. `IsVideo` already derives from `MediaType` as well as the type, so audio and document follow the same rule: a property that answers from either.

**Files:**
- Modify: `WhatsappApp/Models/ChatMessage.cs`
- Modify: `WhatsappApp/Services/DataService.cs`

**Interfaces:**
- Produces: `ChatMessage.IsAudio` and `ChatMessage.IsDocument` (bool, client derived).
- Produces: `DataService.ApplyMedia` sets `Type = MessageType.Audio` for a downloaded audio and leaves a document as text.

- [ ] **Step 1: Add the two properties and their change notifications**

In `WhatsappApp/Models/ChatMessage.cs`, in the `Type` setter, add `IsAudio` to the raised names. Replace:

```csharp
                OnPropertyChanged();
                OnPropertyChanged("IsVideo");
                OnPropertyChanged("ShowsText");
```

with:

```csharp
                OnPropertyChanged();
                OnPropertyChanged("IsVideo");
                OnPropertyChanged("IsAudio");
                OnPropertyChanged("ShowsText");
```

In the `MediaType` setter, do the same and add `IsDocument`. Replace:

```csharp
                _mediaType = value;
                OnPropertyChanged();
                OnPropertyChanged("IsVideo");
                OnPropertyChanged("ShowsText");
```

with:

```csharp
                _mediaType = value;
                OnPropertyChanged();
                OnPropertyChanged("IsVideo");
                OnPropertyChanged("IsAudio");
                OnPropertyChanged("IsDocument");
                OnPropertyChanged("ShowsText");
```

Then, after the `IsVideo` property, add:

```csharp
        /// <summary>
        /// Questa bolla e' un vocale o un audio. Vale anche prima che i byte
        /// arrivino: la riga di cronologia dichiara MediaType "audio".
        /// </summary>
        public bool IsAudio
        {
            get
            {
                return Type == MessageType.Audio
                    || string.Equals(MediaType, "audio", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Questa bolla e' un documento. Non c'e' un MessageType per un
        /// documento: il tipo dichiarato dal server (MediaType "document") e' la
        /// sola cosa che lo distingue da un messaggio di testo, e sul filo il
        /// testo e' gia' il nome del file.
        /// </summary>
        public bool IsDocument
        {
            get { return string.Equals(MediaType, "document", StringComparison.OrdinalIgnoreCase); }
        }
```

- [ ] **Step 2: Let the data service apply an audio and a document**

In `WhatsappApp/Services/DataService.cs`, in `ApplyMedia`, replace:

```csharp
                if (!string.IsNullOrEmpty(result.LocalFileName))
                {
                    // Un video: i byte stanno su disco, e il lettore li apre da
                    // li'. In memoria non ci starebbero.
                    target.MediaFilePath = result.LocalFileName;
                    target.Type = MessageType.Video;
                }
```

with:

```csharp
                if (!string.IsNullOrEmpty(result.LocalFileName))
                {
                    // Video, audio o documento: i byte stanno su disco e il
                    // lettore (o l'app di sistema) apre il file da li'. In
                    // memoria non ci starebbero.
                    target.MediaFilePath = result.LocalFileName;
                    if (string.Equals(result.MediaType, "video", StringComparison.OrdinalIgnoreCase))
                        target.Type = MessageType.Video;
                    else if (string.Equals(result.MediaType, "audio", StringComparison.OrdinalIgnoreCase))
                        target.Type = MessageType.Audio;
                    // Un documento resta testo: la sua bolla e' il nome del file.
                }
```

- [ ] **Step 3: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-memory.js && node tools/check-actions.js`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Models/ChatMessage.cs WhatsappApp/Services/DataService.cs
git commit -m "$(cat <<'EOF'
feat: a message knows it is an audio or a document

Audio and document derive from the declared media type, the way a video
already does, so the bubble can offer the right action.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
git push origin master
```

---

### Task 5: A play bar for audio and an open bar for a document in the bubble

The video bubble is a dark box with a play triangle and a download ring. Audio and documents get the same shape: an audio box that opens the player, and a document box that hands the file to the system. The tap handling is one branch of `Media_Tapped`, and the player is the existing `MediaElement`, which plays an MP3 as happily as a video.

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml`
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs`
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`
- Modify: `WhatsappApp/Strings/it-IT/Resources.resw`

**Interfaces:**
- Consumes (from Task 4): `ChatMessage.IsAudio`, `ChatMessage.IsDocument`, `ChatMessage.MediaFilePath`.
- Consumes: `Windows.System.Launcher.LaunchFileAsync(StorageFile)`, `Windows.UI.Popups.MessageDialog`.
- Produces: `ChatPage.PlayMedia(ChatMessage message, bool audio)`, `ChatPage.OpenDocumentAsync(ChatMessage message)`, `ChatPage.Downloadable` also accepts a file-backed audio and document.

- [ ] **Step 1: Add the two strings, in both languages**

In `WhatsappApp/Strings/en-US/Resources.resw`, after `ChatPage_VideoError`:

```xml
  <data name="ChatPage_AudioError" xml:space="preserve">
    <value>This voice note cannot be played.</value>
  </data>
  <data name="ChatPage_DocumentError" xml:space="preserve">
    <value>There is no app on this phone that can open this file.</value>
  </data>
```

In `WhatsappApp/Strings/it-IT/Resources.resw`, in the same place (same keys, same order):

```xml
  <data name="ChatPage_AudioError" xml:space="preserve">
    <value>Questo vocale non si puo' riprodurre.</value>
  </data>
  <data name="ChatPage_DocumentError" xml:space="preserve">
    <value>Su questo telefono non c'e' un'app che apra questo file.</value>
  </data>
```

- [ ] **Step 2: Add the two bars, in both bubbles**

In `WhatsappApp/Pages/ChatPage.xaml`, there are two identical video `Border`s (one per bubble), each immediately before that bubble's `TextBlock`. After **each** of them, insert an audio bar and a document bar. The audio bar:

```xml
                                <!-- Un vocale o un audio: una barra scura con il triangolo,
                                     che si tocca per ascoltarlo. Prima dei byte e' lo stesso
                                     tocco a chiederli. -->
                                <Border CornerRadius="4"
                                        Margin="0,0,0,4"
                                        Width="220" Height="56"
                                        Background="#FF263238"
                                        Visibility="{Binding IsAudio, Converter={StaticResource BoolToVisibility}}"
                                        Tapped="Media_Tapped">
                                    <Grid>
                                        <Path Fill="White" Width="30" Height="30"
                                              HorizontalAlignment="Center" VerticalAlignment="Center">
                                            <!-- IconPlay -->
                                            <Path.Data>
                                                <PathGeometry>
                                                    <PathGeometry.Figures>
                                                        <PathFigure StartPoint="6,3" IsClosed="True">
                                                            <PathFigure.Segments>
                                                                <PolyLineSegment Points="22,12 6,21"/>
                                                            </PathFigure.Segments>
                                                        </PathFigure>
                                                    </PathGeometry.Figures>
                                                </PathGeometry>
                                            </Path.Data>
                                        </Path>
                                        <ProgressRing IsActive="True" Width="24" Height="24"
                                                      Foreground="White"
                                                      HorizontalAlignment="Center" VerticalAlignment="Center"
                                                      Visibility="{Binding IsMediaLoading, Converter={StaticResource BoolToVisibility}}"/>
                                    </Grid>
                                </Border>
```

The document bar:

```xml
                                <!-- Un documento: una barra scura con un foglio, che si
                                     tocca per aprirlo con l'app di sistema. Prima dei byte
                                     e' lo stesso tocco a chiederli. -->
                                <Border CornerRadius="4"
                                        Margin="0,0,0,4"
                                        Width="220" Height="56"
                                        Background="#FF263238"
                                        Visibility="{Binding IsDocument, Converter={StaticResource BoolToVisibility}}"
                                        Tapped="Media_Tapped">
                                    <Grid>
                                        <Path Fill="White" Width="30" Height="30"
                                              HorizontalAlignment="Center" VerticalAlignment="Center">
                                            <!-- IconDocument -->
                                            <Path.Data>
                                                <PathGeometry>
                                                    <PathGeometry.Figures>
                                                        <PathFigure StartPoint="5,2" IsClosed="True">
                                                            <PathFigure.Segments>
                                                                <LineSegment Point="13,2"/>
                                                                <LineSegment Point="19,8"/>
                                                                <LineSegment Point="19,22"/>
                                                                <LineSegment Point="5,22"/>
                                                            </PathFigure.Segments>
                                                        </PathFigure>
                                                    </PathGeometry.Figures>
                                                </PathGeometry>
                                            </Path.Data>
                                        </Path>
                                        <ProgressRing IsActive="True" Width="24" Height="24"
                                                      Foreground="White"
                                                      HorizontalAlignment="Center" VerticalAlignment="Center"
                                                      Visibility="{Binding IsMediaLoading, Converter={StaticResource BoolToVisibility}}"/>
                                    </Grid>
                                </Border>
```

- [ ] **Step 3: Route the taps and play or open the file**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, add the two `using` lines at the top with the others:

```csharp
using Windows.System;
using Windows.UI.Popups;
```

Replace `Downloadable` with a version that accepts every file-backed kind:

```csharp
        /// <summary>
        /// Un media di questa conversazione che non ha ancora i byte: e' una
        /// riga di cronologia (o un video di cui l'adapter non aveva il file),
        /// e si puo' chiedere al server. Il tipo dice che era un'immagine, un
        /// video, un audio o un documento; si possono chiedere tutti.
        /// </summary>
        private static bool Downloadable(ChatMessage message)
        {
            if (message == null) return false;

            // Solo cio' che e' arrivato da fuori ha un id che il server
            // conosce: un messaggio scritto qui porta un id locale, e
            // chiederlo al server sarebbe una richiesta senza risposta.
            if (!message.IsIncoming) return false;

            if (IsFileBacked(message.MediaType))
                return string.IsNullOrEmpty(message.MediaFilePath);

            if (string.Equals(message.MediaType, "image", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrEmpty(message.MediaData);

            return false;
        }

        /// <summary>I tipi che arrivano su un file: un video, un audio, un documento.</summary>
        private static bool IsFileBacked(string mediaType)
        {
            return string.Equals(mediaType, "video", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "audio", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "document", StringComparison.OrdinalIgnoreCase);
        }
```

In `Media_Tapped`, replace the video branch and the image tail. Replace:

```csharp
            if (message.IsVideo)
            {
                if (!string.IsNullOrEmpty(message.MediaFilePath))
                {
                    PlayVideo(message);
                    return;
                }

                if (Downloadable(message)) RequestMedia(message);
                return;
            }

            if (!string.IsNullOrEmpty(message.MediaData))
```

with:

```csharp
            if (message.IsVideo)
            {
                if (!string.IsNullOrEmpty(message.MediaFilePath))
                {
                    PlayMedia(message, false);
                    return;
                }

                if (Downloadable(message)) RequestMedia(message);
                return;
            }

            if (message.IsAudio)
            {
                if (!string.IsNullOrEmpty(message.MediaFilePath))
                {
                    PlayMedia(message, true);
                    return;
                }

                if (Downloadable(message)) RequestMedia(message);
                return;
            }

            if (message.IsDocument)
            {
                if (!string.IsNullOrEmpty(message.MediaFilePath))
                {
                    await OpenDocumentAsync(message);
                    return;
                }

                if (Downloadable(message)) RequestMedia(message);
                return;
            }

            if (!string.IsNullOrEmpty(message.MediaData))
```

Rename and generalise `PlayVideo`. Replace:

```csharp
        /// <summary>
        /// Apre il video ricevuto nel lettore a tutto schermo. La sorgente e' il
        /// file locale (ms-appdata): il lettore lo apre per conto suo e non c'e'
        /// nessun flusso da tenere aperto per la vita della pagina.
        /// </summary>
        private void PlayVideo(ChatMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.MediaFilePath)) return;

            try
            {
                StopVideo();
                VideoPlayer.Source = new Uri("ms-appdata:///local/" + message.MediaFilePath);
                VideoViewer.Visibility = Visibility.Visible;
                VideoPlayer.Play();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.PlayVideo", ex);
                ShowVideoError();
            }
        }
```

with:

```csharp
        // Vero quando cio' che suona e' un vocale: la scena e' la stessa, ma
        // la frase di errore no.
        private bool _playingAudio;

        /// <summary>
        /// Apre il media ricevuto nel lettore a tutto schermo. La sorgente e' il
        /// file locale (ms-appdata): il lettore lo apre per conto suo e non c'e'
        /// nessun flusso da tenere aperto per la vita della pagina. Vale per un
        /// video e per un vocale: per un audio la scena e' nera e restano i
        /// controlli di trasporto, che sono quelli di sistema.
        /// </summary>
        private void PlayMedia(ChatMessage message, bool audio)
        {
            if (message == null || string.IsNullOrEmpty(message.MediaFilePath)) return;

            try
            {
                StopVideo();
                _playingAudio = audio;
                VideoPlayer.Source = new Uri("ms-appdata:///local/" + message.MediaFilePath);
                VideoViewer.Visibility = Visibility.Visible;
                VideoPlayer.Play();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.PlayMedia", ex);
                ShowVideoError();
            }
        }

        /// <summary>
        /// Apre un documento ricevuto con l'app che il telefono usa per quel
        /// tipo di file. Se non ce n'e' una, o il file non e' piu' li', lo dice
        /// invece di non fare niente.
        /// </summary>
        private async System.Threading.Tasks.Task OpenDocumentAsync(ChatMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.MediaFilePath)) return;

            // C# 5 non lascia attendere dentro un catch (CS1985): si prende nota
            // del guasto e si aspetta dopo, fuori dal blocco.
            bool failed = false;
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(message.MediaFilePath);
                bool opened = await Launcher.LaunchFileAsync(file);
                failed = !opened;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.OpenDocumentAsync", ex);
                failed = true;
            }

            if (failed) await ShowDocumentErrorAsync();
        }

        private async System.Threading.Tasks.Task ShowDocumentErrorAsync()
        {
            var dialog = new MessageDialog(Loc.Get("ChatPage_DocumentError",
                "There is no app on this phone that can open this file."));
            await dialog.ShowAsync();
        }
```

In `ShowVideoError`, pick the sentence from the thing being played. Replace:

```csharp
            VideoErrorText.Text = Loc.Get("ChatPage_VideoError", "This video cannot be played.");
```

with:

```csharp
            VideoErrorText.Text = _playingAudio
                ? Loc.Get("ChatPage_AudioError", "This voice note cannot be played.")
                : Loc.Get("ChatPage_VideoError", "This video cannot be played.");
```

In `StopVideo`, clear the flag. Replace:

```csharp
            VideoErrorText.Visibility = Visibility.Collapsed;
            VideoViewer.Visibility = Visibility.Collapsed;
```

with:

```csharp
            _playingAudio = false;
            VideoErrorText.Visibility = Visibility.Collapsed;
            VideoViewer.Visibility = Visibility.Collapsed;
```

- [ ] **Step 4: Run the guards**

Run:
`node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-actions.js && node tools/check-memory.js`
Expected: PASS, with `18 inline icon Path(s), 11 distinct icon(s)` and `110 key(s)` in both languages.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Pages/ChatPage.xaml.cs \
  WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw
git commit -m "$(cat <<'EOF'
feat: play a voice note and open a document from the bubble

An audio opens in the existing player and a document goes to the system app
that handles it, so a voice note and a file stop being a word.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
git push origin master
```

---

### Task 6: Document the dependency and record the lessons

`ffmpeg` is a new thing to install on the adapter host and a new failure mode (absent), so both READMEs say so, the adapter README's environment table gains the two variables, and the skill records why the app cannot decode Ogg/Opus. The on-device checklist gains the two checks that only a phone can answer.

**Files:**
- Modify: `README.md`, `README.it.md`
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`
- Modify: `.agents/skills/maintain-the-app/SKILL.md`
- Modify: `.agents/skills/test-the-app/SKILL.md`
- Modify: `WhatsappBridge/.env.example` (if it exists; the README table is the source of truth otherwise)

**Interfaces:** none.

- [ ] **Step 1: Document the adapter dependency**

In `WhatsappBridge/README.md` and `WhatsappBridge/README.it.md`, in the environment table (near the rows for `CHATS_AVATARS` and `MESSAGES_LIMIT`), add:

- `FFMPEG_ENABLED` — default `on`. Converts Ogg/Opus voice notes to MP3 for WP8.1.
- `FFMPEG_PATH` — default `ffmpeg`. The executable, when it is not on the PATH.

Add a short paragraph above or below the table, in both languages: WhatsApp voice notes are Ogg/Opus; Windows Phone 8.1 has no Opus decoder, so the adapter runs `ffmpeg` (an external program, not a dependency) to convert them to MP3; without `ffmpeg` the adapter logs a warning at startup and forwards the original bytes, which the phone cannot play. If `.env.example` exists, add the two variables in both of the same file.

- [ ] **Step 2: Document the feature in the project READMEs**

In `README.md` and `README.it.md`, in the section that describes what the app does with media (the one that already mentions images and video), add: a received voice note or audio plays in the app, and a received document opens with the phone's own app; a document that is not downloaded yet is fetched on tap, like a photo or a video. Keep the heading order and depth identical in both files.

- [ ] **Step 3: Record the lesson**

In `.agents/skills/maintain-the-app/SKILL.md`, in `## Known gotchas`, after the incoming-media gotcha, add a bullet. Use this text (no emoji):

```markdown
- **WP8.1 has no Opus decoder.** WhatsApp voice notes are Ogg/Opus, and Opus
  only arrived on Windows 10: the phone can play MP3, AAC/M4A, AMR and WAV, and
  cannot play what WhatsApp actually sends. The adapter therefore detects
  `ffmpeg` once at startup (`WhatsappBridge/ffmpeg.js`, `bridge.probeFfmpeg()`)
  and converts an Ogg/Opus payload to mono 16 kHz 32 kbit/s MP3 before chunking
  it to the app. `ffmpeg` is an external program, not an npm dependency, and the
  adapter must work without it: `toPlayable` returns `null`, the original bytes
  are sent, and the app shows that the note cannot be played. `FFMPEG_ENABLED`
  and `FFMPEG_PATH` change it. Audio and documents are `MediaType` values, not
  `MessageType` values: `MessageType` has no `Document` and adding one would
  change what every older frame deserializes to, so `ChatMessage.IsAudio` and
  `ChatMessage.IsDocument` derive from `MediaType` the way `IsVideo` does.
```

- [ ] **Step 4: Add the on-device checks**

In `.agents/skills/test-the-app/SKILL.md`, continue the numbered on-device checklist (the last numbers used are 40-44) with:

```markdown
45. A voice note received from WhatsApp shows a play bar, and tapping it plays
    the note. With `ffmpeg` absent on the adapter host, the same note says it
    cannot be played instead of staying silent.
46. A document received from WhatsApp shows a document bar and the file name;
    tapping it opens the phone's viewer, or says there is no app for it.
```

- [ ] **Step 5: Run the docs guard**

Run: `node tools/check-docs.js`
Expected: PASS, the two pairs still in step and no emoji.

- [ ] **Step 6: Commit**

```bash
git add README.md README.it.md WhatsappBridge/README.md WhatsappBridge/README.it.md \
  .agents/skills/maintain-the-app/SKILL.md .agents/skills/test-the-app/SKILL.md
git commit -m "$(cat <<'EOF'
docs: record the ffmpeg voice-note dependency and the media lessons

The adapter needs an optional external program for Ogg/Opus and the phone
cannot decode it, so both readers and the skill now say so.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
git push origin master
```

---

## Self-Review

**Spec coverage.** "Make voice notes/audio playable": the adapter converts Ogg/Opus to MP3 (Task 1, Task 2) and the app plays the file (Task 3 stores it, Task 5 offers the play bar and opens the player). "Make documents viewable": the adapter names them and downloads them (Task 2), the app stores them to disk (Task 3), shows a document bar and opens them with the system (Task 5). The user's chosen strategy — transcode with `ffmpeg` — is Task 1, and the graceful fallback when it is absent is `toPlayable` returning `null` plus a startup log (Task 1, Task 2, documented in Task 6).

**Placeholders.** No step says "implement later" or leaves a signature undefined; every step carries the code or the exact command.

**Type consistency.** `IsAudio` / `IsDocument` are added in `ChatMessage` (Task 4) and consumed only in `ChatPage` (Task 5). `MediaFilePath` already exists and is reused unchanged for the two new kinds. `IncomingMediaResult.LocalFileName` keeps its name and gains two producers. On the adapter, `mediaKindOf` returns the four strings the app's `MediaType` checks expect (`video`, `image`, `audio`, `document`), and `createTranscoder(...).toPlayable` returns `{ buffer, mimeType, fileName }`, the exact shape `playableMedia` forwards to `sendMediaChunks`.

**Open risks, named rather than hidden.**
- `Launcher.LaunchFileAsync` on a file in `LocalFolder` can return `false` or throw on a phone with no handler for the type; Task 5 catches both and shows `ChatPage_DocumentError`, and check 46 is a device check because this cannot be verified off-device.
- The document icon is a new geometry; Task 5 Step 4 runs `check-icons.js` and the expected count is stated (11 distinct) so a mismatch is caught immediately.
- A `MediaElement` shows a black scene while an audio plays. That is deliberate — it reuses the tested player and the system transport controls instead of a second, bespoke one — and check 45 confirms it on the phone.
