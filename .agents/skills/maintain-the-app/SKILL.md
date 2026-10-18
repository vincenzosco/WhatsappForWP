---
name: maintain-the-app
description: Constraints and safe workflow for changing the WhatsApp for Windows Phone 8.1 codebase (C# 5 only, vector icons, resw localization, the native client and its protocol core, bilingual docs). Use before editing any file under WhatsappApp/ or any README, when a build fails, or when deciding where a change belongs.
---

# Maintaining the app

## What this is

An unofficial WhatsApp client for **Windows Phone 8.1**, built in Visual Studio
2015 with the WP8.1 SDK. The phone **is** the client: it speaks WhatsApp's own
protocol itself (see `Protocol/`), so there is no server to run - the account, the
Signal sessions and the media keys all live in the app's own folder.

```
WhatsappApp/            WP8.1 XAML app (C# 5)
  App.xaml(.cs)         colours, start page, resource loader warm-up
  Controls/SectionNav   the shared bottom navigation bar
  Pages/                ChatsPage, StatusPage, CallsPage, ChatPage, ConnectionPage, PairPage
  Converters/           IValueConverter implementations used by the XAML
  Protocol/             the wire itself: WABinary (nodes), Noise (the handshake),
                        Signal (X3DH, the double ratchet, the sender key), Media,
                        WaProto (protobuf), Qr (the pairing code)
  Protocol/Client/      the session: the login, the node classifier, the history
                        sync, sending, receipts, the call log, the status feed
  Models/               Contact, ChatMessage, ContactInfo, CallLogEntry
  Services/             NativeConnection (the socket and the login), DataService (state),
                        Loc (strings), ImageHelper, SettingsService,
                        SessionService (last section),
                        Diag (every failure we survive, with its HRESULT)
  Strings/<lang>/       Resources.resw - every user-visible string
  Assets/               generated PNGs (tiles, logos, splash)
ProtocolTests/          the protocol's own tests, run by tools/test-protocol.js
tools/                  static guards - run them, they are the real gate
  test-protocol.js      builds and runs ProtocolTests
  check-no-server.js    the retired server stays out of the tree and the docs
  check-docs.js         the two languages of the docs stay in step, no emoji
README.md / README.it.md                project docs, English + Italian
.agents/skills/         this directory
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
   The live tile is the one place where the icon is **not** a `Path`: a tile
   model wants its image in the payload, as `<image src="..."/>`, and does
   **not** fall back to the manifest logo. The asset is `Assets/TileIcon.png`
   (+ its `.scale-240`), a transparent PNG with no padding.
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
5. **Back navigation.** A Windows Phone 8.1 Runtime app is **not** given the
   Back button: the platform leaves the app from the first page, unlike
   Silverlight. `Services/BackNavigator.cs` subscribes
   `HardwareButtons.BackPressed` once at startup, sets `Handled` and pops the
   root `Frame` while it can; at a section root it leaves the event alone, so
   the system suspends the app. `SectionNav` trims the section page it leaves,
   so Back from any section root still exits.
6. **The protocol core is not the app.** Everything under `WhatsappApp/Protocol/`
   is the wire - the node format, the handshake, the Signal sessions, the media
   keys - and it is pinned against the reference implementations, not against a
   guess. A change there is a change to `ProtocolTests/` too, in the same commit.
7. **One page per section.** A new screen means a new file under `Pages/`, not
   another block inside an existing page, and it must be registered in the
   `.csproj` (a page that is not listed does not exist at build time).
8. **Runtime text is English.** Everything a person reads *outside the app UI* is
   English: the app's `Diag` lines (with every message inside an exception that can
   reach them), the guards and the tooling under `tools/`. Someone reading a
   debugger window or a phone log needs no second language. Only the app UI is
   localized, through the `.resw` pairs; source comments are English too, while the
   test names and the guard-script diagnostics under `tools/` stay Italian, because
   nobody reads them at run time.
9. **The docs are written in pairs, English and Italian.** `README.md` and
   `README.it.md` are versions of each other: a section is
   added, moved or renamed in **both**, in the same commit, with the same heading
   depth and order, and each links to the other. `## Disclosure` (open source,
   maintainers wanted, written by an AI agent, no responsibility for the account
   used) must be the **last** section of the README that presents the project,
   in both languages. New explanatory documents are born as a pair. No emoji: the
   warning sign (U+26A0) is the only exception, for a real hazard.
   Gate: `node tools/check-docs.js`.

## Showing only data that exists

A screen may only show data that exists upstream. The facts this project
established the hard way:

- **The history is pushed, not fetched on demand.** WhatsApp sends the
  conversations and a window of messages after a login; there is no per-chat
  "give me the older ones" this client asks for, so a conversation shows what the
  sync carried and what has arrived since, and the README pair says so.
- **The call log comes from that same sync** (`HistorySync.callLogRecords`), both
  directions, with `callCreatorJID` being *this* account on a call it made - which
  is why an outgoing call is filed under the other party and not under us.
- **The chat list is the account's conversations**, as the sync sends them, not
  the WhatsApp *address book*: on a freshly linked device the address book is
  empty while the conversations are not, which is exactly how the chat list came
  up blank. A group's name comes from the group metadata query, and a profile
  picture is a request that answers an address which then answers the JPEG - for
  a group like anybody else.
- **The profile details have no reader yet.** The name, the number and the
  picture are the app's own; the about line, the business profile and a group's
  members are what the old server used to compose, and `ContactInfoPage` says so
  instead of drawing headings with nothing under them.
- **Notifications can only be raised while the app runs.** WP8.1 suspends the
  app, which closes the socket, and this project has no cloud service to push
  through, so a toast can only be raised for a message that arrives while the app
  is in the foreground. The README pair's Limiti/Limitations section says so.
- **The contact picker is the user's consent.** `ContactPicker` shows the system
  UI and returns what the user taps; the app never enumerates the address book,
  which would need the `contacts` capability and a privacy story this project does
  not want.

The rule that follows: when a source has a limit, the screen and the README pair
state it, and **no screen invents state that nothing sends** - "online" and
"last seen at" were removed for exactly that reason. The other half of the rule is
that what WhatsApp *does* send may be shown: it sends typing notifications
(`chatstate` and the `chat_presence` node), but only to a client marked online, so
the three dots appear only because the client marks itself `available` while the
app runs. A limit is stated, never filled in with a guess.

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

Showing is not the same as being read, either. A conversation follows its newest
bubble only while the reader is **already at the bottom** (`ChatPage.AtBottom`),
and a message that arrives while someone is reading something older is neither
brought into view nor marked read: the two were one action, so every arriving
message both dragged the screen and told the server it had been seen. The position
is watched (`ScrollViewer.ViewChanged`, hooked only while there is a message
waiting) and the message is marked read when the reader reaches it.

## Workflow for any change

1. `git status --short` - start from a clean tree.
2. **A plan is written to be executed now.** When a change is multi-step enough to
   deserve one, write it with the **`writing-plans` skill** (`obra/superpowers`,
   installed at `~/.agents/skills/writing-plans`) to
   `docs/superpowers/plans/YYYY-MM-DD-<name>.md` and then execute it in the same
   session, task by task, until the last task is committed and pushed. The
   document is the record of the work, never the deliverable: stopping at the plan
   leaves the change undone and ships a description of code that does not exist.
   While executing, append a
   `## What execution changed about this plan` section at the end of the plan
   listing every divergence, and do not rewrite the tasks above it.

   **The `writing-plans` Execution Handoff is not a stopping point.** That skill
   ends by offering a choice between *subagent-driven* and *inline* execution, and
   here the answer is always **inline, in this same session**: there is no
   subagent dispatcher in this environment, and the request that produced the plan
   is the request to execute it. Say the two lines the skill asks for and then
   start Task 1 in the next message - never end the turn on the question, and
   never leave a plan that has been written but not run. If `writing-plans` is not
   installed, install it (`npx skills add obra/superpowers --skill writing-plans --skill executing-plans -g -y`)
   rather than writing the plan from memory.
3. Read the file you are about to change **completely**; this codebase keeps
   per-file invariants in comments.
4. Make the change.
5. Run the fast gate: `node tools/test-protocol.js && node tools/check-csharp5.js &&
   node tools/check-icons.js &&
   node tools/check-resw.js --strict && node tools/check-docs.js &&
   node tools/check-tile.js &&
   node tools/check-memory.js && node tools/check-actions.js &&
   node tools/check-fire-and-forget.js && node tools/check-project-files.js &&
   node tools/check-chat-list-source.js && node tools/check-chat-scroll.js &&
   node tools/check-history-sync.js && node tools/check-client-state.js &&
   node tools/check-diagnostics.js &&
   node tools/check-xaml-names.js &&
   node tools/check-attachment-file.js && node tools/check-no-server.js`, plus
   `node --test "tools/test/**/*.test.js"`.
6. If the change is user-visible, say which page and which string key changed.
7. Commit with a message that says *why* (the repo history is the changelog),
   and with **no AI attribution** in it: no `Co-Authored-By`, no "Generated
   with" footer, no robot emoji. [`agent-publisher`](../agent-publisher/SKILL.md)
   has the rule and the reason.
8. **Push.** A change is finished only when it is on `origin/master`: a commit
   that lives on this machine alone is invisible to everyone else, so every
   change ends with `git push`, not with the commit. Never leave the branch
   ahead of `origin/master`.

   **Pushing is the standing request here**, made explicitly by the operator: do
   not stop before `git push origin master`. If the environment holds a rule
   against pushing unasked, this standing request is what it was given for -
   the work is pushed as part of finishing it.

## Where a change belongs

| Change | File |
| --- | --- |
| New section of the app | new `Pages/XxxPage.xaml(.cs)` + a case in `SectionNav` |
| New icon | inline `<Path.Data><PathGeometry>` on the `Path` (never a resource) |
| New string | both `.resw` files (same key), then `x:Uid`/`Loc.Get` |
| Socket/protocol behaviour | `Protocol/Client/WhatsAppSocket.cs`, `Services/NativeConnection.cs` (+ the `ProtocolTests/` that pin it) |
| A new node, a new field, a new query | `Protocol/Client/` (the reader) + `Protocol/WaProto/` (the shape) + a test in `ProtocolTests/` |
| Contacts/messages state | `Services/DataService.cs` (keep `_contactIndex` in sync) |
| State kept across suspend/termination | `Services/SessionService.cs` |
| Image handling | `Services/ImageHelper.cs` |
| Drawing of the pairing QR | `Protocol/Qr/QrCode.cs` (the modules) and `Pages/PairPage` (the rotation) |
| Anything a reader reads (README, guides) | the English file **and** its Italian pair, then `node tools/check-docs.js` |
| A claim about the project (open source, maintainers, AI-written, responsibility) | `## Disclosure` at the end of `README.md` **and** `README.it.md` |

## Known gotchas

- **A reinstall empties the app's storage on WP8.1, and this phone holds its own
  account.** The login is the app's own files - `FileSessionStorage` keeps them in
  `LocalFolder/protocol`, read and written through `CredentialStore` - so an
  uninstall takes the account with it. The next launch finds nothing stored,
  `StartDecision.Decide` sends the phone to the pairing page, and the account is
  linked again by scanning a new code. There is no key to ask anyone for and no
  server that can hand one back: a phone that cannot connect is a phone that has to
  be paired again. A session that WhatsApp *refused* is the other case - it parses
  perfectly and would open on a chat list that can never fill - which is what
  `SessionState.Expired` remembers between runs.

- The app cannot be built on macOS: there is no WP8.1 toolchain. The build gate
  runs on the Windows/Parallels machine.
- **Build from a path that is not the Parallels share.** `C:\Mac\Home` is a
  symbolic link to the shared folder, and the XAML compiler resolves paths
  inconsistently through it: `MarkupCompilePass2` throws an internal
  `KeyNotFoundException`, MSBuild logs `WMC9999: The given key was not present
  in the dictionary`, and Visual Studio then reports every local type of that
  page as missing - for `ChatsPage.xaml` this is the three converters, with
  `The name "UnreadCountToVisibilityConverter" does not exist in the namespace
  "using:WhatsappApp.Converters"` and its two siblings. It is noise: the build
  still succeeds and the `.xbf` files come out byte-identical (verified
  2026-10-01, ARM Debug, 20929 bytes for `ChatsPage.xbf` either way), but it
  hides real errors. Deleting `obj`/`bin` does **not** help, because it is the
  symlinked path and not stale state. The same source builds clean from
  `C:\Temp\...`, and so does `subst X: <checkout>` opened as `X:\WhatsappApp.sln`.
- `x:Uid` on a `Button` overwrites `Content`; do not combine it with a `Path`.
- A key and the same key with a `.Property` suffix cannot coexist in a `.resw`
  (duplicate resource identifier) - the guard enforces this.
- WP8.1 caches the tile name and icons: after changing them, uninstall the app on
  the device before redeploying.
- **The tile templates that exist on WP8.1 are not the Windows ones.**
  `TileSquare150x150IconWithBadge`, `TileSquare71x71IconWithBadge`, the
  `TileSquare150x150PeekImageAndTextNN` family and the
  `TileWide310x150PeekImageAndTextNN` family do exist and are what
  `NotificationService` can use; `TileWide310x150IconWithBadge` does **not**, and
  naming it is a compile error (CS0117), not a silent no-op. This is a phone-only
  SDK: a template that compiles here may still be unsupported at run time, so a
  new tile format is a device check, not a build check.
- **On WP8.1 the badge is what draws a count on the tile, and it is set after
  the tile.** Nothing else puts a number there, so a badge the shell does not
  draw leaves a tile with no number at all - which is what the phone showed:
  the app icon and nothing else. `SetUnread` therefore posts the tile first and
  the badge second, because clearing the tile can take the badge with it.
- **The tile has to say one true thing, so it is one notification.** The unread
  total changes on its own - a chat is read, another message arrives - and a tile
  only replaces the tile's content. With the notification queue on, every message
  left a tile of its own and each of them counted the number of its own moment:
  what the screen showed was a count that had already moved on. `RenderTile`
  posts a single tile, replaced in place, with the count written inside it
  (`TileUnreadOne` / `TileUnreadMany`), the last sender's picture as its image,
  and their name under it - in the medium binding and in the wide one, because
  the two sizes say the same thing and a change cannot arrive on one of them
  alone. The 71x71 binding is the exception, and it is there for a reason of its
  own: the size has no text element and no room for one, so it carries the app
  mark and says "this app has something for you", while the size next to it says
  what and who. `Clear()` on both updaters is what returns the tile and the icon to the
  manifest's defaults.
- **A shared writer is written through a queue, never by two callers at once.**
  The receive path is fire-and-forget on purpose (`DispatchOnUiThread` and the
  frame handlers are `async void`, and the read loop does not await the
  dispatch), so an awaiting handler is running while the next frame is already
  being handled. Two `DataWriter.StoreAsync` calls on one writer put one frame's
  length prefix in front of the other's payload, and the peer reads a frame that
  does not exist; two `FileIO` writes on one file lose one of the two.
  `Services/SerialQueue.cs` is the answer: `_writes.RunAsync(...)` in
  `NativeConnection` (the session store and the pre-key store share it), and the
  same queue in `IncomingMediaStore`, `ChatPreferences` and `Diag`. Put the
  *decision* (the serialized record, the snapshot) on the caller's thread and only
  the write in the queue, or the queue touches state that another thread is still
  changing.
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
  exists). A style with `BasedOn` is left alone, because the base holdsthe setters and may live in another file. Buttons that declare only a `Height` in
  their own markup (the buttons of the account screen, and the ones on `PairPage`)
  are outside the rule on purpose: they are full width and nothing sits next to
  them, so the extra height costs nothing.
- **`NativeConnection.IsConnected` can be true on a dead socket.** Nothing observes
  a socket the OS closed, so a suspended-then-resumed app looks connected and is
  mute. `SessionKeepAlive.IsStale` (a session silent for 30 s) is what notices, and
  the connection loop is what closes it and logs in again; the app sends
  `presence`/`available` on resume for the case where the socket did survive the
  freeze.
- **A suspended app keeps looking online.** WP8.1 freezes the process without
  closing the socket, so a backgrounded phone is still counted as watching and
  WhatsApp keeps showing the account online. The app sends a control frame
  `presence`/`paused` in `App.OnSuspending` (inside the deferral) and
  `presence`/`active` in `App.OnResuming`; the other side keeps that flag per
  socket, and a fresh login clears it.
  Any other path that puts the app in the background has to say the same thing.
- **A media bubble shows only what the server sent.** The size of a document and
  the name of the file come out of `IncomingMediaStore` (`FileName`, `SizeBytes`)
  and are written onto the message in `DataService.ApplyMedia`; the type badge is
  the extension of the name, computed in `ChatMessage.DocumentBadge`. Nothing is
  guessed for a row whose bytes have not arrived: an unknown size is an empty
  string, not a zero. The cover frame of a video is best effort
  (`Services/VideoThumbnail.cs`): a null frame leaves the plain play box, and that
  is not an error to show.
- **One voice player for the whole page.** `ChatPage.VoicePlayer` is a single
  hidden `MediaElement` driven by the play button of the active bubble; the
  position is pushed onto the message by a 250 ms `DispatcherTimer`, because
  `MediaElement.Position` is not a dependency property and cannot be bound. Do
  not give each bubble a `MediaElement`: the list virtualizes, and that is one
  decoder per visible row.
- `Frame.BackStack` is mutable and used on purpose in `SectionNav`.
- `DataService.Contacts` is a public collection: if something adds a contact
  without `AddContact`, `FindContact` rebuilds its index, so keep inserts going
  through `AddContact` when you can.
- `ChatMessage.LoadMediaImageAsync` is a no-op once `MediaImage` is set; do not
  "fix" that by forcing a re-decode.
- **A silent `catch` is a bug.** Every failure the app decides to survive goes
  through `Diag.Failed("<call site>", ex)` before it is handled: the WP8.1
  projection can refuse a call at run time that compiled fine, and "The operation
  identifier is not valid" in the debugger output does not say which call it was.
  `Diag` prints once per site with the HRESULT, and `Debug.WriteLine` is compiled
  out of release builds, so shipping it costs nothing. The app also logs its own
  unhandled exception (`App.OnUnhandled`, and `OnUnobservedTask` one level down):
  a UI-thread exception with no handler kills the process before any `Diag` line,
  so a log that stops at the assembly list names nothing at all. A debug build
  then sets `e.Handled` and survives, because the run being diagnosed is worth
  more alive; a release build still goes down.
- **Do not retry a lookup that has already failed.** `Loc.Loader` and
  `GetUiDispatcher` each remember their failure; without that flag one failure
  becomes one exception per string, or per received message. What they need
  instead is to be resolved once at start-up on the UI thread: `Loc.Prewarm()`,
  called from `OnLaunched`.
- **A connection is owned by the loop that opened it.** `NativeConnection` keeps
  one login at a time: the wait, the login and the reading that follows it are the
  same loop, and when the connection ends for a reason that is not the account's
  death that loop is what closes it and logs in again. Never read from a second
  place: `WebSocketTransport.ReceiveAsync` keeps one waiting reader at a time (its
  `_waiting`, under the socket's own gate), so two readers on one connection steal
  each other's messages.
- **A frame length is not trusted, and a short read is never mistaken for the
  end of a frame.** The node framing lives in `Protocol/WABinary/NodeReader.cs`
  and `NodeWriter.cs`. `NodeReader.Decode` takes a frame with its compression
  prefix, refuses a compressed one (prefix bit `0x02`) instead of reading
  compressed bytes as if they were plain, and throws `ProtocolException` on a
  length that runs past the buffer: a short read desynchronizes the stream, and
  every node after it parses as garbage that still looks valid
  (`ProtocolTests/WABinaryTests.cs` pins a truncated frame). The transport above
  it is `Protocol/Transport/WebSocketTransport.cs`, one `MessageWebSocket` message
  per node, and its message type is set through `Control` because WP8.1's
  projection has no `MessageType` of its own - the default is text, which would
  put every byte above `0x7f` through a UTF-8 encoder.
- **`0x8007274C` is `WSAETIMEDOUT`, not a crypto or login failure.** It means
  `WebSocketTransport.ConnectAsync` never got an answer to
  `wss://web.whatsapp.com/ws/chat` (`WhatsAppSocket.DefaultUrl`), so the handshake
  and the pairing never ran. That connect carries no deadline of its own: what
  follows one is `NativeConnection`'s wait and next login, which is why a run that
  shows this failure and then a fresh attempt is behaving as designed.
- **`Window.Current` is null off the UI thread**, so it cannot be a fallback for
  anything that may run on a network thread: use `CoreApplication.MainView` there,
  or capture the object while you are on the UI thread.
- **The app's memory is not the conversation.** A login brings the history with it
  (`HistorySyncNotification` -> `HistorySync.Decode`), and the rows go through the
  same path a live message does, marked `IsHistory`. The flag matters: without it
  every old message would count as unread and raise a toast. The rows are inserted
  by date, not appended, because the payload promises no order between one sync and
  the next. Dedup is by WhatsApp's own message id, so a sync that re-sends a chunk
  cannot duplicate what is already on screen. Nothing asks for a chat's older
  history on demand (that is the limit the README pair states), and there is no
  request to retry: `DataService.HistoryComplete` is the one thing a page reads, and
  a page that opened before the sync binds from the phone's copy and waits for the
  burst (`HistoryCompleted`) instead.
- **A `[DataMember]` with a strict type is a whole-frame failure waiting to happen.**
  One unexpected string in one field makes `DataContractJsonSerializer` throw
  `SerializationException 0x8013150C` for the entire object, and `ChatMessage.FromJson`
  returns `null`: the message disappears with no visible cause. `Timestamp` is the field
  that bit us (a backslash-escaped `\/Date(ms)\/` where the value is `/Date(ms)/`),
  so it is a `string` on the wire and a leniently parsed `DateTime` in
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
- **The profile picture is a URL before it is a picture.** The `iq` that asks for
  one answers an `url` attribute (`ProfilePictures`, the reference's
  `query="url"`), and the JPEG is a second request to that address, which expires:
  it is fetched by `WinRtMediaFetcher` and the bytes are kept in `AvatarCache`,
  because the next question would be answered with a different address. Treating the
  answer as if it were already a bitmap produces base64 no decoder accepts, and the
  failure is silent: every row falls back to initials. The same trap is in the group
  metadata query: the subject is not the chat list's name for the group, which is
  why `GroupDirectory` asks for it at all.
- **The tile takes its image from the payload, not from the manifest.** An
  update whose `image/@src` is empty renders an iconless tile and raises nothing
  at all - no exception, no log, nothing in the Output window. `SetImage` writes
  the sender's picture when there is one and `ms-appx:///Assets/TileIcon.png`
  when there is not, and creates the `image` element when the template does not
  ship one (create it with the destination document, or the insertion throws):
  the 71x71 iconic template is exactly the one that ships none, so without that
  the small tile would render as a blank square and raise nothing.
  `TileWide310x150IconWithBadge` does not exist on WP8.1 (CS0117), so the wide
  tile takes its binding from `TileWide310x150PeekImageAndText02`:
  `AddWideBinding` imports that binding into the notification and fills it with
  the same helper as the medium one, and without it the wide size keeps the logo
  of the manifest whatever the app has to say. The import is guarded on its own,
  so a wide template the phone refuses cannot take the medium tile away with it.
  Gate: `node tools/check-tile.js`.
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
- **A screen that is shown before the socket is up binds from the phone's own
  copy.** A configured app opens on the chats and never on the settings, so there is
  no moment where a page can wait for a "connected" answer before it has something to
  show: `ChatsPage.OnNavigatedTo` builds the list from `ChatCache` (a photograph,
  replaced row by row by `ApplyChat`, and holding no avatar bytes) and the page then
  follows the sync burst that follows a login (`DataService.HistoryCompleted`).
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
- **A media message is an address and a key, not bytes.** Sending: `MediaUpload`
  encrypts the file, POSTs it to the host WhatsApp answers with (`MediaConn`) and the
  stanza that follows names where it went. Receiving: the encoded `Message` carries the
  media key and the direct path, and `MediaDownload` fetches and decrypts it - one path,
  used by a live message, a history row, an avatar and a status alike. A download that
  fails leaves the bubble without its picture and a tap asks again; it is not a reason
  to discard the message.
- **An upload is whole in memory, and a video is not.** The ciphertext carries its own
  ten-byte signature at the end, so a sender that streamed the file could not compute
  the MAC before the last block either. What comes back is the other way round: a video
  is written to a file as its bytes arrive (`IncomingMediaStore`) and never held whole,
  which is what the 512 MB budget buys.
- **Incoming media arrives in pieces, and a video goes to disk.** `IncomingMediaStore`
  reassembles them by `RelatedMessageId` and checks each piece's `MediaChunkIndex`
  against the number already received (the count `MediaChunkTotal` carries is what
  tells a short stream from a complete one): a photo lands in `MediaData`, and a
  video, an audio or a document is written to a file in `LocalFolder` as it arrives
  (never whole in memory - that is what the 512 MB budget buys) and referenced by
  `ChatMessage.MediaFilePath`, a client-only field.
  A `MediaElement` in `ChatPage` plays it from the `ms-appdata` file URI with
  `AreTransportControlsEnabled`. The play box shows on `ChatMessage.IsVideo`, which is
  true from `MediaType` alone, so it is tappable before the bytes arrive - the first tap
  downloads them, and `ChatMessage.IsMediaLoading` turns the ring in the bubble while
  they do.
- **WP8.1 has no Opus codec at all.** WhatsApp voice notes are Ogg/Opus, and Opus
  only arrived on Windows 10: the phone can play MP3, AAC/M4A, AMR and WAV, so a note
  that arrives cannot be decoded here, and one recorded here is M4A. There used to be
  a server in the middle that converted both ways with `ffmpeg`; it is gone, and the
  README pair's Limiti/Limitations section is where that is written down rather than
  quietly dropped. Anything that converts audio in the future has to do it **on the
  phone**, against a platform with no Opus. Audio and documents are `MediaType` values,
  not `MessageType` values:
  `MessageType` has no `Document` and adding one would change what every older
  frame deserializes to, so `ChatMessage.IsAudio` and `ChatMessage.IsDocument`
  derive from `MediaType` the way `IsVideo` does. A downloaded document is opened
  with `Launcher.LaunchFileAsync` on the file in `LocalFolder`, so the extension
  must be the real one (it comes from the file name in `MediaFileName`).
- **A recorded voice note is M4A, and it leaves through `/send/audio`.**
  `Services/AudioRecorder.cs` records AAC in an M4A container
  (`MediaEncodingProfile.CreateM4a`) because it is the format this phone records
  and decodes without help; WP8.1 has no Opus encoder. The M4A file goes into `LocalFolder` and then
  through the same `AttachmentInbox` slot as a picked photo, so
  `ChatPage.SendAttachmentAsync` streams it with the existing frames.
  `AttachmentInbox.KindName` must answer `audio` for an audio MIME type or
  extension: without that branch a voice note is sent as a document, and the
  receiver sees a file to download instead of a note to play. That word is the one
  `Protocol/Media/MediaUpload.cs` derives the keys with (`image`, `video`, `audio`,
  `document`) and the one the message carries, so the two sides of the wire have to
  agree on it.
- **The microphone is a `DeviceCapability`.** `MediaCapture.InitializeAsync`
  answers access denied without `<DeviceCapability Name="microphone" />` in
  `Package.appxmanifest`. Visual Studio rewrites that file on every build: after
  a build, `git checkout -- WhatsappApp/Package.appxmanifest`.
- **A new source file is a project file too.** Every `.cs` added under
  `WhatsappApp` needs its `<Compile Include>` in `WhatsappApp.csproj`, and every
  `.xaml` its `<Page>`, or MSBuild never sees it and the type it holds "does not
  exist" - but only on the VM build, because no other guard reads the project
  file. `tools/check-project-files.js` is that guard. After a build, check out
  `Package.appxmanifest` **only, never the `.csproj`**: the build runs on a copy
  at `C:\Temp`, so the manifest is the only file Visual Studio can rewrite, and a
  `.csproj` checkout silently discards the `Compile` entries just added - which
  is how `Services/RecordingSession.cs` and `Services/ConversationView.cs` were
  committed without them.
- **A recorder failure must be visible.** A fire-and-forget `async Task` that
  throws loses the exception: the tap does nothing and no `DIAG` line appears.
  Every recorder call is awaited inside a `try`, and the start is single-flight,
  because two `MediaCapture` initializations at once wedge the engine on this
  platform. An exception thrown at the call site - before `AudioRecorder`'s own
  `try` - is caught by `RecordingSession.StartAsync`, not by `Diag` inside the
  service. The state machine (idle, starting, recording) lives in
  `Services/RecordingSession.cs`, one instance per page; the page renders its
  result, and the `DispatcherTimer` that draws the clock stays on the page,
  reading `RecordingSession.StartedAt`.
- **A capture call has a ceiling.** `MediaCapture.InitializeAsync` and
  `StartRecordToStorageFileAsync` run through `AudioRecorder.InTimeAsync`
  (ten seconds): a call that does not answer becomes a `false`, the same failure
  every other path already produces, instead of a freeze.
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
  box but asks for its bytes again. The chat page binds its list only **after** that
  copy is in (`ChatPage.BindAfterCacheAsync`, through `ConversationView.Bind`):
  inserting into a collection the ListView is already watching, in the middle of a
  navigation, answers `E_UNEXPECTED` and the whole load is lost, so the conversation
  opens empty with one `DIAG` line. The first open has no file at all, and that is
  not a failure: `MessageCache.LoadAsync` catches `FileNotFoundException` without a
  `Diag` line and logs only a copy it could not parse, so a missing file never hides
  the real fault in the log again. When a history burst is on its way, the bind also
  waits for `HistoryCompleted`, or twenty seconds, whichever comes first: the burst is
  one row per message, and a bind while it is still inserting re-lays out the list on
  every frame. That wait must not be shorter than the sync it waits for - the payload
  is pushed after the login and was measured at about 15 s on 2026-10-07 - because the
  wait that expires first is the one that binds the list before its burst and hands it
  fifty inserts it never asked for. `check-diagnostics.js` pins the floor, so the value
  cannot drift back.
- **The diagnostics outlive the run.** `Diag` appends every line to `diag.log` through
  a `SerialQueue`, cuts the file to its last 64 KB, writes a run-start and a run-end
  marker, and keeps a `diag-run.marker` file while the run is alive. A marker still
  there at the next startup is what a crash is; the tail it left becomes
  `Diag.PendingCrashTail`, and the run that follows writes it into its own log
  (`CrashReport.Start`, called from `App.StartServicesOnce` right after the marker is
  read), so the crash is read on the diagnostics page and off the phone's `diag.log`.
  The marker is a file and not a
  line in the log because `OnSuspending` is the normal end of a run here: a log with
  no end marker is normal too, and a crash after a resume would slip past anything
  that only read the log. `OnResuming` puts the marker back, and `Diag.Flush(true)`
  runs inside the unhandled-exception handler, before the process may die. The
  in-memory history is only a screen: it dies with the process, which is why every
  earlier crash in this repo arrived as a log that stopped at the assembly list.
- **A login is completed, not only performed.** `ClientPayload.Login` asks to be brought
  up **passively** (`passive` and `pull` are 1), and the reference finishes that login on
  the `success` node with `sendPassiveIq('active')` and the unified-session telemetry
  (`Socket/socket.js`, the `CB:success` handler). This app sends both right after the
  login (`NativeConnection.AnnounceClientStateAsync`, the nodes themselves in
  `Protocol/Client/ClientState.cs`): a client that logs in and then says nothing is the
  passive half of its own login, and that is what the phone showed on 2026-10-17 - a login,
  two `ib` nodes, and the connection gone about two seconds later, at every start. The same
  channel is **read**, not named: the child's tag is what the reference dispatches on
  (`InboundRouter.NotificationType`), the backlog preview is answered
  (`ClientState.OfflineBatch`), and `downgrade_webclient` is the one child that refuses the
  client instead of telling it something (`ClientState.IsClientRefusal`). A read that fails
  names the bytes of the frame it interrupted (`FrameAccumulator.PendingHex`), because the
  end of every run on this phone has the same shape - a few bytes, then `0x80072EFE` - and
  those bytes are the only evidence of where the server stopped talking.
  `check-client-state.js` pins all four of those cables.
- **The login is read up to `success`, and a message is acked after it was read.**
  `NativeSession.LoginAsync` reads nodes until the answer is `success` (or a refusal), and
  queues everything that arrived before it in `_beforeLogin`, which `ReadNodeAsync` hands
  back first: the bootstrap speaks on `ib` and can speak before the answer, and a login
  that reads one node throws that frame away in the one moment when nothing else is
  reading. On the message path the order is the other way round and just as strict: the
  stanza is opened first, then acknowledged (`ReadMessageAsync`), because the ack is what
  stops the server re-sending and an ack on a failed open makes a transient failure
  permanent - it is how one lost open could lose the only history sync the account gets.
  A failed open is answered with `IncomingMessage.RetryReceipt`, the reference's own
  `retry` receipt (`sendRetryReceipt`), which asks the sender to encrypt the stanza again
  and carries this device's `<registration>` - the child that tells the sender its session
  was built with a registration this phone no longer has, which is the one thing that makes
  it throw the session away and start over.
- **A new identity cannot read the old sessions, and a pre-key message is how a chat
  restarts.** A Signal MAC is computed over **both** identity keys (`Cipher.Input`), so the
  moment `PairPage` draws a new `Credentials` every session on disk is unreadable - which is
  why the page wipes `LocalFolder/protocol` (`FileSessionStorage.DeleteAllAsync`) before it
  stores the account, and why the run of 2026-10-18 failed every stanza from every sender,
  the account's own phone included, with `the message's MAC does not verify`. The way back
  is the peer's pre-key message, and it only works because `IncomingMessage.SessionFor`
  reads one as what it is - the start of a session, not a ciphertext for whatever session
  happens to be there. The record keeps the base key its agreement was started by
  (`SessionRecord.KnowsTheirBaseKey`, encoded with the rest of the record), so a
  re-delivery uses the session and a new key replaces it; `libsignal`'s
  `session_builder.initIncoming` is the reference for both halves. Two consequences of the
  same read path: a `previousCounter` beyond 2000 is refused instead of walked to
  (`SkipTo`, the reference's `fillMessageKeys` - the alternative is the sixty seconds the
  watchdog logged), and the plaintext is unpadded before it is parsed
  (`Message.DecodePadded`), because WhatsApp appends one to fifteen bytes behind every
  message and a reader that skipped them reads a length field out of the tail
  (`a protobuf field announces 12095 bytes with 52 left`). The pairing wipe is pinned by
  `check-client-state.js`; the other four have protocol tests.
- **The signed pre-key in the store has to be the account's, not merely *a* signed
  pre-key.** Signed pre-keys are numbered from one, so a store left by a previous pairing
  holds **the same id over another private key**, and the responder path has nothing but the
  id to compare (`StartResponder`, and a pre-key wrapper carries nothing else). The upload
  publishes the *current* identity's public key and signature, so the sender derives one root
  and this phone derives another: every message of every session fails `the message's MAC
  does not verify`, from every sender, with no line pointing at the store - the phone of
  2026-10-18, after the pairing wipe was in place but with no pairing in that run.
  `PreKeyUpload.AdoptSignedPreKey` compares the stored private half's **public key** with the
  account's and replaces it, and `NativeConnection.AdoptSignedPreKeyAsync` runs it at every
  login - not inside the one-time pre-key refill, which is skipped whenever the store still
  has keys. `check-client-state.js` pins that call; `PreKeyUploadTests` and
  `IncomingMessageTests.AStoreWithAnotherKeyUnderTheSameIdCannotReadUntilItIsAdopted` pin the
  behaviour (the second one fails on the MAC first, then reads the same stanza once the
  account's key is in the store).
- **The chat list has one push, and it is not repeatable.** WhatsApp sends the history when
  the device is linked and never again; this client has no app-state sync (`server_sync`,
  `w:sync:app:state`) and no way to ask for the history a second time, so for an account
  whose first sync was missed the list can only be filled by live messages and by the copy
  on disk (`ChatCache`, which refuses to save an empty list). That is why the end of the
  backlog names it - `NativeConnection.ReadNotificationAsync` writes a line when `offline`
  arrives with no chat and no sync - and why "re-link the device" is the answer to an empty
  list that stays empty on a healthy connection. App-state sync is a subsystem, not a
  tweak: implementing it needs the app-state keys a pairing distributes and the patch
  format, and it is the next thing to build if the list must survive a lost first sync.
- **The conversation's view state is one module.** `Services/ConversationView.cs`
  owns the bind, the scroll queue, the viewer lookup and the "at the bottom"
  question, because all four touch the same three things and the `E_UNEXPECTED`
  that emptied a chat lived on that seam. Each step logs its own name
  (`ConversationView/scrollIntoView`, `/findViewer`, `/changeView`), so a phone log
  says which call threw instead of naming the method they share. `UpdateLayout` is
  kept out of the common scroll path: the viewer reaches the bottom without a
  layout pass, and a pass on a list bound during a navigation is what answered
  `E_UNEXPECTED` once the insert was fixed. The conversation `ListView` declares
  no `ItemsSource` in XAML either: a `{Binding}` there is resolved against
  `DataContext`, which on this page is a `Contact` and not a collection, so it
  fights the one writer and leaves the chat empty with a run of first chance
  `SYSTEM.NI.DLL` exceptions and no `DIAG` line. The viewer is resolved only once
  the list has a visual child (WP8.1 has no `FrameworkElement.IsLoaded`), because
  the tree walk on the first frame after a navigation is the same
  `E_UNEXPECTED`. And a picture opened full
  screen is decoded at the size the page draws it, not at the viewer size
  (`ChatPage.HeaderAvatar_Tapped` uses `Contact.AvatarDecodePixels`): the viewer
  copy is held while the conversation behind it is still laying out.
- **A pin, a silence and a deletion are this phone's, and the file that holds them
  is not the chat cache.** `ChatCache` is a photograph of the list that the next
  sync writes over, so a decision kept there would not survive it.
  `ChatPreferences` is the other
  thing: `chat-preferences.json` in `LocalFolder`, read once at start-up (before
  the first row is built, in `DataService.LoadCachedChatsAsync`) and written by the
  caller that changes it. `Hidden` keeps a deleted chat out of `ApplyChat` and
  `ApplyContact`; a live incoming message calls `Reveal`, which is what makes a
  chat deleted by mistake come back - and is why nothing here is a one-way door.
  Muting suppresses the toast only: the unread count is still true.
- **A channel and the status broadcast are not conversations.** Neither can be
  answered, and each takes the place of a person in the list, so
  `DataService.IsNotAConversation` filters both out where a row is applied - the row
  can arrive from the history sync or from the copy already on disk, and that one
  place is where both are stopped. The statuses themselves live in the app's Status
  section (`Protocol/Client/StatusFeed.cs`).
- **The pictures have a cache of their own, and it is read before the rows.** The row
  cache (`ChatCache`, `chats.json`) leaves `AvatarData` out on purpose: it is the file the
  app reads before the connection exists, and one picture per chat would multiply it.
  The bytes live in `AvatarCache` (`avatar-cache.json`), capped at 40 chats, 150000
  characters per picture and 1500000 for the file, written through the same
  `SerialQueue` as `ChatPreferences` (two writes on the same file, launched without
  waiting, can land out of order - that bug was already fixed once in
  `ChatPreferences`). `DataService.LoadCachedChatsAsync` awaits `AvatarCache.LoadAsync()`
  before the first `ApplyChat`, because `ApplyChat` asks the cache for the picture of a
  row that carries none, and a restart then shows the faces before WhatsApp answers.
  `MemoryWatcher` drops only the decoded bitmap, never the bytes, and
  `DataService.RestoreAvatars` (called by `ChatsPage.OnNavigatedTo`) redraws them.
  `tools/check-memory.js` fails on all three of these.
- **The profile details have no reader yet, and the page says so.** `ContactInfoPage`
  fills the name and the number from the contact this phone already has, and asks
  WhatsApp itself for the picture (`RequestAvatarAsync`). The about line, the business
  profile and a group's members are what the retired server used to compose out of
  three routes of its own; nothing here reads them yet, and `ContactInfoPage_NoInfo` is
  the sentence that states the limit instead of drawing headings with nothing under
  them. Whoever wires a native reader fills `Models/ContactInfo.cs` and calls
  `ContactInfoPage.Apply` - the page is already built around that one shape.
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
- **A group has a picture like anyone else.** The picture is a query of its own
  (`Protocol/Client/ProfilePictures.cs`): a `type="get"` with `query="url"` sent to
  the JID, answered with a signed, expiring address that the app then downloads
  (`NativeConnection` asks for `image`, and for `preview` when a row only needs a
  small copy). The JID goes in whole, a group's included: skipping `@g.us`, or
  cutting the JID down to its digits, is what made groups show initials.
