# Voice notes: playing, hearing, and being told the truth about a send Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A received voice note plays in a bubble whose play glyph becomes a pause glyph, the sender's own voice notes each play their own recording, and an attachment that WhatsApp did not take stops showing a sent checkmark.

**Architecture:** Three independent defects, one per symptom, plus one environment defect that decides the first. The adapter gains one new answer (`attachment.sent`) and one new refusal path, because today a send that reaches GOWA and a send whose bytes never left the phone look identical to the app. The app stops deriving a message's status from `IsConnected` and starts taking it from that answer, tied to the bubble through `RelatedMessageId`, which the app already sends for every other per-message frame but not for an attachment. The sender's own recording stops sharing one file name with every other attachment, and the one voice player stops trusting a file name it has seen before.

**Tech Stack:** C# 5 / WP8.1 XAML on the app side; dependency-free Node.js (CommonJS) with `node:test` on the adapter side; `ffmpeg` as an external program on the machine running the adapter.

**Spec:** the user's report of 2026-10-08, verbatim, is the authority — there is no separate design document:

> i messaggi vocali vengono inviati, ma non riesce a cambiare da not playing a playing quelli madati da un altro device, e quelli che ho mandato dal device, si mandano, ma cliccando parte ma non si sente niente, inoltre controllando, sul device da la spunta, ma non vedo niente sulla chat del contatto

Read as three symptoms:

1. **Received voice notes** never change from "not playing" to "playing".
2. **Voice notes sent from this phone** are sent, but tapping one starts playing with no sound.
3. A sent voice note shows the checkmark on the phone while **the contact's chat shows nothing**.

## The facts this plan is built on

Every one of these was read out of the tree on 2026-10-08; each names the file, and each is what a task changes or leans on.

**Symptom 1.** `ffmpeg` is not installed on this machine (`which ffmpeg` prints nothing and `ffmpeg -version` runs no program; there is no `ffmpeg` on the `PATH`). `WhatsappBridge/ffmpeg.js` probes it once at startup and, absent, `toPlayable` returns `null` and `server.js:390` sends the original bytes. WhatsApp voice notes are Ogg/Opus and WP8.1 has no Opus decoder (a hard constraint of this repo), so `VoicePlayer.Source` loads a file the phone cannot decode, `VoicePlayer_MediaFailed` fires, `ChatPage.xaml.cs:869` sets `AudioFailed = true` and `IsPlaying = false` — which is exactly "it never becomes playing". `README.md:126` and `README.md:176` already say all of this and already promise the fallback sentence; the machine is the half that is missing.

**Symptom 2.** `WhatsappApp/Services/AttachmentInbox.cs` copies every outgoing attachment to one fixed name: `private const string CopyBaseName = "outgoing_attachment";` then `file.CopyAsync(LocalFolder, CopyBaseName + extension, NameCollisionOption.ReplaceExisting)`. `ChatPage.StopRecordingAsync` puts the recording in that same slot, so **every outgoing voice note bubble carries `MediaFilePath = "outgoing_attachment.m4a"`** — one file, replaced by the next recording. `ChatPage.ToggleVoice` then refuses to reload it, because the path has not changed:

```csharp
if (_voiceLoadedFile != message.MediaFilePath)
{
    VoicePlayer.Source = new Uri("ms-appdata:///local/" + message.MediaFilePath);
    _voiceLoadedFile = message.MediaFilePath;
}
```

The glyph flips (`message.IsPlaying = true` runs right after `Play()`), and the one `MediaElement` is still sitting at the end of the note it played last, so nothing is heard. A second effect of the same fixed name: an older bubble plays whichever recording was made most recently.

**Symptom 3.** Two halves, and both are gaps, not mistakes.

- *The app never says which bubble the attachment is.* `CommunicationService.SendMediaBeginAsync(string chatId, string transferId, string fileName, string mimeType, int totalChunks)` never sets `frame.RelatedMessageId`, and the adapter reads it there: `messageId: msg.RelatedMessageId || null` (`server.js`, `mediaBegin`). Every other per-message frame sets it — `RequestMediaAsync` does. So the adapter's `transfer.messageId` is always `null`, and the `error` frames it already sends for an incomplete or oversized attachment carry `relatedMessageId: undefined`.
- *The status is a guess, and success is silent.* `ChatPage.xaml.cs` ends an attachment with `message.Status = CommunicationService.Instance.IsConnected ? MessageStatus.Sent : MessageStatus.Failed;`. The adapter's `mediaEnd` answers failures (two `sendControl(session, { command: 'error', ... })` calls) and answers **success with nothing at all**. `DataService.cs:317`'s `case "error"` clears a spinner and raises a banner; it never touches a message's status. So a bubble is marked sent because the socket happens to be up, and a send GOWA refused is invisible on the bubble that owns it.

**Also found while reading, and worth fixing in the same pass** (each is one line and each is a silent no-send): `mediaEnd` returns without a word when `parts.length === 0` (`if (parts.length === 0) return;`), which is what a 0-byte file produces, and again when the transfer id is unknown (`const transfer = session.mediaTransfers.get(...); if (!transfer) return;`). And `SendAttachmentAsync` will happily start a transfer for a 0-byte file, sending `media.begin` with `total = 0`.

## Global Constraints

These hold for every task; they come from `.agents/skills/maintain-the-app/SKILL.md`.

- **C# 5 only.** No `$"..."`, no `?.`, no `get => x`, no `nameof`, no `out var`, no `await` directly inside a `catch` body. Gate: `node tools/check-csharp5.js`.
- **No hardcoded user-visible strings.** UI text goes in both `.resw` files with the same key, then `x:Uid` / `Loc.Get`. Gate: `node tools/check-resw.js --strict`.
- **Runtime text outside the app UI is English** — the adapter's log lines, the `text:` of its `error` frames, and every `Diag` line.
- **Icons are inline `<PathGeometry>`** in a `<Path.Data>`, never a `StaticResource` and never `Figures="M..."`. Gate: `node tools/check-icons.js`.
- **A silent `catch` is a bug.** Every survived failure goes through `Diag.Failed("<call site>", ex)`.
- **The adapter has zero runtime dependencies** and its tests must keep passing. Gate: `cd WhatsappBridge && npm test`.
- **Docker mirror.** Any commit touching `WhatsappBridge/` is mirrored into `vincenzosco/docker-whatsappforwp` with its `tools/sync.js`, and pushed there in the same pass.
- **A new source file is a project file too** — `<Compile Include>` / `<Page>` in `WhatsappApp.csproj`. Gate: `node tools/check-project-files.js`.
- **The frame ceiling is 8 MiB on both sides** and `MEDIA_CHUNK_CHARS` / `MediaChunkChars` are both 700000. Gate: `node tools/check-framing.js`.
- **Docs are written in pairs**, `README.md` + `README.it.md` and `WhatsappBridge/README.md` + `.it.md`, with the same headings in the same order and no emoji. Gate: `node tools/check-docs.js`.
- **A `[DataMember]` with a strict type is a whole-frame failure.** Every field the new frame carries is a `string`.
- Two more guards read these files: `node tools/check-memory.js` (the outgoing file is copied, never read into a `byte[]`) and `node tools/check-fire-and-forget.js`.

## Review Focus

The five conditions this feature will meet that no test above it pins, most likely first. Each gets its test in the task that owns the code.

1. **A voice note the phone still cannot decode** (ffmpeg present, but the file arrived Ogg anyway: the transcode failed, or the payload is an unplayable audio type). The bubble must say it cannot be played, and it must not sit there looking playable and silent. *Task 2 Steps 3-5, checked on the device by Task 6 Step 4.*
2. **An attachment whose local copy is empty** (a tap-length recording, a failed copy). It must not become a bubble with a checkmark; nothing was sent and the file has nothing in it. *Task 3 Step 5, and the adapter's empty-transfer test in Task 4 Step 1.*
3. **Two voice notes of the sender's own, one after the other.** Each bubble must play its own recording, and the second tap must not resume the first one's position. *Task 3 Steps 3-4.*
4. **An attachment refused by the server while the socket is up** — oversized, incomplete, or refused by GOWA. The bubble must say failed and the person must be able to tell why. *Task 4 Steps 1-3, and Task 5's `error` branch.*
5. **The answer arrives for a chat that is not on screen.** The status must be written to the stored message, because the bubble may not exist yet and the page may not be the one in front. *Task 5 Steps 1-3: `SetMessageStatus` writes on the stored message and not on a page's copy.*

---

### Task 1: The device evidence, and the two causes told apart

The phone was disconnected when this plan was written (`ISETool.exe` prints `Errore: ... non e stato rilevato alcun telefono Windows Phone`), so symptoms 1 and 2 are proven from the code and not yet on the device. This task is the first thing that runs, and it is what the rest of the plan is checked against: the log is the only thing that tells an audio file the phone refused from an audio file whose bytes never arrived.

**Files:**
- Evidence: `.tools/voice-before/IsolatedStore/diag.log` (a snapshot, gitignored)
- Record: `.superpowers/sdd/2026-10-08-voice-notes-play-and-send/progress.md` (the ledger, gitignored)

**Interfaces:**
- Consumes: nothing. This task only reads.
- Produces: the ledger entry and the snapshot directory the later tasks are judged against; the decision of which of the two symptom-1 causes below is the live one.

- [ ] **Step 1: Ask the operator to unlock the phone, then pull the run**

`AppDeployCmd.exe` and `ISETool.exe` both need the phone awake. With the app closed and a few minutes after the last use:

```bash
cd /c/Users/Vincenzo/Documenti/WhatsAppForWP && rm -rf .tools/voice-before
cd "/c/Program Files (x86)/Microsoft SDKs/Windows Phone/v8.1/Tools/IsolatedStorageExplorerTool"
env MSYS_NO_PATHCONV=1 ./ISETool.exe ts de 7ccc5b77-3cf2-4020-92a7-9542b250bb49 \
  'C:\Users\Vincenzo\Documenti\WhatsAppForWP\.tools\voice-before'
```

Expected: `Completato.`, and `.tools/voice-before/IsolatedStore/diag.log` present. `Errore: ... non e stato rilevato alcun telefono` means the phone is off or asleep: stop here and say so rather than guessing.

- [ ] **Step 2: Read the log for the two symptom-1 causes**

```bash
cd /c/Users/Vincenzo/Documenti/WhatsAppForWP/.tools/voice-before/IsolatedStore
grep -n "ChatPage/VoicePlayer\|ChatPage.ToggleVoice\|request media\|media.\+requested" diag.log
```

Expected, exactly one of:

- **`ChatPage/VoicePlayer: Failed ... media failed`** — the file arrived, and the phone refused it: cause A, the file is a codec this phone cannot decode, and Task 2 is the fix.
- **no `VoicPlayer` line at all**, and a `request media` line instead — the bytes never arrived, so `MediaFilePath` is still empty and every tap re-requests: cause B, and Task 2 Step 5's diagnosis line plus Task 5's status work are what name it. Record which of the two, in the ledger, with the line.

- [ ] **Step 3: Record the run in the ledger**

Append to `.superpowers/sdd/2026-10-08-voice-notes-play-and-send/progress.md`: the date and time, the snapshot directory, the closing `ok:` lines, and which of the two causes above the log named, quoted verbatim.

---

### Task 2: `ffmpeg` is on the machine that runs the adapter, and the app is told when it is not

Fixes symptom 1. Two halves: the machine gets `ffmpeg` so the conversion happens at all, and the adapter stops handing the app bytes it cannot use without saying so — a voice note that could not be converted arrives with the reason already on it, instead of an unplayable file that the phone discovers on the first tap.

**Files:**
- Modify: `WhatsappBridge/server.js` (`sendMedia` — the incoming direction)
- Modify: `WhatsappBridge/test/server.test.js`
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md` (`### Voice notes need ffmpeg` and its Italian pair)
- Modify: `.agents/skills/run-the-login-server/SKILL.md` (the start-up checks: read the adapter's first lines)

**Interfaces:**
- Consumes: `mediaTools.isAvailable()` from `WhatsappBridge/ffmpeg.js` (exists: `isAvailable() { return available === true; }`).
- Produces: `sendMedia(session, chatId, messageId)` sends, for an Ogg/Opus payload that `toPlayable` could not convert, the original bytes with `MediaMimeType` unchanged (today's behaviour, kept) **and** a `text` control frame whose `text` is a plain English sentence — runtime text outside the app UI is English, so no `.resw` key is involved and no later task reads this.

- [ ] **Step 1: Install `ffmpeg` on this machine**

On the Windows machine that runs the adapter:

```bash
choco install ffmpeg -y
```

Expected: `ffmpeg -version` prints a version line. If Chocolatey is not present, install the build from `ffmpeg.org` and put its `bin` on the `PATH`; what matters is that the same shell that starts the adapter can run `ffmpeg`.

- [ ] **Step 2: Prove the adapter now finds it**

Start GOWA and the adapter with `node tools/start-login.js --no-qr` and read the first lines of the adapter's log.

Expected: `OK ffmpeg found: voice notes will be transcoded to MP3 for WP8.1`. The line `WARN ffmpeg not found (...)`, produced by `ffmpeg.js`'s `probe`, means the install is not on the `PATH` of this shell: fix that before going on.

- [ ] **Step 3: Write the failing test for the unconverted note**

In `WhatsappBridge/test/server.test.js`, in the style of `un allegato immagine va a sendImage e uno sconosciuto a sendFile` — a fake gateway, a socket collecting the frames — with a transcoder whose `toPlayable` returns `null` and `isAvailable()` returns `false`:

```js
 test('un vocale che non si puo convertire arriva con il motivo', async () => {
  const frames = [];
  const bridge = createBridge({ gowa: fakeGowaThatAnswersTheDownload(oggBytes), transcoder: unplayable });
  await bridge.handleControl({ Type: 3, Command: 'media.get', ChatId: 'a@s.whatsapp.net', RelatedMessageId: 'W1' });
  const reason = frames.find((f) => f.Command === 'text' && /cannot be played/.test(f.Text || ''));
  assert.ok(reason, 'the app must be told why the note will not play');
  assert.strictEqual(reason.ChatId, 'a@s.whatsapp.net');
  const media = frames.find((f) => f.Command === 'media' && f.MediaData);
  assert.ok(media, 'the bytes still go out: the README promises the note arrives');
});
```

The two assertions are the point: the reason travels with the bytes, so the bubble can say what is wrong instead of leaving the phone to discover it on the first tap.

- [ ] **Step 4: Run it and watch it fail**

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: FAIL on the new test, because no such frame is sent.

- [ ] **Step 5: Send the reason with the bytes**

In `server.js`'s `sendMedia`, at the point where `mediaTools.toPlayable` returned `null` for a payload `isOggOpus` would have converted (`ffmpeg.js` exports `isOggOpus` for exactly this) and `mediaTools.isAvailable()` is false, `sendControl` a `text` frame for the chat before the bytes go out:

```js
sendControl(session, { command: 'text', chatId, text: 'ffmpeg is not installed on the server: this voice note cannot be converted and will not be played on the phone.' });
```

The bytes still go out: the README pair promises that a note without `ffmpeg` "still arrives and says it cannot be played", and that promise stays.

- [ ] **Step 6: Run the test and the adapter suite**

Run: `cd WhatsappBridge && node --test test/server.test.js && npm test`
Expected: the new test PASSes; the suite reports the same `pass` count as before plus one, `fail 0`.

- [ ] **Step 7: Say it in the skill, in the two READMEs, and in the mirror**

- `.agents/skills/run-the-login-server/SKILL.md`: one line in the start-up checks — read the adapter's first lines and expect `OK ffmpeg found`; a `WARN ffmpeg not found` means received voice notes will not play.
- `WhatsappBridge/README.md` and `.it.md`, in the section that already exists (`### Voice notes need ffmpeg` and its Italian pair): state that the adapter is what needs it, and that without it the note arrives and says so.
- The Docker repository: `node tools/sync.js --from <this checkout>`, `--check`, `(cd server && npm test)`, commit, push. The container's `Dockerfile` already installs `ffmpeg`; nothing to add there.

- [ ] **Step 8: Commit**

```bash
git add WhatsappBridge/server.js WhatsappBridge/test/server.test.js \
  WhatsappBridge/README.md WhatsappBridge/README.it.md \
  .agents/skills/run-the-login-server/SKILL.md
git commit -m "fix: a voice note the server cannot convert says so"
```

---

### Task 3: Each outgoing attachment is its own file, and the player loads what was asked for

Fixes symptom 2. The sender's own bubble must own its recording, and the page's single `MediaElement` must load a file when it is asked for a different message even if the name is one it has seen.

**Files:**
- Modify: `WhatsappApp/Services/AttachmentInbox.cs` (`CopyBaseName`, `PutAsync`, `PutBytesAsync`)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (`ToggleVoice`, `SendAttachmentAsync`)
- Create: `tools/check-attachment-file.js`
- Create: `tools/test/check-attachment-file.test.js`

**Interfaces:**
- Consumes: `AttachmentInbox.PutAsync(StorageFile, string)`, `PutBytesAsync(byte[], string, string, string)` (existing signatures, unchanged).
- Produces: the copied file's name is unique per attachment and is returned unchanged by `AttachmentInbox.LocalFileName`; `ChatPage` sets `message.MediaFilePath` to that name exactly as it does today, so no later task changes.

- [ ] **Step 1: Write the guard and its failing test**

The two structural facts this task creates are invisible to every other guard, so they get one of their own, the fifteenth. `tools/check-attachment-file.js` follows `tools/check-xaml-names.js`'s shape: a pure `attachmentFileProblems(read)` over the two files' text, so the test needs no build, plus a `main()` behind `if (require.main === module)` that walks the two paths and prints `OK: ...` or the problems and exits 1.

`tools/test/check-attachment-file.test.js`, in the style of `tools/test/check-actions.test.js` — the fixture is the real file's text, `read(path) = fs.readFileSync(path.join(__dirname, '..', '..', path), 'utf8')`:

```js
test('la copia di un allegato ha un nome suo e non uno condiviso', () => {
  assert.deepStrictEqual(attachmentFileProblems(read(INBOX)), []);
});
test('un nome condiviso e un problema, non un silenzio', () => {
  const broken = read(INBOX).replace(/"outgoing_attachment_"\s*\+[^;]*/,
    '"outgoing_attachment" + extension');
  const found = attachmentFileProblems(broken).join('\n');
  assert.match(found, /outgoing_attachment/);
});
test('il lettore carica il file della bolla che ha chiesto', () => {
  assert.deepStrictEqual(attachmentFileProblems(read(CHAT)), []);
});
test('un lettore che si fida del nome ripetuto e un problema', () => {
  const broken = read(CHAT).replace(/bool anotherMessage[^\n]*\n/, '');
  const found = attachmentFileProblems(broken).join('\n');
  assert.match(found, /anotherMessage/);
});
```

- [ ] **Step 2: Run it and watch it fail**

Run: `node --test tools/test/check-attachment-file.test.js`
Expected: FAIL, because `tools/check-attachment-file.js` does not exist yet (`Cannot find module`), and once it does, because the two source files still hold the shared name and the name-only load. `attachmentFileProblems(text)` returns an array of sentences, empty when there is nothing wrong.

- [ ] **Step 3: Implement `tools/check-attachment-file.js`**

Two rules, each a plain regex over the file's text, each producing one sentence naming the file and the line:

- in `AttachmentInbox.cs`: the copy target must be built from a name the file returns and not a constant, so match that the create call's name argument contains `NextCopyName(`;
- in `ChatPage.xaml.cs`: `ToggleVoice` must decide on the message as well as the name, so match `anotherMessage` inside `ToggleVoice`.

`main()` prints `OK: the attachment copy name is unique and the player loads the bubble it was asked for.` and exits 0, or the problems and exits 1.

- [ ] **Step 4: Implement the unique copy name in `AttachmentInbox`**

In `WhatsappApp/Services/AttachmentInbox.cs`, the copy target becomes a name that cannot collide: the fixed prefix, a counter, and the extension.

```csharp
// One name per attachment, never shared: the file a message points at must be
// the file that message sent. A single fixed name means the next recording
// replaces the previous one under every bubble that used it.
private static int _copySequence;

private static string NextCopyName(string extension)
{
    _copySequence++;
    return "outgoing_attachment_" + _copySequence.ToString(CultureInfo.InvariantCulture) + extension;
}
```

`PutAsync` and `PutBytesAsync` both create their target with `NextCopyName(extension)` instead of `CopyBaseName + extension`, and both call `PutLocal(file.Name, ...)` with the name that came back, so `LocalFileName` is the unique one. `NameCollisionOption.ReplaceExisting` stays, and `CultureInfo` comes from `System.Globalization`.

- [ ] **Step 5: Make the player load the file it was asked for**

In `ChatPage.xaml.cs`'s `ToggleVoice`, the load is decided by the message and not only by the name:

```csharp
bool anotherMessage = _voiceMessage != message;
if (anotherMessage || _voiceLoadedFile != message.MediaFilePath)
{
    VoicePlayer.Source = new Uri("ms-appdata:///local/" + message.MediaFilePath);
    _voiceLoadedFile = message.MediaFilePath;
}
```

A fresh bubble also starts at its beginning, not at wherever the last one stopped:

```csharp
VoicePlayer.Position = TimeSpan.Zero;
```

before `VoicePlayer.Play()`. Both lines are needed: the first for a name that repeats, the second for the position, and the second alone would not reload a replaced file.

- [ ] **Step 6: Do not start a transfer for an empty file**

In `SendAttachmentAsync`, after `ulong length = (await file.GetBasicPropertiesAsync()).Size;`:

```csharp
if (length == 0)
{
    Diag.Failed("ChatPage.SendAttachmentAsync/empty",
        new InvalidOperationException(fileName + " is 0 bytes"));
    message.Status = MessageStatus.Failed;
    return;
}
```

An empty file is not a smaller voice note: today it produces `total = 0`, the adapter's `mediaEnd` returns on `parts.length === 0` without a word, and the bubble keeps its checkmark.

- [ ] **Step 7: Put the new guard in the lists that claim to be the gate**

The gate is a documented list in four places, and a guard that is not in it is one nobody runs. Add `node tools/check-attachment-file.js` in the same positions `check-xaml-names.js` occupies:

- `README.md` and `README.it.md`, in the `3. ... fast gate:` block, as the line after `check-xaml-names.js` — the pair travels together or `check-docs` fails;
- `.agents/skills/test-the-app/SKILL.md`: the command block, the guard table (a row saying what it catches: an attachment copied to a shared name, and a player that trusts a file name it has already seen), and the `# the tools' own tests (N)` comment, set to the number of **passing** tests the next step prints;
- `.agents/skills/maintain-the-app/SKILL.md`, step 5's fast-gate command.

- [ ] **Step 8: Run the tests and the guards that read these files**

Run: `node --test tools/test/check-attachment-file.test.js && node tools/check-attachment-file.js && node tools/check-memory.js && node tools/check-csharp5.js && node tools/check-actions.js && node tools/check-docs.js`
Expected: PASS on the new test; six `OK` lines.

- [ ] **Step 9: Commit**

```bash
git add tools/check-attachment-file.js tools/test/check-attachment-file.test.js \
  WhatsappApp/Services/AttachmentInbox.cs WhatsappApp/Pages/ChatPage.xaml.cs \
  README.md README.it.md .agents/skills/test-the-app/SKILL.md \
  .agents/skills/maintain-the-app/SKILL.md
git commit -m "fix: every attachment is its own file and every bubble plays its own recording"
```

---

### Task 4: The app names the bubble, and the adapter answers for it

Fixes symptom 3's missing link and its silent half. The app tells the adapter which message an attachment belongs to; the adapter answers every attachment it is given — one frame when GOWA took it, one refusal when it did not, including the three cases that are silent today.

**Files:**
- Modify: `WhatsappApp/Services/CommunicationService.cs` (`SendMediaBeginAsync`)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (the `SendMediaBeginAsync` call site)
- Modify: `WhatsappBridge/server.js` (`mediaBegin`, `mediaEnd`, `sendMediaToGowa` call site)
- Modify: `WhatsappBridge/test/server.test.js`

**Interfaces:**
- Consumes: `ChatMessage.Id` (the app's local id, already on the frame as `RelatedMessageId` for `media.get`).
- Produces:
  - the app-to-adapter frame `media.begin` now carries `RelatedMessageId` = the bubble's `ChatMessage.Id`;
  - the adapter-to-app frame `{ command: 'attachment.sent', chatId, relatedMessageId, text }` on success, where `text` is GOWA's message id (`''` when GOWA reported none);
  - the adapter-to-app frame `{ command: 'error', chatId, relatedMessageId, text }` on every refusal, where `relatedMessageId` is now always present for an attachment. Task 5 reads both.

- [ ] **Step 1: Write the failing tests**

In `WhatsappBridge/test/server.test.js`, the same fake-gateway-and-collected-frames shape as Task 2's test:

```js
test('un allegato riuscito risponde attachment.sent con la bolla', async () => {
  // gateway.sendAudio resolves 'W1'
  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 't1', RelatedMessageId: 'm1', MediaFileName: 'voce.m4a', MediaMimeType: 'audio/mp4', MediaChunkTotal: 1 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 't1', MediaChunkIndex: 0, MediaData: Buffer.from('voce').toString('base64') });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 't1' });
  const answer = frames.find((f) => f.Command === 'attachment.sent');
  assert.ok(answer, 'a send nobody answers is a send nobody can report');
  assert.strictEqual(answer.RelatedMessageId, 'm1');
  assert.strictEqual(answer.Text, 'W1');
  assert.strictEqual(answer.ChatId, 'a@s.whatsapp.net');
});

test('un allegato vuoto non sparisce in silenzio', async () => {
  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 't2', RelatedMessageId: 'm2', MediaChunkTotal: 0 });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 't2' });
  const refusal = frames.find((f) => f.Command === 'error');
  assert.ok(refusal, 'parts.length === 0 must refuse, not return');
  assert.strictEqual(refusal.RelatedMessageId, 'm2');
});

test('un media.end di un trasferimento sconosciuto viene rifiutato', async () => {
  await bridge.handleControl({ Type: 3, Command: 'media.end', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'mai-visto', RelatedMessageId: 'm3' });
  const refusal = frames.find((f) => f.Command === 'error');
  assert.ok(refusal, 'an unknown transfer is a fault, not a no-op');
  assert.strictEqual(refusal.RelatedMessageId, 'm3');
});
```

In all three the frame's `RelatedMessageId` must come back exactly as the app sent it in `media.begin`: that round trip is what Task 5 hangs the bubble's status on.

- [ ] **Step 2: Run them and watch them fail**

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: FAIL, three tests: no `attachment.sent` frame exists, and both silent paths send nothing.

- [ ] **Step 3: Answer on success and refuse on the silent paths, in `server.js`**

In `mediaEnd`, the three changes:

- the unknown transfer refuses instead of returning: `if (!transfer) { sendControl(session, { command: 'error', chatId: msg.ChatId, relatedMessageId: msg.RelatedMessageId || undefined, text: 'The attachment was not being received: send it again.' }); return; }`
- `parts.length === 0` refuses with the same shape and its own sentence, instead of `return`ing;
- after `sendMediaToGowa` resolves, the confirmation goes out with what GOWA returned, which is what `sendMediaToGowa` already returns up the chain (`(data.results || {}).message_id || ''`):

```js
const gowaId = await sendMediaToGowa(session, transfer.chatId, msg.Text, buffer, transfer.mimeType, transfer.fileName);
sendControl(session, {
  command: 'attachment.sent',
  chatId: transfer.chatId,
  relatedMessageId: transfer.messageId || undefined,
  text: gowaId || ''
});
```

The queued path (`session.state.status !== 'connected'`) keeps queueing and answers nothing yet: the confirmation comes when `flushPending` sends it, through the same `sendOutgoing`. Give `sendOutgoing`'s media branch the same `attachment.sent` answer so a queued attachment is confirmed too, and keep `relatedMessageId` on the queued object (add `RelatedMessageId: transfer.messageId` to the pushed object).

- [ ] **Step 4: Run the tests and the adapter suite**

Run: `cd WhatsappBridge && node --test test/server.test.js && npm test`
Expected: the three new tests PASS; `fail 0`; existing media tests still pass (they assert what GOWA was called with, which does not change).

- [ ] **Step 5: Send the message id from the app**

In `WhatsappApp/Services/CommunicationService.cs`:

```csharp
public async Task SendMediaBeginAsync(string chatId, string messageId, string transferId,
    string fileName, string mimeType, int totalChunks)
{
    var frame = NewControlFrame("media.begin");
    frame.Text = chatId;
    frame.RelatedMessageId = messageId;
    ...
}
```

and at the call site in `ChatPage.SendAttachmentAsync`: `SendMediaBeginAsync(_contact.Id, message.Id, transferId, fileName, mimeType, total)`.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Services/CommunicationService.cs WhatsappApp/Pages/ChatPage.xaml.cs \
  WhatsappBridge/server.js WhatsappBridge/test/server.test.js
git commit -m "feat: an attachment names its bubble and the adapter answers for it"
```

---

### Task 5: The bubble's status comes from the answer

Fixes the checkmark. `Status = Sent` stops being a guess about the socket and becomes the adapter's answer for that message; a refusal puts `Failed` on the same bubble, in whichever chat it lives.

**Files:**
- Modify: `WhatsappApp/Services/DataService.cs` (the `case "error"` branch, plus a new `case "attachment.sent"`, plus `SetMessageStatus`)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (`SendAttachmentAsync`, `AddAndSendMessage`)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw`

**Interfaces:**
- Consumes: the `attachment.sent` and `error` frames of Task 4, both carrying `ChatId` and `RelatedMessageId`.
- Produces: `DataService.SetMessageStatus(string chatId, string messageId, MessageStatus status)` — `void`, a no-op when the message is not in `GetMessages(chatId)`; writes the status on the stored message whether or not its page is open.

- [ ] **Step 1: Write the failing test**

There is no C# test host, so the test is the guard's: add to `tools/check-fire-and-forget.js`'s existing scan of `DataService` a check that its `case "attachment.sent"` branch calls `SetMessageStatus`. Write that check first and watch it fail:

```bash
node tools/check-fire-and-forget.js   # FAIL: no attachment.sent branch
```

- [ ] **Step 2: Implement `SetMessageStatus` and the two branches**

In `DataService.cs`, next to `RemoveMessage`/`ApplyEdit`:

```csharp
/// <summary>
/// The adapter's answer for an attachment: the bubble changes from Sending to
/// Sent or Failed. It is written on the stored message and not on a page's copy,
/// because the answer can arrive while another chat is on screen - or before the
/// page that owns the bubble exists at all.
/// </summary>
public void SetMessageStatus(string chatId, string messageId, MessageStatus status)
```

and the switch grows:

```csharp
case "attachment.sent":
    SetMessageStatus(message.ChatId, message.RelatedMessageId, MessageStatus.Sent);
    break;
case "error":
    ClearMediaLoading(message);
    // A refusal that names a bubble is that bubble's failure: without this the
    // message kept the checkmark while the file never left the phone.
    if (!string.IsNullOrEmpty(message.RelatedMessageId))
        SetMessageStatus(message.ChatId, message.RelatedMessageId, MessageStatus.Failed);
    if (!string.IsNullOrEmpty(message.Text)) RaiseAdapterError(message.Text);
    break;
```

- [ ] **Step 3: Stop deciding the status from the socket**

In `ChatPage.SendAttachmentAsync`, remove the final guess:

```csharp
await CommunicationService.Instance.SendMediaEndAsync(transferId, caption);
// The status is the adapter's answer, not the state of the socket: the answer
// arrives as attachment.sent or error and DataService writes it.
```

and replace the earlier `message.Status = CommunicationService.Instance.IsConnected ? MessageStatus.Sent : MessageStatus.Failed;` **only** in the `!IsConnected` guard at the top of the method — that one stays, because a socket that is down is a failure the app knows first-hand. The bubble keeps `MessageStatus.Sending` until the answer.

Same for `AddAndSendMessage`'s text path only in the sense that it must not regress: leave it as it is, and say in the ledger that a text message's status still follows the socket, which is out of this plan's scope.

- [ ] **Step 4: Say it in the two `.resw` files**

A voice note the phone cannot decode already has `ChatPage_AudioError`. The refusal text comes from the adapter and is raised through `RaiseAdapterError`, whose string already exists. Nothing new is user-visible here: run `node tools/check-resw.js --strict` to prove the pairs are still in step, and change no key.

- [ ] **Step 5: Run the guards**

Run: `node tools/check-fire-and-forget.js && node tools/check-resw.js --strict && node tools/check-csharp5.js && node tools/check-actions.js && node tools/check-diagnostics.js`
Expected: five `OK` lines.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Services/DataService.cs WhatsappApp/Pages/ChatPage.xaml.cs \
  tools/check-fire-and-forget.js
git commit -m "fix: a bubble is sent when the adapter says so, and failed when it says no"
```

---

### Task 6: The gate, the docs, and the three symptoms on the phone

Nothing here is proven until the phone says so. This task runs the whole gate, updates the pair that describes what a voice note does, then reproduces all three symptoms on the Lumia and reads the log.

**Files:**
- Modify: `README.md`, `README.it.md` (the voice-note line and the Limitations section)
- Evidence: `.tools/voice-after/IsolatedStore/diag.log`
- Record: `.superpowers/sdd/2026-10-08-voice-notes-play-and-send/progress.md`

**Interfaces:**
- Consumes: everything above.
- Produces: the run that closes the plan.

- [ ] **Step 1: Run the full gate**

```bash
node tools/check-csharp5.js && node tools/check-icons.js \
  && node tools/check-resw.js --strict && node tools/check-docs.js \
  && node tools/check-framing.js && node tools/check-tile.js \
  && node tools/check-memory.js && node tools/check-actions.js \
  && node tools/check-fire-and-forget.js && node tools/check-project-files.js \
  && node tools/check-chat-list-source.js && node tools/check-diagnostics.js \
  && node tools/check-handshake-answer.js && node tools/check-xaml-names.js \
  && node tools/check-attachment-file.js
node --test "tools/test/**/*.test.js"
cd WhatsappBridge && npm test
```

Expected: fifteen `OK` lines and exit 0; the tools suite with its known four `download.test.js` failures (no `zip` on this host) and both new test files passing; the adapter suite `fail 0`.

- [ ] **Step 2: Build ARM, then install on the phone**

```bash
(MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/Bin/MSBuild.exe" WhatsappApp.sln /t:Rebuild \
  /p:Configuration=Debug /p:Platform=ARM /v:m > .build.log 2>&1 &)
# poll for "Build succeeded"/"Build FAILED"; expect 0 warning CS / 0 error CS and 1 package
git checkout -- WhatsappApp/Package.appxmanifest
rm .build.log
cd "/c/Program Files (x86)/Microsoft SDKs/Windows Phone/v8.1/Tools/AppDeploy"
env MSYS_NO_PATHCONV=1 ./AppDeployCmd.exe /install '<abs path to the .appx>' /targetdevice:de
env MSYS_NO_PATHCONV=1 ./AppDeployCmd.exe /launch 7ccc5b77-3cf2-4020-92a7-9542b250bb49 /targetdevice:de
```

- [ ] **Step 3: Reproduce the three symptoms, one at a time**

Ask the operator for, in this order, with the app left open between them:

1. open a chat that has a voice note **from the contact**, tap its play bar, and say whether the glyph turns into a pause and whether anything is heard;
2. record and send a voice note, then tap its bubble: the glyph must turn, and the person must hear their own voice;
3. check the contact's own phone: the voice note must appear there as a voice note.

- [ ] **Step 4: Pull the run and assert on the log**

As in Task 1, into `.tools/voice-after`. Expected, in `diag.log`:

- no `ChatPage/VoicePlayer: Failed` line for the received note, and its bubble's tap left an `ok:` line or nothing;
- no `ChatPage.SendAttachmentAsync` failure, and no `ChatPage.SendAttachmentAsync/empty`;
- the adapter's own log (the container/terminal that runs it) carrying `MSG attachment to ...: N bytes (audio)` and no `ERR attachment to ... failed`. The two together are the answer: if the log shows an `attachment.sent` on the app side and the contact still sees nothing, the fault is in GOWA's `/send/audio` and the last line of the adapter log names it.

- [ ] **Step 5: Say what changed in the README pair**

`README.md:126` / `README.it.md:124` already describe the conversion and the fallback. Add what is now true and was not before: a voice note the server refused no longer shows as sent, and the sender's own note plays when tapped. If the `Limiti`/`Limitations` section claims anything about voice notes, correct it in both languages in the same commit; `node tools/check-docs.js` is the gate.

- [ ] **Step 6: Record the run and close the plan**

Append Task 1 through Task 6 to the ledger: for each, the commit, the commands run and their output, and any divergence. Then append a `## What execution changed about this plan` section to this document listing every place execution departed from it, and do not rewrite the tasks above it.

- [ ] **Step 7: Commit, push, mirror**

```bash
git add README.md README.it.md
git commit -m "docs: say what a voice note now does on the phone"
git push origin master
```

Then the Docker mirror for anything under `WhatsappBridge/` that Task 2 and Task 4 changed, and `git status -sb` showing `## master...origin/master` with nothing ahead.

---

## What execution changed about this plan

The plan was followed and the tree ended where it said it would, except where the hardware was
not there. Six differences worth naming, then what is still owed.

1. **Task 2 Step 5's `text` frame became an `error` frame.** The plan sends the reason as a
   `text` control frame, but the app has no `text` case in
   `DataService.OnControlMessageReceived`, so a `text` frame is dropped and the bubble stays
   exactly as silent as before this plan. The reason travels as an `error` frame instead, which
   reaches `RaiseAdapterError` - the app's one channel for adapter text, whose string already
   exists. The two promises the plan cares about still hold: the bytes go out unchanged, and the
   person is told why the note will not play. The test asserts `/cannot be played/` on the
   frame's text, so it does not depend on which command carries it.

2. **Task 2 Step 1 could not install `ffmpeg` and did not fall back to a manual binary.**
   `choco install ffmpeg -y` fails with `System.UnauthorizedAccessException: Accesso al percorso
   'C:\ProgramData\chocolatey\lib-bad' negato` (evidence in `.tools/ffmpeg-install.log`), because
   the install writes under `C:\ProgramData\chocolatey` and this session is not elevated. The
   plan's fallback - an `ffmpeg.org` build on the `PATH` - was not taken either: it means fetching
   a binary this session cannot verify. What shipped instead is the half the plan can prove: the
   startup log now says `WARN ffmpeg not found` or `WARN ffmpeg cannot make MP3 (...)`, and an
   Ogg/Opus note that cannot be converted goes out with the reason on it. The machine change is
   the operator's.

3. **Task 3 Step 7's guard count and test count are one higher than the plan's text.** The plan
   says "the fifteenth" in Step 3 and then, in Step 7, "the number of passing tests the next step
   prints". `tools/check-attachment-file.js` is the fifteenth guard; the tools suite prints 118
   passing (up from 108 before this plan), and `.agents/skills/test-the-app/SKILL.md` carries
   that number.

4. **Task 5 Step 1's guard did not exist and was added.** The plan says "add to
   `tools/check-fire-and-forget.js`'s existing scan of `DataService` a check that its
   `case \"attachment.sent\"` branch calls `SetMessageStatus`". `check-fire-and-forget.js` does
   *not* scan `DataService`: it parses `#pragma warning disable 4014` regions and method bodies.
   The rule `statusAnswerProblems(source, file)` was written (restricted to
   `WhatsappApp/Services/DataService.cs`), wired into `main()`, exported, and given two tests.
   That is more than the plan describes and it is the correct shape: the guard still exits 0 on
   the fixed tree and would fail on a tree that drops either branch.

5. **Task 5's Files block names the two `.resw` files, and no `.resw` was touched.** Step 4 says
   as much ("Nothing new is user-visible here... change no key"); the Files block is the plan's
   inconsistency, and the correct reading is Step 4's. `node tools/check-resw.js --strict` exits
   0.

6. **Task 6 Steps 3-4 did not run.** The Lumia is not detected - `ISETool.exe ts de ...` prints
   `Errore: La distribuzione non e' riuscita perche' non e' stato rilevato alcun telefono
   Windows Phone` - so no `.tools/voice-after` snapshot exists and the three symptoms were not
   reproduced on hardware. `AppDeployCmd.exe /EnumerateDevices` still lists index 0 `Device`, so
   the SDK is fine and the phone is what is not answering.

### What is still owed

- **The three symptoms on the phone.** They rest on the code, the fifteen guards, fifteen new
  tests across the two plans, and the ARM build; the on-device run is the one check that would
  turn "the code says so" into "the phone says so".
- **`ffmpeg` on this machine.** Until it is installed, received voice notes do not play here; the
  adapter says so in one line at startup instead of leaving it to the first tap.
- **The four `download.test.js` failures** are the missing `zip` on this host, not this change.
