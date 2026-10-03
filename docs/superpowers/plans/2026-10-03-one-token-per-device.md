# One Token Per Device Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A phone that connects to the shared service keeps exactly one token, for as long as it is the same device.

**Architecture:** The phone sends a **stable device id** in every `hello` (`SenderId`), and the adapter derives the token from that id with a secret held in `users.json` (`HMAC-SHA256(secret, deviceId)`). A device that connects again gets the *same* token back instead of a new user; the file stops growing one row per connection.

**Tech Stack:** Node.js (adapter, zero runtime dependencies), C# 5 / WP8.1 (`WhatsappApp`).

**Spec:** the user's report on 2026-10-03: "il token viene rigenerato ogni volta che il dispositivo si connette al server, e non è costante come deve essere, fai in modo che rimanga uno e basta per il dispositivo". The chosen strategy is **stable token derived from the device id**.

## Global Constraints

- Adapter: `WhatsappBridge/` has **zero runtime dependencies**; every change carries a test (`cd WhatsappBridge && npm test`).
- C# 5 only (no `?.`, no `$""`, no expression-bodied members). Gate: `node tools/check-csharp5.js`.
- No new `.cs` file is created by this plan, so no `.csproj` change is needed. If one is added, it must be listed (`node tools/check-project-files.js`).
- Runtime text (adapter logs, `Diag` lines) is **English**; only the app UI is localized.
- Docs are pairs: `WhatsappBridge/README.md` **and** `README.it.md` (`node tools/check-docs.js`).
- Every `WhatsappBridge/` commit is mirrored into `vincenzosco/docker-whatsappforwp` (`tools/sync.js`) and pushed.
- Existing tokens must keep working: a phone that already holds a token is not locked out.

## Review Focus

- A device that presents a **wrong token** but a known device id must get its derived token back, not a second user.
- Two different devices must never receive the same token.
- The `AUTH_MAX_USERS` ceiling must count **devices**, and re-registering an existing device must never consume a slot.
- A `hello` with no device id (an old app build) must not crash the adapter and must not be handed another device's token.
- The secret must never be written to the log.

---

### Task 1: Derive the token from the device id

**Files:**
- Modify: `WhatsappBridge/users.js`
- Test: `WhatsappBridge/test/users.test.js`

**Interfaces:**
- Produces: `deviceToken(secret, deviceId, createHmac)` -> base64url string; `createUserStore(...).register(deviceId, name)` -> `{ token, user, existing }`; `createUserStore(...).findByClientId(deviceId)` -> user | null; the store gains `secret`.
- Consumes: `crypto`, `hashToken` (already there).

- [ ] **Step 1: Write the failing tests**

In `WhatsappBridge/test/users.test.js` add:

```js
test('lo stesso dispositivo ottiene sempre lo stesso token', () => {
  const users = createUserStore({ file: tmpFile() });
  const first = users.register('dev-1', 'vincenzo');
  const again = users.register('dev-1', 'vincenzo');
  assert.strictEqual(again.token, first.token, 'il token deve essere lo stesso');
  assert.strictEqual(users.count(), 1, 'un dispositivo non deve creare due utenti');
});

test('due dispositivi diversi non condividono il token', () => {
  const users = createUserStore({ file: tmpFile() });
  const a = users.register('dev-1', 'a');
  const b = users.register('dev-2', 'b');
  assert.notStrictEqual(a.token, b.token);
  assert.strictEqual(users.count(), 2);
});

test('il token derivato sopravvive a un riavvio del processo', () => {
  const file = tmpFile();
  const first = createUserStore({ file });
  const { token } = first.register('dev-1', 'vincenzo');
  const second = createUserStore({ file });
  assert.strictEqual(second.register('dev-1', 'vincenzo').token, token);
  assert.ok(second.verify(token), 'il token derivato deve verificare');
});

test('findByClientId trova il dispositivo, e non un altro', () => {
  const users = createUserStore({ file: tmpFile() });
  users.register('dev-1', 'vincenzo');
  assert.strictEqual(users.findByClientId('dev-1').name, 'vincenzo');
  assert.strictEqual(users.findByClientId('dev-2'), null);
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd WhatsappBridge && node --test test/users.test.js`
Expected: FAIL - `users.register('dev-1', ...)` treats `dev-1` as a *name* and `findByClientId` does not exist.

- [ ] **Step 3: Implement the derivation in `WhatsappBridge/users.js`**

Add near `newToken`:

```js
const crypto = require('crypto');

// The token of a device is a function of the device id and a secret the service
// keeps: the same device is handed the same token every time, so a phone that
// reconnects (or reinstalls the app and types the token again) is the same user,
// not a new one. This is the trade-off the operator chose: the file now holds a
// secret from which tokens can be derived, where before it held only hashes.
function deviceToken(secret, deviceId, createHmac) {
  const hmac = (createHmac || crypto.createHmac)('sha256', String(secret));
  hmac.update('device:' + String(deviceId));
  return hmac.digest('base64url');
}
```

In `createUserStore`, add `const createHmac = opts.createHmac || crypto.createHmac;` and a module-level `secret`:

- `load()` reads `{ secret, users }`; when `secret` is absent (an older file) generate one with `randomBytes(32).toString('base64url')` and keep it.
- `save()` writes `{ secret, users }`.
- `register(deviceId, name)`:

```js
function register(deviceId, name) {
  const id = String(deviceId || '');
  const known = id ? users.find((u) => u.clientId === id) : null;
  if (known) {
    if (name) known.name = String(name);
    known.lastSeenAt = now();
    save();
    return { token: deviceToken(secret, id, createHmac), user: known, existing: true };
  }
  // No device id (an old app build): the token is random, as before, so the
  // adapter keeps working with a phone that has not been updated yet.
  const token = id ? deviceToken(secret, id, createHmac) : newToken(randomBytes);
  const salt = randomBytes(16).toString('hex');
  const user = {
    id: randomBytes(8).toString('hex'),
    name: String(name || ''),
    clientId: id,
    salt,
    tokenHash: hashToken(token, salt, scryptSync),
    deviceId: '',
    createdAt: now(),
    lastSeenAt: now()
  };
  users.push(user);
  save();
  return { token, user, existing: false };
}
```

- `findByClientId(deviceId)` -> `users.find((u) => u.clientId === String(deviceId || '')) || null`.
- Export `deviceToken` alongside `createUserStore`, `hashToken`, `newToken`.

Keep `verify` as it is: it still matches, because the derived token's scrypt hash is what is stored.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd WhatsappBridge && node --test test/users.test.js`
Expected: PASS, and the pre-existing token tests still pass.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/users.js WhatsappBridge/test/users.test.js
git commit -m "Derive a device's token from its id so it stops changing"
```

---

### Task 2: Hand an existing device its own token back

**Files:**
- Modify: `WhatsappBridge/server.js` (the `hello` case, around line 1139)
- Test: `WhatsappBridge/test/server.test.js`

**Interfaces:**
- Consumes: `users.findByClientId`, `users.register(deviceId, name)`, `{ token, user, existing }` (Task 1).
- Produces: a `registered` frame is sent **only** when the device had no user yet (`existing === false`); a returning device is authenticated silently.

- [ ] **Step 1: Write the failing test**

In `WhatsappBridge/test/server.test.js` add a test in the style of the existing handshake tests: two `hello` frames with the same `SenderId` and no token must leave `users.count() === 1`, and the second `hello` must not emit a `registered` frame.

```js
test('un dispositivo che si riconnette non crea un secondo utente', async () => {
  // ... start a bridge with authRequired true and a user store
  // first hello: no token, SenderId 'dev-1' -> registered frame with a token
  // second hello: no token, SenderId 'dev-1' -> no registered frame, count stays 1
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: FAIL - the second handshake creates a second user (the reported bug).

- [ ] **Step 3: Change the registration condition in `server.js`**

Replace the `if (!verdict.ok && authRegister && ...)` block with:

```js
const clientId = typeof msg.SenderId === 'string' ? msg.SenderId.trim() : '';
let created = null;

if (!verdict.ok && authRegister && users) {
  const known = clientId ? users.findByClientId(clientId) : null;
  // Room is only needed for a device the service has never seen: a returning
  // device is handed its own token again and never counts against the ceiling.
  const roomLeft = known ? true : users.count() < authMaxUsers;
  if (roomLeft) {
    created = users.register(clientId, msg.SenderName || 'device');
    verdict = { ok: true, user: created.user };
    logger('OK', created.existing
      ? `device re-registered: ${created.user.id} (${created.user.name})`
      : `device registered: ${created.user.id} (${created.user.name})`);
  }
}
```

Then send the `registered` frame only for a brand-new device:

```js
if (created && !created.existing && socket) {
  sendToClient(socket, buildChatMessage({
    command: 'registered', token: created.token, senderName: created.user.name,
    chatId: 'system', isIncoming: true
  }));
}
```

- [ ] **Step 4: Run the tests**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, count unchanged plus the new test.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/server.js WhatsappBridge/test/server.test.js
git commit -m "Give a returning device its own token instead of a new user"
```

---

### Task 3: The phone keeps one device id

**Files:**
- Modify: `WhatsappApp/Services/SettingsService.cs`
- Modify: `WhatsappApp/Services/CommunicationService.cs:330`

**Interfaces:**
- Produces: `SettingsService.DeviceId` -> a `string` that is generated once and persisted (`DeviceId` local setting).
- Consumes: nothing new.

- [ ] **Step 1: Add the persisted device id to `SettingsService`**

Add the key next to the others:

```csharp
private const string KeyDeviceId = "DeviceId";
```

and the property (C# 5, no expression body):

```csharp
/// <summary>
/// The id this phone presents to the shared service. It is generated once and
/// kept, because the service derives the token from it: a new id would mean a
/// new token and a second user of the same phone.
/// </summary>
public static string DeviceId
{
    get
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(_deviceId))
        {
            _deviceId = Guid.NewGuid().ToString("N").Substring(0, 8);
            Settings.Values[KeyDeviceId] = _deviceId;
        }
        return _deviceId;
    }
}
```

Add `private static string _deviceId;` with the other fields and `_deviceId = ReadString(KeyDeviceId, "");` in `EnsureLoaded`. `Guid` needs `using System;`, already present.

- [ ] **Step 2: Use it in the handshake**

In `CommunicationService.ConnectToServerAsync`, replace

```csharp
_myUserId = Guid.NewGuid().ToString("N").Substring(0, 8);
```

with

```csharp
// Stable for the life of the install: the server derives the token from it, so
// a fresh id every connection was one new user of the same phone per connect.
_myUserId = SettingsService.DeviceId;
```

- [ ] **Step 3: Verify**

Run: `node tools/check-csharp5.js && node tools/check-project-files.js`
Expected: `OK` from both.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Services/SettingsService.cs WhatsappApp/Services/CommunicationService.cs
git commit -m "Present one device id for the life of the install"
```

---

### Task 4: Document the derived token

**Files:**
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`
- Modify: `.agents/skills/maintain-the-app/SKILL.md`

- [ ] **Step 1: Add the paragraph to both adapter READMEs**

State that the token is derived from the device id with a secret in `users.json`, that a device therefore keeps one token, and that the secret must be treated as a credential (it can derive tokens). Same heading depth in both files.

- [ ] **Step 2: Extend the `maintain-the-app` gotcha**

In the bullet *"On the shared service the token is authentication, not isolation; the device is."*, add: the token is `HMAC-SHA256(secret, deviceId)` and the device id is the phone's persisted `SettingsService.DeviceId`, sent as `SenderId` in `hello`.

- [ ] **Step 3: Verify**

Run: `node tools/check-docs.js`
Expected: `OK`.

- [ ] **Step 4: Commit and mirror**

```bash
git add WhatsappBridge/README.md WhatsappBridge/README.it.md .agents/skills/maintain-the-app/SKILL.md
git commit -m "Say how a device keeps one token"
cd /tmp/docker-whatsappforwp && node tools/sync.js --from /c/Users/Vincenzo/Documenti/WhatsAppForWP
node tools/sync.js --check --from /c/Users/Vincenzo/Documenti/WhatsAppForWP
(cd server && npm test)
git add -A && git commit -m "fix: derive a device's token from its id"
```

---

### Task 5: The full gate

- [ ] **Step 1: Run the fast gate**

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node tools/check-fire-and-forget.js && node tools/check-project-files.js && node tools/check-chat-list-source.js
node --test "tools/test/**/*.test.js"
cd WhatsappBridge && npm test
```

Expected: `OK` from each guard, tool tests green, adapter suite green (one more test than before).

- [ ] **Step 2: Push both repositories**

```bash
git push origin master
cd /tmp/docker-whatsappforwp && git push origin main
```

## What execution changed about this plan

- **Task 1 landed as written.** `register(deviceId, name)` keeps the existing
  `register(name)` call sites working (the name lands in `deviceId`, which is
  what an app build with no device id does anyway), so `users.test.js` needed no
  edits beyond the new tests.
- **Task 2's helper call sites moved.** `sharedBridge()` and two other tests in
  `server.test.js` called `users.register('anna')`; they now pass a device id
  and a name, because the adapter keys on the device.
- **The presence guard (a task of the NAS plan) landed in the same commit as
  Task 2**, because `server.js` was already staged for it. No behaviour was
  dropped; only the commit boundary moved.
- **No `.resw` change**: the token was never a user-visible string, so the app
  side is `SettingsService.DeviceId` and one line of `CommunicationService`.
