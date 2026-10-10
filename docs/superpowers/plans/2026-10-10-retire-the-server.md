# Retire the server: private endpoint repos, an app with only the native client, and a QR at first launch

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** the phone app talks to WhatsApp by itself (the native protocol in
`WhatsappApp/Protocol`), the old server leaves this repository and stops being
the app's data path, the three repositories that exist only to serve that server
become private, and a phone with no session lands straight on a QR code.

**Architecture:** today the app is a thin client of an encrypted TCP protocol
spoken by `WhatsappBridge` (Node adapter) in front of GOWA; the server's three
repositories (`docker-whatsappforwp`, `whatsappforwp-endpoint`,
`go-whatsapp-web-multidevice`) publish the image and the address the phone reads.
`WhatsappApp/Protocol` now speaks that protocol natively — the node codec, the
managed crypto, the Noise handshake and the verification of WhatsApp's
certificate chain are done and verified against the real server — so the server
becomes dead weight. This plan removes it from this repository and from the app,
replaces the app's transport with the native client behind the interfaces the UI
already uses, and makes the first launch without a session a pairing screen.

**Tech Stack:** C# 5 on Windows Phone 8.1 (no `System.Security.Cryptography`, no
`System.Linq` in the app), a hand-written protocol core under
`WhatsappApp/Protocol`, plain .NET 4.5 for the desktop test host
(`ProtocolTests`, run by `node tools/test-protocol.js`), Node scripts as static
guards and as the tools that install and inspect things.

**Spec:** there is no separate spec document. The request is the operator's, in
their words: *"dopo metti privati tutti i repo che sono docker endpoint e quello
che rimane del vecchio server sul main repo toglilo e aggiorna tutta le doc,
inoltre rimuovi il server dall'app e lascia solo bailiyes, inoltre la prima volta
che si entra sull'app, e non c'è una sessione, fai in modo che compare subito il
qr code in modo da accedere"*. The design authority for the native client is
`docs/superpowers/plans/2026-10-10-native-whatsapp-client.md`; this plan owns the
pairing core it needs (its tasks 3 to 6) and the removal of the server.

## Global Constraints

- **C# 5 only**, and under `WhatsappApp/Protocol/**` no
  `System.Security.Cryptography` and no `Windows.*`: `tools/check-csharp5.js`
  fails the build on both, and it is not to be weakened to get past a line.
- **The app's project file is `WhatsappApp/WhatsApp.csproj`** (renamed outside
  this work from `WhatsappApp.csproj`), and every new source file must appear in
  it as a `<Compile Include>`, every new page as `<Page Include>` —
  `tools/check-project-files.js` is what says so.
- **Docs come in pairs** (`README.md` / `README.it.md`, and the pair listed in
  `tools/check-docs.js`'s `PAIRS`): same headings, same order, same depth, no
  emoji, `## Disclosure` last in the pairs marked `disclosure: true`.
- **Every user-visible string is a `.resw` entry in both `Strings/en-US` and
  `Strings/it-IT`**; `tools/check-resw.js` is what says so.
- **Nothing of the user's is sent over an unverified channel**: the certificate
  chain is verified before the `ClientPayload` goes out (already true today).
- **The gates are** `node tools/test-protocol.js`, `node tools/check-csharp5.js`,
  the guards under `tools/check-*.js`, their tests (`node --test
  tools/test/*.test.js`), and `MSBuild WhatsappApp.sln /t:Rebuild
  /p:Platform=x86` (and ARM) reporting `0 Error(s)`.
- **A push to another repository, or a change of a repository's visibility, is
  asked about first** — both are outside this worktree and one of them is
  publicly visible today. The executing skill's stop rule applies.
- **The historical plans under `docs/superpowers/plans/` are records, not docs**:
  they keep saying what was true when they were written and are not rewritten.
  "Update the docs" means the READMEs, the skills and this plan's own status.

## Review Focus

These are the conditions this plan's tasks do not construct on purpose, and the
behaviour a person using the app is entitled to when they happen. Each line
names the task whose tests pin it.

1. **A stored session the server has already revoked** (the phone was unlinked
   from WhatsApp, or logged out from another device): the app must land on the
   QR — with a sentence saying the session expired — and not on a chat list that
   can never load. Task 5.
2. **A QR that expires while it is on screen**: WhatsApp's pairing refs rotate
   every ~20 seconds, so the page must redraw from the new refs instead of
   leaving a dead code the phone will not scan. Task 5.
3. **Two starts while a pairing is in flight** (the user backgrounds the app
   mid-scan and it is relaunched): the credentials must be written once, whole,
   and a half-written set must never be readable as a session. Task 3.
4. **An operator following the old instructions** after the repositories go
   private: `docker pull` of the image now needs `docker login ghcr.io`, and the
   endpoint file is no longer readable anonymously. The docs must say both, in
   the same breath as the commands. Task 7.
5. **An app upgraded in place that still has a server address, a port and a
   public-server switch in its settings**: the new build must ignore them and
   start on the QR instead of crashing on a key nothing reads any more. Task 6.

---

### Task 1: The three endpoint repositories go private, and the image repository stops depending on this one

The repositories that exist to serve the old server are public today:
`docker-whatsappforwp` (the GHCR image), `whatsappforwp-endpoint` (the address
file the phone reads) and the `go-whatsapp-web-multidevice` fork (the engine the
image is built on). `Docker-BrowserForWP` is another project's image and is **not**
in scope.

**Files:**
- Modify: `.tools/docker-whatsappforwp/.github/workflows/image.yml`
- Modify: `.tools/docker-whatsappforwp/README.md`, `.tools/docker-whatsappforwp/README.it.md`
- Create: `docs/superpowers/plans/2026-10-10-retire-the-server.md` (this plan's
  "Execution notes" section, at the bottom)

**Interfaces:**
- Consumes: nothing.
- Produces: the fact that the image needs `docker login ghcr.io`, which Task 6's
  docs restate, and a `server/` directory in the image repository that is its own
  source of truth, which Task 2 assumes when it deletes the adapter here.

- [ ] **Step 1: Record the visibility that is there now**

Run: `gh repo view vincenzosco/docker-whatsappforwp --json visibility` and the
same for `vincenzosco/whatsappforwp-endpoint` and
`vincenzosco/go-whatsapp-web-multidevice`.
Expected: `{"visibility":"PUBLIC"}` three times. Write the three outputs into the
plan's Execution notes: the change is only verifiable against what it was.

- [ ] **Step 2: Change the three to private — asking first**

Ask the operator to confirm, in one message naming all three repositories and the
consequence (`docker pull` and `git clone` will need an account with access).
Then run, for each:

```bash
gh repo edit vincenzosco/docker-whatsappforwp --visibility private --accept-visibility-change-consequences
gh repo edit vincenzosco/whatsappforwp-endpoint --visibility private --accept-visibility-change-consequences
gh repo edit vincenzosco/go-whatsapp-web-multidevice --visibility private --accept-visibility-change-consequences
```

- [ ] **Step 3: Verify the visibility and the package**

Run: `gh repo view <repo> --json visibility` for each.
Expected: `{"visibility":"PRIVATE"}` three times.
Run: `gh api /user/packages/container/docker-whatsappforwp --jq .visibility`
Expected: a visibility for the GHCR package; if it prints `public`, make the
package private too (`gh api -X PATCH /user/packages/container/docker-whatsappforwp -f visibility=private`),
because a private repository with a public package is the leak this task is
about.

- [ ] **Step 4: The image repository stops reading `server/` from here**

In `.tools/docker-whatsappforwp`, edit `.github/workflows/image.yml`: delete the
whole `sync` job (it checks out `vincenzosco/WhatsappForWP` and fails the build
if `server/` has drifted). Keep `server/` and `server/SOURCE_COMMIT` in place —
they become the repository's own source, and `SOURCE_COMMIT` records where the
copy came from.

- [ ] **Step 5: The image repository's READMEs say what changed**

In `.tools/docker-whatsappforwp/README.md` and `README.it.md` (same headings,
same order — this pair is not in `tools/check-docs.js`'s `PAIRS`, so the pair
rule is a courtesy here, not a gate): replace the "server/ is a copy of the
adapter in WhatsappForWP and CI refuses to build if it drifts" paragraph with
"`server/` is this repository's own copy, recorded at `server/SOURCE_COMMIT`; the
repository it came from no longer carries the adapter", and add to the quick
start, immediately after the `git clone` line:

```bash
docker login ghcr.io          # this image is private now
```

- [ ] **Step 6: Commit and push, after asking**

```bash
git -C .tools/docker-whatsappforwp add -A
git -C .tools/docker-whatsappforwp commit -m "chore: server/ is this repository's own copy"
```

Then ask before `git -C .tools/docker-whatsappforwp push origin main` — it is a
shared branch in another repository.
Expected: the push succeeds and `gh run list --repo vincenzosco/docker-whatsappforwp --limit 1`
shows the `image` workflow running on that commit; it is green (the image builds
without the sync job). If the workflow is red, the image does not exist for the
people who were pulling it, and that is this task's failure.

---

### Task 2: The old server leaves this repository

`WhatsappBridge/` (the Node adapter), `WhatsappServer/` (the early .NET
discovery server, with no caller left in the app), `registry/` (the endpoint
registry service) and the tools that only serve them. The adapter's home is the
image repository from Task 1; this repository stops carrying a server.

**Files:**
- Delete: `WhatsappBridge/` (directory: `server.js`, `gowa-client.js`,
  `crypto-helper.js`, `endpoint-publisher.js`, `endpoint-registry.js`,
  `discovery.js`, `webhook-server.js`, `chats.js`, `messages-*`, `audio-duration.js`,
  `avatar-cache.js`, `ffmpeg.js`, `users.js`, `auth.js`, `config.js`,
  `create-user.js`, `calls.js`, `message-format.js`, `package.json`, `test/`,
  `README.md`, `README.it.md`)
- Delete: `WhatsappServer/` (whole directory)
- Delete: `registry/` (whole directory)
- Delete: `tools/start-login.js`, `tools/services.js`, `tools/qr-term.js`,
  `tools/check-framing.js`, `tools/test/check-framing.test.js`,
  `tools/check-handshake-answer.js`, `tools/test/check-handshake-answer.test.js`,
  `tools/handshake-fixtures/` (whole directory)
- Modify: `WhatsappApp.sln` (drop the `WhatsappServer` project),
  `.gitignore` (drop the `WhatsappBridge/*` entries), `tools/check-docs.js`
  (`PAIRS`), `tools/check-chat-list-source.js`, `tools/check-diagnostics.js`,
  `tools/check-memory.js`, `tools/check-fire-and-forget.js` (their comments and
  fixtures name the adapter)
- Test: the guards themselves

**Interfaces:**
- Consumes: Task 1's statement that the image repository is self-contained.
- Produces: a repository whose only subject is the app and its protocol core.

- [ ] **Step 1: Find every remaining consumer of the pieces about to be deleted**

Run: `grep -rn "WhatsappBridge\|WhatsappServer\|registry/\|start-login\|qr-term\|check-framing\|check-handshake-answer" --include="*.js" --include="*.cs" --include="*.md" --include="*.sln" --include="*.json" . | grep -v "^./docs/superpowers/plans/" | grep -v node_modules`
Expected: a list whose entries are all in the files this task edits, plus the
`.agents/skills/` files Task 6 rewrites. Anything else — a guard, a test, a
project file — is added to this task's file list before the deletions happen.

- [ ] **Step 2: `tools/download.js` — decide it by its callers, not by its name**

Run: `grep -rn "download'\|download\"\|require("./download")" --include="*.js" . | grep -v node_modules | grep -v "tools/test/"`
Expected: the callers are `tools/start-login.js` (this task deletes it) and the
adapter's ffmpeg bootstrap (this task deletes it). If those are the only two,
`git rm tools/download.js tools/test/download.test.js` and add a line to the
Execution notes naming the callers that justified it; if anything else calls it,
leave it in place and leave its four `zip`-dependent tests failing on a machine
without `zip` exactly as they do today.

- [ ] **Step 3: Delete the files**

```bash
git rm -r WhatsappBridge WhatsappServer registry tools/handshake-fixtures
git rm tools/start-login.js tools/services.js tools/qr-term.js \
       tools/check-framing.js tools/test/check-framing.test.js \
       tools/check-handshake-answer.js tools/test/check-handshake-answer.test.js
```

- [ ] **Step 4: The solution and the ignore file follow**

In `WhatsappApp.sln`, delete the `WhatsappServer` `Project` line and its
`GlobalSection(ProjectConfigurationPlatforms)` entries (keep the app's and the
solution's own sections). In `.gitignore`, delete the `# Node.js / Bridge` block
(`WhatsappBridge/node_modules/`, `.wwebjs_auth/`, `.npm/`, `package-lock.json`).

- [ ] **Step 5: The docs guard stops pairing a README that is gone**

In `tools/check-docs.js`, `PAIRS` becomes `[{ english: 'README.md', italian:
'README.it.md', disclosure: true }]`.

- [ ] **Step 6: Run every gate that could notice**

Run: `node tools/check-docs.js`, `node tools/check-project-files.js`,
`node tools/check-resw.js`, `node tools/check-chat-list-source.js`,
`node tools/check-diagnostics.js`, `node tools/check-memory.js`,
`node tools/check-fire-and-forget.js`, `node tools/check-csharp5.js`,
`node --test tools/test/*.test.js`
Expected: exit 0 for each (except the four `zip` tests Task 2 Step 2 accounted
for), and no guard left pointing at a deleted file.

Run: `MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/bin/MSBuild.exe" WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /v:minimal /nologo`
Expected: `0 Error(s)`, and the app's package is produced — the solution without
the server project still builds.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "chore: the old server leaves this repository"
```

---

### Task 3: Pairing works in the core — the payload the server expects, the credentials, and the QR refs

This is the client plan's tasks 3 to 6, owned here because the QR screen is this
plan's deliverable. Everything in it is verifiable without the phone until the
last step, and the last step is verifiable against the real server.

**Files:**
- Create: `WhatsappApp/Protocol/WaProto/DeviceProps.cs`,
  `WhatsappApp/Protocol/WaProto/ClientPayload.cs`,
  `WhatsappApp/Protocol/WaProto/AdvIdentity.cs`
- Create: `WhatsappApp/Protocol/Client/Credentials.cs`,
  `WhatsappApp/Protocol/Client/CredentialStore.cs`,
  `WhatsappApp/Protocol/Client/PairingSession.cs`
- Modify: `WhatsappApp/Protocol/Client/WhatsAppSocket.cs` (the login half),
  `WhatsappApp/WhatsApp.csproj`
- Test: `ProtocolTests/ClientPayloadTests.cs`, `ProtocolTests/CredentialsTests.cs`,
  `ProtocolTests/PairingTests.cs`, `ProtocolTests/LiveProbe.cs`

**Interfaces:**
- Consumes: `NoiseHandshake.ProcessServerHello/BuildClientFinish/FinishInit`,
  `CertificateVerifier.Verify`, `Curve25519`, `XEdDsa.Sign`.
- Produces:
  - `Credentials` with `byte[] NoisePrivateKey, NoisePublicKey, IdentityPrivateKey,
    IdentityPublicKey, AdvSecret`, `uint RegistrationId`, `SignedPreKey SignedPreKey`
    (`uint Id`, `byte[] Public`, `byte[] Signature`), `string Jid, Lid, Platform`, `string PushName`.
  - `static Credentials Credentials.Create()` — draws a fresh set and **clamps**
    the identity and noise private keys the way Signal generates them (`k[0] &= 248;
    k[31] &= 127; k[31] |= 64`), because XEdDSA signs with those bytes unreduced.
  - `interface ICredentialStorage { string Read(string key); void Write(string key, string value); }`
  - `CredentialStore` with `CredentialStore(ICredentialStorage storage)`,
    `bool HasSession`, `Credentials Load()`, `void Save(Credentials)`, `void Clear()` —
    one key per field, all values base64, and `HasSession` true only when every key
    is present and parses (Review Focus 3).
  - `static string PairingQr.ForRefs(byte[] refBytes, Credentials credentials)` →
    `https://wa.me/settings/linked_devices#<ref>,<noise pub>,<identity pub>,<adv secret>,<client type>`,
    each base64, `client type` the platform word (`UWP`).
  - `PairingSession` with `PairingSession(ITransport, Credentials)`,
    `Task<IList<string>> StartAsync()`, `Task<Credentials> WaitForPairAsync(TimeSpan)`,
    `void Close()`.

- [ ] **Step 1 (RED): the payload's tests**

Write `ProtocolTests/ClientPayloadTests.cs` asserting, by encoding and decoding
with `ProtoReader`: `passive = false`, `pull = false`, no `username`,
`devicePairingData` present with `eRegid` 4 bytes big-endian, `eKeytype` `0x05`,
`eIdent` the identity public key, `eSkeyId` 3 bytes big-endian, `eSkeyVal`,
`eSkeySig`, and a `deviceProps` blob that decodes to `platformType` UWP,
`os = "Windows Phone"`, `requireFullSync = false` and the nested history sync
config. Write the credential round trip and the `HasSession`-false-on-half-a-set
test (Review Focus 3). Write the QR string test against a fixed credentials
fixture: the exact string, character for character.

- [ ] **Step 2: Run them to watch them fail**

Run: `node tools/test-protocol.js`
Expected: the new tests fail because the types do not exist; the 53 that pass
today still pass.

- [ ] **Step 3 (GREEN): implement**

`DeviceProps` and `ClientPayload` on the existing `ProtoWriter`; the field
numbers come from the reference (`clientHello = 2`, `serverHello = 3`,
`clientFinish = 4` are already pinned; the payload's numbers are settled by
Step 6's live run, which is the only authority for them). `Credentials` and
`CredentialStore` as above. `WhatsAppSocket` gains `Task<Credentials>
PairAsync(...)`: handshake, verify the chain, send `clientFinish`, `FinishInit`,
read nodes, answer the `pair-device` acknowledgement, hand out the refs, and on
`pair-success` verify the account HMAC with the ADV secret, sign the device
identity with `XEdDsa.Sign` over `0x06 0x01 || details || identity public ||
account signature key`, reply `pair-device-sign`, and return the credentials.

- [ ] **Step 4: All offline tests pass**

Run: `node tools/test-protocol.js`
Expected: `OK: <n> protocol test(s)`, exit 0, and `node tools/check-csharp5.js`
prints `OK: <n> C# file(s) are C# 5 compatible`.

- [ ] **Step 5: The app can compile it**

Add the new files to `WhatsappApp/WhatsApp.csproj` and run the x86 rebuild.
Expected: `0 Error(s)`.

- [ ] **Step 6: The live step that decides the field numbers**

Run: `node tools/test-protocol.js --connect --pair`
Expected: `PROBE OK: the server asked for a pairing` followed by the QR payload
strings, and the run leaves the connection open long enough to scan one. A run
that ends in "the server closed the connection" without `<pair-device>` means a
payload field is wrong, and the numbers are then found by comparing this
payload byte for byte with a client that works (Baileys' `generateRegistrationNode`).

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Protocol WhatsappApp/WhatsApp.csproj ProtocolTests
git commit -m "feat: pair this device with a QR code, in the app's own protocol core"
```

---

### Task 4: The phone draws the QR itself

The QR came from the server as a PNG. With the server gone the app must encode
the string from Task 3 itself, in C# 5, with no imaging library beyond the
`WriteableBitmap` the app already uses.

**Files:**
- Create: `WhatsappApp/Protocol/Qr/QrCode.cs`
- Create: `ProtocolTests/QrCodeTests.cs`, `ProtocolTests/Vectors/QrFixture.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: nothing (a pure function of a string).
- Produces: `static class QrCode` with `static bool[,] Encode(string text,
  QrErrorCorrection level)` and `const int QuietZone = 4`; the matrix is
  `[size, size]`, `true` is a dark module; `QrErrorCorrection` is
  `Low, Medium, Quartile, High`, and the pairing payload needs byte mode, level
  `Medium`, versions 1..11.

- [ ] **Step 1: Freeze the fixture from a reference implementation**

In a temporary directory **outside this repository**: `npm i qrcode`, then a
script that prints, for each input, the version, the mask pattern and the matrix:

```js
const qrcode = require('qrcode')
const qr = qrcode.create(text, { errorCorrectionLevel: 'M' })
console.log(qr.version, qr.maskPattern)
console.log(qr.modules.data.map(r => [...r].map(v => (v ? '1' : '0')).join('')).join('\n'))
```

Write the two matrices into `ProtocolTests/Vectors/QrFixture.cs` as one string
per row of `0`/`1`, with the version and mask beside each, for the exact inputs:

- `"HELLO WORLD"`, level `M` (a short alphanumeric-shaped input), and
- the 216-character pairing payload from Task 3 Step 3's fixture, level `M`.

The file's comment names the package, its version and the command, because a
fixture nobody can regenerate is a fixture nobody can trust.

- [ ] **Step 2 (RED): the tests**

`ProtocolTests/QrCodeTests.cs`: for both fixture inputs, `QrCode.Encode` returns
a matrix equal to the fixture row for row **and** the same number of rows and
columns as the fixture (21 + 4·(version − 1)); the three finder patterns are
present at the three corners and absent at the fourth; and a 300-character input
comes back as the next version's size rather than throwing.

- [ ] **Step 3: Run them to watch them fail**

Run: `node tools/test-protocol.js`
Expected: the new tests fail (`QrCode` does not exist); the rest pass.

- [ ] **Step 4 (GREEN): implement**

Reed-Solomon over GF(256), the byte-mode segment, the version/EC tables for
1..11, the mask patterns and their penalty scoring, format and version
information, and the module placement. The fixture is the correctness check for
all of it; where the reference and this implementation could differ benignly
(which mask wins a tie) the fixture decides, and the code follows it.

- [ ] **Step 5: All offline tests pass, and the app compiles**

Run: `node tools/test-protocol.js`, then `node tools/check-csharp5.js`, then the
x86 rebuild.
Expected: exit 0 each, `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Protocol/Qr ProtocolTests WhatsappApp/WhatsApp.csproj
git commit -m "feat: the app encodes its own pairing QR code"
```

---

### Task 5: The first launch with no session is the QR, and the session is a stored account

`SettingsService.HasSavedSettings` means "a server address was configured", and
its default is the public server, so today the app goes straight to the chats and
finds a server. With the server gone the first launch means "no WhatsApp account
is stored", and it must show the QR immediately.

**Files:**
- Create: `WhatsappApp/Pages/PairPage.xaml`, `WhatsappApp/Pages/PairPage.xaml.cs`
- Create: `WhatsappApp/Services/NativeCredentialStorage.cs` (the app's
  `ICredentialStorage` over `ApplicationData.Current.LocalSettings`)
- Modify: `WhatsappApp/App.xaml.cs` (the start page decision),
  `WhatsappApp/WhatsApp.csproj`, `WhatsappApp/Strings/en-US/Resources.resw`,
  `WhatsappApp/Strings/it-IT/Resources.resw`

**Interfaces:**
- Consumes: `CredentialStore`, `Credentials`, `PairingSession`, `QrCode`,
  `WhatsAppSocket.DefaultUrl`, `ITransport` (the WinRT transport is Task 7's; until
  it exists this task uses the same `ITransport` shape and a device build is what
  proves it).
- Produces: `PairPage` with `public static void StartIfUnpaired(Frame frame)` and
  the resw entries `PairPage_Title.Text`, `PairPage_Instructions.Text`,
  `PairPage_Waiting.Text`, `PairPage_ExpiredSession.Text`,
  `PairPage_Error.Text`, `PairPage_Retry.Content` — in both languages.

- [ ] **Step 1 (RED): the start-page decision, as a testable rule**

Create `WhatsappApp/Services/StartDecision.cs` holding the rule and nothing else,
with no WinRT type and no `AppSection` in its signature, so the desktop host can
compile it:

```csharp
public enum StartTarget { Pair, Chats, LastSection }

public static class StartDecision
{
    public static StartTarget Decide(bool hasSession, bool sessionExpired, bool resumed, bool lastSectionIsChats);
}
```

The rule, one test each: no session → `Pair`; `sessionExpired` → `Pair` (which is
what shows the expired-session sentence, chosen from `sessionExpired` alone);
resumed with a session → `lastSectionIsChats ? Chats : LastSection`; cold start
with a session → `Chats`.

Add `<Compile Include="..\WhatsappApp\Services\StartDecision.cs" />` to
`ProtocolTests/ProtocolTests.csproj` **by name** (a blanket `Services/**` would
pull in the WinRT code the host cannot compile), and write
`ProtocolTests/StartupTests.cs` with one test per rule above (Review Focus 1 and
5: a session that does not parse, and a settings map left over from an install
that had a server).

- [ ] **Step 2: Run it to watch it fail**

Run: `node tools/test-protocol.js`
Expected: fail, `StartDecision` does not exist.

- [ ] **Step 3 (GREEN): the page**

`PairPage` on load: `Credentials.Create()`, open the socket, then loop on
`PairingSession.StartAsync()` — it returns the refs the server just issued — and
for each ref draw `PairingQr.ForRefs(ref, credentials)` with `QrCode.Encode` into
a `WriteableBitmap` (one module per pixel, at least 4 pixels per module, plus the
quiet zone), redrawing when the refs change (Review Focus 2). On
`WaitForPairAsync` success: `CredentialStore.Save`, then
`frame.Navigate(typeof(ChatsPage))`.

- [ ] **Step 4: The strings, in both languages**

Add the six keys above to both `.resw` files, Italian translated. Run:
`node tools/check-resw.js` — Expected: exit 0.

- [ ] **Step 5: The app starts there**

`App.xaml.cs`: replace the `SettingsService.HasSavedSettings` branch with
`StartDecision.StartPage(...)` reading `CredentialStore.HasSession`. Run the x86
rebuild. Expected: `0 Error(s)`.

- [ ] **Step 6: On the device**

Install on the Lumia with no credentials. Expected: the QR appears by itself
within a few seconds of the app opening, scanning it with WhatsApp links the
device, and the app lands on the chats. This is the step the plan is for; nothing
else proves it.

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp ProtocolTests docs
git commit -m "feat: the first launch without a session is the pairing QR code"
```

---

### Task 6: The app lives only on the native client

**Gated on the client plan's M3 and M4** (Signal and messages,
`docs/superpowers/plans/2026-10-10-native-whatsapp-client.md`): until the app can
decrypt a message, deleting the code that fetches them leaves a chat list that
cannot fill. That is the whole gate, and it is why this task is sixth and not
second.

The UI does not change. What changes is what is behind it: the eight services
that speak the adapter's protocol leave, and one service that speaks WhatsApp's
replaces them.

**Files:**
- Create: `WhatsappApp/Services/NativeSession.cs`
- Delete: `WhatsappApp/Services/CommunicationService.cs`, `FrameCodec.cs`,
  `CryptoHelper.cs`, `EndpointService.cs`, `DiscoveryService.cs`,
  `ServerPinger.cs`, `AutoConnector.cs`, `ConnectionWatchdog.cs`
- Modify: `WhatsappApp/Services/DataService.cs`, `SettingsService.cs`,
  `WhatsappApp/App.xaml.cs`, `WhatsappApp/Pages/ChatsPage.xaml(.cs)`,
  `WhatsappApp/WhatsApp.csproj`, both `Strings/*/Resources.resw`
- Rename: `WhatsappApp/Pages/ConnectionPage.xaml(.cs)` → `SettingsPage.xaml(.cs)`
- Test: `tools/check-fire-and-forget.js`, `tools/check-diagnostics.js`,
  `tools/check-chat-list-source.js`, `tools/check-memory.js` (their fixtures and
  comments name the deleted services), and the guard from Task 7

**Interfaces:**
- Consumes: `WhatsAppSocket`, `CredentialStore`, `Credentials`, `PairingSession`
  (Task 3), `QrCode` (Task 4), `StartDecision` (Task 5), and the client plan's
  session/message API once M3 and M4 land.
- Produces: `WhatsappApp.Services.NativeSession`, exposing **exactly** the surface
  `DataService` and the pages already call on `CommunicationService` — no more,
  no less, because the pages are not edited for it.

- [ ] **Step 1: Freeze the surface before touching anything**

Run:
`grep -rn 'CommunicationService\.' --include='*.cs' WhatsappApp | sed 's/.*CommunicationService\.//' | sort -u`
and write the list into the plan's Execution notes under `Surface:`. That list is
this task's contract: `NativeSession` gets one member per entry, with the same
name, the same parameters and the same return type. Anything the list does not
contain is not implemented here.

- [ ] **Step 2: Ruling to record — the settings page keeps its name or does not**

The page the gear opens is the server settings page, and its name says so.
Ruling: rename it to `SettingsPage` and keep only the rows that are still about
this phone (notifications, help, version), dropping the address, port, username,
public-server switch and the server-drawn QR rows, and rename the resw keys
`ConnectionPage_*` → `SettingsPage_*` in both languages for the rows that stay.
Cost if wrong: one page rename and a resw rename that `tools/check-resw.js`
catches immediately.

- [ ] **Step 3 (RED): the guard that already exists must fail**

Delete the eight services with `git rm`, leaving `DataService` calling them.
Run: `MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/bin/MSBuild.exe" WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /v:minimal /nologo`
Expected: it does not compile — the list from Step 1 is exactly the set of errors
it reports. That is the check that the list was complete.

- [ ] **Step 4 (GREEN): `NativeSession`**

One member per entry from Step 1, implemented on `WhatsAppSocket` plus the
credential store: connect and handshake with the stored credentials, surface
incoming nodes as the events the pages already subscribe to, and encode outgoing
messages as the nodes the adapter used to build. `DataService` is not rewritten:
it keeps parsing what it parsed, now from WhatsApp's own payloads.

- [ ] **Step 5: The app builds and the guards agree**

Run the x86 (and ARM) rebuild, then every `tools/check-*.js` and
`node --test tools/test/*.test.js`, then `node tools/test-protocol.js`.
Expected: `0 Error(s)`, exit 0 everywhere, and no guard left naming a file that
is gone.

- [ ] **Step 6: On the device**

Install over the version that talked to the server, **without clearing its
settings** — the leftover address, port and switch keys must be ignored (Review
Focus 5). Expected: the app opens on the chats if it has a session and on the QR
if it does not, sends a text and receives one, all with no server running
anywhere. This is the step the whole plan is for.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m 'feat: the app talks to WhatsApp itself, and the server path is gone'
```

---

### Task 7: The docs, and a guard that keeps the server from coming back

**Files:**
- Modify: `README.md`, `README.it.md` (same headings, same order, `## Disclosure`
  last in both)
- Modify: `.agents/skills/maintain-the-app/SKILL.md`,
  `.agents/skills/test-the-app/SKILL.md`, `.agents/skills/update-the-app/SKILL.md`,
  `.agents/skills/release-the-app/SKILL.md`, `.agents/skills/agent-*/SKILL.md`
- Delete: `.agents/skills/run-the-login-server/SKILL.md`
- Create: `tools/check-no-server.js`, `tools/test/check-no-server.test.js`

**Interfaces:**
- Consumes: Task 1's `docker login ghcr.io` requirement and the three private
  repositories; Task 2's empty `WhatsappBridge/`.
- Produces: `node tools/check-no-server.js` → exit 0 with `OK: the repository has
  no server` when none of the server's files, project entries or strings are
  present, and a list of what it found when they are.

- [ ] **Step 1 (RED): the guard's own test**

`tools/test/check-no-server.test.js` (the same shape as
`tools/test/check-project-files.test.js`): given a fixture tree containing
`WhatsappBridge/server.js`, the guard reports it; given a fixture tree containing
only the app and the protocol core, it reports nothing. Run:
`node --test tools/test/check-no-server.test.js` — Expected: fail, the module
does not exist.

- [ ] **Step 2 (GREEN): the guard**

It fails on: a `WhatsappBridge/`, `WhatsappServer/` or `registry/` directory; a
`<Compile Include>` naming `CommunicationService.cs`, `FrameCodec.cs`,
`CryptoHelper.cs`, `EndpointService.cs`, `DiscoveryService.cs`, `AutoConnector.cs`,
`ServerPinger.cs`, `ConnectionWatchdog.cs`; a `SettingsService` key starting with
`Server.`; and a mention of the adapter in `README.md`/`README.it.md` or in
`.agents/skills/**`. Run the test again — Expected: pass.

- [ ] **Step 3: The README pair**

Rewrite the architecture and "how to run" sections of `README.md` and
`README.it.md` into the same shape: what the app is, that it speaks WhatsApp's
own protocol from the phone (no server, no account on anyone else's machine), how
to build it, how to install it, that the first launch shows a QR, and — for the
people who used the old service — that the image now lives in a **private**
repository and needs `docker login ghcr.io`, that the address file is private
too, and that the app no longer reads either (Review Focus 4). Run:
`node tools/check-docs.js` — Expected: exit 0.

- [ ] **Step 4: The skills**

`maintain-the-app`: the architecture section says the app has no server, names
`WhatsappApp/Protocol` as the place the protocol lives, keeps the C# 5 and resw
rules, and drops every adapter instruction. `test-the-app`: the gate list loses
the adapter suite and gains `node tools/check-no-server.js` and
`node tools/test-protocol.js`. `update-the-app` and `release-the-app`: same
surgical pass. `run-the-login-server` is deleted — there is no login server — and
any skill that references it loses the reference; if the operator still wants to
run the old server for comparison, it is
`git clone` of the private image repository and its own README, which is what the
skill says in one paragraph if it is kept at all.

- [ ] **Step 5: Run the whole gate set**

Run: `node tools/test-protocol.js`, `node tools/check-csharp5.js`, every
`tools/check-*.js` including the new one, `node --test tools/test/*.test.js`, and
the x86 rebuild.
Expected: exit 0 each; the app builds; the guard's name is listed where the
README lists the guards.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "docs: the app has no server, and a guard keeps it that way"
```

---

## Ordering and the one gate

Tasks 1, 2, 3, 4, 5 and 7 can run in this order, starting today: the repositories,
the removal of the server from this tree, the pairing core, the QR encoder, the
pairing page, and the documentation that has to be true before the code is.

**Task 6 alone is gated on the client plan's M3 and M4** (Signal and messages,
`docs/superpowers/plans/2026-10-10-native-whatsapp-client.md`), because until the
app can decrypt a message, deleting the code that fetches them leaves a chat list
that cannot fill. Task 7 may run before Task 6, but its README may not claim the
app has no server until Task 6 is done; that claim is the last thing Task 7
writes, and Task 7's own guard is what proves it.

## Execution notes

(filled in as the tasks run: the three visibilities as they were, the `download.js`
decision, the pinning of the payload's field numbers by the live run, and every
ruling made, in the form `Ruling: <what> — <why> — <cost if wrong>`)

### The three visibilities as they were (Task 1 Step 1)

`gh repo view <repo> --json visibility`, 2026-10-10, before anything changed:

```
docker-whatsappforwp: {"visibility":"PUBLIC"}
whatsappforwp-endpoint: {"visibility":"PUBLIC"}
go-whatsapp-web-multidevice: {"visibility":"PUBLIC"}
```

After the change all three answer `{"visibility":"PRIVATE"}`, and the two checks
that do not depend on this machine's token agree: `endpoint.json` on
`raw.githubusercontent.com` and the fork's API entry both answer **404**
anonymously.

### The `download.js` decision (Task 2 Step 2)

Deleted, with its test. Its only caller left in the tree was
`tools/start-login.js`, which this task deletes; nothing under `WhatsappBridge/`
required it (the ffmpeg bootstrap the plan expected does not call it). The four
`zip ENOENT` failures that lived in `tools/test/download.test.js` on a machine
without `zip` went with it.

### The payload's field numbers, arbitrated by the server (Task 3 Step 6)

`node tools/test-protocol.js --connect --pair` printed `PROBE OK: the server asked
for a pairing (6 code(s))` followed by six QR strings. The numbers the server
accepted: `passive` 3, `pull` 33, `userAgent` 5, `webInfo` 6, `connectType` 12,
`connectReason` 13, `devicePairingData` 19, and inside it `eRegid` 1 (four bytes),
`eKeytype` 2 (`0x05`), `eIdent` 3, `eSkeyId` 4 (three bytes), `eSkeyVal` 5,
`eSkeySig` 6, `deviceProps` 8. `DeviceProps`: `os` 1, `version` 2,
`platformType` 3, `requireFullSync` 4, `historySyncConfig` 5, with `UWP` = 21.

`buildHash` (7) is the one field Baileys sends and this client does not; the
server accepted the payload without it.

### Rulings

- **The announced version is WhatsApp Web's, not this app's.** `Ruling:` the
  `UserAgent` version is `2.3000.1049558099` — the first live pairing run came
  back `<failure reason="405" location="frc">`, and 405 is
  `ConnectFailureClientOutdated` in the reference: the server refuses a companion
  older than the floor it supports. Why: the version is a gate, not a label, and
  the app's own 1.0.0 does not pass it. The app's real version lives in
  `DeviceProps`. Cost if wrong: the pairing stops working when WhatsApp raises
  its floor, and the failure is that same bare 405 — so the constant carries a
  comment that says so.
- **`ClientPayload.pull` is field 33, so the protobuf writer had to grow.**
  `Ruling:` `ProtoWriter` accepts field numbers up to 2^29 - 1 instead of 1..15.
  Why: 15 was a limit that held only until the first message past the handshake
  was written. Cost if wrong: none; the tag varint is what enforces it.
- **A handshake that has begun is not a channel that has split.** `Ruling:`
  `WhatsAppSocket` keeps `_noise` and `_channelEncrypted` as two states. Why:
  the first live pairing run decrypted the server's handshake frame as if it were
  a transport frame and died on "the authentication tag does not match". Cost if
  wrong: none, and the bug is pinned by the live run.
- **A pairing that ends without an account is not an error.** `Ruling:`
  `WaitForPairAsync` returns null when the connection ends first. Why: a transport
  out of patience and a server that drops a connection whose codes nobody scanned
  mean the same thing to the caller — show the expiry and start a new session.
  Cost if wrong: a malformed `pair-success` is still an exception, because only
  the read is guarded.
- **`Credentials.Create` takes an `IRandomSource` rather than drawing its own.**
  `Ruling:` the interface replaces the plan's parameterless `Create()`. Why: the
  core may not use `System.Security.Cryptography` (the WP8.1 projection has none)
  and `CryptographicBuffer` is a `Windows.*` type the desktop host cannot
  compile, so randomness is injected — which is also what makes the drawing
  testable. Cost if wrong: one more parameter at the call site.
- **The store keeps `SignedPreKeyPrivate` too.** `Ruling:` one more key than the
  plan's interface list. Why: an incoming session is completed with the signed
  pre-key's private half, so a session that survived a restart without it would
  fail on the first message. Cost if wrong: 44 bytes of storage.
- **`PairingSession.StartAsync` returns the codes, `WaitForPairAsync` the
  account.** `Ruling:` the plan's single `WhatsAppSocket.PairAsync` is split in
  two. Why: the page has to show a code for the half minute a person needs to
  reach for their phone, and one method cannot return both. Cost if wrong: one
  extra type.
- **The QR fixture's short input is not `"HELLO WORLD"`.** `Ruling:` it is
  `"Hello, WhatsApp!"`. Why: the reference encodes `"HELLO WORLD"` in
  alphanumeric mode, which this encoder does not implement — and the pairing
  payload is mixed-case, so byte mode is the mode that matters. Cost if wrong:
  an all-uppercase input produces a larger symbol that still scans.
- **The encoder carries versions 1..40, not 1..11.** `Ruling:` all forty. Why:
  the real pairing payload is **277 characters**, which needs version 12 at level
  Medium — the plan's 216-character estimate was low and its ceiling was under
  the real payload. Cost if wrong: a larger table file.
- **The mask tables come from the reference, not from typing.** `Ruling:`
  `QrTables.cs` and `QrFixture.cs` are printed by a script in a temporary
  directory outside this repository, and the command that regenerates them is in
  the file's header. Why: a hand-typed error correction table produces a symbol
  that decodes to the right length and the wrong data. Cost if wrong: none, the
  script is reproducible.
- **`WhatsappSocketTests`'s first-node fixture gained a three-byte length.**
  `Ruling:` the fixture is framed. Why: the socket now reads the wire, the wire is
  framed, and the old fixture was a bare node no server ever sends — the check is
  unchanged, the input was wrong. Cost if wrong: none.

### Deviations from the plan's file lists

- `tools/check-csharp5.js` (`SCAN_ROOTS` dropped `WhatsappServer`) and
  `tools/test/services.test.js` were added to Task 2: Step 1's grep found them,
  and the plan says anything else that names a deleted file joins the list.
- Task 2's commit initially swept in five unrelated files that were already
  modified in the worktree (`README.md`, `README.it.md`, `ConversationView.cs`,
  `ChatPage.xaml.cs`, `.agents/skills/test-the-app/SKILL.md`). It was soft-reset
  and re-made with only this task's files; those five are still uncommitted.

### Open items, at the time of writing

- **The GHCR package is still publicly pullable.** The repositories are private,
  but an anonymous `ghcr.io/token` still returns a pull token and
  `docker-whatsappforwp:latest` still answers 200 — a private repository with a
  public package, which is the leak Task 1 is about. The token on this machine
  (`gist, read:org, repo, workflow`) has no packages scope, so this is the
  operator's to do: the package's settings page, or
  `gh auth refresh -s read:packages,write:packages`.
- **The push to `docker-whatsappforwp` has not happened.** The commit is local
  (`6891bce`) because it is a shared branch in another repository.
- **Task 5 is not started, and it has a prerequisite the plan did not name:**
  `WhatsappApp/Protocol/Transport/WebSocketTransport.cs`, the WinRT transport,
  does not exist — `Protocol/Transport/` holds `ITransport.cs` alone, so nothing
  on the phone can open the connection yet. The plan's Task 5 assumed it was
  "Task 7's" and left it out; it is the next real piece of work, and it comes
  before the page is worth writing.
- **Task 6 stays gated** on the client plan's M3 and M4, and Task 7's README
  claim waits for it.
