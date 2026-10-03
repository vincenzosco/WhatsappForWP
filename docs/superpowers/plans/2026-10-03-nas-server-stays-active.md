# NAS Server Stays Active Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The WhatsApp session on the NAS survives a restart and a phone reconnection, so the app finds a `connected` server every time it is opened.

**Architecture:** The container is already up and healthy - the fault is in the adapter's startup: `ensureDevice()` picks `devices[0]` (the **oldest** GOWA device, which is unlinked) while the linked session lives on a different device. The adapter must bind to the device that is actually `logged_in`, so its session reads `connected` instead of `disconnected`.

**Tech Stack:** Node.js adapter (zero runtime dependencies), Docker on Synology (`192.168.0.108`), GOWA 9.5.0.

**Spec:** the user's report on 2026-10-03: "perchè il server non è attivo? ogni volta che lo tocchi si deve riattivare". Observed on the NAS: `whatsapp-for-wp8` is `Up 2 days (healthy)` but GOWA reports every device `disconnected` except `4b26ef82` (`logged_in`, `393892672185@s.whatsapp.net`), while the adapter's startup device is the oldest one; the log repeats `can't send presence without PushName set` and app clients connect then drop.

## Global Constraints

- Adapter: zero runtime dependencies; every change carries a test.
- Runtime text (adapter logs) is **English**.
- Docs are pairs (`WhatsappBridge/README.md` + `README.it.md`).
- Every `WhatsappBridge/` commit is mirrored into `vincenzosco/docker-whatsappforwp` and pushed.
- **The NAS is a real machine.** `docker` needs `sudo` there; the password is the one the user supplied. Never print `GH_TOKEN` from `.env`.
- Do not delete `/data/storages`: it holds the linked WhatsApp session. Do not delete `/data/users.json` until the device-token plan has run (it is the only copy of the user records).

## Review Focus

- Two devices are `logged_in` at once: the adapter must pick deterministically, not at random.
- No device is `logged_in`: the adapter must still start (fall back), or the container would die in a restart loop.
- A device that is `disconnected` now but was linked before must not be preferred over the linked one.
- `withDevice(id)` must be given the *chosen* id, not `devices[0]`.
- The webhook must be registered on the chosen device, or this user's messages arrive nowhere.

---

### Task 1: Bind to the device that is logged in

**Files:**
- Modify: `WhatsappBridge/gowa-client.js` (`ensureDevice`, around line 86)
- Test: `WhatsappBridge/test/gowa-client.test.js`

**Interfaces:**
- Consumes: `listDevices()` (already there).
- Produces: `ensureDevice()` resolves to the `id` of the first device whose `state === 'logged_in'`; with none, it keeps today's behaviour (the first device, or a new one) and logs which one it chose.

- [ ] **Step 1: Write the failing tests**

In `WhatsappBridge/test/gowa-client.test.js`, with the injected `fetch` the suite already uses:

```js
test('ensureDevice sceglie il dispositivo collegato, non il primo', async () => {
  const client = new GowaClient({ baseUrl: 'http://x', fetchImpl: fetchListing([
    { id: 'old-1', state: 'disconnected' },
    { id: 'linked', state: 'logged_in' }
  ]) });
  assert.strictEqual(await client.ensureDevice(), 'linked');
});

test('senza nessun dispositivo collegato ripiega sul primo', async () => {
  const client = new GowaClient({ baseUrl: 'http://x', fetchImpl: fetchListing([
    { id: 'old-1', state: 'disconnected' },
    { id: 'old-2', state: 'disconnected' }
  ]) });
  assert.strictEqual(await client.ensureDevice(), 'old-1');
});
```

- [ ] **Step 2: Run them to verify they fail**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: FAIL - the first test gets `old-1`.

- [ ] **Step 3: Implement the choice**

```js
async ensureDevice() {
  const list = await this.request('GET', '/devices');
  const devices = (list.data && list.data.results) || [];
  if (Array.isArray(devices) && devices.length > 0) {
    // The linked device is the one with a WhatsApp session; the list is in
    // creation order, so devices[0] is the oldest - on this NAS that was a
    // device nobody had ever logged into, and the adapter then reported
    // "disconnected" while another device was in fact linked.
    const linked = devices.find((d) => d && d.state === 'logged_in');
    const chosen = linked || devices[0];
    this.resolvedDeviceId = chosen.id || null;
    return this.resolvedDeviceId;
  }
  const created = await this.request('POST', '/devices', { json: {} });
  const id = created.data && created.data.results && created.data.results.id;
  this.resolvedDeviceId = id || null;
  return this.resolvedDeviceId;
}
```

- [ ] **Step 4: Run the tests**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/gowa-client.js WhatsappBridge/test/gowa-client.test.js
git commit -m "Bind the adapter to the device that is actually logged in"
```

---

### Task 2: Do not send presence for an unlinked account

**Files:**
- Modify: `WhatsappBridge/server.js` (`updatePresence`, around line 545)
- Test: `WhatsappBridge/test/server.test.js`

**Interfaces:**
- Consumes: the session's `state.status` (already tracked).
- Produces: `updatePresence` returns early unless the session is `connected`, so GOWA never answers `can't send presence without PushName set`.

- [ ] **Step 1: Write the failing test**

A session whose state is `waiting` must not call `gowa.sendPresence`:

```js
test('non manda la presence se l account non e collegato', async () => {
  // bridge with a fake gowa recording sendPresence calls; a socket handshakes
  // while the account state is 'waiting' -> no call must be recorded
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: FAIL - `sendPresence` is called.

- [ ] **Step 3: Guard the call**

In `updatePresence`, after the `presenceSent` checks, add:

```js
// GOWA cannot set a presence without a PushName, which only exists once the
// account is linked: asking anyway makes it panic in its middleware every five
// seconds and fills the log with a failure that means nothing.
if (session.state.status !== 'connected') return;
```

- [ ] **Step 4: Run the tests**

Run: `cd WhatsappBridge && npm test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/server.js WhatsappBridge/test/server.test.js
git commit -m "Keep the presence quiet until the account is linked"
```

---

### Task 3: Bring the NAS up on the fixed adapter

**Files:** none in this repository (NAS + Docker mirror only).

**Interfaces:**
- Consumes: the mirror commit from the two tasks above.
- Produces: a running NAS whose adapter logs `GOWA device ready: <the logged_in id>` and whose app shows `connected`.

- [ ] **Step 1: Mirror and push the adapter**

```bash
cd /tmp/docker-whatsappforwp
node tools/sync.js --from /c/Users/Vincenzo/Documenti/WhatsAppForWP
node tools/sync.js --check --from /c/Users/Vincenzo/Documenti/WhatsAppForWP
(cd server && npm test)
git add -A && git commit -m "fix: bind to the logged-in device and stay quiet until linked"
git push origin main
```

The `image` workflow builds `ghcr.io/vincenzosco/docker-whatsappforwp:latest` from it.

- [ ] **Step 2: Pull the new image on the NAS and recreate**

```bash
plink -ssh -pw <pw> vincenzo@192.168.0.108 \
  "echo <pw> | sudo -S sh -c 'cd /volume1/docker/whatsapp-for-wp8 && \
   /usr/local/bin/docker compose -f docker-compose.yaml -f docker-compose.nas.yaml pull && \
   /usr/local/bin/docker compose -f docker-compose.yaml -f docker-compose.nas.yaml up -d'"
```

- [ ] **Step 3: Verify the session is the linked one**

```bash
# the adapter's startup line must name the logged_in device
plink ... "sudo docker logs whatsapp-for-wp8 | grep 'GOWA device ready'"
# and GOWA must still report that device as logged_in
plink ... "sudo docker exec whatsapp-for-wp8 node -e \"fetch('http://127.0.0.1:3000/devices').then(r=>r.text()).then(console.log)\""
```

Expected: `GOWA device ready: 4b26ef82-...` and a device with `"state":"logged_in"`.

- [ ] **Step 4: Make the container come back on its own**

The compose file already says `restart: unless-stopped`, so a reboot brings the stack back; what it does **not** do is reopen the tunnel's public port. Confirm the two services:

```bash
plink ... "sudo docker ps --filter name=whatsapp --format '{{.Names}} {{.Status}}'"
```

Expected: both `whatsapp-for-wp8` and `whatsapp-bore` `Up`.

- [ ] **Step 5: Re-link if the session is gone**

If `/devices` shows no `logged_in` device, the WhatsApp session was lost: open the app on the phone and scan the QR from the settings page (the app asks for `login.qr` itself). Nothing else in this plan can substitute for that login.

---

### Task 4: Document what keeps the NAS active

**Files:**
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`

- [ ] **Step 1: Add a paragraph to both**

State that the adapter binds to the GOWA device that is `logged_in` (not the first one), that the session lives in the `/data` volume, and that a lost session is re-linked by scanning the QR from the app. Same heading in both files.

- [ ] **Step 2: Verify and mirror**

```bash
node tools/check-docs.js
cd /tmp/docker-whatsappforwp && node tools/sync.js --from /c/Users/Vincenzo/Documenti/WhatsAppForWP
git add -A && git commit -m "docs: say which device the adapter binds to"
```

---

### Task 5: The full gate

- [ ] **Step 1: Run the fast gate and the adapter suite**

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node tools/check-fire-and-forget.js && node tools/check-project-files.js && node tools/check-chat-list-source.js
cd WhatsappBridge && npm test
```

Expected: all green.

- [ ] **Step 2: Push**

```bash
git push origin master
cd /tmp/docker-whatsappforwp && git push origin main
```

## What execution changed about this plan

- **Task 1 and Task 2 landed**, the second inside the token plan's Task 2 commit
  (same file, already staged). The adapter now logs
  `GOWA device ready: 4b26ef82-abbc-40ea-930e-882224b17ddb` - the device that is
  `logged_in` - and reports `WhatsApp connected as 393892672185@s.whatsapp.net`.
- **Task 3 was performed over SSH** with `plink`/`pscp` rather than an
  interactive session, and `docker` needed `sudo` on the NAS (the login user is
  not in the `docker` group). The image was pulled and the stack recreated;
  both `whatsapp-for-wp8` (healthy) and `whatsapp-bore` came back up.
- **The tunnel kept its port**: `bore.pub:41417`, already published, so the
  endpoint the app reads is unchanged.
- **The session was not lost**, so the re-link step (Task 3, Step 5) was not
  needed: `/data/storages` still held the linked account.
- **The presence panics are gone**: `can't send presence without PushName set`
  went from one every five seconds to zero in the three minutes after the
  restart.
- **`/data/users.json` was left alone on purpose.** It still holds the 12
  accumulated users, including the junk `device`/`probe` rows from the bug. The
  derived-token change stops it growing; removing the old rows is a separate
  decision, because the app may still be holding one of their tokens.
