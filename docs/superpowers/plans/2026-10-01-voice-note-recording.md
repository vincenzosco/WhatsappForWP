# Voice Note Recording Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user record a voice note with the phone microphone on the chat page and send it, so it reaches the contact as a WhatsApp voice note.

**Architecture:** A new `AudioRecorder` service wraps `Windows.Media.Capture.MediaCapture` and writes an AAC/M4A file into `LocalFolder`. The chat page grows a record/stop button pair and a recording bar; on stop the file goes through the existing `AttachmentInbox` slot, appears in the preview bar, and the existing `ChatPage.SendAttachmentAsync` streams it out with the existing `media.begin`/`media.chunk`/`media.end` frames. The adapter learns one new route, `POST /send/audio`, which GOWA turns into a real WhatsApp voice note (waveform), instead of sending the recording as a generic file.

**Tech Stack:** C# 5 / WinRT (`Windows.Media.Capture`, `Windows.Media.MediaProperties`) for the Windows Phone 8.1 app; XAML for the UI; Node.js 18 + `node:test` for the GOWA adapter.

## Global Constraints

These apply to every task below; they are copied verbatim from the project's guards and conventions.

- **C# 5 only.** No interpolated strings, no `?.`, no expression-bodied members, no `nameof`, no inline `out var`, no auto-property initializers, no `using static`. An `await` is **forbidden inside a `catch` or `finally` body** (`CS1985`). `tools/check-csharp5.js` fails on all of these.
- **LINQ extension methods** (`.Any(`, `.Where(`, ...) require `using System.Linq;` in the file, or the WP8.1 compiler answers `CS1061`.
- **Every file is LF and has no BOM.** After creating or editing: `perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' <files>`.
- **A new `.cs` file must be registered in `WhatsappApp/WhatsappApp.csproj`** as a `<Compile Include="Services\X.cs" />` item, or it is never compiled.
- **Never build Any CPU** (the manifest pins `AppxBundlePlatforms=arm`). Only ARM (phone) or x86 (emulator).
- **Buttons** (`tools/check-actions.js`): a button named `X` is wired only to `Click="X_Click"`; a button that declares `Width` must also declare `MinWidth="0"` and `MinHeight="0"`.
- **Icons** (`tools/check-icons.js`): no `Segoe MDL2 Assets`; a `Geometry` never travels through a `ResourceDictionary`; no `Figures="M..."` (must be `PathFigure` + `LineSegment`/`PolyLineSegment`/`ArcSegment`); every `<Path>` carries an `<!-- IconX -->` comment; the same icon name must be the same geometry everywhere.
- **Strings** (`tools/check-resw.js --strict`): every key exists in **both** `Strings/en-US/Resources.resw` and `Strings/it-IT/Resources.resw`, and under `--strict` no key may be unused. Every literal `Text`/`Content`/`PlaceholderText`/`Header` attribute in XAML needs an `x:Uid` whose `X.Attr` key exists.
- **Decoded bitmaps** (`tools/check-memory.js`): every `ImageHelper.From*Async` call passes a decode width of **720 or less**. This task adds no bitmap decoding.
- **Frame ceiling** is `8 * 1024 * 1024` on both sides (`tools/check-framing.js`).
- **No emoji** in any Markdown file, except the warning sign U+26A0 (`tools/check-docs.js`).
- **Commit messages contain no apostrophe** (the shell/`perl` normalizers mangle them). `README.md`/`README.it.md` and `WhatsappBridge/README.md`/`WhatsappBridge/README.it.md` must always have the same headings, at the same levels, in the same order.
- **The Docker mirror** at `/Users/vincenzo/Documents/docker-whatsappforwp` (branch `main`) copies `WhatsappBridge/` into its `server/`. After any adapter change: `cd /Users/vincenzo/Documents/docker-whatsappforwp && node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP`, then `node tools/sync.js --check --from /Users/vincenzo/Documents/WhatsappForWP` (must print `OK: server/ matches the adapter (N file(s))`), then `cd server && npm test`.

**The fast gate** (run it at the end of every task that touches the app):

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && \
node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && \
node tools/check-memory.js && node tools/check-actions.js
node --test "tools/test/**/*.test.js"
```

Both must exit 0. The adapter suite is separate: `cd WhatsappBridge && npm test`.

**The ARM build** (Windows 11 Parallels VM, from a non-symlink copy). Run from the repo root:

```bash
prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rd /s /q C:\Temp\wp81 & robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP > C:\Temp\copy.log 2>&1 & cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=ARM /nologo /v:m > C:\Temp\build.log 2>&1 & powershell -NoProfile -Command \"(Get-Content C:\Temp\build.log -Tail 40) | Set-Content C:\Mac\Home\Documents\WhatsappForWP\build-vm.log\"; echo DONE"
```

Then read `build-vm.log` in the repo root and **delete it**. Expected: `0 Error(s)`. `DEP6100`/`DEP6200` are "no phone attached", not build errors; `WMC9999` and a "converter does not exist" line are the known `C:\Mac\Home` symlink noise. After a build, restore anything Visual Studio rewrote: `git checkout -- WhatsappApp/Package.appxmanifest WhatsappApp/WhatsappApp.csproj`.

---

### Task 1: Adapter sends a voice note through `POST /send/audio`

**Files:**
- Modify: `WhatsappBridge/gowa-client.js` (add `sendAudio` immediately after `sendFile`, around line 218)
- Modify: `WhatsappBridge/server.js` (`sendMediaToGowa`, around line 790-806)
- Test: `WhatsappBridge/test/gowa-client.test.js`
- Test: `WhatsappBridge/test/server.test.js`

**Interfaces:**
- Consumes: the existing `GowaClient.postMedia(path, field, phone, caption, buffer, mimeType, fileName)` helper.
- Produces: `GowaClient.sendAudio(phone, caption, buffer, mimeType, fileName) -> Promise<string>` (the message id). `sendMediaToGowa` routes a payload whose `mediaKindOf` is `"audio"` to `session.gowa.sendAudio(...)` when that method exists, and falls back to `session.gowa.sendFile(...)` when it does not.

- [ ] **Step 1: Write the failing client test**

Append to `WhatsappBridge/test/gowa-client.test.js`:

```js
test('sendAudio posts the audio field on /send/audio', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://g',
    fetchImpl: async (url, options) => {
      seen.push({ url, field: [...options.body.keys()].join(',') });
      return jsonResponse({ status: 200, results: { message_id: 'A1' } });
    }
  });

  assert.strictEqual(
    await client.sendAudio('39@s.whatsapp.net', '', Buffer.from([3]), 'audio/mp4', 'voce.m4a'),
    'A1');

  assert.strictEqual(seen[0].url, 'http://g/send/audio');
  assert.ok(seen[0].field.includes('audio'));
  assert.ok(seen[0].field.includes('phone'));
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: FAIL — `client.sendAudio is not a function`.

- [ ] **Step 3: Add `sendAudio` to the client**

In `WhatsappBridge/gowa-client.js`, after the `sendFile` method, add:

```js
  /**
   * A voice note. GOWA turns it into a WhatsApp voice note (a playable waveform,
   * not a document), and it is the only route that does: a file sent through
   * /send/file with an audio MIME type arrives as an attachment. Field name
   * `audio`, path /send/audio.
   */
  async sendAudio(phone, caption, buffer, mimeType, fileName) {
    return this.postMedia('/send/audio', 'audio', phone, caption, buffer,
      mimeType || 'audio/ogg', fileName || 'voice-note.ogg');
  }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: PASS.

- [ ] **Step 5: Write the failing adapter test (routing)**

Append to `WhatsappBridge/test/server.test.js` (it already has the `mediaBridge(gowa)` helper at ~line 649):

```js
test('un vocale inviato va a sendAudio, non a sendFile', async () => {
  const delivered = [];
  const gowa = {
    sendAudio: async (phone, caption, buffer, mimeType, fileName) => {
      delivered.push({ door: 'audio', mimeType, fileName });
      return 'A1';
    },
    sendFile: async () => { throw new Error('un vocale non passa da sendFile'); },
    sendImage: async () => { throw new Error('un vocale non e un immagine'); },
    sendVideo: async () => { throw new Error('un vocale non e un video'); }
  };
  const bridge = mediaBridge(gowa);

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'v1', MediaFileName: 'voce.m4a', MediaMimeType: 'audio/mp4', MediaChunkTotal: 1 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'v1', MediaChunkIndex: 0, MediaData: Buffer.from('voce').toString('base64') });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'v1' });

  assert.deepStrictEqual(delivered, [
    { door: 'audio', mimeType: 'audio/mp4', fileName: 'voce.m4a' }
  ]);
});

test('senza sendAudio nel client un vocale ripiega su sendFile', async () => {
  const delivered = [];
  const gowa = {
    sendFile: async (phone, caption, buffer, mimeType, fileName) => {
      delivered.push({ door: 'file', mimeType, fileName });
      return 'F1';
    },
    sendImage: async () => { throw new Error('un vocale non e un immagine'); },
    sendVideo: async () => { throw new Error('un vocale non e un video'); }
  };
  const bridge = mediaBridge(gowa);

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'v2', MediaFileName: 'voce.m4a', MediaMimeType: 'audio/mp4', MediaChunkTotal: 1 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'v2', MediaChunkIndex: 0, MediaData: Buffer.from('voce').toString('base64') });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'v2' });

  assert.deepStrictEqual(delivered, [
    { door: 'file', mimeType: 'audio/mp4', fileName: 'voce.m4a' }
  ]);
});
```

- [ ] **Step 6: Run the test to verify it fails**

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: FAIL — the first test throws `un vocale non passa da sendFile` (audio currently falls through to `sendFile`).

- [ ] **Step 7: Route audio in `sendMediaToGowa`**

In `WhatsappBridge/server.js`, replace the three closing lines of `sendMediaToGowa`:

```js
    if (kind === 'video') return session.gowa.sendVideo(chatId, caption || '', buffer, mimeType || 'video/mp4', fileName);
    if (kind === 'image') return session.gowa.sendImage(chatId, caption || '', buffer, mimeType || 'image/jpeg', fileName);
    return session.gowa.sendFile(chatId, caption || '', buffer, mimeType || 'application/octet-stream', fileName);
```

with:

```js
    if (kind === 'video') return session.gowa.sendVideo(chatId, caption || '', buffer, mimeType || 'video/mp4', fileName);
    if (kind === 'image') return session.gowa.sendImage(chatId, caption || '', buffer, mimeType || 'image/jpeg', fileName);
    // An audio payload is a voice note, and only /send/audio makes it one: it
    // is sent there when the client knows the route, and as a file otherwise
    // (an adapter talking to an older GOWA must keep working).
    if (kind === 'audio' && typeof session.gowa.sendAudio === 'function') {
      return session.gowa.sendAudio(chatId, caption || '', buffer, mimeType || 'audio/ogg', fileName || 'voice-note.ogg');
    }
    return session.gowa.sendFile(chatId, caption || '', buffer, mimeType || 'application/octet-stream', fileName);
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `cd WhatsappBridge && npm test`
Expected: all tests pass (199 + 3 new = 202).

- [ ] **Step 9: Normalize line endings and commit**

```bash
perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' WhatsappBridge/gowa-client.js WhatsappBridge/server.js WhatsappBridge/test/gowa-client.test.js WhatsappBridge/test/server.test.js
git add WhatsappBridge/gowa-client.js WhatsappBridge/server.js WhatsappBridge/test/gowa-client.test.js WhatsappBridge/test/server.test.js
git commit -m "The adapter sends a voice note on send audio, not as a file"
```

---

### Task 2: `AudioRecorder` service and the microphone capability

**Files:**
- Create: `WhatsappApp/Services/AudioRecorder.cs`
- Modify: `WhatsappApp/Package.appxmanifest` (add a `DeviceCapability`)
- Modify: `WhatsappApp/WhatsappApp.csproj` (register the new file)

**Interfaces:**
- Produces:
  - `static Task<bool> AudioRecorder.StartAsync()` — starts capturing audio into `LocalFolder\voice_note.m4a`; `false` on any failure (permission denied, no microphone, `MediaCapture` unavailable).
  - `static Task<string> AudioRecorder.StopAsync()` — stops and returns `"voice_note.m4a"`, or `null` when nothing usable was recorded.
  - `static Task AudioRecorder.CancelAsync()` — stops and releases without returning a file.
  - `static bool AudioRecorder.IsRecording { get; }`

- [ ] **Step 1: Write the service**

Create `WhatsappApp/Services/AudioRecorder.cs`:

```csharp
using System;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Records a voice note into the app folder.
    ///
    /// Why it exists: the app could play a voice note but not make one. The
    /// platform call is MediaCapture, initialized for audio only (no camera
    /// preview, no CaptureElement: there is nothing to show while recording),
    /// and recording straight to a StorageFile in LocalFolder.
    ///
    /// The format is AAC in an M4A container (MediaEncodingProfile.CreateM4a):
    /// it is what Windows Phone 8.1 records and decodes without help. Ogg/Opus
    /// is what WhatsApp prefers, but the phone has no Opus encoder, and the
    /// adapter cannot convert in the outgoing direction.
    ///
    /// Every failure is answered with false/null and a Diag line: a denied
    /// microphone, a device with none, a capture engine the projection refuses.
    /// The caller shows the sentence and stays usable.
    /// </summary>
    public static class AudioRecorder
    {
        /// <summary>The recorded file, inside LocalFolder. One at a time.</summary>
        public const string FileName = "voice_note.m4a";

        private static MediaCapture _capture;

        /// <summary>A capture is running.</summary>
        public static bool IsRecording
        {
            get { return _capture != null; }
        }

        /// <summary>
        /// Starts recording the microphone into LocalFolder. It returns false
        /// instead of throwing: the caller has a sentence to show and no way to
        /// recover from an exception.
        /// </summary>
        public static async Task<bool> StartAsync()
        {
            if (_capture != null) return false;

            try
            {
                // Audio only. StreamingCaptureMode.Audio is what keeps the camera
                // out of it: the default would ask for video too, and that is a
                // different capability.
                var settings = new MediaCaptureInitializationSettings();
                settings.StreamingCaptureMode = StreamingCaptureMode.Audio;

                var capture = new MediaCapture();
                await capture.InitializeAsync(settings);

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);

                await capture.StartRecordToStorageFileAsync(
                    MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Auto), file);

                _capture = capture;
                return true;
            }
            catch (Exception ex)
            {
                Diag.Failed("AudioRecorder.StartAsync", ex);
                return false;
            }
        }

        /// <summary>
        /// Stops the recording and returns the name of the file in LocalFolder,
        /// or null when the capture failed or wrote nothing (a tap that lasted a
        /// moment produces an empty file, and an empty voice note is not one).
        /// </summary>
        public static async Task<string> StopAsync()
        {
            MediaCapture capture = _capture;
            if (capture == null) return null;

            bool stopped = false;
            try
            {
                await capture.StopRecordAsync();
                stopped = true;
            }
            catch (Exception ex)
            {
                // Not rethrown and not awaited inside catch: C# 5 answers CS1985.
                Diag.Failed("AudioRecorder.StopAsync", ex);
            }

            Release(capture);

            if (!stopped) return null;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                var properties = await file.GetBasicPropertiesAsync();
                if (properties.Size == 0) return null;
            }
            catch (Exception ex)
            {
                Diag.Failed("AudioRecorder.StopAsync/size", ex);
                return null;
            }

            return FileName;
        }

        /// <summary>
        /// Stops and throws the recording away: the user left the page, or
        /// started again. The file stays on disk and the next recording replaces
        /// it (CreationCollisionOption.ReplaceExisting).
        /// </summary>
        public static async Task CancelAsync()
        {
            MediaCapture capture = _capture;
            if (capture == null) return;

            try
            {
                await capture.StopRecordAsync();
            }
            catch (Exception ex)
            {
                Diag.Failed("AudioRecorder.CancelAsync", ex);
            }

            Release(capture);
        }

        /// <summary>Releases the capture exactly once and forgets it.</summary>
        private static void Release(MediaCapture capture)
        {
            _capture = null;
            try
            {
                capture.Dispose();
            }
            catch (Exception ex)
            {
                Diag.Failed("AudioRecorder.Release", ex);
            }
        }
    }
}
```

- [ ] **Step 2: Register the file in the csproj**

In `WhatsappApp/WhatsappApp.csproj`, after `<Compile Include="Services\VideoThumbnail.cs" />`, add:

```xml
    <Compile Include="Services\AudioRecorder.cs" />
```

- [ ] **Step 3: Declare the microphone capability**

In `WhatsappApp/Package.appxmanifest`, replace the `<Capabilities>` block:

```xml
  <Capabilities>
    <Capability Name="internetClientServer" />
    <Capability Name="privateNetworkClientServer" />
    <!-- Serve per ricevere i beacon UDP dell'adapter sulla rete locale. -->
  </Capabilities>
```

with:

```xml
  <Capabilities>
    <Capability Name="internetClientServer" />
    <Capability Name="privateNetworkClientServer" />
    <!-- Serve per ricevere i beacon UDP dell'adapter sulla rete locale. -->
    <!-- Il microfono del vocale. E' una DeviceCapability e non una Capability:
             senza di essa MediaCapture.InitializeAsync risponde accesso negato e
             la registrazione non parte mai. -->
    <DeviceCapability Name="microphone" />
  </Capabilities>
```

- [ ] **Step 4: Normalize line endings**

```bash
perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' WhatsappApp/Services/AudioRecorder.cs WhatsappApp/Package.appxmanifest WhatsappApp/WhatsappApp.csproj
```

- [ ] **Step 5: Run the fast gate to verify nothing broke**

Run:
```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && \
node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && \
node tools/check-memory.js && node tools/check-actions.js
```
Expected: every guard prints `OK:` and the script exits 0.

- [ ] **Step 6: Build ARM to verify the API surface compiles**

Run the ARM build command from **Global Constraints**, then read `build-vm.log`.

Expected: `0 Error(s)`. This is the step that proves `Windows.Media.Capture.MediaCapture`, `MediaCaptureInitializationSettings.StreamingCaptureMode`, `AudioEncodingQuality` and `StartRecordToStorageFileAsync` all exist in this WP8.1 projection. If the compiler answers `CS0234`/`CS1061` here, stop and reconsider the format: the fallback is the same service recording through `StartRecordToStorageFileAsync` with `MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low)`, but do not change it without a failing build to justify it.

Delete `build-vm.log` afterwards, and restore anything VS rewrote:
```bash
rm -f build-vm.log
git checkout -- WhatsappApp/Package.appxmanifest WhatsappApp/WhatsappApp.csproj
```

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Services/AudioRecorder.cs WhatsappApp/WhatsappApp.csproj WhatsappApp/Package.appxmanifest
git commit -m "Record a voice note with the phone microphone, as M4A"
```

---

### Task 3: `AttachmentInbox` knows what audio is

**Files:**
- Modify: `WhatsappApp/Services/AttachmentInbox.cs` (`MimeFor`, `ExtensionFor`, `KindName`)

**Interfaces:**
- Produces: `AttachmentInbox.KindName(mimeType, fileName)` returns `"audio"` for an `audio/*` MIME type or a `.m4a`/`.aac`/`.mp3`/`.wav`/`.amr`/`.ogg`/`.oga`/`.opus` name; `MimeFor(".m4a")` returns `"audio/mp4"`; `ExtensionFor("audio/mp4", null)` returns `".m4a"`.
- Consumes: nothing new.

- [ ] **Step 1: Add the audio MIME types**

In `WhatsappApp/Services/AttachmentInbox.cs`, inside `MimeFor`, after the line `if (value == ".webm") return "video/webm";`, insert:

```csharp
            if (value == ".m4a") return "audio/mp4";
            if (value == ".aac") return "audio/aac";
            if (value == ".mp3") return "audio/mpeg";
            if (value == ".wav") return "audio/wav";
            if (value == ".amr") return "audio/amr";
            if (value == ".ogg") return "audio/ogg";
            if (value == ".oga") return "audio/ogg";
            if (value == ".opus") return "audio/ogg";
```

- [ ] **Step 2: Give an audio MIME type an extension**

In `ExtensionFor`, replace:

```csharp
            if (mime.StartsWith("image/")) return ".jpg";
            // Anything else is a file, and a file with no name has no extension
            // worth inventing.
            return ".bin";
```

with:

```csharp
            if (mime.StartsWith("image/")) return ".jpg";
            // A recording with no name: the format the phone records is the one
            // that comes back. Without this a voice note MIME type would land on
            // ".bin", which is not a file the phone can play.
            if (mime.StartsWith("audio/")) return ".m4a";
            // Anything else is a file, and a file with no name has no extension
            // worth inventing.
            return ".bin";
```

- [ ] **Step 3: Teach `KindName` the audio word**

Replace the whole `KindName` method:

```csharp
        /// <summary>
        /// "image" or "video": the word the adapter and the app use to decide how
        /// to send and how to draw. The MIME type may be missing (a shared
        /// bitmap), so the word is also derived from the extension.
        /// </summary>
        public static string KindName(string mimeType, string fileName)
        {
            string mime = (mimeType ?? "").ToLower();
            if (mime.StartsWith("video/")) return "video";
            if (mime.StartsWith("image/")) return "image";

            string name = (fileName ?? "").ToLower();
            if (name.EndsWith(".mp4") || name.EndsWith(".mov") || name.EndsWith(".3gp")
                || name.EndsWith(".avi") || name.EndsWith(".mkv") || name.EndsWith(".webm"))
            {
                return "video";
            }
            if (name.EndsWith(".jpg") || name.EndsWith(".jpeg") || name.EndsWith(".png")
                || name.EndsWith(".gif") || name.EndsWith(".bmp"))
            {
                return "image";
            }
            return "document";
        }
```

with:

```csharp
        /// <summary>
        /// "image", "video", "audio" or "document": the word the adapter and the
        /// app use to decide how to send and how to draw. The MIME type may be
        /// missing (a shared bitmap), so the word is also derived from the
        /// extension.
        ///
        /// Audio and not document: a recorded voice note has no extension the
        /// document list knows, and calling it a document is how it used to be
        /// sent as a file instead of a voice note.
        /// </summary>
        public static string KindName(string mimeType, string fileName)
        {
            string mime = (mimeType ?? "").ToLower();
            if (mime.StartsWith("video/")) return "video";
            if (mime.StartsWith("image/")) return "image";
            if (mime.StartsWith("audio/")) return "audio";

            string name = (fileName ?? "").ToLower();
            if (name.EndsWith(".mp4") || name.EndsWith(".mov") || name.EndsWith(".3gp")
                || name.EndsWith(".avi") || name.EndsWith(".mkv") || name.EndsWith(".webm"))
            {
                return "video";
            }
            if (name.EndsWith(".m4a") || name.EndsWith(".aac") || name.EndsWith(".mp3")
                || name.EndsWith(".wav") || name.EndsWith(".amr") || name.EndsWith(".ogg")
                || name.EndsWith(".oga") || name.EndsWith(".opus"))
            {
                return "audio";
            }
            if (name.EndsWith(".jpg") || name.EndsWith(".jpeg") || name.EndsWith(".png")
                || name.EndsWith(".gif") || name.EndsWith(".bmp"))
            {
                return "image";
            }
            return "document";
        }
```

- [ ] **Step 4: Normalize line endings**

```bash
perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' WhatsappApp/Services/AttachmentInbox.cs
```

- [ ] **Step 5: Run the fast gate**

Run the fast gate from **Global Constraints**.
Expected: exit 0, `OK: every decoded bitmap asks for the width it is shown at, ...`.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Services/AttachmentInbox.cs
git commit -m "An audio attachment is audio, not a document"
```

---

### Task 4: The record button, the recording bar and the send path

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml` (input row, preview bar, recording bar)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (handlers, timer, preview, audio message type)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw` (5 new keys)
- Modify: `WhatsappApp/Strings/it-IT/Resources.resw` (the same 5 keys)

**Interfaces:**
- Consumes: `AudioRecorder.StartAsync`/`StopAsync`/`CancelAsync` (Task 2), `AttachmentInbox.PutAsync` and `AttachmentInbox.KindName` (Task 3), `CommunicationService.SendMediaBeginAsync`/`SendMediaChunkAsync`/`SendMediaEndAsync` (existing).
- Produces: `ChatPage.RecordButton_Click`, `ChatPage.StopRecordButton_Click`, `ChatPage.RecordTimer_Tick`, `ChatPage.StartRecordingAsync`, `ChatPage.StopRecordingAsync`, `ChatPage.CancelRecording`; the XAML names `RecordButton`, `StopRecordButton`, `RecordingBar`, `RecordTimerText`, `SelectedAudioPreview`.

- [ ] **Step 1: Add the five strings to both resource files**

In `WhatsappApp/Strings/en-US/Resources.resw`, after the `ChatPage_DocumentSelected` entry, insert:

```xml
  <data name="ChatPage_AudioSelected" xml:space="preserve">
    <value>Voice note selected</value>
  </data>
  <data name="ChatPage_RecordingLabel.Text" xml:space="preserve">
    <value>Recording</value>
  </data>
  <data name="ChatPage_RecordTooltip" xml:space="preserve">
    <value>Record a voice note</value>
  </data>
  <data name="ChatPage_StopRecordTooltip" xml:space="preserve">
    <value>Stop the recording</value>
  </data>
  <data name="ChatPage_RecordError" xml:space="preserve">
    <value>Could not start the recording. Check that this app may use the microphone.</value>
  </data>
```

In `WhatsappApp/Strings/it-IT/Resources.resw`, after the `ChatPage_DocumentSelected` entry, insert:

```xml
  <data name="ChatPage_AudioSelected" xml:space="preserve">
    <value>Vocale selezionato</value>
  </data>
  <data name="ChatPage_RecordingLabel.Text" xml:space="preserve">
    <value>Registrazione</value>
  </data>
  <data name="ChatPage_RecordTooltip" xml:space="preserve">
    <value>Registra un vocale</value>
  </data>
  <data name="ChatPage_StopRecordTooltip" xml:space="preserve">
    <value>Ferma la registrazione</value>
  </data>
  <data name="ChatPage_RecordError" xml:space="preserve">
    <value>Non e' possibile avviare la registrazione. Controlla che l'app possa usare il microfono.</value>
  </data>
```

- [ ] **Step 2: Add the audio preview to the preview bar**

In `WhatsappApp/Pages/ChatPage.xaml`, immediately after the `SelectedVideoPreview` `</Path>` (the one whose comment is `<!-- IconPlay -->` inside `ImagePreviewBar`), insert:

```xml
                <!-- A voice note: the microphone glyph, where an image shows
                     its thumbnail. Nothing is decoded for it. -->
                <Path x:Name="SelectedAudioPreview" Grid.Column="0"
                      Width="30" Height="30" Margin="17,17,0,17"
                      Fill="#FF606060"
                      Visibility="Collapsed">
                    <!-- IconMicrophone -->
                    <Path.Data>
                        <PathGeometry>
                            <PathGeometry.Figures>
                                <PathFigure StartPoint="10,7" IsClosed="True">
                                    <PathFigure.Segments>
                                        <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="False"
                                                    SweepDirection="Clockwise" Point="14,7"/>
                                        <LineSegment Point="14,12"/>
                                        <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="False"
                                                    SweepDirection="Clockwise" Point="10,12"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                                <PathFigure StartPoint="7,11">
                                    <PathFigure.Segments>
                                        <ArcSegment Size="5,5" RotationAngle="0" IsLargeArc="False"
                                                    SweepDirection="Counterclockwise" Point="17,11"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                                <PathFigure StartPoint="12,16">
                                    <PathFigure.Segments>
                                        <LineSegment Point="12,19"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                                <PathFigure StartPoint="9,19">
                                    <PathFigure.Segments>
                                        <LineSegment Point="15,19"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                            </PathGeometry.Figures>
                        </PathGeometry>
                    </Path.Data>
                </Path>
```

- [ ] **Step 3: Add the recording bar**

In `WhatsappApp/Pages/ChatPage.xaml`, right after the `ImagePreviewBar` closing `</Grid>` and before the `<!-- Input row -->` comment, insert:

```xml
            <!-- Recording: a red dot and the elapsed time. It shares row 0 with
                 the preview bar and never shows together with it. -->
            <Grid x:Name="RecordingBar" Grid.Row="0" Height="64"
                  Background="#FFFCE4E4" Visibility="Collapsed">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                <Ellipse Grid.Column="0" Width="12" Height="12"
                         Margin="16,0,0,0" VerticalAlignment="Center"
                         Fill="#FFE53935"/>
                <TextBlock x:Uid="ChatPage_RecordingLabel" Grid.Column="1"
                           Text="Recording"
                           VerticalAlignment="Center" Margin="10,0,0,0"
                           Foreground="#FF606060" FontSize="13"/>
                <TextBlock x:Name="RecordTimerText" Grid.Column="2"
                           Text=""
                           VerticalAlignment="Center" Margin="8,0,0,0"
                           Foreground="#FF606060" FontSize="13"/>
            </Grid>
```

- [ ] **Step 4: Add the record and stop buttons to the input row**

In `WhatsappApp/Pages/ChatPage.xaml`, replace the input row's column definitions:

```xml
            <!-- Input row -->
            <Grid Grid.Row="1">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
```

with:

```xml
            <!-- Input row -->
            <Grid Grid.Row="1">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
```

Then, immediately before `<Button x:Name="SendButton"`, insert the two buttons:

```xml
                <Button x:Name="RecordButton" Grid.Column="2"
                        Background="Transparent"
                        MinWidth="0" MinHeight="0"
                        Width="48" Height="48" BorderThickness="0" Padding="0"
                        Margin="0,4,0,4"
                        Click="RecordButton_Click">
                    <Path Stroke="#FF808080" StrokeThickness="2"
                          StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                          Width="24" Height="24">
                        <!-- IconMicrophone -->
                        <Path.Data>
                            <PathGeometry>
                                <PathGeometry.Figures>
                                    <PathFigure StartPoint="10,7" IsClosed="True">
                                        <PathFigure.Segments>
                                            <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="False"
                                                        SweepDirection="Clockwise" Point="14,7"/>
                                            <LineSegment Point="14,12"/>
                                            <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="False"
                                                        SweepDirection="Clockwise" Point="10,12"/>
                                        </PathFigure.Segments>
                                    </PathFigure>
                                    <PathFigure StartPoint="7,11">
                                        <PathFigure.Segments>
                                            <ArcSegment Size="5,5" RotationAngle="0" IsLargeArc="False"
                                                        SweepDirection="Counterclockwise" Point="17,11"/>
                                        </PathFigure.Segments>
                                    </PathFigure>
                                    <PathFigure StartPoint="12,16">
                                        <PathFigure.Segments>
                                            <LineSegment Point="12,19"/>
                                        </PathFigure.Segments>
                                    </PathFigure>
                                    <PathFigure StartPoint="9,19">
                                        <PathFigure.Segments>
                                            <LineSegment Point="15,19"/>
                                        </PathFigure.Segments>
                                    </PathFigure>
                                </PathGeometry.Figures>
                            </PathGeometry>
                        </Path.Data>
                    </Path>
                </Button>

                <Button x:Name="StopRecordButton" Grid.Column="3"
                        Background="Transparent"
                        MinWidth="0" MinHeight="0"
                        Width="48" Height="48" BorderThickness="0" Padding="0"
                        Margin="0,4,0,4"
                        Visibility="Collapsed"
                        Click="StopRecordButton_Click">
                    <Path Fill="#FFE53935" Width="20" Height="20">
                        <!-- IconStop -->
                        <Path.Data>
                            <PathGeometry>
                                <PathGeometry.Figures>
                                    <PathFigure StartPoint="6,6" IsClosed="True">
                                        <PathFigure.Segments>
                                            <PolyLineSegment Points="18,6 18,18 6,18"/>
                                        </PathFigure.Segments>
                                    </PathFigure>
                                </PathGeometry.Figures>
                            </PathGeometry>
                        </Path.Data>
                    </Path>
                </Button>

```

Finally, move the send button into the last column: in the `SendButton` start tag change `Grid.Column="2"` to `Grid.Column="4"`.

- [ ] **Step 5: Run the XAML guards to verify names, icons and strings agree**

Run:
```bash
node tools/check-icons.js && node tools/check-actions.js && node tools/check-resw.js --strict
```
Expected: `OK: 26 inline icon Path(s), 16 distinct icon(s).`; `OK: 24 button(s) ...`; `OK: 155 key(s) in en-US and it-IT, ...`.

If `check-icons.js` complains that `IconMicrophone differs from the copy at ...`, the two copies (preview and record button) are not byte-identical: fix the copy, not the guard.

- [ ] **Step 6: Add the recording state and the timer to the page**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, after the `private bool _recording;`-worthy field block near the top (right after `private bool _viewChangedHooked;`), add:

```csharp
        // The recording of a voice note: whether the microphone is capturing,
        // when it started, and the half-second timer that draws how long it has
        // been going.
        private bool _recording;
        private DispatcherTimer _recordTimer;
        private DateTime _recordStarted;
```

- [ ] **Step 7: Wire the two tooltips**

In the `ChatPage` constructor, after the `ClearImageButton` tooltip line, add:

```csharp
            ToolTipService.SetToolTip(RecordButton, Loc.Get("ChatPage_RecordTooltip", "Record a voice note"));
            ToolTipService.SetToolTip(StopRecordButton, Loc.Get("ChatPage_StopRecordTooltip", "Stop the recording"));
```

- [ ] **Step 8: Stop a recording when the page is left**

In `OnNavigatedFrom`, replace:

```csharp
            HideFullScreen();
            StopVideo();
            StopVoice();
```

with:

```csharp
            HideFullScreen();
            StopVideo();
            StopVoice();
            // A recording left running would keep the microphone for a page that
            // is gone, and the file it writes would be sent from another chat.
            CancelRecording();
```

- [ ] **Step 9: Add the recording handlers and the preview branch**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, add these members. Put them immediately before `private void AttachButton_Click`:

```csharp
        /// <summary>
        /// The microphone button. It starts a recording; the stop button ends it.
        /// Two buttons instead of one that changes glyph: the page has no
        /// bindable property to hang a second Path on (its DataContext is the
        /// message list), and toggling Visibility from code is what the rest of
        /// this page already does.
        /// </summary>
        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_recording) return;
#pragma warning disable 4014
            StartRecordingAsync();
#pragma warning restore 4014
        }

        private async System.Threading.Tasks.Task StartRecordingAsync()
        {
            bool started = await AudioRecorder.StartAsync();
            if (!started)
            {
                await new MessageDialog(
                    Loc.Get("ChatPage_RecordError", "Could not start the recording.")).ShowAsync();
                return;
            }

            _recording = true;
            _recordStarted = DateTime.Now;
            RecordTimerText.Text = "0:00";
            RecordingBar.Visibility = Visibility.Visible;
            RecordButton.Visibility = Visibility.Collapsed;
            StopRecordButton.Visibility = Visibility.Visible;
            StartRecordTimer();
        }

        private void StopRecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_recording) return;
#pragma warning disable 4014
            StopRecordingAsync();
#pragma warning restore 4014
        }

        /// <summary>
        /// Ends the recording and puts the file in the waiting slot, the same one
        /// the picker uses: the preview bar shows it, and Send streams it out
        /// through SendAttachmentAsync like every other attachment.
        /// </summary>
        private async System.Threading.Tasks.Task StopRecordingAsync()
        {
            string fileName = await AudioRecorder.StopAsync();
            EndRecordingState();

            if (string.IsNullOrEmpty(fileName)) return;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(fileName);
                await AttachmentInbox.PutAsync(file, null);
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.StopRecordingAsync", ex);
            }
        }

        /// <summary>
        /// The recording is thrown away: the page is being left. No file is
        /// deposited, so nothing can be sent by mistake from another chat.
        /// </summary>
        private void CancelRecording()
        {
            if (!_recording) return;
            EndRecordingState();
#pragma warning disable 4014
            AudioRecorder.CancelAsync();
#pragma warning restore 4014
        }

        /// <summary>The buttons and the bar go back to their resting state.</summary>
        private void EndRecordingState()
        {
            _recording = false;
            StopRecordTimer();
            RecordingBar.Visibility = Visibility.Collapsed;
            RecordButton.Visibility = Visibility.Visible;
            StopRecordButton.Visibility = Visibility.Collapsed;
        }

        private void StartRecordTimer()
        {
            if (_recordTimer == null)
            {
                _recordTimer = new DispatcherTimer();
                _recordTimer.Interval = TimeSpan.FromMilliseconds(500);
                _recordTimer.Tick += RecordTimer_Tick;
            }
            _recordTimer.Start();
        }

        private void StopRecordTimer()
        {
            if (_recordTimer != null) _recordTimer.Stop();
        }

        private void RecordTimer_Tick(object sender, object e)
        {
            RecordTimerText.Text = FormatClock((DateTime.Now - _recordStarted).TotalSeconds);
        }

```

- [ ] **Step 10: Show an audio attachment in the preview bar**

In `ShowPendingAttachment`, replace the block from `string kind = AttachmentInbox.KindName(...)` through the closing of the `else` that clears `SelectedImagePreview.Source`:

```csharp
            string kind = AttachmentInbox.KindName(_selectedMediaMimeType, _selectedMediaFileName);
            bool video = kind == "video";
            bool document = kind == "document";

            PreviewLabel.Text = video
                ? Loc.Get("ChatPage_VideoSelected", "Video selected")
                : (document
                    ? Loc.Get("ChatPage_DocumentSelected", "Document selected")
                    : Loc.Get("ChatPage_ImageSelected.Text", "Image selected"));

            SelectedVideoPreview.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            SelectedImagePreview.Visibility = (!video && !document) ? Visibility.Visible : Visibility.Collapsed;
            SelectedDocumentPreview.Visibility = document ? Visibility.Visible : Visibility.Collapsed;
            SelectedDocumentPreviewText.Text = document ? _selectedMediaFileName : "";

            ImagePreviewBar.Visibility = Visibility.Visible;
            if (!video && !document)
            {
#pragma warning disable 4014
                ShowLocalPreviewAsync(_selectedLocalFileName);
#pragma warning restore 4014
            }
            else
            {
                // A video is not decoded here, and a document has nothing to
                // draw: the bar says what is being sent.
                SelectedImagePreview.Source = null;
            }
```

with:

```csharp
            string kind = AttachmentInbox.KindName(_selectedMediaMimeType, _selectedMediaFileName);
            bool video = kind == "video";
            bool document = kind == "document";
            bool audio = kind == "audio";

            PreviewLabel.Text = video
                ? Loc.Get("ChatPage_VideoSelected", "Video selected")
                : (document
                    ? Loc.Get("ChatPage_DocumentSelected", "Document selected")
                    : (audio
                        ? Loc.Get("ChatPage_AudioSelected", "Voice note selected")
                        : Loc.Get("ChatPage_ImageSelected.Text", "Image selected")));

            SelectedVideoPreview.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            SelectedAudioPreview.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
            SelectedImagePreview.Visibility = (!video && !document && !audio)
                ? Visibility.Visible : Visibility.Collapsed;
            SelectedDocumentPreview.Visibility = document ? Visibility.Visible : Visibility.Collapsed;
            SelectedDocumentPreviewText.Text = document ? _selectedMediaFileName : "";

            ImagePreviewBar.Visibility = Visibility.Visible;
            if (!video && !document && !audio)
            {
#pragma warning disable 4014
                ShowLocalPreviewAsync(_selectedLocalFileName);
#pragma warning restore 4014
            }
            else
            {
                // A video is not decoded here, a document has nothing to draw,
                // and a voice note is not a picture: the bar says what is being
                // sent.
                SelectedImagePreview.Source = null;
            }
```

- [ ] **Step 11: Clear the audio preview too**

In `ClearSelectedImage`, replace:

```csharp
            SelectedImagePreview.Source = null;
            SelectedDocumentPreview.Visibility = Visibility.Collapsed;
            SelectedDocumentPreviewText.Text = "";
```

with:

```csharp
            SelectedImagePreview.Source = null;
            SelectedAudioPreview.Visibility = Visibility.Collapsed;
            SelectedDocumentPreview.Visibility = Visibility.Collapsed;
            SelectedDocumentPreviewText.Text = "";
```

- [ ] **Step 12: Send audio as an audio message**

In `SendAttachmentAsync`, replace:

```csharp
            MessageType type = kind == "video" ? MessageType.Video
                : (kind == "image" ? MessageType.Image : MessageType.Text);
```

with:

```csharp
            MessageType type = kind == "video" ? MessageType.Video
                : (kind == "image" ? MessageType.Image
                : (kind == "audio" ? MessageType.Audio : MessageType.Text));
```

- [ ] **Step 13: Normalize line endings**

```bash
perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw
```

- [ ] **Step 14: Run the fast gate**

Run the fast gate from **Global Constraints**, then `node --test "tools/test/**/*.test.js"`.
Expected: exit 0 on every guard; 60 tests pass.

- [ ] **Step 15: Build ARM**

Run the ARM build command from **Global Constraints**, read `build-vm.log`, expect `0 Error(s)`.

```bash
rm -f build-vm.log
git checkout -- WhatsappApp/Package.appxmanifest WhatsappApp/WhatsappApp.csproj
```

- [ ] **Step 16: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw
git commit -m "Record a voice note from the chat page and send it"
```

---

### Task 5: Documentation and the Docker mirror

**Files:**
- Modify: `README.md` (a paragraph in `#### Chats and new chats`)
- Modify: `README.it.md` (the same paragraph, translated)
- Modify: `WhatsappBridge/README.md` (`### Voice notes need ffmpeg`)
- Modify: `WhatsappBridge/README.it.md` (`### I vocali hanno bisogno di ffmpeg`)
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (append to the media invariants)

**Interfaces:**
- Consumes: everything produced by Tasks 1-4.
- Produces: no code interface.

- [ ] **Step 1: Document the app side, in both languages**

In `README.md`, in the `#### Chats and new chats` section, immediately after the paragraph that ends with `the next step is choosing who to send it to.`, add:

```markdown
A voice note is recorded in the app: the microphone button starts
`Windows.Media.Capture.MediaCapture` (audio only, no camera) and writes AAC in
an M4A file in the app folder, which is what this phone records and plays
without a transcoder. The stop button ends the recording, and the file waits in
the same slot a picked photo uses: the preview bar shows it and Send streams it
out. On the adapter side a recorded voice note goes to `POST /send/audio`, which
is what makes WhatsApp draw a voice note with a waveform instead of an audio
file attachment.
```

In `README.it.md`, in the `#### Chat e nuove chat` section, immediately after the paragraph that ends with `il passo successivo e' scegliere a chi mandarla.` (search for the paragraph about sharing an image into the app; if the closing sentence differs, append after that paragraph), add:

```markdown
Un vocale si registra nell'app: il pulsante del microfono avvia
`Windows.Media.Capture.MediaCapture` (solo audio, niente fotocamera) e scrive
AAC in un file M4A nella cartella dell'app, che e' quello che questo telefono
registra e riproduce senza transcodifica. Il pulsante di stop chiude la
registrazione, e il file aspetta nello stesso posto di una foto scelta: la barra
di anteprima lo mostra e Invia lo spedisce. Sul lato adapter un vocale registrato
va su `POST /send/audio`, che e' cio' che fa disegnare a WhatsApp un vocale con
la forma d'onda invece di un allegato audio.
```

Do **not** add a heading: `tools/check-docs.js` requires the two READMEs to keep the same headings.

- [ ] **Step 2: Document the adapter route, in both languages**

In `WhatsappBridge/README.md`, at the end of the `### Voice notes need ffmpeg` section (after the paragraph ending with `that it cannot be played.`), add:

```markdown
The other direction needs no ffmpeg. A recorded voice note arrives from the app
as an M4A/AAC payload; `sendMediaToGowa` gives an `audio` payload to
`session.gowa.sendAudio`, which posts it to `POST /send/audio` (form field
`audio`). That route is what makes GOWA send a WhatsApp voice note rather than a
file with an audio MIME type. An adapter built against a GOWA without the route
falls back to `POST /send/file`.
```

In `WhatsappBridge/README.it.md`, at the end of `### I vocali hanno bisogno di ffmpeg`, add:

```markdown
L'altra direzione non ha bisogno di ffmpeg. Un vocale registrato arriva dall'app
come payload M4A/AAC; `sendMediaToGowa` passa un payload `audio` a
`session.gowa.sendAudio`, che lo pubblica su `POST /send/audio` (campo del form
`audio`). E' quella rotta a far spedire da GOWA un vocale WhatsApp invece di un
file con un MIME audio. Un adapter costruito su un GOWA senza la rotta ripiega
su `POST /send/file`.
```

- [ ] **Step 3: Record the invariant in the project skill**

In `.agents/skills/maintain-the-app/SKILL.md`, in the media invariants list, immediately after the bullet that begins `- **WP8.1 has no Opus decoder.**`, add:

```markdown
- **A recorded voice note is M4A, and it leaves through `/send/audio`.**
  `Services/AudioRecorder.cs` records AAC in an M4A container
  (`MediaEncodingProfile.CreateM4a`) because it is the format this phone records
  and decodes without help; WP8.1 has no Opus encoder, and the adapter converts
  only in the receiving direction. The M4A file goes into `LocalFolder` and then
  through the same `AttachmentInbox` slot as a picked photo, so
  `ChatPage.SendAttachmentAsync` streams it with the existing frames.
  `AttachmentInbox.KindName` must answer `audio` for an audio MIME type or
  extension: without that branch a voice note is a document, and the adapter
  cannot tell the two apart. On the adapter side an `audio` payload goes to
  `GowaClient.sendAudio` (`POST /send/audio`, field `audio`); `sendFile` is only
  the fallback, and a voice note sent through it arrives as a file.
- **The microphone is a `DeviceCapability`.** `MediaCapture.InitializeAsync`
  answers access denied without `<DeviceCapability Name="microphone" />` in
  `Package.appxmanifest`. Visual Studio rewrites that file on every build: after
  a build, `git checkout -- WhatsappApp/Package.appxmanifest`.
```

- [ ] **Step 4: Run the docs and gate checks**

Run:
```bash
node tools/check-docs.js && node tools/check-resw.js --strict
```
Expected: `OK: 2 doc pair(s) in step ...` and exit 0.

- [ ] **Step 5: Commit the documentation**

```bash
perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' README.md README.it.md WhatsappBridge/README.md WhatsappBridge/README.it.md .agents/skills/maintain-the-app/SKILL.md
git add README.md README.it.md WhatsappBridge/README.md WhatsappBridge/README.it.md .agents/skills/maintain-the-app/SKILL.md
git commit -m "Document recording a voice note, in both directions"
```

- [ ] **Step 6: Sync the Docker mirror**

```bash
cd /Users/vincenzo/Documents/docker-whatsappforwp
node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP
node tools/sync.js --check --from /Users/vincenzo/Documents/WhatsappForWP
```
Expected: `OK: server/ matches the adapter (30 file(s))` (28 before, plus the modified adapter files that the mirror tracks).

```bash
cd server && npm test
```
Expected: all tests pass, including the three new ones.

- [ ] **Step 7: Commit and push the mirror**

```bash
cd /Users/vincenzo/Documents/docker-whatsappforwp
git add -A
git commit -m "The adapter sends a voice note on send audio"
git push origin main
```

---

## Self-Review

**1. Spec coverage.** The request has two halves: record a voice note in the app, and send it.

- *Record*: Task 2 provides the microphone capture and the capability; Task 4 provides the buttons, the recording bar with the elapsed time, and the preview. The recording is stopped on leaving the page (`CancelRecording` in `OnNavigatedFrom`).
- *Send*: Task 3 makes the recorded file an `audio` attachment; Task 4 marks it `MessageType.Audio` and streams it through the existing `SendAttachmentAsync`; Task 1 makes the adapter send it as a real WhatsApp voice note via `POST /send/audio`.
- *Not covered on purpose*: playback of the sender's own voice note needed no new code — `IsAudio` is true for `MessageType.Audio` and the single `VoicePlayer` already plays `MediaFilePath`, which `SendAttachmentAsync` sets. Do not add a second player.
- *Not covered on purpose*: converting the recording to Ogg/Opus. WP8.1 has no Opus encoder; the adapter only transcodes inbound. `/send/audio` accepts `audio/mp4`, so M4A is a supported payload, not a workaround.

**2. Placeholder scan.** No "TBD", no "add error handling", no "similar to Task N". Every code step carries the full code; every command carries its expected output.

**3. Type consistency.** Verified across tasks:

- `AudioRecorder.StartAsync() -> Task<bool>`, `StopAsync() -> Task<string>` returning the constant `AudioRecorder.FileName` (`"voice_note.m4a"`), `CancelAsync() -> Task`. Task 4 calls exactly these three, and calls `ApplicationData.Current.LocalFolder.GetFileAsync(fileName)` with the returned name, which is the same constant the service wrote.
- `AttachmentInbox.KindName` returns `"audio"` (Task 3); Task 4 reads it into `bool audio` and branches on it; `SendAttachmentAsync` uses the same `kind` variable to pick `MessageType.Audio`.
- `ChatPage` XAML names used by the code-behind — `RecordButton`, `StopRecordButton`, `RecordingBar`, `RecordTimerText`, `SelectedAudioPreview` — are all declared in Task 4 Step 2-4. `RecordButton_Click`, `StopRecordButton_Click` and `RecordTimer_Tick` are the handlers wired by `Click=` / `Tick +=`.
- Adapter: `GowaClient.sendAudio(phone, caption, buffer, mimeType, fileName)` is defined in Task 1 Step 3 and called with that exact order in Task 1 Step 7.
- Resource keys used by code and XAML are the five added in Task 4 Step 1: `ChatPage_AudioSelected` (`Loc.Get`, Step 10), `ChatPage_RecordingLabel.Text` (`x:Uid`, Step 3), `ChatPage_RecordTooltip` and `ChatPage_StopRecordTooltip` (`Loc.Get`, Step 7), `ChatPage_RecordError` (`Loc.Get`, Step 9). All five are used, so `--strict` passes.

**4. Known gaps, left to the executor.** No phone is attached (`DEP6100`/`DEP6200`), so the recording itself, the permission prompt, and the M4A reaching WhatsApp are verified only by the ARM build and the adapter tests. When a phone is available: record a short note, confirm the preview bar says "Voice note selected", send it, and confirm the phone on the other end shows a voice note rather than a file.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-10-01-voice-note-recording.md`. Two execution options:

**1. Subagent-Driven (recommended)** — a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — execute the tasks in this session with checkpoints.

Which approach?

---

## What execution changed about this plan

Executed inline, 2026-10-01. The tasks above are left as written; these are the
divergences.

- **The manifest was CRLF with a BOM on disk.** Task 2 normalized it to LF and no
  BOM, which rewrites every line of `Package.appxmanifest` in that commit. That is
  the project rule, not a mistake, but it makes the commit look larger than it is.
- **`check-icons.js` counts 27 inline Paths, not the 26 the plan predicted.** The
  distinct-icon count (16) is the number the plan cared about, and it matched.
- **The Italian anchor in `WhatsappBridge/README.it.md` differs from the English
  one.** The planned `oldString` did not exist; the real paragraph ends
  `non si puo' riprodurre.`.
- **`prlctl exec` failed with `PrlJob_GetResult: Invalid argument`** on the first
  ARM build attempt, and even on `echo` right after. Waiting until the VM settled
  fixed it; the build then ran clean. Retry the command rather than treating it as
  a broken VM.
- **Pushing happened last, on explicit request.** The plan's Task 5 Step 7 pushed
  the mirror but said nothing about `git push origin master`; both remotes were
  pushed at the end of the session instead. The project skill now says pushing is
  the default and is not to be held back as a question.
- **The plan is executed inline, always.** The `writing-plans` Execution Handoff
  was answered implicitly (no subagent dispatcher exists here); the project skill
  now records that the handoff is not a stopping point.

### Follow-up: the tap did nothing on a real phone

On a real phone the microphone button did nothing at all and left no `DIAG`
line. Two causes, both invisible from the build: the failure was thrown at the
call site, before `AudioRecorder`'s own `try`, into an `async Task` that nobody
observed; and `_recording` only became true after a successful start, so the
natural second tap began a second `MediaCapture` and wedged the capture engine.
The follow-up plan is
`docs/superpowers/plans/2026-10-02-voice-recording-failure-is-visible.md`:
every recorder call is now awaited inside a `try`, the start is single-flight
through `_startingRecording`, and both `MediaCapture` calls run with a ten
second ceiling.
