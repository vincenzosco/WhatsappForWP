# WP8.1 Remaining Build Errors Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clear the last build errors of `WhatsappApp.sln` on the Windows Phone 8.1 toolchain — two `Windows.Security.Cryptography` / `ContentDialog` API mismatches in the app code — and prove that the four `MainPage.xaml` converter errors disappear with them.

**Architecture:** The previous C# 5 port removed every syntax error, so the compiler can now run semantic analysis and it reports three genuine API errors: `CryptographicBuffer.CreateFromByteArray` has **only** the single-argument overload in the WP8.1 WinRT projection (Windows 8.1/UWP also ship a `(byte[], uint, uint)` overload), and the WP8.1 `ContentDialog` has **no `CloseButtonText`** property (also Windows 10 only). Both are fixed by using the WP8.1-compatible API — manual array slicing with `Buffer.BlockCopy`, and `SecondaryButtonText` instead of `CloseButtonText` — with no change in behaviour. The four `MainPage.xaml` errors ("The name X does not exist in the namespace using:WhatsappApp.Converters") are a **downstream symptom**, not a converter bug: `MainPage.xaml.cs` fails to compile, so the XAML pass cannot resolve the converters declared in the same assembly.

**Tech Stack:** C# 5 (WP8.1 toolchain), WinRT API surface of Windows Phone 8.1, Node.js 18+ (guard script), MSBuild/VS2013 on Windows (authoritative gate).

## Global Constraints

- The WP8.1 project compiles with the **C# 5 compiler**: no C# 6/7 syntax may be introduced by this plan (`tools/check-csharp5.js` must stay green).
- The WP8.1 WinRT projection is a **subset** of the Windows 8.1 API surface. Verified gaps hit by this codebase: `CryptographicBuffer.CreateFromByteArray(byte[], uint, uint)` and `ContentDialog.CloseButtonText` do not exist on WP8.1. `ContentDialog.PrimaryButtonText` and `ContentDialog.SecondaryButtonText` do exist.
- No behaviour change. In particular the decrypted payload must stay byte-identical: the same IV bytes and the same ciphertext+tag bytes must be passed to `CryptographicEngine.Decrypt`, and the "Nuova chat" dialog must still open a chat only when the user confirms.
- Do not touch `WhatsappBridge/**` (29/29 `node --test` tests must stay green) and do not change the AES passphrase or the wire protocol.
- UI strings and code comments stay Italian.
- Error numbers in this plan refer to the user's Error List: 5 and 6 = `CryptoHelper.cs` lines 74-75, 7 = `MainPage.xaml.cs` line 89, 1-4 = `MainPage.xaml` lines 10-13.
- Authoritative gate: `msbuild WhatsappApp.sln /p:Configuration=Debug /p:Platform=x86` on Windows (must end with `0 Error(s)`). Everything runnable on macOS is a proxy.

## File Structure

| File | Responsibility | Change |
| --- | --- | --- |
| `tools/check-csharp5.js` | Static guard for the WP8.1 toolchain: C# 5 syntax **and** the reduced WP8.1 API surface. | add `API_RULES` and report them |
| `WhatsappApp/Services/CryptoHelper.cs` | AES-256-GCM encrypt/decrypt for the adapter channel. | `Decrypt` slices the frame manually before building the `IBuffer`s (errors 5, 6) |
| `WhatsappApp/MainPage.xaml.cs` | Contact list + "Nuova chat" dialog. | `CloseButtonText` → `SecondaryButtonText` (error 7) |
| `README.md` | Build instructions. | one sentence: the guard also checks the WP8.1 API surface |

No XAML and no `.csproj` change: the four converter errors require no edit.

---

### Task 1: Teach the guard the WP8.1 API rules

**Files:**
- Modify: `tools/check-csharp5.js`

**Interfaces:**
- Consumes: nothing.
- Produces: `node tools/check-csharp5.js` also fails on `CryptographicBuffer.CreateFromByteArray(...)` called with three arguments and on `CloseButtonText` written with the property syntax (`.CloseButtonText` or a `CloseButtonText =` initializer line), in addition to the C# 5 syntax rules. Accidental matches on prose are avoided by requiring one of those two usages, so the explanatory comment added in Task 3 Step 1 is not flagged. Exit `1` with `<file>:<line>: <rule>  ->  <source line>`; exit `0` with `OK: N C# file(s) are C# 5 compatible.`

- [ ] **Step 1: Update the header comment**

Replace the whole header comment block:

```js
#!/usr/bin/env node
/**
 * tools/check-csharp5.js
 *
 * Guard for the Windows Phone 8.1 app. It fails on two classes of problem:
 *  1. C# 6/7 syntax: the WP8.1 toolchain compiles the app with the legacy
 *     C# 5 compiler, so such syntax breaks the build with errors such as
 *     "Invalid token '=' in class, struct, or interface member declaration"
 *     and "Unexpected character '$'".
 *  2. Windows 8.1/Windows 10-only members that the reduced WP8.1 WinRT
 *     projection does not expose, which break the build with CS1501/CS1061.
 *
 * Usage:  node tools/check-csharp5.js
 * Exit code 0 = every file is compatible, 1 = violations found.
 */
```

- [ ] **Step 2: Add the `API_RULES` array**

Insert immediately after the closing `];` of `RULES`:

```js
// Members that exist on Windows 8.1 / Windows 10 but not on the WP8.1
// WinRT projection (the WP8.1 compiler answers CS1501 / CS1061).
const API_RULES = [
  { name: 'CryptographicBuffer.CreateFromByteArray with 3 args (WP8.1 has only the 1-arg overload)', re: /CreateFromByteArray\s*\([^)]*,[^)]*,[^)]*\)/ },
  { name: 'ContentDialog.CloseButtonText (WP8.1 has no CloseButtonText)', re: /(\.CloseButtonText\b)|(^\s*CloseButtonText\s*=)/ }
];
```

- [ ] **Step 3: Scan using both rule sets and report them**

Old:

```js
    for (const rule of RULES) {
      if (rule.re.test(lines[i])) {
```

New:

```js
    for (const rule of RULES.concat(API_RULES)) {
      if (rule.re.test(lines[i])) {
```

And the failure footer — old:

```js
if (violations > 0) {
  console.log('\n' + violations + ' C# 6/7 construct(s) found in ' + files.length + ' file(s).');
  console.log('The WP8.1 toolchain needs C# 5 syntax (see docs/superpowers/plans/2026-09-24-wp81-csharp5-port.md).');
  process.exit(1);
}
```

New:

```js
if (violations > 0) {
  console.log('\n' + violations + ' incompatible construct(s) found in ' + files.length + ' file(s).');
  console.log('The WP8.1 toolchain needs C# 5 syntax and the WP8.1 API surface');
  console.log('(see docs/superpowers/plans/2026-09-24-wp81-csharp5-port.md and');
  console.log('docs/superpowers/plans/2026-09-24-wp81-remaining-build-errors.md).');
  process.exit(1);
}
```

- [ ] **Step 4: Run the guard to verify it fails on the three API errors**

Run: `node tools/check-csharp5.js`
Expected: exit `1`, with exactly these three lines (plus the footer):

```
WhatsappApp/MainPage.xaml.cs:89: ContentDialog.CloseButtonText (WP8.1 has no CloseButtonText)  ->  CloseButtonText = "Annulla"
WhatsappApp/Services/CryptoHelper.cs:74: CryptographicBuffer.CreateFromByteArray with 3 args (WP8.1 has only the 1-arg overload)  ->  var iv = CryptographicBuffer.CreateFromByteArray(data, 0, IvLength);
WhatsappApp/Services/CryptoHelper.cs:75: CryptographicBuffer.CreateFromByteArray with 3 args (WP8.1 has only the 1-arg overload)  ->  var cipher = CryptographicBuffer.CreateFromByteArray(data, IvLength, (uint)(data.Length - IvLength));
```

- [ ] **Step 5: Commit**

```bash
git add tools/check-csharp5.js
git commit -m "test: teach the WP8.1 guard the reduced WinRT API surface"
```

---

### Task 2: `CryptoHelper.Decrypt` — slice the frame manually (errors 5, 6)

**Files:**
- Modify: `WhatsappApp/Services/CryptoHelper.cs:74-75`

**Interfaces:**
- Consumes: `CryptoHelper.Key` (SHA-256 of the passphrase), const `IvLength = 12`, `Windows.Security.Cryptography.CryptographicBuffer`, `CryptographicEngine`.
- Produces: unchanged public surface — `static byte[] Encrypt(byte[] plaintext)` and `static byte[] Decrypt(byte[] data)`; `Decrypt` still throws `ArgumentException("Payload cifrato non valido")` when `data` is null or shorter than 28 bytes, and still throws from `CryptographicEngine.Decrypt` when the tag does not verify. Input format stays `[12-byte IV][ciphertext || 16-byte GCM tag]`.

- [ ] **Step 1: Replace the two 3-argument `CreateFromByteArray` calls**

Old (lines 74-75):

```csharp
            var iv = CryptographicBuffer.CreateFromByteArray(data, 0, IvLength);
            var cipher = CryptographicBuffer.CreateFromByteArray(data, IvLength, (uint)(data.Length - IvLength));
```

New:

```csharp
            // WP8.1 exposes only CreateFromByteArray(byte[]): slice the frame
            // manually into [IV] and [ciphertext || tag] before wrapping them.
            byte[] ivBytes = new byte[(int)IvLength];
            Buffer.BlockCopy(data, 0, ivBytes, 0, (int)IvLength);

            int cipherLength = data.Length - (int)IvLength;
            byte[] cipherBytes = new byte[cipherLength];
            Buffer.BlockCopy(data, (int)IvLength, cipherBytes, 0, cipherLength);

            var iv = CryptographicBuffer.CreateFromByteArray(ivBytes);
            var cipher = CryptographicBuffer.CreateFromByteArray(cipherBytes);
```

(Behaviour is identical: `ivBytes` is exactly `data[0..12)` and `cipherBytes` exactly `data[12..data.Length)` — the same two byte ranges the 3-argument overload would have produced. `Buffer` is `System.Buffer`, already available through the existing `using System;`.)

- [ ] **Step 2: Run the guard and check the file is clean**

Run: `node tools/check-csharp5.js | grep CryptoHelper`
Expected: no output.

- [ ] **Step 3: Verify the slicing is byte-exact against the Node implementation**

Run:

```bash
cd WhatsappBridge && node -e "
const fs=require('fs');
const s=fs.readFileSync('../WhatsappApp/Services/CryptoHelper.cs','utf8');
const iv=(s.match(/BlockCopy\(data, 0, ivBytes, 0, \(int\)IvLength\)/)||[]).length;
const ct=(s.match(/BlockCopy\(data, \(int\)IvLength, cipherBytes, 0, cipherLength\)/)||[]).length;
const old=(s.match(/CreateFromByteArray\(data, /g)||[]).length;
console.log('iv slice:',iv,'cipher slice:',ct,'old 3-arg calls:',old);
"; cd ..
```

Expected: `iv slice: 1 cipher slice: 1 old 3-arg calls: 0`

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Services/CryptoHelper.cs
git commit -m "fix: build the AES-GCM buffers with the WP8.1 CreateFromByteArray overload"
```

---

### Task 3: `MainPage` "Nuova chat" dialog (error 7)

**Files:**
- Modify: `WhatsappApp/MainPage.xaml.cs:85-90`

**Interfaces:**
- Consumes: `Windows.UI.Xaml.Controls.ContentDialog` (`Title`, `Content`, `PrimaryButtonText`, `SecondaryButtonText`, `ShowAsync()`), `ContentDialogResult.Primary`, `TextBox.PlaceholderText`, `DataService.Instance.Contacts` / `AddContact(Contact)`, `ChatPage`.
- Produces: unchanged behaviour — the typed number is normalised (`+`, spaces and `-` removed), rejected when shorter than 6 digits or non-numeric, resolved to `<number>@s.whatsapp.net` (or kept as-is if it already contains `@`), and the existing contact is reused when one with that JID is already in the list.

- [ ] **Step 1: Replace `CloseButtonText` with the secondary button**

Old:

```csharp
            var dialog = new ContentDialog
            {
                Title = "Nuova chat",
                Content = input,
                PrimaryButtonText = "Apri",
                CloseButtonText = "Annulla"
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;
```

New:

```csharp
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

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;
```

(The `!= ContentDialogResult.Primary` guard already handles both `Secondary` and `None`, so no other line changes.)

- [ ] **Step 2: Run the guard and check the file is clean**

Run: `node tools/check-csharp5.js | grep MainPage.xaml.cs`
Expected: no output.

- [ ] **Step 3: Commit**

```bash
git add WhatsappApp/MainPage.xaml.cs
git commit -m "fix: use the WP8.1 ContentDialog secondary button for the new-chat dialog"
```

---

### Task 4: Full verification and handoff

**Files:**
- Modify: `README.md` (one sentence in the C# 5 section).

**Interfaces:**
- Consumes: all previous tasks.
- Produces: a repo state where the guard passes, the adapter suite still passes, and the Windows rebuild is expected to end with `0 Error(s)`.

- [ ] **Step 1: Run the guard on the whole solution**

Run: `node tools/check-csharp5.js`
Expected: `OK: 15 C# file(s) are C# 5 compatible.` (exit 0)

- [ ] **Step 2: Re-check for the two forbidden APIs by hand**

Run:

```bash
grep -rn 'CreateFromByteArray([^)]*,[^)]*,' --include=*.cs WhatsappApp WhatsappServer | grep -v obj/ || echo "no 3-arg CreateFromByteArray"
grep -rn 'CloseButtonText' --include=*.cs --include=*.xaml WhatsappApp WhatsappServer | grep -v obj/ || echo "no CloseButtonText"
```

Expected: `no 3-arg CreateFromByteArray` and `no CloseButtonText`

- [ ] **Step 3: Check all touched C# files for balanced braces**

Run:

```bash
for f in WhatsappApp/Services/CryptoHelper.cs WhatsappApp/MainPage.xaml.cs; do awk -v f="$f" '{o+=gsub(/\{/,"{"); c+=gsub(/\}/,"}")} END{print f": "o" open / "c" close"(o==c?" OK":" MISMATCH")}' "$f"; done
```

Expected: both lines end with `OK`.

- [ ] **Step 4: Run the adapter regression suite**

Run: `cd WhatsappBridge && npm test; cd ..`
Expected: `ℹ pass 29` / `ℹ fail 0`.

- [ ] **Step 5: Extend the README note**

Old (in the C# 5 section of `README.md`):

```markdown
Esce con codice 0 quando tutti i file `.cs` della soluzione sono compatibili con
C# 5, altrimenti elenca file, riga e costrutto da correggere.
```

New:

```markdown
Esce con codice 0 quando tutti i file `.cs` della soluzione sono compatibili con
C# 5, altrimenti elenca file, riga e costrutto da correggere. Lo stesso script
controlla anche i membri **assenti dalla proiezione WinRT di Windows Phone 8.1**
(es. `CryptographicBuffer.CreateFromByteArray` a 3 argomenti,
`ContentDialog.CloseButtonText`): compilano su Windows 8.1/10 ma non su WP8.1.
```

- [ ] **Step 6: Commit and push**

```bash
git add README.md
git commit -m "docs: note that the guard also checks the WP8.1 API surface"
git push origin master
```

- [ ] **Step 7: Rebuild on Windows (authoritative gate)**

```bat
msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86
```

Expected: `0 Error(s)`.

About the four `MainPage.xaml` converter errors (1-4): they are a symptom of the
failed C# compile of `MainPage.xaml.cs`. With error 7 fixed, the assembly builds
and the XAML pass resolves `using:WhatsappApp.Converters` normally. If they are
still listed right after the `Rebuild`, run **one more `Build`** without cleaning:

```bat
msbuild WhatsappApp.sln /p:Configuration=Debug /p:Platform=x86
```

Reason: for WP8.1/WinRT XAML, `MarkupCompilePass1` resolves `using:` namespaces
of the project's **own** assembly, which only exists after a successful C#
compile; the first pass after a broken build cannot see it. Only if the four
errors survive that second build should the converters themselves be
investigated (they are correct today: 4 public classes, namespace
`WhatsappApp.Converters`, declared in `WhatsappApp.csproj:95`).

---

## Self-Review

**Spec coverage:** errors 5 and 6 → Task 2 Step 1 (both call sites replaced);
error 7 → Task 3 Step 1; errors 1-4 → explained and handled by Task 4 Step 7
(no source change needed, with a fallback that needs no edit either). The
constraint "no C# 6/7 syntax" is enforced by Task 4 Step 1, and the new API
rules are themselves introduced with a failing-first cycle in Task 1 Step 4.

**Placeholder scan:** no `TBD`/`TODO`/"handle edge cases"; every code step shows
the exact old and new text; every verification step has a command and the
expected output. The only prose-only step is the Windows rebuild, which cannot
be executed from this machine by construction.

**Type consistency:** the fixed code keeps the existing signatures
(`public static byte[] Decrypt(byte[] data)`, `NewChatButton_Click(object,
RoutedEventArgs)`) and local names; `IvLength` stays `const uint 12` and is
cast explicitly wherever an `int` is required (`new byte[(int)IvLength]`,
`Buffer.BlockCopy(..., (int)IvLength, ...)`). No property or method introduced
in this plan is referenced by another task, so no cross-task name drift is
possible.
