---
name: test-the-app
description: How to verify a change to the WhatsApp WP8.1 app and its GOWA adapter - the static guards, what each one catches, the adapter test suite, the Windows msbuild gate, and the on-device checklist. Use before declaring any change done, or when something fails on the device.
---

# Testing the app

## The fast gate (runs on any machine, seconds)

Current expected counts: 41 C# files, 145 keys in each `.resw`, 24 inline icon
Paths (14 distinct icons), 21 buttons, 1 button style, 173 adapter tests, 58 tests
in `tools/test`.

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js        # C# 5 syntax + WP8.1-missing WinRT APIs
node tools/check-icons.js          # icon rules + consistency + no icon font
node tools/check-resw.js --strict  # x:Uid/Loc.Get <-> both .resw, PRIResource, default language
node tools/check-docs.js          # the two languages of the docs are in step, no emoji
node tools/check-framing.js      # the frame byte order and the shared frame ceiling
node tools/check-tile.js        # the tile payload carries its icon, asset within the limits
node tools/check-memory.js      # no bitmap decoded bigger than it is drawn, and the picture caches stay bounded
node tools/check-actions.js     # a button named X is wired to X_Click and draws its icon
node tools/check-fire-and-forget.js  # a call fired without await is observed, or it loses its fault
node tools/check-project-files.js  # every .cs and .xaml is listed in the csproj, or MSBuild cannot see it
node tools/check-chat-list-source.js  # the conversation ListView has no ItemsSource in XAML, only ConversationView.Bind
node tools/check-diagnostics.js   # the run is on disk, and the history wait is not shorter than the read
node --test "tools/test/**/*.test.js"  # the tools' own tests (96)
node tools/qr-term.js --self-test  # terminal QR: module recovery and drawing
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
  still answered `E_NOTIMPL`. `Services/SelfCheck.cs` exists for exactly this, and
  its `DIAG` output is the evidence to ask for.
| `check-docs.js` | A README section added to one language and not the other (the heading counts stop matching), a missing link between the two versions, a `## Disclosure` section that is absent or no longer last, an emoji anywhere in the Markdown (the warning sign U+26A0 is the only exception). |
| `check-framing.js` | A socket `DataReader`/`DataWriter` created without `ByteOrder = ByteOrder.LittleEndian` (the WinRT default byte-swaps the frame length: `0x00000121` came back as `0x21010000`, 553713664, and a good frame was thrown away), an adapter that stopped using `writeUInt32LE`/`readUInt32LE`, a frame ceiling that differs between the app and the adapter. |
| `check-actions.js` | A button whose `Click` handler does not carry its `x:Name` (an icon that opens its neighbour's action), the wrong icon on a title-bar button, a button that declares a `Width` without `MinWidth="0" MinHeight="0"`, and a `Style` with `TargetType="Button"` that declares a `Width` or a `Height` without them - the WP8.1 theme minimums (109 x 57.5) would override the declared size, the `Auto` column would grow and its neighbour would be squeezed, which is exactly how the chat list title was clipped. A style with `BasedOn` is left alone: its base holds the setters. |
| `check-memory.js` | A decode call without its display width, a width wider than the screen, `DecodePixelWidth` set after `SetSourceAsync`, a whole picked or shared file read into a `byte[]`, the row cache carrying picture bytes, the avatar cache without its caps or without the serial queue, and the avatar cache read after the cached rows - a restart then shows initials until the adapter answers. |
| `check-fire-and-forget.js` | A call inside `#pragma warning disable 4014` whose fault nobody observes: it does not go through `Guarded.RunGuardedAsync`, and the method it names catches nothing, so an exception thrown at the call site lands in a Task nobody awaits. That is the voice note whose tap did nothing and left no `DIAG` line. It also refuses two statements in one region, a call the app does not define, and a dispatcher lambda that catches nothing. |
| `check-project-files.js` | A `.cs` or `.xaml` under `WhatsappApp` that `WhatsappApp.csproj` does not list. MSBuild never sees it, so the type it holds "does not exist" only on the VM build, and the guards all pass in the meantime - which is how `RecordingSession.cs` and `ConversationView.cs` were committed without their `Compile` entries. |
| `check-chat-list-source.js` | A second writer for `MessagesListView.ItemsSource`. The list is bound in code by `ConversationView.Bind`; an `ItemsSource="{Binding}"` left in XAML is resolved against `DataContext`, which on this page is a `Contact` and not a collection, so it fights the code-set source and the conversation opens empty with a run of first chance `SYSTEM.NI.DLL` exceptions and no `DIAG` line. |
| `check-diagnostics.js` | The diagnostics that never reach the disk: a log written without a `SerialQueue`, a file with no byte ceiling or no name, no marker file to tell a crash from a suspension, the unhandled-exception handler leaving the line in memory, and a history wait shorter than the adapter's cold read of the account (a 2 s wait against a measured 15 s binds the list before its burst and hands fifty inserts to a list already watching the collection). |

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

## The adapter suite

```bash
cd WhatsappBridge && npm test
```

Expected `pass 173`, `fail 0`. It covers the config and its `.env` loader, the GOWA
client (including the per-device `withDevice`/`createDevice`), the chat list and
the group pictures, the call scan, the `ffmpeg` transcode (with the executable
injected, so the suite needs no `ffmpeg`), the message format (including the
`\/Date(ms)\/` wire format the app requires), the TCP server including the
per-user session isolation, the webhook receiver, the user store and the token
gate, and the discovery beacon (a real UDP round trip). Add a test with every
adapter change.

The Docker repository has its own small suite for the tools it ships:

```bash
cd /tmp/docker-whatsappforwp && node --test "tools/*.test.js"   # 6 tests, the backup tool
```

## Cross-checking the app against the adapter

The two sides must agree on two things that no guard verifies:

1. **Key.** `WhatsappBridge/config.js` `BRIDGE_KEY` must equal the passphrase in
   `WhatsappApp/Services/CryptoHelper.cs` (`WhatsAppCommunityWP8-2026`). A
   mismatch shows up as "Errore decifratura messaggio" for every frame.
2. **Frame shape.** `[4-byte UInt32LE length][1-byte cipher tag (1 = GCM,
   2 = CBC+HMAC)][payload]`, JSON inside, control frames with `Type = 3` and
   `ChatId = "system"`. The app always writes tag 2 (AES-256-CBC +
   HMAC-SHA256), because WP8.1 answers AES-GCM with
   `NotImplementedException 0x80004001`; the adapter replies with the tag of
   the client's last inbound frame. If you change one side, change the other
   and the tests (`WhatsappBridge/test/crypto-helper.test.js` and the fixed
   vector in `SelfCheck.cs`).

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
- The VS2013 XAML designer needs a developer licence / sideload policy and is not
  needed: close the designer, open `.xaml` as XML, or build with `msbuild`.

## The login stack (real, and checkable here)

`node tools/start-login.js` starts GOWA and the adapter and draws the login QR in
the terminal; that much runs on this machine and is worth checking whenever the
adapter's login frames change:

```bash
node tools/start-login.js --no-bridge --once   # GOWA + one QR, then exits
```

What a good run prints: the banner with the LAN address and ports, then a `QR
aggiornato ... 65 moduli, ricostruzione 0.00%` caption. A reconstruction error
above 0% means the drawing is not trustworthy even if it looks right - see
`run-the-login-server`.

The drawing itself was verified independently: the rendered text, parsed back into
an image, decodes with macOS Vision to the identical payload as GOWA's PNG.

## What can and cannot be verified here

The static guards, the login stack and the Windows build are checkable. **Running
the app is not, unless the build machine can host the WP8.1 emulator or you have a
phone:** the XDE images are x86 and need Hyper-V, so on an ARM (Apple Silicon)
host running Windows 11 ARM64 there is no way to boot them. That is a hardware
limit, not a project one - on such a setup the build is the last gate that can be
run, and the checklist below waits for an x86/x64 Windows machine or a
USB-attached WP8.1 device.

One thing the build cannot tell you either is whether the phone implements the platform members the
app compiled against: `SymmetricAlgorithmNames.AesGcm`, `DisplayRequest` and `DatagramSocket` are all
in the WP8.1 reference metadata, and a member that is only declared answers `E_NOTIMPL` at run time.
`SelfCheck` (DEBUG builds, right after `Loc.Prewarm()`) prints one `DIAG ok:` line per capability, or
`DIAG <where>: <type> 0x<HRESULT> <message>` - that line is the fastest answer to "why is the screen
empty".

## On-device checklist

1. Deploy, then open **impostazioni** from the app bar and connect to the adapter.
2. Sign in with the QR code, then with the phone number (both paths).
3. Watch the Output window at start-up: three `DIAG ok:` lines (crypto, screen request, UDP beacon)
   and no `DIAG` line reporting a failure.
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
10. After a change to the socket layer, the debug log must show the three
    `DIAG ok:` start-up lines and **neither** `DIAG ConnectToServerAsync` **nor**
    `DIAG ListenForMessagesAsync`. A `DIAG ReadFrameAsync/length` line means the two
    sides disagree about the frame: read the length it prints before changing
    anything else.
11. On a fresh install the app connects on its own and the QR overlay opens without
    pressing anything. If the connection is already up when the settings page is
    opened, the page asks for the QR again by itself.
12. A frame that the phone cannot read shows up as `DIAG ChatMessage.FromJson: SerializationException`.
    After a change to `ChatMessage` or to `message-format.js`, that line must not appear: if it
    does, the message it named was dropped, and the field it names is the one to look at.
13. After linking, the chat list shows the conversations that exist in WhatsApp -
    not the empty address book - most recent first, with the last message as the
    preview and a profile picture where the person has one (rows without a picture
    keep their coloured initials).
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
    the settings page and without pressing anything. The Output window must show
    the watchdog line `DIAG ConnectionWatchdog/silent` and **not** a stuck
    `DIAG ConnectToServerAsync`.
19. Turn the phone's Wi-Fi off and back on while the app is open: within about a
    minute the app must recover by itself (same two log lines as above).
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
    GOWA has no picture for them. If every row shows initials, the avatar is being
    encoded from `/user/avatar`'s JSON instead of downloaded from the address it
    returns.
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
    note. With `ffmpeg` absent on the adapter host, the same note says it cannot be
    played instead of staying silent.
46. A document received from WhatsApp shows a document bar and the file name; tapping
    it opens the phone's viewer, or says there is no app for it.
47. Chat list: a group row shows the group's **picture**, not just its initials.
    If every group shows initials while people show pictures, the adapter is still
    skipping `@g.us` in `chats.js` or cutting the JID in `gowa-client.js.avatar`;
    on the host, `DEBUG` in `.tools/gowa/gowa.log` shows the `/user/avatar` call.
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
    before, not the initials, and they are there before the adapter answers (the
    `DIAG ok: connected` line arrives after them). Initials here mean the cache was
    not read before the rows, or was never written: see `AvatarCache`.
57. The cache file: no `DIAG failed` line for `AvatarCache`, and a second launch does
    not rewrite `avatar-cache.json` when nothing new arrived (its timestamp in the
    app folder stays the same). A file that grows at every launch is a cache with no
    cap, or one written for rows that already had the bytes.
58. Under pressure: on a 512 MB phone, open and close a few chats until
    `DIAG ok: memory under pressure: releasing decoded images` appears. Go back to
    the chat list: the pictures are drawn again (the bytes were kept, only the
    decoded bitmaps were dropped). Initials that stay mean `RestoreAvatars` was not
    called, or the row had no bytes to rebuild from.
59. A picture the adapter already sent: with the adapter running, switch between
    Chats and Calls a few times. The adapter log shows the avatar requests of the
    first read only - the second chat list is answered from `avatar-cache.js` for
    five minutes.
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
64. Open the info page with the adapter off: after a moment it says there is no
    information from the server, instead of staying in a loading state forever.
