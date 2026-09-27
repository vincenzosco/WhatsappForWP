# Chat Media And Share Crash Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the share crash, make the composer preview tell the truth about what was picked, give audio and document bubbles a word, make a received video actually play with a download indicator, drop WhatsApp channels from the chat list, and cache a chat's messages so opening it is instant.

**Architecture:** The app is a Windows Phone 8.1 WinRT/XAML client (`WhatsappApp/`, C# 5) talking over an encrypted TCP socket to a Node.js adapter (`WhatsappBridge/`) that fronts a GOWA server. Attachments travel as base64 in `media.*` control frames because a frame is capped at 8 MiB. This plan moves the phone's own attachments from "read the whole file into memory" to "copy the file and stream it out", adds a per-chat message cache on disk, and tightens the media bubbles and the adapter's chat list.

**Tech Stack:** C# 5 / XAML (Windows Phone 8.1 WinRT), Node.js (no dependencies in the adapter), Node's built-in test runner for the adapter and the repo guards.

## Global Constraints

- C# 5 syntax only. No `nameof`, `?.`, string interpolation, expression-bodied members, auto-property initializers, inline `out var`, pattern matching. `node tools/check-csharp5.js` enforces this.
- WP8.1 API surface only. `ShareOperation` has no `GetDeferral()`; `Windows.Foundation.Deferral` does not exist; `ContentDialog` has no `CloseButtonText`; `ExceptionRoutedEventArgs` has no `ErrorException` (only `ErrorMessage`). `node tools/check-csharp5.js` enforces this.
- No `Segoe MDL2 Assets`. Icons are inline `<Path>` geometries, each named by a `<!-- IconX -->` comment, and the same icon must be the same geometry everywhere. `node tools/check-icons.js` enforces this.
- Every user-visible string exists in BOTH `WhatsappApp/Strings/en-US/Resources.resw` and `WhatsappApp/Strings/it-IT/Resources.resw`, and `--strict` fails on an unused key. `node tools/check-resw.js --strict` enforces this.
- The frame ceiling is `8 * 1024 * 1024` on both sides, written as `MaxFrameLength` in the app and `MAX_FRAME_LENGTH` in the adapter. `node tools/check-framing.js` enforces this.
- Base64 pieces are cut on multiples of 4 characters so they concatenate without re-encoding. The app sends 700000 base64 characters per chunk (`ChatPage.MediaChunkChars`); the adapter sends `MEDIA_CHUNK_CHARS = 700000`. Keep those two numbers equal.
- README pairs stay in sync: `README.md` + `README.it.md`, and `WhatsappBridge/README.md` + `WhatsappBridge/README.it.md`. Same heading order, root `## Disclosure` last, no emoji in any `.md` file. `node tools/check-docs.js` enforces this.
- Commit messages must not contain apostrophes (the shell wrapper dies with `unexpected EOF`). Commit footer: a `Generated with Codebuff` line prefixed with the robot emoji, then `Co-Authored-By: Codebuff <noreply@codebuff.com>`. The commands below write the footer without the emoji, because this guard rejects any emoji in a markdown file; add it when the commit is actually made.
- The fast gate, run from the repo root, must stay green:
  `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"` and then `cd WhatsappBridge && npm test`.

## File Structure

- `WhatsappApp/Services/AttachmentInbox.cs` (modify) — holds the picked or shared attachment. Changes from a base64 blob in memory to the name of a file copied into `ApplicationData.Current.LocalFolder`.
- `WhatsappApp/Services/ImageHelper.cs` (modify) — gains `FromFileAsync`, so a bitmap can be decoded from an app-local file, not only from base64.
- `WhatsappApp/Models/ChatMessage.cs` (modify) — `LoadBitmapAsync` (base64 or file), `IsMediaLoading`, and `ShowsText` no longer hides audio.
- `WhatsappApp/Pages/ChatPage.xaml` (modify) — composer preview shows a play box for a video; the media bubbles show a download ring.
- `WhatsappApp/Pages/ChatPage.xaml.cs` (modify) — streams an attachment out of the local file; plays a video from `ms-appdata`; drives the loading ring.
- `WhatsappApp/Pages/ChatsPage.xaml.cs` (unchanged) — it only reads `AttachmentInbox.HasAttachment`.
- `WhatsappApp/App.xaml.cs` (modify) — deposits a picked file and a shared bitmap through the new inbox API, with the failure caught.
- `WhatsappApp/Services/MessageCache.cs` (create) — the last messages of a chat, on disk, so a chat paints before the server answers.
- `WhatsappApp/Services/DataService.cs` (modify) — loads the cached messages on open, saves them on leave, clears `IsMediaLoading`.
- `WhatsappApp/WhatsappApp.csproj` (modify) — registers `MessageCache.cs`.
- `WhatsappBridge/chats.js` (modify) — drops `@newsletter` channels from the chat list.
- `WhatsappBridge/message-format.js` (modify) — drops channel messages; audio and documents always carry a word.
- `WhatsappBridge/server.js` (modify) — the media error frame names the message it is about.
- `WhatsappBridge/test/chats.test.js`, `WhatsappBridge/test/message-format.test.js`, `WhatsappBridge/test/server.test.js` (modify) — cover the three adapter changes.
- `tools/check-memory.js`, `tools/test/check-memory.test.js` (modify) — catch a whole-file read, and cover `ImageHelper.FromFileAsync`.
- `.agents/skills/maintain-the-app/SKILL.md`, `.agents/skills/test-the-app/SKILL.md` (modify) — the gotchas and the on-device checks.
- `README.md` + `README.it.md`, `WhatsappBridge/README.md` + `WhatsappBridge/README.it.md` (modify) — describe the new behaviour.

---

### Task 1: A picked or shared file is copied, not read into memory

The crash when sharing a photo or a video is `AttachmentInbox.PutAsync` reading the whole file into a `byte[]` and then base64-encoding it: on a 512 MB phone a full-length video is hundreds of megabytes across those two allocations, and the process is killed. There is also a second hole: `App.OnActivated` calls `AttachmentInbox.PutAsync` without awaiting it and with no `try`, so any failure inside becomes an unobserved task exception.

**Files:**
- Modify: `WhatsappApp/Services/AttachmentInbox.cs`
- Modify: `WhatsappApp/Services/ImageHelper.cs`
- Modify: `WhatsappApp/Models/ChatMessage.cs`
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs`
- Modify: `WhatsappApp/App.xaml.cs`
- Modify: `tools/check-memory.js`
- Test: `tools/test/check-memory.test.js`

**Interfaces:**
- Consumes: `StorageFile.CopyAsync(IStorageFolder, string, NameCollisionOption)`, `StorageFile.GetBasicPropertiesAsync().Size` (ulong), `DataReader.LoadAsync`, `DataReader.ReadBytes`, `ApplicationData.Current.LocalFolder`.
- Produces:
  - `AttachmentInbox.LocalFileName` (string) — the name of the copied file in `LocalFolder`.
  - `AttachmentInbox.PutAsync(StorageFile file, string note)` returns `Task`.
  - `AttachmentInbox.PutBytesAsync(byte[] buffer, string fileName, string mimeType, string note)` returns `Task`.
  - `ImageHelper.FromFileAsync(string localFileName, int decodePixelWidth)` returns `Task<BitmapImage>`.
  - `ChatMessage.LoadBitmapAsync(int decodePixelWidth)` returns `Task<BitmapImage>`.

- [ ] **Step 1: Write the failing guard test**

Add to `tools/test/check-memory.test.js`:

```js
const INBOX = 'WhatsappApp/Services/AttachmentInbox.cs';

test('un allegato letto tutto in memoria si segnala', () => {
  const source = 'byte[] b = new byte[(uint)stream.Size];';
  const problems = memory.attachmentProblems(source, INBOX);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /CopyAsync/);
});

test('un allegato copiato passa', () => {
  const source = 'await file.CopyAsync(ApplicationData.Current.LocalFolder, name, ' +
    'NameCollisionOption.ReplaceExisting);';
  assert.deepStrictEqual(memory.attachmentProblems(source, INBOX), []);
});

test('ImageHelper.FromFileAsync chiede anche lui la misura', () => {
  const problems = memory.decodeProblems(
    'MediaImage = await ImageHelper.FromFileAsync(MediaFilePath);', FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /two arguments/);
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `node --test "tools/test/**/*.test.js"`
Expected: FAIL — `memory.attachmentProblems is not a function` and the `FromFileAsync` case passes zero problems because the pattern ignores `FromFile`.

- [ ] **Step 3: Teach the guard the new rule and the new helper**

In `tools/check-memory.js`, change the call pattern and add the rule. Replace:

```js
const CALL = /ImageHelper\.From(Base64|Bytes)Async\(/;
```

with:

```js
const CALL = /ImageHelper\.From(Base64|Bytes|File)Async\(/;
```

Then add, after `sourceShapeProblems`:

```js
const ATTACHMENT_INBOX = 'WhatsappApp/Services/AttachmentInbox.cs';

/**
 * Problemi di come entra un file scelto o condiviso.
 *
 * Perche' esiste: l'inbox leggeva il file intero in un byte[] e poi lo
 * convertiva in base64. Una foto piccola passa, un video no: due allocazioni da
 * decine di MB su un telefono da 512 MB fanno chiudere l'app, e la condivisione
 * sembra un crash. Il file si copia nella cartella dell'app e si legge a pezzi
 * quando si spedisce (vedi ChatPage.SendAttachmentAsync).
 */
function attachmentProblems(source, file) {
  const problems = [];
  if (file !== ATTACHMENT_INBOX) return problems;
  const code = stripComments(source);
  if (!/\.CopyAsync\s*\(/.test(code)) {
    problems.push(`${file}: a picked or shared file must be copied into the app folder ` +
      'with StorageFile.CopyAsync; reading it into a byte array holds the whole file ' +
      'in memory and takes the app down on a 512 MB phone');
  }
  if (/new\s+byte\s*\[\s*\(?(uint|int)\)?\s*\w+\.Size/.test(code)) {
    problems.push(`${file}: reads a whole file into a byte array instead of copying it`);
  }
  return problems;
}
```

In `main()`, add the new check next to the existing ones:

```js
    problems.push(...decodeProblems(source, rel));
    problems.push(...attachmentProblems(source, rel));
    if (rel === HELPER) problems.push(...sourceShapeProblems(source, rel));
```

And extend the exports:

```js
module.exports = {
  decodeProblems,
  attachmentProblems,
  sourceShapeProblems,
  splitArguments,
  MAX_DECODE
};
```

- [ ] **Step 4: Run the guard to watch it fail on the real file**

Run: `node tools/check-memory.js`
Expected: FAIL with `WhatsappApp/Services/AttachmentInbox.cs: a picked or shared file must be copied ...` and `... reads a whole file into a byte array ...`.

- [ ] **Step 5: Rewrite the inbox around a copied file**

Replace the body of `WhatsappApp/Services/AttachmentInbox.cs` (keep the class comment, adapt it in one sentence) with:

```csharp
using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Un file che entra nell'app da fuori: il selettore immagini o la
    /// condivisione di un'altra applicazione.
    ///
    /// Si COPIA nella cartella dell'app invece di leggerlo in memoria: una
    /// condivisione e' anche un video, e un video intero in un byte[] (piu' la
    /// sua base64) e' il modo piu' veloce per farsi chiudere l'app da un
    /// telefono da 512 MB. Il nome del file copiato e' quello che la pagina
    /// legge a pezzi quando spedisce.
    /// </summary>
    public static class AttachmentInbox
    {
        // Il nome del file copiato: uno solo in attesa alla volta.
        private const string CopyBaseName = "outgoing_attachment";

        private static string _localFileName;
        private static string _fileName;
        private static string _mimeType;
        private static string _note;

        /// <summary>Un allegato che nessuno ha ancora ritirato.</summary>
        public static event Action Ready;

        public static bool HasAttachment
        {
            get { return !string.IsNullOrEmpty(_localFileName); }
        }

        /// <summary>Il nome, dentro LocalFolder, del file copiato.</summary>
        public static string LocalFileName
        {
            get { return _localFileName; }
        }

        public static string FileName
        {
            get { return _fileName; }
        }

        public static string MimeType
        {
            get { return _mimeType; }
        }

        /// <summary>Testo che accompagnava la condivisione, se ce n'era uno.</summary>
        public static string Note
        {
            get { return _note; }
        }

        /// <summary>
        /// Deposita un file scelto o condiviso copiandolo nella cartella
        /// dell'app. Va copiato adesso, non quando servira': dopo una
        /// riattivazione il riferimento al file puo' non essere piu' valido, e
        /// l'app puo' essere stata terminata.
        /// </summary>
        public static async Task PutAsync(StorageFile file, string note)
        {
            if (file == null) return;

            string mimeType = MimeFor(file.FileType);
            string extension = ExtensionFor(mimeType, file.Name);
            StorageFile copy = await file.CopyAsync(
                ApplicationData.Current.LocalFolder,
                CopyBaseName + extension,
                NameCollisionOption.ReplaceExisting);

            PutLocal(copy.Name, file.Name, mimeType, note);
        }

        /// <summary>Deposita i byte gia' letti (una bitmap condivisa).</summary>
        public static async Task PutBytesAsync(byte[] buffer, string fileName, string mimeType, string note)
        {
            if (buffer == null || buffer.Length == 0) return;

            string extension = ExtensionFor(mimeType, fileName);
            StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                CopyBaseName + extension, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(file, buffer);

            PutLocal(file.Name, fileName, mimeType, note);
        }

        private static void PutLocal(string localName, string fileName, string mimeType, string note)
        {
            _localFileName = localName;
            _fileName = fileName;
            _mimeType = string.IsNullOrEmpty(mimeType) ? "image/jpeg" : mimeType;
            _note = note;

            var handler = Ready;
            if (handler != null) handler();
        }

        /// <summary>Ritira l'allegato: chi lo mostra lo fa una volta sola.</summary>
        public static void Clear()
        {
            _localFileName = null;
            _fileName = null;
            _mimeType = null;
            _note = null;
        }

        /// <summary>Il tipo MIME di un'estensione, come lo manda WhatsApp.</summary>
        private static string MimeFor(string extension)
        {
            string value = (extension ?? "").ToLower();
            if (value == ".png") return "image/png";
            if (value == ".gif") return "image/gif";
            if (value == ".bmp") return "image/bmp";
            if (value == ".mp4") return "video/mp4";
            if (value == ".mov") return "video/quicktime";
            if (value == ".3gp") return "video/3gpp";
            if (value == ".avi") return "video/x-msvideo";
            if (value == ".mkv") return "video/x-matroska";
            if (value == ".webm") return "video/webm";
            return "image/jpeg";
        }

        /// <summary>L'estensione del file copiato, dal nome o dal tipo MIME.</summary>
        private static string ExtensionFor(string mimeType, string fileName)
        {
            string name = fileName ?? "";
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) return name.Substring(dot).ToLower();

            string mime = (mimeType ?? "").ToLower();
            if (mime.StartsWith("video/")) return ".mp4";
            if (mime == "image/png") return ".png";
            if (mime == "image/gif") return ".gif";
            if (mime == "image/bmp") return ".bmp";
            return ".jpg";
        }

        /// <summary>
        /// "image" o "video": la parola che l'adapter e l'app usano per decidere
        /// come spedire e come disegnare. Il tipo MIME puo' mancare (una bitmap
        /// condivisa), quindi la parola si ricava anche dall'estensione.
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
            return "image";
        }
    }
}
```

Note `LastIndexOf('.')` on a `string` is the `char` overload, which exists on this toolchain.

- [ ] **Step 6: Add the file-based bitmap decode**

In `WhatsappApp/Services/ImageHelper.cs`, add `using Windows.Storage;` and this method after `FromBytesAsync`:

```csharp
        /// <summary>
        /// bitmap da un file gia' nella cartella dell'app, alla larghezza a cui
        /// viene mostrato. Serve agli allegati: dopo il selettore o una
        /// condivisione i byte stanno su disco, non in memoria.
        /// </summary>
        public static async Task<BitmapImage> FromFileAsync(string localFileName, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(localFileName)) return null;

            StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(localFileName);
            using (var stream = await file.OpenReadAsync())
            {
                var bitmap = new BitmapImage();
                if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
        }
```

`DecodePixelWidth` is set before `SetSourceAsync`, which is what `sourceShapeProblems` checks.

- [ ] **Step 7: Let a message decode a bitmap from either source**

In `WhatsappApp/Models/ChatMessage.cs`, replace `LoadMediaImageAsync` with a shared loader plus the old entry point:

```csharp
        /// <summary>
        /// Decodifica la bitmap di questo messaggio, da base64 o dal file locale,
        /// alla larghezza richiesta. La usano la bolla, lo schermo intero e
        /// l'anteprima di un allegato appena scelto.
        /// </summary>
        public async Task<BitmapImage> LoadBitmapAsync(int decodePixelWidth)
        {
            if (!string.IsNullOrEmpty(MediaData))
                return await ImageHelper.FromBase64Async(MediaData, decodePixelWidth);
            if (!string.IsNullOrEmpty(MediaFilePath))
                return await ImageHelper.FromFileAsync(MediaFilePath, decodePixelWidth);
            return null;
        }

        /// <summary>
        /// Decodifica MediaData (base64) o il file locale in MediaImage. Va
        /// atteso sul thread UI: il flusso deve restare aperto finché
        /// SetSourceAsync non ha finito.
        /// </summary>
        public async Task LoadMediaImageAsync()
        {
            if (Type != MessageType.Image) return;

            // Gia' decodificata (es. si torna sulla pagina): rifarlo sprecherebbe
            // CPU e memoria per un risultato identico.
            if (MediaImage != null) return;

            // Un'immagine dentro un fumetto e' la cosa piu' pesante che si possa
            // decodificare, e sotto pressione si rimanda a quando il telefono
            // respira: nel frattempo resta il segnaposto.
            if (MemoryWatcher.Instance.IsUnderPressure) return;
            if (string.IsNullOrEmpty(MediaData) && string.IsNullOrEmpty(MediaFilePath)) return;

            try
            {
                MediaImage = await LoadBitmapAsync(MediaDecodePixels);
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatMessage.LoadMediaImageAsync", ex);
                MediaImage = null;
            }
        }
```

- [ ] **Step 8: Stream an attachment out instead of holding it**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, replace the three `_selectedImage*` fields with the inbox's names, replace `SendAttachmentAsync`, `ShowPendingAttachment`, `ShowLocalPreviewAsync` and `ClearSelectedImage`, and add the streaming helpers. Replace the fields block:

```csharp
        // L'allegato scelto: il nome del file copiato nella cartella dell'app,
        // piu' cio' che serve per spedirlo. Non i byte: un video intero in
        // memoria e' la cosa piu' pesante che questa pagina potrebbe tenere.
        private string _selectedLocalFileName;
        private string _selectedMediaFileName;
        private string _selectedMediaMimeType;
```

Replace `SendAttachmentAsync` with:

```csharp
        /// <summary>
        /// Quanti byte si leggono per pezzo. E' un multiplo di 3: la sua base64
        /// e' quindi lunga esattamente (byte/3)*4 caratteri, senza padding, e i
        /// pezzi si concatenano in base64 senza ricodificare niente. 525000 byte
        /// fanno 700000 caratteri, come MediaChunkChars e come l'adapter.
        /// </summary>
        private const int MediaChunkBytes = 525000;

        private async System.Threading.Tasks.Task SendAttachmentAsync(string caption)
        {
            string localFileName = _selectedLocalFileName;
            if (string.IsNullOrEmpty(localFileName)) return;

            string fileName = _selectedMediaFileName;
            string mimeType = _selectedMediaMimeType ?? "image/jpeg";
            string kind = AttachmentInbox.KindName(mimeType, fileName);

            var message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = caption ?? "",
                SenderId = CommunicationService.Instance.MyUserId ?? "me",
                SenderName = CommunicationService.Instance.MyUsername ?? Loc.Get("ChatPage_Me", "Me"),
                ChatId = _contact.Id,
                Timestamp = DateTime.Now,
                Type = kind == "video" ? MessageType.Video : MessageType.Image,
                IsIncoming = false,
                Status = MessageStatus.Sending,
                // I byte stanno su disco: qui c'e' solo dove trovarli.
                MediaFilePath = localFileName,
                MediaMimeType = mimeType,
                MediaFileName = fileName,
                MediaType = kind
            };

            // Decodifica locale: il mittente vede la propria immagine.
            if (message.Type == MessageType.Image) await message.LoadMediaImageAsync();

            DataService.Instance.AddMessage(_contact.Id, message);
            MessageTextBox.Text = "";
            ScrollToMessage(message);
            ClearSelectedImage();

            if (!CommunicationService.Instance.IsConnected)
            {
                message.Status = MessageStatus.Failed;
                return;
            }

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(localFileName);
                ulong length = (await file.GetBasicPropertiesAsync()).Size;
                int total = length == 0 ? 0 : (int)((length + (ulong)MediaChunkBytes - 1) / (ulong)MediaChunkBytes);

                string transferId = Guid.NewGuid().ToString("N");
                await CommunicationService.Instance.SendMediaBeginAsync(
                    _contact.Id, transferId, fileName, mimeType, total);

                using (var stream = await file.OpenReadAsync())
                {
                    using (var reader = new DataReader(stream))
                    {
                        // ReadBytes legge byte crudi, quindi l'ordine dei byte
                        // non conta qui: si legge a pezzi e si codifica.
                        for (int i = 0; i < total; i++)
                        {
                            ulong offset = (ulong)i * (ulong)MediaChunkBytes;
                            int size = (int)Math.Min((ulong)MediaChunkBytes, length - offset);

                            while (reader.UnconsumedBufferLength < size)
                            {
                                uint loaded = await reader.LoadAsync((uint)(size - (int)reader.UnconsumedBufferLength));
                                if (loaded == 0) break;
                            }

                            byte[] buffer = new byte[size];
                            reader.ReadBytes(buffer);
                            await CommunicationService.Instance.SendMediaChunkAsync(
                                transferId, i, Convert.ToBase64String(buffer));
                        }
                    }
                }

                await CommunicationService.Instance.SendMediaEndAsync(transferId, caption);
                message.Status = MessageStatus.Sent;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.SendAttachmentAsync", ex);
                message.Status = MessageStatus.Failed;
            }
        }
```

Add `using Windows.Storage.Streams;` next to the other `using` lines. There is already `using Windows.Storage;`.

Also in `SendMessage()`, replace the guard `if (_selectedImageBase64 != null)` with:

```csharp
            if (_selectedLocalFileName != null)
```

Replace `ShowPendingAttachment`, `ShowLocalPreviewAsync` and `ClearSelectedImage` with:

```csharp
        private void ShowPendingAttachment()
        {
            if (!AttachmentInbox.HasAttachment) return;

            string note = AttachmentInbox.Note;
            _selectedLocalFileName = AttachmentInbox.LocalFileName;
            _selectedMediaFileName = AttachmentInbox.FileName;
            _selectedMediaMimeType = AttachmentInbox.MimeType;
            AttachmentInbox.Clear();

            if (!string.IsNullOrEmpty(note) && string.IsNullOrEmpty(MessageTextBox.Text))
            {
                MessageTextBox.Text = note;
            }

            ImagePreviewBar.Visibility = Visibility.Visible;

            bool video = AttachmentInbox.KindName(_selectedMediaMimeType, _selectedMediaFileName) == "video";
            if (video)
            {
                // Un video non si decodifica: non c'e' niente da disegnare.
                SelectedImagePreview.Source = null;
                return;
            }

#pragma warning disable 4014
            ShowLocalPreviewAsync(_selectedLocalFileName);
#pragma warning restore 4014
        }


        /// <summary>
        /// L'anteprima dell'immagine da spedire: la pagina e' larga 480 px, quindi
        /// 720 la copre anche a 1,5x senza decodificare il file intero.
        /// </summary>
        private const int PreviewDecodePixels = 720;

        /// <summary>Anteprima locale: il mittente vede la propria immagine.</summary>
        private async System.Threading.Tasks.Task ShowLocalPreviewAsync(string localFileName)
        {
            try
            {
                SelectedImagePreview.Source = await ImageHelper.FromFileAsync(
                    localFileName, PreviewDecodePixels);
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.ShowLocalPreviewAsync", ex);
            }
        }

        private void ClearSelectedImage()
        {
            _selectedLocalFileName = null;
            _selectedMediaFileName = null;
            _selectedMediaMimeType = null;
            SelectedImagePreview.Source = null;
            ImagePreviewBar.Visibility = Visibility.Collapsed;
        }
```

Replace the image-viewer call so a file-backed image also opens. Replace the body of `ShowFullScreenAsync` with:

```csharp
        private async System.Threading.Tasks.Task ShowFullScreenAsync(ChatMessage message)
        {
            if (message == null) return;

            try
            {
                var bitmap = await message.LoadBitmapAsync(ViewerDecodePixels);
                if (bitmap == null) return;
                ImageViewerImage.Source = bitmap;
                ImageViewer.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.ShowFullScreenAsync", ex);
                HideFullScreen();
            }
        }
```

- [ ] **Step 9: Deposit a share and a pick through the new API**

In `WhatsappApp/App.xaml.cs`, in `OnActivated`, replace the fire-and-forget call with a caught one:

```csharp
            var continuation = e as FileOpenPickerContinuationEventArgs;
            if (continuation == null || continuation.Files == null || continuation.Files.Count == 0)
            {
                return;
            }

#pragma warning disable 4014
            DepositPickedFileAsync(continuation.Files[0]);
#pragma warning restore 4014
```

and add, after `OnActivated`:

```csharp
        /// <summary>
        /// Un file scelto dal selettore. Un guasto qui non ha nessuno che lo
        /// raccolga - OnActivated non e' async e nessuno attende questo Task -
        /// quindi si cattura tutto: un'eccezione non osservata chiude l'app.
        /// </summary>
        private async void DepositPickedFileAsync(StorageFile file)
        {
            try
            {
                await AttachmentInbox.PutAsync(file, null);
            }
            catch (Exception ex)
            {
                Diag.Failed("App/picker", ex);
            }
        }
```

In `AcceptShareAsync`, replace the bitmap branch's `AttachmentInbox.PutBytes(...)` with the awaiting form:

```csharp
                    else if (data.Contains(StandardDataFormats.Bitmap))
                    {
                        var reference = await data.GetBitmapAsync();
                        using (var stream = await reference.OpenReadAsync())
                        {
                            using (var reader = new DataReader(stream))
                            {
                                uint size = (uint)stream.Size;
                                await reader.LoadAsync(size);
                                var buffer = new byte[size];
                                reader.ReadBytes(buffer);
                                await AttachmentInbox.PutBytesAsync(buffer, "shared.png", "image/png", null);
                            }
                        }
                    }
```

- [ ] **Step 10: Run the guard and the tool tests**

Run: `node tools/check-memory.js && node --test "tools/test/**/*.test.js"`
Expected: PASS — `OK: every decoded bitmap asks for the width it is shown at.` and the new tests green.

- [ ] **Step 11: Build on the VM to prove the WP8.1 API surface**

Run:
```bash
prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"
prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"
prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m"
```
Expected: `COPIA=0`, then `Your package has been successfully created.` with no `error CS`.

- [ ] **Step 12: Commit**

```bash
git add WhatsappApp/Services/AttachmentInbox.cs WhatsappApp/Services/ImageHelper.cs \
  WhatsappApp/Models/ChatMessage.cs WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/App.xaml.cs \
  tools/check-memory.js tools/test/check-memory.test.js
git commit -m "$(cat <<'EOF'
fix: copy a shared file instead of reading it into memory

Sharing a video read the whole file into a byte array and then base64-encoded
it, which is what took the app down on a 512 MB phone. The file is now copied
into the app folder and streamed out in chunks, and the picker continuation
catches its own failure instead of leaving an unobserved task.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
```

---

### Task 2: The composer preview says what was picked

The preview bar has one hardcoded label, `Image selected`, and always tries to decode the picked bytes as a bitmap. For a video that decode fails and the bar shows a broken picture under a wrong word.

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml`
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (only the XAML names, already wired in Task 1)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`
- Modify: `WhatsappApp/Strings/it-IT/Resources.resw`

**Interfaces:**
- Consumes: `SelectedVideoPreview`, `SelectedImagePreview`, `PreviewLabel` (the XAML names this task creates), `Loc.Get`.
- Produces: `ChatPage_VideoSelected` resource key.

- [ ] **Step 1: Add the failing resource first**

Add to BOTH `WhatsappApp/Strings/en-US/Resources.resw` and `it-IT/Resources.resw`, right after the `ChatPage_ImageSelected.Text` entry (copy the surrounding indentation exactly):

en-US:
```xml
  <data name="ChatPage_VideoSelected" xml:space="preserve">
    <value>Video selected</value>
  </data>
```
it-IT:
```xml
  <data name="ChatPage_VideoSelected" xml:space="preserve">
    <value>Video selezionato</value>
  </data>
```

- [ ] **Step 2: Run the guard to verify the key is not yet used**

Run: `node tools/check-resw.js --strict`
Expected: FAIL with `Strings/en-US/Resources.resw: "ChatPage_VideoSelected" is never used`.

- [ ] **Step 3: Name the preview parts and add a play box**

In `WhatsappApp/Pages/ChatPage.xaml`, replace the `<Image x:Name="SelectedImagePreview" .../>` and the label `<TextBlock x:Uid="ChatPage_ImageSelected" .../>` inside `ImagePreviewBar` with:

```xml
                <Image x:Name="SelectedImagePreview" Grid.Column="0"
                       Width="48" Height="48" Margin="8,8,0,8"
                       Stretch="UniformToFill"/>
                <!-- Un video non si decodifica: la casella dice cosa si sta
                     spedendo senza disegnare niente. -->
                <Path x:Name="SelectedVideoPreview" Grid.Column="0"
                      Width="30" Height="30" Margin="17,17,0,17"
                      Fill="#FF606060"
                      Visibility="Collapsed">
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
                <TextBlock x:Uid="ChatPage_ImageSelected" x:Name="PreviewLabel" Grid.Column="1"
                           Text="Image selected"
                           VerticalAlignment="Center"
                           Foreground="#FF606060" FontSize="13"
                           Margin="8,0,0,0"/>
```

`IconPlay` must be the same geometry as the play box added earlier in this page; `check-icons` compares them.

The image label is looked up as `ChatPage_ImageSelected.Text`, not `ChatPage_ImageSelected`: that is the name the x:Uid form creates, and a bare key of the same base would collide with it (the guard rejects both existing at once).

Then, in `WhatsappApp/Pages/ChatPage.xaml.cs`'s `ShowPendingAttachment`, replace the video branch that only clears the image with the label and the play box this task just created. Replace:

```csharp
            ImagePreviewBar.Visibility = Visibility.Visible;

            bool video = AttachmentInbox.KindName(_selectedMediaMimeType, _selectedMediaFileName) == "video";
            if (video)
            {
                // Un video non si decodifica: non c'e' niente da disegnare.
                SelectedImagePreview.Source = null;
                return;
            }

#pragma warning disable 4014
            ShowLocalPreviewAsync(_selectedLocalFileName);
#pragma warning restore 4014
        }
```

with:

```csharp
            bool video = AttachmentInbox.KindName(_selectedMediaMimeType, _selectedMediaFileName) == "video";
            PreviewLabel.Text = video
                ? Loc.Get("ChatPage_VideoSelected", "Video selected")
                : Loc.Get("ChatPage_ImageSelected.Text", "Image selected");
            SelectedVideoPreview.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            SelectedImagePreview.Visibility = video ? Visibility.Collapsed : Visibility.Visible;

            ImagePreviewBar.Visibility = Visibility.Visible;
            if (!video)
            {
#pragma warning disable 4014
                ShowLocalPreviewAsync(_selectedLocalFileName);
#pragma warning restore 4014
            }
            else
            {
                SelectedImagePreview.Source = null;
            }
        }
```

- [ ] **Step 4: Run the guards and the build**

Run: `node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-actions.js`
Expected: PASS — a higher inline Path count, one more distinct icon than before only if `IconPlay` is new, and `106`-plus key count with no unused key.

Then build on the VM with the three commands from Task 1 Step 11.
Expected: `Your package has been successfully created.`

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Strings/en-US/Resources.resw \
  WhatsappApp/Strings/it-IT/Resources.resw
git commit -m "$(cat <<'EOF'
fix: the composer preview says video instead of image

A picked video was labelled Image selected and the preview tried to decode it
as a bitmap, so the bar showed a broken picture under a wrong word.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
```

---

### Task 3: WhatsApp channels are not conversations

GOWA's `/chats` returns channel JIDs (`...@newsletter`) and the adapter forwards their messages. Neither belongs in the chat list or in a toast.

**Files:**
- Modify: `WhatsappBridge/chats.js`
- Modify: `WhatsappBridge/message-format.js`
- Test: `WhatsappBridge/test/chats.test.js`
- Test: `WhatsappBridge/test/message-format.test.js`

**Interfaces:**
- Consumes: `collectChats({ gowa, limit, avatars, groupNames, log })`.
- Produces: `chats.js` additionally exports nothing new; the filter is internal. `mapWebhookMessage` returns `null` for a `@newsletter` chat.

- [ ] **Step 1: Write the failing tests**

Add to `WhatsappBridge/test/chats.test.js`:

```js
test('un canale non compare fra le conversazioni', async () => {
  const gowa = {
    chats: async () => [
      { jid: 'a@s.whatsapp.net', name: 'Mario' },
      { jid: '123456@newsletter', name: 'Notizie' }
    ],
    chatMessages: async () => []
  };
  const rows = await collectChats({ gowa, limit: 25, log: () => {} });
  assert.deepStrictEqual(rows.map((r) => r.chatId), ['a@s.whatsapp.net']);
});
```

Add to `WhatsappBridge/test/message-format.test.js`:

```js
test('un messaggio di un canale non viene inoltrato', () => {
  assert.strictEqual(
    mapWebhookMessage({ chat_id: '123456@newsletter', from: '123456@newsletter', body: 'x' }),
    null);
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd WhatsappBridge && npm test`
Expected: FAIL — the channel row is present, and the channel message is mapped instead of `null`.

- [ ] **Step 3: Filter channels in both places**

In `WhatsappBridge/chats.js`, after `isGroupJid`, add:

```js
// Un canale non e' una conversazione: non si puo' rispondere, e nell'elenco
// chat occupa il posto di una persona. GOWA li elenca, quindi si saltano qui.
function isChannelJid(jid) {
  return typeof jid === 'string' && jid.endsWith('@newsletter');
}
```

and in the loop in `collectChats`, right after the `if (!chat || !chat.jid) continue;` line, add:

```js
    if (isChannelJid(chat.jid)) continue;
```

Export it (append to the existing `module.exports`):

```js
module.exports = { previewForMessage, collectChats, isChannelJid };
```

In `WhatsappBridge/message-format.js`, in `mapWebhookMessage`, replace:

```js
  if (!chatId || chatId === 'status@broadcast') return null;
```

with:

```js
  // Un canale non e' una conversazione: i suoi messaggi non si mostrano e non
  // alzano un non letto (vedi chats.js, isChannelJid).
  if (!chatId || chatId === 'status@broadcast' || chatId.endsWith('@newsletter')) return null;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, with the two new tests green and the existing 111 still green.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/chats.js WhatsappBridge/message-format.js \
  WhatsappBridge/test/chats.test.js WhatsappBridge/test/message-format.test.js
git commit -m "$(cat <<'EOF'
fix: keep WhatsApp channels out of the chat list

GOWA lists channel JIDs and forwards their messages, so a channel took a row
where a person belongs and raised unread counts for something nobody can reply
to.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
```

---

### Task 4: An audio or a document bubble says what it is

When the webhook has already downloaded the media, `mediaFromPayload` leaves `fallbackText` empty, so text is empty. For a document the type stays `Text` and the bubble is blank; for audio the app's `ShowsText` hides the text on purpose, so it is blank either way. Nothing shows.

**Files:**
- Modify: `WhatsappBridge/message-format.js`
- Modify: `WhatsappApp/Models/ChatMessage.cs`
- Test: `WhatsappBridge/test/message-format.test.js`

**Interfaces:**
- Consumes: `mediaFromPayload`, `mapWebhookMessage`, `ChatMessage.ShowsText`.
- Produces: `mediaFromPayload` always sets `fallbackText` for `audio` and `document`.

- [ ] **Step 1: Write the failing tests**

Add to `WhatsappBridge/test/message-format.test.js`:

```js
test('un audio scaricato resta una parola', () => {
  const f = mapWebhookMessage({ id: 'A1', chat_id: 'a@s.whatsapp.net', audio: 'statics/media/a.ogg' });
  assert.strictEqual(f.mediaType, 'audio');
  assert.strictEqual(f.text, '[Audio]');
});

test('un documento scaricato mostra il suo nome', () => {
  const f = mapWebhookMessage({
    id: 'D1', chat_id: 'a@s.whatsapp.net',
    document: { path: 'statics/media/d.pdf', filename: 'contratto.pdf' }
  });
  assert.strictEqual(f.mediaType, 'document');
  assert.strictEqual(f.text, 'contratto.pdf');
});

test('un documento senza nome resta una parola', () => {
  const f = mapWebhookMessage({
    id: 'D2', chat_id: 'a@s.whatsapp.net', document: { path: 'statics/media/d.bin' }
  });
  assert.strictEqual(f.text, '[Document]');
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd WhatsappBridge && npm test`
Expected: FAIL — `f.text` is `''` for the downloaded audio and document.

- [ ] **Step 3: Always give the media a word**

In `WhatsappBridge/message-format.js`, replace the `audio` and `document` branches of `mediaFromPayload` with:

```js
  } else if (p.audio !== undefined) {
    // La parola c'e' sempre: con i byte o senza, un fumetto vuoto non dice
    // niente, e un audio non si disegna.
    result.type = 2;
    result.fallbackText = '[Audio]';
    if (typeof p.audio === 'string') {
      result.path = p.audio; result.mimeType = 'audio/ogg'; result.fileName = 'audio.ogg';
    } else if (p.audio && typeof p.audio.path === 'string') {
      result.path = p.audio.path; result.mimeType = 'audio/ogg'; result.fileName = 'audio.ogg';
    }
  } else if (p.video !== undefined) {
```

and

```js
  } else if (p.document !== undefined) {
    result.fallbackText = '[Document]';
    if (p.document && typeof p.document.path === 'string') {
      result.path = p.document.path;
      result.fileName = p.document.filename || null;
      // Il nome del file e' piu' utile di una parola: e' quello che l'utente
      // ha mandato.
      if (result.fileName) result.fallbackText = result.fileName;
    } else {
      result.fallbackText = '[Document not downloaded]';
    }
  } else if (typeof p.sticker === 'string') {
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd WhatsappBridge && npm test`
Expected: PASS.

- [ ] **Step 5: Stop the app hiding an audio label**

In `WhatsappApp/Models/ChatMessage.cs`, in `ShowsText`, replace:

```csharp
            get
            {
                if (Type == MessageType.Audio) return false;
                if (string.IsNullOrEmpty(Text)) return false;
                if (IsVideo && IsMediaPlaceholder) return false;
                return true;
            }
```

with:

```csharp
            get
            {
                if (string.IsNullOrEmpty(Text)) return false;
                if (IsVideo && IsMediaPlaceholder) return false;
                return true;
            }
```

- [ ] **Step 6: Run the app guards and build**

Run: `node tools/check-csharp5.js && node tools/check-memory.js`
Expected: PASS.

Then build on the VM with the three commands from Task 1 Step 11.
Expected: `Your package has been successfully created.`

- [ ] **Step 7: Commit**

```bash
git add WhatsappBridge/message-format.js WhatsappBridge/test/message-format.test.js \
  WhatsappApp/Models/ChatMessage.cs
git commit -m "$(cat <<'EOF'
fix: an audio or a document bubble says what it is

A downloaded audio or document arrived with an empty body and the app hid the
audio label on purpose, so the bubble was blank. The adapter now always names
the media and a document shows its file name.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
```

---

### Task 5: A received video plays, and the bubble shows it is downloading

The viewer sets the source from an `IRandomAccessStream` that it then owns for the life of the page, and `MediaFailed` closes the overlay with no trace - so a failure looks exactly like a tap that did nothing. Play from the file URI instead, keep the overlay up when it fails and say so, and show a ring while the pieces arrive.

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml`
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs`
- Modify: `WhatsappApp/Models/ChatMessage.cs`
- Modify: `WhatsappApp/Services/DataService.cs`
- Modify: `WhatsappBridge/server.js`
- Test: `WhatsappBridge/test/server.test.js`
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`
- Modify: `WhatsappApp/Strings/it-IT/Resources.resw`

**Interfaces:**
- Consumes: `ChatMessage.MediaFilePath`, `IncomingMediaResult`, `ApplicationData.Current.LocalFolder`.
- Produces:
  - `ChatMessage.IsMediaLoading` (bool, client-only, notifies).
  - The adapter's `media` error control frame carries `relatedMessageId`.

- [ ] **Step 1: Write the failing adapter test**

Add to `WhatsappBridge/test/server.test.js`, next to the existing `media.get` failure test:

```js
test('un media che non c e piu dice a quale messaggio si riferisce', async () => {
  const sent = [];
  const gowa = { downloadMedia: async () => null };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({
    Type: 3, Command: 'media.get', Text: 'a@s.whatsapp.net', RelatedMessageId: 'M9'
  });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'error');
  assert.strictEqual(sent[0].RelatedMessageId, 'M9');
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd WhatsappBridge && npm test`
Expected: FAIL — `sent[0].RelatedMessageId` is `undefined`.

- [ ] **Step 3: Name the message in the failure**

In `WhatsappBridge/server.js`, inside `sendMedia`, replace the two failure `sendControl({ command: 'error', ... })` calls with:

```js
      sendControl({
        command: 'error',
        chatId,
        relatedMessageId: messageId,
        text: 'This media is no longer available on the server.'
      });
```

and

```js
      sendControl({
        command: 'error',
        chatId,
        relatedMessageId: messageId,
        text: `Media download failed: ${err.message}`
      });
```

`buildChatMessage` already emits `RelatedMessageId` when it is set.

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 113 tests or more.

- [ ] **Step 5: Add the loading flag and the error string**

In `WhatsappApp/Models/ChatMessage.cs`, add next to `MediaFilePath`:

```csharp
        private bool _isMediaLoading;

        /// <summary>
        /// Vero mentre i byte di questo media stanno arrivando. Non e' un dato
        /// del filo: lo alza la pagina quando chiede il media e lo abbassa il
        /// servizio dati quando i pezzi sono tutti, o quando il server dice che
        /// non c'e' piu'. E' quello che fa girare l'indicatore nella bolla.
        /// </summary>
        public bool IsMediaLoading
        {
            get { return _isMediaLoading; }
            set { _isMediaLoading = value; OnPropertyChanged(); }
        }
```

Add to BOTH resource files, after `ChatPage_VideoSelected`:

en-US:
```xml
  <data name="ChatPage_VideoError" xml:space="preserve">
    <value>This video cannot be played.</value>
  </data>
```
it-IT:
```xml
  <data name="ChatPage_VideoError" xml:space="preserve">
    <value>Questo video non puo' essere riprodotto.</value>
  </data>
```

- [ ] **Step 6: Play from the file, and keep the failure visible**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, delete the `_videoStream` field, `PlayVideoAsync`, `VideoPlayer_MediaFailed` and `StopVideo`, and add:

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

        private void VideoCloseButton_Click(object sender, RoutedEventArgs e)
        {
            StopVideo();
        }

        private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // Un video che il telefono non sa decodificare. Prima si chiudeva lo
            // schermo e basta, quindi un guasto e un tocco a vuoto si vedevano
            // uguali; adesso resta la frase. In WP8.1 l'evento porta solo il
            // messaggio, non l'eccezione.
            string reason = (e != null && !string.IsNullOrEmpty(e.ErrorMessage))
                ? e.ErrorMessage
                : "media failed";
            Diag.Failed("ChatPage/VideoPlayer", new InvalidOperationException(reason));
            ShowVideoError();
        }

        private void ShowVideoError()
        {
            try
            {
                VideoPlayer.Stop();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.ShowVideoError", ex);
            }
            VideoErrorText.Text = Loc.Get("ChatPage_VideoError", "This video cannot be played.");
            VideoErrorText.Visibility = Visibility.Visible;
        }

        /// <summary>Chiude il lettore. Sicura da chiamare anche a vuoto.</summary>
        private void StopVideo()
        {
            try
            {
                VideoPlayer.Stop();
                VideoPlayer.Source = null;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.StopVideo", ex);
            }

            VideoErrorText.Visibility = Visibility.Collapsed;
            VideoViewer.Visibility = Visibility.Collapsed;
        }
```

In `Media_Tapped`, replace `await PlayVideoAsync(message);` with `PlayVideo(message);`. The method is still `async void` for the image branch, which is fine.

Remove the `using Windows.Storage.Streams;` line only if nothing else in the file needs it; Task 1 added it for `SendAttachmentAsync` (`DataReader`, `ByteOrder`), so keep it.

- [ ] **Step 7: Show the loading ring while the pieces arrive**

In `WhatsappApp/Pages/ChatPage.xaml`, inside the video `<Border ... Tapped="Media_Tapped">` in BOTH bubbles, the `Border` now holds two things, and a `Border` takes exactly one child (`WMC0035` otherwise): wrap the play `Path` and the new `ProgressRing` in a `<Grid>`. The result is:

```xml
                                <Border CornerRadius="4"
                                        Margin="0,0,0,4"
                                        Width="220" Height="140"
                                        Background="#FF263238"
                                        Visibility="{Binding IsVideo, Converter={StaticResource BoolToVisibility}}"
                                        Tapped="Media_Tapped">
                                    <Grid>
                                        <Path Fill="White" Width="54" Height="54"
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
                                        <ProgressRing IsActive="True" Width="26" Height="26"
                                                      Foreground="White"
                                                      HorizontalAlignment="Center" VerticalAlignment="Center"
                                                      Visibility="{Binding IsMediaLoading, Converter={StaticResource BoolToVisibility}}"/>
                                    </Grid>
                                </Border>
```

Still in `ChatPage.xaml`, inside the `<Grid x:Name="VideoViewer" ...>`, after the `<Button x:Name="VideoCloseButton" ...>...</Button>`, add:

```xml
            <TextBlock x:Name="VideoErrorText"
                       Text=""
                       Foreground="White"
                       FontSize="15"
                       TextWrapping="Wrap"
                       HorizontalAlignment="Center" VerticalAlignment="Center"
                       Margin="24,0,24,0"
                       Visibility="Collapsed"/>
```

- [ ] **Step 8: Raise the flag when a media is asked for, clear it when it lands**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, replace `RequestMedia` with:

```csharp
        private void RequestMedia(ChatMessage message)
        {
            if (!CommunicationService.Instance.IsConnected) return;
            // Il cerchio parte adesso: il primo pezzo puo' metterci, e senza
            // questo il tocco sembra non aver fatto niente.
            message.IsMediaLoading = true;
#pragma warning disable 4014
            CommunicationService.Instance.RequestMediaAsync(message.ChatId, message.Id);
#pragma warning restore 4014
        }
```

In `WhatsappApp/Services/DataService.cs`, in `OnControlMessageReceived`, replace:

```csharp
                case "media":
                    ApplyMediaFrame(message);
                    break;
```

with:

```csharp
                case "media":
                    ApplyMediaFrame(message);
                    break;
                case "error":
                    ClearMediaLoading(message);
                    break;
```

and add, after `ApplyMedia`:

```csharp
        /// <summary>
        /// Il server dice che quel media non c'e' piu': l'indicatore smette di
        /// girare, altrimenti la bolla resta in attesa per sempre.
        /// </summary>
        private void ClearMediaLoading(ChatMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.RelatedMessageId)) return;

            var list = GetMessages(message.ChatId);
            for (int i = 0; i < list.Count; i++)
            {
                var target = list[i];
                if (target != null && target.Id == message.RelatedMessageId)
                {
                    target.IsMediaLoading = false;
                    return;
                }
            }
        }
```

and in `ApplyMedia`, at the start of the body (before the loop), clear the flag on the target that matches. Add inside the loop, immediately after `if (target == null || target.Id != message.RelatedMessageId) continue;`:

```csharp
                target.IsMediaLoading = false;
```

Also in `ApplyMediaFrame`, the request may target a message that is not in the list (the page was left): leave `IsMediaLoading` alone, it dies with the message.

- [ ] **Step 9: Run the guards, the adapter tests and the build**

Run:
```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && \
node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"
cd WhatsappBridge && npm test
```
Expected: all PASS.

Then build on the VM with the three commands from Task 1 Step 11.
Expected: `Your package has been successfully created.`

- [ ] **Step 10: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Pages/ChatPage.xaml.cs \
  WhatsappApp/Models/ChatMessage.cs WhatsappApp/Services/DataService.cs \
  WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw \
  WhatsappBridge/server.js WhatsappBridge/test/server.test.js
git commit -m "$(cat <<'EOF'
fix: play a received video from the file and show the download

The viewer owned a stream for the life of the page and a decode failure closed
it silently, so a broken video and a dead tap looked the same. It now plays the
ms-appdata file, keeps the overlay up on failure with a sentence, and a ring
turns in the bubble while the pieces arrive.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
```

---

### Task 6: A chat paints from disk before the server answers

Every time a chat is opened the message list starts empty and waits for the `messages` history frame. On a phone that is seconds of blank page per conversation, and it is the main reason the app feels slow. Keep the last messages of each chat on disk and show them at once.

**Files:**
- Create: `WhatsappApp/Services/MessageCache.cs`
- Modify: `WhatsappApp/Services/DataService.cs`
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj`

**Interfaces:**
- Consumes: `ChatMessage.ToJson`, `ChatMessage.FromJson`, `ApplicationData.Current.LocalFolder`, `ObservableCollection<ChatMessage>`.
- Produces:
  - `MessageCache.LoadAsync(string chatId)` returns `Task<List<ChatMessage>>`.
  - `MessageCache.SaveAsync(string chatId, IEnumerable<ChatMessage> messages)` returns `Task`.

- [ ] **Step 1: Create the cache**

Create `WhatsappApp/Services/MessageCache.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>Il file su disco: un solo campo, i messaggi di una chat.</summary>
    [DataContract]
    internal class MessageCacheFile
    {
        [DataMember]
        public List<ChatMessage> Messages { get; set; }
    }

    /// <summary>
    /// Gli ultimi messaggi di una conversazione, tenuti sul telefono.
    ///
    /// Perche' esiste: aprendo una chat l'elenco partiva vuoto e aspettava la
    /// cronologia dal server, che e' l'unica cosa che il telefono non puo'
    /// affrettare. Con questa copia la conversazione si vede subito, e i
    /// messaggi veri la sostituiscono quando arrivano.
    ///
    /// E' una fotografia, non una verita': si tiene solo l'ultima parte della
    /// conversazione, e si riscrive uscendo dalla chat.
    /// </summary>
    public static class MessageCache
    {
        /// <summary>Quanti messaggi si tengono per chat: abbastanza per riempire
        /// lo schermo, non abbastanza per pesare.</summary>
        private const int Keep = 60;

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(MessageCacheFile));

        /// <summary>I messaggi salvati. Mai un'eccezione: la prima volta non c'e' file.</summary>
        public static async Task<List<ChatMessage>> LoadAsync(string chatId)
        {
            var empty = new List<ChatMessage>();
            if (string.IsNullOrEmpty(chatId)) return empty;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileNameFor(chatId));
                string json = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrEmpty(json)) return empty;

                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var cache = Serializer.ReadObject(stream) as MessageCacheFile;
                    if (cache != null && cache.Messages != null) return cache.Messages;
                }
            }
            catch (Exception ex)
            {
                // Prima apertura, o cache scritta da una versione diversa.
                Diag.Failed("MessageCache.Load", ex);
            }
            return empty;
        }

        /// <summary>Scrive la coda della conversazione. Mai un'eccezione.</summary>
        public static async Task SaveAsync(string chatId, IList<ChatMessage> messages)
        {
            if (string.IsNullOrEmpty(chatId) || messages == null || messages.Count == 0) return;

            try
            {
                var slim = new List<ChatMessage>();
                int from = messages.Count > Keep ? messages.Count - Keep : 0;
                for (int i = from; i < messages.Count; i++) slim.Add(Slim(messages[i]));

                string json;
                using (var stream = new MemoryStream())
                {
                    Serializer.WriteObject(stream, new MessageCacheFile { Messages = slim });
                    json = Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                }

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileNameFor(chatId), CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                // Una cache che non si scrive non e' un guasto da mostrare:
                // la cronologia arriva comunque dal server.
                Diag.Failed("MessageCache.Save", ex);
            }
        }

        /// <summary>La copia di un messaggio senza i byte del media: quelli
        /// riempirebbero il file, e per un video non ci starebbero nemmeno.</summary>
        private static ChatMessage Slim(ChatMessage message)
        {
            return new ChatMessage
            {
                Id = message.Id,
                Text = message.Text,
                SenderId = message.SenderId,
                SenderName = message.SenderName,
                ChatId = message.ChatId,
                Timestamp = message.Timestamp,
                Status = message.Status,
                Type = message.Type,
                IsIncoming = message.IsIncoming,
                MediaMimeType = message.MediaMimeType,
                MediaFileName = message.MediaFileName,
                MediaType = message.MediaType,
                MediaFilePath = message.MediaFilePath,
                IsHistory = true
            };
        }

        /// <summary>Un nome di file per chat: l'id ripulito dai caratteri che un
        /// nome di file non accetta.</summary>
        private static string FileNameFor(string chatId)
        {
            var builder = new StringBuilder("messages_");
            for (int i = 0; i < chatId.Length; i++)
            {
                char c = chatId[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '-' || c == '_')
                {
                    builder.Append(c);
                }
            }
            builder.Append(".json");
            return builder.ToString();
        }
    }
}
```

Register it in `WhatsappApp/WhatsappApp.csproj` right after the `IncomingMediaStore.cs` line:

```xml
    <Compile Include="Services\MessageCache.cs" />
```

- [ ] **Step 2: Give the page the cached messages**

In `WhatsappApp/Services/DataService.cs`, add after `GetMessages`:

```csharp
        /// <summary>
        /// Mette i messaggi salvati in cima alla conversazione, se la chat e'
        /// ancora vuota. Sono marcati IsHistory, quindi non contano e non
        /// alzano avvisi: e' esattamente quello che sono.
        /// </summary>
        public async Task LoadCachedMessagesAsync(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return;

            var list = GetMessages(chatId);
            if (list.Count > 0) return;

            var cached = await MessageCache.LoadAsync(chatId);
            for (int i = 0; i < cached.Count; i++)
            {
                var message = cached[i];
                message.ChatId = chatId;
                _chatMessages[chatId].Add(message);
            }
        }
```

In `WhatsappApp/Pages/ChatPage.xaml.cs`, in `OnNavigatedTo`, replace:

```csharp
                // Load messages
                _messages = DataService.Instance.GetMessages(contact.Id);
                MarkRead();
                MessagesListView.ItemsSource = _messages;
```

with:

```csharp
                // Load messages: prima quelli sul telefono, cosi' la
                // conversazione si vede subito, poi la cronologia vera.
                _messages = DataService.Instance.GetMessages(contact.Id);
                MarkRead();
                MessagesListView.ItemsSource = _messages;
#pragma warning disable 4014
                LoadCachedMessagesAsync(contact.Id);
#pragma warning restore 4014
```

and add, next to `RequestMedia`:

```csharp
        /// <summary>
        /// Riempie la conversazione con la copia sul telefono e scorre in fondo.
        /// Va attesa sul thread UI: la collezione e' quella legata alla lista.
        /// </summary>
        private async System.Threading.Tasks.Task LoadCachedMessagesAsync(string chatId)
        {
            await DataService.Instance.LoadCachedMessagesAsync(chatId);
            if (_messages.Count > 0) ScrollToMessage(_messages[_messages.Count - 1]);
        }
```

In `OnNavigatedFrom`, before `DataService.Instance.ActiveChatId = null;`, add:

```csharp
            // La fotografia della conversazione: e' l'uscita che la scrive,
            // non ogni messaggio, altrimenti scriverebbe un file a raffica.
            if (_contact != null && _messages != null)
            {
#pragma warning disable 4014
                MessageCache.SaveAsync(_contact.Id, _messages);
#pragma warning restore 4014
            }
```

- [ ] **Step 3: Make sure the cache cannot erase the live list**

`MessageCache.LoadAsync` is only called when `list.Count == 0` (inside `DataService.LoadCachedMessagesAsync`), so a history frame that already arrived is never overwritten. Confirm this by reading the method back before moving on.

- [ ] **Step 4: Run the guards and build**

Run: `node tools/check-csharp5.js && node tools/check-resw.js --strict && node tools/check-memory.js && node tools/check-actions.js`
Expected: PASS, with the C# file count one higher.

Then build on the VM with the three commands from Task 1 Step 11.
Expected: `Your package has been successfully created.`

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Services/MessageCache.cs WhatsappApp/Services/DataService.cs \
  WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/WhatsappApp.csproj
git commit -m "$(cat <<'EOF'
perf: paint a chat from disk before the server answers

Opening a conversation started from an empty list and waited for the history
frame, which is seconds of blank page per chat. The last messages are kept per
chat and shown at once, then replaced by the real ones.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
```

---

### Task 7: Record what this changed, and check it on the phone

**Files:**
- Modify: `.agents/skills/maintain-the-app/SKILL.md`
- Modify: `.agents/skills/test-the-app/SKILL.md`
- Modify: `README.md` + `README.it.md`
- Modify: `WhatsappBridge/README.md` + `WhatsappBridge/README.it.md`

**Interfaces:**
- Consumes: everything from Tasks 1-6.
- Produces: nothing the code depends on.

- [ ] **Step 1: Add the maintainer gotchas**

In `.agents/skills/maintain-the-app/SKILL.md`, append to the gotchas list:

```markdown
- **A picked or shared file is copied, not read.** `AttachmentInbox.PutAsync` copies
  the chosen file into `LocalFolder` (`StorageFile.CopyAsync`) and keeps its name;
  `ChatPage.SendAttachmentAsync` streams it out in `MediaChunkBytes` (525000, a
  multiple of 3, so each piece is exactly 700000 base64 characters). Reading the file
  into a `byte[]` first is what took the app down when a video was shared from the
  gallery, and `tools/check-memory.js` now fails on it. A fire-and-forget deposit has
  no one to catch its exception: `App.DepositPickedFileAsync` catches its own.
- **A `MediaElement` gets the `ms-appdata` file, not a stream.** Holding an
  `IRandomAccessStream` open for the life of the page means the viewer dies with the
  page, and a decode failure that closes the overlay is indistinguishable from a tap
  that did nothing. `ChatPage.PlayVideo` sets `Source` to
  `ms-appdata:///local/<name>` and leaves `VideoErrorText` up when `MediaFailed`
  fires. WP8.1 still has no `Deferral` here - see the `ShareOperation` gotcha.
- **A cache is a photograph, not a truth.** `MessageCache` holds the last 60 messages
  of a chat and is written on leaving the page, not per message. It is only read when
  the in-memory list is empty (`DataService.LoadCachedMessagesAsync`), so a history
  frame that already arrived is never clobbered. Its entries are `IsHistory`, so they
  raise no toast and add no unread count.
```

- [ ] **Step 2: Add the on-device checks**

In `.agents/skills/test-the-app/SKILL.md`, append after the last numbered item:

```markdown
40. Share a photo from the Gallery, then share a long video (over 30 MB): neither
    takes the app down, and the composition bar says Image selected for the photo and
    Video selected for the video.
41. Receive an audio and a document from someone, with the app closed, then open the
    chat: both say what they are (the document shows its file name), instead of an
    empty bubble.
42. Tap a received video whose pieces are still arriving: a ring turns in the bubble,
    then it plays. Tap a video the phone cannot decode: the overlay stays up and says
    it cannot be played, instead of closing on its own.
43. Open a chat, go back, and reopen it: the messages are there before the connection
    comes up. Kill the app and reopen it: they are still there.
44. The chat list has no entries you cannot reply to (channels).
```

- [ ] **Step 3: Update both README pairs**

In `README.md` and `README.it.md`, add one bullet to the feature list next to the existing attachment bullet, and update the limitations bullet about history media.

English, add after the video-playback bullet:

```markdown
- A file shared or picked from the Gallery is copied into the app's own folder instead of being read into memory, so a long video is streamed out in pieces rather than taking the app down
```

Italian, same position:

```markdown
- un file condiviso o scelto dalla Galleria viene copiato nella cartella dell'app invece che letto in memoria, quindi un video lungo si spedisce a pezzi invece di chiudere l'app
```

Then in the limitations list of both, add:

```markdown
- A chat's last messages are cached per conversation, up to 60, and a received video's bytes stay in the app's local folder until the app is reinstalled or the file is overwritten by another download of the same message.
```

```markdown
- Gli ultimi messaggi di ogni conversazione sono in una cache per chat, fino a 60, e i byte di un video ricevuto restano nella cartella locale dell'app finche' l'app non viene reinstallata o il file non viene sovrascritto da un altro scarico dello stesso messaggio.
```

In `WhatsappBridge/README.md` and `README.it.md`, in the chat-list bullet, add one sentence:

```markdown
  Channel JIDs (`...@newsletter`) are skipped here and in the webhook: a channel is
  not a conversation and cannot be replied to.
```

```markdown
  I JID dei canali (`...@newsletter`) si saltano qui e nel webhook: un canale non e'
  una conversazione e non si puo' rispondere.
```

- [ ] **Step 4: Run the doc guard**

Run: `node tools/check-docs.js`
Expected: PASS — `2 doc pair(s) in step (1 closed by "Disclosure"), no emoji in 35 .md file(s).`

- [ ] **Step 5: Run the whole fast gate and the adapter suite**

Run:
```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && \
node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && \
node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"
cd WhatsappBridge && npm test
```
Expected: every guard prints `OK:`, the tool tests pass, and the adapter suite passes.

- [ ] **Step 6: Build on the VM one last time**

Run the three commands from Task 1 Step 11.
Expected: `COPIA=0` and `Your package has been successfully created.`

- [ ] **Step 7: Commit**

```bash
git add .agents/skills/maintain-the-app/SKILL.md .agents/skills/test-the-app/SKILL.md \
  README.md README.it.md WhatsappBridge/README.md WhatsappBridge/README.it.md
git commit -m "$(cat <<'EOF'
docs: record the attachment, cache and playback lessons

The copied-file rule, the ms-appdata source and the write-on-leave cache are
the parts a later change is most likely to undo, and the phone checklist now
covers sharing a long video and a video that will not decode.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage**

- "l'app e un po slugghish" — Task 6 (cached messages so a chat paints at once) and Task 1 (no whole-file copies, no fire-and-forget failures). Both reduce the work done on the critical path. No other item in the request maps to a separate performance task.
- "quando si carica un video dice immagine selezionata e non lo carica" — Task 2 (label plus a play box, no bitmap decode of a video).
- "rimuovi i canali dalla chat normale" — Task 3 (chats filter and webhook filter).
- "gli audio/documenti non compaiono neanche" — Task 4 (always a word; the app no longer hides the audio label).
- "i video lo clicco, ma non si vede/succede niente e metti un indicatore del download" — Task 5 (ms-appdata playback, visible failure, loading ring).
- "METTI CACHE per allegerire il caricamento di tutto" — Task 6 (per-chat message cache). Avatar caching is deliberately not included; it would be a separate plan.
- "quando provo a condividere una foto/video dalla galleria l'app crasha ancora" — Task 1 (copy instead of read, caught fire-and-forget).

**2. Placeholder scan**

No "TBD", no "handle edge cases", no "similar to Task N". Every code step carries the code to write. The only prose-only step is Task 6 Step 3, which is a verification instruction with a named method and a stated invariant.

**3. Type consistency**

- `AttachmentInbox.PutBytesAsync` (Task 1) is the name used in Task 1 Step 9. The old `PutBytes` is gone; if a later search finds it, that is a missed call site (only `App.xaml.cs` had one).
- `ImageHelper.FromFileAsync` (Task 1 Step 6) is used by `ChatMessage.LoadBitmapAsync` (Task 1 Step 7) and `ChatPage.ShowLocalPreviewAsync` (Task 1 Step 8).
- `ChatMessage.MediaFilePath` exists already (added with the video playback work) and is written in Task 1 Step 8, read in Task 5.
- `ChatMessage.IsMediaLoading` is defined in Task 5 Step 5, raised in Task 5 Step 8, cleared in `DataService.ApplyMedia` and `ClearMediaLoading` (Task 5 Step 8), and bound in Task 5 Step 7.
- `MessageCache.LoadAsync` / `SaveAsync` (Task 6 Step 1) match the calls in Task 6 Step 2.
- `MediaChunkBytes = 525000` (Task 1) is a multiple of 3, so 700000 base64 characters, matching `ChatPage.MediaChunkChars` and the adapter's `MEDIA_CHUNK_CHARS`.
- The adapter's `media` error frame now carries `RelatedMessageId`, which is the field `ClearMediaLoading` matches on (Task 5).
