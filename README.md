# WhatsApp for Windows Phone 8.1

**English** | [Italiano](README.it.md)

A community-maintained WhatsApp client for Windows Phone 8.1 (Universal Windows Platform). This project includes a full WhatsApp-like UI and a bridge server to connect to real WhatsApp servers.

## Projects

### WhatsappApp (Windows Phone 8.1 App)

The main client app with an authentic WhatsApp user interface.

**Features:**
- WhatsApp green theme (header #075E54, accent #25D366, chat bubbles)
- Chat list with avatars and unread badges
- Live tile with how many messages are waiting, the last sender's picture and their name
- Message bubbles with timestamps and sent/delivered/read status
- Text messaging with Enter-to-send
- Image attachment: pick photos from the gallery and send them through the bridge
- Image preview in chat bubbles (base64 over TCP)
- The app finds the adapter on the local network by itself, so there is no address to type
- Login from the phone: the QR code or the phone pairing code is shown full screen in the app
- Typing indicator: three animated dots in the conversation while the other person writes, and the same state is sent back while you write
- UI in the device language: English and Italian

**Architecture:**

The app uses a TCP connection with length-prefixed JSON messages:

```
[4 bytes: UInt32 LE message length] [N bytes: UTF-8 JSON ChatMessage]
```

The `ChatMessage` model uses `DataContractJsonSerializer` for serialization; its
`Timestamp` field is the plain `/Date(<ms>)/` value, with no backslashes.

#### Calls

The Calls tab lists **incoming** calls only, taken from the chats the adapter
scanned (`CALLS_CHAT_LIMIT`, default 25), refreshed when the tab is opened or with
the Refresh button. GOWA keeps no outgoing-call record, so there is nothing else
to show.

#### Chats and new chats

The chat list is the account's real conversation list (`GET /chats`, bound by
`CHATS_LIMIT`), not its address book, which is empty on a freshly linked device.
Each row carries the last message and the profile picture
(`GET /user/avatar` and then the CDN address it returns, so two requests per
chat, a group included; disabled with `CHATS_AVATARS=off`). A group row is named from
`GET /user/my/groups`, one request for all of them, because the chat list has no
usable name for a group and falls back to `Group <number>`.

A new chat can be started in three ways: typing a number with country code,
picking a contact with the system contact picker (the user's consent, so the app
never reads the address book by itself), or tapping a conversation the server
already knows.

While someone is writing, the conversation shows a bubble with three dots, one
rising after the other, and it goes away when they stop. It is the one thing this
app shows that is not a message, and it comes from WhatsApp (`chat_presence`, an
event GOWA 9.5 forwards), not from a guess. The other direction works the same way:
while you type, the adapter tells WhatsApp, and the contact sees the same dots. Both
directions need the account to be online, so the adapter marks it `available` while
the app is connected and `unavailable` when it is not.

Attaching an image uses `PickSingleFileAndContinue`. `PickSingleFileAsync` is
documented as unsupported on Windows Phone, and on the phone it failed silently:
the app is deactivated while the picker is open, and the chosen file arrives at
`App.OnActivated` as a `PickFileContinuation`.

Sharing an image into the app works from any app that offers Share (Photos,
Gallery, a browser): the manifest declares a `windows.shareTarget` extension for
`Bitmap` and `StorageItems`, and `App.OnShareTargetActivated` puts the image in
the same waiting slot the picker uses. The app opens on the chat list, because the
next step is choosing who to send it to.

A voice note is recorded in the app: the microphone button starts
`Windows.Media.Capture.MediaCapture` (audio only, no camera) and writes AAC in
an M4A file in the app folder, which is what this phone records and plays
without a transcoder. The stop button ends the recording, and the file waits in
the same slot a picked photo uses: the preview bar shows it and Send streams it
out. On the adapter side a recorded voice note goes to `POST /send/audio`, which
is what makes WhatsApp draw a voice note with a waveform instead of an audio
file attachment.

### WhatsappServer (.NET Console App)

A simple TCP relay server that broadcasts messages between connected clients.

- Listens on port 8585 (default)
- Relays length-prefixed JSON messages between clients
- Displays connection logs and message previews
- Written in .NET Framework 4.5.1

> **Note:** this relay is superseded by `WhatsappBridge/server.js`, which now
> bridges the WP8 app to a self-hosted **GOWA** server instead of relaying to
> other phones. The .NET project is kept for reference / standalone use.

### GOWA Adapter (Node.js)

A thin adapter that connects the Windows Phone 8.1 app to a self-hosted
[GOWA](https://github.com/vincenzosco/go-whatsapp-web-multidevice) server
(`go-whatsapp-web-multidevice`). It does **not** implement its own WhatsApp
client any more: it uses GOWA's REST API and webhooks.

**Features**

- Login via **QR code** or via **phone number pairing code**, both shown in the app
- Announces itself on the LAN over UDP, so the app finds it without being configured
- Keeps the encrypted (AES-256-GCM) TCP channel between app and adapter
- Sends text, photos, videos and files (`POST /send/message`, `/send/image`, `/send/video`, `/send/file`); an attachment larger than one frame travels in pieces (`media.begin` / `media.chunk` / `media.end`), and media that arrives from WhatsApp comes back the same way in `media` frames that carry their piece index
- Receives incoming messages through a GOWA webhook (HMAC-verified)
- Syncs contacts from `GET /user/my/contacts`
- Lists the account's real **conversations** from `GET /chats` (the address book is empty on a freshly linked device), each with its last message and its **profile picture** from `GET /user/avatar` (two requests per chat, a group included: the endpoint returns the picture's address, not the picture) (`CHATS_LIMIT`, `CHATS_AVATARS`)
- Chats can be **pinned** (the three-dots menu at the top left, or a long press on a row), **muted** (no notification for that conversation; the unread number stays) and **deleted from this phone** (a long press, then Delete). The three are decisions of this phone: the server has no endpoint for any of them, so they are kept in the app's own folder and a chat pinned here is not pinned on your other devices
- A deleted chat comes back when a new message arrives in it, and nothing is deleted on WhatsApp: this app deletes the conversation from this phone, not from the account
- Keeps the last chat list on the phone: the list is on screen while the connection is still coming up, and is refreshed as soon as WhatsApp reports itself connected
- Keeps the connection alive by itself: a watchdog asks for the state every 20 s, and a connection that has been silent for 60 s is dropped and reopened, so the app recovers on its own when WP8.1 closes the socket while it is suspended
- Loads a chat's stored messages when it is opened (`messages`, up to `MESSAGES_LIMIT`), as frames marked `IsHistory`: they are inserted in date order and stay out of the unread count and the toasts
- Tapping an image in a chat opens it over the whole page; tapping it again closes it. A `[Image]` from an old conversation is downloaded first and opens at the next tap
- A chat's profile picture and its name are two targets: tapping the picture opens it over the whole page, tapping the name opens the contact info, with the number, the about text, the business profile and, for a group, the description and the members with their roles (`contact.info`). Everything the server does not have is simply left out, and a page with nothing to show says so
- A received video plays over the whole page on a `MediaElement` with the system transport controls, and closes with the X in the corner. Its bytes arrive in pieces and are written to a file as they come, so a full-length video is never held whole in memory; tapping the play box before the bytes are there downloads them first, like an image
- A received voice note or audio shows a play bar and plays in the same player. WhatsApp sends voice notes as Ogg/Opus and WP8.1 has no Opus decoder, so the adapter converts them to a small mono MP3 first, using `ffmpeg` when it is installed (`FFMPEG_ENABLED`, `FFMPEG_PATH`); without `ffmpeg` the note still arrives and says it cannot be played
- A received document shows its file name and a document bar, and tapping it opens the file with the phone's own app. Its bytes come to a file like a video's, and a document from an old conversation is downloaded on tap first
- A file shared or picked from the Gallery is copied into the app's own folder instead of being read into memory, so a long video is streamed out in pieces rather than taking the app down
- A chat paints from its cached messages before the connection is up, and its last messages are kept per conversation on the phone

**Setup (one command)**

```bash
node tools/start-login.js --download   # --download only the first time
```

It downloads the official GOWA binary for **whatever OS and CPU you are on** into
`.tools/gowa` (macOS Intel/ARM, Linux x64/arm64/armv7/386, Windows x64/386),
verifies the published SHA-256, and unpacks it with `node:zlib` — no `unzip` or
`tar` needed. It then starts `whatsapp rest`, starts `WhatsappBridge/server.js`,
announces the adapter on the LAN over UDP (so the app finds it **by itself**) and
prints the addresses and ports. The login is normally done **on the phone**: the
app shows the QR full screen, so nothing has to be scanned off the computer. A
terminal QR is still available and is drawn as a real scannable code, renewed as
long as it takes; when it does not fit the window the script says so and writes
the PNG to `.tools/gowa/login-qr.png` rather than drawing something truncated.
The adapter registers its own webhook on GOWA.

Which services start is a declarative list (`tools/services.js`), not two
hardcoded children: drop a `WhatsappCallServer/server.js` in the repo and the
launcher starts it too, gives it its own port, shows it in the banner and stops
it with `Ctrl-C` / `--stop` like the rest (`--no-calls` switches it off,
`--list-services` shows the resolved list).

| Option | Effect |
| --- | --- |
| `--code 393401234567` | link with a phone pairing code instead of the QR |
| `--no-bridge` | GOWA and the QR only |
| `--once` | draw a single QR and exit |
| `--no-qr` | draw nothing: the login is done from the phone, in the app (recommended) |
| `--open-qr` | open the code PNG in Preview, where it reloads as the code rotates |
| `--url http://host:3000` | use an already running GOWA |
| `--ui` | also serve GOWA's web dashboard |
| `--stop` | stop a stack started earlier |

The WhatsApp session lives in `.tools/gowa/storages/whatsapp.db` (git ignored),
so later launches reconnect on their own without a new QR. `Ctrl-C` stops GOWA and
the adapter.

Then, in the app: it finds the adapter on the network and connects by itself (there
is still **Enter the address by hand** for a server that cannot be discovered). The
login is done from the phone — the app shows its own full-screen QR and keeps the
screen on while it is visible — or you can reuse the session already linked.

**Requirements:** Node.js 18.13+ and, for the terminal QR, ImageMagick 7
(`magick`). Voice notes also need `ffmpeg` on the machine running the adapter.
Adapter environment variables are documented in
`WhatsappBridge/.env.example` (the file is read at startup; variables already
exported win over it).

**Doing it by hand**, if you prefer to run the pieces yourself: start any GOWA
that speaks the v9 REST API (`whatsapp rest --port=3000 --host=127.0.0.1`), then
`cd WhatsappBridge && cp .env.example .env && npm start`.

To deploy the adapter (plus GOWA) on a NAS or an always-on PC there is a separate
repository: [vincenzosco/docker-whatsappforwp](https://github.com/vincenzosco/docker-whatsappforwp).
It publishes a single-container image with GOWA and this adapter, and the same
server still starts with plain `node server.js`.

## App structure

The app is split into one page per section, with a shared navigation bar
(`WhatsappApp/Controls/SectionNav.xaml`):

| Page | Section |
| --- | --- |
| `Pages/ChatsPage.xaml` | chat list, new chat, access to the settings |
| `Pages/StatusPage.xaml` | status |
| `Pages/CallsPage.xaml` | calls |
| `Pages/ChatPage.xaml` | conversation |
| `Pages/ConnectionPage.xaml` | server configuration and WhatsApp login |

Changing section navigates the root `Frame` and removes from the stack the section
it leaves, so the **Back** button exits the app from any section instead of
walking back through the ones already seen. The three section pages are cached
(`Frame.CacheSize = 3`): moving from one to another does not rebuild the page and
the chat list keeps its scroll position.

## Project skills

In `.agents/skills/` (index in `.agents/skills/README.md`) there are the
instructions to maintain, update, test and release the app: toolchain constraints,
edit recipes, the verification matrix and the deploy checklist. Whoever touches the
code should read them first: they are the project's long memory.

## Contributing

Issues and pull requests are welcome. The project is small on purpose, and the
guards in `tools/` are the contract: a change that passes them locally is almost
always good to merge.

1. Fork the repository and work on a branch (`fix/...`, `feat/...`, `docs/...`).
2. Read `.agents/skills/maintain-the-app/SKILL.md` before touching code: C# 5 only,
   inline vector icons, no hardcoded user-visible strings, docs always in pairs.
3. Make the change, then run the fast gate:

   ```bash
   node tools/check-csharp5.js && node tools/check-icons.js \
     && node tools/check-resw.js --strict && node tools/check-docs.js \
     && node tools/check-framing.js && node tools/check-tile.js \
     && node tools/check-memory.js && node tools/check-actions.js \
     && node tools/check-fire-and-forget.js \
     && node tools/check-project-files.js \
     && node tools/check-chat-list-source.js \
     && node --test "tools/test/**/*.test.js"
   cd WhatsappBridge && npm test
   ```

4. If you touched `WhatsappApp/` or `WhatsappServer/`, build it on Windows
   (`msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86`)
   and say in the pull request that it reports `0 Error(s)`. There is no C# test
   host, so the build is the gate for the app.
5. Write the commit subject as `type: short imperative` in English, and say *why*
   the change is needed, not only what it does: the history is the changelog.
6. User-visible text: add the key to **both** `.resw` files, and update both
   `README.md` and `README.it.md` in the same commit.

A useful bug report has the `DIAG` lines from the Output window, which build you
ran, and what the phone did. If you can, launch `node tools/start-login.js --no-qr`
and attach the adapter log.

## Protocol

The TCP protocol uses length-prefixed JSON messages, compatible with Windows `DataWriter`/`DataReader`:

- 4 bytes: message length (UInt32, Little Endian)
- 1 byte: cipher tag (`1` = AES-256-GCM, `2` = AES-256-CBC + HMAC-SHA256)
- N bytes: encrypted payload

The payload is encrypted with **AES-256-CBC and authenticated with HMAC-SHA256** using a
pre-shared key (`SHA-256` of a passphrase, from which both sides derive two keys with
`HMAC-SHA256`): 16-byte random IV, ciphertext, 32-byte HMAC over IV and ciphertext.
Tag `1` is still accepted and carries a 12-byte IV, the ciphertext and a 16-byte GCM tag,
but **the app always writes tag `2`**: Windows Phone 8.1 answers AES-GCM with
`NotImplementedException 0x80004001`. The adapter replies to each client with the cipher
that client used. The app and the server must use the same passphrase (`BRIDGE_KEY` env
var on the server, constant in `CryptoHelper.cs` in the app).

After decryption, the JSON body follows the `ChatMessage` schema:

```json
{
  "Id": "msg_123",
  "Text": "Hello!",
  "SenderId": "393401234567@s.whatsapp.net",
  "SenderName": "Mario",
  "ChatId": "393401234567@s.whatsapp.net",
  "Timestamp": "\/Date(1750000000000)\/",
  "Status": 1,
  "Type": 0,
  "IsIncoming": true,
  "MediaData": "/9j/4AAQ...base64...",
  "MediaMimeType": "image/jpeg",
  "MediaFileName": "photo.jpg"
}
```

Types: 0=Text, 1=Image, 2=Audio, 3=System
Statuses: 0=Sending, 1=Sent, 2=Delivered, 3=Read, 4=Failed

`Type = 3` frames are **control frames** (`ChatId = "system"`) used for the
WhatsApp login flow: the app sends `login.qr` / `login.code` and the adapter
answers with `qr` / `paircode` / `state` / `contact` / `error` frames. See
`WhatsappBridge/README.md` for the full command table.

A frame length is never trusted: the app fills the 4-byte prefix completely
(`InputStreamOptions.Partial` can split it) and rejects anything outside
`1..8 MiB` (`FrameCodec.MaxFrameLength`), and the adapter drops a client that
announces more than `MAX_FRAME_LENGTH` (the same 8 MiB) instead of buffering it.

`Timestamp` is the one field whose *type* on the wire is worth spelling out: it carries
`/Date(<milliseconds since 1970, UTC>)/`, and no backslashes - the `\/` seen in JSON text is the
reader's escape, not part of the value. Writing the value with backslashes (an adapter bug fixed
in these commits) made the phone throw the whole frame away with
`SerializationException 0x8013150C`, "String was not recognized as a valid DateTime". The app
reads that field as a string and interprets it leniently, so a timestamp it cannot parse costs
the timestamp, not the message.

Both sides pin the byte order explicitly: the adapter writes the length with
`writeUInt32LE` and the app creates its readers and writers through the
`CreateFrameReader`/`CreateFrameWriter` of `WhatsappApp/Services/FrameCodec.cs`,
which set `ByteOrder = ByteOrder.LittleEndian`. WinRT's default is not little-endian, and a
reader that disagrees does not fail loudly: it reads a byte-swapped length
(`0x00000121` came back as `0x21010000`, 553713664) and drops a frame that was
perfectly fine. `tools/check-framing.js` fails the fast gate if a
`DataReader`/`DataWriter` in the socket layer is created any other way.

Everything the server and the app print at run time is English: the adapter's log
and error messages, and the app's `DIAG` lines with the exception messages that
reach them. Source comments are English too: the code explains itself in one
language. The exceptions are the test names and the diagnostics of the guard scripts
under `tools/` - developer tooling that no operator runs - and the localized UI
strings in `Strings/it-IT`, which are translation rather than diagnostics. The error
texts the adapter sends to the app for display are UI content too, and are still
Italian pending the app's own localization.

A connection attempt owns its socket, its `DataReader` and its read loop: only
the newest attempt publishes them and only its loop reads them, so a failed
attempt (a stale saved address, for instance) cannot close the connection that
succeeded. The connect has a 6-second deadline; `0x8007274C` means it expired,
and the app then forgets the saved address and falls back to discovery.

## Building

### WP8 App

Verified toolchain: **Visual Studio 2013 (v12.0) + Windows Phone 8.1 SDK**. The
gate is

```bash
msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86
```

which must close with `0 Error(s)` and produce
`WhatsappApp\AppPackages\WhatsappApp_<version>_Debug_Test\WhatsappApp_<version>_x86_Debug.appxbundle`.

**Build from a path on a local disk, not from the shared folder.** If the project
lives in the Mac share (`C:\Mac\Home\...`), the second pass of the XAML compiler
*always* fails with

```
Microsoft.Windows.UI.Xaml.Common.targets(327,9): Xaml Internal Error error WMC9999:
The given key was not present in the dictionary.
```

even on a freshly cleaned tree and whatever the pages contain: it is the share, not
the code. Copy the project to a disk on the Windows machine and build there
(`robocopy <share> C:\wp81 /E`): same files, `0 Error(s)`.

**The WP8.1 emulator does not start on an Apple Silicon Mac.** The XDE images are
x86 and run on Hyper-V: on an ARM64 Windows guest there is neither x86 Hyper-V nor
those images. Running the app needs an x86/x64 Windows machine (physical or an
Intel VM) or a WP8.1 phone attached over USB.

The Windows Phone 8.1 toolchain compiles the app with the **C# 5** compiler: C# 6/7
syntax (interpolated strings, `?.`, expression-bodied properties, automatic
property initializers, pattern matching, `out var`) does not compile. Before every
build, run:

```bash
node tools/check-csharp5.js
```

It exits with code 0 when every `.cs` file of the solution is C# 5 compatible,
otherwise it lists file, line and the construct to fix. The same script also checks
for members **missing from the Windows Phone 8.1 WinRT projection** (e.g.
`CryptographicBuffer.CreateFromByteArray` with 3 arguments,
`ContentDialog.CloseButtonText`): they compile on Windows 8.1/10 but not on WP8.1.

### GOWA Adapter

```bash
cd WhatsappBridge
npm install
npm test     # unit and integration tests
npm start
```

### Icons, tiles and splash screen

The WhatsApp logo (a white bubble with the handset cut out) is drawn as vector
geometry by `tools/make-brand-assets.js`, which needs ImageMagick 7 (`magick`) and
rewrites the PNGs in `WhatsappApp/Assets/` — already committed, so the script is
only needed when the artwork changes:

```bash
node tools/make-brand-assets.js            # rewrites the PNGs
node tools/make-brand-assets.js --preview  # + ASCII preview to check the logo
```

The interface icons (search, settings, tabs, attachments, send...) do **not** use an
icon font: Windows Phone 8.1 has no `Segoe MDL2 Assets` (it arrived with Windows 10),
so the buttons stayed blank. They are vector `Path` elements with the geometry
**inlined on each `Path`**, preceded by a comment that names the icon:

```xml
<Path Stroke="White" StrokeThickness="2" Width="24" Height="24">
    <!-- IconChats -->
    <Path.Data>
        <PathGeometry>
            <PathGeometry.Figures>
                <PathFigure StartPoint="4,5" IsClosed="True">
                    <PathFigure.Segments>
                        <PolyLineSegment Points="20,5 20,15.5 10.5,15.5 5.5,20 5.5,15.5 4,15.5"/>
                    </PathFigure.Segments>
                </PathFigure>
            </PathGeometry.Figures>
        </PathGeometry>
    </Path.Data>
</Path>
```

Two rules are not style preferences but toolchain requirements:

- the geometry **cannot** live in `App.xaml` and reach the `Path` through
  `Data="{StaticResource Icon…}"`: it compiles, then at runtime it throws
  `XamlParseException: Failed to assign to property
  'Windows.UI.Xaml.Shapes.Path.Data'.` — in WinRT a `Geometry` is not shareable
  through a `StaticResource`
  ([microsoft-ui-xaml#1909](https://github.com/microsoft/microsoft-ui-xaml/issues/1909),
  [#5780](https://github.com/microsoft/microsoft-ui-xaml/issues/5780));
- the geometry must be written in element form (`PathFigure` + `LineSegment` /
  `PolyLineSegment` / `ArcSegment`): on WP8.1 the `PathFigureCollection` converter
  does not accept the string, so `Figures="M…"` **does not compile**
  (`The TypeConverter for "PathFigureCollection" does not support
  converting from a string.`).

The guard checks both (plus "same icon, same geometry"):

```bash
node tools/check-icons.js            # rules + consistency + banned fonts
node tools/check-icons.js --preview  # + ASCII preview (needs ImageMagick)
```

The live tile has an asset of its own, `Assets/TileIcon.png` plus its 240% version
`TileIcon.scale-240.png` (480×480): a transparent PNG with **no padding**, written
by the same `make-brand-assets.js`. It is not a duplicate of `Logo.png` and the two
are not interchangeable: the manifest logos carry the padding the system expects,
and the iconic tile template wants the opposite.

`tools/check-tile.js` guards it, because the failure it catches is invisible. A tile
template does **not** take the icon from the manifest: it wants `<image src="..."/>`
in the payload, and with an empty `src` the tile renders without an icon and raises
no exception at all.

### Memory on a 512 MB device

A 512 MB phone gives the app a hard memory limit (roughly 185 MB; a 1 GB device
allows about twice that) and suspends or terminates it when the app keeps growing.
WP8.1 has **no manifest declaration for this** — `AppxManifestSchema2010_v2.xsd` and
its siblings have no memory element at all — so this is runtime work, in two halves:

- **decode at the size you draw**: `ImageHelper` takes the width the image is shown
  at and sets `DecodePixelWidth` before `SetSourceAsync`. A 640×640 profile picture
  drawn as a 52 px circle costs a few tens of KB instead of almost 2 MB, times one
  per conversation;
- **let go when asked**: `MemoryWatcher` listens to
  `MemoryManager.AppMemoryUsageIncreased` and, at `High`, drops the decoded avatars
  and clears the history of every chat that is not open (the open one is left alone,
  it is the one being read). While the pressure lasts nothing new is decoded. The  limit of the phone is written once, as `DIAG ok: memory budget N MB`;
- **and ask again from the phone**: the bytes of a picture are kept in
  `avatar-cache.json` (at most 40 conversations, about 1 MB), so a restart shows
  the faces before the adapter answers, and a row whose decoded bitmap was dropped
  is redrawn when the list comes back to the front. The pictures are the app's copy
  of what the adapter sent: the adapter keeps them for five minutes as well, so a
  chat list read twice does not go back to WhatsApp.

`tools/check-memory.js` fails the build if a call site forgets the decode width, if
one asks for more pixels than the screen can show, if `ImageHelper` sets
`DecodePixelWidth` after the decode, if the row cache starts carrying picture bytes,
if the avatar cache loses its caps or its serial queue, or if the avatar cache is not
read before the cached rows are applied.
`WhatsappBridge/test/config.test.js` holds the adapter side of the same budget (`CHATS_LIMIT` ≤ 30, `MESSAGES_LIMIT` ≤ 60).

### App language

The app follows the device language automatically through `.resw` resources:

| Language | File | Notes |
| --- | --- | --- |
| English | `WhatsappApp/Strings/en-US/Resources.resw` | `<DefaultLanguage>`: fallback for every other language |
| Italian | `WhatsappApp/Strings/it-IT/Resources.resw` | |

The app UI is the **only localized surface**: everything the scripts and the
server print (the launcher banner, its `--help`, the adapter log, the legacy
relay) is English only, so reading a log never needs a second language. Source
comments are English too, so Italian survives only in the test names and the
guard-script diagnostics, which nobody runs at run time.

- Texts declared in XAML use `x:Uid`, and the property must match the type of the
  element: `TextBlock` -> `.Text`, `Button` -> `.Content`, `TextBox` ->
  `.PlaceholderText`. A wrong pairing is a run-time error.
- Texts built in C# go through `Loc.Get("Key", "fallback")`
  (`WhatsappApp/Services/Loc.cs`), which never throws: if the resource is missing it
  uses the fallback. `Loc.Prewarm()` is called at startup on the UI thread because
  `ResourceLoader.GetForCurrentView()` cannot be created from a background thread
  (messages arrive from the socket on a background thread).
- Icon-only buttons do not use `x:Uid` (it would overwrite the `Path`): their label
  is a tooltip set with `Loc.Get` in the page constructor.
- Before every build, or after touching a string:

```bash
node tools/check-resw.js            # keys, x:Uid, Loc.Get, PRIResource, default language
node tools/check-resw.js --strict   # + fails on unused keys
```

The script fails if an `x:Uid` or a `Loc.Get` has no entry in **both** files, if the
two files do not have the same keys, if a `.resw` is not registered as `PRIResource`
in the `.csproj` (in that case it would never be included in the package) or if
`<DefaultLanguage>` is not one of the supported languages. Without this check an
error in the resources does **not** fail the build: the text simply stays the one
written in the markup.

To check the translations on the device it is enough to change the system language
(Settings > Time & language): Windows restarts the app and the strings change
accordingly. If the app stays in the previous language, close and reopen it.

### Documentation

The documents that explain the project exist in two languages, English and Italian,
and are kept in step:

| Document | English | Italian |
| --- | --- | --- |
| Project README | `README.md` | `README.it.md` |
| Adapter README | `WhatsappBridge/README.md` | `WhatsappBridge/README.it.md` |

Adding, moving or renaming a section means doing it in both files, in the same
commit, and the same goes for the `## Disclosure` section at the end of each README.
A new explanatory document is born as a pair. The guard refuses a pair whose
headings do not match, a document without the disclosure, and any emoji other than
the warning sign:

```bash
node tools/check-docs.js
```

## Shared server and the public endpoint

The adapter can host more than one account. Every user gets a GOWA device of
their own, created on the first handshake, and a token in `hello` decides which
one is theirs; with `AUTH_REQUIRED=on`, a message routed by its `device_id`
never reaches another user's socket. With the switch off nothing changes and the
instance stays private, with no token.

A phone that arrives without a token is given one on its first connection
(`AUTH_REGISTER=on`, the default): the adapter creates the device, answers with a
`registered` frame and the app keeps the token, so the shared service asks for
nothing but the switch. The token identifies the device, it is not a password to
be typed; `AUTH_REGISTER=off` goes back to handing the tokens out by hand.

The public service is not an address compiled into the app: `EndpointService`
reads `endpoint.json` from
[whatsappforwp-endpoint](https://github.com/vincenzosco/whatsappforwp-endpoint),
because the bore.pub tunnel that exposes the server gets a new port every time
it restarts. That file holds an address and nothing else. The switch on the
connection page chooses between the public service and a server of your own.

What remains true, and is worth saying plainly:

- The transport is encrypted by the app-level cipher (AES-256-CBC + HMAC-SHA256)
  with the passphrase compiled into the app. There is no TLS underneath: a WP8.1
  `StreamSocket` cannot pin a certificate, so a self-signed one is not an option.
- The operator of a shared server can technically reach the sessions on the
  machine. The token separates users from each other, not from the operator.
- Tokens are stored only as scrypt hashes, and the WhatsApp session is protected
  by encrypting the volume on the host.

## Limitations

- Status updates are not available: the GOWA server this app talks to has no endpoint for them, so the Status section is empty on purpose.
- Call records list incoming calls only, taken from the most recent chats the server scanned. See the Calls section below for the exact bound.
- Message deletions and edits made on the phone reach the app only while it is connected: they are not replayed after a restart. They are matched by WhatsApp's message id, so messages the app itself sent are not matched.
- Notifications are raised while the app is running: WP8.1 suspends it in the background, which closes the socket, and this project has no cloud service to push through. A message that arrives while the app is suspended is delivered on resume, when the app reconnects by itself - it is not announced at the moment it arrives.
- Pinning, muting and deleting live on this phone only, in `chat-preferences.json` in the app's folder. Nothing about them is sent to WhatsApp or to the adapter, so another device does not see them, and they are lost when the app's data is cleared.
- A chat's unread number is kept by the adapter, in memory, and is cleared when the conversation is opened in the app. Restarting the adapter starts the count again from zero, and messages that arrive while neither the app nor the adapter is running are not counted.
- A file shared from another app is read at the moment the share is handed over, because the app can be terminated while the picker or the sharing app is open. Files that are very large are still held in memory to be sent, so a full-length video may not fit on a phone with 512 MB.
- Typing indicators depend on the account being online, which is what the contacts also see: WhatsApp sends those events only to a client marked online, the adapter marks the account available while an app client is connected, and offline when the last one leaves. With the app closed no presence is sent or received, so nothing is shown in the meantime.
- Opening a chat shows the recent messages the server already has. Older ones are not requested from the phone. A photo or a video in that history shows a word (`[Image]`, `[Video]`) until it is tapped, when the adapter downloads it from the server and the app plays or draws it. A received video's bytes are written to the app's local folder for as long as the session lasts, and are not cleaned up when the app closes. The last 60 messages of a conversation are cached on the phone, so the first view of a chat is a photograph that the server replaces.
- The live tile says how many messages are waiting, who wrote last and their picture. The app writes it, so it needs the app to have run since the count changed: a message that arrives while the app is suspended is counted when it resumes. With the count at zero the tile goes back to the one in the manifest.
- Under memory pressure the app drops the decoded avatars; the bytes stay, and the pictures are redrawn when the chat list comes back. While the pressure lasts nothing new is decoded, so on a phone that stays under pressure the list shows initials for a while.

## Disclaimer

- This is an unofficial project not affiliated with WhatsApp or Meta.
- GOWA (and therefore this adapter) uses unofficial methods to connect to WhatsApp, which violates WhatsApp's Terms of Service.
- Using this bridge may result in a permanent ban of your phone number.
- Only use with test/secondary phone numbers.
- For production use, refer to the official WhatsApp Business API.

## License

MIT - Community maintained project. Use at your own risk.

## Disclosure

**This project is open source, and it needs maintainers.** Issues, translations,
reviews, documentation and pull requests are all welcome, and so is anyone who
wants to help it grow: more hands is the only thing that makes it move faster.

**The app was written 100% by an AI agent**, guided and reviewed by a human. Read
the code with the suspicion that deserves: run the guards in `tools/`, run the
adapter tests, and check anything that touches your own account before trusting it.

**The author does not accept responsibility for the WhatsApp account used to sign
in.** Linking this client means connecting an unofficial client to WhatsApp, which
is against WhatsApp's Terms of Service, and the account can be permanently banned.
Use a test or secondary number, and only if you accept that risk yourself.
