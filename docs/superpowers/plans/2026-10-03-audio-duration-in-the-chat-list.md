# Audio Duration In The Chat List Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The chat-list preview of a conversation whose last message is a voice note reads `Audio 0:10` instead of `[Audio]`.

**Architecture:** GOWA does not expose an audio duration anywhere - `/chat/:jid/messages` (`MessageInfo`: `media_type`, `filename`, `url`, `file_length`, no duration) and the webhook payload (`audio` is only a path) both lack it. The adapter therefore derives it from the bytes: it downloads the last message's audio through the route it already uses for media (`/message/:id/download`, via `gowa.downloadMedia`) and reads the duration out of the container in pure Node - Ogg (Opus and Vorbis), MP4/M4A and MP3 - with no new dependency. The result is cached per message id, because the chat list is rebuilt on every reconnection and on the cache's minute. On the phone, only `ChatsPage` changes: the row preview is already `Contact.LastMessage`, written from the `chat` frame's `Text`, so the work is on the wire.

**Tech Stack:** Node.js 18+ (`node:test`) in `WhatsappBridge`; C# 5 / WinRT XAML on Windows Phone 8.1.

**Spec:** the request of 2026-10-03 - "nella preview delle chat fai in modo che si legga audio e poi eg 0:10 e non [Audio]" - and the choice made with the user: derive the duration from the bytes with a cache, and show it in the chat-list row.

## Global Constraints

- The adapter is a separate, dependency-free Node.js program (`package.json` has zero runtime dependencies): the parsers are written by hand, not pulled in.
- Adapter tests use `node:test` and the names and diagnostics stay Italian, like the rest of `WhatsappBridge/test`.
- Every `WhatsappBridge/` change is mirrored into `vincenzosco/docker-whatsappforwp` with its `tools/sync.js`, and both repositories are pushed.
- Gate before every commit: the eleven guards, `node --test "tools/test/**/*.test.js"`, `cd WhatsappBridge && npm test`.
- C# 5 only; no hardcoded user-visible strings (the label is adapter text, which is English by rule 8).
- Push is the rule: every task ends with `git push origin master` (and the mirror with `git push origin main`).
- No apostrophes in commit messages; no emoji except U+26A0.

## Review Focus

- **A voice note whose bytes cannot be downloaded** (an expired GOWA static, a message the adapter cannot read): the preview must stay `Audio`, not become empty or block the list. Task 3 owns it.
- **A message that is not audio**: text still wins, an image is still `[Image]`, and no download is attempted. Task 2 and Task 3 own it.
- **A container the parser does not know** (a codec GOWA hands back that is none of Ogg/MP4/MP3): no duration, the word stays. Task 1 owns it.
- **The chat list with fifty audio rows**: the downloads must be bounded by the per-message cache, not repeated on every list refresh. Task 3 owns it.
- **A duration that would be shown wrong** (a truncated file, a granule of `0xFFFFFFFFFFFFFFFF`): an implausible duration is dropped, never printed. Task 1 owns it.

---

### Task 1: Read a duration out of audio bytes

**Files:**
- Create: `WhatsappBridge/audio-duration.js`
- Create: `WhatsappBridge/test/audio-duration.test.js`

**Interfaces:**
- Produces: `durationSecondsOf(buffer, mimeType, fileName) -> number | null` (whole seconds, null when unknown), and `formatDuration(seconds) -> '0:10' | '1:05' | '12:00'`.
- Consumes: nothing.

- [ ] **Step 1: Write the failing test**

Create `WhatsappBridge/test/audio-duration.test.js`:

```js
'use strict';

const test = require('node:test');
const assert = require('node:assert');

const { durationSecondsOf, formatDuration } = require('../audio-duration');

/** A page OggS with one granule and a payload, for the parser tests. */
function oggPage(granule, payload) {
  const segments = [payload.length];
  const header = Buffer.alloc(27 + segments.length);
  header.write('OggS', 0, 'ascii');
  header.writeUInt8(0, 4);
  header.writeUInt8(4, 5);
  header.writeBigUInt64LE(BigInt(granule), 6);
  header.writeUInt32LE(1, 14);
  header.writeUInt32LE(0, 18);
  header.writeUInt8(segments.length, 26);
  header.writeUInt8(segments[0], 27);
  return Buffer.concat([header, payload]);
}

function opusHead(sampleRate) {
  const head = Buffer.alloc(19);
  head.write('OpusHead', 0, 'ascii');
  head.writeUInt8(1, 8);
  head.writeUInt8(2, 9);
  head.writeUInt16LE(312, 10);
  head.writeUInt32LE(sampleRate, 12);
  return head;
}

test('un Ogg/Opus prende la durata dalla granula finale', () => {
  const bytes = Buffer.concat([
    oggPage(0, opusHead(48000)),
    oggPage(480000, Buffer.from('audio'))
  ]);
  assert.strictEqual(durationSecondsOf(bytes, 'audio/ogg', 'voce.ogg'), 10);
});

test('una pagina non completata non conta', () => {
  const bytes = Buffer.concat([
    oggPage(0, opusHead(48000)),
    oggPage(480000, Buffer.from('audio')),
    oggPage(-1, Buffer.from('mezza'))
  ]);
  assert.strictEqual(durationSecondsOf(bytes, 'audio/ogg', 'voce.ogg'), 10);
});

test('un MP4/M4A prende la durata dal moov/mvhd', () => {
  const mvhd = Buffer.alloc(8 + 100);
  mvhd.writeUInt32BE(100, 0);
  mvhd.write('mvhd', 4, 'ascii');
  mvhd.writeUInt8(0, 8);
  mvhd.writeUInt32BE(0, 12);
  mvhd.writeUInt32BE(600, 20);
  mvhd.writeUInt32BE(6000, 24);
  const moov = Buffer.alloc(8);
  moov.writeUInt32BE(8 + mvhd.length, 0);
  moov.write('moov', 4, 'ascii');
  const ftyp = Buffer.alloc(8);
  ftyp.writeUInt32BE(8, 0);
  ftyp.write('ftyp', 4, 'ascii');
  assert.strictEqual(durationSecondsOf(Buffer.concat([ftyp, moov, mvhd]), 'audio/mp4', 'voce.m4a'), 10);
});

test('un MP3 conta i frame', () => {
  // MPEG1 Layer III, 128 kbit/s, 44100 Hz: 417 byte per frame, 1152 campioni.
  const frame = Buffer.alloc(417);
  frame.writeUInt8(0xff, 0);
  frame.writeUInt8(0xfb, 1);
  frame.writeUInt8(0x90, 2);
  frame.writeUInt8(0x00, 3);
  const bytes = Buffer.concat([frame, frame, frame, frame, frame]);
  // 5 frame * 1152 / 44100 = 0.1306 s -> 0
  assert.strictEqual(durationSecondsOf(bytes, 'audio/mpeg', 'voce.mp3'), 0);
});

test('i byte che non sono audio non danno una durata', () => {
  assert.strictEqual(durationSecondsOf(Buffer.from('non e audio'), 'audio/ogg', 'x.ogg'), null);
  assert.strictEqual(durationSecondsOf(Buffer.alloc(0), 'audio/ogg', 'x.ogg'), null);
  assert.strictEqual(durationSecondsOf(null, 'audio/ogg', 'x.ogg'), null);
});

test('formatDuration scrive minuti e secondi', () => {
  assert.strictEqual(formatDuration(10), '0:10');
  assert.strictEqual(formatDuration(65), '1:05');
  assert.strictEqual(formatDuration(720), '12:00');
  assert.strictEqual(formatDuration(0), '0:00');
});
```

- [ ] **Step 2: Run it to watch it fail**

Run: `cd WhatsappBridge && node --test test/audio-duration.test.js`
Expected: FAIL, `Cannot find module '../audio-duration'`.

- [ ] **Step 3: Implement the parsers**

Create `WhatsappBridge/audio-duration.js` with `durationSecondsOf` and `formatDuration`. Ogg: walk the pages from `OggS`, keep the last page whose granule is not `0xFFFFFFFFFFFFFFFF`, divide by the sample rate of the Opus/Vorbis identification header (Opus granules are always 48 kHz units). MP4: walk the top-level boxes to `moov`, then to `mvhd`, and read `timescale` and `duration` (the 64-bit form when `version` is 1). MP3: skip an `ID3v2` tag, then walk the frames from the `0xFFE` sync, summing `samplesPerFrame / sampleRate` (1152 for MPEG1 Layer III, 576 for MPEG2). Anything that does not parse returns null.

- [ ] **Step 4: Run the tests**

Run: `cd WhatsappBridge && node --test test/audio-duration.test.js`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/audio-duration.js WhatsappBridge/test/audio-duration.test.js
git commit -m "Read a duration out of the audio bytes"
```

---

### Task 2: The preview says the duration

**Files:**
- Modify: `WhatsappBridge/chats.js` (`MEDIA_LABEL`, `previewForMessage`)
- Modify: `WhatsappBridge/test/chats.test.js`

**Interfaces:**
- Consumes: `formatDuration(number) -> string` from Task 1.
- Produces: `previewForMessage(message, durationSeconds) -> string`; `MEDIA_LABEL.audio === 'Audio'`.

- [ ] **Step 1: Update the test**

In `WhatsappBridge/test/chats.test.js`, the media-label test becomes:

```js
test('previewForMessage names the media when the body is empty', () => {
  assert.strictEqual(previewForMessage({ media_type: 'image' }), '[Image]');
  assert.strictEqual(previewForMessage({ media_type: 'video' }), '[Video]');
  assert.strictEqual(previewForMessage({ media_type: 'audio' }), 'Audio');
  assert.strictEqual(previewForMessage({ media_type: 'audio' }, 10), 'Audio 0:10');
  assert.strictEqual(previewForMessage({ media_type: 'audio' }, 65), 'Audio 1:05');
  assert.strictEqual(previewForMessage({ content: 'ciao', media_type: 'audio' }, 10), 'ciao');
  assert.strictEqual(previewForMessage({ media_type: 'document' }), '[Document]');
  assert.strictEqual(previewForMessage({ media_type: 'sticker' }), '[Sticker]');
  assert.strictEqual(previewForMessage({}), '');
  assert.strictEqual(previewForMessage(null), '');
});
```

- [ ] **Step 2: Run it to watch it fail**

Run: `cd WhatsappBridge && node --test test/chats.test.js`
Expected: FAIL, `'[Audio]' !== 'Audio'`.

- [ ] **Step 3: Implement the label and the duration**

In `WhatsappBridge/chats.js`: change `MEDIA_LABEL.audio` to `'Audio'` and give `previewForMessage(message, durationSeconds)` the second parameter - the body still wins; for audio with a positive `durationSeconds` the label is `'Audio ' + formatDuration(durationSeconds)`; everything else is unchanged. Import `formatDuration` from `./audio-duration`.

- [ ] **Step 4: Run the tests**

Run: `cd WhatsappBridge && node --test test/chats.test.js`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/chats.js WhatsappBridge/test/chats.test.js
git commit -m "Let the preview of an audio say how long it is"
```

---

### Task 3: The chat list measures the last voice note

**Files:**
- Modify: `WhatsappBridge/chats.js` (`collectChats`, a duration cache)
- Modify: `WhatsappBridge/test/chats.test.js` (the `collectChats` case)

**Interfaces:**
- Consumes: `durationSecondsOf` (Task 1), `previewForMessage(message, durationSeconds)` (Task 2), `gowa.downloadMedia(chatId, messageId) -> { base64, mimeType, fileName } | null`.
- Produces: `collectChats` sets `preview` from the measured duration, and `createDurationCache({ ttlMs, maxEntries }) -> { get, put, size }`.

- [ ] **Step 1: Write the failing test**

In `WhatsappBridge/test/chats.test.js`, add to `fakeGowa` a `downloadMedia` that returns a prepared Ogg, and add a case: a chat whose newest message is an audio with an id gets `preview === 'Audio 0:10'`; a chat whose audio download fails gets `preview === 'Audio'`; a chat whose newest message is text never calls `downloadMedia`.

- [ ] **Step 2: Run it to watch it fail**

Run: `cd WhatsappBridge && node --test test/chats.test.js`
Expected: FAIL, `'Audio' !== 'Audio 0:10'`.

- [ ] **Step 3: Measure and cache**

In `collectChats`, when the newest message's `media_type` is `audio` and it has an `id`, ask the cache for the duration by message id; on a miss, download the bytes with `gowa.downloadMedia(chat.jid, last.id)` and measure them with `durationSecondsOf`, then remember the answer (a null answer too, so a failure is not retried on every list). Pass the duration to `previewForMessage`. The cache is `createDurationCache` in the same file: a `Map` with a TTL and a ceiling on entries, shaped like `avatar-cache.js`. Every failure is caught and logged at `DEBUG`, and the preview keeps the word.

- [ ] **Step 4: Run the whole adapter suite**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 0 fail.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/chats.js WhatsappBridge/test/chats.test.js
git commit -m "Measure the last voice note for the chat list"
```

---

### Task 4: The app's own label and the docs

**Files:**
- Modify: `WhatsappBridge/message-format.js` (`HISTORY_MEDIA_LABEL`, the audio fallback)
- Modify: `WhatsappBridge/test/message-format.test.js`
- Modify: `WhatsappBridge/README.md` and `WhatsappBridge/README.it.md`

**Interfaces:**
- Consumes: nothing.
- Produces: `mapWebhookMessage` and `mapHistoryMessage` use `Audio` for the bubble text, like the row.

- [ ] **Step 1: Update the tests**

In `WhatsappBridge/test/message-format.test.js`, the three assertions that expect `'[Audio]'` (the downloaded audio, the history row, and the fallback) become `'Audio'`.

- [ ] **Step 2: Run them to watch them fail**

Run: `cd WhatsappBridge && node --test test/message-format.test.js`
Expected: FAIL, `'[Audio]' !== 'Audio'`.

- [ ] **Step 3: Change the two labels**

In `WhatsappBridge/message-format.js`: `HISTORY_MEDIA_LABEL.audio` becomes `'Audio'`, and the audio branch of `mediaFromPayload` sets `result.fallbackText = 'Audio'`. The bubble text and the row now read the same word.

- [ ] **Step 4: Record it in both adapter READMEs**

In `WhatsappBridge/README.md` and `WhatsappBridge/README.it.md`, in the section that describes what the adapter sends for a media with no text, say that an audio is `Audio`, with its duration (`Audio 0:10`) in the chat-list preview when the bytes can be measured. Both languages, same heading, then `node tools/check-docs.js`.

- [ ] **Step 5: Run the tests and the gate**

Run: `cd WhatsappBridge && npm test`, then the eleven guards and the tool tests.
Expected: PASS, every guard `OK`.

- [ ] **Step 6: Commit and push, then mirror**

```bash
git add WhatsappBridge/message-format.js WhatsappBridge/test/message-format.test.js WhatsappBridge/README.md WhatsappBridge/README.it.md
git commit -m "Say Audio in the bubble too"
git push origin master
```

Then the mirror from Global Constraints: clone `vincenzosco/docker-whatsappforwp`, `node tools/sync.js --from <this checkout>`, `--check` until `OK: server/ matches the adapter`, `cd server && npm test`, commit, `git push origin main`.

---

## Self-Review

**1. Spec coverage.** "The preview must read Audio and then a duration" is Task 2 (the label and the format) and Task 3 (where the number comes from). The choice made with the user - derive from the bytes, cache, show in the row - is Task 1 (the parsers), Task 3 (the download and the cache) and nothing on the phone, because the row already shows `Text`. Task 4 keeps the bubble and the row consistent.

**2. Step scan.** Each step is one action with one checkable result: write the failing test, run it, implement, run it, commit. The parsers are one file because they are one question - how long are these bytes - and each container is a branch of the same function.

**3. Type consistency.** `durationSecondsOf(buffer, mimeType, fileName)` returns a whole number of seconds or null; `previewForMessage(message, durationSeconds)` takes that number and treats a non-positive one as absent; `createDurationCache` follows `createAvatarCache`'s shape (`get`, `put`, `size`), so `chats.js` reads the same way as `gowa-client.js`.

**4. Review Focus.** Each line names its owner: the failed download and the unknown container (Task 3 Step 3 and Task 1 Step 3), a message that is not audio (Task 2 Step 3), the repeated list refresh (Task 3 Step 3's cache), an implausible duration (Task 1 Step 3's null).

**5. Proportion.** The plan is shorter than the code it changes; the only body it fixes is the shape of the cache and the labels.

## What execution changed about this plan

- **Tasks 2 and 3 were written in one pass.** The label change and the measurement
  live in the same file and the same test, so they were committed as two commits
  (`chats.js`/`chats.test.js` once, then the bubble and the READMEs) rather than
  the four the plan describes. Nothing about the interface changed.
- **The MPEG version field was mapped backwards.** The first implementation read
  `1` as MPEG1, but the field is `3` = MPEG1, `2` = MPEG2, `0` = MPEG2.5, `1` =
  reserved. The failing test caught it before the commit.
- **The test helper writes the "no granule" page as all bits set.** `-1` is the
  granule of a page no packet ends on, and it is not a valid signed value:
  `BigInt(-1)` raised `ERR_OUT_OF_RANGE`, so the helper writes
  `0xffffffffffffffffn` instead.
- **The cache keeps a failure for two minutes, not thirty.** A download that did
  not work is worth retrying on the next list; a measured duration is not worth
  measuring again. `createDurationCache` has the two TTLs for that reason, and it
  is exported so the test can reuse one cache across two `collectChats` calls.
- **The mirror took 30 files**, one more than the 29 the previous sync recorded:
  `audio-duration.js` entered the list on its own, because `tools/sync.js` reads
  the adapter's `.js` files from the source instead of keeping a hand-written one.

## What the phone run should now show

1. A conversation whose last message is a voice note reads `Audio 0:10` in the list, and the number is the real length of the note.
2. A conversation whose last message is text, an image or a document is unchanged.
3. When the bytes cannot be fetched, the row reads `Audio` - never empty, and the list still arrives.

## What this needs to be seen

The change is on the wire and not in the app: the row already shows the `Text` of
its `chat` frame. Two things must be true before the phone shows it:

1. The NAS must run the new adapter - the Docker image rebuilt from
   `vincenzosco/docker-whatsappforwp` (`d6e31e7`) - or the row keeps `[Audio]`.
2. The bytes of the last voice note must still be under GOWA's `statics`. A note
   whose file is gone reads `Audio`, which is the designed answer.
