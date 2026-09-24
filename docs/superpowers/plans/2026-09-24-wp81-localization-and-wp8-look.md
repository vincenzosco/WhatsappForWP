# WP8.1 Device-Language Localization + WhatsApp-WP8 Look — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every user-visible string in the app come from a resource file chosen automatically from the device language (English `en-US` + Italian `it-IT`, English as the neutral fallback), and reshape the shell into the hub layout of the released WhatsApp for Windows Phone 8.

**Architecture:** WP8.1 resolves `x:Uid` at run time and `ResourceLoader.GetString` at run time — neither is checked by the compiler, and a missing entry silently falls back to the markup literal instead of failing the build. Since this machine has no WP8.1 compiler, a **static guard (`tools/check-resw.js`) is the verification gate** for all localization work: it cross-checks every `x:Uid`/`Loc.Get` against both `.resw` files, the `csproj` resource registration, and the default language. UI chrome is rebuilt on the WP8 `Pivot` hub (title bar + `chat`/`stato`/`chiamate` sections + bottom app bar), keeping the already-verified vector `Path` icons.

**Tech Stack:** C# 5 (the VS2013/WP8.1 toolchain compiler), WinRT XAML for Windows Phone 8.1, `.resw` resources + `ResourceLoader` (MRT/ResourceLoader), Node.js guards under `tools/`.

## Global Constraints

- **C# 5 only.** No `$"..."`, no `?.`, no expression-bodied members (`=>` bodies/`get =>`), no auto-property initializers, no `out <type> var`, no `is <Type> name`, no `nameof`, no discard `_ =`. Enforced by `node tools/check-csharp5.js`.
- **No icon font.** WP8.1 has no `Segoe MDL2 Assets`; every icon is a `PathGeometry` in `App.xaml` referenced as `Data="{StaticResource IconX}"`. Enforced by `node tools/check-icons.js`.
- **Default language is `en-US`** and is the neutral fallback: any device language other than Italian shows English.
- **Supported languages: `en-US` and `it-IT` only.** Resource files live at `WhatsappApp/Strings/<lang>/Resources.resw`.
- **`x:Uid` property must match the element type:** `TextBlock` → `.Text`, `Button` → `.Content`, `TextBox` → `.PlaceholderText`. A mismatch is a run-time error.
- **Never put `x:Uid` on an icon-only `Button`** — it would overwrite the `Path` content. Localize those with `ToolTipService.SetToolTip` from code-behind.
- **Never build a localized string outside a UI-thread dispatch.** `ResourceLoader.GetForCurrentView()` throws when created on a background thread.
- **Adapter must not regress:** `cd WhatsappBridge && npm test` stays at 29/29.
- **The authoritative build gate is the user's machine:** `msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86` (expect `0 Error(s)`).
- Every task ends with a commit; the final task pushes to `origin/master`.

---

## File Structure

| File | Responsibility |
| --- | --- |
| `tools/check-resw.js` *(new)* | Guard: resource completeness, `x:Uid` correctness, `csproj` wiring. |
| `WhatsappApp/Services/Loc.cs` *(new)* | Only access point to localized strings from C#, with a safe fallback. |
| `WhatsappApp/Strings/en-US/Resources.resw` *(new)* | Every English string (also the neutral fallback). |
| `WhatsappApp/Strings/it-IT/Resources.resw` *(new)* | Every Italian string. |
| `WhatsappApp/App.xaml(.cs)` | Icon geometries; creates the resource loader once on the UI thread. |
| `WhatsappApp/MainPage.xaml(.cs)` | Hub (`Pivot`): title bar, three sections, bottom app bar. |
| `WhatsappApp/Pages/ChatPage.xaml(.cs)` | Conversation: header without dead call buttons, localized input area. |
| `WhatsappApp/Pages/ConnectionPage.xaml(.cs)` | Server + WhatsApp login, fully localized, no string sniffing. |
| `WhatsappApp/Services/CommunicationService.cs` | Localized status/errors + a real `ConnectionEstablished` event. |
| `WhatsappApp/Services/DataService.cs` | Localized group names, `ActiveChatId` (no unread badge on the open chat). |
| `WhatsappApp/Models/ChatMessage.cs` | Localized "Yesterday"/media labels. |
| `WhatsappApp/Converters/Converters.cs` | Remove the dead `OnlineStatusConverter` (hardcoded Italian). |
| `WhatsappApp/WhatsappApp.csproj` | `PRIResource` + `Compile` entries, `DefaultLanguage` = `en-US`. |

**Deliberately out of scope (YAGNI):** localizing `Package.appxmanifest` (the only translatable value is the description; every other value is the brand name, and a wrong `ms-resource:` reference breaks `makepri`), and in-app search (the released app had a search button, but a non-functional button was one of the reported defects — no dead chrome goes in).

---

### Task 1: Localization guard

**Files:**
- Create: `tools/check-resw.js`

**Interfaces:**
- Consumes: nothing.
- Produces: `node tools/check-resw.js` → exit 0 when the resource files are complete and every `x:Uid`/`Loc.Get("Key", ...)` resolves; exit 1 otherwise. `--strict` additionally fails on unused keys. Consumed by Tasks 2-10.

- [ ] **Step 1: Write the guard**

Create `tools/check-resw.js`:

```js
#!/usr/bin/env node
/**
 * tools/check-resw.js
 *
 * Guard for the app localization (WhatsappApp/Strings/<lang>/Resources.resw).
 *
 * WP8.1 resolves x:Uid and ResourceLoader.GetString at run time: a missing or
 * mistyped entry does NOT fail the build, it silently leaves the literal from
 * the markup in place. A .resw that is not registered in the csproj is never
 * compiled into the package at all. None of that is caught by the compiler, so
 * this guard is the only thing that catches it.
 *
 * Rules:
 *   A. every literal localizable attribute in XAML is a binding, a known
 *      non-translatable value, or backed by x:Uid="X" plus an entry
 *      "X.<Attr>" in BOTH resource files;
 *   B. en-US and it-IT declare exactly the same keys, and no key collides with
 *      a property identifier of the same name;
 *   C. every Loc.Get("Key", ...) in C# exists in BOTH resource files;
 *   D. (--strict) no key is left unused;
 *   E. both .resw files are registered as PRIResource in the csproj and
 *      <DefaultLanguage> is one of the supported languages.
 *
 * Usage:
 *   node tools/check-resw.js
 *   node tools/check-resw.js --strict
 */
'use strict';

const { spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');
const STRINGS = path.join(APP, 'Strings');
const CSPROJ = path.join(APP, 'WhatsappApp.csproj');
const LANGS = ['en-US', 'it-IT'];
const DEFAULTS = [LANGS[0], LANGS[1]];
const STRICT = process.argv.includes('--strict');

// Properties that carry user-visible text.
const LOCALIZABLE = ['Text', 'Content', 'PlaceholderText', 'Header'];
// Literals that never need a translation (brand names).
const ALLOWED_LITERALS = new Set(['WhatsApp']);
// Values made only of symbols/digits/punctuation need no translation.
const SYMBOL_ONLY = /^[^\p{L}]*$/u;

function walk(dir, out, keep) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'obj' || entry.name === 'bin') continue;
      walk(path.join(dir, entry.name), out, keep);
    } else if (keep(entry.name)) {
      out.push(path.join(dir, entry.name));
    }
  }
  return out;
}

function readResw(file) {
  const xml = fs.readFileSync(file, 'utf8');
  const keys = new Set();
  for (const m of xml.matchAll(/<data\s+name="([^"]+)"/g)) keys.add(m[1]);
  return { xml, keys };
}

const problems = [];
const warnings = [];
const used = new Set();

// --------------------------------------------------------------------------
// E. csproj wiring
// --------------------------------------------------------------------------
const csproj = fs.readFileSync(CSPROJ, 'utf8');
for (const lang of LANGS) {
  const needle = '<PRIResource Include="Strings\\' + lang + '\\Resources.resw"';
  if (!csproj.includes(needle)) {
    problems.push('WhatsappApp/WhatsappApp.csproj: missing ' + needle.replace(' Include=', ' l\'Include ').replace(/"$/, '"/>'));
  }
}
const defaultLang = /<DefaultLanguage>([^<]+)<\/DefaultLanguage>/.exec(csproj);
if (!defaultLang) {
  problems.push('WhatsappApp/WhatsappApp.csproj: no <DefaultLanguage>');
} else if (DEFAULTS.indexOf(defaultLang[1]) === -1) {
  problems.push('WhatsappApp/WhatsappApp.csproj: <DefaultLanguage>' + defaultLang[1] +
    '</DefaultLanguage> is not one of ' + DEFAULTS.join(', '));
}

// --------------------------------------------------------------------------
// B. both languages, same keys
// --------------------------------------------------------------------------
const resw = {};
for (const lang of LANGS) {
  const file = path.join(STRINGS, lang, 'Resources.resw');
  if (!fs.existsSync(file)) {
    problems.push('missing ' + path.relative(ROOT, file));
    resw[lang] = { xml: '', keys: new Set() };
    continue;
  }
  const hasXmllint = spawnSync('xmllint', ['--version'], { stdio: 'ignore' }).status === 0;
  if (hasXmllint) {
    const ok = spawnSync('xmllint', ['--noout', file], { stdio: 'ignore' }).status === 0;
    if (!ok) problems.push(path.relative(ROOT, file) + ': not well-formed XML');
  }
  resw[lang] = readResw(file);
}

for (const key of resw[LANGS[0]].keys) {
  if (!resw[LANGS[1]].keys.has(key)) {
    problems.push('Strings/en-US/Resources.resw: "' + key + '" has no it-IT translation');
  }
}
for (const key of resw[LANGS[1]].keys) {
  if (!resw[LANGS[0]].keys.has(key)) {
    problems.push('Strings/it-IT/Resources.resw: "' + key + '" has no en-US entry');
  }
}
for (const key of resw[LANGS[0]].keys) {
  const dot = key.indexOf('.');
  if (dot === -1) continue;
  const base = key.slice(0, dot);
  if (resw[LANGS[0]].keys.has(base)) {
    problems.push('Strings/en-US/Resources.resw: "' + base + '" and "' + key +
      '" cannot coexist (duplicate resource identifier)');
  }
}

function exists(key) {
  return resw[LANGS[0]].keys.has(key) && resw[LANGS[1]].keys.has(key);
}

// --------------------------------------------------------------------------
// A. x:Uid coverage of literal attributes
// --------------------------------------------------------------------------
const TAG = /<[A-Za-z_][^<>]*>/g;
for (const file of walk(APP, [], (n) => n.endsWith('.xaml'))) {
  const rel = path.relative(ROOT, file);
  const xaml = fs.readFileSync(file, 'utf8').replace(/<!--[\s\S]*?-->/g, '');
  for (const tag of xaml.match(TAG) || []) {
    const tagName = (/^<([A-Za-z_][\w.:]*)/.exec(tag) || [, '?'])[1];
    const uidMatch = /\sx:Uid="([^"]*)"/.exec(tag);
    const uid = uidMatch ? uidMatch[1] : null;
    for (const prop of LOCALIZABLE) {
      const m = new RegExp('\\s' + prop + '="([^"]*)"').exec(tag);
      if (!m) continue;
      const value = m[1];
      if (value === '' || value.charAt(0) === '{') continue;
      if (ALLOWED_LITERALS.has(value) || SYMBOL_ONLY.test(value)) continue;
      if (uid && exists(uid + '.' + prop)) {
        used.add(uid + '.' + prop);
        continue;
      }
      problems.push(rel + ': <' + tagName + '> ' + prop + '="' + value + '" -> ' +
        (uid ? 'no entry "' + uid + '.' + prop + '" in both .resw files' : 'missing x:Uid'));
    }
  }
}

// --------------------------------------------------------------------------
// C. Loc.Get keys used from code
// --------------------------------------------------------------------------
for (const file of walk(APP, [], (n) => n.endsWith('.cs'))) {
  const rel = path.relative(ROOT, file);
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
  lines.forEach((line, i) => {
    for (const m of line.matchAll(/\bLoc\.Get\(\s*"([^"]+)"/g)) {
      const key = m[1];
      used.add(key);
      if (!exists(key)) {
        problems.push(rel + ':' + (i + 1) + ': Loc.Get("' + key +
          '") has no entry in both .resw files');
      }
    }
  });
}

// --------------------------------------------------------------------------
// D. unused keys
// --------------------------------------------------------------------------
for (const key of resw[LANGS[0]].keys) {
  if (!used.has(key)) warnings.push('Strings/en-US/Resources.resw: "' + key + '" is never used');
}

const report = problems.concat(STRICT ? warnings : []);
if (report.length) {
  console.log(report.join('\n'));
  if (!STRICT && warnings.length) {
    console.log('\n(' + warnings.length + ' unused key(s) — use --strict to fail on them)');
  }
  console.log('\n' + report.length + ' problem(s).');
  process.exit(1);
}
console.log('OK: ' + resw[LANGS[0]].keys.size + ' key(s) in en-US and it-IT, ' +
  'every x:Uid and Loc.Get lookup resolved.');
if (warnings.length) console.log('(' + warnings.length + ' unused key(s); --strict would fail.)');
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd /Users/vincenzo/Documents/WhatsappForWP && node tools/check-resw.js`
Expected: FAIL, exit 1. The report lists, for every hardcoded string in the app, lines of the form
`WhatsappApp/MainPage.xaml: <TextBlock> Text="CHAT" -> missing x:Uid` (about 45 lines), plus
`WhatsappApp/WhatsappApp.csproj: missing ...PRIResource...` and `WhatsappApp/WhatsappApp.csproj: <DefaultLanguage>it-IT</DefaultLanguage> is not one of en-US, it-IT`.

- [ ] **Step 3: Commit**

```bash
git add tools/check-resw.js
git commit -m "test: guard the .resw resources and every x:Uid lookup"
```

---

### Task 2: Localization infrastructure

**Files:**
- Create: `WhatsappApp/Services/Loc.cs`
- Create: `WhatsappApp/Strings/en-US/Resources.resw`
- Create: `WhatsappApp/Strings/it-IT/Resources.resw`
- Modify: `WhatsappApp/WhatsappApp.csproj`
- Modify: `WhatsappApp/App.xaml.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `WhatsappApp.Services.Loc` with `static string Loc.Get(string key, string fallback)` and `static void Loc.Prewarm()`. Key naming: `x:Uid` entries are `Identifier.Property` (`TextBlock` → `.Text`, `Button` → `.Content`, `TextBox` → `.PlaceholderText`); keys read from code are plain identifiers. Every key consumed by later tasks is listed in Step 1.

- [ ] **Step 1: Create the English resource file**

Create `WhatsappApp/Strings/en-US/Resources.resw`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <data name="MainPage_TabChats.Text" xml:space="preserve">
    <value>chats</value>
  </data>
  <data name="MainPage_TabStatus.Text" xml:space="preserve">
    <value>status</value>
  </data>
  <data name="MainPage_TabCalls.Text" xml:space="preserve">
    <value>calls</value>
  </data>
  <data name="MainPage_EmptyTitle.Text" xml:space="preserve">
    <value>No chats yet</value>
  </data>
  <data name="MainPage_EmptyHint.Text" xml:space="preserve">
    <value>Tap new chat to start</value>
  </data>
  <data name="MainPage_StatusEmptyTitle.Text" xml:space="preserve">
    <value>No status updates</value>
  </data>
  <data name="MainPage_StatusEmptyHint.Text" xml:space="preserve">
    <value>Status updates are not supported in this version.</value>
  </data>
  <data name="MainPage_CallsEmptyTitle.Text" xml:space="preserve">
    <value>No calls</value>
  </data>
  <data name="MainPage_CallsEmptyHint.Text" xml:space="preserve">
    <value>Calls are not supported in this version.</value>
  </data>
  <data name="MainPage_NewChatTooltip" xml:space="preserve">
    <value>New chat</value>
  </data>
  <data name="MainPage_SettingsTooltip" xml:space="preserve">
    <value>Settings</value>
  </data>
  <data name="MainPage_NewChatTitle" xml:space="preserve">
    <value>New chat</value>
  </data>
  <data name="MainPage_NewChatPrompt" xml:space="preserve">
    <value>Phone number with country code (e.g. 393401234567)</value>
  </data>
  <data name="MainPage_NewChatOpen" xml:space="preserve">
    <value>Open</value>
  </data>
  <data name="MainPage_NewChatCancel" xml:space="preserve">
    <value>Cancel</value>
  </data>
  <data name="MainPage_NewChatInvalid" xml:space="preserve">
    <value>Enter a valid number with country code (e.g. 393401234567).</value>
  </data>
  <data name="ChatPage_Input.PlaceholderText" xml:space="preserve">
    <value>Type a message</value>
  </data>
  <data name="ChatPage_ImageSelected.Text" xml:space="preserve">
    <value>Image selected</value>
  </data>
  <data name="ChatPage_Online" xml:space="preserve">
    <value>online</value>
  </data>
  <data name="ChatPage_LastSeenToday" xml:space="preserve">
    <value>last seen today at {0}</value>
  </data>
  <data name="ChatPage_Me" xml:space="preserve">
    <value>Me</value>
  </data>
  <data name="ChatPage_ImageError" xml:space="preserve">
    <value>Could not open the image: {0}</value>
  </data>
  <data name="ChatPage_BackTooltip" xml:space="preserve">
    <value>Back</value>
  </data>
  <data name="ChatPage_AttachTooltip" xml:space="preserve">
    <value>Attach an image</value>
  </data>
  <data name="ChatPage_SendTooltip" xml:space="preserve">
    <value>Send</value>
  </data>
  <data name="ChatPage_ClearImageTooltip" xml:space="preserve">
    <value>Remove the image</value>
  </data>
  <data name="ChatMessage_Photo" xml:space="preserve">
    <value>Photo</value>
  </data>
  <data name="ChatMessage_Audio" xml:space="preserve">
    <value>Audio</value>
  </data>
  <data name="ChatMessage_File" xml:space="preserve">
    <value>File</value>
  </data>
  <data name="ChatMessage_Yesterday" xml:space="preserve">
    <value>Yesterday</value>
  </data>
  <data name="ConnectionPage_ServerLabel.Text" xml:space="preserve">
    <value>WhatsApp server (adapter)</value>
  </data>
  <data name="ConnectionPage_ServerPlaceholder.PlaceholderText" xml:space="preserve">
    <value>e.g. 192.168.1.100</value>
  </data>
  <data name="ConnectionPage_PortLabel.Text" xml:space="preserve">
    <value>TCP port</value>
  </data>
  <data name="ConnectionPage_NameLabel.Text" xml:space="preserve">
    <value>Your name</value>
  </data>
  <data name="ConnectionPage_Connect.Content" xml:space="preserve">
    <value>Connect to the server</value>
  </data>
  <data name="ConnectionPage_Disconnect.Content" xml:space="preserve">
    <value>Disconnect</value>
  </data>
  <data name="ConnectionPage_WhatsAppTitle.Text" xml:space="preserve">
    <value>WhatsApp connection</value>
  </data>
  <data name="ConnectionPage_QrLogin.Content" xml:space="preserve">
    <value>Sign in with QR code</value>
  </data>
  <data name="ConnectionPage_OrPhone.Text" xml:space="preserve">
    <value>or sign in with your phone number</value>
  </data>
  <data name="ConnectionPage_PhonePlaceholder.PlaceholderText" xml:space="preserve">
    <value>Number with country code (e.g. 393401234567)</value>
  </data>
  <data name="ConnectionPage_GetCode.Content" xml:space="preserve">
    <value>Get the code</value>
  </data>
  <data name="ConnectionPage_Continue.Content" xml:space="preserve">
    <value>Continue</value>
  </data>
  <data name="ConnectionPage_HelpTitle.Text" xml:space="preserve">
    <value>How to sign in</value>
  </data>
  <data name="ConnectionPage_HelpQr.Text" xml:space="preserve">
    <value>QR code: open WhatsApp on your phone, open Linked devices and tap Link a device, then scan the code.</value>
  </data>
  <data name="ConnectionPage_HelpPhone.Text" xml:space="preserve">
    <value>Phone number: enter the international number, tap Get the code, then type it in WhatsApp.</value>
  </data>
  <data name="ConnectionPage_Version.Text" xml:space="preserve">
    <value>WhatsApp Community Edition v2.0 (GOWA)</value>
  </data>
  <data name="ConnectionPage_FirstRunTitle" xml:space="preserve">
    <value>First-time setup</value>
  </data>
  <data name="ConnectionPage_SettingsTitle" xml:space="preserve">
    <value>Server settings</value>
  </data>
  <data name="ConnectionPage_Connecting" xml:space="preserve">
    <value>Connecting to {0}:{1}...</value>
  </data>
  <data name="ConnectionPage_Connected" xml:space="preserve">
    <value>Connected!</value>
  </data>
  <data name="ConnectionPage_ConnectFailed" xml:space="preserve">
    <value>Connection failed</value>
  </data>
  <data name="ConnectionPage_WhatsAppConnected" xml:space="preserve">
    <value>WhatsApp connected!</value>
  </data>
  <data name="ConnectionPage_ConnectedAs" xml:space="preserve">
    <value>Connected as {0}</value>
  </data>
  <data name="ConnectionPage_Waiting" xml:space="preserve">
    <value>Waiting for pairing: follow the instructions below.</value>
  </data>
  <data name="ConnectionPage_WhatsAppDisconnected" xml:space="preserve">
    <value>Not connected to WhatsApp. Sign in with the QR code or your phone number.</value>
  </data>
  <data name="ConnectionPage_InvalidPhone" xml:space="preserve">
    <value>Enter a valid number with country code (e.g. 393401234567).</value>
  </data>
  <data name="ConnectionPage_RequestingQr" xml:space="preserve">
    <value>Requesting the QR code...</value>
  </data>
  <data name="ConnectionPage_RequestingCode" xml:space="preserve">
    <value>Requesting the code...</value>
  </data>
  <data name="ConnectionPage_QrUnavailable" xml:space="preserve">
    <value>QR code not available.</value>
  </data>
  <data name="ConnectionPage_QrHint" xml:space="preserve">
    <value>Open WhatsApp, open Linked devices and tap Link a device, then scan the code.</value>
  </data>
  <data name="ConnectionPage_QrHintDuration" xml:space="preserve">
    <value>Open WhatsApp, open Linked devices and tap Link a device, then scan the code (valid for about {0} seconds).</value>
  </data>
  <data name="ConnectionPage_QrError" xml:space="preserve">
    <value>Could not show the QR code: {0}</value>
  </data>
  <data name="ConnectionPage_PairCode" xml:space="preserve">
    <value>Code: {0}</value>
  </data>
  <data name="ConnectionPage_PairCodeHint" xml:space="preserve">
    <value>Enter this code in WhatsApp: Linked devices, Link a device, Link with phone number instead.</value>
  </data>
  <data name="CommService_Connecting" xml:space="preserve">
    <value>Connecting...</value>
  </data>
  <data name="CommService_Connected" xml:space="preserve">
    <value>Connected to the server</value>
  </data>
  <data name="CommService_ConnectError" xml:space="preserve">
    <value>Connection error: {0}</value>
  </data>
  <data name="CommService_ConnectionLost" xml:space="preserve">
    <value>Connection lost: {0}</value>
  </data>
  <data name="CommService_Disconnected" xml:space="preserve">
    <value>Disconnected</value>
  </data>
  <data name="CommService_NotConnected" xml:space="preserve">
    <value>Not connected</value>
  </data>
  <data name="CommService_SendError" xml:space="preserve">
    <value>Send error: {0}</value>
  </data>
  <data name="CommService_DecryptError" xml:space="preserve">
    <value>Message decryption error: {0}</value>
  </data>
  <data name="CommService_Me" xml:space="preserve">
    <value>Me</value>
  </data>
  <data name="CommService_ServerStarted" xml:space="preserve">
    <value>Server started on port {0}</value>
  </data>
  <data name="CommService_ServerStartError" xml:space="preserve">
    <value>Server start error: {0}</value>
  </data>
  <data name="CommService_ClientConnected" xml:space="preserve">
    <value>New client connected ({0} online)</value>
  </data>
  <data name="CommService_ClientDisconnected" xml:space="preserve">
    <value>Client disconnected: {0}</value>
  </data>
  <data name="CommService_ClientRemoved" xml:space="preserve">
    <value>Client removed ({0} online)</value>
  </data>
  <data name="DataService_Group" xml:space="preserve">
    <value>Group {0}</value>
  </data>
</root>
```

- [ ] **Step 2: Create the Italian resource file**

Create `WhatsappApp/Strings/it-IT/Resources.resw` — same `resheader` block and the same 79 `<data>` names, with these values:

| Key | Value |
| --- | --- |
| `MainPage_TabChats.Text` | `chat` |
| `MainPage_TabStatus.Text` | `stato` |
| `MainPage_TabCalls.Text` | `chiamate` |
| `MainPage_EmptyTitle.Text` | `Nessuna chat` |
| `MainPage_EmptyHint.Text` | `Tocca nuova chat per iniziare` |
| `MainPage_StatusEmptyTitle.Text` | `Nessuno stato` |
| `MainPage_StatusEmptyHint.Text` | `Gli stati non sono supportati in questa versione.` |
| `MainPage_CallsEmptyTitle.Text` | `Nessuna chiamata` |
| `MainPage_CallsEmptyHint.Text` | `Le chiamate non sono supportate in questa versione.` |
| `MainPage_NewChatTooltip` | `Nuova chat` |
| `MainPage_SettingsTooltip` | `Impostazioni` |
| `MainPage_NewChatTitle` | `Nuova chat` |
| `MainPage_NewChatPrompt` | `Numero con prefisso internazionale (es. 393401234567)` |
| `MainPage_NewChatOpen` | `Apri` |
| `MainPage_NewChatCancel` | `Annulla` |
| `MainPage_NewChatInvalid` | `Inserisci un numero valido con prefisso internazionale (es. 393401234567).` |
| `ChatPage_Input.PlaceholderText` | `Scrivi un messaggio` |
| `ChatPage_ImageSelected.Text` | `Immagine selezionata` |
| `ChatPage_Online` | `in linea` |
| `ChatPage_LastSeenToday` | `ultimo accesso oggi {0}` |
| `ChatPage_Me` | `Io` |
| `ChatPage_ImageError` | `Impossibile aprire l'immagine: {0}` |
| `ChatPage_BackTooltip` | `Indietro` |
| `ChatPage_AttachTooltip` | `Allega un'immagine` |
| `ChatPage_SendTooltip` | `Invia` |
| `ChatPage_ClearImageTooltip` | `Rimuovi l'immagine` |
| `ChatMessage_Photo` | `Foto` |
| `ChatMessage_Audio` | `Audio` |
| `ChatMessage_File` | `File` |
| `ChatMessage_Yesterday` | `Ieri` |
| `ConnectionPage_ServerLabel.Text` | `Server WhatsApp (adapter)` |
| `ConnectionPage_ServerPlaceholder.PlaceholderText` | `es. 192.168.1.100` |
| `ConnectionPage_PortLabel.Text` | `Porta TCP` |
| `ConnectionPage_NameLabel.Text` | `Il tuo nome` |
| `ConnectionPage_Connect.Content` | `Connetti al server` |
| `ConnectionPage_Disconnect.Content` | `Disconnetti` |
| `ConnectionPage_WhatsAppTitle.Text` | `Connessione a WhatsApp` |
| `ConnectionPage_QrLogin.Content` | `Accedi con QR code` |
| `ConnectionPage_OrPhone.Text` | `oppure accedi con il tuo numero` |
| `ConnectionPage_PhonePlaceholder.PlaceholderText` | `Numero con prefisso internazionale (es. 393401234567)` |
| `ConnectionPage_GetCode.Content` | `Ottieni codice` |
| `ConnectionPage_Continue.Content` | `Continua` |
| `ConnectionPage_HelpTitle.Text` | `Come accedere` |
| `ConnectionPage_HelpQr.Text` | `QR code: apri WhatsApp sul telefono, apri Dispositivi collegati e tocca Collega un dispositivo, poi inquadra il codice.` |
| `ConnectionPage_HelpPhone.Text` | `Numero: inserisci il numero internazionale, tocca Ottieni codice e digita il codice in WhatsApp.` |
| `ConnectionPage_Version.Text` | `WhatsApp Community Edition v2.0 (GOWA)` |
| `ConnectionPage_FirstRunTitle` | `Prima configurazione` |
| `ConnectionPage_SettingsTitle` | `Impostazioni server` |
| `ConnectionPage_Connecting` | `Connessione a {0}:{1}...` |
| `ConnectionPage_Connected` | `Connesso!` |
| `ConnectionPage_ConnectFailed` | `Connessione fallita` |
| `ConnectionPage_WhatsAppConnected` | `WhatsApp connesso!` |
| `ConnectionPage_ConnectedAs` | `Connesso come {0}` |
| `ConnectionPage_Waiting` | `In attesa di abbinamento: segui le istruzioni qui sotto.` |
| `ConnectionPage_WhatsAppDisconnected` | `Non connesso a WhatsApp. Accedi con il QR code o con il numero.` |
| `ConnectionPage_InvalidPhone` | `Inserisci un numero valido con prefisso internazionale (es. 393401234567).` |
| `ConnectionPage_RequestingQr` | `Richiesta del QR code in corso...` |
| `ConnectionPage_RequestingCode` | `Richiesta del codice in corso...` |
| `ConnectionPage_QrUnavailable` | `QR code non disponibile.` |
| `ConnectionPage_QrHint` | `Apri WhatsApp, apri Dispositivi collegati e tocca Collega un dispositivo, poi inquadra il codice.` |
| `ConnectionPage_QrHintDuration` | `Apri WhatsApp, apri Dispositivi collegati e tocca Collega un dispositivo, poi inquadra il codice (valido circa {0} secondi).` |
| `ConnectionPage_QrError` | `Impossibile mostrare il QR code: {0}` |
| `ConnectionPage_PairCode` | `Codice: {0}` |
| `ConnectionPage_PairCodeHint` | `Inserisci questo codice in WhatsApp: Dispositivi collegati, Collega un dispositivo, Collega con numero di telefono.` |
| `CommService_Connecting` | `Connessione in corso...` |
| `CommService_Connected` | `Connesso al server` |
| `CommService_ConnectError` | `Errore connessione: {0}` |
| `CommService_ConnectionLost` | `Connessione persa: {0}` |
| `CommService_Disconnected` | `Disconnesso` |
| `CommService_NotConnected` | `Non connesso` |
| `CommService_SendError` | `Errore invio: {0}` |
| `CommService_DecryptError` | `Errore decifratura messaggio: {0}` |
| `CommService_Me` | `Io` |
| `CommService_ServerStarted` | `Server avviato sulla porta {0}` |
| `CommService_ServerStartError` | `Errore avvio server: {0}` |
| `CommService_ClientConnected` | `Nuovo client connesso ({0} connessi)` |
| `CommService_ClientDisconnected` | `Client disconnesso: {0}` |
| `CommService_ClientRemoved` | `Client rimosso ({0} connessi)` |
| `DataService_Group` | `Gruppo {0}` |

Machine-checkable form (any script may be used to generate the file, but the result must contain exactly these names):

```bash
grep -c '<data name=' WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw
# expected: 79 and 79
diff <(grep -o '<data name="[^"]*"' WhatsappApp/Strings/en-US/Resources.resw) \
     <(grep -o '<data name="[^"]*"' WhatsappApp/Strings/it-IT/Resources.resw)
# expected: no output
```

- [ ] **Step 3: Create the `Loc` helper**

Create `WhatsappApp/Services/Loc.cs`:

```csharp
using System;
using Windows.ApplicationModel.Resources;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Unico punto di accesso alle stringhe localizzate
    /// (Strings\&lt;lingua&gt;\Resources.resw). La lingua la sceglie il sistema
    /// in base a quella del dispositivo; se manca una risorsa si usa il testo
    /// di fallback, quindi un errore nelle risorse non fa mai esplodere l'app.
    /// </summary>
    public static class Loc
    {
        private static ResourceLoader _loader;

        private static ResourceLoader Loader
        {
            get
            {
                if (_loader == null)
                {
                    try { _loader = ResourceLoader.GetForCurrentView(); }
                    catch { }
                }
                return _loader;
            }
        }

        /// <summary>
        /// Crea il loader sul thread UI. GetForCurrentView non si puo' chiamare
        /// da un thread di background; una volta creato, invece, il loader si
        /// puo' interrogare da qualsiasi thread.
        /// </summary>
        public static void Prewarm()
        {
            var loader = Loader;
            if (loader != null)
            {
                try { loader.GetString("MainPage_TabChats.Text"); }
                catch { }
            }
        }

        public static string Get(string key, string fallback)
        {
            var loader = Loader;
            if (loader == null) return fallback;

            try
            {
                string value = loader.GetString(key);
                return string.IsNullOrEmpty(value) ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }
    }
}
```

- [ ] **Step 4: Register the files and switch the default language**

In `WhatsappApp/WhatsappApp.csproj`, replace:

```xml
    <DefaultLanguage>it-IT</DefaultLanguage>
```

with:

```xml
    <!-- en-US so that any device language other than Italian falls back to
         English instead of Italian. -->
    <DefaultLanguage>en-US</DefaultLanguage>
```

Then replace:

```xml
    <Compile Include="Services\DataService.cs" />
    <Compile Include="Services\SettingsService.cs" />
```

with:

```xml
    <Compile Include="Services\DataService.cs" />
    <Compile Include="Services\Loc.cs" />
    <Compile Include="Services\SettingsService.cs" />
```

Then replace:

```xml
  <ItemGroup>
    <Content Include="Assets\Logo.scale-240.png" />
```

with:

```xml
  <ItemGroup>
    <!-- String resources: one Resources.resw per supported language, in
         Strings\<lang>\. VS2013 indexes <PRIResource> items into the app PRI;
         a file that is not listed here is never shipped. -->
    <PRIResource Include="Strings\en-US\Resources.resw" />
    <PRIResource Include="Strings\it-IT\Resources.resw" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="Assets\Logo.scale-240.png" />
```

- [ ] **Step 5: Warm the loader up on the UI thread**

In `WhatsappApp/App.xaml.cs`, replace:

```csharp
            Frame rootFrame = Window.Current.Content as Frame;
```

with:

```csharp
            // Il loader delle risorse non si puo' creare da un thread di
            // background: lo si crea qui, una volta, sul thread UI.
            Loc.Prewarm();

            Frame rootFrame = Window.Current.Content as Frame;
```

- [ ] **Step 6: Run the guard**

Run: `cd /Users/vincenzo/Documents/WhatsappForWP && node tools/check-resw.js --strict`
Expected: exit 1, but with **only** `missing x:Uid` / `no entry` lines for the literals still in the XAML (Tasks 3-9 remove them) — no `is not one of` message for `<DefaultLanguage>`, no `missing <PRIResource ...>` line, no `has no it-IT translation`, no `never used`.
Cross-check the wiring:

```bash
grep -c 'PRIResource' WhatsappApp/WhatsappApp.csproj   # expected: 2
grep -c 'Loc.cs' WhatsappApp/WhatsappApp.csproj         # expected: 1
grep -A1 '<DefaultLanguage>' WhatsappApp/WhatsappApp.csproj | head -1  # expected: <DefaultLanguage>en-US</DefaultLanguage>
node tools/check-csharp5.js                              # expected: OK
```

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Strings WhatsappApp/Services/Loc.cs WhatsappApp/WhatsappApp.csproj WhatsappApp/App.xaml.cs
git commit -m "feat: follow the device language with en-US/it-IT string resources"
```

---

### Task 3: The WP8 hub shell (MainPage) and the icon set

**Files:**
- Modify: `WhatsappApp/App.xaml`
- Modify: `WhatsappApp/MainPage.xaml` (full replacement)

**Interfaces:**
- Consumes: `IconNewChat` from `App.xaml`, `MainPage_*` keys from Task 2.
- Produces: `x:Name="ChatListView"`, `x:Name="EmptyStatePanel"`, `x:Name="NewChatButton"`, `x:Name="SettingsButton"` — all used by Task 4; `Click="NewChatButton_Click"` and `Click="ConnectionButton_Click"` handler names must stay exactly as they are.

- [ ] **Step 1: Rewrite the icon set in `App.xaml`**

Replace the whole `<ResourceDictionary>` block of `WhatsappApp/App.xaml` with:

```xml
        <ResourceDictionary>
            <!-- WhatsApp Global Colors -->
            <SolidColorBrush x:Key="WhatsAppDarkTealBrush" Color="#FF075E54"/>
            <SolidColorBrush x:Key="WhatsAppTealBrush" Color="#FF128C7E"/>
            <SolidColorBrush x:Key="WhatsAppGreenBrush" Color="#FF25D366"/>
            <SolidColorBrush x:Key="WhatsAppLightGreenBrush" Color="#FFDCF8C6"/>
            <SolidColorBrush x:Key="WhatsAppChatBgBrush" Color="#FFECE5DD"/>
            <SolidColorBrush x:Key="WhatsAppBlueBrush" Color="#FF34A7ED"/>

            <!-- Icon geometries (24x24). Vector paths instead of an icon font:
                 WP8.1 has no "Segoe MDL2 Assets", so glyph-based buttons were
                 blank. Every geometry has to be referenced by a page, so the
                 ones whose last control was removed are gone too. -->
            <PathGeometry x:Key="IconBack" Figures="M15.5,4 L7.5,12 L15.5,20"/>
            <PathGeometry x:Key="IconCalls" Figures="M16.6,16.6 A6.5,6.5 0 0 1 7.4,7.4"/>
            <PathGeometry x:Key="IconChats" Figures="M4,5 L20,5 L20,15.5 L10.5,15.5 L5.5,20 L5.5,15.5 L4,15.5 Z"/>
            <PathGeometry x:Key="IconClose" Figures="M6,6 L18,18 M18,6 L6,18"/>
            <PathGeometry x:Key="IconNewChat" Figures="M4,5 L20,5 L20,15.5 L10.5,15.5 L5.5,20 L5.5,15.5 L4,15.5 Z M12,7.6 L12,13 M9.3,10.3 L14.7,10.3"/>
            <PathGeometry x:Key="IconPhoto" Figures="M3.5,5.5 L20.5,5.5 L20.5,18.5 L3.5,18.5 Z M3.5,15 L8.5,10 L12.5,14 L15.5,11.5 L20.5,15.5 M7.5,8 A1.3,1.3 0 1 1 7.5,10.6 A1.3,1.3 0 1 1 7.5,8"/>
            <PathGeometry x:Key="IconSend" Figures="M2.5,20.5 L21.5,12 L2.5,3.5 L2.5,9.8 L15,12 L2.5,14.2 Z"/>
            <PathGeometry x:Key="IconSettings" Figures="M4,6.5 L20,6.5 M4,12 L20,12 M4,17.5 L20,17.5 M9,4.5 A2,2 0 1 1 9,8.5 A2,2 0 1 1 9,4.5 M15,10 A2,2 0 1 1 15,14 A2,2 0 1 1 15,10 M9,15.5 A2,2 0 1 1 9,19.5 A2,2 0 1 1 9,15.5"/>
            <PathGeometry x:Key="IconStatus" Figures="M12,4 A8,8 0 1 1 12,20 A8,8 0 1 1 12,4 Z M12,7.5 L12,12.3 L16,14.5"/>

            <!-- Override the default app background -->
            <SolidColorBrush x:Key="ApplicationPageBackgroundThemeBrush" Color="#FFECE5DD"/>
        </ResourceDictionary>
```

Added: `IconNewChat`. Removed: `IconAdd`, `IconContacts`, `IconMore`, `IconSearch`, `IconVideo` (their only controls are removed in this task and in Task 5) and the two never-referenced styles `WhatsAppHeaderTextStyle` / `WhatsAppIconButtonStyle`.

Verify nothing still points at the removed keys before committing:

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
grep -rn 'WhatsAppHeaderTextStyle\|WhatsAppIconButtonStyle' WhatsappApp --include=*.xaml --include=*.cs
# expected: no output besides App.xaml, which no longer defines them either
```

- [ ] **Step 2: Replace `MainPage.xaml`**

Replace the entire content of `WhatsappApp/MainPage.xaml` with:

```xml
<Page
    x:Class="WhatsappApp.MainPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:conv="using:WhatsappApp.Converters"
    Background="#FFECE5DD">

    <Page.Resources>
        <conv:BoolToVisibilityConverter x:Key="BoolToVisibility"/>
        <conv:UnreadCountToVisibilityConverter x:Key="UnreadToVisibility"/>
        <conv:InitialToColorConverter x:Key="InitialToColor"/>
        <conv:OnlineToDotColorConverter x:Key="OnlineToDotColor"/>

        <SolidColorBrush x:Key="WhatsAppHeaderBrush" Color="#FF075E54"/>
        <SolidColorBrush x:Key="WhatsAppAccentBrush" Color="#FF25D366"/>
        <SolidColorBrush x:Key="WhatsAppChatBgBrush" Color="#FFECE5DD"/>

        <!-- App bar buttons: the released WP8 app had a bottom bar with icons
             and no floating button. -->
        <Style x:Key="AppBarIconButtonStyle" TargetType="Button">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Padding" Value="0"/>
            <Setter Property="Width" Value="64"/>
            <Setter Property="Height" Value="48"/>
        </Style>
    </Page.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Title bar -->
        <Grid Grid.Row="0" Background="{StaticResource WhatsAppHeaderBrush}" Height="56">
            <TextBlock Text="WhatsApp"
                       Foreground="White" FontSize="24" FontWeight="SemiBold"
                       VerticalAlignment="Center" Margin="16,0,0,4"/>
        </Grid>

        <!-- Hub: the three sections of the released app, swiped as one page -->
        <Pivot Grid.Row="1" Background="{StaticResource WhatsAppHeaderBrush}">

            <PivotItem>
                <PivotItem.Header>
                    <TextBlock x:Uid="MainPage_TabChats" Text="chats"
                               Foreground="White" FontSize="19" FontWeight="SemiBold"/>
                </PivotItem.Header>

                <Grid Background="{StaticResource WhatsAppChatBgBrush}">
                    <ListView x:Name="ChatListView"
                              ItemsSource="{Binding}"
                              Background="Transparent"
                              SelectionMode="Single"
                              SelectionChanged="ChatListView_SelectionChanged">
                        <ListView.ItemsPanel>
                            <ItemsPanelTemplate>
                                <VirtualizingStackPanel/>
                            </ItemsPanelTemplate>
                        </ListView.ItemsPanel>
                        <ListView.ItemTemplate>
                            <DataTemplate>
                                <Grid Height="72" Background="White">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="72"/>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>

                                    <!-- Avatar circle with initials -->
                                    <Grid Grid.Column="0" Width="52" Height="52"
                                          Margin="10,10,0,10" VerticalAlignment="Center">
                                        <Ellipse Width="52" Height="52"
                                                 Fill="{Binding Initials, Converter={StaticResource InitialToColor}}"/>
                                        <TextBlock Text="{Binding Initials}"
                                                   Foreground="White" FontSize="18" FontWeight="SemiBold"
                                                   VerticalAlignment="Center" HorizontalAlignment="Center"/>
                                        <!-- Online dot -->
                                        <Ellipse Width="12" Height="12"
                                                 Fill="{Binding IsOnline, Converter={StaticResource OnlineToDotColor}}"
                                                 Stroke="White" StrokeThickness="2"
                                                 VerticalAlignment="Bottom" HorizontalAlignment="Right"
                                                 Visibility="{Binding IsOnline, Converter={StaticResource BoolToVisibility}}"/>
                                    </Grid>

                                    <!-- Contact info -->
                                    <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                        <TextBlock Text="{Binding Name}" Foreground="Black" FontSize="17" FontWeight="SemiBold"
                                                   TextTrimming="WordEllipsis" Margin="0,0,0,2"/>
                                        <TextBlock Text="{Binding LastMessage}" Foreground="#FF808080" FontSize="14"
                                                   TextTrimming="WordEllipsis" MaxHeight="18"
                                                   Opacity="0.8"/>
                                    </StackPanel>

                                    <!-- Time and unread badge -->
                                    <StackPanel Grid.Column="2" VerticalAlignment="Center" Margin="0,0,12,0">
                                        <TextBlock Text="{Binding LastMessageTime}" Foreground="#FF808080" FontSize="12"
                                                   HorizontalAlignment="Right"/>
                                        <Border MinWidth="20" Height="20"
                                                CornerRadius="10"
                                                Background="{StaticResource WhatsAppAccentBrush}"
                                                HorizontalAlignment="Right"
                                                Margin="0,4,0,0"
                                                Visibility="{Binding UnreadCount, Converter={StaticResource UnreadToVisibility}}">
                                            <TextBlock Text="{Binding UnreadCount}" Foreground="White" FontSize="11"
                                                       FontWeight="Bold" HorizontalAlignment="Center"
                                                       VerticalAlignment="Center"/>
                                        </Border>
                                    </StackPanel>

                                    <!-- Separator -->
                                    <Rectangle Grid.Column="1" Grid.ColumnSpan="2" Height="1" Fill="#1A000000"
                                               VerticalAlignment="Bottom" Margin="0,0,12,0"/>
                                </Grid>
                            </DataTemplate>
                        </ListView.ItemTemplate>
                    </ListView>

                    <!-- Empty state: shown by UpdateEmptyState() -->
                    <StackPanel x:Name="EmptyStatePanel" Visibility="Collapsed"
                                VerticalAlignment="Center" HorizontalAlignment="Center">
                        <Path Data="{StaticResource IconChats}" Stroke="#FFBDBDBD" StrokeThickness="2"
                              StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                              Width="48" Height="48" Stretch="Uniform" HorizontalAlignment="Center"/>
                        <TextBlock x:Uid="MainPage_EmptyTitle" Text="No chats yet"
                                   Foreground="#FF9E9E9E" FontSize="16"
                                   HorizontalAlignment="Center" Margin="0,10,0,0"/>
                        <TextBlock x:Uid="MainPage_EmptyHint" Text="Tap new chat to start"
                                   Foreground="#FFBDBDBD" FontSize="13"
                                   HorizontalAlignment="Center" Margin="0,2,0,0"/>
                    </StackPanel>
                </Grid>
            </PivotItem>

            <PivotItem>
                <PivotItem.Header>
                    <TextBlock x:Uid="MainPage_TabStatus" Text="status"
                               Foreground="White" FontSize="19" FontWeight="SemiBold"/>
                </PivotItem.Header>

                <Grid Background="{StaticResource WhatsAppChatBgBrush}">
                    <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" MaxWidth="320">
                        <Path Data="{StaticResource IconStatus}" Stroke="#FFBDBDBD" StrokeThickness="2"
                              StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                              Width="48" Height="48" Stretch="Uniform" HorizontalAlignment="Center"/>
                        <TextBlock x:Uid="MainPage_StatusEmptyTitle" Text="No status updates"
                                   Foreground="#FF9E9E9E" FontSize="16"
                                   HorizontalAlignment="Center" Margin="0,10,0,0"/>
                        <TextBlock x:Uid="MainPage_StatusEmptyHint" Text="Status updates are not supported in this version."
                                   Foreground="#FFBDBDBD" FontSize="13" TextWrapping="Wrap" TextAlignment="Center"
                                   HorizontalAlignment="Center" Margin="0,2,0,0"/>
                    </StackPanel>
                </Grid>
            </PivotItem>

            <PivotItem>
                <PivotItem.Header>
                    <TextBlock x:Uid="MainPage_TabCalls" Text="calls"
                               Foreground="White" FontSize="19" FontWeight="SemiBold"/>
                </PivotItem.Header>

                <Grid Background="{StaticResource WhatsAppChatBgBrush}">
                    <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" MaxWidth="320">
                        <Path Data="{StaticResource IconCalls}" Stroke="#FFBDBDBD" StrokeThickness="3"
                              StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                              Width="48" Height="48" Stretch="Uniform" HorizontalAlignment="Center"/>
                        <TextBlock x:Uid="MainPage_CallsEmptyTitle" Text="No calls"
                                   Foreground="#FF9E9E9E" FontSize="16"
                                   HorizontalAlignment="Center" Margin="0,10,0,0"/>
                        <TextBlock x:Uid="MainPage_CallsEmptyHint" Text="Calls are not supported in this version."
                                   Foreground="#FFBDBDBD" FontSize="13" TextWrapping="Wrap" TextAlignment="Center"
                                   HorizontalAlignment="Center" Margin="0,2,0,0"/>
                    </StackPanel>
                </Grid>
            </PivotItem>
        </Pivot>

        <!-- Bottom app bar: only actions that work -->
        <Grid Grid.Row="2" Background="#FFF5F5F5" Height="48">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>

            <Button x:Name="NewChatButton" Grid.Column="0"
                    Style="{StaticResource AppBarIconButtonStyle}"
                    Click="NewChatButton_Click">
                <Path Data="{StaticResource IconNewChat}" Stroke="#FF128C7E" StrokeThickness="2"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24"/>
            </Button>

            <Button x:Name="SettingsButton" Grid.Column="2"
                    Style="{StaticResource AppBarIconButtonStyle}"
                    Click="ConnectionButton_Click">
                <Path Data="{StaticResource IconSettings}" Stroke="#FF128C7E" StrokeThickness="2"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24"/>
            </Button>
        </Grid>
    </Grid>
</Page>
```

- [ ] **Step 3: Verify the icons and the markup**

Run:
```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-icons.js
# expected: OK: 9 icon(s) defined, 9 reference(s) resolved.
node tools/check-icons.js --preview
# expected: an ASCII preview per icon; IconNewChat shows a speech bubble with a "+" inside.
node tools/check-resw.js
# expected: FAIL only with "missing x:Uid" lines for MainPage.xaml leftovers (removed in Task 4) -
#           no line mentions IconAdd/IconContacts/IconMore/IconSearch/IconVideo any more.
node -e "require('fs').readFileSync('WhatsappApp/MainPage.xaml','utf8')" # sanity: file readable
```
If the `IconNewChat` preview does not show a bubble with a plus, fix the `Figures` before committing.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/App.xaml WhatsappApp/MainPage.xaml
git commit -m "feat: rebuild the main page as a WP8 pivot hub with a real app bar"
```

---

### Task 4: MainPage code-behind

**Files:**
- Modify: `WhatsappApp/MainPage.xaml.cs`

**Interfaces:**
- Consumes: `Loc.Get` (Task 2), `NewChatButton`/`SettingsButton` names and localized keys (Task 3 + Task 2).
- Produces: nothing new for later tasks.

- [ ] **Step 1: Localize the tooltips and the new-chat dialog**

In `WhatsappApp/MainPage.xaml.cs`, replace:

```csharp
        public MainPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Required;
        }
```

with:

```csharp
        public MainPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Required;

            // I pulsanti dell'app bar sono solo icone: il testo (che il
            // sistema legge anche come etichetta di accessibilita') e' un tooltip.
            ToolTipService.SetToolTip(NewChatButton, Loc.Get("MainPage_NewChatTooltip", "New chat"));
            ToolTipService.SetToolTip(SettingsButton, Loc.Get("MainPage_SettingsTooltip", "Settings"));
        }
```

Then replace the whole `NewChatButton_Click` body:

```csharp
        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            var input = new TextBox
            {
                PlaceholderText = "Numero con prefisso internazionale, es. 393401234567"
            };

            // WP8.1 ContentDialog has no CloseButtonText: "Annulla" is the
            // secondary button, and the dialog can also be dismissed with the
            // hardware back button (result = None).
            var dialog = new ContentDialog
            {
                Title = "Nuova chat",
                Content = input,
                PrimaryButtonText = "Apri",
                SecondaryButtonText = "Annulla"
            };
```

with:

```csharp
        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            var input = new TextBox
            {
                PlaceholderText = Loc.Get("MainPage_NewChatPrompt",
                    "Phone number with country code (e.g. 393401234567)")
            };

            // WP8.1 ContentDialog has no CloseButtonText: the cancel text is the
            // secondary button, and the dialog can also be dismissed with the
            // hardware back button (result = None).
            var dialog = new ContentDialog
            {
                Title = Loc.Get("MainPage_NewChatTitle", "New chat"),
                Content = input,
                PrimaryButtonText = Loc.Get("MainPage_NewChatOpen", "Open"),
                SecondaryButtonText = Loc.Get("MainPage_NewChatCancel", "Cancel")
            };
```

Then replace:

```csharp
            if (phone.Length < 6 || !phone.All(char.IsDigit))
            {
                return;
            }
```

with:

```csharp
            if (phone.Length < 6 || !phone.All(char.IsDigit))
            {
                StatusTextForValidation();
                return;
            }
```

and add the helper right after the closing brace of `NewChatButton_Click`:

```csharp
        /// <summary>
        /// Nessun campo di stato su questa pagina: l'errore di validazione
        /// viene mostrato dove l'utente sta guardando, cioe' nella dialog.
        /// </summary>
        private void StatusTextForValidation()
        {
            // Il testo e' localizzato; la dialog e' gia' chiusa, quindi si
            // segnala l'errore riaprendo l'input con il messaggio.
            var again = new ContentDialog
            {
                Title = Loc.Get("MainPage_NewChatTitle", "New chat"),
                Content = Loc.Get("MainPage_NewChatInvalid",
                    "Enter a valid number with country code (e.g. 393401234567)."),
                PrimaryButtonText = Loc.Get("MainPage_NewChatOpen", "Open"),
                SecondaryButtonText = Loc.Get("MainPage_NewChatCancel", "Cancel")
            };
#pragma warning disable 4014
            again.ShowAsync();
#pragma warning restore 4014
        }
```

- [ ] **Step 2: Verify**

Run:
```bash
cd /Users/vincenzo/Documents/WhatsappForWP
grep -n '"' WhatsappApp/MainPage.xaml.cs | grep -v '//' | grep -v 'Loc.Get' | grep -E '"[A-Za-z ]{4,}"'
# expected: no user-visible literal left (only identifiers such as "@s.whatsapp.net",
#           "#FF075E54", "contacts", "system", "N", "HH:mm", "dd/MM", "?" may remain)
node tools/check-resw.js
# expected: FAIL only for ChatPage.xaml / ConnectionPage.xaml leftovers
node tools/check-csharp5.js
# expected: OK
```

- [ ] **Step 3: Commit**

```bash
git add WhatsappApp/MainPage.xaml.cs
git commit -m "feat: localize the main page tooltips and new-chat dialog"
```

---

### Task 5: ChatPage markup — released look and localized text

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml` (full replacement)
- Modify: `WhatsappApp/App.xaml` (drop `IconVideo`)

**Interfaces:**
- Consumes: `ChatPage_Input.PlaceholderText`, `ChatPage_ImageSelected.Text` from Task 2.
- Produces: `x:Name="ClearImageButton"` (used in Task 6) and the unchanged names `BackButton`, `AttachButton`, `SendButton`, `MessageTextBox`, `MessagesListView`, `ImagePreviewBar`, `SelectedImagePreview`, `ContactNameText`, `OnlineStatusText`.

- [ ] **Step 1: Remove the dead call buttons and localize the markup**

Replace the whole `WhatsappApp/Pages/ChatPage.xaml` with:

```xml
<Page
    x:Class="WhatsappApp.Pages.ChatPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:conv="using:WhatsappApp.Converters"
    Background="#FFECE5DD">

    <Page.Resources>
        <conv:BoolToVisibilityConverter x:Key="BoolToVisibility"/>
        <conv:MessageStatusToStringConverter x:Key="MsgStatusToString"/>
        <conv:MessageStatusToColorConverter x:Key="MsgStatusToColor"/>
        <conv:MessageTypeToImageVisibilityConverter x:Key="MsgTypeToImageVis"/>
        <conv:MessageTypeToTextVisibilityConverter x:Key="MsgTypeToTextVis"/>

        <SolidColorBrush x:Key="WhatsAppHeaderBrush" Color="#FF075E54"/>
        <SolidColorBrush x:Key="WhatsAppAccentBrush" Color="#FF25D366"/>
        <SolidColorBrush x:Key="WhatsAppSentBubbleBrush" Color="#FFDCF8C6"/>
        <SolidColorBrush x:Key="WhatsAppReceivedBubbleBrush" Color="#FFFFFFFF"/>
        <SolidColorBrush x:Key="WhatsAppChatBgBrush" Color="#FFECE5DD"/>
        <SolidColorBrush x:Key="WhatsAppTextPrimaryBrush" Color="#DE000000"/>
        <SolidColorBrush x:Key="WhatsAppTextSecondaryBrush" Color="#8A000000"/>
    </Page.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Header: back + name/status, as in the released app (no dead call buttons) -->
        <Grid Grid.Row="0" Background="{StaticResource WhatsAppHeaderBrush}" Height="56">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <Button x:Name="BackButton" Grid.Column="0"
                    Background="Transparent"
                    Width="48" Height="48" Margin="0,4,0,0" Padding="0"
                    Click="BackButton_Click"
                    BorderThickness="0">
                <Path Data="{StaticResource IconBack}" Stroke="White" StrokeThickness="2.4"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24"/>
            </Button>

            <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="8,0,0,4">
                <TextBlock x:Name="ContactNameText" Text=""
                           Foreground="White" FontSize="18" FontWeight="SemiBold"
                           TextTrimming="WordEllipsis"/>
                <TextBlock x:Name="OnlineStatusText" Text=""
                           Foreground="#B0FFFFFF" FontSize="12" Margin="0,-2,0,0"/>
            </StackPanel>
        </Grid>

        <!-- Messages -->
        <ListView x:Name="MessagesListView" Grid.Row="1"
                  ItemsSource="{Binding}"
                  Background="Transparent"
                  SelectionMode="None">
            <ListView.ItemsPanel>
                <ItemsPanelTemplate>
                    <VirtualizingStackPanel/>
                </ItemsPanelTemplate>
            </ListView.ItemsPanel>
            <ListView.ItemTemplate>
                <DataTemplate>
                    <Grid Margin="4,2,4,2" HorizontalAlignment="Stretch">
                        <!-- Incoming bubble -->
                        <Border Background="{StaticResource WhatsAppReceivedBubbleBrush}"
                                CornerRadius="8"
                                MaxWidth="400"
                                HorizontalAlignment="Left"
                                Visibility="{Binding IsIncoming, Converter={StaticResource BoolToVisibility}}">
                            <StackPanel Margin="10,6,10,4">
                                <Border CornerRadius="4"
                                        Margin="0,0,0,4"
                                        Visibility="{Binding Type, Converter={StaticResource MsgTypeToImageVis}}"
                                        MaxWidth="300" MaxHeight="200">
                                    <Image Source="{Binding MediaImage}"
                                           Stretch="UniformToFill"
                                           HorizontalAlignment="Center"/>
                                </Border>
                                <TextBlock Text="{Binding Text}" TextWrapping="Wrap"
                                           Visibility="{Binding Type, Converter={StaticResource MsgTypeToTextVis}}"
                                           Foreground="{StaticResource WhatsAppTextPrimaryBrush}" FontSize="16"/>
                                <TextBlock Text="{Binding FormattedTime}"
                                           HorizontalAlignment="Right" Margin="0,4,0,0"
                                           Foreground="{StaticResource WhatsAppTextSecondaryBrush}" FontSize="11"/>
                            </StackPanel>
                        </Border>

                        <!-- Outgoing bubble -->
                        <Border Background="{StaticResource WhatsAppSentBubbleBrush}"
                                CornerRadius="8"
                                MaxWidth="400"
                                HorizontalAlignment="Right"
                                Visibility="{Binding IsOutgoing, Converter={StaticResource BoolToVisibility}}">
                            <StackPanel Margin="10,6,10,4">
                                <Border CornerRadius="4"
                                        Margin="0,0,0,4"
                                        Visibility="{Binding Type, Converter={StaticResource MsgTypeToImageVis}}"
                                        MaxWidth="300" MaxHeight="200">
                                    <Image Source="{Binding MediaImage}"
                                           Stretch="UniformToFill"
                                           HorizontalAlignment="Center"/>
                                </Border>
                                <TextBlock Text="{Binding Text}" TextWrapping="Wrap"
                                           Visibility="{Binding Type, Converter={StaticResource MsgTypeToTextVis}}"
                                           Foreground="{StaticResource WhatsAppTextPrimaryBrush}" FontSize="16"/>
                                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,4,0,0">
                                    <TextBlock Text="{Binding FormattedTime}"
                                               Foreground="{StaticResource WhatsAppTextSecondaryBrush}" FontSize="11"/>
                                    <TextBlock Text="{Binding Status, Converter={StaticResource MsgStatusToString}}"
                                               Foreground="{Binding Status, Converter={StaticResource MsgStatusToColor}}"
                                               FontSize="11" Margin="4,0,0,0"/>
                                </StackPanel>
                            </StackPanel>
                        </Border>
                    </Grid>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>

        <!-- Input area -->
        <Grid Grid.Row="2" Background="#FFF0F0F0">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="56"/>
            </Grid.RowDefinitions>

            <!-- Selected image preview -->
            <Grid x:Name="ImagePreviewBar" Grid.Row="0" Height="64"
                  Background="#FFE8E8E8" Visibility="Collapsed">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <Image x:Name="SelectedImagePreview" Grid.Column="0"
                       Width="48" Height="48" Margin="8,8,0,8"
                       Stretch="UniformToFill"/>
                <TextBlock x:Uid="ChatPage_ImageSelected" Grid.Column="1"
                           Text="Image selected"
                           VerticalAlignment="Center"
                           Foreground="#FF606060" FontSize="13"
                           Margin="8,0,0,0"/>
                <Button x:Name="ClearImageButton" Grid.Column="2"
                        Background="Transparent"
                        Width="44" Height="44" BorderThickness="0" Padding="0"
                        Click="ClearImageButton_Click">
                    <Path Data="{StaticResource IconClose}" Stroke="#FF808080" StrokeThickness="2.4"
                          StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                          Width="24" Height="24"/>
                </Button>
            </Grid>

            <!-- Input row -->
            <Grid Grid.Row="1">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>

                <Button x:Name="AttachButton" Grid.Column="0"
                        Background="Transparent"
                        Width="48" Height="48" BorderThickness="0" Padding="0"
                        Margin="0,4,0,4"
                        Click="AttachButton_Click">
                    <Path Data="{StaticResource IconPhoto}" Stroke="#FF808080" StrokeThickness="2"
                          StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                          Width="24" Height="24"/>
                </Button>

                <TextBox x:Name="MessageTextBox" x:Uid="ChatPage_Input" Grid.Column="1"
                         Text=""
                         PlaceholderText="Type a message"
                         VerticalAlignment="Center"
                         Height="40"
                         FontSize="16"
                         Background="White"
                         BorderThickness="0"
                         KeyDown="MessageTextBox_KeyDown"
                         Margin="0,0,4,0"/>

                <Button x:Name="SendButton" Grid.Column="2"
                        Background="{StaticResource WhatsAppAccentBrush}"
                        Foreground="White"
                        Width="48" Height="48"
                        Margin="0,4,8,4"
                        BorderThickness="0" Padding="0"
                        Click="SendButton_Click">
                    <Path Data="{StaticResource IconSend}" Fill="White" Width="22" Height="22"/>
                </Button>
            </Grid>
        </Grid>
    </Grid>
</Page>
```

Changes besides the localized keys: the disabled call/video buttons are gone (and with them `Text="Contatto"` / `Text="in linea"`, now set from code), the `MsgTypeToImageVis`/`MsgTypeToTextVis`/timestamp wrappers are simplified to plain `TextBlock`s, and `x:Name="ClearImageButton"` was added for the tooltip.

- [ ] **Step 2: Drop the now-unused `IconVideo`**

In `WhatsappApp/App.xaml` delete this line:

```xml
            <PathGeometry x:Key="IconVideo" Figures="M3.5,7 L14,7 L14,17 L3.5,17 Z M14,11 L20.5,7.5 L20.5,16.5 L14,13 Z"/>
```

- [ ] **Step 3: Verify**

Run:
```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-icons.js
# expected: OK: 9 icon(s) defined, 9 reference(s) resolved.
node tools/check-resw.js
# expected: FAIL only for ConnectionPage.xaml leftovers
node -e "
const fs=require('fs');
const x=fs.readFileSync('WhatsappApp/Pages/ChatPage.xaml','utf8').replace(/<!--[\s\S]*?-->/g,'');
const names=[...x.matchAll(/x:Name=\"([^\"]+)\"/g)].map(m=>m[1]);
const cs=fs.readFileSync('WhatsappApp/Pages/ChatPage.xaml.cs','utf8');
const missing=names.filter(n=>!cs.includes(n));
console.log(names.length+' named elements; referenced in code-behind:', names.length-missing.length);
if(missing.length) console.log('NOT referenced:', missing.join(', '));
"
# expected: ClearImageButton is listed as not referenced yet (Task 6 adds it) - nothing else may be missing
```

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml WhatsappApp/App.xaml
git commit -m "feat: give the conversation page the released WP8 header and localized text"
```

---

### Task 6: ChatPage code-behind and messages

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs`
- Modify: `WhatsappApp/Models/ChatMessage.cs`

**Interfaces:**
- Consumes: `Loc.Get` (Task 2), `ClearImageButton` (Task 5), `DataService.ActiveChatId` (Task 9 — declare the field there; this task may reference it only after Task 9 lands, so set it in Task 9 instead if applied out of order).
- Produces: localized `ChatMessage.MediaTypeText` and `ChatMessage.FormatTime`.

- [ ] **Step 1: Localize the chat page strings**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, replace:

```csharp
        public ChatPage()
        {
            this.InitializeComponent();
        }
```

with:

```csharp
        public ChatPage()
        {
            this.InitializeComponent();

            // Icon-only buttons: the label lives in the tooltip.
            ToolTipService.SetToolTip(BackButton, Loc.Get("ChatPage_BackTooltip", "Back"));
            ToolTipService.SetToolTip(AttachButton, Loc.Get("ChatPage_AttachTooltip", "Attach an image"));
            ToolTipService.SetToolTip(SendButton, Loc.Get("ChatPage_SendTooltip", "Send"));
            ToolTipService.SetToolTip(ClearImageButton, Loc.Get("ChatPage_ClearImageTooltip", "Remove the image"));
        }
```

Then replace:

```csharp
                ContactNameText.Text = contact.Name;
                OnlineStatusText.Text = contact.IsOnline ? "in linea" : "ultimo accesso oggi " + DateTime.Now.ToString("HH:mm");
```

with:

```csharp
                ContactNameText.Text = contact.Name;
                OnlineStatusText.Text = contact.IsOnline
                    ? Loc.Get("ChatPage_Online", "online")
                    : string.Format(Loc.Get("ChatPage_LastSeenToday", "last seen today at {0}"),
                        DateTime.Now.ToString("HH:mm"));
```

Then replace (twice, once in `SendMessage` and once in `SendImageMessage`):

```csharp
                SenderName = CommunicationService.Instance.MyUsername ?? "Io",
```

with:

```csharp
                SenderName = CommunicationService.Instance.MyUsername ?? Loc.Get("ChatPage_Me", "Me"),
```

Then replace:

```csharp
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Errore selezione immagine: " + ex.Message);
            }
```

with:

```csharp
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    string.Format(Loc.Get("ChatPage_ImageError", "Could not open the image: {0}"), ex.Message));
            }
```

- [ ] **Step 2: Localize the message labels**

In `WhatsappApp/Models/ChatMessage.cs`, add the using:

```csharp
using WhatsappApp.Services;
```

right after:

```csharp
using System.Threading.Tasks;
```

Then replace:

```csharp
            if (dt.Date == now.Date.AddDays(-1))
                return "Ieri";
```

with:

```csharp
            if (dt.Date == now.Date.AddDays(-1))
                return Loc.Get("ChatMessage_Yesterday", "Yesterday");
```

Then replace:

```csharp
        // Short text for media messages shown without loading the full image
        public string MediaTypeText
        {
            get
            {
                if (Type == MessageType.Image) return "Foto";
                if (Type == MessageType.Audio) return "Audio";
                return "File";
            }
        }
```

with:

```csharp
        // Short text for media messages shown without loading the full image
        public string MediaTypeText
        {
            get
            {
                if (Type == MessageType.Image) return Loc.Get("ChatMessage_Photo", "Photo");
                if (Type == MessageType.Audio) return Loc.Get("ChatMessage_Audio", "Audio");
                return Loc.Get("ChatMessage_File", "File");
            }
        }
```

- [ ] **Step 3: Verify**

Run:
```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-resw.js
# expected: FAIL only for ConnectionPage.xaml leftockets
node tools/check-csharp5.js
# expected: OK
grep -n 'Io"\|Ieri"\|Foto"\|"in linea"' WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/Models/ChatMessage.cs
# expected: no output
```

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/Models/ChatMessage.cs
git commit -m "feat: localize the conversation page and message labels"
```

---

### Task 7: CommunicationService — localized messages and a real connection event

**Files:**
- Modify: `WhatsappApp/Services/CommunicationService.cs`

**Interfaces:**
- Consumes: `Loc.Get` (Task 2).
- Produces: `public event EventHandler ConnectionEstablished` — raised on the UI thread once the socket is up, consumed by `ConnectionPage` in Task 8.

- [ ] **Step 1: Add the event**

In `WhatsappApp/Services/CommunicationService.cs`, replace:

```csharp
        public event EventHandler<string> ConnectionStatusChanged;
        public event EventHandler<string> ErrorOccurred;
```

with:

```csharp
        public event EventHandler<string> ConnectionStatusChanged;
        public event EventHandler<string> ErrorOccurred;

        /// <summary>
        /// Sollevato (sul thread UI) quando il socket e' pronto. Sostituisce il
        /// controllo sul testo dello stato, che si rompeva cambiando lingua.
        /// </summary>
        public event EventHandler ConnectionEstablished;
```

Then replace:

```csharp
        private void RaiseErrorOccurred(string error)
```

with:

```csharp
        private void RaiseConnectionEstablished()
        {
            var handler = ConnectionEstablished;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseErrorOccurred(string error)
```

- [ ] **Step 2: Localize every message**

Apply these replacements one by one (each keeps the `Loc.Get` call **inside** the dispatched lambda so the loader is touched on the UI thread):

```csharp
                DispatchOnUiThread(() =>
                    RaiseConnectionStatusChanged("Server avviato sulla porta " + port)
                );
```
→
```csharp
                DispatchOnUiThread(() =>
                {
                    RaiseConnectionStatusChanged(
                        string.Format(Loc.Get("CommService_ServerStarted", "Server started on port {0}"), port));
                    RaiseConnectionEstablished();
                });
```

```csharp
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred("Errore avvio server: " + ex.Message)
                );
```
→
```csharp
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred(string.Format(
                        Loc.Get("CommService_ServerStartError", "Server start error: {0}"), ex.Message))
                );
```

```csharp
            DispatchOnUiThread(() =>
                RaiseConnectionStatusChanged("Nuovo client connesso (" + _serverClients.Count + " connessi)")
            );
```
→
```csharp
            DispatchOnUiThread(() =>
                RaiseConnectionStatusChanged(string.Format(
                    Loc.Get("CommService_ClientConnected", "New client connected ({0} online)"),
                    _serverClients.Count))
            );
```

```csharp
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred("Client disconnesso: " + ex.Message)
                );
```
→
```csharp
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred(string.Format(
                        Loc.Get("CommService_ClientDisconnected", "Client disconnected: {0}"), ex.Message))
                );
```

```csharp
                DispatchOnUiThread(() =>
                    RaiseConnectionStatusChanged("Client rimosso (" + _serverClients.Count + " connessi)")
                );
```
→
```csharp
                DispatchOnUiThread(() =>
                    RaiseConnectionStatusChanged(string.Format(
                        Loc.Get("CommService_ClientRemoved", "Client removed ({0} online)"),
                        _serverClients.Count))
                );
```

```csharp
                DispatchOnUiThread(() =>
                    RaiseConnectionStatusChanged("Connessione in corso...")
                );
```
→
```csharp
                DispatchOnUiThread(() =>
                    RaiseConnectionStatusChanged(Loc.Get("CommService_Connecting", "Connecting..."))
                );
```

```csharp
                DispatchOnUiThread(() =>
                    RaiseConnectionStatusChanged("Connesso al server")
                );
```
→
```csharp
                DispatchOnUiThread(() =>
                {
                    RaiseConnectionStatusChanged(Loc.Get("CommService_Connected", "Connected to the server"));
                    RaiseConnectionEstablished();
                });
```

```csharp
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred("Errore connessione: " + ex.Message)
                );
```
→
```csharp
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred(string.Format(
                        Loc.Get("CommService_ConnectError", "Connection error: {0}"), ex.Message))
                );
```

```csharp
                    DispatchOnUiThread(() =>
                        RaiseErrorOccurred("Connessione persa: " + ex.Message)
                    );
```
→
```csharp
                    DispatchOnUiThread(() =>
                        RaiseErrorOccurred(string.Format(
                            Loc.Get("CommService_ConnectionLost", "Connection lost: {0}"), ex.Message))
                    );
```

```csharp
                DispatchOnUiThread(() =>
                    RaiseConnectionStatusChanged("Disconnesso")
                );
            }
        }
```
→
```csharp
                DispatchOnUiThread(() =>
                    RaiseConnectionStatusChanged(Loc.Get("CommService_Disconnected", "Disconnected"))
                );
            }
        }
```

```csharp
                DispatchOnUiThread(() => RaiseErrorOccurred("Non connesso"));
```
→
```csharp
                DispatchOnUiThread(() => RaiseErrorOccurred(Loc.Get("CommService_NotConnected", "Not connected")));
```

```csharp
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred("Errore invio: " + ex.Message)
                );
```
→
```csharp
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred(string.Format(
                        Loc.Get("CommService_SendError", "Send error: {0}"), ex.Message))
                );
```

```csharp
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred("Errore decifratura messaggio: " + ex.Message)
                );
```
→
```csharp
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred(string.Format(
                        Loc.Get("CommService_DecryptError", "Message decryption error: {0}"), ex.Message))
                );
```

```csharp
                SenderName = _myUsername ?? "Io",
```
→
```csharp
                SenderName = _myUsername ?? Loc.Get("CommService_Me", "Me"),
```

```csharp
            DispatchOnUiThread(() => RaiseConnectionStatusChanged("Disconnesso"));
        }
    }
}
```
→
```csharp
            DispatchOnUiThread(() =>
                RaiseConnectionStatusChanged(Loc.Get("CommService_Disconnected", "Disconnected")));
        }
    }
}
```

- [ ] **Step 3: Verify**

Run:
```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-resw.js
# expected: FAIL only for ConnectionPage.xaml
node tools/check-csharp5.js
# expected: OK
grep -n 'Connesso\|Errore \|Connessione \|Disconnesso\|Non connesso\|"Io"' WhatsappApp/Services/CommunicationService.cs
# expected: no output - every message now comes from Loc.Get
```

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Services/CommunicationService.cs
git commit -m "feat: localize the connection messages and expose a ConnectionEstablished event"
```

---

### Task 8: ConnectionPage markup and code

**Files:**
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml` (full replacement)
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml.cs`

**Interfaces:**
- Consumes: `ConnectionEstablished` (Task 7), `Loc.Get` (Task 2), `ConnectionPage_*` keys (Task 2).
- Produces: nothing new.

- [ ] **Step 1: Replace the markup**

Replace the whole `WhatsappApp/Pages/ConnectionPage.xaml` with:

```xml
<Page
    x:Class="WhatsappApp.Pages.ConnectionPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Background="#FFECE5DD">

    <Page.Resources>
        <SolidColorBrush x:Key="WhatsAppHeaderBrush" Color="#FF075E54"/>
        <SolidColorBrush x:Key="WhatsAppAccentBrush" Color="#FF25D366"/>
        <SolidColorBrush x:Key="WhatsAppGreenBrush" Color="#FF128C7E"/>
    </Page.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <Grid Grid.Row="0" Background="{StaticResource WhatsAppHeaderBrush}" Height="56">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <Button x:Name="BackButton" Grid.Column="0"
                    Background="Transparent"
                    Width="48" Height="48" Margin="0,4,0,0" Padding="0"
                    Click="BackButton_Click"
                    BorderThickness="0">
                <Path Data="{StaticResource IconBack}" Stroke="White" StrokeThickness="2.4"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24"/>
            </Button>

            <TextBlock x:Name="PageTitleText" Grid.Column="1" Text=""
                       Foreground="White" FontSize="20" FontWeight="SemiBold"
                       VerticalAlignment="Center" Margin="8,0,0,4"/>
        </Grid>

        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
            <StackPanel Margin="16,16,16,16">

                <!-- Server (adapter) -->
                <TextBlock x:Uid="ConnectionPage_ServerLabel" Text="WhatsApp server (adapter)"
                           Foreground="#FF075E54" FontSize="14" FontWeight="SemiBold"/>
                <TextBox x:Name="ServerAddressBox" x:Uid="ConnectionPage_ServerPlaceholder"
                         Text="192.168.1.100"
                         PlaceholderText="e.g. 192.168.1.100"
                         Background="White" FontSize="16" Margin="0,4,0,0"/>

                <TextBlock x:Uid="ConnectionPage_PortLabel" Text="TCP port"
                           Foreground="#FF075E54" FontSize="14" FontWeight="SemiBold" Margin="0,12,0,0"/>
                <TextBox x:Name="ServerPortBox" Text="8585"
                         PlaceholderText="8585"
                         Background="White" FontSize="16" Margin="0,4,0,0"/>

                <TextBlock x:Uid="ConnectionPage_NameLabel" Text="Your name"
                           Foreground="#FF075E54" FontSize="14" FontWeight="SemiBold" Margin="0,12,0,0"/>
                <TextBox x:Name="UsernameBox" Text=""
                         Background="White" FontSize="16" Margin="0,4,0,0"/>

                <Button x:Name="ActionButton" x:Uid="ConnectionPage_Connect"
                        Content="Connect to the server"
                        Background="{StaticResource WhatsAppAccentBrush}"
                        Foreground="White" FontSize="18" FontWeight="SemiBold"
                        Height="52" BorderThickness="0" Margin="0,12,0,0"
                        Click="ActionButton_Click"/>

                <Button x:Name="DisconnectButton" x:Uid="ConnectionPage_Disconnect"
                        Content="Disconnect"
                        Background="#FFE53935"
                        Foreground="White" FontSize="16"
                        Height="44" BorderThickness="0" Margin="0,8,0,0"
                        Visibility="Collapsed"
                        Click="DisconnectButton_Click"/>

                <!-- Status -->
                <Border x:Name="StatusPanel" Background="#FFF0F0F0" CornerRadius="8" Margin="0,8,0,0"
                        Visibility="Collapsed">
                    <StackPanel Margin="12">
                        <TextBlock x:Name="StatusText" Text=""
                                   Foreground="#FF128C7E" FontSize="14" TextAlignment="Center"
                                   TextWrapping="Wrap"/>
                    </StackPanel>
                </Border>

                <!-- WhatsApp login -->
                <StackPanel x:Name="WhatsAppPanel" Visibility="Collapsed" Margin="0,16,0,0">
                    <TextBlock x:Uid="ConnectionPage_WhatsAppTitle" Text="WhatsApp connection"
                               Foreground="#FF075E54" FontSize="14" FontWeight="SemiBold"/>

                    <Border Background="White" CornerRadius="8" Margin="0,4,0,0">
                        <StackPanel Margin="12">
                            <TextBlock x:Name="WhatsAppStateText" Text=""
                                       Foreground="#FF606060" FontSize="14" TextWrapping="Wrap"
                                       TextAlignment="Center"/>

                            <Button x:Name="LoginQrButton" x:Uid="ConnectionPage_QrLogin"
                                    Content="Sign in with QR code"
                                    Background="{StaticResource WhatsAppGreenBrush}"
                                    Foreground="White" FontSize="16"
                                    Height="44" BorderThickness="0" Margin="0,10,0,0"
                                    Click="LoginQrButton_Click"/>

                            <Image x:Name="QrImage" Width="240" Height="240" Margin="0,10,0,0"
                                   Visibility="Collapsed" Stretch="Uniform"/>

                            <TextBlock x:Name="QrInfoText" Text="" Foreground="#FF808080" FontSize="12"
                                       TextWrapping="Wrap" TextAlignment="Center" Margin="0,6,0,0"/>

                            <TextBlock x:Uid="ConnectionPage_OrPhone" Text="or sign in with your phone number"
                                       Foreground="#FF606060" FontSize="13"
                                       HorizontalAlignment="Center" Margin="0,14,0,0"/>

                            <TextBox x:Name="PhoneBox" x:Uid="ConnectionPage_PhonePlaceholder"
                                     Text=""
                                     PlaceholderText="Number with country code (e.g. 393401234567)"
                                     Background="#FFF7F7F7" FontSize="16" Margin="0,6,0,0"/>

                            <Button x:Name="LoginCodeButton" x:Uid="ConnectionPage_GetCode"
                                    Content="Get the code"
                                    Background="{StaticResource WhatsAppGreenBrush}"
                                    Foreground="White" FontSize="16"
                                    Height="44" BorderThickness="0" Margin="0,8,0,0"
                                    Click="LoginCodeButton_Click"/>

                            <TextBlock x:Name="PairCodeText" Text="" Foreground="#FF075E54" FontSize="22"
                                       FontWeight="Bold" TextAlignment="Center"
                                       TextWrapping="Wrap" Margin="0,10,0,0"/>

                            <Button x:Name="ContinueButton" x:Uid="ConnectionPage_Continue"
                                    Content="Continue"
                                    Background="{StaticResource WhatsAppAccentBrush}"
                                    Foreground="White" FontSize="18" FontWeight="SemiBold"
                                    Height="48" BorderThickness="0" Margin="0,14,0,0"
                                    IsEnabled="False"
                                    Click="ContinueButton_Click"/>
                        </StackPanel>
                    </Border>

                    <!-- How to -->
                    <Border Background="#FFF7F7F7" CornerRadius="8" Margin="0,8,0,0">
                        <StackPanel Margin="12">
                            <TextBlock x:Uid="ConnectionPage_HelpTitle" Text="How to sign in"
                                       Foreground="#FF606060" FontSize="12" FontWeight="SemiBold"/>
                            <TextBlock x:Uid="ConnectionPage_HelpQr"
                                       Text="QR code: open WhatsApp on your phone, open Linked devices and tap Link a device, then scan the code."
                                       Foreground="#FF606060" FontSize="12" TextWrapping="Wrap" Margin="0,4,0,0"/>
                            <TextBlock x:Uid="ConnectionPage_HelpPhone"
                                       Text="Phone number: enter the international number, tap Get the code, then type it in WhatsApp."
                                       Foreground="#FF606060" FontSize="12" TextWrapping="Wrap" Margin="0,4,0,0"/>
                        </StackPanel>
                    </Border>
                </StackPanel>

                <TextBlock x:Uid="ConnectionPage_Version" Text="WhatsApp Community Edition v2.0 (GOWA)"
                           Foreground="#FF808080" FontSize="11"
                           HorizontalAlignment="Center" Margin="0,16,0,16"/>
            </StackPanel>
        </ScrollViewer>
    </Grid>
</Page>
```

The help `<TextBlock>` with `<Run>` children is replaced by three `x:Uid`-ed `TextBlock`s (`Run` cannot carry `x:Uid`), and every literal that is set from code (`ServerPortBox` placeholder, version banner excepted) is left as the markup fallback.

- [ ] **Step 2: Localize and de-sniff the code-behind**

In `WhatsappApp/Pages/ConnectionPage.xaml.cs`, replace:

```csharp
        public ConnectionPage()
        {
            this.InitializeComponent();
        }
```

with:

```csharp
        public ConnectionPage()
        {
            this.InitializeComponent();
            ToolTipService.SetToolTip(BackButton, Loc.Get("ChatPage_BackTooltip", "Back"));
        }
```

Replace:

```csharp
            PageTitleText.Text = _isFirstRun ? "Prima configurazione" : "Impostazioni Server";

            CommunicationService.Instance.ConnectionStatusChanged += OnConnectionStatusChanged;
            CommunicationService.Instance.ErrorOccurred += OnErrorOccurred;
            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;
```

with:

```csharp
            PageTitleText.Text = _isFirstRun
                ? Loc.Get("ConnectionPage_FirstRunTitle", "First-time setup")
                : Loc.Get("ConnectionPage_SettingsTitle", "Server settings");

            CommunicationService.Instance.ConnectionStatusChanged += OnConnectionStatusChanged;
            CommunicationService.Instance.ErrorOccurred += OnErrorOccurred;
            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;
            CommunicationService.Instance.ConnectionEstablished += OnConnectionEstablished;
```

Replace:

```csharp
            CommunicationService.Instance.ControlMessageReceived -= OnControlMessageReceived;
        }
```

with:

```csharp
            CommunicationService.Instance.ControlMessageReceived -= OnControlMessageReceived;
            CommunicationService.Instance.ConnectionEstablished -= OnConnectionEstablished;
        }

        private void OnConnectionEstablished(object sender, EventArgs e)
        {
            ShowConnectedState();
        }
```

Replace:

```csharp
            StatusText.Text = "Connessione a " + address + ":" + port + "...";

            bool connected = await CommunicationService.Instance.ConnectToServerAsync(address, port, username);
            if (connected)
            {
                SettingsService.Save(address, port, username);
                StatusText.Text = "Connesso!";
                ShowConnectedState();
                await CommunicationService.Instance.SendControlAsync("status");
            }
            else
            {
                StatusText.Text = "Connessione fallita";
                ActionButton.IsEnabled = true;
            }
```

with:

```csharp
            StatusText.Text = string.Format(
                Loc.Get("ConnectionPage_Connecting", "Connecting to {0}:{1}..."), address, port);

            bool connected = await CommunicationService.Instance.ConnectToServerAsync(address, port, username);
            if (connected)
            {
                SettingsService.Save(address, port, username);
                StatusText.Text = Loc.Get("ConnectionPage_Connected", "Connected!");
                ShowConnectedState();
                await CommunicationService.Instance.SendControlAsync("status");
            }
            else
            {
                StatusText.Text = Loc.Get("ConnectionPage_ConnectFailed", "Connection failed");
                ActionButton.IsEnabled = true;
            }
```

Replace the three WhatsApp-state branches:

```csharp
                case "connected":
                    WhatsAppStateText.Text = string.IsNullOrEmpty(accountJid)
                        ? "WhatsApp connesso!"
                        : "Connesso come " + accountJid.Split('@')[0];
```

with:

```csharp
                case "connected":
                    WhatsAppStateText.Text = string.IsNullOrEmpty(accountJid)
                        ? Loc.Get("ConnectionPage_WhatsAppConnected", "WhatsApp connected!")
                        : string.Format(Loc.Get("ConnectionPage_ConnectedAs", "Connected as {0}"),
                            accountJid.Split('@')[0]);
```

```csharp
                case "waiting":
                    WhatsAppStateText.Text = "In attesa di abbinamento... segui le istruzioni qui sotto.";
```

with:

```csharp
                case "waiting":
                    WhatsAppStateText.Text = Loc.Get("ConnectionPage_Waiting",
                        "Waiting for pairing: follow the instructions below.");
```

```csharp
                default:
                    WhatsAppStateText.Text = "Non connesso a WhatsApp. Accedi con QR code o con il numero.";
```

with:

```csharp
                default:
                    WhatsAppStateText.Text = Loc.Get("ConnectionPage_WhatsAppDisconnected",
                        "Not connected to WhatsApp. Sign in with the QR code or your phone number.");
```

Replace:

```csharp
                case "paircode":
                    PairCodeText.Text = "Codice: " + message.PairCode;
                    WhatsAppStateText.Text = "Inserisci questo codice su WhatsApp > Dispositivi collegati > Collega un dispositivo > Collega con numero di telefono.";
                    QrImage.Visibility = Visibility.Collapsed;
                    break;
```

with:

```csharp
                case "paircode":
                    PairCodeText.Text = string.Format(Loc.Get("ConnectionPage_PairCode", "Code: {0}"),
                        message.PairCode);
                    WhatsAppStateText.Text = Loc.Get("ConnectionPage_PairCodeHint",
                        "Enter this code in WhatsApp: Linked devices, Link a device, Link with phone number instead.");
                    QrImage.Visibility = Visibility.Collapsed;
                    break;
```

Replace:

```csharp
            if (string.IsNullOrEmpty(base64))
            {
                QrInfoText.Text = "QR code non disponibile.";
                return;
            }
```

with:

```csharp
            if (string.IsNullOrEmpty(base64))
            {
                QrInfoText.Text = Loc.Get("ConnectionPage_QrUnavailable", "QR code not available.");
                return;
            }
```

Replace:

```csharp
                QrInfoText.Text = duration > 0
                    ? "Apri WhatsApp > Dispositivi collegati > Collega un dispositivo e inquadra il codice (valido ~" + duration + "s)."
                    : "Apri WhatsApp > Dispositivi collegati > Collega un dispositivo e inquadra il codice.";
            }
            catch (Exception ex)
            {
                QrInfoText.Text = "Impossibile mostrare il QR code: " + ex.Message;
            }
```

with:

```csharp
                QrInfoText.Text = duration > 0
                    ? string.Format(Loc.Get("ConnectionPage_QrHintDuration",
                        "Open WhatsApp, open Linked devices and tap Link a device, then scan the code (valid for about {0} seconds)."),
                        duration)
                    : Loc.Get("ConnectionPage_QrHint",
                        "Open WhatsApp, open Linked devices and tap Link a device, then scan the code.");
            }
            catch (Exception ex)
            {
                QrInfoText.Text = string.Format(
                    Loc.Get("ConnectionPage_QrError", "Could not show the QR code: {0}"), ex.Message);
            }
```

Replace:

```csharp
            PairCodeText.Text = "";
            QrInfoText.Text = "Richiesta del QR code in corso...";
            await CommunicationService.Instance.SendControlAsync("login.qr");
```

with:

```csharp
            PairCodeText.Text = "";
            QrInfoText.Text = Loc.Get("ConnectionPage_RequestingQr", "Requesting the QR code...");
            await CommunicationService.Instance.SendControlAsync("login.qr");
```

Replace:

```csharp
                WhatsAppStateText.Text = "Inserisci un numero valido con prefisso internazionale (es. 393401234567).";
                return;
            }

            QrImage.Visibility = Visibility.Collapsed;
            QrInfoText.Text = "";
            WhatsAppStateText.Text = "Richiesta del codice in corso...";
```

with:

```csharp
                WhatsAppStateText.Text = Loc.Get("ConnectionPage_InvalidPhone",
                    "Enter a valid number with country code (e.g. 393401234567).");
                return;
            }

            QrImage.Visibility = Visibility.Collapsed;
            QrInfoText.Text = "";
            WhatsAppStateText.Text = Loc.Get("ConnectionPage_RequestingCode", "Requesting the code...");
```

Replace the string-sniffing status handler:

```csharp
        private void OnConnectionStatusChanged(object sender, string status)
        {
            StatusText.Text = status;
            StatusPanel.Visibility = Visibility.Visible;
            if (status != null && (status.Contains("Connesso") || status.Contains("avviato")))
                ShowConnectedState();
        }
```

with:

```csharp
        /// <summary>
        /// Mostra solo il messaggio: se la connessione e' riuscita lo dice
        /// l'evento ConnectionEstablished, non il testo (che e' localizzato).
        /// </summary>
        private void OnConnectionStatusChanged(object sender, string status)
        {
            StatusText.Text = status;
            StatusPanel.Visibility = Visibility.Visible;
        }
```

- [ ] **Step 3: Verify**

Run:
```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-resw.js
# expected: OK: 79 key(s) in en-US and it-IT, every x:Uid and Loc.Get lookup resolved.
node tools/check-resw.js --strict
# expected: same line, exit 0 (no unused key)
node tools/check-csharp5.js
# expected: OK
grep -n 'Contains(' WhatsappApp/Pages/ConnectionPage.xaml.cs
# expected: no output - the localized-string sniffing is gone
node -e "
const fs=require('fs');
const x=fs.readFileSync('WhatsappApp/Pages/ConnectionPage.xaml','utf8').replace(/<!--[\s\S]*?-->/g,'');
const names=[...x.matchAll(/x:Name=\"([^\"]+)\"/g)].map(m=>m[1]);
const cs=fs.readFileSync('WhatsappApp/Pages/ConnectionPage.xaml.cs','utf8');
const missing=names.filter(n=>!cs.includes(n));
console.log(names.length+' named elements; unreferenced:', missing.length?missing.join(', '):'none');
"
# expected: unreferenced: none
```

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Pages/ConnectionPage.xaml WhatsappApp/Pages/ConnectionPage.xaml.cs
git commit -m "feat: localize the setup page and stop reading the connection state from its text"
```

---

### Task 9: Dead converter, localized group names, unread badge on the open chat

**Files:**
- Modify: `WhatsappApp/Converters/Converters.cs`
- Modify: `WhatsappApp/Services/DataService.cs`
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs`

**Interfaces:**
- Consumes: `Loc.Get` (Task 2).
- Produces: `public string DataService.ActiveChatId { get; set; }` (used by `ChatPage`).

- [ ] **Step 1: Delete the dead converter**

First confirm it is dead:

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
grep -rn 'OnlineStatus' WhatsappApp --include=*.xaml
# expected: no output
```

Then delete this block from `WhatsappApp/Converters/Converters.cs` (including the `///` summary above it):

```csharp
    /// <summary>
    /// Converts a boolean to online status text
    /// </summary>
    public class OnlineStatusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool isOnline = (bool)value;
            if (parameter != null && parameter.ToString() == "ForContact")
            {
                return isOnline ? "in linea" : "non in linea";
            }
            return isOnline ? "Online" : "Offline";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

```

- [ ] **Step 2: Localize the group name and track the open chat**

In `WhatsappApp/Services/DataService.cs`, replace:

```csharp
        private Contact _selectedContact;
        private string _connectionStatus;
        private bool _isServerRunning;
```

with:

```csharp
        private Contact _selectedContact;
        private string _connectionStatus;
        private bool _isServerRunning;
        private string _activeChatId;

        /// <summary>
        /// Chat attualmente aperta: i messaggi che arrivano qui sono gia' letti,
        /// quindi non devono incrementare il contatore dei non letti.
        /// </summary>
        public string ActiveChatId
        {
            get { return _activeChatId; }
            set { _activeChatId = value; }
        }
```

Replace:

```csharp
            if (jid.EndsWith("@g.us")) return "Gruppo " + user;
```

with:

```csharp
            if (jid.EndsWith("@g.us")) return string.Format(Loc.Get("DataService_Group", "Group {0}"), user);
```

Replace both unread increments:

```csharp
                contact.LastMessage = message.Text;
                contact.LastMessageTime = message.FormattedTime;
                if (message.IsIncoming)
                    contact.UnreadCount++;
```

with (this exact block appears twice — once in `OnNetworkMessageReceived`, once in `AddMessage`; apply to both):

```csharp
                contact.LastMessage = message.Text;
                contact.LastMessageTime = message.FormattedTime;
                if (message.IsIncoming && message.ChatId != _activeChatId)
                    contact.UnreadCount++;
```

In the `OnNetworkMessageReceived` variant only, the increment is written as:

```csharp
                contact.LastMessage = message.Text;
                contact.LastMessageTime = message.FormattedTime;
                if (message.IsIncoming)
                    contact.UnreadCount++;

                var idx = _contacts.IndexOf(contact);
```

→

```csharp
                contact.LastMessage = message.Text;
                contact.LastMessageTime = message.FormattedTime;
                if (message.IsIncoming && message.ChatId != _activeChatId)
                    contact.UnreadCount++;

                var idx = _contacts.IndexOf(contact);
```

- [ ] **Step 3: Tell the service which chat is open**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, replace:

```csharp
                // Listen for new messages
                CommunicationService.Instance.MessageReceived += OnMessageReceived;
```

with:

```csharp
                // Da qui in poi i messaggi di questa chat sono gia' letti
                DataService.Instance.ActiveChatId = contact.Id;

                // Listen for new messages
                CommunicationService.Instance.MessageReceived += OnMessageReceived;
```

and replace:

```csharp
            base.OnNavigatedFrom(e);
            CommunicationService.Instance.MessageReceived -= OnMessageReceived;
```

with:

```csharp
            base.OnNavigatedFrom(e);
            CommunicationService.Instance.MessageReceived -= OnMessageReceived;
            DataService.Instance.ActiveChatId = null;
```

- [ ] **Step 4: Verify**

Run:
```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-resw.js --strict
# expected: OK: 79 key(s) ... exit 0
node tools/check-csharp5.js
# expected: OK
node tools/check-icons.js
# expected: OK: 9 icon(s) defined, 9 reference(s) resolved.
grep -rn 'in linea\|OnlineStatusConverter\|"Gruppo ' WhatsappApp --include=*.cs
# expected: no output besides ChatPage.xaml.cs's Loc.Get("ChatPage_Online", "online")
```

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Converters/Converters.cs WhatsappApp/Services/DataService.cs WhatsappApp/Pages/ChatPage.xaml.cs
git commit -m "fix: drop the dead converter and stop counting the open chat as unread"
```

---

### Task 10: Final verification, docs, push

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: everything above.
- Produces: the finished change set.

- [ ] **Step 1: Run every gate**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js        # expected: OK: 15 C# file(s) are C# 5 compatible.
node tools/check-icons.js          # expected: OK: 9 icon(s) defined, 9 reference(s) resolved.
node tools/check-resw.js --strict  # expected: OK: 79 key(s) in en-US and it-IT, every x:Uid and Loc.Get lookup resolved.
for f in WhatsappApp/App.xaml WhatsappApp/MainPage.xaml WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Pages/ConnectionPage.xaml; do
  xmllint --noout "$f" || echo "MALFORMED: $f"
done                                # expected: no MALFORMED line
cd WhatsappBridge && npm test && cd ..
# expected: 29 pass, 0 fail
grep -rn 'FontFamily' WhatsappApp --include=*.xaml   # expected: no output
git status --short
```

- [ ] **Step 2: Document the localization contract in `README.md`**

Insert this section right before the existing `## Sviluppo` (or the closest equivalent heading), adapting to the file's actual current headings:

```markdown
## Lingua dell'app

L'app segue automaticamente la lingua del dispositivo tramite risorse `.resw`:

| Lingua | File | Note |
| --- | --- | --- |
| Inglese | `WhatsappApp/Strings/en-US/Resources.resw` | `<DefaultLanguage>`, fallback per ogni altra lingua |
| Italiano | `WhatsappApp/Strings/it-IT/Resources.resw` | |

- I testi dell'interfaccia dichiarati in XAML usano `x:Uid` (la proprieta' deve
  corrispondere al tipo: `TextBlock` -> `.Text`, `Button` -> `.Content`,
  `TextBox` -> `.PlaceholderText`).
- I testi costruiti in C# passano da `Loc.Get("Chiave", "fallback")`
  (`WhatsappApp/Services/Loc.cs`), che non lancia mai eccezioni.
- I pulsanti con la sola icona non usano `x:Uid` (sovrascriverebbe il `Path`):
  l'etichetta e' un tooltip impostato da `Loc.Get` nel costruttore della pagina.
- Non aggiungere stringhe a mano: `node tools/check-resw.js --strict` fallisce
  se una `x:Uid`, una `Loc.Get` o una chiave non e' presente in entrambi i file,
  se i due file non hanno le stesse chiavi o se il `.resw` non e' registrato
  come `PRIResource` nel `.csproj`.
- Cambio lingua: Windows riavvia l'app quando cambia la lingua di sistema; se
  l'app resta in inglese/italiano vecchio, chiuderla e riaprirla.
```

- [ ] **Step 3: Commit and push**

```bash
git add README.md
git commit -m "docs: document the resw localization contract"
git push origin master
```

Expected: `master -> master` accepted.

---

## Self-Review

**1. Spec coverage**

| Requirement | Task |
| --- | --- |
| Language follows the device automatically | 2 (resw + `PRIResource` + `DefaultLanguage`), 2 Step 5 (`Loc.Prewarm` on the UI thread), 8/9/7/6/4 (every consumer) |
| English and Italian | 2 (both files, identical key sets) |
| Interface like the released WP8 app | 3 (title bar + `Pivot` with `chat`/`stato`/`chiamate` + bottom app bar), 5 (conversation header without dead call buttons) |
| "Silverlight pages" | 3 — the WP8 Silverlight signature was the `Pivot` hub; this project is a WP8.1 **WinRT** app (`Windows.UI.Xaml`), so `PhoneApplicationPage`/`ApplicationBar` from Silverlight do not exist here. The layout is reproduced with the WinRT `Pivot` and the project's own verified vector icons. |
| No regressions in the adapter | 10 (`npm test` = 29/29) |
| Push | 10 Step 3 |

**2. Placeholder scan:** no `TBD`/`TODO`/"similar to Task N"/"add error handling". Every code step carries the literal replacement text; the resource inventory is spelled out key by key in Task 2.

**3. Type consistency:** `Loc.Get(string, string)` / `Loc.Prewarm()` are introduced in Task 2 and used with exactly that signature in Tasks 4, 6, 7, 8, 9. `ConnectionEstablished` is `EventHandler` in Task 7 and consumed as `(object, EventArgs)` in Task 8. `DataService.ActiveChatId` is a `string` property declared in Task 9 and assigned in Task 9 Step 3. `ClearImageButton` is created in Task 5 and consumed in Task 6. Key names used by `Loc.Get`/`x:Uid` in Tasks 3-9 all appear in the Task 2 inventories (79 keys, checked by `--strict`).

**Known follow-ups (not in this plan):** in-app chat search, calling/status support (both would need new adapter endpoints), manifest localization for the tile name.
