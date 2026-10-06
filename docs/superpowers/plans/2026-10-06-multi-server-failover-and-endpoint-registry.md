# Multi-server failover and endpoint registry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the app keep working when one server goes down by probing an ordered list of servers and connecting to the first that answers, have every server publish its current address into a shared registry, and keep the chats on the phone across any server.

**Architecture:** The `whatsappforwp-endpoint` repository's `endpoint.json` grows a `servers` array (top-level `host`/`port` kept for compatibility). Every server upserts its own entry into that array. The app parses the list, caches it, and `AutoConnector` probes each candidate in order before falling back to LAN discovery. The chats are already device-local (`ChatCache`, `MessageCache`, `ChatPreferences`) and are never cleared on reconnect; servers must share `BRIDGE_KEY` and accept the phone's device token.

**Tech Stack:** C# 5 (WP8.1), Node.js 18+ (adapter, zero runtime dependencies), POSIX shell (tunnel container).

**Spec:** the user's request (2026-10-06): *the Docker server can be configured as another server for the app, so if one goes down it keeps working by connecting automatically (by ping) to another server; the chats stay on any server; and each server publishes its updated IP to the repo so its address stays current.*

## Global Constraints

- C# **5** only: no `?.`, `$"..."`, `nameof`, pattern matching, auto-property initializers, `out var`, `await` inside `catch`.
- The adapter is **dependency-free**: `package.json` has zero runtime dependencies; tests use `node:test`.
- Runtime text is **English**; UI strings live in `Strings/<lang>/Resources.resw` in **both** languages; docs are written in **pairs** (EN + IT) in the same commit.
- A new adapter file must be mirrored into `vincenzosco/docker-whatsappforwp` via its `tools/sync.js`.
- Frame length is little-endian, 4-byte prefix, `1..8 MiB` (`FrameCodec` on the app, `MAX_FRAME_LENGTH` on the adapter): not touched by this plan.
- `endpoint.json` must stay readable by the **old** app: keep top-level `host`, `port`, `updatedAt`, `tls`, `fingerprint` always present.

## Review Focus

- **A registry with one entry still works** (the common case): `servers` absent, or a single-element array, must connect as before.
- **A registry entry whose host resolves to nothing** must cost one 6 s probe and let the next candidate run, not abort the whole attempt.
- **Two entries for the same host:port** must be probed once, not twice.
- **A malformed `servers` array** (a string, an entry missing `port`) must be ignored entry-by-entry, not throw and lose the whole list.
- **The adapter publishing with no token / no repo** must log and continue, never crash the server.

---

### Task 1: The registry format (Node module)

**Files:**
- Create: `WhatsappBridge/endpoint-registry.js`
- Test: `WhatsappBridge/test/endpoint-registry.test.js`

**Interfaces:**
- Produces:
  - `parseRegistry(textOrObject) -> { updatedAt, host, port, tls, fingerprint, servers: Server[] }` where `Server = { id, name, host, port, updatedAt }`. Never throws; unknown/invalid fields become `''`, `/`, `0`.
  - `upsertServer(registry, entry, options) -> registry`, `entry = { id, name, host, port, updatedAt }`, `options = { preferred?: boolean, now?: string }`. Replaces by `id` or appends; with `preferred` moves the entry first. Always keeps top-level `host`/`port` equal to `servers[0]`.
  - `serializeRegistry(registry) -> string` (2-space JSON, trailing newline).

- [ ] **Step 1: Write the failing tests** — cover: parse of the legacy single-endpoint file; parse of the list file; a `servers` value that is a string is ignored (falls back to top-level); an entry with no `port` is dropped; upsert replaces by `id`; upsert appends a new `id`; `preferred` moves it first and rewrites top-level; `serialize` round-trips.
- [ ] **Step 2: Run** `cd WhatsappBridge && node --test test/endpoint-registry.test.js` — expect FAIL (module missing).
- [ ] **Step 3: Implement** the four functions with the shapes above.
- [ ] **Step 4: Run** the test — expect PASS.
- [ ] **Step 5: Commit** `feat: define the server registry format`.

### Task 2: Each server publishes its own address (adapter)

**Files:**
- Modify: `WhatsappBridge/config.js` (new `endpoint` block + `DEFAULTS` keys)
- Create: `WhatsappBridge/endpoint-publisher.js`
- Modify: `WhatsappBridge/server.js` (publish at startup)
- Test: `WhatsappBridge/test/endpoint-publisher.test.js`

**Interfaces:**
- Consumes: `endpoint-registry.parseRegistry/upsertServer/serializeRegistry` (Task 1).
- Produces:
  - `detectLocalAddress(interfaces) -> string` — first non-internal IPv4, skipping the virtual interfaces `discovery.js` already lists.
  - `createEndpointPublisher({ endpoint, log, fetchImpl, now, interfaces }) -> { publish() }`; `endpoint` is `config.endpoint` with `{ publish, repo, token, id, name, host, port }`. `publish()` reads `https://api.github.com/repos/<repo>/contents/endpoint.json`, upserts `{ id, name, host: host || detectLocalAddress(), port: port || bridgePort }`, writes it back. **Never throws**: a missing token/repo logs `[endpoint] ...` and returns. `publish()` returns a Promise resolving to `true` when written, `false` otherwise.
- `config.endpoint` = `{ publish: bool, repo, token, id, name, host, port: int }`.

- [ ] **Step 1: Write the failing tests** — `detectLocalAddress` picks the real IPv4 and skips `docker0`/`lo`; `publish` with no token resolves `false` and logs; `publish` with an injected `fetchImpl` sends the merged `servers` list and the write body carries the entry.
- [ ] **Step 2: Run** — expect FAIL.
- [ ] **Step 3: Implement** config keys, the module, and the startup call in `server.js` guarded by `config.endpoint.publish`.
- [ ] **Step 4: Run** the test — expect PASS; then `npm test` (whole suite).
- [ ] **Step 5: Commit** `feat: publish the server address into the registry at startup`.

### Task 3: The tunnel publishes into the list (Docker mirror)

**Files:**
- Modify: `docker/publish-endpoint.sh` (upsert by `ENDPOINT_SERVER_ID`, keep top-level)
- Modify: `docker-compose.nas.yaml` (pass `ENDPOINT_SERVER_ID`, `ENDPOINT_SERVER_NAME`)
- Modify: `.env.example`, `README.md`, `README.it.md` (the tunnel section)

**Interfaces:**
- Produces: `endpoint.json` whose `servers` contains this tunnel's entry with id `${ENDPOINT_SERVER_ID:-public}`, and whose top-level `host`/`port` are this tunnel's (preferred).

- [ ] **Step 1:** Extend the script to read the current file, drop the entry with the same `id`, prepend this one, and write both `servers` and the top-level fields. Keep the `manual()` fallback.
- [ ] **Step 2:** Add the two env vars to the compose file and `.env.example`, and document them in both Docker READMEs.
- [ ] **Step 3:** Commit in the mirror repository.

### Task 4: The app reads a list of servers

**Files:**
- Modify: `WhatsappApp/Models/DiscoveredServer.cs` (add `Id`)
- Modify: `WhatsappApp/Services/EndpointService.cs`
- Guard: `node tools/check-csharp5.js` (the build proves the rest)

**Interfaces:**
- Produces:
  - `DiscoveredServer.Id` (`string`, default `""`).
  - `EndpointService.ResolveAllAsync() -> Task<List<DiscoveredServer>>` — the fetched list, else the cached list, else an empty list; caches the list in `LocalSettings` under `EndpointServers` as JSON.
  - `EndpointService.ResolveAsync()` unchanged in signature: the first of the list.
  - `EndpointService.CachedAll() -> List<DiscoveredServer>`.

- [ ] **Step 1:** Add `Id`; add `ResolveAllAsync`/`CachedAll`; `FetchAsync` returns a list parsing `servers` (falling back to top-level `host`/`port`), skipping entries whose host is empty or whose port is outside `1..65535`.
- [ ] **Step 2:** `node tools/check-csharp5.js` — expect OK.
- [ ] **Step 3:** Commit `feat: read every server from the endpoint registry`.

### Task 5: The app fails over between servers

**Files:**
- Modify: `WhatsappApp/Services/AutoConnector.cs`

**Interfaces:**
- Consumes: `EndpointService.ResolveAllAsync` (Task 4), `CommunicationService.ConnectToServerAsync`, `DiscoveryService`.
- Produces: `AutoConnector.TryConnectAsync(username, discoverySeconds)` — builds an ordered, de-duplicated candidate list (registry when `UsePublicServer`, then the saved address), probes each with the existing 6 s deadline, saves the winner, and only then falls back to LAN discovery; raises `ServerUnavailable` exactly once at the end if nothing answered.

- [ ] **Step 1:** Rewrite `TryConnectAsync` with the candidate loop and a private `AddCandidate` that de-duplicates by address **and** port.
- [ ] **Step 2:** `node tools/check-csharp5.js` — expect OK.
- [ ] **Step 3:** ARM build gate (below).
- [ ] **Step 4:** Commit `feat: fail over to another server when one does not answer`.

### Task 6: Documentation

**Files:**
- Modify: `README.md`, `README.it.md` (the "Shared server and the public endpoint" section)
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md` (the config table + the registry)
- Append a "What execution changed about this plan" section to this file.

- [ ] **Step 1:** Document: the registry list, automatic probing and failover, the LAN fallback, and the requirement that every server share `BRIDGE_KEY` and accept the phone's device token (same `users.json` secret, or `AUTH_REGISTER=on`) for the chats to follow the phone.
- [ ] **Step 2:** `node tools/check-docs.js` — expect OK.
- [ ] **Step 3:** Commit `docs: multi-server failover and the shared registry`.

### Task 7: Verification and push

- [ ] **Step 1:** The eleven static guards + `node --test "tools/test/**/*.test.js"` (known 4 unrelated `zip ENOENT` failures).
- [ ] **Step 2:** `cd WhatsappBridge && npm test`.
- [ ] **Step 3:** ARM Debug rebuild (`MSYS_NO_PATHCONV=1 ... /t:Rebuild /p:Platform=ARM`); restore `Package.appxmanifest`, never the `.csproj`.
- [ ] **Step 4:** `git push`; mirror the adapter into `docker-whatsappforwp` with `tools/sync.js`, commit, push.

## What execution changed about this plan

- **The tunnel script grew a `jq` dependency.** Preserving the other servers'
  rows cannot be done with the tools the tunnel image had (sed cannot edit JSON
  reliably), so `Dockerfile.tunnel` now installs `jq` and `publish-endpoint.sh`
  merges with it. The merge was verified on the NAS in a throwaway Alpine
  container: a republish updated the `public` row, kept the `nas-lan` row, and
  rewrote the top-level pair.
- **A real bug was caught by that verification.** The first draft used
  `${current:-{}}`; bash closes the parameter expansion on the first brace and
  appends the second, feeding jq an extra `}`. The script now normalises
  `current` to `{}` with an explicit `if`.
- **The server-id and interval names settled as:** `ENDPOINT_SERVER_ID`,
  `ENDPOINT_SERVER_NAME`, `ENDPOINT_PUBLISH_MINUTES`, `ENDPOINT_TOKEN` (falling
  back to `GH_TOKEN`). They are wired in `config.js`, `.env.example`,
  `docker-compose.nas.yaml` and `Dockerfile.tunnel`.
- **The app's JSON reading needed two compile fixes:** on the WP8.1 projection
  `JsonArray` has no `GetAt` and its indexer returns `IJsonValue`, not
  `JsonValue`. `EndpointService` uses `array[i]` with an `IJsonValue` variable.
- **`ResolveAsync()` was kept** as the first of `ResolveAllAsync()`, because the
  pairing screen opens one socket of its own and needs a single address.
- **Chat continuity needed no code change.** The caches are already device-local
  and are never cleared on reconnect, and `ChatsPage` re-requests the list on the
  `state` frame of whichever server answers; the work was documentation plus the
  shared `BRIDGE_KEY`/token note.
