# QR, Connection Timeout and Frame Limits Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the phone stay connected to the adapter and actually receive (and re-request) the login QR code, instead of dying with a frame-read OutOfMemoryException and a WinSock timeout.

**Architecture:** The failures are in the socket layer of `WhatsappApp/Services/CommunicationService.cs`, not in the crypto or the login flow. Three defects compound: (1) `ReadFrameAsync` trusts a 4-byte length it may have read out of alignment and allocates it blind; (2) `_clientSocket` / `_reader` / `_writer` are single shared fields, so a second (or failed) connect attempt disposes or shares the objects of the live connection, and two `ListenForMessagesAsync` loops read the same `DataReader`; (3) a saved server address that no longer answers turns into a raw WinSock timeout with no deadline and no fallback. The fix is to make the frame reader bounded and partial-safe, make every connection attempt own its own socket/reader/writer (only the newest attempt publishes and keeps its listener), and give the connect a 6-second deadline with a fallback to network discovery. The adapter gets a matching frame-length cap so it can never be the source of an absurd length.

**Tech Stack:** C# 5 on Windows Phone 8.1 (`Windows.Networking.Sockets.StreamSocket`, `Windows.Storage.Streams.DataReader`), Node.js adapter (`WhatsappBridge`, zero runtime dependencies, `node:test`), `.resw` localization (en-US + it-IT).

## Global Constraints

- **C# 5 only.** No `$"..."`, `?.`, expression-bodied members, `out var`, `is T x`, `nameof`, `_ =`, async entry points. Use `delegate { }` and named methods. Trailing `;` after the last enum member is fine.
- **There is no C# test host in this repo.** A C# change is verified by: the static guards, the WP8.1 build gate on the Parallels VM, and the `DIAG` lines the app prints on the device. Every C# task below states the exact guard command *and* the exact `DIAG` line/absence it expects. Where a real test host exists (the Node adapter) the task is real TDD.
- **Never a silent `catch`.** Every survived failure goes through `Diag.Failed("<call site>", ex)` before it is handled.
- **No hardcoded user-visible strings.** New text goes in both `WhatsappApp/Strings/en-US/Resources.resw` and `WhatsappApp/Strings/it-IT/Resources.resw` with the **same key**, and is read with `Loc.Get("Key", "fallback")`.
- **No emoji in any `.md`** (U+26A0 is the only allowed exception). No emoji in this plan either.
- **Docs are written in pairs.** `README.md` / `README.it.md` and `WhatsappBridge/README.md` / `README.it.md`: same heading depth and order, every change in both, in the same commit.
- **The adapter has zero runtime dependencies.** Do not add one.
- **Frame invariant (both sides):** one frame is `[4-byte little-endian UInt32 payload length][payload]`. `payload` is `[1-byte cipher tag][16-byte IV][AES-256-CBC ciphertext][32-byte HMAC-SHA256]` for tag 2. The maximum accepted payload length is **8 MiB (`8388608`)** on both sides.
- **Guard commands (all must pass before each commit):**
  - `node tools/check-csharp5.js`
  - `node tools/check-icons.js`
  - `node tools/check-resw.js --strict`
  - `node tools/check-docs.js`
  - `xmllint --noout WhatsappApp/Pages/*.xaml WhatsappApp/App.xaml WhatsappApp/MainPage.xaml`
  - `cd WhatsappBridge && npm test`
  - `node --test "tools/test/**/*.test.js"` (a bare directory argument fails on Node 26)
- **The real build gate (run by the user, on the Windows machine, once, in the final task):**
  - `prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"`
  - `prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"` -> `COPIA=0`
  - `prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m"` -> `Errori: 0, Avvisi: 2` (the two known: CS0618 `PickSingleFileAsync`, CS4014).
- **Commits:** English, `type: short imperative`, one commit per task, then `git push origin master`.

---

## The evidence this plan is built on

Device log from the last run (abridged to the new lines):

```
DIAG ok: crypto AES-256-CBC + HMAC-SHA256
DIAG ok: schermo sempre acceso (DisplayRequest)
DIAG ok: beacon UDP in ascolto sulla porta 8587
DIAG ConnectToServerAsync: Exception 0x8007274C  A connection attempt failed because the connected party did not properly respond after a period of time...
DIAG ListenForMessagesAsync: OutOfMemoryException 0x8007000E  Insufficient memory to continue the execution of the program.
```

What each line means, and which task fixes it:

| Evidence | Diagnosis | Task |
| --- | --- | --- |
| `DIAG ok: crypto ...`, `DIAG ok: beacon ...` | The crypto and discovery work shipped by the previous plan are verified on the device. Do not touch `CryptoHelper` or `DiscoveryService`. | - |
| `ConnectToServerAsync 0x8007274C` | `0x8007274C` is `WSAETIMEDOUT`: `StreamSocket.ConnectAsync` timed out. The app never reached the handshake, so it never had a QR to show. `StreamSocket` has no timeout parameter and no cancellation token, so the app waits for the OS stack (tens of seconds). Most likely cause: `App.StartAutoConnect` tries the *saved* address first (`AutoConnector.TryConnectAsync`) and that address is stale. | Tasks 3, 5 |
| `ListenForMessagesAsync OutOfMemoryException 0x8007000E` | The read loop blew up. `ReadFrameAsync` reads a 4-byte length and then does `await reader.LoadAsync(payloadLength)` with no upper bound; a length read out of alignment (a slice of JSON) is a multi-gigabyte number. And the reader *can* be misaligned, because `_clientSocket`/`_reader`/`_writer` are single shared fields: the app auto-connects at startup (`App.StartAutoConnect`) while `ConnectionPage` connects too, so a failed attempt's `CleanUpClientSocket()` disposes the live connection's `DataReader` and two `ListenForMessagesAsync` loops read the same one. | Tasks 1, 2 |
| No QR shown although the user saw a connection | The QR frame is a control frame delivered by the very read loop that died (`qr` -> `ConnectionPage.ShowQrCode`). Fixing the loop restores it; Task 4 covers the remaining case (connection established while the connection page was not open). | Tasks 1, 2, 4 |

## File structure

| File | Responsibility | Change |
| --- | --- | --- |
| `WhatsappApp/Services/CommunicationService.cs` | the encrypted TCP client, the frame reader/writer, control frames | Task 1: bounded, partial-safe `ReadFrameAsync`. Task 2: connection-scoped socket/reader/writer + supersede id. Task 3: connect deadline, address validation, friendly failure text |
| `WhatsappApp/Strings/en-US/Resources.resw`, `.../it-IT/Resources.resw` | every user-visible string | Task 3: 4 new keys |
| `WhatsappApp/Pages/ConnectionPage.xaml.cs` | the settings/QR page | Task 4: ask for the QR whenever the page opens on a live connection |
| `WhatsappApp/Services/AutoConnector.cs` | pick the adapter without the user typing anything | Task 5: a saved address that does not answer falls back to discovery |
| `WhatsappBridge/server.js` | GOWA HTTP + webhook -> encrypted TCP frames | Task 6: refuse a frame larger than the limit; export the limit |
| `WhatsappBridge/test/frame-limit.test.js` | adapter tests | Task 6: new (TDD) |
| `README.md`, `README.it.md`, `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`, `.agents/skills/maintain-the-app/SKILL.md`, `.agents/skills/test-the-app/SKILL.md` | docs and rules | Task 7 |

---

### Task 1: A bounded, partial-safe frame reader

**Files:**
- Modify: `WhatsappApp/Services/CommunicationService.cs` (usings; `ReadFrameAsync` at the end of the class)

**Interfaces:**
- Consumes: nothing new.
- Produces: `private const uint MaxFrameLength = 8 * 1024 * 1024;` and `private static async Task<bool> LoadAtLeastAsync(DataReader reader, uint count)` — Task 6 uses the same numeric limit on the adapter side. `ReadFrameAsync(DataReader reader) : Task<byte[]>` keeps its signature and its contract: a whole frame, or `null` when the connection is closed / the frame is not acceptable.

Why: with `InputStreamOptions.Partial`, `LoadAsync(4)` may return 2 bytes. The current code then returns `null` and kills the connection on a frame that was merely split across two TCP segments. And when a length *is* read it is used unbounded as an allocation size, which is where `OutOfMemoryException 0x8007000E` comes from.

- [ ] **Step 1: Add the two usings**

In `WhatsappApp/Services/CommunicationService.cs`, the using block currently starts with `using System;` and ends with `using WhatsappApp.Models;`. Add `using System.IO;` (for `InvalidDataException`) after `using System.Collections.Generic;`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
```

- [ ] **Step 2: Add the limit next to the other constants**

Immediately after the `_isConnected` field declaration:

```csharp
        private bool _isConnected = false;

        /// <summary>
        /// Frame piu' grande che accettiamo. Il prefisso di 4 byte e' l'unica
        /// cosa che l'altro capo controlla: se il lettore e' disallineato quella
        /// lunghezza e' un pezzo di JSON, cioe' un numero enorme. Senza un
        /// limite LoadAsync lo usava come dimensione e l'app finiva in
        /// OutOfMemoryException (0x8007000E) invece di chiudere la connessione.
        /// Otto mebibyte lasciano passare un'immagine in base64 e restano
        /// lontani dalla memoria di un telefono WP8.1.
        /// </summary>
        private const uint MaxFrameLength = 8 * 1024 * 1024;
```

- [ ] **Step 3: Replace `ReadFrameAsync`**

Replace the whole existing `ReadFrameAsync` method (the one that starts with `/// Reads one complete frame: [4-byte length][payload].` and ends with the closing brace before `DecryptToMessage`) with these two methods:

```csharp
        /// <summary>
        /// Reads one complete frame: [4-byte length][payload].
        /// Returns null when the connection is closed or the frame is not
        /// acceptable (a length outside 1..MaxFrameLength is a fault, not a
        /// payload: it is recorded and the connection is dropped).
        /// </summary>
        private async Task<byte[]> ReadFrameAsync(DataReader reader)
        {
            if (!await LoadAtLeastAsync(reader, 4)) return null;

            uint payloadLength = reader.ReadUInt32();

            if (payloadLength == 0 || payloadLength > MaxFrameLength)
            {
                Diag.Failed("ReadFrameAsync/length",
                    new InvalidDataException("lunghezza frame fuori intervallo: " + payloadLength));
                return null;
            }

            if (!await LoadAtLeastAsync(reader, payloadLength)) return null;

            byte[] payload = new byte[payloadLength];
            reader.ReadBytes(payload);
            return payload;
        }

        /// <summary>
        /// Riempie il buffer del reader finche' non ha almeno <paramref name="count"/>
        /// byte non consumati. Con InputStreamOptions.Partial una LoadAsync puo'
        /// restituirne meno del richiesto: il prefisso di lunghezza letto con una
        /// sola LoadAsync(4) veniva spezzato a meta' frame e la connessione
        /// cadeva su un frame che era solo arrivato in due pezzi.
        /// </summary>
        private static async Task<bool> LoadAtLeastAsync(DataReader reader, uint count)
        {
            while (reader.UnconsumedBufferLength < count)
            {
                uint loaded = await reader.LoadAsync(count - reader.UnconsumedBufferLength);
                if (loaded == 0) return false;   // flusso chiuso dall'altro capo
            }
            return true;
        }
```

- [ ] **Step 4: Run the guard**

```bash
node tools/check-csharp5.js
```

Expected: `OK: 27 C# file(s)` (no C# 6/7 syntax, no missing WP8.1 API).

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Services/CommunicationService.cs
git commit -m "fix: bound the frame reader and read the length prefix fully"
```

---

### Task 2: One connection owns one socket, one reader, one listener

**Files:**
- Modify: `WhatsappApp/Services/CommunicationService.cs` (`ConnectToServerAsync`, `ListenForMessagesAsync`, `CleanUpClientSocket`, `Disconnect`, and the new `DisposePublishedSocket`)

**Interfaces:**
- Consumes: `MaxFrameLength` and `LoadAtLeastAsync` from Task 1.
- Produces: `private int _connectionId`; `private void DisposePublishedSocket()`; `private static void DisposeSocket(StreamSocket socket, DataWriter writer, DataReader reader)`; `private async Task ListenForMessagesAsync(int attempt, DataReader reader)` (signature changed — Task 3 does not touch it, Task 4/5 do not call it).

Why: the two failures in the device log are two symptoms of the same defect. `App.StartAutoConnect` and `ConnectionPage` both call `ConnectToServerAsync`; because `_clientSocket` / `_reader` / `_writer` are single fields, the losing attempt's cleanup disposes the winning connection's `DataReader`, and both attempts can leave a `ListenForMessagesAsync` loop reading the same reader. An attempt now owns its objects in locals, publishes them only if it is still the newest attempt, and its listener runs only while its id is still current.

- [ ] **Step 1: Add the attempt counter and the two disposal helpers**

Directly after the `MaxFrameLength` constant added in Task 1:

```csharp
        /// <summary>
        /// Numero del tentativo di connessione. Ogni tentativo lo incrementa e
        /// pubblica i propri oggetti solo se e' ancora quello piu' recente; il
        /// suo lettore continua solo finche' l'id resta quello. Senza questo,
        /// l'avvio automatico e la pagina si contendevano `_reader`: un
        /// tentativo fallito chiudeva il DataReader della connessione riuscita e
        /// due lettori sullo stesso DataReader lo disallineavano, il che e' la
        /// strada da cui e' arrivato OutOfMemoryException.
        /// </summary>
        private int _connectionId;
```

- [ ] **Step 2: Give the attempt its own objects**

Replace the whole body of `ConnectToServerAsync` — from `public async Task<bool> ConnectToServerAsync(string address, int port, string username)` through its closing brace — with this version:

```csharp
        public async Task<bool> ConnectToServerAsync(string address, int port, string username)
        {
            int attempt = ++_connectionId;

            _isServerMode = false;
            _serverAddress = address;
            _serverPort = port;
            _myUserId = Guid.NewGuid().ToString("N").Substring(0, 8);
            _myUsername = username;

            // Oggetti del tentativo, non del servizio: finche' non e' pubblicata,
            // questa connessione non esiste per nessun altro.
            StreamSocket socket = null;
            DataWriter writer = null;
            DataReader reader = null;

            try
            {
                DispatchOnUiThread(() =>
                    RaiseConnectionStatusChanged(Loc.Get("CommService_Connecting", "Connecting..."))
                );

                socket = new StreamSocket();
                var hostName = new HostName(address);
                await socket.ConnectAsync(hostName, port.ToString());

                writer = new DataWriter(socket.OutputStream);
                reader = new DataReader(socket.InputStream);
                reader.InputStreamOptions = InputStreamOptions.Partial;

                // Un tentativo piu' nuovo ha gia' preso il posto di questo:
                // si chiude quello che abbiamo aperto e non si tocca niente di
                // condiviso (era il modo in cui un timeout cancellava la
                // connessione riuscita dell'altro tentativo).
                if (attempt != _connectionId)
                {
                    DisposeSocket(socket, writer, reader);
                    return false;
                }

                DisposePublishedSocket();
                _clientSocket = socket;
                _writer = writer;
                _reader = reader;
                _isConnected = true;

                // Send handshake with our identity (encrypted)
                var handshake = new ChatMessage
                {
                    Id = "handshake",
                    Text = username,
                    Command = "hello",
                    SenderId = _myUserId,
                    SenderName = username,
                    ChatId = "system",
                    Timestamp = DateTime.Now,
                    Type = MessageType.System,
                    IsIncoming = false
                };
                // Il primo frame e' anche il primo uso del cifrario: se il
                // cifrario non c'e' l'errore va detto qui, invece di uscire
                // come "operazione non implementata" senza dire quale passo.
                try
                {
                    await SendFrameAsync(writer, Encoding.UTF8.GetBytes(handshake.ToJson()));
                }
                catch (Exception ex)
                {
                    Diag.Failed("ConnectToServerAsync/handshake", ex);
                    if (attempt == _connectionId)
                    {
                        _isConnected = false;
                        DisposePublishedSocket();
                        DispatchOnUiThread(() =>
                            RaiseErrorOccurred(string.Format(
                                Loc.Get("CommService_ConnectError", "Connection error: {0}"),
                                ExplainConnectionFailure(ex, "handshake"))));
                    }
                    else
                    {
                        DisposeSocket(socket, writer, reader);
                    }
                    return false;
                }

                DispatchOnUiThread(() =>
                {
                    RaiseConnectionStatusChanged(Loc.Get("CommService_Connected", "Connected to the server"));
                    RaiseConnectionEstablished();
                });

                // Il lettore porta con se' l'id del tentativo e il suo reader:
                // niente campi condivisi, niente secondo lettore sullo stesso
                // DataReader.
#pragma warning disable 4014
                Task.Run(() => ListenForMessagesAsync(attempt, reader));
#pragma warning restore 4014

                return true;
            }
            catch (Exception ex)
            {
                Diag.Failed("ConnectToServerAsync", ex);

                // Solo il tentativo ancora valido puo' dichiarare il guasto: se
                // nel frattempo ne e' partito uno piu' nuovo, questo e' rumore e
                // i suoi oggetti si chiudono senza toccare la connessione
                // vincente.
                DisposeSocket(socket, writer, reader);
                if (attempt == _connectionId)
                {
                    _isConnected = false;
                    DisposePublishedSocket();
                    DispatchOnUiThread(() =>
                        RaiseErrorOccurred(string.Format(
                            Loc.Get("CommService_ConnectError", "Connection error: {0}"),
                            ExplainConnectionFailure(ex, "socket"))));
                }
                return false;
            }
        }
```

Note: the `ExplainConnectionFailure(ex, "socket")` call keeps its current two-argument shape in this task. Task 3 adds the endpoint argument.

- [ ] **Step 3: Replace `CleanUpClientSocket` with the two helpers**

Replace the existing `CleanUpClientSocket` method with:

```csharp
        /// <summary>
        /// Chiude la connessione pubblicata, qualunque essa sia, senza toccare
        /// l'id dei tentativi. Idempotente: la chiamano il ramo di fallimento
        /// del handshake, il catch esterno e il lettore che finisce.
        /// </summary>
        private void DisposePublishedSocket()
        {
            DataWriter writer = _writer;
            DataReader reader = _reader;
            StreamSocket socket = _clientSocket;

            _writer = null;
            _reader = null;
            _clientSocket = null;

            DisposeSocket(socket, writer, reader);
        }

        /// <summary>
        /// Chiude gli oggetti di un tentativo. Non tocca i campi: non sa se
        /// quella connessione e' mai stata pubblicata, ed e' esattamente il
        /// motivo per cui esiste.
        /// </summary>
        private static void DisposeSocket(StreamSocket socket, DataWriter writer, DataReader reader)
        {
            try { if (writer != null) writer.Dispose(); }
            catch (Exception ex) { Diag.Failed("DisposeSocket/writer", ex); }
            try { if (reader != null) reader.Dispose(); }
            catch (Exception ex) { Diag.Failed("DisposeSocket/reader", ex); }
            try { if (socket != null) socket.Dispose(); }
            catch (Exception ex) { Diag.Failed("DisposeSocket/socket", ex); }
        }

        /// <summary>
        /// Invalida i lettori in corso e chiude la connessione pubblicata.
        /// L'incremento dell'id e' la parte che conta: e' quello che fa uscire
        /// un eventuale ListenForMessagesAsync ancora in esecuzione.
        /// </summary>
        private void CleanUpClientSocket()
        {
            _connectionId++;
            DisposePublishedSocket();
        }
```

- [ ] **Step 4: Make the listener belong to one attempt**

Replace `private async Task ListenForMessagesAsync()` entirely with:

```csharp
        /// <summary>
        /// Legge i frame della connessione <paramref name="attempt"/> finche' e'
        /// quella pubblicata. Il reader arriva come parametro: prenderlo da
        /// `_reader` significava leggere il reader di un'altra connessione non
        /// appena ne partiva una nuova.
        /// </summary>
        private async Task ListenForMessagesAsync(int attempt, DataReader reader)
        {
            try
            {
                while (_isConnected && attempt == _connectionId)
                {
                    byte[] payload = await ReadFrameAsync(reader);
                    if (payload == null) break;

                    DispatchMessage(DecryptToMessage(payload));
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("ListenForMessagesAsync", ex);
                if (_isConnected && attempt == _connectionId)
                {
                    DispatchOnUiThread(() =>
                        RaiseErrorOccurred(string.Format(
                            Loc.Get("CommService_ConnectionLost", "Connection lost: {0}"), ex.Message))
                    );
                }
            }
            finally
            {
                // Un lettore superato non deve dichiarare disconnessa la
                // connessione che l'ha sostituito.
                if (attempt == _connectionId)
                {
                    _isConnected = false;
                    DisposePublishedSocket();
                    DispatchOnUiThread(() =>
                        RaiseConnectionStatusChanged(Loc.Get("CommService_Disconnected", "Disconnected"))
                    );
                }
            }
        }
```

- [ ] **Step 5: Stop the listener on an explicit disconnect**

In `Disconnect()`, make the first statement bump the id:

```csharp
        public void Disconnect()
        {
            // Ferma un lettore ancora in esecuzione prima di chiudere i suoi
            // oggetti: e' quello che distingue una disconnessione voluta da un
            // guasto di rete da segnalare.
            _connectionId++;
            _isConnected = false;
            WhatsAppState = "disconnected";
            AccountJid = "";
```

(the rest of `Disconnect()` is unchanged)

Then, in the same method, replace the `try { if (_writer != null) ... }` block and the following four assignments with a single call, because the disposal and the nulling now live in one place:

```csharp
            try
            {
                if (_serverListener != null) _serverListener.Dispose();
            }
            catch (Exception ex)
            {
                Diag.Failed("Disconnect", ex);
            }

            DisposePublishedSocket();
            _serverListener = null;
```

- [ ] **Step 6: Run the guards**

```bash
node tools/check-csharp5.js
node tools/check-resw.js --strict
```

Expected: `OK: 27 C# file(s)` and `OK: 91 key(s) in en-US and it-IT, every x:Uid and Loc.Get lookup resolved.`

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Services/CommunicationService.cs
git commit -m "fix: give every connection attempt its own socket, reader and listener"
```

---

### Task 3: A connect deadline, address validation, and text for the timeout

**Files:**
- Modify: `WhatsappApp/Services/CommunicationService.cs` (`ConnectToServerAsync`, new `ConnectWithDeadlineAsync`, `ExplainConnectionFailure`, new `Endpoint` / `RaiseConnectError`)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`
- Modify: `WhatsappApp/Strings/it-IT/Resources.resw`

**Interfaces:**
- Consumes: `_connectionId` and `DisposeSocket` from Task 2.
- Produces: `private static string ExplainConnectionFailure(Exception ex, string stage, string endpoint)` (three arguments — Task 4 and Task 5 do not call it), `private static string Endpoint(string address, int port)`, `private void RaiseConnectError(string detail)`, and the four new resource keys.

Why: `0x8007274C` is `WSAETIMEDOUT`, and `StreamSocket.ConnectAsync` has neither a timeout parameter nor a cancellation token, so the app waits for the OS stack. In practice the app also *starts* with the wrong target: `SettingsService.ServerAddress` is tried first (Task 5 changes that), so the very first thing the user sees is a raw WinSock sentence in English about an address they cannot see.

- [ ] **Step 1: Add the deadline and the WinSock codes**

Directly after the `_connectionId` field added in Task 2:

```csharp
        /// <summary>
        /// Deadline della ConnectAsync, in millisecondi. StreamSocket non
        /// accetta un timeout e non ha un CancellationToken: senza un limite il
        /// telefono resta immobile su un indirizzo che non risponde piu' finche'
        /// non si arrende lo stack TCP. Il socket si chiude alla scadenza, che e'
        /// l'unico modo di annullare una connessione ancora in volo.
        /// </summary>
        private const int ConnectDeadlineMs = 6000;

        // Gli errori WinSock arrivano come eccezioni WinRT con FACILITY_WIN32:
        // 0x8007xxxx. WSAETIMEDOUT e' quello visto sul dispositivo.
        private const int WsaETimedOut = unchecked((int)0x8007274C);
        private const int WsaEConnRefused = unchecked((int)0x8007274D);
        private const int WsaENetUnreachable = unchecked((int)0x80072743);
        private const int WsaEHostUnreachable = unchecked((int)0x80072751);
```

- [ ] **Step 2: Validate the address, and connect through the deadline**

In `ConnectToServerAsync`, replace the block

```csharp
                socket = new StreamSocket();
                var hostName = new HostName(address);
                await socket.ConnectAsync(hostName, port.ToString());
```

with

```csharp
                // Prima di aprire un socket: un indirizzo vuoto o una porta
                // fuori intervallo non sono un guasto di rete da spiegare, sono
                // un dato da correggere.
                if (string.IsNullOrEmpty(address) || port < 1 || port > 65535)
                {
                    Diag.Failed("ConnectToServerAsync/address",
                        new ArgumentException("indirizzo o porta non valida: " + Endpoint(address, port)));
                    DispatchOnUiThread(() =>
                        RaiseErrorOccurred(string.Format(
                            Loc.Get("CommService_InvalidAddress",
                                "Enter a valid address (host name or IP, port 1-65535): {0}"),
                            Endpoint(address, port))));
                    return false;
                }

                HostName hostName;
                try
                {
                    hostName = new HostName(address);
                }
                catch (Exception ex)
                {
                    Diag.Failed("ConnectToServerAsync/hostname", ex);
                    DispatchOnUiThread(() =>
                        RaiseErrorOccurred(string.Format(
                            Loc.Get("CommService_InvalidAddress",
                                "Enter a valid address (host name or IP, port 1-65535): {0}"),
                            Endpoint(address, port))));
                    return false;
                }

                socket = new StreamSocket();
                await ConnectWithDeadlineAsync(socket, hostName, port);
```

Also change both `ExplainConnectionFailure(ex, "handshake")` and `ExplainConnectionFailure(ex, "socket")` to pass the endpoint:

```csharp
                                ExplainConnectionFailure(ex, "handshake", Endpoint(address, port))));
```

```csharp
                            ExplainConnectionFailure(ex, "socket", Endpoint(address, port))));
```

- [ ] **Step 3: Add `ConnectWithDeadlineAsync`, `Endpoint` and `ExplainConnectionFailure`**

Add these three members next to `DisposeSocket`:

```csharp
        /// <summary>
        /// ConnectAsync con una scadenza. Alla scadenza il socket viene chiuso,
        /// che e' l'unico modo di annullare una connessione in volo, e si lancia
        /// TimeoutException: la spiegazione all'utente la scrive
        /// ExplainConnectionFailure.
        /// </summary>
        private static async Task ConnectWithDeadlineAsync(StreamSocket socket, HostName hostName, int port)
        {
            Task connecting = socket.ConnectAsync(hostName, port.ToString()).AsTask();
            Task deadline = Task.Delay(ConnectDeadlineMs);

            if (await Task.WhenAny(connecting, deadline) != connecting)
            {
                try { socket.Dispose(); }
                catch (Exception ex) { Diag.Failed("ConnectWithDeadlineAsync/cancel", ex); }

                // La ConnectAsync abbandonata fallira' con "operazione
                // annullata": si osserva, altrimenti resta un'eccezione senza
                // lettore.
                connecting.ContinueWith(
                    delegate(Task t) { AggregateException ignored = t.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

                throw new TimeoutException(string.Format(
                    "nessuna risposta da {0}:{1} entro {2} ms",
                    hostName.RawName, port, ConnectDeadlineMs));
            }

            await connecting;   // propaga il guasto vero (rifiuto, host irraggiungibile, ...)
        }

        /// <summary>"indirizzo:porta", la forma in cui l'utente ha scritto il dato.</summary>
        private static string Endpoint(string address, int port)
        {
            return string.Format("{0}:{1}", address, port);
        }

        /// <summary>
        /// Traduce il guasto in una riga comprensibile. "The method or operation
        /// is not implemented" non dice all'utente che manca un pezzo di
        /// piattaforma; il testo inglese di WinSock non dice ne' quale indirizzo
        /// ne' cosa fare.
        /// </summary>
        private static string ExplainConnectionFailure(Exception ex, string stage, string endpoint)
        {
            bool platformMissing = ex is NotImplementedException
                || ex is PlatformNotSupportedException
                || ex.HResult == unchecked((int)0x80004001);

            if (platformMissing)
            {
                return string.Format(
                    Loc.Get("CommService_PlatformMissing",
                        "This phone does not implement a required Windows feature ({0}: {1})"),
                    stage, ex.Message);
            }

            if (ex is TimeoutException || ex.HResult == WsaETimedOut)
            {
                return string.Format(
                    Loc.Get("CommService_ConnectTimeout",
                        "The server at {0} did not answer. Check that the PC is on, on the same network, and that the port is open."),
                    endpoint);
            }

            if (ex.HResult == WsaEConnRefused)
            {
                return string.Format(
                    Loc.Get("CommService_ConnectRefused",
                        "The server at {0} refused the connection. Check that the adapter is running."),
                    endpoint);
            }

            if (ex.HResult == WsaENetUnreachable || ex.HResult == WsaEHostUnreachable)
            {
                return string.Format(
                    Loc.Get("CommService_ConnectUnreachable",
                        "The server at {0} is not reachable on this network."),
                    endpoint);
            }

            return ex.Message;
        }
```

The old two-argument `ExplainConnectionFailure` is gone: both call sites now pass three arguments.

- [ ] **Step 4: Add the `using` for `AsTask()`**

The extension `IAsyncAction.AsTask()` lives in `System.Runtime.InteropServices.WindowsRuntime`. Add the using after `using System.Linq;`:

```csharp
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
```

- [ ] **Step 5: Add the four resource keys to en-US**

In `WhatsappApp/Strings/en-US/Resources.resw`, insert these four entries immediately after the `CommService_ClientRemoved` entry:

```xml
  <data name="CommService_ConnectTimeout" xml:space="preserve">
    <value>The server at {0} did not answer. Check that the PC is on, on the same network, and that the port is open.</value>
  </data>
  <data name="CommService_ConnectRefused" xml:space="preserve">
    <value>The server at {0} refused the connection. Check that the adapter is running.</value>
  </data>
  <data name="CommService_ConnectUnreachable" xml:space="preserve">
    <value>The server at {0} is not reachable on this network.</value>
  </data>
  <data name="CommService_InvalidAddress" xml:space="preserve">
    <value>Enter a valid address (host name or IP, port 1-65535): {0}</value>
  </data>
```

- [ ] **Step 6: Add the same four keys to it-IT**

In `WhatsappApp/Strings/it-IT/Resources.resw`, insert these four entries in the same position (immediately after `CommService_ClientRemoved`):

```xml
  <data name="CommService_ConnectTimeout" xml:space="preserve">
    <value>Il server {0} non ha risposto. Controlla che il PC sia acceso, sulla stessa rete, e che la porta sia aperta.</value>
  </data>
  <data name="CommService_ConnectRefused" xml:space="preserve">
    <value>Il server {0} ha rifiutato la connessione. Controlla che l'adapter sia avviato.</value>
  </data>
  <data name="CommService_ConnectUnreachable" xml:space="preserve">
    <value>Il server {0} non e' raggiungibile su questa rete.</value>
  </data>
  <data name="CommService_InvalidAddress" xml:space="preserve">
    <value>Inserisci un indirizzo valido (nome host o IP, porta 1-65535): {0}</value>
  </data>
```

- [ ] **Step 7: Run the guards**

```bash
node tools/check-csharp5.js
node tools/check-resw.js --strict
xmllint --noout WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw
```

Expected: `OK: 27 C# file(s)`; `OK: 95 key(s) in en-US and it-IT, every x:Uid and Loc.Get lookup resolved.`; `xmllint` prints nothing.

- [ ] **Step 8: Commit**

```bash
git add WhatsappApp/Services/CommunicationService.cs WhatsappApp/Strings
git commit -m "fix: give the connect a deadline and say what the timeout means"
```

---

### Task 4: The connection page asks for the QR whenever it opens on a live connection

**Files:**
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml.cs` (`OnNavigatedTo`)

**Interfaces:**
- Consumes: `CommunicationService.Instance.SendControlAsync("login.qr")`, already used in `OnConnectionEstablished` and `LoginQrButton_Click`.
- Produces: nothing other tasks use.

Why: with Task 1 and Task 2 the read loop survives, so a connection established *while the page is open* raises `ConnectionEstablished` and asks for the QR. But `App.StartAutoConnect` connects at startup, before the page exists: `OnNavigatedTo` then sees `IsConnected` and only sends `status`, so no QR is ever requested until the user finds the button. The QR must be asked for on every entry to the page while the account is not linked.

- [ ] **Step 1: Ask for the QR in `OnNavigatedTo`**

In `WhatsappApp/Pages/ConnectionPage.xaml.cs`, replace

```csharp
            if (CommunicationService.Instance.IsConnected)
            {
                ShowConnectedState();
                // OnNavigatedTo is not async: fire the status request and ignore the task
                CommunicationService.Instance.SendControlAsync("status");
            }
```

with

```csharp
            if (CommunicationService.Instance.IsConnected)
            {
                ShowConnectedState();

                // Due richieste distinte. Lo stato dipinge il pannello; il QR
                // serve perche' la connessione puo' essere stata aperta
                // dall'avvio automatico, prima che questa pagina esistesse: in
                // quel caso ConnectionEstablished e' gia' passato e nessuno ha
                // mai chiesto il codice. Il login si fa dal telefono, quindi il
                // codice si chiede da soli a ogni ingresso.
#pragma warning disable 4014
                CommunicationService.Instance.SendControlAsync("status");
                if (CommunicationService.Instance.WhatsAppState != "connected")
                {
                    CommunicationService.Instance.SendControlAsync("login.qr");
                }
#pragma warning restore 4014
            }
```

- [ ] **Step 2: Check the XAML is untouched and the C# is still C# 5**

```bash
node tools/check-csharp5.js
xmllint --noout WhatsappApp/Pages/ConnectionPage.xaml
```

Expected: `OK: 27 C# file(s)`; `xmllint` prints nothing.

- [ ] **Step 3: Commit**

```bash
git add WhatsappApp/Pages/ConnectionPage.xaml.cs
git commit -m "fix: request the login QR whenever the connection page opens"
```

---

### Task 5: A saved address that no longer answers falls back to discovery

**Files:**
- Modify: `WhatsappApp/Services/AutoConnector.cs` (`TryConnectAsync`)

**Interfaces:**
- Consumes: `CommunicationService.ConnectToServerAsync`, `DiscoveryService.StartAsync`, `DiscoveryService.WaitForSingleAsync`, `SettingsService.Save`, all already used in this file.
- Produces: no signature change; `TryConnectAsync(string username, int discoverySeconds) : Task<bool>` keeps its contract ("true when the connection is active at the end").

Why: `TryConnectAsync` returns immediately when a saved address fails, and the saved address is exactly the one `App.StartAutoConnect` uses. A router that hands out a new IP for the PC therefore produces the reported `0x8007274C` on every start, with the adapter sitting right there on the network, announced by its beacon. A stale address must be forgotten, not reported.

- [ ] **Step 1: Try the saved address, then fall back to discovery**

Replace the body of `TryConnectAsync` (from `if (CommunicationService.Instance.IsConnected) return true;` through the `finally` block) with:

```csharp
            if (CommunicationService.Instance.IsConnected) return true;
            if (_running) return false;

            _running = true;
            try
            {
                string address = SettingsService.ServerAddress;
                int port = SettingsService.ServerPort;

                if (!string.IsNullOrEmpty(address))
                {
                    bool onSaved = await CommunicationService.Instance.ConnectToServerAsync(address, port, username);
                    if (onSaved)
                    {
                        SettingsService.Save(address, port, username);
                        return true;
                    }

                    // L'indirizzo salvato non risponde piu' (il DHCP ha dato al
                    // PC un altro IP): non e' un guasto da mostrare, e' un dato
                    // da dimenticare. Si prova l'unico adapter che si annuncia.
                    Diag.Failed("AutoConnector/saved",
                        new InvalidOperationException("nessuna risposta da " + address + ":" + port));
                }

                await DiscoveryService.Instance.StartAsync();
                DiscoveredServer server = await DiscoveryService.Instance.WaitForSingleAsync(discoverySeconds);
                if (server == null) return false;

                bool connected = await CommunicationService.Instance.ConnectToServerAsync(
                    server.Address, server.Port, username);
                if (connected) SettingsService.Save(server.Address, server.Port, username);
                return connected;
            }
            finally
            {
                _running = false;
            }
```

- [ ] **Step 2: Run the guard**

```bash
node tools/check-csharp5.js
```

Expected: `OK: 27 C# file(s)`.

- [ ] **Step 3: Commit**

```bash
git add WhatsappApp/Services/AutoConnector.cs
git commit -m "fix: fall back to discovery when the saved server address is stale"
```

---

### Task 6: The adapter refuses a frame larger than the limit (TDD)

**Files:**
- Create: `WhatsappBridge/test/frame-limit.test.js`
- Modify: `WhatsappBridge/server.js` (top-level constant, the TCP `data` handler, `module.exports`)

**Interfaces:**
- Consumes: `createBridge({ config, gowa, log, debug })` and `cryptoHelper.buildFrame`, already used by `WhatsappBridge/test/server.test.js`.
- Produces: `MAX_FRAME_LENGTH` exported from `WhatsappBridge/server.js` = `8388608`; the TCP server destroys a socket whose announced frame length is `0` or greater than that.

Why: the app now bounds what it will allocate, but the adapter should never make an absurd length reachable in the first place. Today `msgLen` from an out-of-sync or hostile client is used to decide "do I have the whole frame yet" with no ceiling, so the buffer would grow until the Node process dies. The two sides must agree on the number, and a test must pin it.

- [ ] **Step 1: Write the failing test**

Create `WhatsappBridge/test/frame-limit.test.js`:

```js
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const net = require('node:net');
const { createBridge, MAX_FRAME_LENGTH } = require('../server');
const cryptoHelper = require('../crypto-helper');

function startBridge() {
  const lines = [];
  const log = (level, ...args) => lines.push(`${level} ${args.join(' ')}`);
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({ config, gowa: {}, log, debug: () => {} });
  return new Promise((resolve) => {
    bridge.tcpServer.listen(0, '127.0.0.1', () => {
      resolve({ bridge, lines, port: bridge.tcpServer.address().port });
    });
  });
}

function waitForClose(socket) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(
      () => reject(new Error('il server non ha chiuso la connessione')), 2000);
    socket.once('close', () => { clearTimeout(timer); resolve(); });
  });
}

test('MAX_FRAME_LENGTH e\' 8 MiB, la stessa soglia dell\'app WP8', () => {
  assert.strictEqual(MAX_FRAME_LENGTH, 8 * 1024 * 1024);
});

test('una lunghezza annunciata oltre il limite chiude la connessione, non attende i byte', async () => {
  const { bridge, lines, port } = await startBridge();
  const socket = net.connect(port, '127.0.0.1');
  try {
    await new Promise((resolve) => socket.once('connect', resolve));

    // Solo il prefisso: se il server lo accettasse resterebbe in attesa di
    // MAX_FRAME_LENGTH + 1 byte che non arrivano mai.
    const header = Buffer.alloc(4);
    header.writeUInt32LE(MAX_FRAME_LENGTH + 1, 0);
    socket.write(header);

    await waitForClose(socket);
    assert.ok(
      lines.some((line) => line.includes('lunghezza')),
      'il motivo della chiusura deve finire nel log: ' + JSON.stringify(lines));
  } finally {
    socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('una lunghezza annunciata a zero chiude la connessione invece di girare a vuoto', async () => {
  const { bridge, port } = await startBridge();
  const socket = net.connect(port, '127.0.0.1');
  try {
    await new Promise((resolve) => socket.once('connect', resolve));

    const header = Buffer.alloc(4);
    header.writeUInt32LE(0, 0);
    socket.write(header);

    await waitForClose(socket);
  } finally {
    socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('un frame normale resta accettato dopo il limite', async () => {
  const { bridge, port } = await startBridge();
  const socket = net.connect(port, '127.0.0.1');
  const messages = [];
  let buffer = Buffer.alloc(0);
  const waiters = [];
  socket.on('data', (chunk) => {
    buffer = Buffer.concat([buffer, chunk]);
    while (buffer.length >= 4) {
      const len = buffer.readUInt32LE(0);
      if (buffer.length < 4 + len) break;
      const payload = buffer.slice(4, 4 + len);
      buffer = buffer.slice(4 + len);
      const json = JSON.parse(cryptoHelper.decodePayload(payload));
      messages.push(json);
      while (waiters.length) waiters.shift()(json);
    }
  });
  try {
    await new Promise((resolve) => socket.once('connect', resolve));

    // Il primo frame che il bridge manda da solo: lo stato al collegamento.
    const first = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('timeout')), 2000);
      waiters.push((m) => { clearTimeout(timer); resolve(m); });
      if (messages.length) { clearTimeout(timer); resolve(messages.shift()); }
    });
    assert.strictEqual(first.Command, 'state');
  } finally {
    socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});
```

- [ ] **Step 2: Run it to verify it fails**

```bash
cd WhatsappBridge && node --test test/frame-limit.test.js
```

Expected: FAIL. The first test fails with `MAX_FRAME_LENGTH` undefined (`AssertionError`), and the second and third hang until `il server non ha chiuso la connessione`.

- [ ] **Step 3: Implement the ceiling**

In `WhatsappBridge/server.js`, add the constant next to `LOG_TAGS`:

```js
/**
 * Oltre questa lunghezza il prefisso di 4 byte non e' un payload, e' un
 * guasto (client disallineato o ostile). Deve restare uguale a
 * CommunicationService.MaxFrameLength nell'app WP8.1: le due parti parlano
 * dello stesso frame, quindi ne hanno lo stesso tetto.
 */
const MAX_FRAME_LENGTH = 8 * 1024 * 1024;
```

Then, inside `net.createServer((socket) => { ... })`, in the `socket.on('data', ...)` handler, replace

```js
      while (buffer.length >= 4) {
        const msgLen = buffer.readUInt32LE(0);
        if (buffer.length < 4 + msgLen) break;
```

with

```js
      while (buffer.length >= 4) {
        const msgLen = buffer.readUInt32LE(0);

        // Un client disallineato annuncia una lunghezza enorme: senza un tetto
        // il server resterebbe in attesa di gigabyte e il buffer crescerebbe
        // finche' il processo non cade. Zero e' l'altro caso degenere: un frame
        // vuoto farebbe girare il ciclo senza consumare niente.
        if (msgLen === 0 || msgLen > MAX_FRAME_LENGTH) {
          logger('ERR', `Frame non accettabile da ${remote} (lunghezza ${msgLen}): connessione chiusa`);
          socket.destroy();
          return;
        }

        if (buffer.length < 4 + msgLen) break;
```

- [ ] **Step 4: Export it**

At the bottom of `WhatsappBridge/server.js`, replace

```js
module.exports = { createBridge, main, makeLogger };
```

with

```js
module.exports = { createBridge, main, makeLogger, MAX_FRAME_LENGTH };
```

- [ ] **Step 5: Run it to verify it passes**

```bash
cd WhatsappBridge && node --test test/frame-limit.test.js
```

Expected: PASS, 4 tests.

- [ ] **Step 6: Run the whole adapter suite**

```bash
cd WhatsappBridge && npm test
```

Expected: PASS. 45 tests before this task, 49 after (4 added).

- [ ] **Step 7: Commit**

```bash
git add WhatsappBridge/server.js WhatsappBridge/test/frame-limit.test.js
git commit -m "fix: refuse a frame larger than the agreed limit in the adapter"
```

---

### Task 7: Write down the invariants the device taught us

**Files:**
- Modify: `README.md`, `README.it.md`
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`
- Modify: `.agents/skills/maintain-the-app/SKILL.md`
- Modify: `.agents/skills/test-the-app/SKILL.md`

**Interfaces:**
- Consumes: the behaviour shipped in Tasks 1-6.
- Produces: nothing code depends on.

- [ ] **Step 1: Add a bullet to the control protocol section of the adapter docs**

In `WhatsappBridge/README.md`, in the `## Control protocol` section, append this bullet at the end of the section:

```markdown
- One frame is `[4-byte little-endian length][payload]`. Anything above
  `MAX_FRAME_LENGTH` (8 MiB, exported from `server.js` and equal to
  `CommunicationService.MaxFrameLength` in the app) is treated as a fault: the
  adapter logs the length and closes the socket instead of buffering.
```

In `WhatsappBridge/README.it.md`, in the `## Protocollo di controllo` section, append the matching bullet:

```markdown
- Un frame e' `[lunghezza 4 byte little-endian][payload]`. Oltre
  `MAX_FRAME_LENGTH` (8 MiB, esportato da `server.js` e uguale a
  `CommunicationService.MaxFrameLength` nell'app) la lunghezza annunciata e' un
  guasto: l'adapter la scrive nel log e chiude il socket, invece di accumulare.
```

- [ ] **Step 2: Add a bullet to the protocol section of the project docs**

In `README.md`, in the `## Protocol` section, append at the end of the section:

```markdown
- A connection attempt owns its socket, its `DataReader` and its read loop: only
  the newest attempt publishes them, and only its loop reads them. A failed
  attempt closes only its own objects. Two readers on one `DataReader` desync it,
  which is what turned a bad length into an `OutOfMemoryException` on the phone.
- The connect has a 6-second deadline and a bounded frame reader (8 MiB). A
  timeout (`0x8007274C`) is reported with the address and the port; when the
  saved address stops answering, the app forgets it and falls back to discovery.
```

In `README.it.md`, in the `## Protocollo` section, append the matching bullet:

```markdown
- Un tentativo di connessione possiede il suo socket, il suo `DataReader` e il suo
  ciclo di lettura: solo il tentativo piu' recente li pubblica, e solo il suo ciclo
  li legge. Un tentativo fallito chiude soltanto i propri oggetti. Due lettori
  sullo stesso `DataReader` lo disallineano, ed e' cosi' che una lunghezza sbagliata
  diventava un `OutOfMemoryException` sul telefono.
- La connessione ha una scadenza di 6 secondi e il lettore ha un tetto di 8 MiB. Un
  timeout (`0x8007274C`) viene detto con indirizzo e porta; quando l'indirizzo
  salvato smette di rispondere, l'app lo dimentica e ripiega sulla scoperta.
```

- [ ] **Step 3: Record the gotchas in the maintain skill**

In `.agents/skills/maintain-the-app/SKILL.md`, in `## Known gotchas`, append these bullets:

```markdown
- **A connection is owned by the attempt that opened it.** `ConnectToServerAsync`
  takes the next `_connectionId`, keeps its socket/reader/writer in locals,
  publishes them only while that id is still current, and hands the reader to
  `ListenForMessagesAsync(attempt, reader)`. Never read `_reader` from a loop and
  never let a failing attempt call cleanup on the published fields: two readers on
  one `DataReader` desync it, and the next length read is a slice of JSON.
- **A frame length is not trusted.** `ReadFrameAsync` fills the 4-byte prefix fully
  (`InputStreamOptions.Partial` can split it) and rejects anything outside
  `1..MaxFrameLength` (8 MiB). The adapter's `MAX_FRAME_LENGTH` is the same number:
  change one and you must change the other, or one side will drop what the other
  sends.
- **`0x8007274C` is `WSAETIMEDOUT`, not a crypto or login failure.** It means
  `ConnectAsync` never got an answer; the handshake and the QR never ran. Check the
  address first: `AutoConnector` tries `SettingsService.ServerAddress` before
  discovery, so a stale IP shows up here. `StreamSocket` has no timeout, which is
  why `ConnectWithDeadlineAsync` closes the socket after 6 seconds.
```

- [ ] **Step 4: Record the on-device expectations in the test skill**

In `.agents/skills/test-the-app/SKILL.md`, in `## On-device checklist`, append:

```markdown
- After a change to the socket layer, the debug log must show the three `DIAG ok:`
  startup lines (crypto, DisplayRequest, beacon), **no** `DIAG ConnectToServerAsync`,
  and **no** `DIAG ListenForMessagesAsync`. A `DIAG ReadFrameAsync/length` line means
  the two sides disagree about the frame: read the length it prints before changing
  anything else.
- On a fresh install the app connects on its own and the QR overlay opens without
  pressing anything. If the connection is already up when the settings page is
  opened, the page asks for the QR again by itself.
```

- [ ] **Step 5: Run the docs gate**

```bash
node tools/check-docs.js
node tools/check-resw.js --strict
```

Expected: `OK: 2 doc pair(s)...` with no problem reported and no emoji; `OK: 95 key(s) in en-US and it-IT...`.

- [ ] **Step 6: Commit**

```bash
git add README.md README.it.md WhatsappBridge/README.md WhatsappBridge/README.it.md .agents/skills
git commit -m "docs: record the frame limit, the attempt id and the timeout meaning"
```

---

### Task 8: Whole-solution verification, then the phone

**Files:** none (verification only; if the guards find a problem, fix it in the file that owns it and commit separately).

- [ ] **Step 1: Run the full fast gate**

```bash
node tools/check-csharp5.js
node tools/check-icons.js
node tools/check-resw.js --strict
node tools/check-docs.js
xmllint --noout WhatsappApp/App.xaml WhatsappApp/MainPage.xaml WhatsappApp/Pages/*.xaml
cd WhatsappBridge && npm test && cd ..
node --test "tools/test/**/*.test.js"
```

Expected: all green. In particular `check-resw.js --strict` reports `95 key(s)` and `npm test` reports 49 passing, `node --test` reports 17 passing.

- [ ] **Step 2: Run the real build gate (user, on the Windows machine)**

```bash
prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"
prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"
prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m"
```

Expected: `COPIA=0`; then `Errori: 0, Avvisi: 2` (the two known warnings only) and `WhatsappApp_1.0.1.0_x86_Debug.appxbundle` produced.

- [ ] **Step 3: Deploy and watch the log (user, on the phone)**

Start the adapter (`node tools/start-login.js` or `npm start` in `WhatsappBridge`), deploy from Visual Studio with the debugger attached, and confirm in this order:

1. `DIAG ok: crypto AES-256-CBC + HMAC-SHA256`, `DIAG ok: schermo sempre acceso (DisplayRequest)`, `DIAG ok: beacon UDP in ascolto sulla porta 8587`.
2. **No** `DIAG ConnectToServerAsync`. If there is one, the HResult it prints says which of the four cases it is (timeout, refused, unreachable, invalid address).
3. **No** `DIAG ListenForMessagesAsync`, and in particular no `OutOfMemoryException 0x8007000E`.
4. No `DIAG ReadFrameAsync/length`. If it appears, note the number: it is the length the two sides disagree about.
5. The QR overlay opens on its own and stays up while the phone scans it, then `WhatsAppState` becomes `connected` and the continue button enables.

- [ ] **Step 4: Commit anything the gate forced you to change**

Only if Steps 1-3 required a fix. Use the message of whichever task owned the file; do not open a new commit for a plan-only change.

---

## Self-Review

**1. Spec coverage**

| Requirement from the evidence | Task |
| --- | --- |
| `0x8007274C` connect timeout | Task 3 (deadline, validation, four WinSock cases, message) and Task 5 (stale saved address no longer blocks the connection) |
| `OutOfMemoryException 0x8007000E` in `ListenForMessagesAsync` | Task 1 (bounded, partial-safe reader) and Task 2 (one reader per connection, failed attempt no longer disposes the live one) |
| No QR although connected | Task 1 + Task 2 restore the control frame; Task 4 covers a connection established before the page opened |
| Adapter must not be the source of an absurd length | Task 6 (ceiling, zero-length, Node tests, `MAX_FRAME_LENGTH` exported) |
| Behaviour must be recorded for the next change | Task 7 (both README pairs, maintain skill, test skill) |

No spec item is left without a task.

**2. Placeholder scan**

No `TBD`, no "add error handling", no "similar to Task N": every code step carries the complete replacement text, and every verification step carries the exact command and the expected output. Task 3 and Task 5 name the exact anchor text to replace, and Task 8's only conditional step (Step 4) states that it runs only if the gate failed.

**3. Type consistency**

- `MaxFrameLength` (C#, `uint`, 8 MiB) and `MAX_FRAME_LENGTH` (JS, 8 MiB) are declared once each and named exactly this way in Task 1, Task 3's comments, Task 6 and the docs.
- `LoadAtLeastAsync(DataReader, uint) : Task<bool>` is declared in Task 1 and used twice in the same task's `ReadFrameAsync`; no other task calls it.
- `DisposeSocket(StreamSocket, DataWriter, DataReader)` and `DisposePublishedSocket()` are declared in Task 2 and used in Task 2 and Task 3 only.
- `ListenForMessagesAsync(int attempt, DataReader reader)` is declared and called in Task 2 with that exact signature; Task 4 and Task 5 do not reference it.
- `ExplainConnectionFailure(Exception, string, string)` is given three arguments at both call sites in Task 3; the two-argument form is deleted there.
- New resource keys are `CommService_ConnectTimeout`, `CommService_ConnectRefused`, `CommService_ConnectUnreachable`, `CommService_InvalidAddress`, spelled identically in the code (`Loc.Get`) and in both `.resw` files, and used, so `--strict` stays green.
- Test count arithmetic: 45 adapter tests + 4 new = 49, asserted in Task 6 Step 6 and Task 8 Step 1.
