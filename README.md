**English** | [Italiano](README.it.md)

<p align="center">
  <img src="WhatsappApp/Assets/WhatsAppLogo.png" width="128" alt="WhatsApp for Windows Phone 8.1 app icon">
</p>

<h1 align="center">WhatsApp for Windows Phone 8.1</h1>

<p align="center">
  <strong>A WhatsApp client that runs on a 2014 phone, written from the protocol up.</strong><br>
  The phone talks to WhatsApp itself: Noise handshake, binary nodes, the Signal ratchet, media.
  There is no server to run and nothing to configure.
</p>

<p align="center">
  <a href="#build-it">Build it</a> ·
  <a href="#use-it">Use it</a> ·
  <a href="#how-it-works">How it works</a> ·
  <a href="#troubleshooting">Troubleshooting</a>
</p>

<p align="center">
  <a href="#build-it"><img src="https://img.shields.io/badge/platform-Windows%20Phone%208.1-2ea44f" alt="Windows Phone 8.1"></a>
  <a href="README.it.md"><img src="https://img.shields.io/badge/languages-English%20%7C%20Italiano-blue" alt="English and Italian"></a>
</p>

Windows Phone 8.1 lost its official WhatsApp client years ago. This one is open source, lives in a
single app on the phone, and speaks WhatsApp's own WebSocket protocol instead of talking to a
bridge on a PC. It runs on a real WP8.1 device or the x86 emulator. It is free and it needs no
account beyond the WhatsApp number you link.

There is no installer and no release: you build it from source with Visual Studio 2013.

## What it is

One app, one process, no backend. `Protocol/` is the wire (handshake, nodes, Signal sessions,
media keys), `Protocol/Client/` is the session (login, history sync, send path, receipts, call
log, status feed), and `Services/NativeConnection.cs` owns the socket and the events the pages
listen to.

The three tabs are the released app's: chats, status, calls, under a fixed tab strip with the
WP8 app bar. A conversation is a page of its own.

| Page | What it is |
| --- | --- |
| `Pages/HomePage.xaml` | chats, status and calls, swiped under `Controls/TabHeader.xaml` |
| `Controls/StatusPane.xaml` | the status feed |
| `Controls/CallsPane.xaml` | the call log |
| `Pages/ChatPage.xaml` | conversation |
| `Pages/SettingsPage.xaml` | theme (the chat wallpaper) and the way to the account |
| `Pages/ConnectionPage.xaml` | the linked number and the connection state |
| `Pages/PairPage.xaml` | the QR code that links a device |

What works today:

- text messages, images and voice notes, with sent/delivered/read marks on every bubble, and a **Try again** line under a bubble whose send did not make it
- a search over the chat list, by name, by what the chat last said, or by the number
- a long press on a message copies its words, deletes it on this phone, or takes it back from everybody within two days - and a message another device takes back goes from this one too
- the real chat list from the history sync, avatars, unread counts, groups, and the names saved in the primary phone's WhatsApp address book, which arrive encrypted in the account's app state
- typing indicators in both directions
- the status feed: what contacts posted, a tap opens the photo, the video or the words, and the view receipt goes back to `status@broadcast`. The feed of the last day is kept on the phone, so it is still there after a restart, and an update whose photo did not arrive has a **Try again** button instead of a dead end
- the call log, both directions, with missed calls spelled out
- a live tile that rotates the people waiting - their name and their message, and between one message and the next the picture alone of somebody still unanswered - plus a pinned-chat tile, lock screen text, and a background check about every 15 minutes
- theme picker with the wallpapers the released package ships, plus the doodle and plain white
- English and Italian, following the phone's language

## Good for, not for

**Good for:** keeping a WP8.1 phone usable as a second WhatsApp device; reading the protocol in
C# 5; a phone that is not your main number.

**Not for:** replacing WhatsApp on your main phone. This is an unofficial client, it breaks
WhatsApp's Terms of Service, and the number that links it can be banned. Use a test or secondary
number. It is also not a complete client yet: voice notes in Ogg/Opus do not play, older history
is not fetched on demand, and edits made from other devices are not read.

## Build it

You need **Visual Studio 2013 (v12.0) with the Windows Phone 8.1 SDK**. Newer VS versions do not
carry that toolchain. Build from a path on a local disk, not from a shared folder or a network
drive.

**Command line** (from the repository root):

```bash
msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86
```

The build must end with `0 Error(s)` and leave
`WhatsappApp\AppPackages\WhatsApp_<version>_Debug_Test\WhatsApp_<version>_x86_Debug.appxbundle`.
For the phone, build `Release` on `ARM` instead and use **Build > Deploy**.

**From Visual Studio**: open `WhatsappApp.sln`, pick `Debug` / `x86` (emulator) or `Release` /
`ARM` (phone), then F5.

<details>
<summary><strong>What the build needs and what it refuses</strong> The toolchain is old on purpose.</summary>
<br>

- The WP8.1 toolchain compiles with the **C# 5** compiler. Interpolated strings, `?.`,
  expression-bodied members, auto-property initializers, pattern matching and `out var` do not
  compile here. `node tools/check-csharp5.js` lists every offending file, line and construct, and
  also catches WinRT members that exist on Windows 10 but not in the WP8.1 projection.
- Build inside `C:\Mac\Home\...` (the Parallels share) and the second XAML pass fails every time
  with `Xaml Internal Error error WMC9999: The given key was not present in the dictionary`. Copy
  the tree to a local disk (`robocopy <share> C:\wp81 /E`) and it builds.
- The WP8.1 emulator does not start on an Apple Silicon Mac: its images are x86 and need Hyper-V.
  Use an x86/x64 Windows machine or a phone over USB.

</details>

## Use it

1. Open the app. The first screen is the QR code, drawn by the app itself (no image comes from
   anywhere).
2. On your main phone: WhatsApp > linked devices > link a device, and scan it.
3. The app logs in, reads the history sync, and fills the chat list.
4. Tap a chat to write. Enter sends. The paperclip attaches a photo, the microphone records a
   voice note.
5. Swipe to the status tab to see what your contacts posted. Tap one to open it: the photo or the
   video, or the words, with a caption under it.
6. The calls tab lists the call log WhatsApp sent with the sync.

Tips:

- A chat can be pinned to the Start screen from its long press menu. The tile carries the
  person's picture with their name in the large line and their last message under it, and the
  number of waiting messages on the badge. The app's own tile reads the same way, and between
  one message and the next it shows the face alone of somebody who is waiting for an answer.
  A pinned tile is drawn again at every start, from the picture and the count the phone has, so
  a new version of the app does not leave the card of the previous one on the Start screen. The
  picture it draws is the full-size profile picture it asks WhatsApp for once the chat is pinned,
  not the small preview the chat list uses: the tile is the one place that shows a face at the
  size of the tile.
- An update somebody posted and a call nobody answered each raise a notification, in the same
  area as a message: the author's name, and nothing else - the update itself is opened where it is.
  Each is announced once, from a mark kept on the phone, because WhatsApp re-sends the whole day
  of updates and the whole call log at every login.
- The Calls badge counts the missed calls that arrived since the log was last on screen, so it goes
  back to zero when the tab is opened instead of showing the same number for good.
- The background check is on by default. Turn it off in Settings if the phone should stay quiet.
- If the app opens an empty list and stays there, open the Diagnostics page from the empty state:
  it shows the last `DIAG` lines.

## How it works

Every step is pinned against the reference implementations (`@whiskeysockets/baileys` and
`whatsmeow`), and the tests that keep it honest live in `ProtocolTests/`.

- **Transport**: a WebSocket to `wss://web.whatsapp.com/ws/chat` over TLS. Everything on it is a
  binary node: a tag, an attribute map, children or bytes (`Protocol/WABinary/`). A node's length
  is checked against a ceiling before it is read.
- **Handshake**: the Noise protocol handshake with the server's certificate chain, verified on the
  phone (`Protocol/Noise/NoiseHandshake.cs`).
- **Pairing**: the QR is encoded by the app (`Protocol/Qr/QrCode.cs`), and linking stores the
  account in the protocol's own credential store in the app folder.
- **Messages**: X3DH for a new session, the double ratchet for everything after it, the sender key
  for a group (`Protocol/Signal/`). A message with media names the keys and the address of the
  file; the bytes go to and from WhatsApp's media host and are encrypted and decrypted on the
  phone (`Protocol/Media/`).
- **Session**: the login, the node classifier, the history sync, the send path with its receipts,
  the profile-picture query, the call log and the status feed (`Protocol/Client/`).
- **One connection, one owner**: only the newest attempt publishes its socket and read loop, so a
  socket that fails cannot close the one that succeeded. The client reconnects by itself, and a
  silent session is closed and reopened instead of kept as a socket that looks alive.

A status is not a chat: it lives in `status@broadcast`, its author is the `participant`, and
watching it is a `read` receipt to that list carrying the status id and the author. The feed keeps
them out of the chat list, which is why a phone with a day of statuses does not show a row called
"status" at the top.

The interface icons are vector `Path` elements with the geometry inlined on each `Path`: WP8.1 has
no `Segoe MDL2 Assets`, so a glyph font renders blank. `node tools/check-icons.js` enforces that,
the geometry element form, and that the same icon is the same geometry everywhere.

## Troubleshooting

<details>
<summary><strong>The build fails with WMC9999 "The given key was not present in the dictionary"</strong></summary>
<br>

The project is on the Mac share. The second XAML compiler pass always fails there. Copy the tree
to a local disk and build again; nothing in the code needs to change.
</details>

<details>
<summary><strong>The app opens on an empty chat list</strong></summary>
<br>

Either the phone is not linked yet (the first screen is the QR), or the connection ended before
the history sync. Open the Diagnostics page from the empty state: the `DIAG` lines name the step
the connection reached. The same lines are in `diag.log` in the app folder, which
`ISETool.exe ts` copies off the phone.
</details>

<details>
<summary><strong>Messages arrive only every 15 minutes while the app is closed</strong></summary>
<br>

That is the background check, not a bug. WP8.1 closes the socket when it suspends the app and
there is no cloud service to push through, so `BackgroundSync` wakes on a `TimeTrigger`, connects,
reads what is waiting, updates the tile and the lock screen, and closes. 15 minutes is the
shortest interval a `TimeTrigger` accepts; the system decides the exact moment and can delay it.
Turn it off in Settings if you prefer nothing in the background.

The permission is the phone's, not the switch's: the user can take background work away from the
app in the phone's own settings, and battery saver does it on its own for a while. The
registration stays and nothing wakes, so Settings reads the permission again on every visit and
says so under the switch instead of showing a switch that is on for a check that never runs.

Diagnostics answers the same question for a report: the interval, whether the task is registered,
whether the phone permits it, and what the last wake did - when it started, how it ended, how long
it took and what it put on the tile. `running` with no ending is a wake the system took down at its
30-second ceiling.

A tap on the tile - and on the lock screen, which shows the same tile - opens what arrived last:
an update nobody has watched goes to Status, a missed call goes to Calls, and the chats are the
fallback. WP8.1 hands a tap on the tile the same empty arguments as the app list, so the app
writes down what the phone is advertising when it draws the badge and takes it back once at the
next start. The section it lands on then counts as seen, so the same news cannot pull the next
start to the same tab.
</details>

<details>
<summary><strong>A voice note from WhatsApp does not play</strong></summary>
<br>

WhatsApp's own voice notes are Ogg/Opus, and WP8.1 has no Opus decoder. A note recorded here goes
out as M4A/AAC, which this phone records and plays. The server that used to convert both ways is
gone with the rest of the backend.
</details>

<details>
<summary><strong>The chat list shows numbers instead of names</strong></summary>
<br>

Names come from the history sync's push names, the group metadata query, and the phone's own
address book, which the app reads only after you grant it. A contact whose name nobody shared and
who is not saved on the phone stays a number.
</details>

<details>
<summary><strong>A photo in a chat or a status shows a word until tapped</strong></summary>
<br>

History rows carry the keys and the address of the file, not the file. The app downloads it the
first time you tap it, then keeps it. A status photo that still does not appear after the download
has usually expired: statuses live for a day.
</details>

## Commands

Run these from the repository root before a pull request. The guards are the contract: each one
catches a failure the build cannot see.

```
node tools/check-csharp5.js         C# 6/7 syntax and WinRT members WP8.1 does not have
node tools/check-icons.js           inline vector icons, element form, same icon same geometry
node tools/check-resw.js --strict   x:Uid, Loc.Get, both .resw files, no unused key
node tools/check-docs.js            README pair, same headings, disclosure last, no emoji
node tools/check-memory.js          decode width, cache caps, per-chat ceiling
node tools/check-tile.js            tile payload, asset sizes, and a tile the platform refuses
node tools/check-actions.js         the app bar buttons
node tools/check-fire-and-forget.js no task whose fault nobody observes
node tools/check-project-files.js   every file the projects compile
node tools/check-xaml-names.js      one x:Name per namescope
node tools/check-client-state.js    the login's passive/active sentences and the notification channel
node tools/check-background-task.js the task's activatable class, its 30 s budget, and the phone's permission
node tools/check-diagnostics.js     the log that survives a crash
node tools/check-launch-route.js    a tap on the tile: what it records and the one start that uses it
node tools/check-notification-alerts.js  an update and a missed call announced once, and a badge that drops
node tools/check-held-group.js       a group message that waits for its sender key instead of a retry
node tools/check-avatar-refusal.js   a chat that refuses its picture, told apart from a missing login
node tools/check-no-exception-probes.js  the padding is read, not guessed with a throw per trim
node tools/check-app-state.js       the saved names: the shared keys are kept, the sync is asked, the rows are told
node tools/check-chat-list-refresh.js  one chat per person, and a list that asks for its rows again
node tools/check-one-row-per-chat.js  a chat row is created canonical and once: list, groups, statuses
node tools/check-chat-list-source.js, check-history-sync.js, check-send-status.js,
     check-attachment-file.js, check-chat-scroll.js, check-no-server.js, check-status-viewer.js
```

The whole gate, one line:

```bash
for f in tools/check-*.js; do node "$f" || exit 1; done
node --test tools/test/*.test.js
node tools/test-protocol.js
```

`tools/test-protocol.js` builds and runs the 376 protocol tests in `ProtocolTests/`. The protocol
tests are plain C# with a tiny runner, because the WP8.1 toolchain cannot host a modern test
framework.

## Good to know

- Older messages are only the ones WhatsApp pushed after the login: this client does not ask for a
  conversation's older history on demand.
- Deletions and edits made on another device are not read yet. A message revoked elsewhere stays
  on screen until a fresh login re-syncs the conversation.
- Pinning, muting and deleting live on this phone only, in `chat-preferences.json`. Nothing about
  them is sent to WhatsApp, so another device does not see them and clearing the app data loses
  them.
- A chat's unread number comes from WhatsApp with the history sync and is cleared when you open
  the conversation, which tells the server. Messages that arrive while the app is closed are
  counted by the next sync.
- A received video is written to the app's local folder and is not cleaned up when the app closes.
- The last 60 messages of every conversation are written to the phone when the sync finishes, so a
  chat opened after a restart shows them at once.
- On a 512 MB phone the app gets roughly 185 MB. It decodes every picture at the size it is drawn,
  drops decoded avatars under memory pressure, and keeps at most 200 messages per chat in memory.
  `node tools/check-memory.js` guards all three.
- Typing indicators need the account to be present, which is what contacts see as online. With the
  app closed nothing is sent or received.
- The live tile needs the app to have run since the count changed; a message that arrives while
  the app is suspended is counted by the background check.
- A file shared into the app from another app is read at the moment the share is handed over,
  because the app can be terminated while the picker is open. A very large video may not fit in
  memory on a 512 MB phone.

## Contributing

Issues and pull requests are welcome. Read `.agents/skills/maintain-the-app/SKILL.md` before
touching code: C# 5 only, inline vector icons, no hardcoded user-visible strings, docs always in
pairs. The index of the project skills is in `.agents/skills/README.md`.

1. Fork and branch: `fix/...`, `feat/...`, `docs/...`.
2. Make the change, then run the gate above. It must be green.
3. Build the app on Windows and say in the pull request that it reports `0 Error(s)`. There is no
   C# test host for the app, so the build is its gate.
4. Write the commit subject as `type: short imperative` in English and say why the change is
   needed, not only what it does. The history is the changelog.
5. User-visible text goes into **both** `.resw` files, and `README.md` and `README.it.md` change in
   the same commit.

A useful bug report has the `DIAG` lines from the Output window, the build you ran, and what the
phone did.

The documents that explain the project exist in two languages and are kept in step. Adding,
moving or renaming a section means doing it in both READMEs in the same commit, and the
`## Disclosure` section stays last in both. `node tools/check-docs.js` refuses a pair whose
headings differ, a document without the disclosure, and any emoji other than the warning sign.

## Disclaimer

- This is an unofficial project, not affiliated with WhatsApp or Meta.
- It uses unofficial methods to connect to WhatsApp, which violates WhatsApp's Terms of Service.
- Linking it may get your phone number permanently banned.
- Only use a test or secondary phone number.

## License

MIT, community maintained. Use at your own risk.

## Disclosure

**This project is open source, and it needs maintainers.** Issues, translations, reviews,
documentation and pull requests are all welcome, and so is anyone who wants to help it grow.

**The author does not accept responsibility for the WhatsApp account used to sign in.** Linking
this client means connecting an unofficial client to WhatsApp, which is against WhatsApp's Terms
of Service, and the account can be permanently banned. Use a test or secondary number, and only if
you accept that risk yourself.
