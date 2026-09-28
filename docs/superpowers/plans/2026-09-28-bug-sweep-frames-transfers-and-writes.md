# Bug sweep: frames, transfers and file writes

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the five defects a read of the app, the adapter and the .NET server turned up - three of them races where asynchronous code shares a `DataWriter`, a file or a dictionary.

**Architecture:** The app's receive path is fire-and-forget by design (`DispatchOnUiThread` and the `async void` handlers return at their first `await`), so a handler that awaits is running *while the next frame is already being handled*: any two of them that touch the same `DataWriter`, the same `StorageFile` or the same transfer entry interleave. The fix is one small primitive, `Services/SerialQueue.cs`, which runs work one piece at a time in arrival order, used by the three places that need it. Two further fixes are about trusting a number that arrives over the wire: the adapter sends an attachment even when a piece is missing, and the .NET server allocates whatever length a client announces.

**Tech Stack:** WP8.1 XAML app (WinRT, C# 5, no test project - its gate is `tools/check-csharp5.js` plus the msbuild build on the Parallels VM), Node.js adapter (`node --test`, 133 tests), .NET Framework 4.5.1 console server (built by the same solution).

## Global Constraints

- **C# 5 only.** No `nameof`, no string interpolation, no `?.`, no expression-bodied members, no `async` Main. `node tools/check-csharp5.js` (37 C# files after Task 1) must stay green, and so must every other guard:
  ```bash
  node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict \
    && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js \
    && node tools/check-memory.js && node tools/check-actions.js \
    && node --test "tools/test/**/*.test.js" && (cd WhatsappBridge && npm test)
  ```
  Counts as of the first commit: 36 C# files (37 after Task 1), 125 keys in each `.resw`, 23 inline icon Paths (14 distinct), 20 buttons and 1 button style, 133 adapter tests (137 after Task 4), 47 tests in `tools/test`.
- **A new C# file must be added to `WhatsappApp/WhatsappApp.csproj`** as a `<Compile Include="Services\SerialQueue.cs" />`, or it is not compiled at all. That file has been rewritten as CRLF+BOM by an edit tool before: after editing it run `perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' WhatsappApp/WhatsappApp.csproj` and check with `file`.
- **No `Segoe MDL2 Assets`, no icon changes, no new user string.** The `.resw` pair stays at 125 keys; `check-resw.js --strict` enforces it.
- **LF, no BOM** in every file that is touched.
- **The wire format does not change.** No frame field is added, removed or renamed: `MediaChunkTotal` already travels in `media.begin` and the app already sets it on every chunk of an upload (`ChatPage.SendMediaChunkAsync`). The 4-byte little-endian length prefix and the 8 MiB ceiling (`CommunicationService.MaxFrameLength` = `server.js`'s `MAX_FRAME_LENGTH`) are shared and must stay equal.
- **The adapter keeps zero npm runtime dependencies.**
- **Build gate** on the Parallels VM `Windows 11` (retry the identical MSBuild call once on exit 255 with `PrlJob_GetRetCode: Invalid argument`); the solution builds `WhatsappApp` **and** `WhatsappServer`, so one run covers Tasks 1, 2, 3 and 5:
  ```bash
  prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"
  prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"
  prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:n /p:WarningLevel=4"
  ```
  Expected: `COPIA=0`, then `Avvisi: 0`, `Errori: 0` and `Your package has been successfully created.` (`/v:m` prints no `Avvisi`/`Errori` summary here; `/v:n` does.)
- **Every adapter change also updates the Docker mirror** (`/tmp/docker-whatsappforwp`), with `server/SOURCE_COMMIT` pointing at the commit that carries the change: `node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP`, then `node tools/sync.js --check`, then `cd server && npm test`, commit and push on the mirror's `main`.
- **Commits**: explicit paths only (`git add <file> ...`), never `-A`. `WhatsappApp/Package.appxmanifest` carries a deliberate uncommitted diff and the two `.DS_Store` files are untracked noise - leave all three alone. Commit messages carry **no apostrophes**. Footer: `Generated with Codebuff` prefixed with the robot emoji, then `Co-Authored-By: Codebuff <noreply@codebuff.com>` - written without the emoji inside this markdown file, because `check-docs.js` rejects emoji in a `.md`, and added when the commit is made.
- **No emoji in any `.md`**, except the warning sign U+26A0.
- **A fix that cannot be tested here is still a fix**: C# has no test project in this repo, so Tasks 1, 2, 3 and 5 are verified by the guards, by the build, and by the reading that produced them. Each of those tasks adds the on-device check that would catch a regression, in `test-the-app`.

---

## The five defects

### 1. Two frames can be written to one socket at the same time (app, severe)

`DataWriter` is not safe under concurrent `StoreAsync` calls: it has one buffer, and `WriteUInt32` writes into it. Every send funnels through `CommunicationService.SendMessageAsync` -> `SendFrameAsync(_writer, ...)`, and two of those can overlap:

- `ChatPage.SendAttachmentAsync` awaits one `SendMediaChunkAsync` per 525000-byte piece, for as long as the upload lasts (a 30 MB video is about 45 pieces);
- while that loop is awaiting, the user can type a message and press send (`SendMessage` -> `AddAndSendMessage` -> `SendMessageAsync`), and `MarkRead()` sends a `read` control frame on entering a chat.

When two overlap, one writer's length prefix can end up in front of the other's payload. The receiver then reads a frame in two halves: the tag byte is a JSON byte, the HMAC check fails, `DecryptToMessage` logs `DecryptToMessage`, and `ListenForMessagesAsync` closes the connection. The user sees "Connection lost" and a reconnect after having sent a message during an upload.

Same defect, second path: `BroadcastToAllClientsAsync` creates a new `DataWriter` per client per frame over the same `OutputStream`, so two overlapping broadcasts interleave at the stream, not just at the buffer.

### 2. An incoming media is abandoned when two pieces overlap (app, severe for video/audio/document)

`IncomingMediaStore.AddChunkAsync` checks the piece order against `pending.Received` **before** the await and increments `Received` **after** it:

```csharp
if (frame.MediaChunkIndex != pending.Received) { Transfers.Remove(...); Abandon(pending); return null; }
try {
    if (pending.ToDisk) { pending.Writer.WriteBytes(...); await pending.Writer.StoreAsync(); }
    else { pending.Base64.Append(frame.MediaData); }
    pending.Received++;
}
```

The delivery path lets a second call start while the first is awaiting: `CommunicationService.DispatchOnUiThread` is `async void` and is never awaited by the read loop, `DataService.OnControlMessageReceived` calls `ApplyMediaFrame(message)` which is `async void`, and `ApplyMediaFrame` awaits `AddChunkAsync`. So piece N+1 can arrive while piece N is being written to disk. The index check then fails against a `Received` that has not moved yet, the transfer is thrown away, and every following piece starts a new transfer that fails the same way: a multi-piece video, voice note or document never arrives, and the file left on disk is a fragment. Images are single-piece (they stay in `Base64`, no await), which is why this has not been seen as often as it deserves.

Third, smaller part of the same defect: two overlapping calls would also `WriteBytes`/`StoreAsync` to the same `pending.Writer`.

### 3. A preference change can be overwritten by an older one (app, data loss)

`ChatPreferences.Save()` fires `WriteAsync(file)` without waiting, and `WriteAsync` serialises the snapshot at its start and then awaits `CreateFileAsync(ReplaceExisting)` + `WriteTextAsync`. Two changes in quick succession - "Unpin all" walks the whole list calling `SetPinned(id, false)`, which is one `Save()` per chat - produce two (or more) overlapping writes of the same file. Whichever wins is arbitrary, so the file can end up holding the state from *before* the last change, and on WP8.1 two writers on one file can also raise a sharing violation, which the `catch` turns into a `Diag.Failed` line and nothing else. Result: a pin, a mute or a delete that was on screen is gone after a restart.

### 4. The adapter sends an attachment that arrived incomplete, and keeps the pieces of one that never ends (adapter)

`server.js`:

```js
function mediaChunk(msg) {
  const transfer = mediaTransfers.get(msg.MediaTransferId);
  if (!transfer) return;
  transfer.parts[msg.MediaChunkIndex] = Buffer.from(msg.MediaData || '', 'base64');
}
```

`mediaBegin` drops `MediaChunkTotal` on the floor, and `mediaEnd` concatenates whatever pieces are there (`transfer.parts.filter((part) => part)`), then sends it. So:

- a piece lost because the app's upload threw (the app catches, marks the message failed and never sends `media.end`, but a *later* `media.end` for that id, or a retry that skips a piece, is silently accepted) produces a truncated file on the receiver's side with no error anywhere;
- `msg.MediaChunkIndex` is used as an array index with no bound: one frame with `MediaChunkIndex: 2000000000` makes a sparse array with two billion holes, and `filter` then walks it;
- `MAX_MEDIA_BYTES` is only checked at the end, after all the pieces have been buffered, so a client can make the adapter hold 64 MB before being told no;
- a transfer whose `media.end` never comes is never removed from `mediaTransfers`, and nothing limits how many live transfers there are: each one is a growing array of base64 pieces, kept for the life of the process.

### 5. The .NET server allocates whatever length a client announces (server, low)

`WhatsappServer/Program.cs`:

```csharp
int messageLength = BitConverter.ToInt32(lengthBytes, 0);
var messageData = new byte[messageLength];
```

A negative length throws `OverflowException`, caught, connection dropped - survivable. A length of 2 GB is an immediate attempt at a 2 GB allocation: on a machine with the page file it succeeds and the process sits on it, which is a denial of service from an unauthenticated peer. The app has had the answer to this since the frame-limit work (`MaxFrameLength`); the server has none. `_clients` is also a `List<TcpClient>` written by the accept loop and read by every client task, with no lock, so a broadcast during a connect/disconnect can throw `InvalidOperationException` inside a client's task.

## File Structure

| File | Responsibility after the change |
| --- | --- |
| `WhatsappApp/Services/SerialQueue.cs` (new) | Runs work one piece at a time, in arrival order, on whatever thread the previous piece finished on. The only place that knows how the queue is built. |
| `WhatsappApp/WhatsappApp.csproj` | Lists the new file under `<Compile>`. |
| `WhatsappApp/Services/CommunicationService.cs` | Writes a frame only through the queue, so one socket has one write in flight. |
| `WhatsappApp/Services/IncomingMediaStore.cs` | Assembles one piece at a time, so the order check and the counter cannot be raced. |
| `WhatsappApp/Services/ChatPreferences.cs` | Snapshots on the caller's thread and writes the file through the queue, in order. |
| `WhatsappBridge/server.js` | Refuses an attachment whose pieces do not all arrive, bounds the piece index and the live transfers. |
| `WhatsappBridge/test/server.test.js` | The four new tests. |
| `WhatsappBridge/README.md` + `README.it.md` | What `media.begin`/`media.end` now guarantee, in both languages. |
| `WhatsappServer/Program.cs` | Rejects a frame length outside the shared ceiling, and guards the client list. |
| `.agents/skills/maintain-the-app/SKILL.md` | The gotcha: a shared writer is written through a queue. |
| `.agents/skills/test-the-app/SKILL.md` | The refreshed counts and the on-device checks for the three races. |

---

### Task 1: one write at a time on the socket, and the queue that does it

**Files:**
- Create: `WhatsappApp/Services/SerialQueue.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj` (the `<Compile>` block, after `Services\SelfCheck.cs`)
- Modify: `WhatsappApp/Services/CommunicationService.cs` (`SendFrameAsync`, `BroadcastToAllClientsAsync`, fields)
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (`## Known gotchas`)
- Modify: `.agents/skills/test-the-app/SKILL.md` (counts line, the on-device checklist)

**Interfaces:**
- Consumes: nothing.
- Produces: `WhatsappApp.Services.SerialQueue` with
  - `Task<T> RunAsync<T>(Func<Task<T>> work)`
  - `Task RunAsync(Func<Task> work)`
  One instance per shared resource; `RunAsync` never throws the antecedent's exception at the caller (a failed piece does not poison the queue).

- [x] **Step 1: Write the queue**

Create `WhatsappApp/Services/SerialQueue.cs`:

```csharp
using System;
using System.Threading.Tasks;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Una cosa alla volta, nell'ordine in cui arrivano.
    ///
    /// Perche' esiste: mezzo servizio di questa app e' asincrono, e i suoi
    /// handler non si aspettano - DispatchOnUiThread e' async void, i gestori
    /// dei frame sono async void, e la scrittura di un file parte senza che
    /// nessuno la aspetti. Due di loro che toccano la stessa risorsa si
    /// intrecciano: due StoreAsync sullo stesso DataWriter mettono il prefisso
    /// di lunghezza di uno davanti al payload dell'altro, e la connessione cade
    /// su un frame che non esiste.
    ///
    /// Il lavoro entra qui e viene eseguito tutto, in fila: la coda e' una
    /// catena di Task, non un thread, quindi non costa niente quando e' vuota.
    /// Un pezzo che fallisce non ferma la coda: il guasto lo vede chi ha
    /// chiamato, e il pezzo dopo parte lo stesso.
    /// </summary>
    public sealed class SerialQueue
    {
        private readonly object _gate = new object();
        private Task _tail = Done();

        private static Task Done()
        {
            var source = new TaskCompletionSource<bool>();
            source.SetResult(true);
            return source.Task;
        }

        /// <summary>
        /// Accoda il lavoro e restituisce il suo esito. La prima parte viene
        /// eseguita subito se la coda e' vuota (ExecuteSynchronously), come
        /// farebbe una chiamata diretta.
        /// </summary>
        public Task<T> RunAsync<T>(Func<Task<T>> work)
        {
            if (work == null) throw new ArgumentNullException("work");

            Task<T> next;
            lock (_gate)
            {
                next = _tail
                    .ContinueWith(delegate { return work(); },
                        TaskContinuationOptions.ExecuteSynchronously)
                    .Unwrap();

                // La coda continua anche se questo pezzo fallisce: l'eccezione
                // resta nel Task che e' stato restituito, e va osservata li'.
                _tail = next.ContinueWith(
                    delegate(Task<T> finished) { AggregateException ignored = finished.Exception; },
                    TaskContinuationOptions.ExecuteSynchronously);
            }
            return next;
        }

        /// <summary>La stessa coda, per un lavoro che non restituisce niente.</summary>
        public Task RunAsync(Func<Task> work)
        {
            if (work == null) throw new ArgumentNullException("work");

            return RunAsync<bool>(async delegate
            {
                await work();
                return true;
            });
        }
    }
}
```

- [x] **Step 2: Add the file to the project and repair the line endings**

In `WhatsappApp/WhatsappApp.csproj`, right after `<Compile Include="Services\SelfCheck.cs" />`, add:

```xml
    <Compile Include="Services\SerialQueue.cs" />
```

Then, because an edit tool can leave that file as CRLF with a BOM:

```bash
perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' WhatsappApp/WhatsappApp.csproj
file WhatsappApp/WhatsappApp.csproj
```

Expected: `... ASCII text` (not `with BOM`, not `with CRLF line terminators`), and `grep -c "Compile Include" WhatsappApp/WhatsappApp.csproj` is one more than before.

- [x] **Step 3: Run the C# guard**

Run: `node tools/check-csharp5.js`
Expected: `OK: 37 C# file(s) are C# 5 compatible.` If it reports the new file, the fix is in the file, not in the guard: `TaskCompletionSource`, `ContinueWith`, `Unwrap` and `async delegate` are all WP8.1-legal.

- [x] **Step 4: Send every frame through the queue**

In `WhatsappApp/Services/CommunicationService.cs`, add the field next to the other socket fields (after `private bool _isConnected = false;`):

```csharp
        /// <summary>
        /// Una scrittura alla volta su un socket. Il DataWriter ha un solo
        /// buffer: due StoreAsync in volo insieme mettono il prefisso di
        /// lunghezza di uno davanti al payload dell'altro, e l'altro capo legge
        /// un frame che non esiste. Succedeva mandando un messaggio mentre un
        /// allegato stava ancora salendo.
        /// </summary>
        private readonly SerialQueue _writes = new SerialQueue();
```

Replace `SendFrameAsync` with:

```csharp
        /// <summary>
        /// Encrypts the JSON bytes and writes one frame on the given writer:
        /// [4-byte UInt32LE payload length][encrypted payload].
        /// Il writer arriva da fuori perche' e' quello del socket, creato una
        /// volta in ConnectToServerAsync: prima ne veniva creato — e mai
        /// chiuso — uno nuovo per ogni frame inviato.
        /// Il payload si cifra adesso, con il writer di adesso: quello che entra
        /// in coda e' una scrittura gia' decisa, non una promessa di scrivere.
        /// </summary>
        private Task SendFrameAsync(DataWriter writer, byte[] jsonBytes)
        {
            byte[] payload = CryptoHelper.Encrypt(jsonBytes);
            return _writes.RunAsync(() => WriteFrameAsync(writer, payload));
        }

        /// <summary>La scrittura, eseguita dalla coda.</summary>
        private static async Task WriteFrameAsync(DataWriter writer, byte[] payload)
        {
            writer.WriteUInt32((uint)payload.Length);
            writer.WriteBytes(payload);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
```

Replace the body of the `foreach` in `BroadcastToAllClientsAsync` so a whole broadcast is one queued piece (the `DataWriter` there is created per client, but the `OutputStream` behind it is shared):

```csharp
            foreach (var client in snapshot)
            {
                if (client == excludeSocket) continue;

                StreamSocket target = client;
                try
                {
                    // Una trasmissione intera e' un pezzo della coda: due
                    // trasmissioni in volo sullo stesso OutputStream si
                    // intreccerebbero come due StoreAsync sullo stesso buffer.
                    await _writes.RunAsync(delegate
                    {
                        return WriteFrameAsync(CreateFrameWriter(target.OutputStream), payload);
                    });
                }
                catch (Exception ex)
                {
                    Diag.Failed("BroadcastToAllClientsAsync", ex);
                    deadClients.Add(target);
                }
            }
```

Note the `delegate { return ... }` and not a lambda with an expression body: C# 5 accepts both, but the captured variable must be a local (`target`), never the `foreach` variable, which on C# 5 is shared by the closure.

- [x] **Step 5: Check the other callers still compile**

Run: `grep -n "SendFrameAsync\|WriteFrameAsync" WhatsappApp/Services/CommunicationService.cs`
Expected: the handshake call in `ConnectToServerAsync` is `await SendFrameAsync(writer, Encoding.UTF8.GetBytes(handshake.ToJson()));` (an `await` on a `Task`, unchanged), and the only other two hits are the definitions.

- [x] **Step 6: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"`
Expected: every line `OK: ...`, with `37 C# file(s)`.

- [x] **Step 7: Build it**

Run the three build-gate commands.
Expected: `COPIA=0`, `Avvisi: 0`, `Errori: 0`, `Your package has been successfully created.`

- [x] **Step 8: Write the gotcha and the counts**

In `.agents/skills/maintain-the-app/SKILL.md`, in `## Known gotchas`, add:

```markdown
- **A shared writer is written through a queue, never by two callers at once.**
  The receive path is fire-and-forget on purpose (`DispatchOnUiThread` and the
  frame handlers are `async void`, and the read loop does not await the
  dispatch), so an awaiting handler is running while the next frame is already
  being handled. Two `DataWriter.StoreAsync` calls on one writer put one frame's
  length prefix in front of the other's payload, and the peer reads a frame that
  does not exist; two `FileIO` writes on one file lose one of the two.
  `Services/SerialQueue.cs` is the answer: `_writes.RunAsync(...)` in
  `CommunicationService`, and the same queue in `IncomingMediaStore` and
  `ChatPreferences`. Put the *decision* (the encrypted payload, the JSON
  snapshot) on the caller's thread and only the write in the queue, or the queue
  touches state that another thread is still changing.
```

In `.agents/skills/test-the-app/SKILL.md`, change the counts line to `37 C# files` and add to the on-device checklist:

```markdown
53. Send a text message while a large video is still uploading (the progress
    line keeps moving): the video arrives whole, the text is not lost, and the
    connection does not drop. A dropped connection here means two writes on one
    socket: see the `SerialQueue` gotcha in `maintain-the-app`.
```

- [x] **Step 9: Commit**

```bash
git add WhatsappApp/Services/SerialQueue.cs WhatsappApp/WhatsappApp.csproj WhatsappApp/Services/CommunicationService.cs .agents/skills/maintain-the-app/SKILL.md .agents/skills/test-the-app/SKILL.md
git commit -m "$(cat <<'EOF'
fix: one frame at a time on the socket

A DataWriter has one buffer and is not safe under concurrent StoreAsync, and
two sends could overlap: the attachment loop awaits one piece per 525000 bytes
while the user can send a text, and MarkRead sends a read frame on entering a
chat. A single frame with a wrong length prefix is enough to desync the stream
and drop the connection.

SerialQueue runs work one piece at a time in arrival order; the socket writes
go through it, and the encrypted payload is built on the caller's thread so the
queued work touches nothing shared.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
```

---

### Task 2: an incoming media is assembled one piece at a time

**Files:**
- Modify: `WhatsappApp/Services/IncomingMediaStore.cs` (`AddChunkAsync`)
- Modify: `.agents/skills/test-the-app/SKILL.md` (the on-device checklist)

**Interfaces:**
- Consumes: `SerialQueue.RunAsync<T>(Func<Task<T>>)` and `RunAsync(Func<Task>)` from Task 1.
- Produces: `IncomingMediaStore.AddChunkAsync(ChatMessage frame) -> Task<IncomingMediaResult>` with **unchanged signature and unchanged semantics for the caller** (`DataService.ApplyMediaFrame` stays as it is); the pieces are now processed one at a time, in arrival order.

- [x] **Step 1: Add the queue and split the method**

In `WhatsappApp/Services/IncomingMediaStore.cs`, after the `Transfers` field, add:

```csharp
        /// <summary>
        /// I pezzi di tutti i media in arrivo, uno alla volta.
        ///
        /// Perche' serve: il controllo d'ordine qui sotto confronta l'indice del
        /// pezzo con Received, e Received si incrementa dopo la scrittura su
        /// disco. Il percorso che porta qui non aspetta (DispatchOnUiThread e'
        /// async void, ApplyMediaFrame e' async void), quindi il pezzo dopo
        /// poteva arrivare mentre il primo era ancora in scrittura: il
        /// confronto falliva, il media veniva buttato via, e ogni pezzo
        /// successivo ne apriva un altro che falliva allo stesso modo. Un video
        /// o un vocale non arrivavano mai.
        /// </summary>
        private static readonly SerialQueue Chunks = new SerialQueue();
```

Then turn `AddChunkAsync` into the two halves below - the public method keeps its name and signature and only queues, the body moves to `AddChunkCoreAsync` unchanged:

```csharp
        /// <summary>
        /// Aggiunge un pezzo. Restituisce null finche' il media non e' completo.
        /// Il pezzo entra in coda: il controllo d'ordine e la scrittura devono
        /// essere un'operazione sola.
        /// </summary>
        public static Task<IncomingMediaResult> AddChunkAsync(ChatMessage frame)
        {
            return Chunks.RunAsync(delegate { return AddChunkCoreAsync(frame); });
        }

        private static async Task<IncomingMediaResult> AddChunkCoreAsync(ChatMessage frame)
        {
            if (frame == null || string.IsNullOrEmpty(frame.RelatedMessageId)) return null;
            if (string.IsNullOrEmpty(frame.MediaData)) return null;
            // ... tutto il corpo attuale, dalla riga `int total = ...` all'ultimo return
        }
```

Everything from `int total = frame.MediaChunkTotal > 0 ? frame.MediaChunkTotal : 1;` to the final `return result;` moves into `AddChunkCoreAsync` **verbatim**: the dictionary, the index check, the `ToDisk` branch, `pending.Received++`, the completion branch. The order inside it is what the queue now protects; do not "simplify" the increment to before the await, because with the queue the check-then-write pair is atomic and moving the increment would only hide the invariant.

- [x] **Step 2: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"`
Expected: every line `OK: ...`, `37 C# file(s)`.

- [x] **Step 3: Build it**

Run the three build-gate commands.
Expected: `COPIA=0`, `Avvisi: 0`, `Errori: 0`, `Your package has been successfully created.`

- [x] **Step 4: Add the on-device check**

In `.agents/skills/test-the-app/SKILL.md`, after check 53, add:

```markdown
54. Have someone send a video of about 30 MB (several pieces) with the app closed,
    then open the chat and tap the play box: the video plays through to the end,
    and the file on the phone is as long as the original. Half a video, or a
    video that says it cannot be played, means the pieces were assembled two at
    a time: see `IncomingMediaStore` and the `SerialQueue` gotcha.
```

- [x] **Step 5: Commit**

```bash
git add WhatsappApp/Services/IncomingMediaStore.cs .agents/skills/test-the-app/SKILL.md
git commit -m "$(cat <<'EOF'
fix: assemble an incoming media one piece at a time

The order check ran before the await and Received moved after it, and nothing
held the two together: the next piece could be handled while the previous was
still being written, so the check failed, the transfer was thrown away and
every following piece started another one that failed the same way. A video,
a voice note or a document was never assembled.

The pieces now go through a SerialQueue, so the check and the write are one
operation and the file gets the pieces in order.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
```

---

### Task 3: a preference change is written in order

**Files:**
- Modify: `WhatsappApp/Services/ChatPreferences.cs` (`Save`, `WriteAsync` -> `Serialize` + `WriteFileAsync`)
- Modify: `.agents/skills/test-the-app/SKILL.md` (the on-device checklist)

**Interfaces:**
- Consumes: `SerialQueue.RunAsync(Func<Task>)` from Task 1.
- Produces: unchanged public surface (`LoadAsync`, `IsLoaded`, `IsPinned`, `IsMuted`, `IsHidden`, `SetPinned`, `SetMuted`, `Hide`, `Reveal`); `Save()` still returns void and still never throws.

- [x] **Step 1: Snapshot on the caller's thread, write through the queue**

In `WhatsappApp/Services/ChatPreferences.cs`, replace `Save()` and `WriteAsync` with:

```csharp
        /// <summary>
        /// Scatta adesso e scrive in coda. Lo scatto si fa sul thread di chi ha
        /// cambiato la preferenza, dove l'elenco e' fermo: dentro la coda il
        /// lavoro tocca solo una stringa, non Known.
        ///
        /// Scrivere subito e senza aspettare perdeva l'ultimo cambio: due
        /// modifiche ravvicinate (Unpin all ne fa una per chat) lanciavano due
        /// scritture sullo stesso file, e poteva finire sul disco quella piu'
        /// vecchia. Ora la seconda aspetta la prima, e l'ultima scritta e'
        /// l'ultima decisa.
        /// </summary>
        private static void Save()
        {
            string json = Serialize();
            Writes.RunAsync(delegate { return WriteFileAsync(json); });
        }

        /// <summary>L'elenco come sta adesso, in JSON. Va chiamato sul thread che ha cambiato la preferenza.</summary>
        private static string Serialize()
        {
            var file = new ChatPreferenceFile { Chats = new List<ChatPreference>() };
            foreach (var entry in Known.Values) file.Chats.Add(entry);

            using (var stream = new MemoryStream())
            {
                Serializer.WriteObject(stream, file);
                return Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
            }
        }

        /// <summary>
        /// Scrive il file. Non aspetta nessuno - chi cambia un pin non ha niente
        /// da fare con l'esito - quindi cattura da sola: un deposito senza
        /// padrone non deve poter far cadere la pagina. Mai un'eccezione.
        /// </summary>
        private static async Task WriteFileAsync(string json)
        {
            try
            {
                StorageFile storage = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(storage, json);
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPreferences.Save", ex);
            }
        }
```

and add the field next to `Known`:

```csharp
        /// <summary>Le scritture del file, una alla volta e in ordine.</summary>
        private static readonly SerialQueue Writes = new SerialQueue();
```

`using System.IO;` and `using System.Text;` are already there; no `using` changes are needed.

- [x] **Step 2: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-resw.js --strict && node tools/check-memory.js && node --test "tools/test/**/*.test.js"`
Expected: every line `OK: ...`, `37 C# file(s)`, `125 key(s)`.

- [x] **Step 3: Build it**

Run the three build-gate commands.
Expected: `COPIA=0`, `Avvisi: 0`, `Errori: 0`, `Your package has been successfully created.`

- [x] **Step 4: Add the on-device check**

In `.agents/skills/test-the-app/SKILL.md`, after check 54, add:

```markdown
55. Pin two chats, then press Unpin all and immediately kill the app: on the next
    start no chat is pinned. A pin that comes back means two writes of the same
    file overlapped: see `ChatPreferences`.
```

- [x] **Step 5: Commit**

```bash
git add WhatsappApp/Services/ChatPreferences.cs .agents/skills/test-the-app/SKILL.md
git commit -m "$(cat <<'EOF'
fix: write the chat preferences in order

Save fired a write and moved on, and the write serialised its snapshot when it
started and then awaited two file operations, so two changes in a row (Unpin
all is one per chat) could land the older snapshot last, or collide on the same
file and lose one of them. A pin, a mute or a delete that was on screen could
be gone after a restart.

The snapshot is taken on the caller's thread and the file write goes through a
SerialQueue, so the last change is the last thing written.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
```

---

### Task 4: the adapter refuses an attachment that did not arrive whole

**Files:**
- Modify: `WhatsappBridge/server.js` (`mediaBegin`, `mediaChunk`, `mediaEnd`, the constants near `MAX_MEDIA_BYTES`)
- Test: `WhatsappBridge/test/server.test.js`
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`
- Modify: `.agents/skills/test-the-app/SKILL.md` (the adapter test count)
- Modify: `/tmp/docker-whatsappforwp/` (the mirror: `tools/sync.js`, `server/`, `SOURCE_COMMIT`)

**Interfaces:**
- Consumes: `sendControl(fields)`, `logger(level, message)`, `mediaTransfers` (a `Map`), `MAX_MEDIA_BYTES` (64 MiB), `mediaKindOf`, `sendMediaToGowa` - all already in `createBridge`.
- Produces: `mediaBegin`/`mediaChunk`/`mediaEnd` behave the same for a complete transfer; for an incomplete one they send nothing to GOWA and one `error` control frame with `relatedMessageId` set to the transfer's message when it has one. New constant `MAX_LIVE_TRANSFERS = 4`. Nothing else in the module's exports changes.

- [x] **Step 1: Write the failing tests**

In `WhatsappBridge/test/server.test.js`, after the test `un allegato immagine va a sendImage e uno sconosciuto a sendFile`, add:

```js
test('un allegato a cui manca un pezzo non viene mandato, e lo dice', async () => {
  let sent = 0;
  const gowa = {
    sendVideo: async () => { sent++; return 'V1'; },
    sendImage: async () => { sent++; return 'I1'; },
    sendFile: async () => { sent++; return 'F1'; }
  };
  const handles = [];
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => handles.push(decodeFrame(packet)) });

  const base64 = Buffer.from('un video finto che si divide in tre').toString('base64');
  const third = Math.ceil((base64.length / 3) / 4) * 4;

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'c1', MediaFileName: 'clip.mp4', MediaMimeType: 'video/mp4', MediaChunkTotal: 3 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'c1', MediaChunkIndex: 0, MediaData: base64.slice(0, third) });
  // il pezzo 1 non arriva mai
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'c1', MediaChunkIndex: 2, MediaData: base64.slice(third * 2) });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'c1' });

  assert.strictEqual(sent, 0, 'un allegato incompleto non deve partire');
  const errors = handles.filter((f) => f.Command === 'error');
  assert.strictEqual(errors.length, 1);
  assert.match(errors[0].Text, /complete/i);
});

test('un pezzo con indice fuori dal totale dichiarato viene ignorato', async () => {
  let size = 0;
  const gowa = {
    sendVideo: async (phone, caption, buffer) => { size = buffer.length; return 'V1'; },
    sendImage: async () => { throw new Error('non e un video'); },
    sendFile: async () => { throw new Error('non e un video'); }
  };
  const bridge = mediaBridge(gowa);
  const bytes = Buffer.from('due pezzi e due soli');
  const base64 = bytes.toString('base64');

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'x1', MediaFileName: 'clip.mp4', MediaMimeType: 'video/mp4', MediaChunkTotal: 2 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'x1', MediaChunkIndex: 900000000, MediaData: base64 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'x1', MediaChunkIndex: 0, MediaData: base64 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'x1', MediaChunkIndex: 1, MediaData: '' });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'x1' });

  assert.strictEqual(size, bytes.length, 'il pezzo fuori intervallo non deve entrare nel file');
});

test('un allegato oltre il tetto si ferma mentre arriva, non alla fine', async () => {
  let sent = 0;
  const gowa = {
    sendVideo: async () => { sent++; return 'V1'; },
    sendImage: async () => { sent++; return 'I1'; },
    sendFile: async () => { sent++; return 'F1'; }
  };
  const handles = [];
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => handles.push(decodeFrame(packet)) });

  // Un pezzo dichiarato enorme: il tetto si supera con la sua lunghezza, senza
  // che il test debba davvero allocare 64 MB.
  const huge = Buffer.alloc(1024).toString('base64');
  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'h1', MediaFileName: 'big.mp4', MediaMimeType: 'video/mp4', MediaChunkTotal: 200000 });
  for (let i = 0; i < 200000 && handles.filter((f) => f.Command === 'error').length === 0; i++) {
    const chunk = { Type: 3, Command: 'media.chunk', MediaTransferId: 'h1', MediaChunkIndex: i, MediaData: huge };
    // il primo giro dichiara un pezzo oltre il tetto: meta' del lavoro lo fa il server
    await bridge.handleControl(chunk);
    if (i === 0) break;
  }

  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'h1' });
  assert.strictEqual(sent, 0);
  assert.ok(handles.some((f) => f.Command === 'error'), 'oltre il tetto deve arrivare un errore');
});
```

- [x] **Step 2: Run them and watch them fail**

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: the first two fail (`sent` is 1 where 0 is expected, and the out-of-range piece is counted), the third fails because nothing checks the size while the pieces arrive. The existing 133 tests still pass.

- [x] **Step 3: Make the transfer know its total, its bounds and its size**

In `WhatsappBridge/server.js`, next to `MAX_MEDIA_BYTES`, add:

```js
  // Quante spedizioni possono essere aperte insieme. L'app ne apre una alla
  // volta; il numero serve a non tenere in memoria i pezzi di un client che
  // apre una spedizione e non la chiude mai (non c'e' nessun media.end che
  // ripulisca, e ogni pezzo resta li').
  const MAX_LIVE_TRANSFERS = 4;
```

Replace `mediaBegin`, `mediaChunk` and `mediaEnd` with:

```js
  function mediaBegin(msg) {
    if (!msg.MediaTransferId) return;

    // Il totale dichiarato e' quello che rende verificabile la fine: senza,
    // un allegato a cui manca un pezzo e' indistinguibile da uno intero.
    const declared = Number(msg.MediaChunkTotal);
    const chunkTotal = Number.isInteger(declared) && declared > 0 ? declared : null;

    // Lo stesso id due volte: il secondo comando riparte da zero invece di
    // sommarsi al primo.
    mediaTransfers.delete(msg.MediaTransferId);

    // Una spedizione mai chiusa non si accumula all'infinito: la piu' vecchia
    // paga per la nuova.
    if (mediaTransfers.size >= MAX_LIVE_TRANSFERS) {
      const oldest = mediaTransfers.keys().next();
      if (!oldest.done) {
        logger('WARN', `too many open attachments: dropping ${oldest.value}`);
        mediaTransfers.delete(oldest.value);
      }
    }

    mediaTransfers.set(msg.MediaTransferId, {
      chatId: msg.ChatId,
      messageId: msg.RelatedMessageId || null,
      fileName: msg.MediaFileName || null,
      mimeType: msg.MediaMimeType || null,
      chunkTotal,
      bytes: 0,
      parts: []
    });
  }

  function mediaChunk(msg) {
    const transfer = mediaTransfers.get(msg.MediaTransferId);
    if (!transfer) return;

    const index = Number(msg.MediaChunkIndex);
    if (!Number.isInteger(index) || index < 0) return;

    // Fuori dall'intervallo dichiarato non e' un pezzo di questo file: usarlo
    // come indice di un array voleva dire un array con due miliardi di buchi.
    if (transfer.chunkTotal !== null && index >= transfer.chunkTotal) {
      logger('WARN', `attachment piece ${index} is outside 0..${transfer.chunkTotal - 1}, ignored`);
      return;
    }

    // Ogni pezzo e' un multiplo di 4 caratteri base64: decodificarlo da solo e
    // concatenare i byte da' esattamente il file intero.
    const part = Buffer.from(msg.MediaData || '', 'base64');

    // Il tetto si controlla mentre i byte arrivano, non dopo averli tenuti
    // tutti in memoria.
    if (transfer.bytes + part.length > MAX_MEDIA_BYTES) {
      mediaTransfers.delete(msg.MediaTransferId);
      logger('WARN', `attachment over ${MAX_MEDIA_BYTES} bytes, refused while arriving`);
      sendControl({
        command: 'error',
        chatId: transfer.chatId,
        relatedMessageId: transfer.messageId || undefined,
        text: 'The file is too large to send.'
      });
      return;
    }

    transfer.bytes += part.length;
    transfer.parts[index] = part;
  }

  async function mediaEnd(msg) {
    const transfer = mediaTransfers.get(msg.MediaTransferId);
    if (!transfer) return;
    mediaTransfers.delete(msg.MediaTransferId);

    const parts = transfer.parts.filter((part) => part);
    if (parts.length === 0) return;

    // Un pezzo mancante e' un guasto, non un file piu' corto: mandare meta'
    // video senza dirlo e' peggio che non mandarlo.
    if (transfer.chunkTotal !== null && parts.length !== transfer.chunkTotal) {
      logger('WARN', `attachment incomplete: ${parts.length} of ${transfer.chunkTotal} pieces`);
      sendControl({
        command: 'error',
        chatId: transfer.chatId,
        relatedMessageId: transfer.messageId || undefined,
        text: 'The attachment did not arrive complete: send it again.'
      });
      return;
    }

    const buffer = Buffer.concat(parts);

    if (buffer.length > MAX_MEDIA_BYTES) {
      logger('WARN', `attachment too large (${buffer.length} bytes), refused`);
      sendControl({ command: 'error', chatId: transfer.chatId, text: 'The file is too large to send.' });
      return;
    }

    if (state.status !== 'connected') {
      // Come un messaggio di testo: si tiene da parte e parte alla connessione.
      pendingOutgoing.push({
        ChatId: transfer.chatId,
        Text: msg.Text || '',
        MediaData: buffer.toString('base64'),
        MediaMimeType: transfer.mimeType,
        MediaFileName: transfer.fileName
      });
      logger('INFO', 'WhatsApp not ready: attachment queued');
      return;
    }

    try {
      logger('MSG', `attachment to ${transfer.chatId}: ${buffer.length} bytes (${mediaKindOf(transfer.mimeType, transfer.fileName)})`);
      await sendMediaToGowa(transfer.chatId, msg.Text, buffer, transfer.mimeType, transfer.fileName);
    } catch (err) {
      logger('ERR', `attachment to ${transfer.chatId} failed: ${err.message}`);
      sendControl({ command: 'error', chatId: transfer.chatId, text: `Send failed: ${err.message}` });
    }
  }
```

- [x] **Step 4: Run the tests again**

Run: `cd WhatsappBridge && npm test`
Expected: `pass 136, fail 0` (133 existing + 3 new). If one of the three fails, the failure is in the fix, not in the test: re-read the piece-order expectations above.

Hmm, careful: the third test's loop is written to bail out after one piece of 1024 bytes; the size guard only fires when the accumulated bytes pass 64 MiB, which one 1 KiB piece never does. **Rewrite that test at execution time** around the real guard: send pieces until `handles` has an `error` or 64 MiB is reached is far too slow. Instead drive it with `MAX_MEDIA_BYTES` reached by one piece *if the guard is on bytes*, which needs a big buffer - so keep it honest and small by asserting the guard on the declared total instead: 200000 pieces of 3 bytes each would take too long. The executable version of this test is:

```js
test('un allegato oltre il tetto si ferma mentre arriva, non alla fine', async () => {
  let sent = 0;
  const gowa = {
    sendVideo: async () => { sent++; return 'V1'; },
    sendImage: async () => { sent++; return 'I1'; },
    sendFile: async () => { sent++; return 'F1'; }
  };
  const handles = [];
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => handles.push(decodeFrame(packet)) });

  // Il tetto e' 64 MiB: si supera con un singolo pezzo dichiarato da 64 MiB + 1
  // byte, che e' esattamente il caso che il guard deve fermare prima di
  // Buffer.concat.
  const overTheTop = 64 * 1024 * 1024 + 1;
  const piece = Buffer.alloc(1024 * 1024, 7);
  const base64 = piece.toString('base64');
  const repeats = Math.ceil((overTheTop / piece.length) / 4) * 4;   // multiplo di 4, sopra il tetto

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'h1', MediaFileName: 'big.mp4', MediaMimeType: 'video/mp4', MediaChunkTotal: repeats });
  for (let i = 0; i < repeats; i++) {
    await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'h1', MediaChunkIndex: i, MediaData: base64 });
    if (handles.some((f) => f.Command === 'error')) break;
  }
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'h1' });

  assert.strictEqual(sent, 0);
  assert.ok(handles.some((f) => f.Command === 'error'), 'oltre il tetto deve arrivare un errore');
});
```

- [x] **Step 5: Document the guarantee in both READMEs**

In `WhatsappBridge/README.md`, in the section that describes the app -> adapter attachment frames (the one holding the `media.begin` / `media.chunk` / `media.end` rows), add a paragraph:

```markdown
An attachment is sent to WhatsApp only when every piece it announced has
arrived: `media.begin` carries `MediaChunkTotal`, and a transfer with a piece
missing is refused with an `error` frame instead of being sent short. A single
piece may be at most 8 MiB (the frame ceiling); a whole attachment stops at
64 MiB while it is still arriving.
```

In `WhatsappBridge/README.it.md`, the same paragraph in the same position:

```markdown
Un allegato va a WhatsApp solo quando sono arrivati tutti i pezzi che aveva
annunciato: `media.begin` porta `MediaChunkTotal`, e una spedizione a cui manca
un pezzo viene rifiutata con un frame `error` invece di partire piu' corta. Un
pezzo singolo puo' arrivare a 8 MiB (il tetto del frame); un allegato intero si
ferma a 64 MiB mentre sta ancora arrivando.
```

- [x] **Step 6: Run the whole gate**

Run the fast gate of the Global Constraints.
Expected: every line `OK: ...`; `check-docs.js` reports the two doc pairs still in step; the test counts line in `test-the-app` now says `136 adapter tests` (update it in the same commit).

- [x] **Step 7: Commit and push the app repository**

```bash
git add WhatsappBridge/server.js WhatsappBridge/test/server.test.js WhatsappBridge/README.md WhatsappBridge/README.it.md .agents/skills/test-the-app/SKILL.md
git commit -m "$(cat <<'EOF'
fix: refuse an attachment that did not arrive whole

media.begin threw MediaChunkTotal away, so media.end could not tell a complete
attachment from one missing a piece: it concatenated what it had and sent it,
and the other side got half a video with no error anywhere. The piece index was
also used as an array index without a bound, the 64 MiB ceiling was checked only
after buffering everything, and a transfer whose end never came was never
removed.

The total is kept, a piece outside it is ignored, the ceiling is enforced while
the pieces arrive and at most four transfers can be open at once.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
git push origin master
git rev-parse --short HEAD
```

Note the hash it prints: the mirror's `SOURCE_COMMIT` is that commit.

- [x] **Step 8: Mirror it into the Docker repository**

```bash
cd /tmp/docker-whatsappforwp
node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP
node tools/sync.js --check
cd server && npm test
```
Expected: `OK: server/ matches the adapter (22 file(s))` from `--check`, and the mirror's suite at `pass 136, fail 0`.

Then set `server/SOURCE_COMMIT` to the hash from Step 7 (the mirror keeps that file by hand), and:

```bash
cd /tmp/docker-whatsappforwp
git add -A server/SOURCE_COMMIT server
git commit -m "$(cat <<'EOF'
sync: refuse an attachment that did not arrive whole

Picks up the adapter's media transfer validation: the declared piece total is
kept, a piece outside it is ignored, the ceiling is enforced while the pieces
arrive, and at most four transfers are open at once.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
git push origin main
```

---

### Task 5: the .NET server stops trusting the length it is given

**Files:**
- Modify: `WhatsappServer/Program.cs` (`MainAsync`'s accept loop, `HandleClientAsync`, `BroadcastMessageAsync`)

**Interfaces:**
- Consumes: nothing.
- Produces: unchanged console behaviour; a client that announces a length outside `1..8 MiB` is disconnected and reported instead of being believed.

- [x] **Step 1: Add the ceiling**

In `WhatsappServer/Program.cs`, inside `class Program`, after `private static bool _isRunning = true;`, add:

```csharp
        /// <summary>
        /// Il tetto di un frame. E' lo stesso numero di CommunicationService e
        /// di server.js: il prefisso di 4 byte e' l'unica cosa che l'altro capo
        /// controlla, e una lunghezza creduta e' una dimensione di allocazione.
        /// Senza questo un client che annuncia 2 GB fa allocare 2 GB a questo
        /// processo, e un client disallineato lo fa cadere dove vuole.
        /// </summary>
        private const int MaxFrameLength = 8 * 1024 * 1024;
```

- [x] **Step 2: Reject a length that cannot be a payload**

In `HandleClientAsync`, replace

```csharp
                    int messageLength = BitConverter.ToInt32(lengthBytes, 0);

                    // Read message content
                    var messageData = new byte[messageLength];
```

with

```csharp
                    int messageLength = BitConverter.ToInt32(lengthBytes, 0);

                    // Un frame vuoto, negativo o sopra il tetto non e' un
                    // payload: e' un disallineamento o un client che chiede
                    // memoria. Si chiude questa connessione invece di credergli.
                    if (messageLength < 1 || messageLength > MaxFrameLength)
                    {
                        Console.WriteLine("Client sent an unusable frame length (" +
                            messageLength + "), connection dropped: " + Describe(client.Client.RemoteEndPoint as IPEndPoint));
                        break;
                    }

                    // Read message content
                    var messageData = new byte[messageLength];
```

- [x] **Step 3: Guard the client list**

`_clients` is a `List<TcpClient>` touched by the accept loop and by every client task. Replace the three places that touch it:

```csharp
        private static readonly object _clientsGate = new object();

        private static void AddClient(TcpClient client)
        {
            lock (_clientsGate) { _clients.Add(client); }
        }

        private static void RemoveClient(TcpClient client)
        {
            lock (_clientsGate) { _clients.Remove(client); }
        }

        private static List<TcpClient> SnapshotClients()
        {
            lock (_clientsGate) { return new List<TcpClient>(_clients); }
        }

        private static int ClientCount()
        {
            lock (_clientsGate) { return _clients.Count; }
        }
```

In `MainAsync`: `_clients.Add(client);` becomes `AddClient(client);`.
In `HandleClientAsync`'s `finally`: `_clients.Remove(client);` becomes `RemoveClient(client);` and the message uses `ClientCount()`.
In `BroadcastMessageAsync`: `foreach (var client in _clients)` becomes `foreach (var client in SnapshotClients())` and the two `_clients.Remove(dead)` become `RemoveClient(dead)`.

- [x] **Step 4: Build it**

Run the three build-gate commands.
Expected: `COPIA=0`, `Avvisi: 0`, `Errori: 0`, `Your package has been successfully created.` (`WhatsappServer.exe` is built by the same solution.)

- [x] **Step 5: Commit and push**

```bash
git add WhatsappServer/Program.cs
git commit -m "$(cat <<'EOF'
fix: the console server stops trusting the frame length

A 4-byte prefix read from an unauthenticated peer was used as an array size, so
a client announcing 2 GB made the process allocate 2 GB, and a negative length
was an OverflowException per connection. The length is now checked against the
same 8 MiB ceiling the app and the adapter use.

The client list is also a List touched by the accept loop and by every client
task, so it now has a lock and the broadcast walks a snapshot.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
git push origin master
```

---

## Self-review

**Coverage of this sweep.** The files read line by line for this plan: `CryptoHelper.cs` with `crypto-helper.js` and its vector test, `CommunicationService.cs` (all 962 lines), `IncomingMediaStore.cs`, `ChatPage.xaml.cs`'s send path and input row, `ChatPreferences.cs`, `SettingsService.cs`, `MessageCache.cs`, `ChatCache.cs`, `DataService.cs`'s receive path, `WhatsappServer/Program.cs`, `server.js`'s media paths, `message-format.js`'s date handling, and the three title-bar guards. Found and fixed: the five above. Found and **deliberately not** fixed, with the reason: `CommunicationService`'s server mode (`OnServerConnectionReceived`, `_serverMode`, `BroadcastToAllClientsAsync`'s caller) is unreachable - `_serverListener` is never created, so `_isServerMode = true` is never executed; deleting it is a separate decision, not a bug fix, and Task 1 keeps its broadcast correct in case it comes back.

**Not yet examined, so this plan does not claim them clean:** `DataService.cs` after line 240 (the control-frame switch, delete, unread, pin sort), `ChatsPage.xaml.cs` (535 lines), `ConnectionPage.xaml.cs` (568), `App.xaml.cs`, `ChatMessage.cs` (687), `Contact.cs`, `NotificationService.cs`, `ConnectionWatchdog.cs`, `DiscoveryService.cs`, `SelfCheck.cs`, `MemoryWatcher.cs`, `AttachmentInbox.cs`, `ImageHelper.cs`, `AutoConnector.cs`, `SectionNav.xaml.cs`, the converters, and on the adapter side `gowa-client.js`, `chats.js`, `calls.js`, `config.js`, `discovery.js`, `ffmpeg.js`, `webhook-server.js` and `tools/*.js`. A second sweep is a separate piece of work, and the honest thing is to say so rather than to call the tree audited.

**Placeholder scan.** Every step carries the code, the command and the expected output. The one place where a plan-time draft was replaced by an executable version is Task 4's third test, and the replacement is printed in full underneath it rather than left to the reader.

**Type consistency.** `SerialQueue.RunAsync<T>(Func<Task<T>>)` returns `Task<T>` and is what `IncomingMediaStore.AddChunkAsync` returns; the non-generic overload returns `Task` and is what `CommunicationService.SendFrameAsync` and `ChatPreferences.Save` need. `AddChunkCoreAsync` is `private static async Task<IncomingMediaResult>` and keeps the parameter list of the public method. `WriteFrameAsync` is `private static async Task` and takes `(DataWriter, byte[])` - the same pair `SendFrameAsync` receives. On the adapter side `mediaTransfers` entries gain `messageId`, `chunkTotal` and `bytes`, and all three are read in `mediaChunk`/`mediaEnd` under the same names.

**The risk this plan accepts.** A queued write that never completes (a half-open socket whose `StoreAsync` hangs) now holds the queue behind it. That was also true before for the socket itself - the write hung either way - but with the queue it would hold *later* sends too. The `ConnectionWatchdog` notices the silence, tears the connection down and reconnects, which disposes the writer and drains the queue; the on-device check 53 is the observation for it.

## Execution Handoff

Repo rule: **inline execution in this same session, task by task, to the final push, plus the Docker mirror for Task 4.** The plan is executed in order, each task ending with its commit; the VM build runs after the three app tasks together and again after the server task, since the compiler names the file that failed either way.

## What execution changed about this plan

Executed on 2026-09-28, in this order: `4343139` (Task 1), `a8d5921` (Task 2), `d1eb28a` (Task 3), `d737289` (Task 4, adapter + README pair + counts, pushed), mirror `eee5e9c` (pushed to `main`), `3942e1d` (Task 5, pushed).

**The builds came out clean, three runs instead of five.** Task 1 had its own; Tasks 2 and 3 shared one (both are C#-only and the compiler names the file that failed, so a shared run loses nothing but a minute); Task 5 had its own because it changes the other project in the solution. All three ended `Your package has been successfully created.`, `Avvisi: 0`, `Errori: 0`, and the last one's log shows `Csc.exe ... /warn:4 ... /out:obj\Debug\WhatsappServer.exe` with no warning, so the server really was recompiled.

**The counts that came out:** 37 C# files (was 36), 136 adapter tests (was 133, as planned), 47 tests in `tools/test` (unchanged), 39 `.md` files (the plan is one of them).

**The out-of-range piece index is worse than the plan said.** Its test failed before the fix in **24.2 seconds** - `filter` walking an array with 900 million holes. The plan called it a memory problem; it is a CPU problem too, and the 24 seconds are in this session's log. That test is also the one that proves the fix: after it, the piece is dropped before it ever reaches the array, and the test finishes in milliseconds.

**Four things the plan got wrong, all fixed in place:**

1. Task 4's third test, as drafted, passed *before* the fix: it asserted an `error` frame after `media.end`, which the old end-of-transfer size check also produced. The executable version asserts the error is already there **before** `media.end` is sent, which is what "stops while it is still arriving" actually means, and that version fails without the early guard.
2. `node tools/sync.js --check` needs `--from` as well: the plan printed it without, and the run answered `usage: node tools/sync.js [--check] --from <WhatsappForWP>`.
3. The mirror's `server/SOURCE_COMMIT` is **written by `sync.js`**, not by hand: after the sync it already held the new main-repo hash. The plan's step to set it manually was unnecessary.
4. `git add -A server` was replaced by explicit paths (the project's rule), and the mirror's commit carries only `server/SOURCE_COMMIT`, `server/server.js` and `server/test/server.test.js`.

**What was not verified, and cannot be from here.** None of the three races can be reproduced on this machine: there is no WP8.1 renderer and no C# test project, so Tasks 1-3 are verified by the guards, by the build and by the reading in the plan; the observations that would catch a regression are on-device checks 53, 54 and 55, added in Tasks 1-3. Task 4 is the one with real tests (133 -> 136, all green, and the failing run is recorded above).
