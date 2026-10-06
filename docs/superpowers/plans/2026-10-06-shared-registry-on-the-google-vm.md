# The shared registry on the Google VM Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A free e2-micro VM becomes the single writer of `endpoint.json`: every server (the owner's, and anyone else's) reports its address to the VM with an HTTP POST, and the VM publishes the merged list to the repository with its own GitHub token. The app keeps reading the file exactly as it does today.

**Architecture:** Three small pieces. (1) A dependency-free Node service on the VM (`registry/`) holds the reported rows in memory, persists them to disk, merges them with the remote `endpoint.json` **in place** (so the `public` row stays first and the top-level `host`/`port` keep meaning the tunnel address), writes only when the text really changed, and expires rows nobody has refreshed. (2) The adapter gains a *reporter* mode in `endpoint-publisher.js`: with `ENDPOINT_REGISTRY_URL` set it POSTs to the VM instead of writing to GitHub. (3) The tunnel does the same from `publish-endpoint.sh`, so no container needs a GitHub token any more. The app is untouched.

**Tech Stack:** Node 18+/22 (`node:http`, global `fetch`, `node:test`), shell for the tunnel, systemd for the service, Google Cloud CLI for the VM. No npm dependencies anywhere: the adapter's zero-dependency rule covers the new files too.

**Spec:** the request as given, verbatim: *"adesso creo una vm e2-micro su google, quella gratuita con ip statico, poi modifica il server in modo che il container mio ma anche di altre persone, mandino il suo ip alla vm, e la vm lo pubblica con il token gh sul repo, e l'app lo legge, installa googlecli per google cloud, mi loggo e gestisci tutto"*. Decisions taken with the requester: the VM **already exists** (coordinates discovered with `gcloud`); the register endpoint requires a **shared secret** (`REGISTRY_TOKEN`, `401` without it); the tunnel **also reports to the VM**, so the VM is the only writer and the containers keep no GitHub token.

## Global Constraints

- **Zero npm dependencies.** `registry/` and the adapter use only Node built-ins. `node:test` for tests; no test runner package.
- **C# side untouched**: this change is `WhatsappBridge/` + the Docker repository + docs. The app already reads the list and needs nothing.
- **Runtime text is English** (logs, errors, comments); the guard-script and test names may stay Italian.
- **Docs come in pairs**: `WhatsappBridge/README.md` + `.it.md` (and the mirror's pair) are versions of each other, same headings in the same order; `node tools/check-docs.js` is the gate.
- **`endpoint.json` keeps `public` first.** The top-level `host`/`port` mirror `servers[0]`, and an app built before the list reads only that pair. Any merge must update rows **in place** and never reorder.
- **Write only on a real change.** A heartbeat that rewrites the file every 15 minutes is a commit every 15 minutes; the service compares the rendered text with the remote one and skips an identical write.
- **Never commit a secret.** The token lives in the VM's `/etc/whatsappforwp-registry.env` (mode `600`), the shared secret in the deployments' `.env` (already git-ignored).
- Every commit to `WhatsappBridge/` ends with the same content mirrored into `docker-whatsappforwp` via `tools/sync.js`, committed and pushed, and the app repository's `master` is pushed too.

## Review Focus

- **A second writer.** If the adapter or the tunnel keeps its GitHub path while the VM also writes, the sha-gated race that already exists gets worse and rows vanish. Pinned in Tasks 4 and 5 (the registry path replaces the GitHub path, it does not run beside it).
- **A row that moves.** A naive merge that filters-then-pushes moves an existing row to the end, which changes `servers[0]` and therefore the address an old app dials. Pinned in Task 1's tests.
- **A container that goes offline.** Its row must disappear after `REGISTRY_TTL_MINUTES`, and reappearing must be one POST. Pinned in Task 1's tests and Task 2's.
- **The remote file missing or unreadable.** A fresh repository, or GitHub answering 5xx, must leave the reported rows publishable rather than crashing the service, and must not erase rows it never managed. Pinned in Task 2's tests.
- **An unauthenticated or malformed POST.** A wrong secret is `401`, a bad id/host/port is `400`, a 1 MB body is refused, and the process stays up. Pinned in Task 2's tests.

---

### Task 1: the registry logic, pure and tested

**Files:**
- Create: `registry/registry.js` (mirror repository)
- Test: `registry/registry.test.js`

**Interfaces:**
- Produces:
  - `normalizeEntry(raw)` → `{id, name, host, port, updatedAt}|null` (id `[A-Za-z0-9._-]{1,64}`, host `1..253` with no space, port `1..65535`).
  - `upsertReported(list, entry, nowIso)` → a new list with the entry replaced **in place** by `id` or appended.
  - `expire(list, ttlMs, nowMs)` → `{ live, dropped }`, `dropped` being the ids that went stale.
  - `mergeRegistry(remote, reported, dropped, nowIso)` → the registry object to publish: remote rows in their original order, reported rows replacing theirs in place, new ones appended, `dropped` removed **unless** they were reported again, top-level `host`/`port` = `servers[0]`, `tls`/`fingerprint` carried over.
  - `renderMarkdown(registry)` → the same shape `publish-endpoint.sh` writes today (title, updated line, one bullet per server).

- [ ] **Step 1: Write the failing tests**

Cover, at minimum: a second report for the same id updates in place and keeps its index; a new id is appended last; `mergeRegistry` leaves `public` first and the top-level pair equal to it; an expired id is removed from the merged output; an expired id that reported again stays; `tls`/`fingerprint` survive; a malformed entry (bad port, empty host, id with a space) is `null` and changes nothing; `renderMarkdown` names every server.

Run: `node --test registry/registry.test.js`
Expected: FAIL, `registry.js` does not exist.

- [ ] **Step 2: Implement `registry/registry.js`**

Pure functions, no I/O, no `require` of anything but the standard library. The merge is the one piece of real thought: build the output from the remote list, `splice` the replacement at the index the row already had, `push` the new ones, and filter the `dropped` ids out afterwards only if they are not in the reported set.

- [ ] **Step 3: Verify**

Run: `node --test registry/registry.test.js`
Expected: PASS.

- [ ] **Step 4: Commit** (in the mirror repository)

```bash
git add registry/registry.js registry/registry.test.js
git commit -m "feat: the registry merge keeps every row in place"
```

---

### Task 2: the service on the VM

**Files:**
- Create: `registry/service.js`
- Test: `registry/service.test.js`

**Interfaces:**
- Consumes: `registry/registry.js` (Task 1).
- Produces: `createRegistryService(options)` with
  - options: `{ token, repo, secret, file, ttlMinutes, debounceMs, publishMinutes, fetchImpl, now, log }`
  - returns `{ server, start(port), publishNow(), state(), handleRequest(req, res), stop() }`
  - HTTP: `POST /register` (JSON), `GET /health`, `GET /servers`, everything else `404`.
  - env read by `main()`: `REGISTRY_PORT` (8787), `REGISTRY_FILE` (`/var/lib/whatsappforwp-registry/registry.json`), `REGISTRY_TOKEN`, `REGISTRY_TTL_MINUTES` (1440), `REGISTRY_DEBOUNCE_MS` (5000), `REGISTRY_PUBLISH_MINUTES` (15), `REGISTRY_REPO`, `REGISTRY_GH_TOKEN`.

- [ ] **Step 1: Write the failing tests**

With a fake `fetchImpl` (a `Map` of the two file paths) and a temp `file`: a POST registers; a second POST for the same id updates in place; `401` on a wrong or missing secret when one is configured; `200` with no secret when none is configured; `400` on a bad body; `413`/closed on an oversized body; `GET /health` reports the count; the file on disk is rewritten; a publish writes `endpoint.json` **and** `endpoint.md`; an unchanged registry does **not** write again; a stale row is dropped from the published file.

Run: `node --test registry/service.test.js`
Expected: FAIL.

- [ ] **Step 2: Implement `registry/service.js`**

`node:http`, a 8 KB body cap, `crypto.timingSafeEqual` for the secret, atomic persistence (`write` then `rename`), a debounced publish plus the heartbeat interval, and a `publishNow()` that reads the remote file through the Contents API (with `sha`), merges, and PUTs only when the rendered text differs. All failures are logged and swallowed: a GitHub outage must not take the service down.

- [ ] **Step 3: Verify**

Run: `node --test registry/service.test.js && node --test registry/registry.test.js`
Expected: PASS, and the whole `registry/` suite green.

- [ ] **Step 4: Commit**

```bash
git add registry/service.js registry/service.test.js
git commit -m "feat: the registry service collects the servers and publishes them"
```

---

### Task 3: what installs it on the VM

**Files:**
- Create: `registry/whatsappforwp-registry.service`
- Create: `registry/setup.sh`
- Modify: `registry/README.md` — **not** created: the mirror README pair carries the prose (Task 6).

**Interfaces:**
- Consumes: `registry/service.js`.
- Produces: a systemd unit `whatsappforwp-registry` running `node /opt/whatsappforwp-registry/service.js` as `DynamicUser` with `StateDirectory=whatsappforwp-registry`, `EnvironmentFile=/etc/whatsappforwp-registry.env`, `ProtectSystem=strict`, `NoNewPrivileges=yes`; and a `setup.sh` that copies the two files, installs Node 22 from the official tarball when the system Node is older than 18, writes the env file with mode `600`, and enables and starts the unit.

- [ ] **Step 1: Write the unit and the script**

`setup.sh <service.js> <unit> --token <gh token> --secret <registry secret> [--repo owner/name] [--port 8787]`, run **on the VM** as root: idempotent, so a second run is a reinstall. It must not print the token.

- [ ] **Step 2: Verify the script is at least syntactically sane where it can run**

Run: `bash -n registry/setup.sh && sh -n docker/publish-endpoint.sh`
Expected: no output, exit 0. (The real run happens in Task 7, on the VM.)

- [ ] **Step 3: Commit**

```bash
git add registry/setup.sh registry/whatsappforwp-registry.service
git commit -m "feat: install the registry as a systemd service"
```

---

### Task 4: the adapter reports to the VM

**Files:**
- Modify: `WhatsappBridge/config.js` (two keys), `WhatsappBridge/endpoint-publisher.js`
- Test: `WhatsappBridge/test/endpoint-publisher.test.js`
- Mirror: `$M/server/…` through `tools/sync.js`

**Interfaces:**
- Produces: `endpoint.registryUrl` (`ENDPOINT_REGISTRY_URL`) and `endpoint.registrySecret` (`ENDPOINT_REGISTRY_SECRET`). With a URL set, `publish()` POSTs `{id, name, host, port, secret}` to `<url>/register` and returns; the GitHub paths are not touched.

- [ ] **Step 1: Write the failing test** — `publish con un registro manda la riga alla VM e non a GitHub`: a fake `fetchImpl` that records the call, a `registryUrl`, and an assertion that the method is `POST`, the URL ends `/register`, the JSON body carries this server's id/host/port, and **no** Contents-API call happened.
- [ ] **Step 2: Run it** — `cd WhatsappBridge && npm test`, expect FAIL.
- [ ] **Step 3: Implement** — a `reportToRegistry()` helper used first in `publish()`, plus the two config keys and their defaults in `config.js`.
- [ ] **Step 4: Run it** — expect the whole adapter suite green (288 + the new one).
- [ ] **Step 5: Mirror, commit, push** — `node tools/sync.js --from <app checkout>`, `--check`, `(cd server && npm test)`, commit and push in both repositories.

---

### Task 5: the tunnel reports to the VM too

**Files:**
- Modify: `docker/publish-endpoint.sh`, `Dockerfile.tunnel`, `docker-compose.yaml`, `docker-compose.nas.yaml`, `.env.example`

**Interfaces:**
- Consumes: the same `POST /register` contract as Task 4.
- Produces: when `ENDPOINT_REGISTRY_URL` is set the script POSTs with `curl` (already in the image) and exits without touching GitHub; the GitHub path stays as the fallback when it is not set.

- [ ] **Step 1: Add the branch** — build the body with `printf` (no `jq`, which the image has but the test host does not), `curl -fsS --max-time 20`, and report the outcome in English on the container log.
- [ ] **Step 2: Verify the branch really posts** — start the Task 2 service locally on `127.0.0.1:8787` with a known secret and a temp file, then run `ENDPOINT_REGISTRY_URL=http://127.0.0.1:8787 ENDPOINT_REGISTRY_SECRET=… ENPOINT_... sh docker/publish-endpoint.sh 41417` and assert the service received the row (`GET /servers`). Then the same with a wrong secret, expecting the failure line and a non-zero-free exit.
- [ ] **Step 3: Pass the two variables through** — `env_file` already carries them; add them to the tunnel's explicit `environment:` block and to `.env.example` so they are discoverable.
- [ ] **Step 4: Commit** (mirror repository) and push.

---

### Task 6: the docs

**Files:**
- Modify: `WhatsappBridge/README.md` + `.it.md` (config table), `WhatsappBridge/.env.example`
- Modify (root, app repository): `README.md` + `README.it.md`
- Modify (mirror): `README.md` + `README.it.md`

- [ ] **Step 1** — the adapter pair: the two new variables, and the sentence that says a container no longer needs a GitHub token when a registry is configured.
- [ ] **Step 2** — the root pair: the paragraph about the registry now says the list is collected by a small service on a free VM and published from there, so the containers keep no credential.
- [ ] **Step 3** — the mirror pair: a new `## The registry on the VM` / `## Il registro sulla VM` section with the POST contract, the variables, the TTL, and the `setup.sh` run. Same heading, same depth, same order in both languages.
- [ ] **Step 4: Verify** — `node tools/check-docs.js` from the app repository, and `git grep -n 'REGISTRY_'` in both repositories to confirm every documented variable exists.
- [ ] **Step 5: Commit and push** both repositories.

---

### Task 7: the VM

**Files:** none — this is the deployment.

- [ ] **Step 1: The CLI** — the Cloud SDK from the official Windows archive at `C:\Users\Vincenzo\google-cloud-sdk` (started before Task 1; verify `bin/gcloud.cmd version` answers).
- [ ] **Step 2: Log in** — `gcloud auth login` is interactive and **the requester runs it**; then `gcloud compute instances list` to find the e2-micro, its zone and its external IP (the static one), and `gcloud compute firewall-rules list` to see whether the registry port is open.
- [ ] **Step 3: Open the port** — if nothing allows it, create the rule for `REGISTRY_PORT` (default 8787) from `0.0.0.0/0`.
- [ ] **Step 4: Install** — copy `registry/` to the VM (`gcloud compute scp`) and run `setup.sh` with the token and the secret.
- [ ] **Step 5: Verify from outside** — `GET http://<ip>:8787/health` answers `{"ok":true,...}`; a `POST /register` with the secret adds a row; without it, `401`. Then check `endpoint.json` in the repository carries the row.

---

### Task 8: point this deployment at the VM, and close the loop

**Files:** `/volume1/docker/whatsapp-for-wp8/.env` on the NAS (not in git).

- [ ] **Step 1** — add `ENDPOINT_REGISTRY_URL` and `ENDPOINT_REGISTRY_SECRET` to the NAS `.env` (backup first, as in the previous change).
- [ ] **Step 2** — recreate the stack and read the logs: the adapter prints `[endpoint] reported 192.168.0.108:8585 as nas …`, the tunnel prints `[publish] bore.pub:41417 reported …`, and no `[endpoint] could not publish` line appears.
- [ ] **Step 3** — confirm `endpoint.json` still has `public` first with the tunnel's address, the `nas` row updated, and `updatedAt` fresh.
- [ ] **Step 4** — report honestly, including that the token has been pasted into the chat and should be rotated.

---

## What execution changed about this plan

- **Where `registry/` lives.** The plan put it in the mirror repository
  (`docker-whatsappforwp`); it lives in the app repository instead. The service is
  deployed by `gcloud compute scp` and systemd, never built as an image, so no
  image build needs it, and one copy is easier to keep honest than a source plus
  a mirror. The mirror's README carries the deployment prose, and its
  half-written `registry/registry.js` was removed.
- **`registry/service.js` retries.** GitHub refuses a write whose `sha` is stale,
  and while a deployment is still moving to this service the file has another
  writer: `publishNow()` reads the file again and retries once instead of making
  the list wait for the quarter-hour heartbeat.
- **`setup.sh` copies `registry.js` too**, not just `service.js`: the service
  `require`s it, and installing one without the other is a service that cannot
  start. It takes the directory `service.js` sits in as its source, which keeps
  the plan's two positional arguments.
- **The tunnel test runs the script after `tr -d '\r'`.** The checkout on Windows
  is CRLF and the image CI builds is LF; a CRLF script fails in a way that looks
  like a registry bug.
- **A refused report exits non-zero.** The plan's Task 5 step 2 asked for a
  non-zero exit and that is what it is; the tunnel entrypoint calls the script in
  the background, so a failure is a log line rather than a stopped tunnel.
- **Task 6 grew a third place**: `WhatsappBridge/.env.example`, which the plan
  did not name.
- **The live-publish check compares rows, not bytes.** The first publish
  legitimately moves the top-level `updatedAt` forward - the top of the file
  records the newest row - so a byte comparison fails a correct run. It is now
  compared row by row, with the top-level pair.

### The deployment

- Project `whatsapp-server-forwp`, instance `whatsapp-main-server-wp`, zone
  `us-central1-a`, e2-micro, static external address `34.9.230.208`.
- Firewall rule `whatsappforwp-registry` for `tcp:8787` from `0.0.0.0/0`,
  targeting the tag `whatsappforwp-registry`, which was added to the instance.
- `gcloud compute scp` then `setup.sh` as root: Node 22.23.3 into `/opt/node`
  with `/usr/local/bin/node` pointing at it, `/etc/whatsappforwp-registry.env`
  at mode 600, the unit enabled and active.
- Verified from outside the VM: `GET /health` answers `{"ok":true,...}`, a
  report without the secret is `401`, a malformed row is `400`.
- Both containers report to it: the adapter logs `[endpoint] reported
  192.168.0.108:8585 as nas to http://34.9.230.208:8787`, the tunnel logs
  `[publish] bore.pub:41417 is live as public in http://34.9.230.208:8787`.
- The VM published with its own token: the live `endpoint.json` keeps `public`
  first with the top-level pair mirroring it and `tls`/`fingerprint` carried
  over, `endpoint.md` was regenerated, and the file still parses with the
  adapter's `parseRegistry` (two rows, `public` first).
- A restart re-read both rows from `/var/lib/whatsappforwp-registry/registry.json`
  and published nothing: the heartbeat is not a commit.
- The NAS `.env` gained `ENDPOINT_REGISTRY_URL` and `ENDPOINT_REGISTRY_SECRET`
  (backup `.env.bak-20261006-registry`). `GH_TOKEN` stays for a rollback but is
  no longer used by either container.

### A gap found after the deployment, and fixed

The tunnel published its address once, when bore gave it one, and the registry
drops a row nobody has confirmed within `REGISTRY_TTL_MINUTES` - a day. A tunnel
that stays up for a day is the normal case, so the `public` row would have
vanished by tomorrow and taken the app's only public address with it. The
adapter was never at risk: it re-reports every `ENDPOINT_PUBLISH_MINUTES`.

`tunnel-entrypoint.sh` now runs a small loop that says the same address again
every `ENDPOINT_REFRESH_MINUTES` (6 hours), killed and restarted with the tunnel
it belongs to. A repeat costs nothing: the registry writes the file only when
something really changed, so a refresh is not a commit. Pinned in two places -
`registry/service.test.js` for "a second identical report does not write", and
`tools/tunnel-refresh-check.sh` in the Docker repository for the loop itself (it
repeats, it stops at `0`, it survives a value that is not a number).

### Left open

- `gcloud` needed the requester for the browser sign-in. That was the only step
  that did; everything after it ran from this machine.
- `gcloud compute scp`/`ssh` fell back to PuTTY's `pscp`/`plink`, which cannot
  accept a host key without a prompt: the key was seeded with `ssh-keyscan`, the
  PuTTY directory dropped from `PATH`, and the Windows `scp`/`ssh` used directly.
- A publish that changes the file is two commits, because the Contents API
  writes `endpoint.json` and `endpoint.md` one at a time. Writes only happen on a
  real change, so that is the cost of a change and not of a heartbeat.
- The token was pasted into the chat and now lives in the VM's environment file;
  it should be rotated.
