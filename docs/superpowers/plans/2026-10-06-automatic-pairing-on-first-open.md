# Automatic pairing on first open Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A phone that has no frame key of its own offers the key it generates to the server by itself the first time it connects after the app is opened, instead of waiting for the *Pair* button.

**Architecture:** The pairing exchange moves out of `ConnectionPage` into a new `Services/PairingService.cs`, which both the page and a new automatic step use. The automatic step is one call at the top of `CommunicationService.ConnectToServerAsync`, guarded once per process and skipped when the phone already holds a key; it is `await`ed before the socket is opened, so the handshake that follows is the first frame written with the key the server just adopted.

**Tech Stack:** C# 5 on the WP8.1 toolchain, WinRT `StreamSocket`, the project's own `FrameCodec`/`CryptoHelper`, the `tools/` static guards and the ARM MSBuild gate. No adapter change: `pair.code`, `pair` and `paired` already exist.

**Spec:** the request as given, verbatim: "fai in modo che l'app appena aperta, mandi una volta la pairing key al server". Clarified by the requester as **"Pairing automatico al primo avvio"**: if the phone does not have a key of its own yet, the app pairs itself once on opening (asks for the code with `pair.code`, generates the key, sends it with `pair`), with no tap on *Pair*.

## Global Constraints

- C# 5 only: no `$"..."`, `?.`, `nameof`, pattern matching, auto-property initializers, `out var`; no `await` inside a `catch` block.
- No hardcoded user-visible strings: XAML strings use `x:Uid`, C# strings use `Loc.Get("Key", "fallback")`.
- Runtime text (Diag lines, log lines) stays English; the app UI is localized through the two `.resw` files.
- Every new `.cs` under `WhatsappApp` needs its `<Compile Include>` in `WhatsappApp.csproj`.
- A silent `catch` is a bug: every survived failure goes through `Diag.Failed("<call site>", ex)`.
- Docs are written in pairs (`README.md` + `README.it.md`, `WhatsappBridge/README.md` + `.it.md`) and `node tools/check-docs.js` must pass.
- The gate is `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node tools/check-fire-and-forget.js && node tools/check-project-files.js && node tools/check-chat-list-source.js`, then the ARM Debug build.
- After every build: `git checkout -- WhatsappApp/Package.appxmanifest`; never check out `WhatsappApp.csproj`.

## Review Focus

- **A server that is not waiting to be paired** (`PAIRING=off`, or it already has a key): the automatic step must leave the connection exactly as it was and must not delay it more than one round trip. Pinned in Task 3.
- **A phone that already has a key of its own**: the automatic step must do nothing at all - the pairing replaces the *server's* key, it does not set the phone's - or a second automatic pairing would throw away a key another server still uses. Pinned in Task 3.
- **A server that cannot be reached from the automatic step** (refused, dropped packets, timeout): the attempt must not throw into the connection path, and the manual *Pair* button must still work. Pinned in Tasks 1 and 3.
- **Running the exchange twice** (a reconnect, the watchdog, two candidates in one process): the automatic attempt is once per process, so `pair.code` is asked once and the code is not spent on a loop. Pinned in Task 3.
- **The manual flow after the move**: the page must still show the code the server sent, still report "pairing off" when the server sends none, and still save the key and the token on success. Pinned in Task 2.

---

### Task 1: `PairingService`, the exchange in one place

**Files:**
- Create: `WhatsappApp/Services/PairingService.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj` (the `Services\*.cs` group, after `ServerPinger.cs`)

**Interfaces:**
- Consumes: `CryptoHelper.SetPassphrase/NewSecret/SealWith/Encrypt`, `FrameCodec.CreateFrameWriter/CreateFrameReader/WriteFrameAsync/ReadFrameAsync`, `CommunicationService.ConnectWithDeadlineAsync(socket, hostName, port)`, `SettingsService.BridgeKey/Token/DeviceId`, `Diag.Failed/Ok`, `ChatMessage`.
- Produces:
  - `public sealed class PairingResult { public bool Paired; public bool NoCode; public bool Error; public string Code; public string Message; }`
  - `public static Task<PairingResult> PairingService.PairAsync(string address, int port, string username, string code)`
  - `public static Task<bool> PairingService.TryAutoPairAsync(string address, int port, string username)`

- [ ] **Step 1: Write the failing check**

`tools/check-project-files.js` is the RED→GREEN driver, the same way it was for `Services/ServerPinger.cs`: it fails while the new file is not in the project.

Run: `node tools/check-project-files.js`
Expected: FAIL with `WhatsappApp/Services/PairingService.cs: not in WhatsappApp.csproj as a <Compile Include>`.

- [ ] **Step 2: Add the project entry**

In `WhatsappApp/WhatsappApp.csproj`, next to the other services:

```xml
    <Compile Include="Services\PairingService.cs" />
```

- [ ] **Step 3: Implement the service**

`PairingService.PairAsync` is the exact exchange `ConnectionPage.PairWithServerAsync` performs today, with the same frames in the same order and the same two turns, and it never throws:

1. `CryptoHelper.SetPassphrase(SettingsService.BridgeKey)` - the outer frame is keyed the way this phone is configured now, which before pairing is the server's default.
2. `new StreamSocket()`, `await CommunicationService.ConnectWithDeadlineAsync(socket, new HostName(address), port)`, then `FrameCodec.CreateFrameWriter/CreateFrameReader`.
3. Up to two turns, `code` normalized first (`NormalizeCode`: uppercase, keep only `A-Z0-9`):
   - when `code` is empty, ask with a `pair.code` frame (`Id`/`Command` `pair.code`, `ChatId` `system`, `SenderId` `SettingsService.DeviceId`), read one reply with an 8 s timeout, and take `reply.PairingCode` where `reply.Command == "pair.info"`; empty means `NoCode = true`, stop;
   - `string newKey = CryptoHelper.NewSecret()`, `sealedPayload = CryptoHelper.SealWith(NormalizeCode(code), "{\"BridgeKey\":\"" + newKey + "\",\"SenderName\":\"" + JsonEscape(username) + "\"}")`;
   - send the `pair` frame (`PairingPayload = sealedPayload`, `SenderId = SettingsService.DeviceId`, `SenderName = username`, `Type = MessageType.System`, `IsIncoming = false`);
   - read one reply: `paired` sets `SettingsService.BridgeKey = newKey`, copies `reply.Token` into `SettingsService.Token` when non-empty, and sets `Paired = true`; anything else clears `code` for the next turn and keeps `reply.Text` in `Message`.
4. `Code` carries the code exactly as the server sent it (dashed form), so the page can show it.
5. Any exception is `Diag.Failed("PairingService/pair", ex)` plus `Error = true` and `Message = ex.Message`; the socket, writer and reader are closed in a `finally` (writer detach, reader detach, socket dispose, each in its own silently-ignored close, as `DisposePairingSocket` does today).

`TryAutoPairAsync` is the automatic entry point: it returns `false` immediately when it already ran once in this process (`private static bool _autoTried`) or when `SettingsService.BridgeKey` is not empty, otherwise calls `PairAsync(address, port, username, "")` and returns `result.Paired`.

- [ ] **Step 4: Verify**

Run: `node tools/check-project-files.js && node tools/check-csharp5.js && node tools/check-fire-and-forget.js`
Expected: all three OK.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Services/PairingService.cs WhatsappApp/WhatsappApp.csproj
git commit -m "feat: move the pairing exchange into PairingService"
```

---

### Task 2: the connection page uses the service

**Files:**
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml.cs` (`PairWithServerAsync`, and the private helpers it no longer needs)

**Interfaces:**
- Consumes: `PairingService.PairAsync`, `PairingResult` (Task 1).
- Produces: nothing new; behaviour of the *Pair* button unchanged.

- [ ] **Step 1: Replace the body of `PairWithServerAsync`**

The page keeps only what belongs to a page: choosing `address`/`port` (the registry walk over `EndpointService.Instance.ResolveAllAsync()` with a TCP probe for each row, unchanged), calling `PairingService.PairAsync(address, port, EnsureUsername(), PairingCodeBox.Text)`, and rendering the result:

```csharp
PairingResult result = await PairingService.PairAsync(
    address, port, EnsureUsername(), PairingCodeBox.Text);

if (result.Paired)
{
    SettingsService.Save(address, port, EnsureUsername());
    BridgeKeyBox.Text = SettingsService.BridgeKey;
    TokenBox.Text = SettingsService.Token;
    PairingCodeBox.Text = "";
    PairingStatusText.Text = Loc.Get("ConnectionPage_Paired",
        "Paired. The server now uses the key this phone generated.");
}
else if (result.NoCode)
{
    PairingCodeBox.Text = "";
    PairingStatusText.Text = Loc.Get("ConnectionPage_PairNoCode",
        "The server did not send a pairing code: either pairing is off, or it already has a key.");
}
else
{
    if (!string.IsNullOrEmpty(result.Code)) PairingCodeBox.Text = result.Code;
    string message = !string.IsNullOrEmpty(result.Message)
        ? result.Message
        : Loc.Get("ConnectionPage_PairFailed", "The pairing did not succeed.");
    PairingStatusText.Text = result.Error
        ? string.Format(Loc.Get("ConnectionPage_PairFailed", "The pairing did not succeed.")
            + " ({0})", result.Message)
        : message;
}
```

- [ ] **Step 2: Delete what moved**

Remove the now-dead private members of the page: `AskPairingCodeAsync`, `ReadPairingReplyAsync`, `NormalizePairingCode`, `DisposePairingSocket`, `JsonEscape`, and any `using` that only they used. Keep `FrameCodec`/`CryptoHelper` imports only if something else in the file still needs them.

- [ ] **Step 3: Verify**

Run: `node tools/check-csharp5.js && node tools/check-fire-and-forget.js && node tools/check-project-files.js`
Expected: OK.

- [ ] **Step 4: Build**

Run: `MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/Bin/MSBuild.exe" WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=ARM /v:m`
Expected: `Your package has been successfully created.` with 0 errors.
Then: `git checkout -- WhatsappApp/Package.appxmanifest`

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Pages/ConnectionPage.xaml.cs
git commit -m "refactor: the connection page pairs through PairingService"
```

---

### Task 3: the app pairs itself on the first connect of a run

**Files:**
- Modify: `WhatsappApp/Services/CommunicationService.cs` (top of `ConnectToServerAsync`, just before `CryptoHelper.SetPassphrase(SettingsService.BridgeKey)`)

**Interfaces:**
- Consumes: `PairingService.TryAutoPairAsync` (Task 1).
- Produces: nothing new; `ConnectToServerAsync` keeps its signature and its return value.

- [ ] **Step 1: Add the automatic step**

`ConnectToServerAsync` is the one place every connection is opened - the settings button, the discovered-server row, the single announced adapter and `AutoConnector` all end here - so the automatic pairing belongs here rather than in any one caller. Insert, before the passphrase is set (so the handshake below is written with whatever key the server just adopted):

```csharp
// "The app just opened": a phone that has no key of its own offers the one
// it generates to the server it is about to talk to, once per run. The
// server replaces its key with this one and answers `paired`, so the
// passphrase set below is the new key and the handshake is the first frame
// written with it. It never throws, it is skipped when this phone already
// has a key, and a server that is not waiting to be paired answers at once
// and leaves the connection exactly as it was.
await PairingService.TryAutoPairAsync(address, port, username);
```

- [ ] **Step 2: Verify the guards and the build**

Run: `node tools/check-csharp5.js && node tools/check-fire-and-forget.js && node tools/check-framing.js`
Expected: OK.

Run: `MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/Bin/MSBuild.exe" WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=ARM /v:m`
Expected: `Your package has been successfully created.` with 0 errors.
Then: `git checkout -- WhatsappApp/Package.appxmanifest`

- [ ] **Step 3: Commit**

```bash
git add WhatsappApp/Services/CommunicationService.cs
git commit -m "feat: the app offers its key to the server on the first open"
```

---

### Task 4: the docs say the app does it by itself

**Files:**
- Modify: `README.md`, `README.it.md` (the pairing paragraph, around "The frame cipher key does not have to be chosen by hand")
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md` (the *Pairing* / *Accoppiamento* section, "The app does not need that line")

**Interfaces:**
- Consumes: the behaviour of Tasks 1-3.
- Produces: nothing; prose only.

- [ ] **Step 1: Root READMEs**

In both, the paragraph that today reads "the connection page of the app takes that code and sends a key the phone generated" gains one sentence: with no key of its own, the app does this by itself the first time it opens, asking the server for the code; the *Pair* button on the connection page does the same thing by hand.

- [ ] **Step 2: Adapter READMEs**

In both, the paragraph that starts "The app does not need that line" gains the same sentence: the app asks for the code on its own the first time it opens with no key of its own, and the button is the manual path.

- [ ] **Step 3: Verify**

Run: `node tools/check-docs.js`
Expected: OK.

- [ ] **Step 4: Commit**

```bash
git add README.md README.it.md WhatsappBridge/README.md WhatsappBridge/README.it.md
git commit -m "docs: the app pairs itself on the first open"
```

---

### Task 5: the whole gate, and the push

- [ ] **Step 1: Run the full gate**

```bash
node tools/check-csharp5.js && node tools/check-icons.js && \
node tools/check-resw.js --strict && node tools/check-docs.js && \
node tools/check-framing.js && node tools/check-tile.js && \
node tools/check-memory.js && node tools/check-actions.js && \
node tools/check-fire-and-forget.js && node tools/check-project-files.js && \
node tools/check-chat-list-source.js
```
Expected: every guard OK, exit 0.

- [ ] **Step 2: Rebuild once on the final tree**

```bash
MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/Bin/MSBuild.exe" WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=ARM /v:m
git checkout -- WhatsappApp/Package.appxmanifest
```
Expected: `Your package has been successfully created.`, 0 errors, and a clean worktree afterwards.

- [ ] **Step 3: Push**

```bash
git push origin master
```
Expected: `origin/master` = the new `HEAD`. No adapter file changed, so the Docker repository needs no mirror commit.

---

## What execution changed about this plan

(Left empty until execution; every divergence from the tasks above is listed here.)
