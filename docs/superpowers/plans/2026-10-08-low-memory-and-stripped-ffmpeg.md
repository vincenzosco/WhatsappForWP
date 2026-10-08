# Low memory, and an ffmpeg that only does what this phone needs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A 512 MB phone keeps a bounded amount of a conversation in memory, and the adapter can run a stripped ffmpeg (audio codecs only, video when the build has it) while telling the truth about what that build can do.

**Architecture:** Two halves that meet at `ffmpeg`. The adapter's transcoder stops assuming that "ffmpeg runs" means "ffmpeg can do the job": it probes the encoders, muxers and decoders it actually calls and reports `{ audio, video }`, so a stripped binary is a supported configuration and not a silent failure. The app stops letting a single open conversation grow without a ceiling, and `check-memory.js` holds that ceiling. The stripped build itself is a documented recipe plus `FFMPEG_PATH`, because what the adapter needs to *trust* a stripped binary is the capability probe, not the binary.

**Tech Stack:** dependency-free Node.js (CommonJS) with `node:test` on the adapter side; `ffmpeg` as an external program on the machine that runs the adapter; C# 5 / WP8.1 on the app side.

**Spec:** the user's request of 2026-10-08, verbatim, is the authority:

> ottimizza ancora un po per device con poca memoria (512mb), inoltre se riesci, prova a portare una versione strippata, solo audio e anche video se ti serve per il device come fallback

Clarified with the user the same day: the stripped version is a **reduced ffmpeg for the adapter** (audio codecs, plus the video codecs when they are needed), not a second app build; and the two halves ship together.

## Global Constraints

These hold for every task; they come from `.agents/skills/maintain-the-app/SKILL.md`.

- **C# 5 only.** No `$"..."`, no `?.`, no `get => x`, no `nameof`, no `out var`, no `await` directly inside a `catch` body. Gate: `node tools/check-csharp5.js`.
- **No hardcoded user-visible strings in the app.** UI text goes in both `.resw` files. Gate: `node tools/check-resw.js --strict`.
- **Runtime text outside the app UI is English** - adapter log lines, `error` frame text, `Diag` lines.
- **A silent `catch` is a bug.** Every survived failure goes through `Diag.Failed` (app) or a `logger(...)` call (adapter).
- **The adapter has zero runtime dependencies**; its tests must keep passing. Gate: `cd WhatsappBridge && npm test`.
- **Docker mirror.** Any commit touching `WhatsappBridge/` is mirrored into `vincenzosco/docker-whatsappforwp` with its `tools/sync.js`, then `--check`, `(cd server && npm test)`, commit, push.
- **Docs are written in pairs**, `README.md` + `README.it.md` and `WhatsappBridge/README.md` + `.it.md`, same headings in the same order, no emoji. Gate: `node tools/check-docs.js`.
- **Every `.md` stays below U+2190** - no arrows, no emoji beyond U+26A0; spell a relationship out in words.
- **A new source file is a project file too** - `<Compile Include>` in `WhatsappApp.csproj`. Gate: `node tools/check-project-files.js`.
- **This plan depends on the voice-note plan** `2026-10-08-voice-notes-play-and-send.md` having run its Task 2 first: that task installs `ffmpeg` and edits `WhatsappBridge/ffmpeg.js`'s `sendMedia` caller in `server.js`. This plan's Task 1 builds on the `ffmpeg.js` that Task 2 leaves behind. Do not start Task 1 before that.

## Review Focus

The five conditions this work will meet that no test above it pins, most likely first. Each gets its test in the task that owns the code.

1. **A stripped ffmpeg with no MP3 encoder** (a build made with only the Opus decoder, or a package whose `libmp3lame` is missing). Today `probe()` runs `-version`, sees it succeed, sets `available = true`, and every voice note then fails its transcode and silently returns `null` - the phone gets Ogg/Opus it cannot play and the server says nothing. It must be detected at startup and said in the log. *Task 1 Steps 1-4.*
2. **A build with audio but no video** (the audio-only stripped ffmpeg the request asks for). A video must still be sent unchanged, and no transcode must be attempted. *Task 1 Step 3, `toSmallerVideo` gated on the video capability.*
3. **A conversation that reaches the ceiling while it is open** (a busy group chat left open). The oldest messages must fall out of memory without the list breaking, and reopening the chat must still show them because the server sends the history again. *Task 3 Steps 3-5.*
4. **The ceiling crossing exactly at the boundary** (`Count == Max`, then one more). A loop that trims with `while`, not `if`, must handle a burst that adds several at once. *Task 3 Step 3.*
5. **A stripped build whose only fault is the video encoder**, used on a large video. The video goes out unchanged and the log says the build cannot compress it, rather than the send failing. *Task 1 Step 4.*

---

### Task 1: The transcoder reports what the binary can do

The adapter calls three ffmpeg products: an MP3 for a received voice note (encoder `libmp3lame`, muxer `mp3`), a smaller MP4 for a large video (encoder `libx264`, muxer `mp4`), and it reads Ogg/Opus (decoder `opus`). A stripped build may have any subset. `probe()` today only runs `-version` and then claims the whole job works, which is the defect a stripped build exposes.

**Files:**
- Modify: `WhatsappBridge/ffmpeg.js` (`createTranscoder`: `probe`, `isAvailable`, `toSmallerVideo`, a new `capabilities`)
- Modify: `WhatsappBridge/test/ffmpeg.test.js` (create it if it does not exist; otherwise add to it, following `WhatsappBridge/test/*.test.js` style)

**Interfaces:**
- Consumes: the `run(args, input)` injection and `logger(level, message)` that `createTranscoder` already takes; `TRANSCODE_ARGS`, `VIDEO_ARGS`.
- Produces:
  - `capabilities()` returns `{ audio: boolean, video: boolean }` - `audio` is true when the build can make an MP3 from Ogg/Opus, `video` when it can make a smaller MP4;
  - `isAvailable()` returns the **audio** capability (a build that cannot make an MP3 is not "available" for the voice notes, whatever else it can do);
  - `toSmallerVideo` returns `null` unless the **video** capability is true.

- [ ] **Step 1: Write the failing test for a stripped build**

In the adapter's ffmpeg test file, a fake `run` that answers each probe call with a realistic listing, in the shape the real ffmpeg prints (the encoder and muxer names appear as bare words on their own lines):

```js
test('un ffmpeg senza libmp3lame non e disponibile, e lo dice', async () => {
  const logs = [];
  const transcoder = createTranscoder({
    log: (level, message) => logs.push(level + ' ' + message),
    run: async (args) => {
      const key = args[args.length - 1];
      if (key === '-version') return 'ffmpeg version 6.1';
      if (key === '-encoders') return ' A..... libx264              H.264';
      if (key === '-muxers') return ' E  mp4  MP4';
      if (key === '-decoders') return ' A....D opus  Opus';
      return '';
    }
  });
  const ok = await transcoder.probe();
  assert.strictEqual(ok, false);
  assert.deepStrictEqual(transcoder.capabilities(), { audio: false, video: false });
  assert.ok(logs.some((line) => /WARN/.test(line) && /libmp3lame/.test(line)),
    'the log must name what the build is missing');
});

test('un ffmpeg con audio ma senza video serve solo i vocali', async () => {
  const transcoder = createTranscoder({
    run: async (args) => {
      const key = args[args.length - 1];
      if (key === '-version') return 'ffmpeg version 6.1';
      if (key === '-encoders') return ' A..... libmp3lame          MP3';
      if (key === '-muxers') return ' E  mp3  MP3';
      if (key === '-decoders') return ' A....D opus  Opus';
      return '';
    }
  });
  assert.strictEqual(await transcoder.probe(), true);
  assert.deepStrictEqual(transcoder.capabilities(), { audio: true, video: false });
});
```

- [ ] **Step 2: Run it and watch it fail**

Run: `cd WhatsappBridge && node --test test/ffmpeg.test.js`
Expected: FAIL - `capabilities is not a function`, and `probe` returns `true` for the first case.

- [ ] **Step 3: Implement the capability probe**

In `createTranscoder`, keep the `available` flag but add a `caps` object and a probe that reads the four listings. A helper that runs one listing and answers whether a word is present:

```js
let available = null;
let caps = { audio: false, video: false };

async function listing(args) {
  const text = await run(args, null);
  return Buffer.isBuffer(text) ? text.toString('utf8') : String(text || '');
}

async function canMake(encoder, muxer, decoder) {
  const encoders = await listing(['-hide_banner', '-encoders']);
  if (encoders.indexOf(encoder) < 0) return { ok: false, missing: encoder };
  const muxers = await listing(['-hide_banner', '-muxers']);
  if (muxers.indexOf(muxer) < 0) return { ok: false, missing: muxer };
  const decoders = await listing(['-hide_banner', '-decoders']);
  if (decoders.indexOf(decoder) < 0) return { ok: false, missing: decoder };
  return { ok: true, missing: '' };
}
```

`probe()` runs `['-version']` first (a binary that is not there throws, exactly as today), then `canMake('libmp3lame', 'mp3', 'opus')` for `caps.audio` and `canMake('libx264', 'mp4', 'none-present-in-a-stripped-build-is-fine')` - for video the decoder check is skipped, because the video path re-encodes an input it has already accepted, so pass a decoder word that is always present and is not checked. Concretely: give `canMake` a `null` decoder to skip that check. Log one line: `OK ffmpeg found: audio yes, video no` or `WARN ffmpeg cannot make MP3 (libmp3lame missing): received voice notes will not be playable on WP8.1`. `available = caps.audio`. The exact sentences are the plan's; write them in English.

- [ ] **Step 4: Gate the video path on the video capability**

In `toSmallerVideo`, replace the `available !== true` guard with `caps.video !== true`, so an audio-only build sends the video unchanged and never starts a transcode that would fail. Add the log line for the refused case is the caller's, not this method's; this method returns `null`, which is the existing contract.

- [ ] **Step 5: Run the tests and the adapter suite**

Run: `cd WhatsappBridge && node --test test/ffmpeg.test.js && npm test`
Expected: both new tests PASS; the suite `fail 0`. If a previous test asserted `isAvailable()` true after a `-version`-only fake `run`, update that fake to answer the four listings - the behaviour it tested is the one this task changes on purpose, and the note belongs in the ledger.

- [ ] **Step 6: Commit**

```bash
git add WhatsappBridge/ffmpeg.js WhatsappBridge/test/ffmpeg.test.js
git commit -m "fix: the adapter knows which codecs its ffmpeg actually has"
```

---

### Task 2: The stripped build is a documented recipe, and the mirror carries it

The request is to "bring" a stripped ffmpeg. The adapter runs whatever `FFMPEG_PATH` points at, so what it must be told is which configure flags produce an audio-only build and which add video, and which packages the distro ships with the encoder already present. This task writes those two paragraphs and points the start-up check at the capability line.

**Files:**
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md` (the existing `### Voice notes need ffmpeg` / `### I vocali hanno bisogno di ffmpeg` section)
- Modify: `.agents/skills/run-the-login-server/SKILL.md` (the start-up checks)

**Interfaces:**
- Consumes: `capabilities()` and the log sentences of Task 1.
- Produces: nothing code reads; the section and the skill line are the deliverable.

- [ ] **Step 1: Add the two recipes to the adapter README pair**

In the existing voice-note section, in both languages and the same order: (a) the audio-only build, a `./configure` line with the flags that keep just the pieces the voice notes use (`--disable-everything --disable-doc --disable-programs` plus `--enable-ffmpeg`, `--enable-protocol=pipe`, `--enable-demuxer=ogg`, `--enable-decoder=opus`, `--enable-encoder=libmp3lame`, `--enable-muxer=mp3`, `--enable-parser=opus`, `--enable-filter=aresample`); (b) the audio-and-video build, the same line plus `--enable-encoder=libx264 --enable-muxer=mp4 --enable-decoder=h264 --enable-parser=h264 --enable-filter=scale`, and note that `libmp3lame` and `libx264` need a build with `--enable-gpl --enable-libmp3lame --enable-libx264`; (c) one sentence that `FFMPEG_PATH` points the adapter at the stripped binary and that the adapter logs what the binary can do, so a missing encoder is visible at startup and not at the first voice note. Keep the existing promise sentence (the note still arrives and says it cannot be played).

- [ ] **Step 2: Point the start-up check at the capability line**

In `.agents/skills/run-the-login-server/SKILL.md`'s start-up checks, one line: after starting, read the adapter's first lines and expect `OK ffmpeg found: audio yes, video yes` (or `video no` for a stripped build); a `WARN ffmpeg cannot make MP3` means received voice notes will not play and the fix is an ffmpeg with `libmp3lame`.

- [ ] **Step 3: Run the docs gate**

Run: `node tools/check-docs.js`
Expected: `OK`; the pair still has the same headings in the same order.

- [ ] **Step 4: Mirror into the Docker repository and push**

The `WhatsappBridge/` change of Task 1 and this task's README change travel to `vincenzosco/docker-whatsappforwp`: `node tools/sync.js --from <this checkout>`, then `--check`, then `(cd server && npm test)`, then commit and push there. The container's `Dockerfile` installs the full ffmpeg; add one comment line there that a stripped build is supported through `FFMPEG_PATH` and is detected by the capability probe.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/README.md WhatsappBridge/README.it.md \
  .agents/skills/run-the-login-server/SKILL.md
git commit -m "docs: say how to build a stripped ffmpeg, and what the adapter does with it"
```

---

### Task 3: A conversation in memory has a ceiling

`DataService._chatMessages` grows without a bound: `AddMessage` and `AddHistoryMessage` only ever add. The adapter sends 50 history messages (`MESSAGES_LIMIT`), but every live message of a busy chat stays in memory for the session. On a 512 MB phone that is the one unbounded growth left. This task puts a ceiling on it and gives `check-memory.js` a rule that holds it.

**Files:**
- Modify: `WhatsappApp/Services/DataService.cs` (`AddMessage` around line 943, `AddHistoryMessage` around line 921, and a new constant near `_chatMessages` at line 31)
- Modify: `tools/check-memory.js` (a new rule)
- Modify: `tools/test/check-memory.test.js`

**Interfaces:**
- Consumes: `_chatMessages` (`Dictionary<string, ObservableCollection<ChatMessage>>`), `GetMessages(chatId)`.
- Produces: `private const int MaxMessagesPerChat = 200;` on `DataService`, and a `TrimToCeiling(ObservableCollection<ChatMessage>)` private helper both adders call after inserting.

- [ ] **Step 1: Write the failing guard test**

In `tools/test/check-memory.test.js`, in the existing style (`checkMemory`/`memoryProblems(read)` - match whatever the file already exports):

```js
test('la cronologia in memoria di una chat ha un tetto', () => {
  const broken = read(DATA_SERVICE).replace(/MaxMessagesPerChat\s*=\s*\d+/, 'MaxMessagesPerChat = 0');
  const found = memory.historyCeilingProblems(broken, DATA_SERVICE).join('\n');
  assert.match(found, /MaxMessagesPerChat/);
});

test('il tetto e applicato con un ciclo e non con un if', () => {
  const broken = read(DATA_SERVICE).replace(/while \(list\.Count > MaxMessagesPerChat\)/, 'if (list.Count > MaxMessagesPerChat)');
  const found = memory.historyCeilingProblems(broken, DATA_SERVICE).join('\n');
  assert.match(found, /while/);
});
```

- [ ] **Step 2: Run it and watch it fail**

Run: `node --test tools/test/check-memory.test.js`
Expected: FAIL - the rule does not exist, so `memoryProblems` returns no such sentence.

- [ ] **Step 3: Implement the ceiling in `DataService.cs`**

```csharp
/// <summary>
/// How many messages of one chat are kept in memory. The server still holds the
/// older ones: this is a ceiling on growth, not a deletion, and reopening the chat
/// asks for the history again. Without it a busy chat grows for as long as the
/// session lasts, which is the last unbounded allocation on a 512 MB phone.
/// </summary>
private const int MaxMessagesPerChat = 200;

private static void TrimToCeiling(ObservableCollection<ChatMessage> list)
{
    // while and not if: a burst can add several at once, and one trim per add
    // would leave the list over the ceiling until the next message.
    while (list.Count > MaxMessagesPerChat) list.RemoveAt(0);
}
```

Call `TrimToCeiling(_chatMessages[message.ChatId]);` at the end of `AddMessage` (after the `.Add`), and `TrimToCeiling(list);` at the end of `AddHistoryMessage` (after the `Insert`). The oldest fall out; the open chat is the one being appended to, so the fall is at the top of the list.

- [ ] **Step 4: Add the rule to `check-memory.js`**

A new `historyCeilingProblems(source, file)` restricted to `WhatsappApp/Services/DataService.cs`: fail when the file has no `MaxMessagesPerChat = <digits>`, or the number is below 100, and fail when the file does not contain `while (list.Count > MaxMessagesPerChat)`. Wire it into `main()`'s per-file loop and export it. The sentence names the file and says what a missing ceiling costs on a 512 MB phone.

- [ ] **Step 5: Run the tests and the guards that read these files**

Run: `node --test tools/test/check-memory.test.js && node tools/check-memory.js && node tools/check-csharp5.js && node tools/check-project-files.js`
Expected: the two new tests PASS; four `OK` lines.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Services/DataService.cs tools/check-memory.js tools/test/check-memory.test.js
git commit -m "fix: a chat in memory stops growing forever"
```

---

### Task 4: The gate, the README memory section, and the ledger

The two halves meet in one gate run and one pair of documents; this task is what makes them a change rather than two patches.

**Files:**
- Modify: `README.md`, `README.it.md` (the `### Memory on a 512 MB device` / `### Memoria su un dispositivo da 512 MB` section, and `### Voice notes need ffmpeg` if it makes a claim about codecs)
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md` (only if Task 2 left a number or a claim to correct)
- Record: `.superpowers/sdd/2026-10-08-low-memory-and-stripped-ffmpeg/progress.md` (gitignored)

**Interfaces:**
- Consumes: every task above.
- Produces: the run that closes the plan.

- [ ] **Step 1: Run the full gate**

```bash
node tools/check-csharp5.js && node tools/check-icons.js \
  && node tools/check-resw.js --strict && node tools/check-docs.js \
  && node tools/check-framing.js && node tools/check-tile.js \
  && node tools/check-memory.js && node tools/check-actions.js \
  && node tools/check-fire-and-forget.js && node tools/check-project-files.js \
  && node tools/check-chat-list-source.js && node tools/check-diagnostics.js \
  && node tools/check-handshake-answer.js && node tools/check-xaml-names.js
node --test "tools/test/**/*.test.js"
cd WhatsappBridge && npm test
```

Expected: the guards all `OK` and exit 0 (fifteen once the voice plan's `check-attachment-file.js` exists); the tools suite with its known four `download.test.js` failures (no `zip` on this host); the adapter suite `fail 0`.

- [ ] **Step 2: Say the new ceiling in the README pair**

In the memory section of both languages, add one line: a conversation kept in memory is bounded at 200 messages, older ones fall out and come back from the server when the chat is reopened; `check-memory.js` fails the build if the ceiling or its `while` trim is removed. Keep the existing three bullets intact.

- [ ] **Step 3: Build ARM, to prove the app still compiles**

```bash
(MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/Bin/MSBuild.exe" WhatsappApp.sln /t:Rebuild \
  /p:Configuration=Debug /p:Platform=ARM /v:m > .build.log 2>&1 &)
# poll for "Build succeeded"/"Build FAILED"; expect 0 warning CS / 0 error CS
git checkout -- WhatsappApp/Package.appxmanifest
rm .build.log
```

- [ ] **Step 4: Record the run and close the plan**

Append every task to the ledger: the commit, the commands run and their output, and any divergence. Then append a `## What execution changed about this plan` section to this document and do not rewrite the tasks above it. Note in the ledger that the on-device memory number (`DIAG ok: memory budget N MB`, and the `bound N rows, memory M MB` line of a chat with more than 200 messages) is the one check this plan cannot make without the phone, because it was disconnected.

- [ ] **Step 5: Commit and push**

```bash
git add README.md README.it.md
git commit -m "docs: say what a chat in memory is bounded to on a 512 MB phone"
git push origin master
```

---

## What execution changed about this plan

The two halves shipped and the tree is where the plan said it would be. Four differences, then
what is still owed.

1. **Task 1 named the helper `hasPieces`, not the plan's `canMake`, and reads the three listings
   once.** The plan's Step 3 sketch has `canMake(encoder, muxer, decoder)` run `-encoders`,
   `-muxers` and `-decoders` itself, which would run each listing twice (once for audio, once for
   video). The implementation reads the three listings once and passes them in:
   `hasPieces(encoder, muxer, decoder, encoders, muxers, decoders)`. Behaviour is unchanged and
   is what the plan fixes: `capabilities()` returns `{ audio, video }`, `isAvailable()` is the
   audio capability, `toSmallerVideo` returns `null` unless the video capability is true, and one
   line is logged at startup. The five Review Focus conditions each have their test in
   `WhatsappBridge/test/ffmpeg.test.js` (17 tests), including the no-MP3 build, the audio-only
   build, and the video-only-fault build.

2. **Task 2 was split across two commits and two repositories.** The README pair and the skill
   line landed in `a175654` (the same commit as the voice plan's Task 2, because both edit the
   adapter README's voice-note area). The mirror is `vincenzosco/docker-whatsappforwp` commit
   `4fb5684`, after `node tools/sync.js --from ...`, `--check` (`OK: server/ matches the adapter
   (35 file(s))`) and `(cd server && npm test)` (285/285). The plan's Step 5 commit message was
   used for the app side only; the mirror has its own `sync:` subject because that is that
   repository's convention.

3. **Task 3's new tests reuse the `DATA_SERVICE` constant the test file already declares.** The
   plan's Step 1 snippet is silent about it; `tools/test/check-memory.test.js` already has
   `const DATA_SERVICE = 'WhatsappApp/Services/DataService.cs';` at line 74, so declaring it a
   second time would have been a `SyntaxError` on the whole file. The four new tests use the
   existing constant.

4. **Task 4's on-device memory number could not be read.** The ARM build succeeded (`BUILD-EXIT=0`,
   one package created, 0 `warning CS` / 0 `error CS`), the fifteen guards exit 0, the tools suite
   is 123 / 118 pass / 4 fail / 1 skip, and the adapter suite is 285/285; but the Lumia is not
   detected (`ISETool.exe ts de ...` prints `Errore: ... non e' stato rilevato alcun telefono
   Windows Phone`), so neither `DIAG ok: memory budget N MB` nor the `bound N rows, memory M MB`
   line of a chat past 200 messages exists.

### What is still owed

- **The on-device memory number**: a run with a chat past 200 messages, and the `bound` and
  `memory` lines read from `diag.log`. The `while` trim and the 200-message ceiling are pinned by
  `check-memory.js` and the four new tests, but the actual megabytes on a 512 MB phone are
  unmeasured.
- **`ffmpeg` on the machine**: the probe and the stripped-build recipe are done; the binary that
  makes received voice notes play here is not installed (the install needs an elevated shell).
- The four `download.test.js` failures are the missing `zip` on this host, not this change.
## The on-device memory numbers of 2026-10-08

The 512 MB Lumia ran the ARM build with the message ceiling in place, and the figures the plan
asked for are now on disk (`.tools/voice-device/IsolatedStore/diag.log`, run started 21:16:44, and
`.tools/voice-device2/IsolatedStore/diag.log`, run started 21:44:27):

- `ok: memory budget 185 MB` - the first line, as checklist item 31 expects, and about half of what
  a 1 GB phone reports;
- `ok: chat list: 21 row(s), showing 21, memory 16 MB` through `21 MB` across the refreshes;
- `ok: bound 50 rows, memory 19 MB` in the first run and `21 MB` in the second, for an opened
  conversation;
- no `memory under pressure` line and no `ChatPage/...` failure, so the app was not closed and no
  decoded image had to be dropped on either run.

The ceiling itself was not reached: 50 messages bound is well under `MaxMessagesPerChat` (200), so
the trim did not fire. What the run proves is the budget and the steady memory across a chat list
and an opened conversation on the target device; the trim's own test remains the guard
(`check-memory.js`, `historyCeilingProblems`).
