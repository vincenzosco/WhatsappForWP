---
name: maintain-the-app
description: Constraints and safe workflow for changing the WhatsApp for Windows Phone 8.1 codebase (C# 5 only, vector icons, resw localization, GOWA adapter, bilingual docs). Use before editing any file under WhatsappApp/, WhatsappBridge/ or any README, when a build fails, or when deciding where a change belongs.
---

# Maintaining the app

## What this is

An unofficial WhatsApp client for **Windows Phone 8.1**, built in Visual Studio
2015 with the WP8.1 SDK, that talks to a self-hosted **GOWA**
(go-whatsapp-web-multidevice) server through a thin Node.js adapter.

```
WhatsappApp/            WP8.1 XAML app (C# 5)
  App.xaml(.cs)         colours, start page, resource loader warm-up
  Controls/SectionNav   the shared bottom navigation bar
  Pages/                ChatsPage, StatusPage, CallsPage, ChatPage, ConnectionPage
  Converters/           IValueConverter implementations used by the XAML
  Models/               Contact, ChatMessage, ServerConfig
  Services/             CommunicationService (socket), DataService (state),
                        CryptoHelper (AES-256-CBC + HMAC), Loc (strings), ImageHelper,
                        SettingsService, SessionService (last section),
                        Diag (every failure we survive, with its HRESULT),
                        SelfCheck (DEBUG-only probe of the platform)
  Strings/<lang>/       Resources.resw - every user-visible string
  Assets/               generated PNGs (tiles, logos, splash)
WhatsappBridge/         Node.js adapter: GOWA HTTP + webhook -> encrypted TCP frames
WhatsappServer/         legacy .NET console project (not part of the app flow)
tools/                  static guards - run them, they are the real gate
  start-login.js        starts GOWA + the adapter and draws the login QR
  qr-term.js            PNG -> terminal QR (module recovery + half blocks)
  check-docs.js         the two languages of the docs stay in step, no emoji
README.md / README.it.md                project docs, English + Italian
WhatsappBridge/README.md / .it.md       adapter docs, English + Italian
.agents/skills/         this directory
.tools/                 local, git ignored: the GOWA binary, its log and
                        storages/whatsapp.db (the live WhatsApp session)
```

## Hard constraints

1. **C# 5.** The WP8.1 toolchain compiler rejects C# 6/7 syntax:
   `$"..."`, `?.`, `get => x`, `X Y { get; set; } = v;`, `out int x`,
   `is Contact c`, `nameof(...)`, `_ = ...`.
   Gate: `node tools/check-csharp5.js` (it also flags WP8.1-missing WinRT APIs
   such as `CryptographicBuffer.CreateFromByteArray(byte[], uint, uint)` and
   `ContentDialog.CloseButtonText`).
2. **No icon font.** WP8.1 predates `Segoe MDL2 Assets`; an icon button using it
   renders blank. Icons are `Path` elements with the geometry **inlined** as
   `<Path.Data><PathGeometry>...</PathGeometry></Path.Data>`, each named by an
   `<!-- IconX -->` comment above its `Path.Data`.
   Two forms are forbidden, both because they break on this toolchain:
   `Data="{StaticResource IconX}"` (a `Geometry` is not shareable through a
   `StaticResource` in WinRT - compiles, then throws
   `Failed to assign to property 'Windows.UI.Xaml.Shapes.Path.Data'` at
   runtime; microsoft-ui-xaml#1909 / #5780) and `Figures="M..."` (WP8.1's
   `PathFigureCollection` converter has no string form, 18 build errors).
   Gate: `node tools/check-icons.js` (add `--preview` for an ASCII render).
3. **No hardcoded user-visible strings.** XAML uses `x:Uid` with the property
   that matches the element (`TextBlock`→`.Text`, `Button`→`.Content`,
   `TextBox`→`.PlaceholderText`); C# uses `Loc.Get("Key", "fallback")`. Icon-only
   buttons get their label from `ToolTipService.SetToolTip(button, Loc.Get(...))`
   in the constructor - an `x:Uid` on them would overwrite the `Path`.
   Gate: `node tools/check-resw.js --strict`.
4. **Never create a `ResourceLoader` off the UI thread.** Strings arrive from the
   socket on a background thread; `Loc.Prewarm()` runs on the UI thread at
   startup and `Loc.Get` falls back to its literal instead of throwing.
5. **Back navigation.** Do not subscribe to `HardwareButtons.BackPressed` to
   reimplement Back; the system pops the frame back stack and exits at the root.
   `SectionNav` trims the section page it leaves, so Back exits from any section.
6. **The adapter is a separate, dependency-free Node.js program** (`package.json`
   has zero runtime dependencies). Its tests must keep passing.
7. **One page per section.** A new screen means a new file under `Pages/`, not
   another block inside an existing page, and it must be registered in the
   `.csproj` (a page that is not listed does not exist at build time).
8. **Runtime text is English.** Everything a person reads *outside the app UI* is
   English: the adapter's log and error messages, the `text:` frames it sends for
   display, the app's `Diag`/`SelfCheck` lines (with every message inside an
   exception that can reach them), the launcher `tools/start-login.js`,
   `qr-term.js` and `download.js`, the service-list reasons, and the legacy relay's
   console text. An operator reading a container log or a debugger window needs no
   second language. Only the app UI is localized, through the `.resw` pairs;
   source comments and the adapter's test names stay Italian, because nobody reads
   them at run time.
9. **The docs are written in pairs, English and Italian.** `README.md` and
   `README.it.md` are versions of each other, and so are
   `WhatsappBridge/README.md` and `WhatsappBridge/README.it.md`: a section is
   added, moved or renamed in **both**, in the same commit, with the same heading
   depth and order, and each links to the other. `## Disclosure` (open source,
   maintainers wanted, written by an AI agent, no responsibility for the account
   used) must be the **last** section of the README that presents the project,
   in both languages. New explanatory documents are born as a pair. No emoji: the
   warning sign (U+26A0) is the only exception, for a real hazard.
   Gate: `node tools/check-docs.js`.

## Showing only data that exists

A screen may only show data that exists upstream. Two facts this project
established the hard way:

- **GOWA has no status endpoint.** Nothing in its API or its webhooks exposes
  status updates, so the Status section is empty on purpose and says so, rather
  than pretending the feature is missing from the app.
- **GOWA records incoming calls only** (`CreateIncomingCallRecord`), in its own
  chat storage; the Calls section scans the most recent chats and says so in its
  empty state and in the README pair.
- **The chat list is `GET /chats`, not `GET /user/my/contacts`.** The second one
  is the WhatsApp *address book*: on a freshly linked device it is empty while
  `/chats` is full, which is exactly how the chat list came up blank. Profile
  pictures come from `GET /user/avatar?phone=<digits>&is_preview=true`, and only
  for people: groups have none.
- **Notifications can only be raised while the app runs.** WP8.1 suspends the
  app, which closes the TCP socket, and this project has no cloud service to push
  through, so a toast can only be raised for a message that arrives while the app
  is in the foreground. The README pair's Limiti/Limitations section says so.
- **The contact picker is the user's consent.** `ContactPicker` shows the system
  UI and returns what the user taps; the app never enumerates the address book,
  which would need the `contacts` capability and a privacy story this project does
  not want.

The rule that follows: when a source has a limit, the screen and the README pair
state it, and **no screen invents state that no server sends** - presence, "online",
"last seen at" were all removed for exactly that reason.

## Workflow for any change

1. `git status --short` - start from a clean tree.
2. Read the file you are about to change **completely**; this codebase keeps
   per-file invariants in comments.
3. Make the change.
4. Run all four guards (and `cd WhatsappBridge && npm test` if you touched the
   adapter).
5. If the change is user-visible, say which page and which string key changed.
6. Commit with a message that says *why* (the repo history is the changelog).

## Where a change belongs

| Change | File |
| --- | --- |
| New section of the app | new `Pages/XxxPage.xaml(.cs)` + a case in `SectionNav` |
| New icon | inline `<Path.Data><PathGeometry>` on the `Path` (never a resource) |
| New string | both `.resw` files (same key), then `x:Uid`/`Loc.Get` |
| Socket/protocol behaviour | `Services/CommunicationService.cs` (+ adapter + its tests) |
| Contacts/messages state | `Services/DataService.cs` (keep `_contactIndex` in sync) |
| State kept across suspend/termination | `Services/SessionService.cs` |
| Image handling | `Services/ImageHelper.cs` |
| A new GOWA call | `WhatsappBridge/gowa-client.js`, a control command in `server.js`, and the app side in `ConnectionPage`/`CommunicationService` |
| Local start-up behaviour (ports, login, stop) | `tools/start-login.js` (+ the `run-the-login-server` skill) |
| Drawing of the login QR | `tools/qr-term.js` (run its `--self-test` afterwards) |
| Anything a reader reads (README, guides) | the English file **and** its Italian pair, then `node tools/check-docs.js` |
| A claim about the project (open source, maintainers, AI-written, responsibility) | `## Disclosure` at the end of `README.md` **and** `README.it.md` |

## Known gotchas

- The app cannot be built on macOS: there is no WP8.1 toolchain. The build gate
  runs on the Windows/Parallels machine.
- `x:Uid` on a `Button` overwrites `Content`; do not combine it with a `Path`.
- A key and the same key with a `.Property` suffix cannot coexist in a `.resw`
  (duplicate resource identifier) - the guard enforces this.
- WP8.1 caches the tile name and icons: after changing them, uninstall the app on
  the device before redeploying.
- **The tile templates that exist on WP8.1 are not the Windows ones.**
  `TileSquare150x150IconWithBadge` and `TileSquare71x71IconWithBadge` do exist and
  are what `NotificationService` uses; `TileWide310x150IconWithBadge` does
  **not**, and naming it is a compile error (CS0117), not a silent no-op. This is
  a phone-only SDK: a template that compiles here may still be unsupported at run
  time, so a new tile format is a device check, not a build check.
- **On WP8.1 the number on the tile is drawn by the *badge*, not by the tile.**
  A tile notification only replaces the tile's content, which is why the
  `IconWithBadge` templates exist: they put the app icon back while the badge does
  the counting. `Clear()` on both updaters is what returns the tile and the icon
  to the manifest's defaults.
- **`CommunicationService.IsConnected` can be true on a dead socket.** Nothing
  observes a socket that the OS closed, so a suspended-then-resumed app looks
  connected and is mute. `LastInboundUtc` plus `ConnectionWatchdog` are the only
  things that notice. If you add a way to reconnect, remember that
  `AutoConnector.TryConnectAsync` returns immediately while `IsConnected` is true:
  a dead connection has to be `Disconnect()`ed before it will be retried.
- `Frame.BackStack` is mutable and used on purpose in `SectionNav`.
- `DataService.Contacts` is a public collection: if something adds a contact
  without `AddContact`, `FindContact` rebuilds its index, so keep inserts going
  through `AddContact` when you can.
- `ChatMessage.LoadMediaImageAsync` is a no-op once `MediaImage` is set; do not
  "fix" that by forcing a re-decode.
- Nothing under `.tools/` is committed, and the GOWA login flow **restarts** every
  time `GET /app/login` is called: see `run-the-login-server` before touching
  `tools/start-login.js` or the login frames of the adapter.
- **A silent `catch` is a bug.** Every failure the app decides to survive goes
  through `Diag.Failed("<call site>", ex)` before it is handled: the WP8.1
  projection can refuse a call at run time that compiled fine, and "The operation
  identifier is not valid" in the debugger output does not say which call it was.
  `Diag` prints once per site with the HRESULT, and `Debug.WriteLine` is compiled
  out of release builds, so shipping it costs nothing.
- **Do not retry a lookup that has already failed.** `Loc.Loader` and
  `GetUiDispatcher` each remember their failure; without that flag one failure
  becomes one exception per string, or per received message. What they need
  instead is to be resolved once at start-up on the UI thread: `Loc.Prewarm()` and
  `CommunicationService.Instance.Prewarm()`, both called from `OnLaunched`.
- **A connection is owned by the attempt that opened it.** `ConnectToServerAsync`
  takes the next `_connectionId`, keeps its socket/reader/writer in locals,
  publishes them only while that id is still current, and hands the reader to
  `ListenForMessagesAsync(attempt, reader)`. Never read `_reader` from a loop and
  never let a failing attempt call cleanup on the published fields: two readers on
  one `DataReader` desync it, and the next length read is a slice of JSON.
- **A frame length is not trusted, and its byte order is never left to a
  default.** `ReadFrameAsync` fills the 4-byte prefix fully
  (`InputStreamOptions.Partial` can split it) and rejects anything outside
  `1..MaxFrameLength` (8 MiB). The length is little-endian on the wire
  (`writeUInt32LE`/`readUInt32LE`), but WinRT's `DataReader`/`DataWriter` do not
  default to that: build them only through `CreateFrameReader`/`CreateFrameWriter`
  in `CommunicationService.cs`. A byte-swapped length does not throw - it reads a
  number that looks like a corrupt frame (`0x00000121` came back as `0x21010000`,
  553713664) and closes the connection right after a successful connect. The
  adapter's `MAX_FRAME_LENGTH` is the same 8 MiB: change one and you must change the
  other. Gate: `node tools/check-framing.js`.
- **`0x8007274C` is `WSAETIMEDOUT`, not a crypto or login failure.** It means
  `ConnectAsync` never got an answer; the handshake and the QR never ran. Check the
  address first: `AutoConnector` tries `SettingsService.ServerAddress` before
  discovery, so a stale IP shows up here. `StreamSocket` has no timeout, which is
  why `ConnectWithDeadlineAsync` closes the socket after 6 seconds.
- **`Window.Current` is null off the UI thread**, so it cannot be a fallback for
  anything that may run on a network thread: use `CoreApplication.MainView` there,
  or capture the object while you are on the UI thread.
- **A `[DataMember]` with a strict type is a whole-frame failure waiting to happen.**
  One unexpected string in one field makes `DataContractJsonSerializer` throw
  `SerializationException 0x8013150C` for the entire object, and `ChatMessage.FromJson`
  returns `null`: the message disappears with no visible cause. `Timestamp` is the field
  that bit us (the adapter wrote a backslash-escaped `\/Date(ms)\/` instead of
  `/Date(ms)/`), so it is a `string` on the wire and a leniently parsed `DateTime` in
  the app. When adding a field, ask what the deserializer does with a value it did not
  expect.
