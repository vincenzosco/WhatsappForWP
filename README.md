# WhatsApp for Windows Phone 8.1

**English** | [Italiano](README.it.md)

A community-maintained WhatsApp client for Windows Phone 8.1 (Universal Windows Platform). The phone **is** the client: it speaks WhatsApp's own protocol itself - the Noise handshake, the binary node format, the Signal double ratchet, the media upload and download - so there is no server to run and nothing to configure.

## Projects

### WhatsappApp (Windows Phone 8.1 App)

The main client app with an authentic WhatsApp user interface.

**Features:**
- WhatsApp green theme (header #075E54, accent #25D366, chat bubbles)
- Chat list with avatars and unread badges
- Live tile with how many messages are waiting, the last sender's picture and their name
- Message bubbles with timestamps and sent/delivered/read status
- Text messaging with Enter-to-send
- Image attachment: pick photos from the gallery and send them to WhatsApp's own uploader
- Image preview in chat bubbles
- The first launch links the phone itself: the QR code is drawn by the app (its modules are encoded on the phone, no image comes from anywhere) and scanning it ties this device to the account
- Typing indicator: three animated dots in the conversation while the other person writes, and the same state is sent back while you write
- UI in the device language: English and Italian

**Architecture:**

The app opens WhatsApp's own WebSocket endpoint (`wss://web.whatsapp.com/ws/chat`)
over TLS and speaks the protocol on it. `WhatsappApp/Protocol/` is the wire: the
Noise handshake and its certificate chain check, the binary node reader and writer,
the Signal sessions (X3DH, the double ratchet, the sender key of a group), the media
keys and the protobuf messages. `WhatsappApp/Protocol/Client/` is the session: the
login, the node classifier, the history sync, the send path, the receipts, the call
log and the status feed. `WhatsappApp/Services/NativeConnection.cs` owns the socket,
the retry and the events the pages listen to.

#### Calls

The Calls tab lists the account's own call log, as WhatsApp's history sync carries
it (`HistorySync.callLogRecords`), from both directions: a call this phone made and
one it received are both there, and a call it never answered reads `missed`. It is
refreshed when the tab is opened.

#### Chats and new chats

The chat list is the account's real conversation list, as the history sync sends it
after a login: each row carries the conversation, its last message, its unread count
and its name. A group is named from WhatsApp's own metadata query, because the
synced row has no usable name for one. The profile picture of a chat is asked of
WhatsApp when its row is drawn (the query answers a URL and the URL answers the
JPEG), so a group has one like anybody else, and the bytes are kept on the phone so
a restart shows the faces before the connection is up.

A new chat can be started in three ways: typing a number with country code,
picking a contact with the system contact picker (the user's consent, so the app
never reads the address book by itself), or tapping a conversation the server
already knows.

While someone is writing, the conversation shows a bubble with three dots, one
rising after the other, and it goes away when they stop. It is the one thing this
app shows that is not a message, and it comes from WhatsApp (`chatstate` and the
`chat_presence` node), not from a guess. The other direction works the same way:
while you type, this phone sends the same node and the contact sees the same dots.
Both directions need the account to be present, so the client marks itself
`available` while the app is running and `unavailable` when the OS suspends it.

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
the same slot a picked photo uses: the preview bar shows it and Send uploads it
to WhatsApp's media host as a voice note.

The parts of this that need saying are in **Limitations**: WhatsApp's own voice
notes are Ogg/Opus and WP8.1 has neither an Opus encoder nor an Opus decoder, so a
note recorded here goes out as M4A in a voice-note bubble, and an Ogg/Opus note
that arrives cannot be played on this phone. The server that used to convert both
ways (with `ffmpeg`) is gone with the rest of it.

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
     && node tools/check-history-sync.js \
     && node tools/check-diagnostics.js \
     && node tools/check-handshake-answer.js \
     && node tools/check-xaml-names.js \
     && node tools/check-attachment-file.js \
     && node --test "tools/test/**/*.test.js"
   ```

4. Build the app on Windows
   (`msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86`)
   and say in the pull request that it reports `0 Error(s)`. There is no C# test
   host, so the build is the gate for the app.
5. Write the commit subject as `type: short imperative` in English, and say *why*
   the change is needed, not only what it does: the history is the changelog.
6. User-visible text: add the key to **both** `.resw` files, and update both
   `README.md` and `README.it.md` in the same commit.

A useful bug report has the `DIAG` lines from the Output window, which build you
ran, and what the phone did. The same lines are in the app's own `diag.log`, which
the Diagnostics page shows and which `ISETool.exe ts` copies off the phone.

## Protocol

The client speaks WhatsApp's own protocol, and none of it is invented here: every
step is pinned against the reference implementations (`@whiskeysockets/baileys` and
`whatsmeow`), and the tests that keep it honest live in `ProtocolTests/`.

- **Transport**: a WebSocket to `wss://web.whatsapp.com/ws/chat` over TLS, and
everything on it is a **binary node** - a tag, an attribute map, a list of children
or bytes (`Protocol/WABinary/`). `NodeReader` and `NodeWriter` are the two halves of
that format, and a node's length is checked against a ceiling before it is read.
- **Handshake**: the Noise protocol handshake with the server's own certificate
chain, checked on the phone (`Protocol/Noise/NoiseHandshake.cs`). The pairing that
follows is a QR whose modules this app encodes itself (`Protocol/Qr/QrCode.cs`).
- **Messages**: the Signal protocol - X3DH for a new session, the double ratchet for
every message after it, the sender key for a group (`Protocol/Signal/`). A message
that carries media names the keys and the address of the file, and the bytes are
uploaded to and downloaded from WhatsApp's own media host, encrypted and decrypted
on the phone (`Protocol/Media/`).
- **The session**: the node classifier, the history sync, the send path with its
receipts, the profile-picture query, the call log and the status feed
(`Protocol/Client/`). The account is the protocol's own credential store, in the
app's folder (`Protocol/Client/CredentialStore.cs`).

Everything the app prints at run time is English: its `DIAG` lines and the exception
messages that reach them. Source comments are English too: the code explains itself in
one language. The exceptions are the test names and the diagnostics of the guard
scripts under `tools/` - developer tooling that nobody runs - and the UI strings in
`Strings/it-IT`, which are translation rather than diagnostics.

A connection is owned by the attempt that opened it: only the newest attempt
publishes its socket and its read loop, so a socket that fails cannot close the one
that succeeded. The client reconnects by itself, with a bounded wait between
attempts, and a session that has been silent for a while is closed and reopened
rather than kept as a socket that looks alive and receives nothing.

## Building

### WP8 App

Verified toolchain: **Visual Studio 2013 (v12.0) + Windows Phone 8.1 SDK**. The
gate is

```bash
msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86
```

which must close with `0 Error(s)` and produce
`WhatsappApp\AppPackages\WhatsappApp_<version>_Debug_Test\WhatsappApp_<version>_x86_Debug.appxbundle`.

Every `DIAG` line the app writes is also pushed to the debugger, so **Visual
Studio 2013** with the solution open, the configuration set to **Debug**, the
platform to **ARM** and the phone as the target shows them in **Debug → Windows →
Output** when the app is started with **F5** (deploy and debug). For an app that is
already installed, use **Debug → Attach to Process** and pick it. Filter the window
on `DIAG`. Without a debugger attached nothing appears there; `diag.log` (export
it with `ISETool.exe ts`) and the Diagnostics page remain the channels on the
phone.

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

### Icons, tiles and splash screen

The WhatsApp logo (a green bubble with the handset cut out) comes from the raster
reference `tools/brand/logo-source.png` (the mark on a white background).
`tools/make-brand-assets.js` keys that white background out and rewrites the PNGs
in `WhatsappApp/Assets/` — already committed, so the script is only needed when
the artwork changes. It is plain Node (no ImageMagick): the PNG is decoded and
encoded with `zlib`.

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
`TileIcon.scale-240.png` (480×480): a transparent PNG written by the same
`make-brand-assets.js`, with the mark drawn inside the tile at the same inner
proportion as the manifest logos. It is not a duplicate of `Logo.png`: the iconic
tile template draws the image itself, so a mark that fills the whole asset looks
zoomed and cut on a 150 px tile.

The manifest sets `BackgroundColor="#FFFFFF"` together with
`ForegroundText="dark"`. The icons and the tile are white and the mark is green,
so the two can be told apart; `dark` is the other half of the pair, because
light text on a white tile is invisible.

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
  the faces before the connection is up, and a row whose decoded bitmap was dropped
  is redrawn when the list comes back to the front. The pictures are the app's own
  copy of what WhatsApp answered: a picture is asked for once, and the cache is what
  keeps a chat list read twice from asking again;
- **cap the conversation**: a chat kept in memory is bounded at 200 messages
  (`MaxMessagesPerChat` in `DataService`); the older ones fall out and come back
  from the server when the chat is reopened. This was the last allocation a busy
  conversation could grow without a limit, and it is what a 512 MB phone needs most;

`tools/check-memory.js` fails the build if a call site forgets the decode width, if
one asks for more pixels than the screen can show, if `ImageHelper` sets
`DecodePixelWidth` after the decode, if the row cache starts carrying picture bytes,
if the avatar cache loses its caps or its serial queue, if the avatar cache is not
read before the cached rows are applied, or if a chat's in-memory history has no
`MaxMessagesPerChat` ceiling or does not apply it with a `while` trim.

### App language

The app follows the device language automatically through `.resw` resources:

| Language | File | Notes |
| --- | --- | --- |
| English | `WhatsappApp/Strings/en-US/Resources.resw` | `<DefaultLanguage>`: fallback for every other language |
| Italian | `WhatsappApp/Strings/it-IT/Resources.resw` | |

The app UI is the **only localized surface**: everything the app and the scripts
print (the `DIAG` lines, the guards' diagnostics, the tooling under `tools/`) is
English only, so reading a log never needs a second language. Source comments are
English too, so Italian survives only in the test names and the guard-script
diagnostics, which nobody runs at run time.

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

Adding, moving or renaming a section means doing it in both files, in the same
commit, and the same goes for the `## Disclosure` section at the end of each README.
A new explanatory document is born as a pair. The guard refuses a pair whose
headings do not match, a document without the disclosure, and any emoji other than
the warning sign:

```bash
node tools/check-docs.js
```

## Limitations

- A voice note that arrives as Ogg/Opus cannot be played here: WhatsApp's own voice notes are Ogg/Opus and WP8.1 has no Opus decoder, and the server that used to convert them to MP3 (with `ffmpeg`) is gone. A note **sent** from this phone goes out as the M4A/AAC this device records, so a receiving client may draw it as an audio file rather than a voice note.
- Older messages are only the ones WhatsApp pushed after the login: this client does not ask for a conversation's older history on demand, so a chat shows what the last sync carried and what has arrived since.
- Message deletions and edits made on another device are not read yet: a message revoked elsewhere stays on screen until the conversation is fetched again on a fresh login.
- Notifications are raised while the app is running: WP8.1 suspends it in the background, which closes the socket, and this project has no cloud service to push through. A message that arrives while the app is suspended is delivered on resume, when the app reconnects by itself - it is not announced at the moment it arrives.
- Pinning, muting and deleting live on this phone only, in `chat-preferences.json` in the app's folder. Nothing about them is sent to WhatsApp, so another device does not see them, and they are lost when the app's data is cleared.
- A chat's unread number comes from WhatsApp with the history sync and is cleared when the conversation is opened in the app: the count is told to the server, and messages that arrive while the app is closed are counted when the next sync brings them.
- A file shared from another app is read at the moment the share is handed over, because the app can be terminated while the picker or the sharing app is open. Files that are very large are still held in memory to be sent, so a full-length video may not fit on a phone with 512 MB.
- Typing indicators depend on the account being present, which is what the contacts also see: WhatsApp sends those events only to a client marked online, the app marks the account available while it runs and unavailable when the OS suspends it. With the app closed no presence is sent or received, so nothing is shown in the meantime.
- A photo or a video in the history shows a word (`[Image]`, `[Video]`) until it is tapped, when the app downloads it from WhatsApp and plays or draws it. A received video's bytes are written to the app's local folder and are not cleaned up when the app closes. The last 60 messages of a conversation are cached on the phone, so the first view of a chat is a photograph the connection then replaces.
- The live tile says how many messages are waiting, who wrote last and their picture. The app writes it, so it needs the app to have run since the count changed: a message that arrives while the app is suspended is counted when it resumes. With the count at zero the tile goes back to the one in the manifest.
- Under memory pressure the app drops the decoded avatars; the bytes stay, and the pictures are redrawn when the chat list comes back. While the pressure lasts nothing new is decoded, so on a phone that stays under pressure the list shows initials for a while.

## Disclaimer

- This is an unofficial project not affiliated with WhatsApp or Meta.
- This client uses unofficial methods to connect to WhatsApp, which violates WhatsApp's Terms of Service.
- Using this bridge may result in a permanent ban of your phone number.
- Only use with test/secondary phone numbers.
- For production use, refer to the official WhatsApp Business API.

## License

MIT - Community maintained project. Use at your own risk.

## Disclosure

**This project is open source, and it needs maintainers.** Issues, translations,
reviews, documentation and pull requests are all welcome, and so is anyone who
wants to help it grow: more hands is the only thing that makes it move faster.


**The author does not accept responsibility for the WhatsApp account used to sign
in.** Linking this client means connecting an unofficial client to WhatsApp, which
is against WhatsApp's Terms of Service, and the account can be permanently banned.
Use a test or secondary number, and only if you accept that risk yourself.
