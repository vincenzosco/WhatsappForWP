---
name: test-the-app
description: How to verify a change to the WhatsApp WP8.1 app - the static guards, what each one catches, the protocol test suite, the Windows msbuild gate, and the on-device checklist. Use before declaring any change done, or when something fails on the device.
---

# Testing the app

## The fast gate (runs on any machine, seconds)

Current expected counts: 327 protocol tests, 131 keys in each `.resw`, 188 tests in
`tools/test`, 31 buttons and 2 button styles. The counts printed by the guards
themselves are the source of truth; these are what to expect when nothing is out of
step.

```bash
node tools/test-protocol.js      # builds and runs ProtocolTests/, the protocol's own suite
node tools/check-csharp5.js        # C# 5 syntax + WP8.1-missing WinRT APIs
node tools/check-icons.js          # icon rules + consistency + no icon font
node tools/check-resw.js --strict  # x:Uid/Loc.Get <-> both .resw, PRIResource, default language
node tools/check-docs.js          # the two languages of the docs are in step, no emoji
node tools/check-no-server.js    # the retired server is out of the tree and out of the docs
node tools/check-tile.js        # the tile payload carries its icon, asset within the limits, decoded pixels measured before they are drawn
node tools/check-memory.js      # no bitmap decoded bigger than it is drawn, every bitmap built on the thread that draws it, the picture caches bounded
node tools/check-actions.js     # a button named X is wired to X_Click and draws its icon
node tools/check-fire-and-forget.js  # a call fired without await is observed, or it loses its fault
node tools/check-project-files.js  # every .cs and .xaml is listed in the csproj, or MSBuild cannot see it
node tools/check-chat-list-source.js  # the conversation ListView has no ItemsSource in XAML, only ConversationView.Bind
node tools/check-chat-snapshot.js  # the chat list is written before the conversations' tails
node tools/check-chat-scroll.js  # a chat follows the history burst, and the arrow back to the newest message is wired
node tools/check-history-sync.js  # the sync's messages reach the app marked as history, and only this account's sync is applied
node tools/check-client-state.js  # the login is completed with the active iq, and the notification channel is read instead of dropped
node tools/check-diagnostics.js   # the run is on disk, the crash report reads the marker, and the history wait is not shorter than the read
node tools/check-xaml-names.js  # a x:Name is used once in one namescope
node tools/check-attachment-file.js  # each attachment copies to its own name, and the player reloads the bubble it was asked for
node tools/check-lid-names.js  # a LID is never printed as a phone number, and the rule is asked before digits
node tools/check-status-viewer.js  # a status tap opens the update, the row binds only what its model has, the feed survives a restart
node tools/check-background-task.js  # the manifest, the task component and the registration name the same class
node --test "tools/test/**/*.test.js"  # the tools' own tests (207)
```

Exit code 0 and an `OK: ...` line each. What they catch that the build does not:

| Guard | Catches |
| --- | --- |
| `check-csharp5.js` | Syntax the WP8.1 compiler rejects (it never shows up here otherwise), APIs that exist on Windows 10 but not on WP8.1, and a LINQ extension method (`.All(...)`, `.Where(...)`) in a file without `using System.Linq;` - a CS1061 that only msbuild reports. |
| `check-icons.js` | A blank icon button (`Segoe MDL2 Assets`); `Data="{StaticResource IconX}"`, which compiles but throws at runtime; a `PathGeometry` that is not inlined in a `<Path.Data>`; `Figures="M..."`, the string form of `PathGeometry.Figures` that does not compile on WP8.1; a `Path` with no inline geometry or no `<!-- IconX -->` comment; two copies of the same icon name with different geometry. |
| `check-resw.js` | A string that would silently stay in the markup language: missing/mistyped `x:Uid`, `x:Uid` on the wrong property, a `Loc.Get` key absent from a language, languages whose key sets differ, a `.resw` missing from the `csproj` (`PRIResource`), a wrong `<DefaultLanguage>`, a key/`.Property` collision, an unused key. |

Two lessons the gates taught:

- `check-resw.js` reads **every** `Loc.Get("...")` occurrence in C#, comments
  included, as a key lookup. A diagnostic label must therefore be built as
  `"Loc.Get key " + key`, never with the literal call shape inside the string.
- Membership of `Windows.winmd` is **not** a runtime guarantee: `AesGcm`,
  `AesCbc`, `DisplayRequest` and `RequestActive` are all listed, and the device
  still answered `E_NOTIMPL`. That is why the cipher the handshake needs is
  written out in `Protocol/Crypto/AesGcm.cs` rather than asked of the platform
  (its header comment says so), and why a platform member that has never run on
  the phone is not treated as working.
| `check-docs.js` | A README section added to one language and not the other (the heading counts stop matching), a missing link between the two versions, a `## Disclosure` section that is absent or no longer last, an emoji anywhere in the Markdown (the warning sign U+26A0 is the only exception). |
| `check-actions.js` | A button whose `Click` handler does not carry its `x:Name` (an icon that opens its neighbour's action), the wrong icon on a title-bar button, a button that declares a `Width` without `MinWidth="0" MinHeight="0"`, and a `Style` with `TargetType="Button"` that declares a `Width` or a `Height` without them - the WP8.1 theme minimums (109 x 57.5) would override the declared size, the `Auto` column would grow and its neighbour would be squeezed, which is exactly how the chat list title was clipped. A style with `BasedOn` is left alone: its base holds the setters. |
| `check-memory.js` | A decode call without its display width, a width wider than the screen, `DecodePixelWidth` set after `SetSourceAsync`, a whole picked or shared file read into a `byte[]`, the row cache carrying picture bytes, the avatar cache without its caps or without the serial queue, and the avatar cache read after the cached rows - a restart then shows initials until WhatsApp answers - and an `ImageHelper` that builds its `BitmapImage` without asking which thread it is on (a pool thread answers `RPC_E_WRONG_THREAD` 0x8001010E, the picture is lost and the row keeps its initials). |
| `check-fire-and-forget.js` | A call inside `#pragma warning disable 4014` whose fault nobody observes: it does not go through `Guarded.RunGuardedAsync`, and the method it names catches nothing, so an exception thrown at the call site lands in a Task nobody awaits. That is the voice note whose tap did nothing and left no `DIAG` line. It also refuses two statements in one region, a call the app does not define, and a dispatcher lambda that catches nothing. |
| `check-project-files.js` | A `.cs` or `.xaml` under `WhatsappApp` that `WhatsappApp.csproj` does not list. MSBuild never sees it, so the type it holds "does not exist" only on the VM build, and the guards all pass in the meantime - which is how `RecordingSession.cs` and `ConversationView.cs` were committed without their `Compile` entries. |
| `check-chat-list-source.js` | A second writer for `MessagesListView.ItemsSource`. The list is bound in code by `ConversationView.Bind`; an `ItemsSource="{Binding}"` left in XAML is resolved against `DataContext`, which on this page is a `Contact` and not a collection, so it fights the code-set source and the conversation opens empty with a run of first chance `SYSTEM.NI.DLL` exceptions and no `DIAG` line. |
| `check-chat-snapshot.js` | Three ways for the list on disk to be older than the screen, all seen on the phone of 23-24 October 2026. The order inside `DataService.SaveSnapshotAsync`: the chat list written **after** the conversations' tails - each tail is a read and a write of its own file, one at a time (about 1.8 s each), so a list written last is a list never written when the app is closed in the middle, and that store had 57 `messages_*.json` with no `chats.json`. A message path that never asks for the list to be written (`ScheduleListSave`): every writer was an *ending*, and the run of 24 October was stopped from the debugger, so its rows went with the process. A start that loads `chats.json` without asking the tails for the rows it does not name (`RestoreRowsFromTheirTailsAsync`): that file held 2 rows against 56 tails, 54 conversations on the phone and not on the list. |
| `check-chat-scroll.js` | A chat that opens on its old messages: the page not subscribed to `HistoryCompleted` (so the burst that lands after the bind moves nothing and the reader stays at the oldest row), a handler that does not ask `ActiveChatId` (a burst for a chat no longer in front would move another page's list), and the arrow back to the newest message - missing its `RecentMessagesButton_Click` wiring, `Grid.Row="1"`, `Visibility="Collapsed"` or `<!-- IconArrowDown -->` - plus the page holding its own `ScrollableHeight`, a second opinion about the bottom that goes stale. |
| `check-history-sync.js` | The two cables of the history sync that the protocol suite cannot see, because they live in the app: a `NativeConnection.ApplySync` that keeps `_chats.ApplyHistory`'s answer to itself - the sync then fills only the list, every chat opens empty, and the page waits for a burst that never closes because `HistoryCompleted` is raised per chat that received rows - and a `DataService.OnNativeMessage` that drops `incoming.IsHistory`, so every old message arrives as new, with a toast each and a chat row counted once more per sync. It also refuses a sync applied from a stanza that is not this account's: `historySyncNotification` is a self-only protocol message, and the reference drops it from anybody else ("an attacker could otherwise spoof any of these to manipulate local state"), which on this phone would hand a contact the chat list, the call log and the status feed. |
| `check-client-state.js` | The sentences a companion owes the server after its login, and the channel that can refuse it. `ClientPayload.Login` asks to be brought up **passively** (`passive` and `pull` are 1) and the reference completes that login with `sendPassiveIq('active')` on `success`: a client that logs in and then says nothing is the passive half of its own login, which on the phone of 2026-10-17 was a login, two `ib` nodes and a dead connection two seconds later, four runs in a row. It also refuses a `NativeConnection` that only **names** the notification (`nothing reads it yet`) instead of reading its child - the child is what the reference dispatches on, and `downgrade_webclient` is the one that refuses the client - and a `WhatsAppSocket` whose failed read does not name the bytes of the frame it interrupted, which is the only evidence of where the server stopped talking. Two more cables are pinned there: a login (`NativeSession.LoginAsync`) that reads one node instead of up to `success` and drops what arrived before it (the bootstrap's `ib` is exactly that frame), and a message path that acknowledges a stanza before opening it - the ack stops the server re-sending, so a failed open must answer with `IncomingMessage.RetryReceipt` instead. The sixth is the one the run of 2026-10-18 needed: `PairPage` must call `FileSessionStorage.DeleteAllAsync()` **before** it stores the new account, because a Signal MAC covers both identity keys and the sessions written by the previous pairing cannot read a single stanza of the new one (`the message's MAC does not verify`, from the account's own phone and from a contact alike). The seventh is the same failure one step further in: the login must call `NativeConnection.AdoptSignedPreKeyAsync`, because the store's signed pre-key can carry the account's own id over the previous identity's private key, and the responder path has nothing but the id to compare. |
| `check-diagnostics.js` | The diagnostics that never reach the disk: a log written without a `SerialQueue`, a file with no byte ceiling or no name, no marker file to tell a crash from a suspension, the unhandled-exception handler leaving the line in memory, and a history wait shorter than WhatsApp's cold read of the account (a 2 s wait binds the list before the sync that follows a login arrives, and hands fifty inserts to a list already watching the collection). |
| `check-attachment-file.js` | An outgoing attachment copied to the shared name `outgoing_attachment` (every recording then replaces the one before it, and every bubble that points at that name plays the last file), and a `ToggleVoice` that trusts the file name alone instead of reloading when the bubble changes (`bool anotherMessage = _voiceMessage != message`). |
| `check-lid-names.js` | A LID printed as a phone number. A LID is WhatsApp's identifier for an account whose number this phone does not know, and its user part is 14-15 digits, so anything that asks "is this a number?" before "is this a LID?" answers `+150375613116620` - a number nobody has. `DataService.DisplayNameForJid` had the guard written under the digits branch, which every LID satisfies: a guard that could not fire. The guard also refuses the three display sites when they stop asking `JidUtils.IsLid`, any other file that builds `"+" + user` from a JID without it (`ConnectionPage` is exempt: it prints this account's own number), and an `IsLid` that matches a suffix on the raw address instead of decoding it. |
| `check-status-viewer.js` | The status surface taken apart by hand: a tap that marks the update watched without opening it (`StatusViewerPage` is never navigated to), a viewer that does not send the `read` receipt or does not ask for the bytes a history status does not carry, a receipt addressed to the author instead of the broadcast list, a feed that is not read back at start or is written once per status instead of once per burst (the history brings a whole day in chunks, so a write per insert rewrites the file dozens of times on the phone the cache exists for), a status inserted at the top instead of where its time says, and a status row that binds a property its model does not have - which is what the run of 2026-10-22 answered with two binding errors per row and an empty grey circle where the author's initial belongs. |
| `check-background-task.js` | The background check that never wakes. On WP8.1 the task cannot live in the app's assembly: the system creates it in a process of its own, from a class the manifest declares activatable. The guard refuses a task class in the wrong project, an `EntryPoint` that disagrees with the `ActivatableClassId` or with `BackgroundSync.EntryPoint`, an in-process server that does not point at `CLRHost.dll`, a `Task Type` that is not `timer`, and a `BackgroundSyncTask.cs` missing from the component's `<Compile Include>`. The failure it was written for is silent: the registration succeeds, the log says `the check runs every 15 minute(s)`, and no `background: woke` ever appears - two builds of 22 October 2026 sat like that for hours. |
| `check-xaml-names.js` | Two elements in one namescope with the same `x:Name`. A `DataTemplate` registers its names when a row is realized, so the second one answers `XamlParseException 0x802B000A` at run time and the screen that holds it closes instead of opening - it was the play button of the incoming bubble and the one of the outgoing bubble, both called `PlayAudioButton`, and the chat that closed the moment it was opened. The same name in two different templates is legal and is not flagged, and neither is a name inside a comment. |

Also worth running while the tree is open:

```bash
for f in WhatsappApp/*.xaml WhatsappApp/Pages/*.xaml WhatsappApp/Controls/*.xaml; do
  xmllint --noout "$f" || echo "MALFORMED: $f"
done

# every x:Name in a page must be referenced by its code-behind (or be intentional)
node -e "
const fs=require('fs');
for (const p of ['Pages/ChatsPage','Pages/StatusPage','Pages/CallsPage','Pages/ChatPage','Pages/ConnectionPage','Controls/SectionNav']) {
  const x=fs.readFileSync('WhatsappApp/'+p+'.xaml','utf8').replace(/<!--[\s\S]*?-->/g,'');
  const names=[...x.matchAll(/x:Name=\"([^\"]+)\"/g)].map(m=>m[1]);
  const cs=fs.readFileSync('WhatsappApp/'+p+'.xaml.cs','utf8');
  const missing=names.filter(n=>!cs.includes(n));
  console.log(p+': unreferenced -> '+(missing.length?missing.join(', '):'none'));
}"
```

```bash
# every event handler a page's XAML names must exist in its code-behind
node -e "
const fs=require('fs');
const ATTRS=['Click','ItemClick','Toggled','Checked','Unchecked','SelectionChanged','TextChanged','KeyDown','KeyUp','Loaded','Tapped'];
for (const dir of ['Pages','Controls']) for (const f of fs.readdirSync('WhatsappApp/'+dir)) {
  if (!f.endsWith('.xaml')) continue;
  const src='WhatsappApp/'+dir+'/'+f, cs=src+'.cs';
  if (!fs.existsSync(cs)) continue;
  const declared=new Set(fs.readFileSync(cs,'utf8').split(/[^A-Za-z0-9_]+/));
  const named=[...fs.readFileSync(src,'utf8').matchAll(new RegExp('('+ATTRS.join('|')+')[=]\"([A-Za-z_][A-Za-z0-9_]*)\"','g'))].map(m=>m[2]);
  const missing=named.filter(n=>!declared.has(n));
  console.log(src+': '+(missing.length?'missing '+missing.join(', '):'none'));
}"
```

## The protocol suite

```bash
node tools/test-protocol.js
```

Expected `OK: 327 protocol test(s)`, exit 0. It compiles `ProtocolTests/` against
the app's own sources and runs them: the binary node reader and writer, the Noise
handshake, the Signal sessions (X3DH, the double ratchet, the sender key of a
group), the media key schedule and the four media protobufs, the credential store,
the history payload, the call log, the status feed and the pre-key upload. Several
of those fixtures are the **reference implementations' own output**
(`ProtocolTests/Vectors/`), so a change that only agrees with itself fails here.
Add a test with every protocol change: this is the only machine that can run the
wire code.

## What no offline test covers

- **The TLS connection and the server's answers.** Everything past the wire format
  needs a real account.
- **Anything the platform does.** A `MediaElement` that refuses a codec, a
  `WriteableBitmap` that renders no modules, a `MediaCapture` that answers access
  denied: the build compiles them and only the phone can run them.
- **The two ends of the app itself**: a page reached, a row drawn, a toast raised.
  The guards read the source; the device checklist below is what runs it.

## The real build gate (Windows machine)

```bash
msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86
```

Expected `0 Error(s)`. Notes worth remembering:

- **Build from a local disk path, never from a Parallels/Mac shared folder.**
  With the project under `C:\Mac\Home\...` the XAML compiler's second pass fails
  every time with
  `Microsoft.Windows.UI.Xaml.Common.targets(327,9): Xaml Internal Error error
  WMC9999: The given key was not present in the dictionary.` - on a freshly
  cleaned tree, for every platform, and with the previous XAML too, so it is
  never the change under test. Copy the repo to a disk on the Windows side
  (`robocopy <share> C:\wp81 /E`) and build there: same files, `0 Error(s)`.
  A symptom of the shared folder is that `obj\` still ends up with
  `WhatsappApp.exe` and `App.xbf` even though the build reports `WMC9999`.
- `Platform=x86` (for the emulator) and `AnyCPU` both build; the earlier
  successful artifacts live in `obj\Debug\`, the x86 ones in `obj\x86\Debug\`.
- MSBuild 12.0 (`C:\Program Files (x86)\MSBuild\12.0\Bin\MSBuild.exe`) and
  `devenv.com` report the same result; if a PDB is locked, a `devenv.exe` from a
  previous run is still alive.
- If a Rebuild is followed by a build that fails early, the XAML pass
  (`MarkupCompilePass1`) may report
  `The name "...Converter" does not exist in the namespace ...`. Building a
  second time (without cleaning) resolves the reference against the freshly
  compiled assembly.
- **`error CS1061` from `obj\Pages\<Page>.xaml` that names a handler the tree no
  longer has is a stale XAML copy, not a source error.** The XAML pass writes a
  preprocessed copy of every page and a `.g.cs` next to it under
  `obj\<Configuration>\Pages\` (the platform-less configurations, so `AnyCPU`)
  and `obj\<Platform>\<Configuration>\Pages\`; when that copy is newer than the
  `.xaml` it came from, MSBuild finds the target up to date and compiles the old
  wiring. The `#line` directives in the copy are also what put `obj\Pages\...`
  and a line number the page no longer has into the message. The refactor of
  2026-10-11 that deleted the connection page's buttons left eleven of them in
  `obj\Debug\Pages\` - `'ConnectionPage' does not contain a definition for
  'QrOverlayCloseButton_Click'`, at `obj\Pages\ConnectionPage.xaml(253,98)` -
  while no `.xaml` in the tree named that handler. The handler snippet above says
  whether the source is consistent (all 42 handler attributes were, that day),
  and `rm -rf WhatsappApp/obj WhatsappApp/bin` followed by a build answers
  `0 Error(s)`.
- **The gate is `/t:Rebuild`: a plain solution build for `x86` ends in
  `error APPX3104`.** `AppxBundlePlatforms` is `arm` - the phone - and the
  bundle target refuses the x86 packages the same command has just written:
  `msbuild WhatsappApp.sln /p:Configuration=Debug /p:Platform=x86` (no
  `/t:Rebuild`) fails with `error APPX3104: ... x86 ... not in the list of
  platforms selected for building the app bundle ... arm`. It is the packaging
  step and not a code error, and the ARM package is unaffected: `/t:Rebuild` and
  a direct `WhatsappApp.csproj -p:Platform=x86` build both end at `0 Error(s)`.
- The VS2013 XAML designer needs a developer licence / sideload policy and is not
  needed: close the designer, open `.xaml` as XML, or build with `msbuild`.

## What runs against the real server (checkable here)

The protocol suite has a live mode: `node tools/test-protocol.js --connect` skips
the offline suite and reads WhatsApp's first node from the real endpoint once, so
the codec, the framing and the handshake are checked against the server and not
only against this repository's own fixtures. The run that arbitrated the pairing
payload's field numbers was

```bash
node tools/test-protocol.js --connect --pair
```

and its line - `PROBE OK: the server asked for a pairing (6 code(s))` - is what to
expect while the pairing flow is unchanged.

Everything past that point needs WhatsApp itself: the phone draws its own pairing
code (`Pages/PairPage.xaml.cs`) and the account is linked by scanning it, which is
a device run and not something this machine can do.

## What can and cannot be verified here

The static guards, the protocol suite and the Windows build are checkable. **Running
the app is not, unless the build machine can host the WP8.1 emulator or you have a
phone:** the XDE images are x86 and need Hyper-V, so on an ARM (Apple Silicon)
host running Windows 11 ARM64 there is no way to boot them. That is a hardware
limit, not a project one - on such a setup the build is the last gate that can be
run, and the checklist below waits for an x86/x64 Windows machine or a
USB-attached WP8.1 device.

One thing the build cannot tell you either is whether the phone implements the platform members the
app compiled against: `AesGcm`, `DisplayRequest` and `DatagramSocket` are all
in the WP8.1 reference metadata, and a member that is only declared answers `E_NOTIMPL` at run time.
That is why the cipher the handshake needs is written out in `Protocol/Crypto/AesGcm.cs`, and a
failure the app survives is written down as
`DIAG <where>: <type> 0x<HRESULT> <message>` (`Services/Diag.cs`) - that line is the fastest answer
to "why is the screen empty".

## Reading the phone: its store and its log

The store is the only evidence that outlives a run - `diag.log` for what happened,
`chats.json` for the list the next start draws, `messages_*.json` for the conversations.
There are two doors to it, and the first one works with the phone on the lock screen:

**The platform tool (this is the one to use).** `ISETool`, with the target `de` for a
connected phone. `xd` is the *emulator*: against a real phone it answers
`Errore: 0x80131622` or `Errore: Timeout` - which reads exactly like a phone that is not
connected, and cost an hour on 24 October 2026.

```bash
T="/c/Program Files (x86)/Microsoft SDKs/Windows Phone/v8.1/Tools/IsolatedStorageExplorerTool/ISETool.exe"
mkdir -p .tools/phone-now9
"$T" ts de 7ccc5b77-3cf2-4020-92a7-9542b250bb49 "$(cygpath -w .tools/phone-now9)"
"$T" dir de 7ccc5b77-3cf2-4020-92a7-9542b250bb49          # the root, to prove the door
"$T" EnumerateDevices                                       # what else there is to talk to
```

`ts` takes the snapshot into `<desktop-path>\IsolatedStore\` - the directory has to exist
first - and prints `Completato.` when it has. The product id is the one in
`WhatsappApp/Package.appxmanifest`.

**The file explorer (fallback, and the only one that shows a deleted file).**
`Phone\Data\Users\DefApps\AppData\<product-id>\LocalState`. That `Data` segment exists
only while the phone is **unlocked**: when `Phone` lists just `Documents, Downloads,
Music, Pictures, Ringtones, Videos` the phone is on its lock screen, and retrying will
not find it - `.tools/pull-localstate.ps1` prints `MISSING Data` in exactly that state,
which is not a missing app.

What to read in the snapshot:

- `diag.log` - the run markers (`=== run started 2026-10-24T08:25:00 ===`) separate a
  launch from the background checks that follow it. A `diag.log~RF*.TMP` beside it is the
  previous copy left by an interrupted write: `diag.log` is the current one.
- `chats.json` - `Chats` is the list the next start draws, `HistoryComplete` is whether
  the sync ever finished. Count the rows here and compare them with the number of
  `messages_*.json` files: **56 tails against 2 rows** is a list that lost 54
  conversations, and the tails are what can give them back (each message carries its
  `ChatId`, `Text` and `Timestamp`).
- `messages_<chat id>.json` - one conversation's tail, keyed on the JID with the
  punctuation stripped (`393761539975@s.whatsapp.net` -> `393761539975swhatsappnet`,
  `120363384070968155@g.us` -> `120363384070968155gus`).
- `name-cache.json`, `avatar-cache.json`, `calls.json`, `status.json` - the rest of the
  screens' copies; same rule, they say what the app had on disk.

## On-device checklist

1. Deploy a build with nothing stored: the app opens the pairing page by itself and
   draws a code. Scan it from WhatsApp on another phone (*Linked devices*), then
   open the account screen: it shows the number this phone is linked as.
2. Let the page sit: its six codes rotate on a timer, and when the last one is
   spent it takes the dead code away and says it expired.
3. Watch the Output window at start-up: no `DIAG` line reporting a failure.
4. Send a text message and an image (attach + caption); verify the outgoing bubble
   shows sent / delivered / failed correctly.
5. Receive a message with the app open and with it closed: the unread badge must
   appear only in the second case.
6. Switch section with the bottom bar three times: the highlighted icon follows,
   lists keep their scroll position, and **Back** exits instead of walking back
   through the sections.
7. Change the device language (Settings > Time & language) and reopen: every
   visible string must switch between English and Italian.
8. Open the software keyboard on the chat page: the input row must stay above it.
9. Scroll a long conversation: no blank rows, no stutter (the item containers are
   deliberately styled to keep per-item layout cheap).
10. After a change to the socket layer, the log must show one login and no `DIAG`
    line naming the transport. Repeated reconnects, or a failure whose site is the
    socket, mean the handshake or the framing is the place to look - and the
    protocol suite's `--connect` run is the faster check.
11. On a fresh install the app asks for a pairing on its own, without anyone
    pressing anything: the page that draws the code is what a phone with no
    account opens on.
12. A node the phone cannot read shows up as a `DIAG` line from the decoder (a
    `ProtocolException`). After a change to the message decoder that line must not
    appear: the message it named was dropped, and the field it names is the one to
    look at.
13. After linking, the chat list shows the conversations that exist in WhatsApp -
    not the empty address book - most recent first, with the last message as the
    preview and a profile picture where the person has one (rows without a picture
    keep their coloured initials). **Within a minute of the pairing**, with no empty
    state left on screen, and the history arrives behind a media download as often
    as inline: the run's `DIAG` lines name it either way - `native: ... byte(s) of
    downloaded history`, then `native: history sync type N carried K
    conversation(s), M row(s) now`, then `native: the history is complete, ...`. A
    list that stays empty is named too: `carries neither an inline payload nor a
    mediaKey/directPath` is a server that sent no history, and `DIAG failed:
    NativeConnection/history` is a blob that refused, with its address in the
    sentence.
14. Receive a message in a chat that is not open: a toast appears with the sender
    and the text, and the tile badge counts it. Open that chat: the badge goes
    down. Turn the notifications switch off in the settings: no toast, no badge.
15. The toast must **not** appear for a message in the chat that is currently open.
16. Start a new chat three ways: type a number, tap *Choose from contacts* (the
    system picker opens; cancelling must leave the dialog usable), and tap one of
    the conversations listed under it. A contact whose number is stored with
    spaces or a `+` must still open the right chat.
17. The unread count must be visible on the **tile** as well as in the badge, and
    both must go back to nothing when the last unread chat is opened. The profile
    picture in the chat list must be a **circle**, not a square or a rounded
    square, and a row without a picture must still show its coloured initials.
18. Background the app for a couple of minutes, then bring it back: it must
    reconnect on its own and the conversation list must refresh - without opening
    the    settings page and without pressing anything. The Output window must show the
    connection coming back on its own (a fresh `DIAG ok: whatsapp: connected`) and
    no `DIAG` line naming the transport.
19. Turn the phone's Wi-Fi off and back on while the app is open: within about a
    minute the app must recover by itself (the same lines as above).
20. Unread counts, like WhatsApp: with the list open, a message in chat A puts a
    number on A's row and on the badge. Open A: the number goes. **While A is
    open**, a new message in A must not put a number on A's row (it is being read
    as it arrives) and must **not** raise a toast; a message in B must still put a
    number on B's row and raise a toast. Leave A and come back: nothing left over.
21. Open a chat, background the app, receive messages **in that chat**, then
    return: the app reconnects and the messages are read on screen. They must not
    haunt the badge afterwards - this was the path where an exclusion in the
    counter lost them for good.
22. Chat list: people show their profile picture, and initials appear only when
    WhatsApp has none for them. If every row shows initials, the picture query was
    never answered: the run's `DIAG` lines name the call that failed.
23. Chat list: a group row shows the group's subject, not `Group` followed by its
    number. If it shows the number, `/user/my/groups` did not answer and the row
    fell back to the chat list's name.
24. Chat: the attach button opens the system picker. If nothing happens, look for
    `ChatPage/image` in the diagnostics - the picker call is the one WP8.1
    implements, and the file only arrives after the app is reactivated.
25. After choosing an image in the picker the app comes back **by itself** with
    the image in the preview bar (it was deactivated, not closed), and Send sends
    it as an image.
26. Share: in Photos, tapping Share lists WhatsApp; choosing it opens the app on
    the chat list with the notice that an image is waiting, and opening a chat
    shows it in the preview bar.
27. Open a chat that already has messages, right after starting the app: the
    conversation appears, oldest first, with its times, and the page ends at the
    bottom. An empty chat here means `messages` was never answered.
28. An old photo in that history shows a bubble with `[Image]`, not an empty
    bubble.
29. Open that chat again: no duplicates appear, and no toast fires while the
    history loads. The unread number on the chat list must not move either.
30. Live tile: pin the app, then receive a message with the app in the background
    so the count goes up. The Start tile must show the app icon **with the
    number**; with the count back at zero it returns to the tile in the manifest.
    An icon with no number is a badge problem, a tile with no icon at all is
    `check-tile`'s bug (the payload sent no `src`).
    - The medium tile shows the sender's **name in the large line** and the
      message under it, and the number in the corner badge. A count in the large
      line is the build before 23 October 2026.
    - Wait through a few rotations with one unanswered person: the tile must
      alternate the message and the same person's picture alone. Half a tile
      covered by a face with no text at all is that second turn, not a bug.
    - Two people unanswered: both names appear, and any spare rotation is a
      picture. Five people: all five names, no picture turn - a name is never
      traded for somebody else's face.
    - `DIAG ok: tile: the decoder answered NxN instead of 336x336, so it is
      scaled here` means this phone's decoder ignores the requested scale, which
      is what read past the buffer on 23 October 2026 (`IndexOutOfRangeException`
      in `TileCanvas.Draw`). The line is expected; a `TileImage/Store` failure is
      not.
    - Resize the tile through all three sizes. Medium and large: the person and
      the message over their picture. Wide (310x150): the picture is **whole** and
      centred on teal, not a magnified band of a face cut at the chin - that band
      is the square card filled into the wide binding, and it means
      `TileImage.WideOf` was not used for that binding. Large (310x310): the tile
      shows the conversation; the green logo and a number instead is the manifest
      fallback, so the 310x310 binding was not posted.
    - Smallest size (71x71): the sender's picture, not the app mark. The mark
      there is a chat that has no picture at all.
    - Pin a chat and open its tile: the name in the large line and the last
      message under it, the picture whole at every size, and the count in the
      badge. A count "3" as the large line is the build before 23 October 2026.
31. Memory: the first line of the debug output is `DIAG ok: memory budget N MB`.
    On a 512 MB phone that is the limit being watched, and it is roughly half of
    what a 1 GB phone reports. Scroll a long chat list on a 512 MB phone: the app
    must not be closed, and `DIAG ok: memory under pressure: releasing decoded
    images` says when it dropped the avatars - the rows fall back to initials and
    come back later.
32. Title bar: the bubble with the plus opens the new-chat dialog and the sliders
    open the settings, in both orientations, and a long press reads the name of
    the button under the finger. If an icon opens the other action, the build on
    the phone is not the one in this tree: `node tools/check-actions.js` says what
    the source does and `obj/x86/Debug/Pages/ChatsPage.g.cs` says what the build
    did.
33. Start the app from the tile: the chat list is already on screen, with names and
    last messages, before the connection is up, and it refreshes by itself within a
    few seconds. Settings must not have to be opened.
34. Have someone send a message while the app is closed, then open the app: that
    chat is at the top and shows an unread number. Open it and go back: the number is
    gone.
35. Share a photo from Photos: the app opens on the chats with the image ready, and
    it does not crash. Share a short video (under about 6 MB): it appears in the chat
    and the other side receives a video, not a broken image.
36. Open a chat whose messages arrived while the app was closed: a photo shows
    `[Image]`. Tap it once, wait, tap it again: the image opens full screen, and a
    tap closes it.
37. Send a photo yourself and tap it: it opens full screen. Tap it again: it closes.
38. Have someone send a video longer than about 6 MB while the app is closed, then
    open the chat: the row shows the play box (not a bare word). Tap it once, wait for
    the pieces, tap it again: the video plays with the system controls, and the X
    closes it. Leave and come back mid-download: it does not lock up or play half a
    file.
39. Send a video yourself: the play box appears while it is in flight, and tapping it
    does nothing, because the app does not keep the bytes it sent.
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
45. A voice note received from WhatsApp shows a play bar, and tapping it plays the
    note; one the phone cannot decode says it cannot be played instead of staying
    silent.
46. A document received from WhatsApp shows a document bar and the file name; tapping
    it opens the phone's viewer, or says there is no app for it.
47. Chat list: a group row shows the group's **picture**, not just its initials.
    If every group shows initials while people show pictures, the picture query for
    a `@g.us` JID was never answered: `Protocol/Client/ProfilePictures.cs` builds it
    and the run's `DIAG` lines name it.
48. Hold a row for about a second: a menu with Pin, Mute and Delete chat appears
    next to that row, and it does not appear again when the finger is lifted. Pin
    it and leave the list: the row is at the top with the pin mark, and it is still
    there after killing and reopening the app. Mute it: a message that arrives in
    that chat raises no toast, and its unread number still goes up.
49. The three-dots button at the top left opens a menu with Pin a chat and Unpin
    all. Pin a chat opens a list where the pinned chats are already ticked;
    ticking two and pressing Done puts both at the top, and Unpin all drops every
    pin. A long press on the button reads More.
50. Delete a chat from the row menu: the confirmation says it disappears from this
    phone, the row goes, and the thread is gone when the chat list refreshes.
    Have someone write in that chat: it comes back with the new message. Kill and
    reopen the app before that message: the deleted row stays deleted.
51. Title bar: the whole word `WhatsApp` is on screen, with no letters missing on
    the right, the three dots at the left edge and the two icons at the right. If
    the title is still cut, the build on the phone is not the one in this tree:
    `node tools/check-actions.js` says what the source does. The theme minimums
    behind this are in `maintain-the-app`, under the known gotchas.
52. Open a chat: the photo button sits at the left edge of the input row, the send
    button at the right edge, and the text box between them is about 376 px wide.
    Go back: the arrow is at the left edge and the name starts right after it. The
    same on the settings page, whose back arrow is a 48 px button and not a 109 px
    one.
53. Send a text message while a large video is still uploading (the progress
    line keeps moving): the video arrives whole, the text is not lost, and the
    connection does not drop. A dropped connection here means two writes on one
    socket: see the `SerialQueue` gotcha in `maintain-the-app`.
54. Have someone send a video of about 30 MB (several pieces) with the app closed,
    then open the chat and tap the play box: the video plays through to the end,
    and the file on the phone is as long as the original. Half a video, or a
    video that says it cannot be played, means the pieces were assembled two at
    a time: see `IncomingMediaStore` and the `SerialQueue` gotcha.
55. Pin two chats, then press Unpin all and immediately kill the app: on the next
    start no chat is pinned. A pin that comes back means two writes of the same
    file overlapped: see `ChatPreferences`.
56. Restart: close the app from the phone (long-press the back arrow), then open it
    again. The chat list shows the pictures of the conversations you have seen
    before, not the initials, and they are there before WhatsApp answers (the
    `DIAG ok: whatsapp: connected` line arrives after them). Initials here mean the
    cache was not read before the rows, or was never written: see `AvatarCache`.
    The **rows** have to be there too, and before any message arrives: a list that
    starts empty and fills one chat at a time as messages come in means `chats.json`
    was never written - the snapshot wrote the conversations' tails first and the close
    landed in the middle (23 October 2026). The log says which half arrived:
    `cache: the list written, N row(s)` before `cache: N conversation(s) written`.
    A list shorter than the conversations on the phone is the other half of the same
    rule: the log says `cache: N conversation(s) given back by their tails (M on the
    phone)`, and it can only say that where `M` is greater than the rows of
    `chats.json`. See `check-chat-snapshot.js`.

    Then **kill it, do not close it**: hold the back arrow, or stop the debugger, right
    after a message arrives, and open the app again. The row is there. It is there
    because `cache: the list written, N row(s), mid-run` appears a few seconds after the
    message and not at the end of the run: a run that is killed has no end for the
    snapshot to hang on (24 October 2026, a debugger stop with 2 rows on disk and 56
    conversations on the phone).
57. The cache file: no `DIAG failed` line for `AvatarCache`, and a second launch does
    not rewrite `avatar-cache.json` when nothing new arrived (its timestamp in the
    app folder stays the same). A file that grows at every launch is a cache with no
    cap, or one written for rows that already had the bytes.
58. Under pressure: on a 512 MB phone, open and close a few chats until
    `DIAG ok: memory under pressure: releasing decoded images` appears. Go back to
    the chat list: the pictures are drawn again (the bytes were kept, only the
    decoded bitmaps were dropped). Initials that stay mean `RestoreAvatars` was not
    called, or the row had no bytes to rebuild from.
59. A picture the app already has: switch between Chats and Calls a few times.
    The second chat list shows the same pictures without asking WhatsApp for them
    again (`AvatarCache`); a query for every visit means the cache was not read.
60. Open a chat whose contact has a picture: the header shows the round picture.
    Tap it: the picture fills the screen, and a tap closes it. Nothing else
    navigates.
61. Tap the name in the same chat: the contact-info page opens with the big
    picture, the name and, when the server knows them, the number and the about
    text.
62. Open a group chat and tap the name: the page shows the group description and
    the members, with the admin label on the ones that have it. A member without a
    name shows the number.
63. Open a chat with no picture at all: the header shows the initials, tapping them
    does nothing, and the name still opens the info page.
64. Open the info page with no connection: after a moment it says there is no
    information from the server, instead of staying in a loading state forever.
