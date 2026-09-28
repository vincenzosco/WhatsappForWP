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
   It also refuses to `await` inside a `catch` block (CS1985, "Cannot await in
   the body of a catch clause"): take note of the failure in the catch and await
   after the block. That one is positional, not a property of the line, so the
   guard scans each `catch` body and flags a bare `await` (`await` inside a
   lambda in the block belongs to the lambda and is fine).
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
   The live tile is the one place where the icon is **not** a `Path`: the
   `TileSquare150x150IconWithBadge` model wants `<image src="..."/>` in the
   payload and does **not** fall back to the manifest logo. The asset is
   `Assets/TileIcon.png` (+ its `.scale-240`), a transparent PNG with no padding.
   Gate: `node tools/check-tile.js`.
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
  pictures come from `GET /user/avatar?phone=<JID>&is_preview=true`, for people
  **and** groups: the parameter is a JID (GOWA appends a suffix only to a value
  with no `@` in it, `SanitizePhone`), and the profile-picture lookup accepts any
  JID. Asking with the bare digits is what made every group show initials.
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

### "Read" is a decision, never an assumption

`DataService.UnreadCount` counts **every** incoming message. Nothing in the
counter asks which chat is open: an arriving message is unread until something
*dipslays* it. The only place that decides is `ChatPage`, which calls
`ClearUnread` when it opens the conversation and for each message it puts on
screen - exactly what WhatsApp does, and the reason the number disappears from a
row you are reading.

This is not a style preference. Excluding the active chat inside the counter (the
older shape) hid a real bug: with the app suspended on an open chat, messages
delivered on resume were never counted **and** never cleared, so they vanished
from both the row and the badge. `DataService.ActiveChatId` now has one job only -
suppressing the *toast* for the chat on screen.

## Workflow for any change

1. `git status --short` - start from a clean tree.
2. **A plan is written to be executed now.** When a change is multi-step enough to
   deserve one, write it to `docs/superpowers/plans/YYYY-MM-DD-<name>.md` and then
   execute it in the same session, task by task, until the last task is committed
   and pushed. The document is the record of the work, never the deliverable:
   stopping at the plan leaves the change undone and ships a description of code
   that does not exist. While executing, append a
   `## What execution changed about this plan` section at the end of the plan
   listing every divergence, and do not rewrite the tasks above it.
3. Read the file you are about to change **completely**; this codebase keeps
   per-file invariants in comments.
4. Make the change.
5. Run the fast gate: `node tools/check-csharp5.js && node tools/check-icons.js &&
   node tools/check-resw.js --strict && node tools/check-docs.js &&
   node tools/check-framing.js && node tools/check-tile.js &&
   node tools/check-memory.js && node tools/check-actions.js`, plus
   `node --test "tools/test/**/*.test.js"` and `cd WhatsappBridge && npm test`.
6. If the change is user-visible, say which page and which string key changed.
7. Commit with a message that says *why* (the repo history is the changelog).
8. **Push.** A change is finished only when it is on `origin/master`: a commit
   that lives on this machine alone is invisible to everyone else, so every
   change ends with `git push`, not with the commit. Never leave the branch
   ahead of `origin/master`. A change under `WhatsappBridge/` is not finished
   either until it is mirrored into the Docker repository - see *The Docker
   repository* below.

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
| A change to the adapter (`WhatsappBridge/`) | the same change in the Docker repository (`docker-whatsappforwp`): run its `tools/sync.js`, commit and push - see *The Docker repository* |
| Local start-up behaviour (ports, login, stop) | `tools/start-login.js` (+ the `run-the-login-server` skill) |
| Drawing of the login QR | `tools/qr-term.js` (run its `--self-test` afterwards) |
| Anything a reader reads (README, guides) | the English file **and** its Italian pair, then `node tools/check-docs.js` |
| A claim about the project (open source, maintainers, AI-written, responsibility) | `## Disclosure` at the end of `README.md` **and** `README.it.md` |

## The Docker repository

`WhatsappBridge/` is the source of truth for the adapter; the repository
`vincenzosco/docker-whatsappforwp` holds a **copy** of it in `server/`, plus the
`Dockerfile` that puts GOWA and the adapter in one image. That image is what most
people run, so an adapter change that stops at this repository is a change half
the users never get. **Every commit that touches `WhatsappBridge/` ends with the
same commit mirrored into the Docker repository.**

The mirror is not a patch. `tools/sync.js` **in the Docker repository** reads the
list of adapter files out of this checkout (its `adapterFiles`), so a new module
cannot be forgotten, and writes `server/SOURCE_COMMIT` with the commit the copy
came from.

```bash
# 1. here: the adapter change, its tests, the commit, the push
cd WhatsappBridge && npm test
git push

# 2. in a checkout of the Docker repository
#    (git clone https://github.com/vincenzosco/docker-whatsappforwp /tmp/docker-whatsappforwp)
cd /tmp/docker-whatsappforwp
git pull
node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP
node tools/sync.js --check --from /Users/vincenzo/Documents/WhatsappForWP  # OK: server/ matches ...
(cd server && npm test)                                                    # same count as step 1
git add -A && git commit -m "fix: ..." && git push
```

What makes it *pass*: the `sync` job of `.github/workflows/image.yml` checks this
repository out at the commit `server/SOURCE_COMMIT` names, runs
`node tools/sync.js --check --from ../app`, and fails the build if the copy no
longer matches. A mirrored commit that missed a file therefore turns the image
build red instead of shipping an old adapter. After the push, the workflow builds
linux/amd64 and linux/arm64 and publishes
`ghcr.io/vincenzosco/docker-whatsappforwp:latest`.

Two things the copy cannot carry, because they belong to the container and not to
the adapter:

- **An external program.** The runtime stage of the `Dockerfile` installs
  `ffmpeg`, which the adapter calls for Ogg/Opus voice notes; the Node.js path in
  the Docker README tells the reader to install it by hand. Anything new the
  adapter starts executing needs the same treatment, or it is present when run
  from a checkout and missing in the image most people use.
- **A new environment variable**, in `.env.example` and in **both** Docker README
  tables, or it is an option nobody can set. Both compose files pass `.env`
  through with `env_file`, so there is nothing else to wire.

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
- **On WP8.1 a theme minimum overrides the size you declare.** The default
  `Button` style sets `MinWidth = PhoneButtonMinWidth = 109` and
  `MinHeight = PhoneButtonMinHeight = 57.5` (the phone kit's `generic.xaml` and
  `themeresources.xaml`), and a minimum beats `Width`/`Height`: an icon button
  written `Width="48" Height="48"` is really drawn 109 x 57.5. A fixed-size
  button must therefore also declare `MinWidth="0" MinHeight="0"`, or the `Auto`
  column around it grows past the drawing and squeezes its neighbour - that is how
  the chat list title lost its last letters when the three-dots button was added,
  and why the message box of a chat was 122 px narrower than drawn.
  `tools/check-actions.js` refuses a button that declares a `Width` without the
  two minimums, and it refuses the same omission in a `Style` with
  `TargetType="Button"` that declares a `Width` or a `Height` (a style holds for
  every button that uses it, so it cannot know which column they land in and must
  be safe on both axes; `NavIconButtonStyle` in `SectionNav.xaml` is the one that
  exists). A style with `BasedOn` is left alone, because the base holds the
  setters and may live in another file. Buttons that declare only a `Height` in
  their own markup (the connection page Connect/Disconnect/login buttons) are
  outside the rule on purpose: they are full width and nothing sits next to them,
  so the extra height costs nothing.
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
- **The app's memory is not the conversation.** Opening a chat used to show only
  what had arrived while the app was running, because nothing ever asked the
  adapter for what was already on the server: a receive path is not a read path.
  `ChatPage.OnNavigatedTo` asks (`messages`, with the chat JID in `Text`, the same
  field `login.code` carries its number in) once per chat per process -
  `DataService.MarkHistoryRequested` - and the adapter answers with ordinary
  message frames carrying `IsHistory`. The flag matters: without it every old
  message would count as unread and raise a toast. The frames are inserted by
  date, not appended, because the adapter does not promise an order (see
  `newestMessage` in `WhatsappBridge/chats.js`). Dedup is by WhatsApp's own
  message id, so a chat can be requested again without duplicating what is already
  on screen, and a message the server has no id for is dropped rather than risked
  twice. History media has no bytes - it is not among what the webhook delivered -
  so it travels as text (`[Image]`, `[Video]`): a frame of type Image with no
  `MediaData` would draw an empty bubble.
- **A `[DataMember]` with a strict type is a whole-frame failure waiting to happen.**
  One unexpected string in one field makes `DataContractJsonSerializer` throw
  `SerializationException 0x8013150C` for the entire object, and `ChatMessage.FromJson`
  returns `null`: the message disappears with no visible cause. `Timestamp` is the field
  that bit us (the adapter wrote a backslash-escaped `\/Date(ms)\/` instead of
  `/Date(ms)/`), so it is a `string` on the wire and a leniently parsed `DateTime` in
  the app. When adding a field, ask what the deserializer does with a value it did not
  expect.
- **On WP8.1 a file picker is not awaited, it is continued.**
  `FileOpenPicker.PickSingleFileAsync` is documented as unsupported on Windows
  Phone, for both Windows Runtime and Silverlight; on the phone it throws, the
  catch logs it, and the button looks dead. The supported call is
  `PickSingleFileAndContinue()`, which deactivates the app and delivers the file
  to `App.OnActivated` as `ActivationKind.PickFileContinuation` with a
  `FileOpenPickerContinuationEventArgs`. Because the process can be terminated
  while the picker is open, the result never goes to a page instance:
  `AttachmentInbox` reads the bytes at reactivation, and whichever page shows an
  attachment takes them, through its `Ready` event and its `HasAttachment` check
  on navigation. `ShareOperation` lives in
  `Windows.ApplicationModel.DataTransfer.ShareTarget`, not in
  `Windows.ApplicationModel.DataTransfer`.
- **A share target goes in the manifest's default namespace, with no prefix.**
  `ShareTarget`, `SupportedFileTypes`, `FileType` and `DataFormat` are declared by
  `AppxManifestSchema2010_v2.xsd` in `http://schemas.microsoft.com/appx/2010/manifest`
  - the `xmlns` of this manifest - even though `VisualElements` is `m3:`
  (`appx/2014/manifest`). With `m3:` the build answers `APPX3030` ("must be a valid
  application extension category") and `APPX3002` (unrecognized element), which
  reads like an unsupported feature and is only a namespace mistake.
- **GOWA's `/user/avatar` returns an address, not an image.** `AvatarResponse` is
  `{url, id, type}`, so the picture is a second request to the CDN URL. Encoding
  the first response as if it were a bitmap produces base64 that no decoder
  accepts, and the failure is silent: every row falls back to initials. The same
  shape of trap is in `/user/my/groups`: whatsmeow's `types.GroupInfo` has no json
  tags and embeds `GroupName`, so `encoding/json` promotes the fields and the
  subject arrives as a top-level `Name`. GOWA's own chat list has no usable name
  for a group and answers `Group <number>`.
- **The tile takes its icon from the payload, not from the manifest.** A
  `TileSquare150x150IconWithBadge` update whose `image/@src` is empty renders an
  iconless tile and raises nothing at all - no exception, no log, nothing in the
  Output window. `SetTileBadge` writes `ms-appx:///Assets/TileIcon.png` on every
  binding it sends, including the 71x71 one it imports, and creates the `image`
  element when the template does not ship one (create it with the destination
  document, or the insertion throws). `TileWide310x150IconWithBadge` does not exist
  on WP8.1 (CS0117), so the wide tile keeps the manifest's. Gate:
  `node tools/check-tile.js`.
- **A bitmap decodes at the size you ask for, and at no other size.**
  `BitmapImage.DecodePixelWidth` must be set before `SetSourceAsync` - after, it
  does nothing - and it is the difference between a 52 px circle costing a few tens
  of KB and costing almost 2 MB, once per conversation. Every decode goes through
  `ImageHelper.From*Async(base64, width)`; the QR decoder in `ConnectionPage` is
  the one deliberate exception, because a QR only scans at 1:1. Under pressure
  `MemoryWatcher` drops the decoded avatars and clears the history of the chats
  that are not open - and forgets `MarkHistoryRequested` with them, or a chat
  emptied that way stays empty forever. Note also that `AppMemoryUsageLevel` on
  WP8.1 has no `OverLimit` (CS0117): `High` is the top it can name. Gate:
  `node tools/check-memory.js`.
- **Two icon-only buttons next to each other are one edited line away from being
  swapped.** The rule is not style: a button named `X` is wired only to `X_Click`
  and draws the icon `tools/check-actions.js` declares for it. The two title-bar
  buttons keep a gap between them and carry `AutomationProperties.SetName` (the
  same key as their tooltip), so the device can answer which is which. Gate:
  `node tools/check-actions.js`.
- **A screen that is shown before the socket is up cannot ask for its data.**
  `ChatsPage.OnNavigatedTo` used to request the list only when
  `IsConnected && Contacts.Count == 0`; on a cold start neither is true, so the list
  stayed empty until the user walked through the settings page, which is what "I have
  to press Continue every time" was. The request now happens in `RequestChats()` -
  called on navigation and on every `state` frame - and it waits for
  `WhatsAppState == "connected"`, because the adapter answers "not connected" until
  the WhatsApp login is done. `ChatCache` keeps the last list on the phone so the
  screen is not empty while that happens: it is a photograph, replaced row by row by
  `ApplyChat`, and it holds no avatar bytes.
- **`ShareOperation` has an order, and on WP8.1 it has no `GetDeferral()`.** Report
  `ReportStarted()` first, `ReportDataRetrieved()` once the bytes are in, and
  `ReportCompleted()` at the end (or `ReportError()` in the catch); calling
  `ReportCompleted()` in a `finally` without ever calling `ReportStarted()` is the
  crash that sharing a photo produced. The plan wrapped the reads in
  `Windows.Foundation.Deferral` and `operation.GetDeferral()`; that type does not exist
  in the WP8.1 projection (the build answers `CS0234`/`CS1061`), and it is not needed -
  the share target's app is in the foreground, so the operation stays valid.
  `tools/check-csharp5.js` flags both `.GetDeferral(` and `Windows.Foundation.Deferral`,
  so the guard catches the reintroduction. A share target's file types are declared in the manifest's default namespace;
  a video type missing there means the app is not offered for it at all.
- **A frame is capped at 8 MiB and base64 adds a third.** An attachment therefore
  travels as `media.begin` / `media.chunk` / `media.end`, with each chunk a multiple of
  4 base64 characters so the bytes can be concatenated without re-encoding. The adapter
  picks GOWA's door from the MIME type (or the extension): `/send/image`, `/send/video`,
  `/send/file`. Before this, every attachment went through `sendImage`, so a video
  arrived as a broken image.
- **GOWA's `/message/:id/download` answers with an address, not bytes.** Like
  `/user/avatar`, it is two requests: `results.file_url` is the static file, fetched
  after. An empty `file_url` means the file is not under `statics`, which for the app is
  "no longer available", not a fault. The bytes go back in `media` control frames tied
  to the existing message by `RelatedMessageId` - a message frame would count as new and
  raise a toast.
- **Incoming media is chunked too, and a video goes to disk.** The same 8 MiB ceiling
  applies to what the adapter sends back, so `sendMediaChunks` splits the base64 into
  `MEDIA_CHUNK_CHARS` (700000) characters - the same number as `ChatPage.MediaChunkChars`
  - one `media` frame each, with `MediaChunkIndex` / `MediaChunkTotal` / `MediaType`. The
  webhook path uses it as well: a message with an id is announced first and its bytes
  follow, so nothing large rides inside a message frame. On the phone
  `IncomingMediaStore` reassembles by `RelatedMessageId`: a photo into `MediaData`, a
  video streamed into a file in `LocalFolder` (never whole in memory - that is what the
  512 MB budget buys) and referenced by `ChatMessage.MediaFilePath`, a client-only field.
  A `MediaElement` in `ChatPage` plays it from the `ms-appdata` file URI with
  `AreTransportControlsEnabled`. The play box shows on `ChatMessage.IsVideo`, which is
  true from `MediaType` alone, so it is tappable before the bytes arrive - the first tap
  downloads them, and `ChatMessage.IsMediaLoading` turns the ring in the bubble while
  they do.
- **WP8.1 has no Opus decoder.** WhatsApp voice notes are Ogg/Opus, and Opus only
  arrived on Windows 10: the phone can play MP3, AAC/M4A, AMR and WAV, and cannot
  play what WhatsApp actually sends. The adapter therefore detects `ffmpeg` once
  at startup (`WhatsappBridge/ffmpeg.js`, `bridge.probeFfmpeg()`) and converts an
  Ogg/Opus payload to mono 16 kHz 32 kbit/s MP3 before chunking it to the app.
  `ffmpeg` is an external program, not an npm dependency, and the adapter must
  work without it: `toPlayable` returns `null`, the original bytes are sent, and
  the app shows that the note cannot be played. `FFMPEG_ENABLED` and `FFMPEG_PATH`
  change it. Audio and documents are `MediaType` values, not `MessageType` values:
  `MessageType` has no `Document` and adding one would change what every older
  frame deserializes to, so `ChatMessage.IsAudio` and `ChatMessage.IsDocument`
  derive from `MediaType` the way `IsVideo` does. A downloaded document is opened
  with `Launcher.LaunchFileAsync` on the file in `LocalFolder`, so the extension
  must be the real one (it comes from the file name in `MediaFileName`).
- **A picked or shared file is copied, not read.** `AttachmentInbox.PutAsync` copies
  the chosen file into `LocalFolder` (`StorageFile.CopyAsync`) and keeps its name;
  `ChatPage.SendAttachmentAsync` streams it out in `MediaChunkBytes` (525000, a
  multiple of 3, so each piece is exactly 700000 base64 characters). Reading the file
  into a `byte[]` first is what took the app down when a video was shared from the
  gallery, and `tools/check-memory.js` now fails on it. A fire-and-forget deposit has
  no one to catch its exception: `App.DepositPickedFileAsync` catches its own.
- **The video viewer gets the `ms-appdata` file, not a stream.** Holding an
  `IRandomAccessStream` open for the life of the page means the viewer dies with the
  page, and a decode failure that closes the overlay is indistinguishable from a tap
  that did nothing. `ChatPage.PlayVideo` sets `Source` to `ms-appdata:///local/<name>`
  and leaves `VideoErrorText` up when `MediaFailed` fires. WP8.1 still has no `Deferral`
  here - see the `ShareOperation` gotcha.
- **A `Border` takes exactly one child.** Wrapping the play `Path` and the download
  `ProgressRing` inside the video `Border` is not enough - they need a `<Grid>` around
  them, or the XAML compiler answers `WMC0035: Duplication assignment to the Child
  property`.
- **A cache is a photograph, not a truth.** `MessageCache` holds the last 60 messages
  of a chat and is written on leaving the page, not per message. It is only read when
  the in-memory list is empty (`DataService.LoadCachedMessagesAsync`), and its rows go
  through `AddHistoryMessage`, so the dedupe and the date order are the same as the real
  history. Its entries are `IsHistory`, so they raise no toast and add no unread count.
  `MediaFilePath` is not a `[DataMember]`, so a cached video keeps its word and its play
  box but asks for its bytes again.
- **A pin, a silence and a deletion are this phone's, and the file that holds them
  is not the chat cache.** `ChatCache` is a photograph the server replaces row by
  row, so a decision kept there would be gone at the next `chats` reply and a chat
  that `CHATS_LIMIT` no longer lists would lose it. `ChatPreferences` is the other
  thing: `chat-preferences.json` in `LocalFolder`, read once at start-up (before
  the first row is built, in `DataService.LoadCachedChatsAsync`) and written by the
  caller that changes it. `Hidden` keeps a deleted chat out of `ApplyChat` and
  `ApplyContact`; a live incoming message calls `Reveal`, which is what makes a
  chat deleted by mistake come back - and is why nothing here is a one-way door.
  Muting suppresses the toast only: the unread count is still true.
- **A pinned chat is moved, not sorted.** `DataService.ResortContacts` walks the
  collection and moves each pinned row in front of the first row that is not
  pinned, so the rows keep the recency order the collection already had. Every
  path that moves a row to the top (`OnNetworkMessageReceived`, `AddMessage`) must
  call it afterwards, or an arriving message in an unpinned chat jumps over the
  pinned ones.
- **`Holding` fires twice.** `ChatRow_Holding` returns unless
  `e.HoldingState == HoldingState.Started`; without that test the menu is built
  again on the release, and a tap on a menu item that lands on the row re-opens it.
  The namespace is `Windows.UI.Xaml.Input`, not `Windows.UI.Input`.
- **The row's menu captures the id and the state before it is shown.** By the time
  a menu item is tapped the row may already be gone (delete), so the handlers close
  over the values the menu was built from instead of reading `contact` again. The
  loops that apply a pin snapshot `DataService.Contacts` first
  (`SnapshotContacts`): `SetPinned` moves rows inside that same collection.
- **A group has a picture like anyone else.** `GET /user/avatar` accepts a group
  JID: `SanitizePhone` only appends a suffix when the value has no `@`, and
  `GetProfilePictureInfo` takes the JID as it comes. So `chats.js` asks for a
  group's picture too, and `gowa-client.js.avatar()` keeps the whole JID (cutting
  `:device` off the front of the local part, not the `@g.us` off the end). Skipping
  `@g.us`, or cutting the JID to its digits, is what made groups show initials.
  `POST /group/photo` is the other direction - it sets a group's picture, it does
  not read one.
