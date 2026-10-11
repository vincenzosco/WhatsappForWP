# The chat list a pairing does not fill Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** after the account is linked the conversation list fills on the phone - the history
WhatsApp sends behind a media download is fetched, decrypted and applied exactly like the
inline bootstrap already is - and every case the phone can hit is named in the log instead of
presenting an empty list.

**Architecture:** `WhatsappApp/Protocol/**` keeps the rules (pure, compiled twice, tested by
`ProtocolTests`); the app keeps the wiring (`Services/NativeConnection`). One new pure file,
`Protocol/Media/HistoryBlob.cs`, owns the two steps a downloaded history needs - the media keys
the scheme derives for a `md-msg-hist` blob, and the fetch-open-inflate order - and
`NativeConnection.ApplyHistory` becomes the async caller that reaches for it when the
notification carries a `directPath` instead of an inline payload. Nothing else moves: the rows
still reach the pages through `NativeChats` -> `DataService` -> `ChatsPage`.

**Tech Stack:** C# 5 on Windows Phone 8.1 (app), plain .NET 4.5 for the desktop test host
`ProtocolTests` (run by `node tools/test-protocol.js`), Node guards under `tools/check-*.js`,
MSBuild 14 for the app and the solution.

**Spec:** the operator's report, in their words: *"non si vedono le chat nell'app, dopo essere
stata collegata con whatsapp"*. The gap is the one the plan that retired the server recorded
while it was executed: *"The history chunks that arrive **behind a media download** are still
named in the log and not fetched (`NativeConnection.ApplyHistory`); that half of the sentence
above still stands."* (`docs/superpowers/plans/2026-10-11-native-only-client.md`.) The authority
for the two shapes is the reference checked out on this machine, `/tmp/m4ref` (Baileys, the same
checkout the other fixtures were printed from): `src/Utils/history.ts`
(`downloadHistory`, `downloadAndProcessHistorySyncNotification`) and
`src/Utils/messages-media.ts` (`getMediaKeys`, `hkdfInfoKey`). The notification's own field
numbers are already pinned by `ProtocolTests/Vectors/HistoryFixture.cs`.

## Global Constraints

- **C# 5 only** under `WhatsappApp/**`: no `System.Security.Cryptography`, no `Windows.*` type
  outside `Services/**`, no LINQ extension method without `using System.Linq;`.
  `node tools/check-csharp5.js` fails the build on all three and is never weakened.
- **The protocol core compiles twice** - into the app and into `ProtocolTests` (net45). HTTP is
  reached through `IMediaFetcher`, never through WinRT, inside `Protocol/**`.
- **Every new `.cs` under `WhatsappApp` is listed in `WhatsappApp/WhatsApp.csproj`**
  (`node tools/check-project-files.js`); `ProtocolTests` picks the core up from
  `..\WhatsappApp\Protocol\**\*.cs` and needs no line of its own.
- **Fixtures are printed from the reference, never written by hand**, and each one carries the
  command that regenerated it in its doc comment. A fixture that agrees only with this client
  is not evidence.
- **A call fired without `await` must be observed** (`node tools/check-fire-and-forget.js`):
  either `Guarded.RunGuardedAsync`, or a method that answers for its own exceptions.
- **No new settings key, no new server, no new WinRT API.** The phone stores no address, port
  or token (`node tools/check-no-server.js`).
- **The fast gate is never skipped**: `node tools/test-protocol.js`, the guards named in
  `test-the-app`, `node --test "tools/test/**/*.test.js"`, and
  `msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86` ending in
  `0 Error(s)`.

## Review Focus

- **A notification that carries a `directPath` but no `mediaKey`** (or a truncated one): a
  person expects the list to fill, and this client cannot open the blob. The log must name the
  field that was missing - an empty list with no sentence is the failure this whole plan exists
  to end. Pinned by `ANotificationWithNoKeyIsRefusedByName`.
- **A blob whose `fileSha256`/`fileLength` describe the inflated payload** rather than the
  compressed one: the media path's checks would refuse a body WhatsApp really sent, and the
  person sees an empty list plus a `decrypted to something else than fileSha256 says` line.
  The behavior is stated in Task 3's Ruling and pinned by
  `AHashTakenOverTheInflatedPayloadIsRefusedByName`, so the device log is unambiguous.
- **A body that is already inflated** (a server that stops compressing this type): `Zlib.Inflate`
  throws and the sync is lost. Expected: one `DIAG failed` line naming the blob, no half-filled
  list. Pinned by `ABlobThatIsNotCompressedIsRefused`.
- **A download that fails at the moment of the pairing** (no data connection, a blob taken
  down): the row list stays empty for that login. Expected: the failure is named with its
  address and the loop survives. Pinned by `AFailedDownloadNamesItsAddress` (Task 3) and by
  Task 4's `catch`, which is what turns it into a `DIAG failed` line; the live case - a network
  that really went away mid-pairing - is the device step in Task 4, because only the phone has
  a network to lose.
- **A sync that arrives while the list is on screen**: the rows must appear without leaving the
  page (the collection is bound from code). Verified by the device step in Task 4.

---

### Task 1: the phone run that names which case this phone hits

**Files:**
- Modify: none. This task writes the lines it reads into the table at the end of this plan.

**Interfaces:**
- Consumes: nothing.
- Produces: the decision that says whether Tasks 2-4 are the fix, and the four log lines that
  are the acceptance criteria of Task 4.

- [ ] **Step 1: Deploy the tree as it stands to the phone**

Run: `msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=ARM`
Expected: `0 Error(s)`, then deploy `WhatsappApp/AppPackages/WhatsApp_1.3.12.4_ARM_Debug_Test`'
s `.appx` to the device (or press Deploy on `Debug|ARM` in Visual Studio).

- [ ] **Step 2: Link the account if it is not linked, then open the chat list and wait 30 s**

- [ ] **Step 3: Tap the Diagnostics button in the empty state and read the log**

Run: the Diagnostics page, or `LocalFolder/diagnostics.log` (the page's **Share** copies it).
Expected: one of these four lines (the exact strings are the ones in
`WhatsappApp/Services/NativeConnection.cs`):

| The log says | What it means | What runs next |
| --- | --- | --- |
| `native: history sync type 0 is behind a media download, which this build cannot fetch yet` | The sync arrived and only the download half is unwritten - this plan's own case | Tasks 2, 3, 4 |
| `native: history sync type N carried K conversation(s), M row(s) now` **and the list is still empty** | The sync is applied and the rows do not reach the page | stop: `ChatsPage`/`DataService` is the place to look, not this plan's Task 4 |
| `native: the history is complete, ...` and no `history sync` line before it | The notification never reached `ApplyHistory` | Task 4, Step 1 (the log line that names the fields the notification carried) first |
| no `history sync` line and no `history complete` line at all | WhatsApp sent no history for this login | check `whatsapp: connected to` and the pairing lines in the same log, then `node tools/test-protocol.js --connect` to prove the socket is live before touching any code |

- [ ] **Step 4: Write the lines into the table at the end of this plan, with the date**

---

### Task 2: the keys a `md-msg-hist` blob is encrypted with

**Files:**
- Modify: `WhatsappApp/Protocol/Media/MediaKeys.cs`
- Modify: `ProtocolTests/Vectors/HistoryFixture.cs` (three hex constants + the command that printed them)
- Modify: `ProtocolTests/HistorySyncTests.cs`

**Interfaces:**
- Consumes: `MediaKeys.Expand/Iv/CipherKey/MacKey(byte[] mediaKey, string mediaType)`, unchanged.
- Produces: `MediaKeys.History` (`public const string History = "md-msg-hist";`),
  `MediaKeys.Info(MediaKeys.History)`, and `HistoryFixture.HistoryIv`,
  `HistoryFixture.HistoryCipherKey`, `HistoryFixture.HistoryMacKey` (the reference's hex).

- [ ] **Step 1: Print the keys from the reference**

Run:

```bash
cd /tmp/m4ref && node -e "
const m = require('./node_modules/@whiskeysockets/baileys/lib/Utils/messages-media.js')
console.log(m.hkdfInfoKey('md-msg-hist'))
m.getMediaKeys(Buffer.alloc(32, 7), 'md-msg-hist').then((k) => {
  console.log(k.iv.toString('hex'))
  console.log(k.cipherKey.toString('hex'))
  console.log(k.macKey.toString('hex'))
})
"
```

Expected: `WhatsApp History Keys`, then one hex string each. The media key is **32 bytes of
`0x07`** and is the fixture's own; the three printed strings are what Steps 2-3 paste. If
`messages-media.js` refuses `require` (the package is ESM), the same four values come from
`node --input-type=module -e "import * as m from
'@whiskeysockets/baileys/lib/Utils/messages-media.js'; ..."`; the four values, not the command,
are what the test needs.

- [ ] **Step 2: Write the failing test**

In `ProtocolTests/HistorySyncTests.cs`:

```csharp
[Test]
public static void TheHistoryKeysAreTheReferences()
{
    byte[] mediaKey = new byte[32];
    for (int i = 0; i < mediaKey.Length; i++) mediaKey[i] = 7;

    Assert.AreEqual(HistoryFixture.HistoryInfo, MediaKeys.Info(MediaKeys.History),
        "the info string is the reference's hkdfInfoKey('md-msg-hist')");
    Assert.AreEqual(HistoryFixture.HistoryIv, Hex(MediaKeys.Iv(mediaKey, MediaKeys.History)));
    Assert.AreEqual(HistoryFixture.HistoryCipherKey, Hex(MediaKeys.CipherKey(mediaKey, MediaKeys.History)));
    Assert.AreEqual(HistoryFixture.HistoryMacKey, Hex(MediaKeys.MacKey(mediaKey, MediaKeys.History)));
}
```

Add to `ProtocolTests/Vectors/HistoryFixture.cs`, with the Step 1 command in the doc comment
above them and the note that the media key is 32 bytes of `0x07`:
`public const string HistoryInfo` (the printed info string, `"WhatsApp History Keys"` unless
Step 1 answered otherwise), `HistoryIv`, `HistoryCipherKey`, `HistoryMacKey`.

- [ ] **Step 3: Run it to see it fail**

Run: `node tools/test-protocol.js`
Expected: the build succeeds and the run fails with
`not a media type this client knows: md-msg-hist` - `NameOf` refuses the type instead of
guessing it.

- [ ] **Step 4: Implement it**

In `WhatsappApp/Protocol/Media/MediaKeys.cs`: add
`public const string History = "md-msg-hist";` beside the four existing type constants, and a
new branch in `NameOf` returning the name Step 1 printed. Extend the class comment with the
reference's own source: the info string is `hkdfInfoKey` (`Utils/messages-media.ts`) over
`MEDIA_HKDF_KEY_MAPPING` (`Defaults/index.ts`), where `'md-msg-hist'` is the history blob - the
type WhatsApp downloads the history behind, and the only type this client reads that is not a
message's media. Do **not** make `NameOf` fall through to a default: an unknown type stays a
refusal.

- [ ] **Step 5: Run the suite and the guard**

Run: `node tools/test-protocol.js && node tools/check-csharp5.js`
Expected: `OK: 235 protocol test(s)` (234 plus the one this task adds), exit 0.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Protocol/Media/MediaKeys.cs ProtocolTests/HistorySyncTests.cs ProtocolTests/Vectors/HistoryFixture.cs
git commit -m "feat: the media keys of a history blob, from the reference's own mapping"
```

---

### Task 3: the blob itself - fetch, open, inflate

**Files:**
- Create: `WhatsappApp/Protocol/Media/HistoryBlob.cs`
- Modify: `WhatsappApp/WhatsApp.csproj:168` (insert `<Compile Include="Protocol\Media\HistoryBlob.cs" />` above the `MediaDownload.cs` line, which keeps that group in name order)
- Modify: `ProtocolTests/Vectors/HistoryFixture.cs` (the notification and the body)
- Modify: `ProtocolTests/HistorySyncTests.cs`
- Modify: `ProtocolTests/MediaTests.cs:175` (`private sealed class ScriptedFetcher` -> `internal
  sealed class`, so both test classes use the one scripted fetcher instead of two copies)

**Interfaces:**
- Consumes: `MediaDownload.FetchAsync(IMediaFetcher, byte[] mediaKey, string mediaType, string
  directPath, string url, byte[] fileSha256, byte[] fileEncSha256, long fileLength)`,
  `MediaDownload.Open(byte[] body, byte[] mediaKey, string mediaType, byte[] fileSha256,
  byte[] fileEncSha256, long fileLength, string address)`, `MediaKeys.History` (Task 2),
  `Zlib.Inflate(byte[])`, `HistorySyncNotification.{MediaKey, DirectPath, FileLength,
  FileSha256, FileEncSha256}`.
- Produces:
  `public static byte[] HistoryBlob.Open(byte[] body, HistorySyncNotification notification)` -
  the **decompressed** `HistorySync` bytes, i.e. what `HistorySync.Decode` takes;
  `public static Task<byte[]> HistoryBlob.FetchAsync(IMediaFetcher fetcher,
  HistorySyncNotification notification)` - the same bytes from `notification.DirectPath`.
  Both throw `ProtocolException` naming the field or the check that refused it.

- [ ] **Step 1: Print the encrypted body from the reference**

Run (the `<Plain hex>` is the whole of `HistoryFixture.Plain`, `471` bytes):

```bash
cd /tmp/m4ref && node -e "
const crypto = require('crypto'), zlib = require('zlib')
const p = require('@whiskeysockets/baileys').proto
const m = require('./node_modules/@whiskeysockets/baileys/lib/Utils/messages-media.js')
const plain = Buffer.from('<Plain hex>', 'hex')
const compressed = zlib.deflateSync(plain)
const mediaKey = Buffer.alloc(32, 7)
m.getMediaKeys(mediaKey, 'md-msg-hist').then(({ iv, cipherKey, macKey }) => {
  const cipher = crypto.createCipheriv('aes-256-cbc', cipherKey, iv)
  const ciphertext = Buffer.concat([cipher.update(compressed), cipher.final()])
  const body = Buffer.concat([ciphertext, crypto.createHmac('sha256', macKey)
    .update(Buffer.concat([iv, ciphertext])).digest().slice(0, 10)])
  console.log('notification', p.Message.HistorySyncNotification.encode({
    fileSha256: crypto.createHash('sha256').update(compressed).digest(),
    fileLength: compressed.length,
    mediaKey,
    fileEncSha256: crypto.createHash('sha256').update(body).digest(),
    directPath: '/v/t62/hist',
    syncType: 2
  }).finish().toString('hex'))
  console.log('encryptedBody', body.toString('hex'))
})
"
```

Expected: two hex strings - the notification, and a body that is ten bytes longer than the
compressed payload (`fileSha256` and `fileLength` describe the **compressed** plaintext, which
is what this client's media path hashes and measures).

- [ ] **Step 2: Write the failing test**

In `ProtocolTests/HistorySyncTests.cs`:

```csharp
[Test]
public static void AHistoryBehindADownloadBecomesTheSameConversations()
{
    HistorySyncNotification notification =
        HistorySyncNotification.Decode(Hex(HistoryFixture.DownloadedNotification));
    byte[] sync = HistoryBlob.Open(Hex(HistoryFixture.EncryptedBody), notification);

    Assert.AreEqual(HistoryFixture.Plain, Assert.Hex(sync),
        "the downloaded blob is the same history the inline bootstrap carries");
    Assert.AreEqual(2, HistorySync.Decode(sync).Conversations.Count,
        "and it decodes to the two conversations");
}
```

and the refusals the Review Focus lists, each asserting the *sentence* rather than only that
something threw - a `ProtocolException` whose message does not name the field is a `DIAG` line
nobody can act on:

```csharp
[Test]
public static void ANotificationWithNoKeyIsRefusedByName()
{
    var bare = new HistorySyncNotification();
    bare.DirectPath = "/v/t62/hist";
    string said = null;
    try { HistoryBlob.Open(new byte[64], bare); }
    catch (ProtocolException error) { said = error.Message; }
    Assert.IsTrue(said != null, "a blob with no media key cannot be opened");
    Assert.IsTrue(said.Contains("mediaKey"), "the sentence names the missing field: " + said);
}

[Test]
public static void AHashTakenOverTheInflatedPayloadIsRefusedByName()
{
    HistorySyncNotification notification =
        HistorySyncNotification.Decode(Hex(HistoryFixture.DownloadedNotification));
    notification.FileSha256 = Sha256.Hash(Hex(HistoryFixture.Plain));

    string said = null;
    try { HistoryBlob.Open(Hex(HistoryFixture.EncryptedBody), notification); }
    catch (ProtocolException error) { said = error.Message; }
    Assert.IsTrue(said != null, "the media path's own check refuses it");
    Assert.IsTrue(said.Contains("fileSha256"), "and the sentence names the check: " + said);
}

[Test]
public static void ABlobThatIsNotCompressedIsRefused()
{
    string said = null;
    try { Zlib.Inflate(Hex(HistoryFixture.Plain)); }
    catch (Exception error) { said = error.Message; }
    Assert.IsTrue(said != null,
        "a history that arrives uncompressed cannot be inflated: the caller's DIAG line names the blob");
}
```

`Sha256` needs `using WhatsappApp.Protocol.Crypto;` at the top of `HistorySyncTests.cs`.

Add to `ProtocolTests/Vectors/HistoryFixture.cs`, with the Step 1 command in the doc comment:
`DownloadedNotification` (the printed notification: media key `0x07` × 32, `directPath`
`/v/t62/hist`, `fileLength` as printed) and `EncryptedBody` (the printed body).

- [ ] **Step 3: Run it to see it fail**

Run: `node tools/test-protocol.js`
Expected: `HistoryBlob` does not exist yet - the compile fails with
`The name 'HistoryBlob' does not exist in the current context`.

- [ ] **Step 4: Implement `HistoryBlob.Open`**

```csharp
public static byte[] Open(byte[] body, HistorySyncNotification notification)
{
    if (notification == null) throw new ProtocolException("there is no history notification to open");
    if (notification.MediaKey == null || notification.MediaKey.Length != 32)
        throw new ProtocolException("the history blob names no 32-byte mediaKey");
    if (string.IsNullOrEmpty(notification.DirectPath))
        throw new ProtocolException("the history blob names no directPath to fetch");

    byte[] compressed = MediaDownload.Open(body, notification.MediaKey, MediaKeys.History,
        notification.FileSha256, notification.FileEncSha256, notification.FileLength,
        notification.DirectPath);

    return Zlib.Inflate(compressed);
}
```

`Zlib.Inflate` is the one the inline bootstrap already goes through, and it takes both the zlib
wrapper and a raw deflate stream - which is why it is not replaced with `DeflateStream` here.

- [ ] **Step 5: Run it to see it pass**

Run: `node tools/test-protocol.js`
Expected: `OK: 239 protocol test(s)` (235 plus the four in Step 2), exit 0 - the downloaded
fixture decodes to `HistoryFixture.Plain`, byte for byte.

- [ ] **Step 6: Implement `HistoryBlob.FetchAsync`, and add it to the project file**

```csharp
public static async Task<byte[]> FetchAsync(IMediaFetcher fetcher,
    HistorySyncNotification notification)
{
    if (fetcher == null) throw new ProtocolException("a download needs something to fetch with");
    if (notification == null) throw new ProtocolException("there is no history notification to fetch");

    byte[] body = await MediaDownload.FetchAsync(fetcher, notification.MediaKey, MediaKeys.History,
        notification.DirectPath, null, notification.FileSha256, notification.FileEncSha256,
        notification.FileLength);

    return Open(body, notification);
}
```

`MediaDownload.FetchAsync` already builds the URL (`mmg.whatsapp.net` + the path) and checks the
MAC before the decrypt; this adds nothing to that order. Add the `WhatsApp.csproj` line from
**Files** and run `node tools/check-project-files.js` - expected `OK: every .cs and .xaml under
WhatsappApp is listed in the project file.`

Then the two tests that need a fetch, in `ProtocolTests/HistorySyncTests.cs`:

```csharp
[Test]
public static void TheFetchedHistoryBecomesTheSameConversations()
{
    HistorySyncNotification notification =
        HistorySyncNotification.Decode(Hex(HistoryFixture.DownloadedNotification));
    MediaTests.ScriptedFetcher fetcher = new MediaTests.ScriptedFetcher();
    fetcher.Add(MediaDownload.DefaultHost + "/v/t62/hist", Hex(HistoryFixture.EncryptedBody));

    byte[] sync = HistoryBlob.FetchAsync(fetcher, notification).Result;

    Assert.AreEqual(HistoryFixture.Plain, Assert.Hex(sync),
        "the fetch is the path the phone takes, and it ends at the inline fixture's own bytes");
}

[Test]
public static void AFailedDownloadNamesItsAddress()
{
    HistorySyncNotification notification =
        HistorySyncNotification.Decode(Hex(HistoryFixture.DownloadedNotification));
    string said = null;
    try { HistoryBlob.FetchAsync(new MediaTests.ScriptedFetcher(), notification).Wait(); }
    catch (AggregateException error) { said = error.InnerException.Message; }
    Assert.IsTrue(said != null, "a download that fails is raised, not swallowed");
    Assert.IsTrue(said.Contains("/v/t62/hist"), "and the sentence names the address: " + said);
}
```

- [ ] **Step 7: The guard and the suite**

Run: `node tools/check-csharp5.js && node tools/check-project-files.js && node tools/test-protocol.js`
Expected: exit 0 each, `OK: 241 protocol test(s)` (239 plus the two Step 6 adds).

- [ ] **Step 8: Commit**

```bash
git add WhatsappApp/Protocol/Media/HistoryBlob.cs WhatsappApp/WhatsApp.csproj ProtocolTests/HistorySyncTests.cs ProtocolTests/Vectors/HistoryFixture.cs
git commit -m "feat: the history behind a media download is fetched and inflated"
```

**Ruling - the three values the media path checks are the notification's own.**
`FileSha256`, `FileEncSha256` and `FileLength` go to `MediaDownload.Open` unchanged, because a
history blob is an encrypted media blob like any other and the reference's `downloadHistory`
inflates *after* its download, so the numbers describe the compressed plaintext this client
hands to its own checks. Why not skip them and keep only the MAC: the MAC proves the sender, the
hashes prove the *file*, and a client that drops checks to make an empty screen go away is the
mistake this plan exists to end. Cost if wrong: the phone logs `decrypted to something else than
fileSha256 says`, which names the case instead of hiding it - and the fix is then to inflate
before hashing, a change confined to this file.

---

### Task 4: the wiring, and the phone that shows its chats

**Files:**
- Modify: `WhatsappApp/Services/NativeConnection.cs:404-415` (`ReadMessageAsync` calls the async
  form), `WhatsappApp/Services/NativeConnection.cs:1064-1100` (`ApplyHistory` -> a private
  `ApplySync(HistorySync)` plus the new async entry point)
- Modify: `.agents/skills/test-the-app/SKILL.md` (the on-device checklist: an item that says what
  a fresh pairing must show)

**Interfaces:**
- Consumes: `HistoryBlob.FetchAsync`, `HistorySyncNotification.{InlinePayload, MediaKey,
  DirectPath, SyncType}`, `WinRtMediaFetcher` (the same one the message media path constructs),
  `Diag.Ok`/`Diag.Failed`.
- Produces: `private async Task ApplyHistoryAsync(HistorySyncNotification notification)` in
  `NativeConnection`, and the log lines the device run is read against.

- [ ] **Step 1: Name the shape before anything else**

Change the first branch of `ApplyHistory` so that a notification carrying **neither** an inline
payload **nor** a media key with a path says so by name:

`Diag.Ok("native: history sync type " + notification.SyncType + " carries neither an inline payload nor a mediaKey/directPath")`

Expected after this step, on a phone whose server sent no history at all: that line, and not the
silence the phone has today.

- [ ] **Step 2: Make the caller async and reach for the blob**

In `ReadMessageAsync`, `applyHistory(history)` becomes `await ApplyHistoryAsync(history)` -
the acknowledgement is already sent before this point, so the download cannot delay it, and the
stanza reader waits for exactly as long as the message media path already makes it wait.

Update the doc comment above the method while it is open: it still says *"The download half is
not written yet, and it says so instead of pretending the history was empty"*, which stops
being true here.

Split the existing body so both shapes share one apply:
`ApplySync(HistorySync sync)` keeps everything from `ApplyCalls(sync)` to the
`HistoryFinished` raise; `ApplyHistoryAsync` decides:
`InlinePayload != null` -> `ApplySync(HistorySync.Decode(Zlib.Inflate(notification.InlinePayload)))`
exactly as today; otherwise `ApplySync(HistorySync.Decode(await HistoryBlob.FetchAsync(new WinRtMediaFetcher(), notification)))`,
inside a `try`/`catch` that answers `Diag.Failed("NativeConnection/history", error)` and returns.
A failed download must not end the login: the socket loop this runs on is the one every message
arrives on.

- [ ] **Step 3: The guards that own the two new sentences**

Run: `node tools/check-fire-and-forget.js && node tools/check-csharp5.js && node tools/check-project-files.js`
Expected: `OK` and exit 0 each. A `Task` nobody awaits, or a `#pragma warning disable 4014` whose
fault nobody observes, fails the first one - the call is `await`ed here, so it does not.

- [ ] **Step 4: The whole fast gate**

Run: `node tools/test-protocol.js`, then every guard named in `test-the-app` in turn, then
`node --test "tools/test/**/*.test.js"`.
Expected: 241 protocol tests, 112 tool tests, 0 failures, `OK` on each guard.

- [ ] **Step 5: The build gate**

Run: `msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86`
Expected: `0 Error(s)` and `Your package has been successfully created.` (If a build reports
`error CS1061` from `obj\Pages\...`, that is a stale XAML copy and not this change: delete
`WhatsappApp/obj` and `WhatsappApp/bin` and build again - `test-the-app` records the case.)

- [ ] **Step 6: The device checklist item**

In `.agents/skills/test-the-app/SKILL.md`, under **On-device checklist**, add one item:

> Link the phone to an account whose WhatsApp has conversations: **within a minute of the
> pairing, the chat list shows them** - most recent first, with the last message as the preview
> - and no empty state stays on screen. The run's `DIAG` lines name the history as it arrives:
> `native: history sync type 0 carried N conversation(s), M row(s) now`, then
> `native: the history is complete, N row(s) in the batch, M in the list`. A list that stays
> empty with `behind a media download` in the log means the blob was never fetched, and a
> `DIAG failed: NativeConnection/history` line names the address that refused.

Run `node tools/check-docs.js` after the edit: expected
`OK: 1 doc pair(s) in step ... no emoji in N .md file(s)`, exit 0.

- [ ] **Step 7: The device run - the acceptance**

Deploy the ARM package, link the account, and read the list and the log against Task 1's table.
Expected: the conversations are on screen, the log carries the two `native: history sync` lines
and then `native: the history is complete`, and no `DIAG failed` line names
`NativeConnection/history`.

- [ ] **Step 8: Commit**

```bash
git add WhatsappApp/Services/NativeConnection.cs .agents/skills/test-the-app/SKILL.md
git commit -m "fix: the history a pairing leaves behind a download now fills the chat list"
```

---

## Progress

Filled in as the plan runs; Task 1's lines are recorded here the day they are read.

| Task | Commit | State |
| --- | --- | --- |
| 1. the phone run that names the case | - | not started |
| 2. the `md-msg-hist` keys | - | not started |
| 3. `HistoryBlob` | - | not started |
| 4. the wiring and the device | - | not started |

What the phone said (Task 1, Step 4):

| Date | `native: history sync ...` line | Which case |
| --- | --- | --- |
| - | - | - |
