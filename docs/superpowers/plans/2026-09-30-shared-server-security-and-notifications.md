# Shared server, secure transport and background notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** turn the app plus adapter from one self-hosted instance into a shared public service with per-user isolation, encrypted storage and transport, an auto-discovered public address that follows a bore.pub tunnel, a clear "server not available" message, and best-effort notifications while the app is closed.

**Architecture:** GOWA already speaks multi-device (`X-Device-Id`, `GET/POST /devices`), so one container can host many users if every user gets a device of their own. The adapter becomes the gate: it authenticates a per-user token, resolves it to that user's GOWA device, and scopes every command and every webhook delivery to it. Transport keeps its app-level cipher and gains TLS underneath, with the certificate fingerprint pinned on the phone. At rest, tokens are stored only as scrypt hashes and the WhatsApp session volume is encrypted by the host. The public address of the instance is not compiled into the app: a second, tiny repository holds `endpoint.json`, which a tunnel script rewrites whenever bore gives the container a new port, and the app reads it at startup. A background timer task wakes the app occasionally to fetch new messages and raise a toast; true push (WNS) is gone, and the plan says so instead of pretending otherwise.

**Tech Stack:** Windows Phone 8.1 WinRT/XAML, C# 5; Node.js 18+ (CommonJS, `node:test`, `tls`, `crypto`); Docker; GOWA v9.5.0; a separate `whatsappforwp-endpoint` repository.

## Global Constraints

- **C# 5 only** (`node tools/check-csharp5.js`): no `$"..."`, no `nameof`, no expression-bodied members, no `?.` on the left of an assignment.
- Colours, fonts: the WP8.1 theme only. No `Segoe MDL2 Assets`; an icon is an inline `Path` with an `<!-- IconX -->` comment.
- User-visible strings live in **both** `WhatsappApp/Strings/en-US/Resources.resw` and `WhatsappApp/Strings/it-IT/Resources.resw` (141 keys today, `--strict` fails on an unused one).
- Source comments are **Italian, no accented characters**; user strings carry accents normally.
- **Fast gate after every task:**
  `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"`, then `cd WhatsappBridge && npm test`.
- **Build gate after every task that touches the app**, on the Parallels VM `Windows 11` (retry the same MSBuild call once on exit 255 + `PrlJob_GetRetCode: Invalid argument`): rmdir `C:\Temp\wp81`, robocopy the repo minus `obj bin AppPackages BundleArtifacts node_modules .tools .git`, then `MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m /p:WarningLevel=4` → `Avvisi: 0`, `Errori: 0`, `Your package has been successfully created.`
- Every file is **LF, no BOM**. After any edit run:
  `perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' <file>`
- **Every change under `WhatsappBridge/` also lands in the Docker repository** at `/tmp/docker-whatsappforwp`: `node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP`, then `node tools/sync.js --check --from /Users/vincenzo/Documents/WhatsappForWP`, then `cd server && npm test`, then commit and push to `main`.
- Commit messages contain **no apostrophe**.
- Plans and docs carry **no emoji**, except U+26A0.
- A `Border` takes exactly one child (WMC0035): wrap two in a `<Grid>`.
- A `<Button>` named `X` is wired only to `X_Click`; a button that declares `Width` also declares `MinWidth="0" MinHeight="0"`.
- **Current expected counts:** 40 C# files, 141 keys per `.resw`, 24 inline `Path` (14 distinct icons), 21 buttons + 1 Button style, 58 tests in `tools/test`, 153 adapter tests.

## Hard constraint, stated up front

WP8.1 has no working push channel any more: raw push needs WNS, which Microsoft retired. The only way an app that is not running can be woken is a **background task** (`windows.backgroundTasks` / `timer`), which the OS runs at its own discretion - in practice no more often than every 30 minutes, and only when battery and thermals allow. This plan implements that, and calls it what it is: **best-effort**. Notifications are immediate while the app is alive; while it is closed they are whatever the OS grants.

---

## File Structure

| File | Create/Modify | Responsibility |
| --- | --- | --- |
| `WhatsappBridge/users.js` | **Create** | Per-user records: token hash (scrypt), GOWA device id, created/last-seen. |
| `WhatsappBridge/auth.js` | **Create** | Verify a token against `users.js`, resolve it to a user, mint new tokens. |
| `WhatsappBridge/server.js` | Modify | Require a token on `hello`; scope every command to the caller's device; route webhooks per device; TLS listener. |
| `WhatsappBridge/gowa-client.js` | Modify | A client bound to one device id, plus `createDevice`/`listDevices`. |
| `WhatsappBridge/test/users.test.js`, `test/auth.test.js`, `test/server.test.js` | Modify | The token and isolation tests. |
| `WhatsappBridge/config.js`, `.env.example` | Modify | `AUTH_REQUIRED`, `TLS_*`, `USERS_FILE`. |
| `WhatsappBridge/tls.js` | **Create** | Load the certificate and key, fingerprint the certificate, build the TLS options. |
| `docker/entrypoint.sh`, `Dockerfile`, `docker-compose.yaml` | Modify | Encrypted volume guidance, cert mount, no plaintext fallback in production. |
| `tools/backup.js` | **Create** | Export/restore the session volume for a server migration. |
| A new repo `whatsappforwp-endpoint` | **Create** | `endpoint.json`, `README.md`, `publish.js`, a GitHub Action. |
| `WhatsappApp/Services/EndpointService.cs` | **Create** | Fetch the public address, cache it, fall back to the saved one. |
| `WhatsappApp/Services/SettingsService.cs` | Modify | `Token`, `UsePublicServer`, `EndpointUrl`. |
| `WhatsappApp/Services/CommunicationService.cs` | Modify | Send the token in `hello`; upgrade the socket to TLS; pin the fingerprint; raise the offline message after the retries. |
| `WhatsappApp/Services/BackgroundNotifier.cs` | **Create** | The timer background task: fetch, toast, badge. |
| `WhatsappApp/Package.appxmanifest` | Modify | `BackgroundTasks`/timer declaration. |
| `WhatsappApp/Pages/ConnectionPage.xaml(.cs)` | Modify | Public/own switch, token field, the offline message. |
| `WhatsappApp/Strings/{en-US,it-IT}/Resources.resw` | Modify | The new strings, in both files. |
| `README.md` / `README.it.md`, `.agents/skills/*` | Modify | The new architecture, counts, on-device checks. |

---

## Phase A: the adapter is a gate, not an open door

### Task A1: users and tokens

**Files:**
- Create: `WhatsappBridge/users.js`, `WhatsappBridge/auth.js`
- Test: `WhatsappBridge/test/users.test.js`

**Interfaces:**
- Produces:
  - `createUserStore({ file, scryptSync, randomBytes, now })` → `{ register(name), verify(token), get(token), count(), load(), save() }`
  - a record is `{ id, name, tokenHash, salt, deviceId, createdAt, lastSeenAt }`
  - `register(name)` → `{ token, user }` (the plain token is returned **once** and never stored)
  - `verify(token)` → the user record or `null` (constant-time compare)
  - `auth.parseToken(headers)` → the bearer token or `''`

- [ ] **Step 1: Write the failing tests** (scrypt is injected so tests are fast), covering: a new token verifies, a wrong token does not, the file never contains the plain token, `lastSeenAt` is updated, the file survives a reload.

- [ ] **Step 2: Run them, fail.**

- [ ] **Step 3: Implement `users.js`**

Key code:

```javascript
'use strict';

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

// Un utente del servizio condiviso: un token che l'app tiene, e il device GOWA
// che gli appartiene. Il token non si salva mai: si salva il suo hash scrypt,
// perche' un file di token in chiaro e' un file di chiavi.
function hashToken(token, salt, scryptSync) {
  return scryptSync(token, salt, 64).toString('hex');
}

function createUserStore(options) {
  const opts = options || {};
  const file = opts.file;
  const scryptSync = opts.scryptSync || crypto.scryptSync;
  const randomBytes = opts.randomBytes || crypto.randomBytes;
  const now = opts.now || Date.now;
  const timingSafeEqual = opts.timingSafeEqual || crypto.timingSafeEqual;

  let users = [];

  function load() {
    try {
      const raw = JSON.parse(fs.readFileSync(file, 'utf8'));
      users = Array.isArray(raw.users) ? raw.users : [];
    } catch (err) {
      if (err && err.code !== 'ENOENT') throw err;
      users = [];
    }
    return users;
  }

  function save() {
    if (!file) return;
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, JSON.stringify({ users }, null, 2));
  }

  function register(name) {
    const token = randomBytes(32).toString('base64url');
    const salt = randomBytes(16).toString('hex');
    const user = {
      id: randomBytes(8).toString('hex'),
      name: String(name || ''),
      salt,
      tokenHash: hashToken(token, salt, scryptSync),
      deviceId: '',
      createdAt: now(),
      lastSeenAt: now()
    };
    users.push(user);
    save();
    return { token, user };
  }

  function verify(token) {
    if (!token || !users.length) return null;
    for (const user of users) {
      const candidate = Buffer.from(hashToken(token, user.salt, scryptSync), 'hex');
      const stored = Buffer.from(user.tokenHash, 'hex');
      if (candidate.length === stored.length && timingSafeEqual(candidate, stored)) {
        user.lastSeenAt = now();
        return user;
      }
    }
    return null;
  }

  load();
  return { register, verify, count: () => users.length, load, save,
    users: () => users };
}

module.exports = { createUserStore, hashToken };
```

- [ ] **Step 4: `auth.js`**, which only reads the token out of the handshake frame:

```javascript
'use strict';

// Il token viaggia nel frame `hello`, cifrato come tutto il resto: l'header
// HTTP non esiste su questo protocollo. Un campo dedicato e' piu' chiaro del
// riuso di Text, che per `hello` porta il nome utente.
function parseToken(frame) {
  const value = frame && frame.Token;
  return typeof value === 'string' ? value.trim() : '';
}

module.exports = { parseToken };
```

- [ ] **Step 5: tests pass; commit.**

### Task A2: `hello` must carry a valid token

**Files:** Modify `WhatsappBridge/server.js`; `WhatsappApp/Models/ChatMessage.cs` gains `[DataMember] Token`; `WhatsappApp/Services/CommunicationService.cs` sends it.

**Interfaces:** `hello` whose `Token` does not verify is answered with `command: "unauthorized"` and the socket is closed; `AUTH_REQUIRED=off` keeps the old behaviour for a private instance.

- [ ] **Step 1-5:** test first (`server.test.js`: an unauthenticated `hello` gets `unauthorized` and no `state`), then implement, then the two app changes, then commit.

### Task A3: the fingerprint is pinned and TLS is on

**Files:** Create `WhatsappBridge/tls.js`; modify `server.js` (a `tls.createServer` when `TLS_CERT`/`TLS_KEY` are set), `config.js`, `.env.example`; `CommunicationService.cs` (`UpgradeToSslAsync`), `SettingsService.cs` (`ServerFingerprint`), `ConnectionPage`.

- The certificate's SHA-256 is shown in the app's settings after the first connection; a mismatch is refused with a message, never silently downgraded.

- [ ] **Steps:** test `tls.options()` with a generated self-signed pair (openssl in the test), implement, then the app upgrade path and the pin, then commit.

---

## Phase B: one container, many users

### Task B1: per-user device

**Files:** Modify `gowa-client.js` (`withDevice(id)`, `createDevice(label)`, `listDevices()`), `server.js` (create the device on first successful `hello`, remember it on the user record).

- [ ] A user's first connection creates a GOWA device named after the user id and stores it; every later command builds a `GowaClient` bound to that device id. Test: two tokens get two different `X-Device-Id` headers.

### Task B2: webhook routing

**Files:** Modify `server.js` (`handleWebhookEvent` maps the payload's device to a user and sends only to that user's sockets).

- [ ] Test: a message for device A never reaches the socket authenticated as user B.

---

## Phase C: data at rest

### Task C1: hashed tokens, nothing else
Covered by A1; add a guard (`tools/check-...`? no) and a test that `users.json` contains no 40+ char base64url string equal to a returned token.

### Task C2: the volume is encrypted
**Files:** Modify `docker-compose.yaml`, `README.md`/`README.it.md`.

- [ ] A `docker-compose.secure.yaml` that mounts `/data` from a host path documented as LUKS-encrypted, plus the exact host commands. No application change: GOWA cannot encrypt its own session, so full-volume encryption is the honest answer, stated as such.

### Task C3: backup and restore for a migration

**Files:** Create `tools/backup.js`; modify the READMEs.

- [ ] `node tools/backup.js export ./session.tar` tars `/data/storages` (the WhatsApp session) and `users.json`; `restore` unpacks it. A restored volume reuses the link: the device does not scan the QR again. Documented as the migration procedure and verified on the VM by restoring into a second container.

---

## Phase D: the public address follows the tunnel

### Task D1: the endpoint repository

**Files:** Create the repository `whatsappforwp-endpoint` (public): `endpoint.json`, `endpoint.md`, `README.md`, `publish.js`, `.github/workflows/publish.yml`.

- [ ] `endpoint.json` is `{ "updatedAt": "...", "host": "bore.pub", "port": 12345, "tls": false, "fingerprint": "" }`. `publish.js` takes the bore output and rewrites the file, commits and pushes. The Action runs on `workflow_dispatch` and on a schedule, recording the current tunnel. The app parses the JSON.

### Task D2: the app reads it

**Files:** Create `WhatsappApp/Services/EndpointService.cs`; modify `AutoConnector`, `SettingsService` (`UsePublicServer`, `EndpointUrl`), `ConnectionPage`, both `.resw`.

- [ ] At startup, when `UsePublicServer` is on, `EndpointService` fetches the JSON over HTTPS, caches it in `LocalFolder`, and uses the newest of cache/remote. If the fetch fails, the cached address is used, and if there is none, the saved address. Tests are Node-side for the JSON parse and on-device for the flow.

---

## Phase E: the message the user sees

### Task E1: "Server non disponibile, riprova tra qualche minuto :)"

**Files:** Modify `AutoConnector` (raise a flag after the retries), `CommunicationService` (a new event), `ChatsPage`/`ConnectionPage`, both `.resw`.

- [ ] `CommService_ServerUnavailable` in both files; shown after the automatic reconnect has tried and failed, not on every socket error. The trailing `:)` is part of both strings, verbatim.

---

## Phase F: notifications while the app is closed

**Structural constraint discovered while planning execution (2026-09-30).** A WP8.1 WinRT background task cannot live in the app's own executable: the manifest `EntryPoint` must name a class in a referenced **Windows Runtime Component**. So Phase F needs a new project (`WhatsappApp.Tasks`), added to `WhatsappApp.sln`, referenced by the app, with the task settings capability. This is why F1 and F2 are not folded into the app project, and why they must land together: a manifest that names an entry point which does not exist makes the package fail to validate.

**What is already true, and is not a code change.** A toast the app raises while it is alive already appears on the lock screen, provided the user lists the app under Settings > lock screen. No manifest attribute forces that, and none should: it is the user's choice. What is missing is only the case where the app is not running.

### Task F1: the task component and the manifest declaration

**Files:** Create `WhatsappApp.Tasks/WhatsappApp.Tasks.csproj` (Windows Runtime Component) and `WhatsappApp.Tasks/BackgroundNotifier.cs`; modify `WhatsappApp.sln`, `WhatsappApp/WhatsappApp.csproj` (a `ProjectReference`), `WhatsappApp/Package.appxmanifest`.

- [ ] Add `<Extension Category="windows.backgroundTasks" EntryPoint="WhatsappApp.Tasks.BackgroundNotifier">` with `<Task Type="timer"/>`, plus the app's `TimerTrigger` registration and `BackgroundExecutionManager.RequestAccessAsync()`. `check-icons`/`check-tile` must stay green, and the VM build must produce the package with the new project.

### Task F2: the notifier's body

**Files:** Modify `WhatsappApp.Tasks/BackgroundNotifier.cs`, `WhatsappApp/Services/BackgroundNotifierRegistration.cs` (create), both `.resw`.

- [ ] The task opens its own socket (it must not depend on `CommunicationService`, whose dispatcher does not exist in a background process), sends `hello` with the token, asks for `chats`, compares the unread counts with a snapshot in `LocalSettings`, raises one toast per increase, updates the badge, and returns. Bounded: one attempt, a hard timeout, the same `MaxFrameLength` as the foreground reader.

---

## Phase G: the plan's own honesty

### Task G1: document the limits

**Files:** Modify `README.md`/`README.it.md`, both skills.

- [ ] State: notifications while the app is closed are best-effort and OS-scheduled; there is no push; a shared server means the operator can technically reach the sessions, and the token is what separates users; the endpoint repository is public and holds only an address.

---

## Self-Review

**Coverage.** Public shared server -> A1-A3, B1-B2; "use your own" -> D2 (the switch and the saved address); at-rest encryption -> C1-C2; transport encryption -> A3 (plus the cipher that already exists); offline message -> E1; migration without re-linking -> C3; tunnel `.md` repository -> D1-D2; lock-screen/closed-app notifications -> F1-F2, with the WNS limit stated.

**Risk, stated.** A1-A3, B1-B2 and C3 rewrite the adapter's trust model: they must land together, because an adapter that authenticates but does not scope to a device is worse than one that does neither. The remaining phases are independent.

**Type consistency.** `Token` is the wire name in `ChatMessage` and in `buildChatMessage`; `users.js` returns `{ token, user }` and `auth.js` returns a string; `EndpointService` returns `{ host, port, tls, fingerprint }`.

## What execution changed about this plan

Landed in this push:

- **Phase E**: the offline message. `CommunicationService.ServerUnavailable` (marshalled
  on the UI thread by `NotifyServerUnavailable`), raised by `AutoConnector` when the saved
  address and the discovery both come up empty, and shown by `ChatsPage` as a strip that
  stays until `ConnectionEstablished`, and by `ConnectionPage` in its status line. One new
  key, `CommService_ServerUnavailable`, in both `.resw` files, with the requested Italian
  text verbatim (141 -> 142 keys). Gate and VM build green.
- **Phase D1**: the repository `whatsappforwp-endpoint` was created (public, pushed):
  `endpoint.json`, `endpoint.md`, `publish.js` with tests-by-inspection, a validating and
  a manually-triggered publishing GitHub Action, and a README. It holds the address and
  nothing else; the token is what separates users, not the secrecy of the host.

Deferred, with the reason:

- **Phase F** gained a structural fact: on WP8.1 a background task must be a separate
  Windows Runtime Component, so it needs a new project in the solution. It is written into
  the phase above and should be its own session.
- **Phases A, B and C** are the security and tenancy core, and they must land together: an
  adapter that authenticates a token but does not scope every command and every webhook to
  that user's GOWA device would give a false sense of isolation. That is the next session's
  first work, not a tail-end commit.

Landed in the following pushes:

- **Phase D2**: `EndpointService` reads the public address from a file on GitHub, caches it
  in `LocalSettings`, falls back to the last good one, and the settings page gained a
  public/own switch. 143 -> 145 keys.
- **Phase A2** (already landed earlier): the token in `hello`, refused with `unauthorized`
  and a closed socket.
- **Phase B**: the adapter is now session-scoped. `createBridge` keeps a session per GOWA
  device (empty key for the private instance), and every command, cache, unread count and
  socket set lives in it. `hello` with a valid token creates the user's device
  (`gowa.createDevice`, labelled with the user name) and moves the socket there; a webhook
  is routed by its top-level `device_id`, so one user's message never reaches another's
  socket. Four isolation tests in `test/server.test.js`. 165 -> 173 adapter tests.

Still open, with the reason:

- **Phase A3** (TLS underneath the app cipher). WP8.1 `StreamSocket` has no certificate
  validation callback, so a self-signed certificate cannot be pinned in the app the way the
  plan assumed. The transport is already end-to-end encrypted by the app-level
  AES-256-CBC + HMAC-SHA256 cipher; a TLS layer with a public certificate is still worth
  adding for the framing metadata, but the pinning half of A3 needs a different design.
- **Phase C2/C3** (volume encryption guidance and `tools/backup.js`) are next.
