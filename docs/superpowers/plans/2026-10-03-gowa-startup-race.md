# GOWA Startup Race Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The adapter binds to the linked GOWA device even when it starts in the same container as GOWA and asks for the device list before GOWA has answered and restored its session from disk.

**Architecture:** The NAS plan made `ensureDevice()` prefer the device that reports `logged_in`. That fixed a warm container, not a cold one: the adapter and GOWA start together, GOWA's REST API answers a few seconds later, and the login state is read from `/data/storages` a few seconds after that. One reading taken too early sees every device `disconnected`, so the adapter binds to the oldest unlinked device (or to nothing at all, logging `GOWA not reachable`), and every session it opens answers `disconnected` until somebody restarts it. Two waits fix it: one until the API answers, and one until a device reports `logged_in`.

**Tech Stack:** Node.js adapter (zero runtime dependencies), Docker on Synology (`192.168.0.108`), GOWA 9.5.0.

**Spec:** the user's report on 2026-10-03 ("perchè il server non è attivo? ogni volta che lo tocchi si deve riattivare"), and the NAS evidence after the first fix: `[ERR] GOWA not reachable at http://127.0.0.1:3000: fetch failed` in the adapter's first second, followed by `GOWA device ready: (default)`.

## Global Constraints

- Adapter: zero runtime dependencies; every change carries a test.
- Runtime text (adapter logs) is **English**; test names are Italian, like the rest of `test/`.
- Every `WhatsappBridge/` commit is mirrored into `vincenzosco/docker-whatsappforwp` and pushed.
- The waits must not delay a **healthy** start: an adapter that finds a `logged_in` device on the first reading proceeds immediately.
- The waits must not stop the container from starting at all: after the deadline the adapter still binds to the first device, as before.
- **The NAS is a real machine.** `docker` needs `sudo` there. Never print `GH_TOKEN` from `.env`. Do not delete `/data/storages` (the linked session) or `/data/users.json`.

## Review Focus

- GOWA never answers: `waitUntilReady` must give up and let the container keep running on the old behaviour, not hang forever and not crash in a restart loop.
- No device is ever `logged_in`: `ensureDevice` must still resolve (or create) a device: an unlinked server has to start so it can serve the QR login.
- A wait of zero means "do not wait", not "do not look": the first reading must still happen, or the fallback silently disappears.
- The device list is empty: the adapter must create a device, as it did before.
- A device that is `logged_in` from the start: exactly one reading, no one-second sleep in the way of startup.

---

### Task 1: Wait until GOWA answers before binding a device

**Files:**
- Modify: `WhatsappBridge/gowa-client.js` (`waitUntilReady`, new)
- Modify: `WhatsappBridge/server.js` (`main()`, before `ensureDevice()`)
- Test: `WhatsappBridge/test/gowa-client.test.js`

**Interfaces:**
- Consumes: `this.request(method, path)` (already there).
- Produces: `waitUntilReady(timeoutMs = 60000)` resolves `true` as soon as `GET /devices` answers, and throws the last error after the timeout.

- [ ] **Step 1: Write the failing tests**

```js
test('waitUntilReady aspetta che la API di GOWA risponda', async () => {
  let calls = 0;
  const fetchImpl = makeFetch(async () => {
    calls++;
    if (calls < 3) throw new Error('fetch failed');
    return jsonResponse({ status: 200, results: [] });
  });
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });

  assert.strictEqual(await client.waitUntilReady(10000), true);
  assert.strictEqual(calls, 3);
});

test('waitUntilReady rinuncia quando GOWA non risponde mai', async () => {
  const fetchImpl = makeFetch(async () => { throw new Error('fetch failed'); });
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });

  await assert.rejects(() => client.waitUntilReady(1500), /fetch failed/);
});
```

- [ ] **Step 2: Run them to verify they fail**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: FAIL - `client.waitUntilReady is not a function`.

- [ ] **Step 3: Implement `waitUntilReady(timeoutMs = 60000)`**

Poll `GET /devices` once a second until it answers; keep the last error and rethrow it when the deadline passes.

- [ ] **Step 4: Call it from `main()`**

In `WhatsappBridge/server.js`, immediately before `const deviceId = await gowa.ensureDevice();`:

```js
// GOWA starts with the adapter in the same container and answers only a few
// seconds later: asking for a device before that logged "GOWA not reachable"
// and left the adapter with no device at all.
if (typeof gowa.waitUntilReady === 'function') await gowa.waitUntilReady();
```

The `typeof` check keeps the call working against a `gowa` stub, as the tests use.

- [ ] **Step 5: Run the tests**

Run: `cd WhatsappBridge && npm test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add WhatsappBridge/gowa-client.js WhatsappBridge/server.js WhatsappBridge/test/gowa-client.test.js
git commit -m "Wait for GOWA to answer before binding a device"
```

---

### Task 2: Re-read the device list until the login shows up

**Files:**
- Modify: `WhatsappBridge/gowa-client.js` (`ensureDevice`, constructor `linkedWaitMs`)
- Test: `WhatsappBridge/test/gowa-client.test.js`

**Interfaces:**
- Consumes: the `ensureDevice` from the NAS plan.
- Produces: `ensureDevice()` reads `GET /devices` at least once, repeats it every second until a device reports `state === 'logged_in'` or `linkedWaitMs` (constructor option, default 15000) expires, and then resolves to the linked device's id, or the first device, or a newly created one.

- [ ] **Step 1: Write the failing test**

```js
test('ensureDevice aspetta che GOWA rilegga il login dal disco', async () => {
  let reads = 0;
  const fetchImpl = makeFetch(async () => {
    reads++;
    const linked = reads >= 3;
    return jsonResponse({
      status: 200,
      results: [
        { id: 'old-1', state: 'disconnected' },
        { id: 'linked', state: linked ? 'logged_in' : 'disconnected' }
      ]
    });
  });
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });

  assert.strictEqual(await client.ensureDevice(), 'linked');
  assert.ok(reads >= 3, 'deve rileggere finche il login non compare');
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: FAIL - the old code returns `old-1` after one reading.

- [ ] **Step 3: Rewrite `ensureDevice` as a bounded re-read loop**

`for (;;)`: read `/devices`; stop when the list is empty (fall through to create a device), when a device is `logged_in`, or when the deadline has passed; otherwise sleep one second. Fall back to `devices[0]` at the end. The first reading always happens, so `linkedWaitMs: 0` means "do not wait", not "do not look".

- [ ] **Step 4: Shorten the wait in the two tests that do not test the wait**

`ensureDevice riusa il primo device esistente` takes `linkedWaitMs: 0`; `senza nessun device collegato ensureDevice ripiega sul primo` takes `linkedWaitMs: 1200`. Both keep asserting the fallback, not the 15 seconds.

- [ ] **Step 5: Run the whole adapter suite**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 230 tests, no test slower than a couple of seconds.

- [ ] **Step 6: Commit, mirror, deploy**

Same steps as the NAS plan's Task 3: mirror with `node tools/sync.js --from ...` then `--check`, mirror commit + push, wait for the `image` workflow, pull the image on the NAS and recreate the stack with `sudo`.

- [ ] **Step 7: Verify on the NAS**

The startup log must show `GOWA device ready: <the logged_in device>` and **no** `GOWA not reachable` line, and `WhatsApp connected as 393892672185@s.whatsapp.net` must follow.

## What execution changed about this plan

- **Both waits landed as written.** `waitUntilReady(timeoutMs = 60000)` and the
  bounded re-read in `ensureDevice` are exactly the shape the plan asked for, and
  `server.js` calls `waitUntilReady()` before `ensureDevice()`.
- **The device the adapter binds to is not a fixed id.** The plan named
  `4b26ef82-…` from the previous deployment, but GOWA **recreates its device list
  on every container start**: the ids in `/devices` after the redeploy were all
  new (`created_at` inside the same minute), and the `logged_in` one was
  `24f43937-159c-4bcb-a957-4c98cde91d56`. That is exactly why `ensureDevice` must
  *find* the linked device rather than remember one, and the verification below
  checks the binding against `/devices` live instead of against a literal id.
- **`whatsapp-for-wp8` had to be recreated, not just restarted**: `docker compose
  up -d` recreated both `whatsapp-for-wp8` and `whatsapp-bore`, so the tunnel
  reconnected. It asked for the same port and kept it: `bore.pub:41417`, already
  published, so the app's endpoint did not change.
- **The old image proved the bug in its own log.** Before the redeploy, the
  running container had `[ERR] GOWA not reachable at http://127.0.0.1:3000: fetch
  failed` in its startup - the race this plan fixes - followed by a device-ready
  line for an unlinked device. After the redeploy there is **no** `GOWA not
  reachable` line at all, and `WhatsApp connected as
  393892672185@s.whatsapp.net` follows within the same second.
- **On the NAS `docker` is not on root's PATH**, so every command is
  `/usr/local/bin/docker` under `sudo -S` (the login user is not in the `docker`
  group); `sudo -S docker …` answers `sudo: docker: command not found`.
- **A test needed its wait shortened**: `ensureDevice riusa il primo device
  esistente` asserts the fallback, so it now passes `linkedWaitMs: 0` and asserts
  `calls.length >= 1` instead of `=== 1` - one reading still happens, but the test
  no longer pays for the wait it is not testing.