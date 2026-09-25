---
name: test-the-app
description: How to verify a change to the WhatsApp WP8.1 app and its GOWA adapter - the static guards, what each one catches, the adapter test suite, the Windows msbuild gate, and the on-device checklist. Use before declaring any change done, or when something fails on the device.
---

# Testing the app

## The fast gate (runs on any machine, seconds)

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js        # C# 5 syntax + WP8.1-missing WinRT APIs
node tools/check-icons.js          # icon geometries defined <-> referenced
node tools/check-resw.js --strict  # x:Uid/Loc.Get <-> both .resw, PRIResource, default language
```

Exit code 0 and an `OK: ...` line each. What they catch that the build does not:

| Guard | Catches |
| --- | --- |
| `check-csharp5.js` | Syntax the WP8.1 compiler rejects (it never shows up here otherwise), and APIs that exist on Windows 10 but not on WP8.1. |
| `check-icons.js` | A blank icon button (`Segoe MDL2 Assets`), a `{StaticResource IconX}` that does not exist, a geometry nothing uses, and `Figures="M..."` - the string form of `PathGeometry.Figures`, which does not compile on WP8.1. |
| `check-resw.js` | A string that would silently stay in the markup language: missing/mistyped `x:Uid`, `x:Uid` on the wrong property, a `Loc.Get` key absent from a language, languages whose key sets differ, a `.resw` missing from the `csproj` (`PRIResource`), a wrong `<DefaultLanguage>`, a key/`.Property` collision, an unused key. |

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

Expected `pass 29`, `fail 0`. It covers the config, the GOWA client, the message
format (including the `\/Date(ms)\/` wire format the app requires), the TCP
server and the webhook receiver. Add a test with every adapter change.

## Cross-checking the app against the adapter

The two sides must agree on two things that no guard verifies:

1. **Key.** `WhatsappBridge/config.js` `BRIDGE_KEY` must equal the passphrase in
   `WhatsappApp/Services/CryptoHelper.cs` (`WhatsAppCommunityWP8-2026`). A
   mismatch shows up as "Errore decifratura messaggio" for every frame.
2. **Frame shape.** `[4-byte UInt32LE length][AES-256-GCM payload]`, JSON inside,
   control frames with `Type = 3` and `ChatId = "system"`. If you change one
   side, change the other and the tests.

## The real build gate (Windows machine)

```bash
msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86
```

Expected `0 Error(s)`. Notes worth remembering:

- If a Rebuild is followed by a build that fails early, the XAML pass
  (`MarkupCompilePass1`) may report
  `The name "...Converter" does not exist in the namespace ...`. Building a
  second time (without cleaning) resolves the reference against the freshly
  compiled assembly.
- The VS2013 XAML designer needs a developer licence / sideload policy and is not
  needed: close the designer, open `.xaml` as XML, or build with `msbuild`.

## On-device checklist

1. Deploy, then open **impostazioni** from the app bar and connect to the adapter.
2. Sign in with the QR code, then with the phone number (both paths).
3. Send a text message and an image (attach + caption); verify the outgoing bubble
   shows ✓/✓✓/✗ correctly.
4. Receive a message with the app open and with it closed: the unread badge must
   appear only in the second case.
5. Switch section with the bottom bar three times: the highlighted icon follows,
   lists keep their scroll position, and **Back** exits instead of walking back
   through the sections.
6. Change the device language (Settings > Time & language) and reopen: every
   visible string must switch between English and Italian.
7. Open the software keyboard on the chat page: the input row must stay above it.
8. Scroll a long conversation: no blank rows, no stutter (the item containers are
   deliberately styled to keep per-item layout cheap).
