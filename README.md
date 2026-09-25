# WhatsApp for Windows Phone 8.1

**English** | [Italiano](README.it.md)

A community-maintained WhatsApp client for Windows Phone 8.1 (Universal Windows Platform). This project includes a full WhatsApp-like UI and a bridge server to connect to real WhatsApp servers.

## Projects

### WhatsappApp (Windows Phone 8.1 App)

The main client app with an authentic WhatsApp user interface.

**Features:**
- WhatsApp green theme (header #075E54, accent #25D366, chat bubbles)
- Chat list with avatars, unread badges, online indicators
- Message bubbles with timestamps and sent/delivered/read status
- Text messaging with Enter-to-send
- Image attachment: pick photos from the gallery and send them through the bridge
- Image preview in chat bubbles (base64 over TCP)
- The app finds the adapter on the local network by itself, so there is no address to type
- Login from the phone: the QR code or the phone pairing code is shown full screen in the app
- UI in the device language: English and Italian

**Architecture:**

The app uses a TCP connection with length-prefixed JSON messages:

```
[4 bytes: UInt32 LE message length] [N bytes: UTF-8 JSON ChatMessage]
```

The `ChatMessage` model uses `DataContractJsonSerializer` for serialization (DateTime in `\/Date()\/` format).

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
- Sends text and images through `POST /send/message` and `POST /send/image`
- Receives incoming messages through a GOWA webhook (HMAC-verified)
- Syncs contacts from `GET /user/my/contacts`

**Setup (one command)**

```bash
node tools/start-login.js --download   # --download only the first time
```

It downloads the official GOWA binary for this platform into `.tools/gowa`
(SHA-256 verified), starts `whatsapp rest`, starts `WhatsappBridge/server.js`,
announces the adapter on the LAN over UDP (so the app finds it **by itself**) and
prints the addresses and ports. The login is normally done **on the phone**: the
app shows the QR full screen, so nothing has to be scanned off the computer. A
terminal QR is still available and is drawn as a real scannable code, renewed as
long as it takes; when it does not fit the window the script says so and writes
the PNG to `.tools/gowa/login-qr.png` rather than drawing something truncated.
The adapter registers its own webhook on GOWA.

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
(`magick`). Adapter environment variables are documented in
`WhatsappBridge/.env.example` (the file is read at startup; variables already
exported win over it).

**Doing it by hand**, if you prefer to run the pieces yourself: start any GOWA
that speaks the v9 REST API (`whatsapp rest --port=3000 --host=127.0.0.1`), then
`cd WhatsappBridge && cp .env.example .env && npm start`.

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

## Protocol

The TCP protocol uses length-prefixed JSON messages, compatible with Windows `DataWriter`/`DataReader`:

- 4 bytes: message length (UInt32, Little Endian)
- N bytes: encrypted payload

The payload is encrypted with **AES-256-GCM** using a pre-shared key (SHA-256 of a passphrase):
12-byte random IV, ciphertext, 16-byte auth tag. The app and the server must use the
same passphrase (`BRIDGE_KEY` env var on the server, constant in `CryptoHelper.cs` in the app).

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

### App language

The app follows the device language automatically through `.resw` resources:

| Language | File | Notes |
| --- | --- | --- |
| English | `WhatsappApp/Strings/en-US/Resources.resw` | `<DefaultLanguage>`: fallback for every other language |
| Italian | `WhatsappApp/Strings/it-IT/Resources.resw` | |

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
