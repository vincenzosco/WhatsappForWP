# Runtime failures: name them, then fix them — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** make every runtime failure the app currently survives say where it happened and with which HRESULT, then fix the defects that reading the code already proves.

**Architecture:** one diagnostic sink (`Diag`) replaces the silent `catch` walls, so the next debug run names the failing call instead of printing "The operation identifier is not valid" with no caller; a DEBUG-only start-up probe exercises the three platform surfaces this project cannot check from a Mac (AES-256-GCM, the UDP discovery bind, the screen request) through the same sink; the two lookups that re-throw forever (the resource loader and the dispatcher) stop retrying.

**Tech Stack:** C# 5 on the Windows Phone 8.1 WinRT stack (`Windows.UI.Xaml`), built with Visual Studio 2013 / MSBuild 12; the Node.js adapter in `WhatsappBridge/` is unchanged by this plan.

## The evidence

A debug run of `WhatsappApp.exe` on a real device/emulator prints, with the debugger attached:

```
A first chance exception of type 'System.Exception' occurred in WhatsappApp.exe
WinRT information: The operation identifier is not valid.        (repeated, about 27 times)

A first chance exception of type 'System.NotImplementedException' occurred in WhatsappApp.exe
A first chance exception of type 'System.NotImplementedException' occurred in mscorlib.ni.dll
```

Three facts follow from reading the code:

1. `0x800710DD` ("The operation identifier is not valid") is a **WinRT call made in a context that
   cannot serve it** — almost always a call from a thread that is not the UI thread. The app has ten
   `catch` blocks that discard it without a trace, so the log cannot say which call threw.
2. The only `NotImplementedException` this *project* can throw is
   `ConvertBack` in `WhatsappApp/Converters/Converters.cs` (seven copies, lines 48, 69, 96, 123, 142,
   159, 177 — one per converter except `BoolToVisibilityConverter`, which converts back for real).
   Everything else that could raise one lives in the platform.
3. Two places **retry a failed lookup on every single use**, because the failure is not remembered:
   `Loc.Loader` (`ResourceLoader.GetForCurrentView()`, called for every translated string) and
   `CommunicationService.GetUiDispatcher()` (called for every received message). One failure becomes
   an unbounded stream of first-chance exceptions, which is exactly the shape of the log.

What the metadata check cannot tell us, and why Task 6 exists: `DatagramSocket`, `DisplayRequest`,
`DatagramSocketControl` and `SymmetricAlgorithmNames.AesGcm` **are** all present in the Windows
Phone 8.1 reference metadata (`Windows.winmd` in
`C:\Program Files (x86)\Windows Phone Kits\8.1\References\CommonConfiguration\Neutral`), which is
why the app compiles. Presence in the metadata is not proof that the phone implements the member, and
there is no way to find out from this Mac except by asking the phone at run time.

## Global Constraints

- **C# 5 only.** No `$"..."`, `?.`, expression-bodied members, `nameof`, `out var`, `is Type name`,
  `_ = ...`, async entry points. Gate: `node tools/check-csharp5.js` (currently `OK: 25 C# file(s)`).
- **No hardcoded user-visible strings.** One is added nowhere by this plan: the diagnostics go to the
  debugger, not to the screen. If a later step needs a visible string, it goes into **both**
  `WhatsappApp/Strings/en-US/Resources.resw` and `.../it-IT/Resources.resw`, with the `x:Uid`
  property matching the element and code-side keys bare. Gate: `node tools/check-resw.js --strict`
  (currently `OK: 90 key(s)`).
- **No icon font, geometry inlined on the `Path`.** Not touched here. Gate: `node tools/check-icons.js`.
- **Docs come in pairs and carry no emoji.** `README.md`/`README.it.md` and
  `WhatsappBridge/README.md`/`WhatsappBridge/README.it.md` move together; `docs/superpowers/plans/*`
  are English-only records of finished work and are not part of the pairs. Gate:
  `node tools/check-docs.js`.
- **A new `.cs` file that is not in `WhatsappApp.csproj` does not exist at build time.**
- **The build gate runs on the Windows machine, from a local disk path**, never from the Parallels
  share:
  `C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86`
  must end with `0 Error(s)` and produce `WhatsappApp_1.0.1.0_x86_Debug.appxbundle`. The two known
  warnings (`CS0618` `FileOpenPicker.PickSingleFileAsync` in `ChatPage.xaml.cs`, `CS4014` in
  `ConnectionPage.xaml.cs`) are expected and not part of this plan.
- `cd WhatsappBridge && npm test` is `pass 36`, `fail 0`, and must stay there.

## File structure

| File | Responsibility |
| --- | --- |
| Create `WhatsappApp/Services/Diag.cs` | One sink for the failures the app decides to survive: deduplicated, with the HRESULT, silent in release. |
| Create `WhatsappApp/Services/SelfCheck.cs` | DEBUG-only probe of the three platform surfaces the app cannot verify off-device. |
| Modify `WhatsappApp/Services/Loc.cs` | Remember a failed resource loader instead of retrying it for every string. |
| Modify `WhatsappApp/Services/CommunicationService.cs` | Dispatcher lookup that stops retrying, and every transport catch routed through `Diag`. |
| Modify `WhatsappApp/Services/DiscoveryService.cs` | Beacon socket and parse errors routed through `Diag`. |
| Modify `WhatsappApp/Models/ChatMessage.cs` | JSON and image-decode failures routed through `Diag`. |
| Modify `WhatsappApp/Pages/ConnectionPage.xaml.cs` | The screen request must never be able to break the QR overlay; failures routed through `Diag`. |
| Modify `WhatsappApp/Pages/ChatPage.xaml.cs` | Image failure routed through `Diag` instead of a bare `Debug.WriteLine`. |
| Modify `WhatsappApp/Converters/Converters.cs` | `ConvertBack` stops throwing `NotImplementedException`. |
| Modify `WhatsappApp/App.xaml.cs` | Runs `SelfCheck` in DEBUG after `Loc.Prewarm()`. |
| Modify `WhatsappApp/WhatsappApp.csproj` | Registers the two new files. |
| Modify `.agents/skills/maintain-the-app/SKILL.md` | The file map and the rule: a silent catch is a bug. |

---

### Task 1: One sink for the failures the app survives

**Files:**
- Create: `WhatsappApp/Services/Diag.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj:100-108`

**Interfaces:**
- Consumes: nothing.
- Produces: `Diag.Failed(string where, Exception ex)`, `Diag.Ok(string what)`,
  `Diag.Describe(Exception ex) -> string`. Every later task calls `Diag.Failed` from inside a `catch`
  and `Diag.Ok` from the probe in Task 6.

- [x] **Step 1: Create the sink**

Create `WhatsappApp/Services/Diag.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Un posto solo per i guasti che l'app decide di sopravvivere.
    ///
    /// Perche' esiste: ogni `catch` silenzioso nasconde un guasto vero, e nel log
    /// del debugger un'eccezione WinRT arriva come
    ///
    ///     A first chance exception of type 'System.Exception' occurred ...
    ///     WinRT information: The operation identifier is not valid.
    ///
    /// senza dire *quale* chiamata l'ha lanciata. Qui il guasto viene registrato
    /// con il punto esatto e con l'HRESULT, che e' l'unica cosa che distingue un
    /// membro assente sulla piattaforma (E_NOTIMPL) da una chiamata fatta dal
    /// thread sbagliato (E_ILLEGAL_METHOD_CALL) o da un'operazione non piu'
    /// valida (0x800710DD).
    ///
    /// Ogni riga compare una volta sola: senza la deduplica, un ciclo che fallisce
    /// ogni due secondi (il beacon di scoperta) riempirebbe il log e nasconderebbe
    /// tutto il resto. Debug.WriteLine e' compilato via nelle build di rilascio,
    /// quindi in produzione questo codice non scrive e non costa.
    /// </summary>
    public static class Diag
    {
        private static readonly List<string> Seen = new List<string>();
        private static readonly object Gate = new object();

        /// <summary>Da chiamare dentro un catch, per un guasto da cui si prosegue.</summary>
        public static void Failed(string where, Exception ex)
        {
            Write(where + ": " + Describe(ex));
        }

        /// <summary>
        /// Una capacita' che invece c'e'. Il probe di avvio la registra una volta
        /// sola per esecuzione: la riga dice "questo telefono sa farlo".
        /// </summary>
        public static void Ok(string what)
        {
            Write("ok: " + what);
        }

        /// <summary>
        /// Tipo, HRESULT in esadecimale e messaggio: senza il codice due guasti
        /// diversi restano indistinguibili nel log.
        /// </summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return "(nessuna eccezione)";
            return ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + " " + (ex.Message ?? "");
        }

        /// <summary>Righe registrate finora, in ordine di prima occorrenza.</summary>
        public static List<string> Lines()
        {
            lock (Gate)
            {
                return new List<string>(Seen);
            }
        }

        private static void Write(string line)
        {
            lock (Gate)
            {
                if (Seen.Contains(line)) return;
                Seen.Add(line);
            }
            Debug.WriteLine("DIAG " + line);
        }
    }
}
```

- [x] **Step 2: Register the file in the project**

In `WhatsappApp/WhatsappApp.csproj`, in the `Services` block, keep the list alphabetical — insert after
`Services\DataService.cs` and before `Services\DiscoveryService.cs`:

```xml
    <Compile Include="Services\Diag.cs" />
```

- [x] **Step 3: Run the guard**

Run: `node tools/check-csharp5.js`
Expected: `OK: 26 C# file(s) are C# 5 compatible.`

If the compiler later rejects `ex.HResult` (CS1061 — it is a .NET 4.5 member, present on WP8.1, but
this is the one thing the guard cannot see), replace the body of `Describe` with:

```csharp
            if (ex == null) return "(nessuna eccezione)";
            int hresult = System.Runtime.InteropServices.Marshal.GetHRForException(ex);
            return ex.GetType().Name + " 0x" + hresult.ToString("X8") + " " + (ex.Message ?? "");
```

- [x] **Step 4: Commit**

```bash
git add WhatsappApp/Services/Diag.cs WhatsappApp/WhatsappApp.csproj
git commit -m "feat(app): add one sink for the runtime failures the app survives"
```

---

### Task 2: The two lookups that throw again on every use

**Files:**
- Modify: `WhatsappApp/Services/Loc.cs`
- Modify: `WhatsappApp/Services/CommunicationService.cs:36-42` (fields), `:117-134` (`GetUiDispatcher`), `:150-163` (`DispatchOnUiThread`), `:558-562` (`Disconnect`)

**Interfaces:**
- Consumes: `Diag.Failed(string, Exception)` from Task 1.
- Produces: `Loc.Loader` that stops retrying after a failure (Task 3 and every page depend on the
  unchanged public surface: `Loc.Get(string, string)`, `Loc.Prewarm()`); a private
  `CommunicationService.TryGetDispatcher(Func<CoreDispatcher>, string)`; and
  `CommunicationService.Prewarm()`, which resolves the dispatcher where it can actually be found.

- [x] **Step 1: Stop retrying a failed resource loader**

Replace the whole content of `WhatsappApp/Services/Loc.cs` with:

```csharp
using System;
using Windows.ApplicationModel.Resources;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Unico punto di accesso alle stringhe localizzate
    /// (Strings\&lt;lingua&gt;\Resources.resw). La lingua la sceglie il sistema
    /// in base a quella del dispositivo; se manca una risorsa si usa il testo
    /// di fallback, quindi un errore nelle risorse non fa mai esplodere l'app.
    /// </summary>
    public static class Loc
    {
        private static ResourceLoader _loader;

        /// <summary>
        /// Vero dopo che GetForCurrentView ha fallito una volta. Senza questo,
        /// ogni Loc.Get riprovava la stessa chiamata e ne registrava
        /// l'eccezione: un guasto solo diventava un'eccezione per stringa.
        /// </summary>
        private static bool _loaderUnavailable;

        private static ResourceLoader Loader
        {
            get
            {
                if (_loader != null || _loaderUnavailable) return _loader;

                try
                {
                    _loader = ResourceLoader.GetForCurrentView();
                }
                catch (Exception ex)
                {
                    _loaderUnavailable = true;
                    Diag.Failed("Loc.Loader", ex);
                }
                return _loader;
            }
        }

        /// <summary>
        /// Crea il loader sul thread UI. GetForCurrentView non si puo' chiamare
        /// da un thread di background; una volta creato, invece, il loader si
        /// puo' interrogare da qualsiasi thread.
        ///
        /// E' anche l'unico punto in cui si ritenta dopo un fallimento: se il
        /// primo tentativo e' partito da un thread di background, qui si azzera
        /// il blocco e si prova di nuovo, sul thread giusto.
        /// </summary>
        public static void Prewarm()
        {
            _loaderUnavailable = false;

            var loader = Loader;
            if (loader != null)
            {
                try { loader.GetString("Nav_Chats"); }
                catch (Exception ex) { Diag.Failed("Loc.Prewarm", ex); }
            }
        }

        public static string Get(string key, string fallback)
        {
            var loader = Loader;
            if (loader == null) return fallback;

            try
            {
                string value = loader.GetString(key);
                return string.IsNullOrEmpty(value) ? fallback : value;
            }
            catch (Exception ex)
            {
                // L'etichetta non si costruisce con la parentesi dopo il punto:
                // check-resw.js legge ogni chiamata con una stringa letterale
                // come una chiave da cercare nei .resw, e cosi' costruita
                // sembrava una chiave mancante (anche scritta in un commento).
                Diag.Failed("Loc.Get key " + key, ex);
                return fallback;
            }
        }
    }
}
```

Two details of that code are not optional.

`Diag.Failed("Loc.Get key " + key, ex)` must not be written with the parenthesis right after the dot:
`tools/check-resw.js` reads **every** `Loc.Get("...")` occurrence in the C# files, comments included,
as a key to look up in both `.resw` files, so a label built that way makes the guard report a missing
key named `" + key + "`.

`Prewarm()` clearing `_loaderUnavailable` is the whole point of the flag being separate from the
loader: the background thread may fail, the UI thread must still get a chance.

- [x] **Step 2: Add the failure flag to the dispatcher fields**

In `WhatsappApp/Services/CommunicationService.cs`, replace:

```csharp
        // Cached UI dispatcher for marshalling events to the UI thread
        private CoreDispatcher _uiDispatcher;
```

with:

```csharp
        // Cached UI dispatcher for marshalling events to the UI thread
        private CoreDispatcher _uiDispatcher;

        // Vero quando nessuna delle sorgenti ha dato un dispatcher: senza questo
        // flag ogni messaggio ricevuto ripeteva le tre chiamate e le loro
        // eccezioni, per tutta la durata della connessione.
        private bool _uiDispatcherFailed;
```

- [x] **Step 3: Replace the dispatcher lookup**

In `WhatsappApp/Services/CommunicationService.cs`, replace the whole `GetUiDispatcher` method:

```csharp
        private CoreDispatcher GetUiDispatcher()
        {
            if (_uiDispatcher == null)
            {
                try
                {
                    _uiDispatcher = CoreApplication.GetCurrentView().CoreWindow.Dispatcher;
                }
                catch
                {
                    try
                    {
                        _uiDispatcher = CoreApplication.MainView.CoreWindow.Dispatcher;
                    }
                    catch { }
                }
            }
            return _uiDispatcher;
        }
```

with:

```csharp
        private CoreDispatcher GetUiDispatcher()
        {
            if (_uiDispatcher != null || _uiDispatcherFailed) return _uiDispatcher;

            // Due sorgenti, dalla piu' diretta. Da un thread di background
            // GetCurrentView fallisce; MainView e' quella che continua a
            // rispondere. Ognuna registra il proprio fallimento una volta sola.
            // (Window.Current non e' una sorgente: da un thread di background
            // restituisce null, quindi "ripiegare" li' darebbe solo un
            // NullReferenceException in piu'.)
            _uiDispatcher = TryGetDispatcher(
                delegate { return CoreApplication.GetCurrentView().CoreWindow.Dispatcher; },
                "GetUiDispatcher/GetCurrentView")
                ?? TryGetDispatcher(
                    delegate { return CoreApplication.MainView.CoreWindow.Dispatcher; },
                    "GetUiDispatcher/MainView");

            if (_uiDispatcher == null)
            {
                _uiDispatcherFailed = true;
                Diag.Failed("GetUiDispatcher", new InvalidOperationException("nessun CoreDispatcher disponibile"));
            }
            return _uiDispatcher;
        }

        /// <summary>
        /// Va chiamato una volta all'avvio, sul thread UI: e' l'unico momento in
        /// cui il dispatcher si trova di sicuro. Risolverlo la prima volta da un
        /// thread di background e' il motivo per cui questo servizio restava
        /// senza dispatcher e riprovava le due chiamate a ogni messaggio.
        /// </summary>
        public void Prewarm()
        {
            GetUiDispatcher();
        }

        private static CoreDispatcher TryGetDispatcher(Func<CoreDispatcher> source, string where)
        {
            try
            {
                return source();
            }
            catch (Exception ex)
            {
                Diag.Failed(where, ex);
                return null;
            }
        }
```

- [x] **Step 3b: Resolve it at start-up**

In `WhatsappApp/App.xaml.cs`, inside `OnLaunched`, right after `Loc.Prewarm();`:

```csharp
            // Stesso motivo del loader: il dispatcher si trova di sicuro solo
            // qui, sul thread UI. Risolverlo piu' tardi, da un thread di rete,
            // lasciava il servizio senza dispatcher per tutta la sessione.
            CommunicationService.Instance.Prewarm();
```

- [x] **Step 4: Register the fallback path of a dispatch**

In `WhatsappApp/Services/CommunicationService.cs`, replace the catch of `DispatchOnUiThread`:

```csharp
            catch
            {
                if (!dispatched) action();
            }
```

with:

```csharp
            catch (Exception ex)
            {
                Diag.Failed("DispatchOnUiThread", ex);
                if (!dispatched) action();
            }
```

- [x] **Step 5: Stop throwing the dispatcher away on disconnect**

In `WhatsappApp/Services/CommunicationService.cs`, in `Disconnect()`, delete this line
(it is followed by a blank line and then `DispatchOnUiThread(...)`):

```csharp
            _uiDispatcher = null;
```

The dispatcher object is not tied to the socket, and clearing it only forces `GetUiDispatcher()` to
walk the three sources again on the very next line.

- [x] **Step 6: Run the guard**

Run: `node tools/check-csharp5.js`
Expected: `OK: 26 C# file(s) are C# 5 compatible.`

- [x] **Step 7: Commit**

```bash
git add WhatsappApp/Services/Loc.cs WhatsappApp/Services/CommunicationService.cs
git commit -m "fix(app): stop retrying the resource loader and the dispatcher after a failure"
```

---

### Task 3: Give the transport a voice

**Files:**
- Modify: `WhatsappApp/Services/CommunicationService.cs:189-196`, `:232-238`, `:309-317`, `:332-340`, `:378-384`, `:458-461`, `:521-529`, `:544-556`
- Modify: `WhatsappApp/Models/ChatMessage.cs:225-234`, `:296-308`

**Interfaces:**
- Consumes: `Diag.Failed(string, Exception)`.
- Produces: no signature change anywhere; every failure in this layer reaches `Diag` before it
  reaches the user text.

- [x] **Step 1: Route every transport catch**

In `WhatsappApp/Services/CommunicationService.cs`, add `Diag.Failed("<call site>", ex);` as the
**first** statement of each of these catch blocks. The `where` string is the method or the call that
failed, so the log line names it:

| Method | `where` to pass |
| --- | --- |
| `StartServerAsync` | `"StartServerAsync"` |
| `OnServerConnectionReceived` | `"OnServerConnectionReceived"` |
| `ConnectToServerAsync` | `"ConnectToServerAsync"` |
| `ListenForMessagesAsync` | `"ListenForMessagesAsync"` |
| `SendMessageAsync` | `"SendMessageAsync"` |
| `DecryptToMessage` | `"DecryptToMessage"` |
| `BroadcastToAllClientsAsync` | `"BroadcastToAllClientsAsync"` |

For example, the `ConnectToServerAsync` catch becomes:

```csharp
            catch (Exception ex)
            {
                Diag.Failed("ConnectToServerAsync", ex);
                _isConnected = false;
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred(string.Format(
                        Loc.Get("CommService_ConnectError", "Connection error: {0}"), ex.Message))
                );
                return false;
            }
```

and `ListenForMessagesAsync` becomes:

```csharp
            catch (Exception ex)
            {
                Diag.Failed("ListenForMessagesAsync", ex);
                if (_isConnected)
                {
                    DispatchOnUiThread(() =>
                        RaiseErrorOccurred(string.Format(
                            Loc.Get("CommService_ConnectionLost", "Connection lost: {0}"), ex.Message))
                    );
                }
            }
```

- [x] **Step 2: Route the two silent catches in the server mode**

In `WhatsappApp/Services/CommunicationService.cs`, inside `OnServerConnectionReceived`, turn the
silent catches around `client.Dispose()` (around line 458) and in `Disconnect()` (around line 544)
into logging ones. For a `foreach` body:

```csharp
                try
                {
                    client.Dispose();
                }
                catch (Exception ex)
                {
                    Diag.Failed("OnServerConnectionReceived/dispose", ex);
                    deadClients.Add(client);
                }
```

and, in `Disconnect()`:

```csharp
                foreach (var client in _serverClients)
                {
                    try { client.Dispose(); }
                    catch (Exception ex) { Diag.Failed("Disconnect/dispose", ex); }
                }
```

The outer cleanup in `Disconnect()` (`try { ... } catch { }` around the four `Dispose` calls) becomes:

```csharp
            catch (Exception ex)
            {
                Diag.Failed("Disconnect", ex);
            }
```

- [x] **Step 3: Route the two silent catches in the message model**

In `WhatsappApp/Models/ChatMessage.cs`, `LoadMediaImageAsync`:

```csharp
            catch (Exception ex)
            {
                Diag.Failed("ChatMessage.LoadMediaImageAsync", ex);
                MediaImage = null;
            }
```

and `FromJson`:

```csharp
            catch (Exception ex)
            {
                Diag.Failed("ChatMessage.FromJson", ex);
                return null;
            }
```

`ChatMessage.cs` already has `using WhatsappApp.Services;` (for `Loc` and `ImageHelper`), so no new
using is needed.

- [x] **Step 4: Run the guard**

Run: `node tools/check-csharp5.js`
Expected: `OK: 26 C# file(s) are C# 5 compatible.`

- [x] **Step 5: Commit**

```bash
git add WhatsappApp/Services/CommunicationService.cs WhatsappApp/Models/ChatMessage.cs
git commit -m "fix(app): report every transport failure instead of discarding it"
```

---

### Task 4: Give the discovery a voice

**Files:**
- Modify: `WhatsappApp/Services/DiscoveryService.cs:79-91` (`StartAsync`), `:97-103` (`Stop`), `:149-156` (`OnMessageReceived`), `:206-217` (`Parse`)

**Interfaces:**
- Consumes: `Diag.Failed(string, Exception)`.
- Produces: unchanged public surface (`Port`, `Instance`, `StartAsync`, `Stop`, `Snapshot`,
  `WaitForSingleAsync`, `IsListening`, `ServersChanged`).

- [x] **Step 1: Route the socket failures**

In `WhatsappApp/Services/DiscoveryService.cs`, `StartAsync` becomes:

```csharp
            catch (Exception ex)
            {
                // Se la porta non si apre non c'e' niente da riprovare qui: si
                // registra perche' e quale, e la pagina continua con
                // l'inserimento manuale dell'indirizzo.
                Diag.Failed("DiscoveryService.StartAsync", ex);
                _socket = null;
            }
```

`Stop` becomes:

```csharp
        public void Stop()
        {
            DatagramSocket socket = _socket;
            _socket = null;
            if (socket == null) return;
            try { socket.MessageReceived -= OnMessageReceived; }
            catch (Exception ex) { Diag.Failed("DiscoveryService.Stop/handler", ex); }
            try { socket.Dispose(); }
            catch (Exception ex) { Diag.Failed("DiscoveryService.Stop/dispose", ex); }
        }
```

- [x] **Step 2: Route the datagram and the parse**

In the same file, `OnMessageReceived`'s catch becomes:

```csharp
            catch (Exception ex)
            {
                // Un datagramma malformato non deve fermare l'ascolto, ma un
                // guasto che si ripete a ogni beacon va visto una volta.
                Diag.Failed("DiscoveryService.OnMessageReceived", ex);
            }
```

and `Parse`'s catch becomes:

```csharp
            catch (Exception ex)
            {
                Diag.Failed("DiscoveryService.Parse", ex);
                return null;
            }
```

`DiscoveryService.cs` needs `using WhatsappApp.Services;`? No: the file is **inside**
`namespace WhatsappApp.Services`, so `Diag` resolves without a new using.

- [x] **Step 3: Run the guard**

Run: `node tools/check-csharp5.js`
Expected: `OK: 26 C# file(s) are C# 5 compatible.`

- [x] **Step 4: Commit**

```bash
git add WhatsappApp/Services/DiscoveryService.cs
git commit -m "fix(app): report discovery failures instead of swallowing them"
```

---

### Task 5: The screen request must not be able to break the QR, and `ConvertBack` must not throw

**Files:**
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml.cs:351-355` (`ShowQrCode` catch), `:380-400` (`KeepScreenOn`, `ReleaseScreenOn`)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs:256-262`
- Modify: `WhatsappApp/Converters/Converters.cs:46-49`, `:65-70`, `:92-96`, `:119-123`, `:138-142`, `:155-159`, `:173-177`

**Interfaces:**
- Consumes: `Diag.Failed(string, Exception)`.
- Produces: unchanged public surface; `ConvertBack` now returns
  `Windows.UI.Xaml.DependencyProperty.UnsetValue` instead of throwing.

- [x] **Step 1: Make the screen request unable to fail the overlay**

In `WhatsappApp/Pages/ConnectionPage.xaml.cs`, replace `KeepScreenOn` and `ReleaseScreenOn` with:

```csharp
        /// <summary>
        /// Lo schermo resta acceso: se si spegne o si abbassa la luminosita'
        /// mentre si inquadra, il codice diventa illeggibile e la scansione
        /// fallisce senza nessun messaggio d'errore.
        ///
        /// Tutto dentro il try, costruzione compresa: creare la richiesta puo'
        /// fallire sulla piattaforma, e una comodita' non deve mai impedire di
        /// mostrare il codice. Prima la costruzione stava fuori dal try, e
        /// l'eccezione usciva dal gestore del frame di controllo.
        /// </summary>
        private void KeepScreenOn()
        {
            if (_displayRequestActive) return;
            try
            {
                if (_displayRequest == null) _displayRequest = new Windows.System.Display.DisplayRequest();
                _displayRequest.RequestActive();
                _displayRequestActive = true;
            }
            catch (Exception ex)
            {
                // Limite di richieste attive raggiunto, o membro non
                // implementato su questo telefono: si prosegue senza.
                Diag.Failed("KeepScreenOn", ex);
            }
        }

        private void ReleaseScreenOn()
        {
            if (!_displayRequestActive || _displayRequest == null) return;
            try { _displayRequest.RequestRelease(); }
            catch (Exception ex) { Diag.Failed("ReleaseScreenOn", ex); }
            _displayRequestActive = false;
        }
```

- [x] **Step 2: Report why a QR did not appear**

In the same file, the catch of `ShowQrCode` becomes:

```csharp
            catch (Exception ex)
            {
                Diag.Failed("ShowQrCode", ex);
                QrInfoText.Text = string.Format(
                    Loc.Get("ConnectionPage_QrError", "Could not show the QR code: {0}"), ex.Message);
            }
```

- [x] **Step 3: Report the image failure in the chat page**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, replace:

```csharp
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    string.Format(Loc.Get("ChatPage_ImageError", "Could not open the image: {0}"), ex.Message));
            }
```

with:

```csharp
            catch (Exception ex)
            {
                Diag.Failed("ChatPage/image", ex);
                Debug.WriteLine(
                    string.Format(Loc.Get("ChatPage_ImageError", "Could not open the image: {0}"), ex.Message));
            }
```

The file has `using WhatsappApp.Services;` at line 13 but **not** `using System.Diagnostics;`, so that
one line has to be added — put it after `using System.IO;` (line 3), which is where the `System` group
is already sorted:

- [x] **Step 4: Stop `ConvertBack` from throwing**

In `WhatsappApp/Converters/Converters.cs`, in **all seven** converters
(`MessageStatusToStringConverter`, `MessageStatusToColorConverter`, `InitialToColorConverter`,
`UnreadCountToVisibilityConverter`, `OnlineToDotColorConverter`,
`MessageTypeToImageVisibilityConverter`, `MessageTypeToTextVisibilityConverter` — seven occurrences at
lines 48, 69, 96, 123, 142, 159 and 177; `BoolToVisibilityConverter` at line 21 is left alone because
its `ConvertBack` returns a real value), replace:

```csharp
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
```

with:

```csharp
        /// <summary>
        /// Questi converter sono a senso unico: XAML non deve mai scrivere
        /// indietro. Restituire UnsetValue lo dice al motore di binding; lanciare
        /// un'eccezione, invece, farebbe fallire l'app se un giorno un TextBox
        /// finisse legato a uno di questi (TextBox.Text e' TwoWay per default).
        /// </summary>
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return Windows.UI.Xaml.DependencyProperty.UnsetValue;
        }
```

- [x] **Step 5: Prove that no `NotImplementedException` of ours is left**

Run:

```bash
grep -rn "NotImplementedException" WhatsappApp --include=*.cs | grep -v "/obj/"
```

Expected: no output (only the generated `WhatsappApp/obj/Debug/XamlTypeInfo.g.cs` may contain it).

- [x] **Step 6: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js`
Expected: four `OK:` lines (`26 C# file(s)`, `13 inline icon Path(s)`, `90 key(s)`, `2 doc pair(s)`).

- [x] **Step 7: Commit**

```bash
git add WhatsappApp/Pages/ConnectionPage.xaml.cs WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/Converters/Converters.cs
git commit -m "fix(app): make the screen request harmless and stop ConvertBack from throwing"
```

---

### Task 6: Ask this phone what it can actually do

**Files:**
- Create: `WhatsappApp/Services/SelfCheck.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj:100-108`
- Modify: `WhatsappApp/App.xaml.cs:63-66`

**Interfaces:**
- Consumes: `Diag.Ok(string)` and `Diag.Failed(string, Exception)` (Task 1), `CryptoHelper.Encrypt(byte[])`
  / `CryptoHelper.Decrypt(byte[])` (existing, static), `DiscoveryService.Instance.StartAsync()` /
  `.IsListening` (existing).
- Produces: `SelfCheck.RunAsync()` — a fire-and-forget probe; it returns nothing and never throws.

- [x] **Step 1: Write the probe**

Create `WhatsappApp/Services/SelfCheck.cs`:

```csharp
using System;
using System.Text;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Verifica all'avvio, solo in DEBUG, che le tre cose di piattaforma da cui
    /// l'app non puo' prescindere esistano davvero su questo telefono.
    ///
    /// Serve perche' compilare non lo dimostra: la proiezione WinRT di WP8.1
    /// elenca dei membri che a runtime possono rispondere "non implementato", e
    /// da un Mac non c'e' modo di accorgersene. Il risultato finisce nel log con
    /// la forma di Diag, cosi' un giro di debug dice tutto in tre righe:
    ///
    ///     DIAG ok: crypto AES-256-GCM
    ///     DIAG ok: schermo sempre acceso (DisplayRequest)
    ///     DIAG ok: beacon UDP in ascolto sulla porta 8587
    ///
    /// e al posto di una riga "ok" una riga con il guasto e il suo HRESULT.
    /// </summary>
    public static class SelfCheck
    {
        /// <summary>Non restituisce niente e non lancia: e' un messaggio nel log.</summary>
        public static async void RunAsync()
        {
            CheckCrypto();
            CheckScreenRequest();
            await CheckDiscoveryAsync();
        }

        /// <summary>
        /// Il cifrario del canale: se GCM non e' implementato qui, il socket non
        /// puo' funzionare e la cosa va saputa subito, non alla prima schermata
        /// vuota.
        /// </summary>
        private static void CheckCrypto()
        {
            try
            {
                byte[] probe = Encoding.UTF8.GetBytes("whatsapp-wp8");
                byte[] frame = CryptoHelper.Encrypt(probe);
                byte[] back = CryptoHelper.Decrypt(frame);

                bool equal = back != null && back.Length == probe.Length;
                for (int i = 0; equal && i < probe.Length; i++) equal = back[i] == probe[i];

                if (equal) Diag.Ok("crypto AES-256-GCM");
                else Diag.Failed("SelfCheck.crypto",
                    new InvalidOperationException("il giro di andata e ritorno non torna"));
            }
            catch (Exception ex)
            {
                Diag.Failed("SelfCheck.crypto", ex);
            }
        }

        /// <summary>Lo schermo acceso mentre si inquadra il codice.</summary>
        private static void CheckScreenRequest()
        {
            try
            {
                var request = new Windows.System.Display.DisplayRequest();
                request.RequestActive();
                request.RequestRelease();
                Diag.Ok("schermo sempre acceso (DisplayRequest)");
            }
            catch (Exception ex)
            {
                Diag.Failed("SelfCheck.screen", ex);
            }
        }

        /// <summary>L'ascolto dei beacon: e' quello che fa trovare il server da soli.</summary>
        private static async System.Threading.Tasks.Task CheckDiscoveryAsync()
        {
            try
            {
                await DiscoveryService.Instance.StartAsync();
                if (DiscoveryService.Instance.IsListening) Diag.Ok("beacon UDP in ascolto sulla porta 8587");
                else Diag.Failed("SelfCheck.discovery",
                    new InvalidOperationException("la porta UDP non si e' aperta"));
            }
            catch (Exception ex)
            {
                Diag.Failed("SelfCheck.discovery", ex);
            }
        }
    }
}
```

- [x] **Step 2: Register the file**

In `WhatsappApp/WhatsappApp.csproj`, in the `Services` block, insert after `Services\Loc.cs` and
before `Services\SessionService.cs`:

```xml
    <Compile Include="Services\SelfCheck.cs" />
```

- [x] **Step 3: Run the probe at start-up**

In `WhatsappApp/App.xaml.cs`, replace:

```csharp
            // Il loader delle risorse non si puo' creare da un thread di
            // background: lo si crea qui, una volta, sul thread UI.
            Loc.Prewarm();
```

with:

```csharp
            // Il loader delle risorse non si puo' creare da un thread di
            // background: lo si crea qui, una volta, sul thread UI.
            Loc.Prewarm();

#if DEBUG
            // Solo in debug: dice in tre righe cosa questo telefono sa fare
            // davvero, invece di lasciarlo scoprire da un catch silenzioso. In
            // rilascio non esiste, quindi non costa niente all'avvio.
            SelfCheck.RunAsync();
#endif
```

`App.xaml.cs` already has `using WhatsappApp.Services;`, so `SelfCheck` resolves without a new using.

- [x] **Step 4: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js`
Expected: four `OK:` lines, the first with `27 C# file(s)`.

- [x] **Step 5: Commit**

```bash
git add WhatsappApp/Services/SelfCheck.cs WhatsappApp/WhatsappApp.csproj WhatsappApp/App.xaml.cs
git commit -m "feat(app): probe the platform at start-up instead of guessing what it supports"
```

---

### Task 7: Say it in the skills, then prove it on the phone

**Files:**
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (file map, `tools/` block; the gotchas list)
- Modify: `.agents/skills/test-the-app/SKILL.md` ("What can and cannot be verified here", the
  on-device checklist)

**Interfaces:**
- Consumes: everything above.
- Produces: the rule that keeps this from coming back, and a recorded result.

- [x] **Step 1: Put the two new services on the map**

In `.agents/skills/maintain-the-app/SKILL.md`, in the `WhatsappApp/` tree block, replace:

```
  Services/             CommunicationService (socket), DataService (state),
                        CryptoHelper (AES-GCM), Loc (strings), ImageHelper,
                        SettingsService, SessionService (last section)
```

with:

```
  Services/             CommunicationService (socket), DataService (state),
                        CryptoHelper (AES-GCM), Loc (strings), ImageHelper,
                        SettingsService, SessionService (last section),
                        Diag (every failure we survive, with its HRESULT),
                        SelfCheck (DEBUG-only probe of the platform)
```

- [x] **Step 2: Write the rule down**

In the same file, in **Known gotchas**, after the bullet about `.tools/`, add:

```
- **A silent `catch` is a bug.** Every failure the app decides to survive goes through
  `Diag.Failed("<call site>", ex)` before it is handled: the WP8.1 projection can reject a call at
  runtime that compiled fine, and "The operation identifier is not valid" in the debugger output does
  not say which call it was. `Diag` prints once per site with the HRESULT, and `Debug.WriteLine` is
  compiled out of release builds, so it costs nothing to ship.
- **Do not retry a lookup that has already failed.** `Loc.Loader` and `GetUiDispatcher` each remember
  their failure; without that flag a single failure becomes one exception per string, or per received
  message. `Loc.Prewarm()` is the only place allowed to clear the flag, because it is the only one
  that runs on the UI thread on purpose.
```

- [x] **Step 3: Record what the probe is for**

In `.agents/skills/test-the-app/SKILL.md`, in the section **What can and cannot be verified here**,
after the sentence about the XDE images needing Hyper-V, add:

```
One thing the build cannot tell you either is whether the phone implements the platform members the
app compiled against: `SymmetricAlgorithmNames.AesGcm`, `DisplayRequest` and `DatagramSocket` are all
in the WP8.1 reference metadata, and a member that is only declared answers `E_NOTIMPL` at run time.
`SelfCheck` (DEBUG builds, right after `Loc.Prewarm()`) prints one `DIAG ok:` line per capability, or
`DIAG <where>: <type> 0x<HRESULT> <message>` — that line is the fastest answer to "why is the screen
empty".
```

And in the on-device checklist, after item 2, insert:

```
3. Watch the Output window at start-up: three `DIAG ok:` lines (crypto, screen request, UDP beacon)
   and no `DIAG` line reporting a failure.
```

renumbering the following items (the old 3 becomes 4, and so on to the end).

- [x] **Step 4: Run every gate**

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict \
  && node tools/check-docs.js && node tools/qr-term.js --self-test && (cd WhatsappBridge && npm test)
```

Expected: four `OK:` lines, `Tutti i controlli sono passati.`, `pass 36`, `fail 0`.

- [x] **Step 5: Build on the Windows machine**

From the Mac, with the Parallels VM `Windows 11` running:

```bash
prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"
prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"
prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m"
```

Expected: `COPIA=0`, then the two known warnings (`CS0618` in `ChatPage.xaml.cs`, `CS4014` in
`ConnectionPage.xaml.cs`) and `0 Error(s)`, with
`WhatsappApp_1.0.1.0_x86_Debug.appxbundle` produced.

- [ ] **Step 6: Run it on the device and read the three lines**

Deploy and start the app with the debugger attached, with the adapter running on the Mac
(`node tools/start-login.js --no-qr`), then look at the Output window.

Expected, in the first seconds:

```
DIAG ok: crypto AES-256-GCM
DIAG ok: schermo sempre acceso (DisplayRequest)
DIAG ok: beacon UDP in ascolto sulla porta 8587
```

and no more than a few `DIAG ... 0x...` lines, each naming a call site. Write the exact lines that
appeared into this plan, under the Task 7 checkbox, before committing: they are the only evidence this
feature ever ran.

- [ ] **Step 7: Decide from what the log says**

- **`SelfCheck.crypto` reports a failure (E_NOTIMPL or similar).** The AES-256-GCM transport cannot
  work on this device. That is a **separate plan**: `WhatsappBridge/crypto-helper.js` and
  `CryptoHelper.cs` must agree on a second algorithm (AES-256-CBC + HMAC) selected at connect time, it
  needs tests on both sides, and it touches the wire format. Do not start it in this plan.
- **`SelfCheck.screen` reports a failure.** Expected on some images; the QR still shows, because
  Task 5 step 1 removed the constructor from outside the `try`. Nothing further to do.
- **`SelfCheck.discovery` reports a failure.** The beacon is not reaching the app; the manual address
  still works. Check that the manifest still declares `privateNetworkClientServer` and that the Mac and
  the phone are on the same network, then re-read `DiscoveryService.StartAsync`.
- **Nothing but `DIAG ok:` lines.** The two `NotImplementedException` lines were the converters
  (fixed in Task 5) and the `0x800710DD` burst came from the retry loops (fixed in Task 2). Done.

- [x] **Step 8: Commit**

```bash
git add .agents/skills/maintain-the-app/SKILL.md .agents/skills/test-the-app/SKILL.md \
  docs/superpowers/plans/2026-09-25-runtime-failures-naming-and-fixing.md
git commit -m "docs: record what the runtime probe says and how to read it"
git push origin master
```

---

## Self-Review

**1. Spec coverage**

| Requirement from the pasted log | Task |
| --- | --- |
| "The operation identifier is not valid" (about 27 times) with no caller named | Task 1 (the sink) + Tasks 2, 3, 4, 5 (every site that swallowed it now names itself) |
| The two `NotImplementedException` lines | Task 5 step 4 (the only ones this project can throw) + Task 6 (a probe that tells ours from the platform's) |
| Not knowing what the phone implements | Task 6 |
| Not repeating the same mistake | Task 7 steps 1-3 |

Nothing in the log is left without a task, and no task claims to fix what it cannot prove.

**2. Placeholder scan**

No "TBD", no "handle edge cases", no "similar to Task N": every step carries its code or its exact
command and expected output. The only conditional text is Task 7 step 7, which is a *decision table*
with a named outcome per line, plus the explicit instruction to write the real log lines back into the
plan — that is a result to record, not a step left unwritten.

**3. Type consistency**

- `Diag.Failed(string, Exception)` is called with exactly that shape in Tasks 2, 3, 4, 5, 6; `Diag.Ok(string)`
  and `Diag.Describe(Exception)` are used only by `SelfCheck` and by no one else.
- `TryGetDispatcher(Func<CoreDispatcher>, string)` is private to `CommunicationService` and is only
  called from `GetUiDispatcher`; the `delegate { ... }` form is C# 5 and the `??` chain returns
  `CoreDispatcher`.
- `SelfCheck.RunAsync()` is `async void`, called once from `App.OnLaunched` under `#if DEBUG`; it is
  never awaited anywhere, which is why it takes no parameters and returns nothing.
- `Loc.Prewarm()` keeps its signature and its single call site. `Loc.Get(string, string)` is unchanged.
- The `where` strings in Task 3 step 1 match the method names so a log line can be found by method
  name; the two `dispose` variants use `<method>/dispose` and `<method>/handler`, which no other task
  uses.

## What execution changed about the plan

Two things were learnt while running it, and both are in the code as committed:

1. **`Window.Current` is not a dispatcher fallback.** As written, step 3 of Task 2 did not compile
   (`CS0103`, `Window` is not in scope in `CommunicationService.cs`), and qualifying it would have
   been worse: `Window.Current` returns null off the UI thread, so the "fallback" would have produced
   a `NullReferenceException` instead of a dispatcher. The third source is gone, and the real fix for
   "no dispatcher on a network thread" is to resolve it on the UI thread while there is one:
   `CommunicationService.Prewarm()`, called from `OnLaunched` next to `Loc.Prewarm()` (commit
   `65fdc5a`).
2. **The diagnostic label must not look like a lookup.** `Diag.Failed("Loc.Get(" + key + ")", ex)`
   tripped `tools/check-resw.js`, which reads every `Loc.Get("...")` in the C# sources - comments
   included - as a resource key. The label is `"Loc.Get key " + key`, and the code carries a comment
   saying why, so the next person does not re-introduce it.

The rest ran as written: 27 C# files, four guards green, and the Windows build ending in the two known
warnings and `WhatsappApp_1.0.1.0_x86_Debug.appxbundle`.
