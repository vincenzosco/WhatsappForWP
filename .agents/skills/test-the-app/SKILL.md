---
name: test-the-app
description: How to verify a change to the WhatsApp WP8.1 app and its GOWA adapter - the static guards, what each one catches, the adapter test suite, the Windows msbuild gate, and the on-device checklist. Use before declaring any change done, or when something fails on the device.
---

# Testing the app

## The fast gate (runs on any machine, seconds)

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js        # C# 5 syntax + WP8.1-missing WinRT APIs
node tools/check-icons.js          # icon rules + consistency + no icon font
node tools/check-resw.js --strict  # x:Uid/Loc.Get <-> both .resw, PRIResource, default language
node tools/check-docs.js          # the two languages of the docs are in step, no emoji
node tools/qr-term.js --self-test  # terminal QR: module recovery and drawing
```

Exit code 0 and an `OK: ...` line each. What they catch that the build does not:

| Guard | Catches |
| --- | --- |
| `check-csharp5.js` | Syntax the WP8.1 compiler rejects (it never shows up here otherwise), APIs that exist on Windows 10 but not on WP8.1, and a LINQ extension method (`.All(...)`, `.Where(...)`) in a file without `using System.Linq;` - a CS1061 that only msbuild reports. |
| `check-icons.js` | A blank icon button (`Segoe MDL2 Assets`); `Data="{StaticResource IconX}"`, which compiles but throws at runtime; a `PathGeometry` that is not inlined in a `<Path.Data>`; `Figures="M..."`, the string form of `PathGeometry.Figures` that does not compile on WP8.1; a `Path` with no inline geometry or no `<!-- IconX -->` comment; two copies of the same icon name with different geometry. |
| `check-resw.js` | A string that would silently stay in the markup language: missing/mistyped `x:Uid`, `x:Uid` on the wrong property, a `Loc.Get` key absent from a language, languages whose key sets differ, a `.resw` missing from the `csproj` (`PRIResource`), a wrong `<DefaultLanguage>`, a key/`.Property` collision, an unused key. |
| `check-docs.js` | A README section added to one language and not the other (the heading counts stop matching), a missing link between the two versions, a `## Disclosure` section that is absent or no longer last, an emoji anywhere in the Markdown (the warning sign U+26A0 is the only exception). |

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

Expected `pass 36`, `fail 0`. It covers the config and its `.env` loader, the GOWA
client, the message format (including the `\/Date(ms)\/` wire format the app
requires), the TCP server, the webhook receiver and the discovery beacon (a real
UDP round trip). Add a test with every adapter change.

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
