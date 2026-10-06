# Server Publishing via gh and Ping Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let any server publish its own address into the shared registry through the GitHub CLI (`gh`) when it has no token, and make the app measure every server and connect to the fastest one.

**Architecture:** The adapter already writes its row into `whatsappforwp-endpoint/endpoint.json` through the GitHub Contents API with a token, and the tunnel writes the public row with `gh`. We add a second transport to the adapter's publisher: with no token configured it shells out to `gh api`, so a machine that has run `gh auth login` publishes with no secret stored anywhere. In the app, a new `ServerPinger` times one TCP connection per candidate; `AutoConnector` measures all candidates in parallel, orders them by latency and then keeps the existing connect loop, so the fastest reachable server wins instead of the first listed.

**Tech Stack:** C# 5 (Windows Phone 8.1), Node.js 18 with zero runtime dependencies (`node:test` tests), POSIX shell (`gh` CLI on the server), Markdown docs in EN/IT pairs.

**Spec:** the user's request (2026-10-06), quoted verbatim:

> per il server, ci serve un modo che il server possa in automatico pubblicare su un altro/lo stesso repo, la lista del suo ip e degli altri server, vedi come fare tu da gh e aggiorna i readme per spiegare in modo dettagliato ma semplice, inoltre l'app deve scegliere il server secondo il suo pin

Resolved with the user before writing this plan: **"pin" = ping (latency)**; the app **measures all servers and connects to the fastest**; the publishing mechanism is the author's choice, and it is the `gh` transport below.

## Global Constraints

- C# **5** only: no `?.`, `$"..."`, `nameof`, pattern matching, auto-property initializers, `out var`; no `await` inside `catch`/`finally`.
- The adapter is **dependency-free**: `package.json` has zero runtime dependencies; tests use `node:test`. `gh` is an OS binary reached through `child_process`, not a package.
- Every `.cs` under `WhatsappApp` must be a `<Compile Include>` in `WhatsappApp.csproj` (`node tools/check-project-files.js` fails otherwise).
- Docs are written in **pairs**, EN and IT, in the same commit (`node tools/check-docs.js` enforces the pair and the no-emoji rule; the only emoji allowed is U+26A0).
- Italian it-IT strings use `e'`, not `è`. Runtime text is English.
- A changed adapter file must be mirrored into `vincenzosco/docker-whatsappforwp` with `node tools/sync.js --from <WhatsappForWP checkout>`.
- `endpoint.json` must stay readable by the old app: top-level `host`, `port`, `updatedAt`, `tls`, `fingerprint` always present and equal to `servers[0]`.
- Gate before any commit that touches code: the eleven guards (`node tools/check-*.js` chain), `cd WhatsappBridge && npm test`, and the ARM Debug rebuild.
- No apostrophes in commit messages.

## Review Focus

1. **One-candidate setups must not pay for ranking.** A private server (public switch off) yields a single candidate; the extra TCP probe must be skipped, so nothing changes for the common case. *Pinned by Task 3, Step 5.*
2. **Nothing answers at all.** Every probe fails and every connect fails: LAN discovery must still run and `ServerUnavailable` must be raised exactly once, exactly as today. *Pinned by Task 3, Step 9.*
3. **The old/one-row registry.** A legacy single-address `endpoint.json`, or a list with one row, must resolve exactly as before (the ping path is never entered with one candidate). *Pinned by Task 3, Step 6.*
4. **A fast but mute server.** The ping measures TCP only, so a server that accepts the connection but never answers the handshake can win the ranking; the connect must still fail over to the next candidate, and the watchdog must still drop it. *Pinned by Task 3, Step 9.*
5. **`gh` missing, logged out, or without write access — with no token.** Publishing must warn and return `false`; the server keeps serving and never crashes. *Pinned by Task 1, Step 1 (third test).*

---

## File Structure

| File | Change | Responsibility |
|------|--------|----------------|
| `WhatsappBridge/endpoint-publisher.js` | modify | Choose the transport (token→API, else `gh`); read/merge/write the registry through it |
| `WhatsappBridge/test/endpoint-publisher.test.js` | modify | Pin the transport choice and the merge through `gh` |
| `WhatsappApp/Services/ServerPinger.cs` | create | Time one TCP connection to an address, return ms or `NoAnswer` |
| `WhatsappApp/Services/AutoConnector.cs` | modify | Measure all candidates, order by latency, keep the connect loop |
| `WhatsappApp/WhatsappApp.csproj` | modify | List the new `.cs` so MSBuild sees it |
| `README.md`, `README.it.md` | modify | How a server publishes (token or `gh`) and how the app picks the fastest |
| `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`, `WhatsappBridge/.env.example` | modify | The adapter config and the two publishing transports |
| `docker-whatsappforwp` (`README.md`, `README.it.md`, `.env.example`, `server/`) | modify | Mirror the adapter, document `gh` publishing in the deployment |
| `whatsappforwp-endpoint` (`publish.js`, `README.md`) | modify | The by-hand path must keep the `servers` list instead of wiping it |

Tasks 5 and 6 touch different repositories; their commits land there.

---

### Task 1: The adapter publishes with `gh` when there is no token

**Files:**
- Modify: `WhatsappBridge/endpoint-publisher.js` (require block ~line 24; `contentsUrl` line 71; `headers` line 76; `readCurrent` line 86; `publish` line 99; `start` line 159; exports line 170)
- Test: `WhatsappBridge/test/endpoint-publisher.test.js`

**Interfaces:**
- Produces: `createEndpointPublisher({ endpoint, bridgePort, log, fetchImpl, execImpl, now, interfaces }) -> { publish(): Promise<boolean>, start(): Timeout }`.
- Transport rule (decided): `endpoint.token` non-empty → GitHub Contents API through `fetchImpl` (unchanged behaviour); empty → `gh` through `execImpl`.
- `execImpl(command, args) -> string` (stdout). Default: `(command, args) => execFileSync(command, args, { encoding: 'utf8' })`.
- The `gh` invocations are fixed; use exactly these:
  - read: `gh api repos/<repo>/contents/endpoint.json` → stdout is the Contents-API JSON (`content` base64, `sha`).
  - write: `gh api --method PUT repos/<repo>/contents/endpoint.json -f message=<message> -f content=<base64>` plus `-f sha=<sha>` when a sha was read.
  - `<repo>` is `endpoint.repo || DEFAULT_REPO`.
- The merge stays in `endpoint-registry.js`: `upsertServer(current.text, { id, name, host, port }, { now })` then `serializeRegistry`.

- [ ] **Step 1: Write the failing tests**

Append to `WhatsappBridge/test/endpoint-publisher.test.js` (`encode` already exists in the file):

```javascript
test('publish senza token usa gh e fonde la lista', async () => {
  const existing = JSON.stringify({
    host: 'bore.pub',
    port: 41417,
    servers: [{ id: 'public', name: 'Public server', host: 'bore.pub', port: 41417 }],
  });

  const calls = [];
  const publisher = createEndpointPublisher({
    endpoint: {
      publish: true, repo: 'me/repo', token: '',
      serverId: 'nas', serverName: 'NAS', host: '192.168.0.108', port: 8585,
    },
    log: () => {},
    now: () => '2026-10-06T12:00:00.000Z',
    execImpl: (command, args) => {
      calls.push({ command, args });
      if (args.indexOf('--method') !== -1) return '';
      return JSON.stringify({ sha: 'abc', content: encode(existing) });
    },
  });

  assert.strictEqual(await publisher.publish(), true);

  const put = calls.find((c) => c.args.indexOf('--method') !== -1);
  assert.ok(put, 'una scrittura e stata fatta');
  assert.strictEqual(put.command, 'gh');
  const contentArg = put.args.find((a) => a.indexOf('content=') === 0);
  const written = JSON.parse(Buffer.from(contentArg.slice('content='.length), 'base64').toString('utf8'));
  assert.strictEqual(written.host, 'bore.pub', 'il pubblico resta il preferito');
  assert.strictEqual(written.servers.length, 2);
  assert.strictEqual(written.servers[1].id, 'nas');
  assert.strictEqual(written.servers[1].host, '192.168.0.108');
  assert.ok(put.args.indexOf('sha=abc') !== -1, 'lo sha letto viene rimandato');
});

test('publish senza token e senza gh avvisa e non pubblica', async () => {
  const seen = [];
  const publisher = createEndpointPublisher({
    endpoint: { publish: true, repo: 'me/repo', token: '', serverId: 'nas', host: '10.0.0.5', port: 8585 },
    log: (level, message) => seen.push(level + ' ' + message),
    execImpl: () => {
      const err = new Error('spawn gh ENOENT');
      err.code = 'ENOENT';
      throw err;
    },
  });

  assert.strictEqual(await publisher.publish(), false);
  assert.ok(seen.some((line) => line.indexOf('not published') !== -1));
});

test('publish con token usa l API e non gh', async () => {
  const calls = [];
  const publisher = createEndpointPublisher({
    endpoint: { publish: true, repo: 'me/repo', token: 'tok', serverId: 'pc', host: '10.0.0.5', port: 8585 },
    log: () => {},
    execImpl: () => { throw new Error('must not be called'); },
    fetchImpl: async (url, options) => {
      calls.push({ url, options });
      if (options && options.method === 'PUT') return jsonResponse(200, {});
      return jsonResponse(404, { message: 'Not Found' });
    },
  });

  assert.strictEqual(await publisher.publish(), true);
  assert.strictEqual(calls.length, 2);
});
```

Then **update the existing test** `publish senza token avvisa e non pubblica` (line 32): it currently passes `token: ''` and no `execImpl`, so with the new transport it would run the real `gh`. Give it the same throwing `execImpl` as the test above, so it never touches the network.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd WhatsappBridge && node --test test/endpoint-publisher.test.js`
Expected: FAIL — the `gh` tests get a read without `execImpl`, or the write arguments are the API's, not `gh`'s.

- [ ] **Step 3: Implement the two transports in `endpoint-publisher.js`**

Add `const { execFileSync } = require('child_process');` beside `require('os')`. In `createEndpointPublisher` add `const execImpl = opts.execImpl || ((command, args) => execFileSync(command, args, { encoding: 'utf8' }));`.

Refactor `contentsUrl`/`headers`/`readCurrent` into transport-named helpers and make `publish()` pick one:
- token set → API read (`readViaApi(repo) -> { text, sha }`, existing logic) and API write (`writeViaApi(repo, body) -> boolean`, existing PUT).
- token empty → `readViaGh(repo) -> { text, sha }` (run the read command, `JSON.parse` the stdout; any throw → `{ text: '', sha: '' }`) and `writeViaGh(repo, body) -> boolean` (run the write command; a throw → `false`).

Keep `publish()`'s existing guards, the host/port resolution, the `upsertServer` + `serializeRegistry` body (base64), the `sha` field, the OK/WARN log lines and the "never throws" contract. With no token and no `gh`, the read throws and becomes `{ text: '', sha: '' }`, the write throws and returns `false`, and the final WARN must contain `not published`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd WhatsappBridge && node --test test/endpoint-publisher.test.js && npm test`
Expected: PASS for the file; `npm test` reports 288 tests, 288 pass (285 + 3).

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/endpoint-publisher.js WhatsappBridge/test/endpoint-publisher.test.js
git commit -m "feat: publish the registry with gh when the server has no token"
```

---

### Task 2: Document the two publishing transports (adapter)

**Files:**
- Modify: `WhatsappBridge/README.md` (config table lines 182-189; section "The server registry" line 332)
- Modify: `WhatsappBridge/README.it.md` (same two places)
- Modify: `WhatsappBridge/.env.example` (the `ENDPOINT_*` block, lines 91-113)

**Interfaces:** none (docs only). Produces the operator-facing facts Task 1 relies on.

- [ ] **Step 1: Add the `gh` transport to both READMEs**

In the config table next to `ENDPOINT_TOKEN`, state: empty falls back to `GH_TOKEN`; **with neither set, the adapter publishes through the GitHub CLI** (`gh api`), so a machine that has run `gh auth login` needs no token at all. `.env.example` gets the same note as a comment above `ENDPOINT_TOKEN`, and in the sentence above `GH_TOKEN`.

In "The server registry" (EN) / "Il registro dei server" (IT), after the paragraph that describes `ENDPOINT_PUBLISH=on`, add one short paragraph: the adapter uses the API when a token is set, otherwise `gh`; the repository is `ENDPOINT_REPO` (`vincenzosco/whatsappforwp-endpoint` by default), which may point at another repository or a fork, and two servers must not share `ENDPOINT_SERVER_ID`.

- [ ] **Step 2: Check the docs pair, then commit**

Run: `node tools/check-docs.js`
Expected: `OK: 2 doc pair(s) ...` (no emoji, pairs closed).

```bash
git add WhatsappBridge/README.md WhatsappBridge/README.it.md WhatsappBridge/.env.example
git commit -m "docs: record how a server publishes without a token, on gh"
```

---

### Task 3: The app picks the fastest server

**Files:**
- Create: `WhatsappApp/Services/ServerPinger.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj` (after line 109, `Services\EndpointService.cs`)
- Modify: `WhatsappApp/Services/AutoConnector.cs` (between the saved-address block that ends at line 84 and the `Diag.Ok("connecting: ...")` line)

**Interfaces:**
- Produces: `ServerPinger.MeasureAsync(string address, int port) -> Task<int>` — milliseconds, or `ServerPinger.NoAnswer` (`public const int NoAnswer = -1`) when the connection does not answer within `CommunicationService`'s existing deadline.
- Produces: `AutoConnector.TryConnectAsync(string username, int discoverySeconds) -> Task<bool>` (existing signature, unchanged).
- Consumes: `CommunicationService.ConnectWithDeadlineAsync(StreamSocket, HostName, int)` (internal, same assembly), `ServerPinger`, `Diag.Ok`, `SettingsService`, `DiscoveryService` (all existing).

- [ ] **Step 1: Create `WhatsappApp/Services/ServerPinger.cs` with the failing guard visible**

```csharp
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;

namespace WhatsappApp.Services
{
    /// <summary>
    /// How fast a server answers, measured on the one thing the app can time
    /// without speaking the frame protocol: the TCP connection. The socket is
    /// opened and closed, and the answer is the milliseconds it took, or
    /// NoAnswer when it did not answer within the same deadline every other
    /// connection of the app uses. Nothing here throws: an unreachable server
    /// is a low score, not a failure.
    /// </summary>
    public static class ServerPinger
    {
        /// <summary>The score of a server that did not answer.</summary>
        public const int NoAnswer = -1;

        public static async Task<int> MeasureAsync(string address, int port)
        {
            var socket = new StreamSocket();
            var watch = Stopwatch.StartNew();
            try
            {
                await CommunicationService.ConnectWithDeadlineAsync(
                    socket, new HostName(address), port);
                watch.Stop();
                return (int)watch.ElapsedMilliseconds;
            }
            catch (Exception)
            {
                return NoAnswer;
            }
            finally
            {
                try { socket.Dispose(); }
                catch (Exception) { }
            }
        }
    }
}
```

- [ ] **Step 2: Run the project-files guard to verify it fails**

Run: `node tools/check-project-files.js`
Expected: FAIL, naming `WhatsappApp/Services/ServerPinger.cs` as not listed in the project.

- [ ] **Step 3: Add the file to the project**

In `WhatsappApp/WhatsappApp.csproj`, after `<Compile Include="Services\EndpointService.cs" />`: `<Compile Include="Services\ServerPinger.cs" />`.

- [ ] **Step 4: Run the guard and the C# 5 check to verify they pass**

Run: `node tools/check-project-files.js && node tools/check-csharp5.js`
Expected: both OK.

- [ ] **Step 5: Rank the candidates in `AutoConnector.TryConnectAsync`**

After the saved-address block and before the `Diag.Ok("connecting: ...")` line, insert:

```csharp
                // One candidate needs no ranking: measuring it would add one
                // connection to the common private-server case for nothing.
                if (candidates.Count > 1)
                {
                    await RankByLatencyAsync(candidates);
                }
```

Add the private method next to `AddCandidate`:

```csharp
        /// <summary>
        /// Orders the candidates by how fast they answer, fastest first. A server
        /// that did not answer keeps a place at the end of the list instead of
        /// being dropped: the ping is a hint, the connection is the judge, and a
        /// lost probe must not lose a server.
        /// </summary>
        private static async Task RankByLatencyAsync(List<DiscoveredServer> candidates)
        {
            var probes = new Task<int>[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
            {
                probes[i] = ServerPinger.MeasureAsync(candidates[i].Address, candidates[i].Port);
            }
            int[] latencies = await Task.WhenAll(probes);

            var text = new System.Text.StringBuilder("ping: ");
            for (int i = 0; i < latencies.Length; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(candidates[i].Endpoint).Append("=");
                text.Append(latencies[i] == ServerPinger.NoAnswer
                    ? "no answer" : latencies[i] + " ms");
            }
            Diag.Ok(text.ToString());

            var ranked = new List<DiscoveredServer>();
            var taken = new bool[candidates.Count];
            while (ranked.Count < candidates.Count)
            {
                int best = -1;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (taken[i]) continue;
                    if (best == -1)
                    {
                        best = i;
                        continue;
                    }
                    int left = latencies[i] == ServerPinger.NoAnswer ? int.MaxValue : latencies[i];
                    int right = latencies[best] == ServerPinger.NoAnswer ? int.MaxValue : latencies[best];
                    // Strictly smaller, so equal scores keep the registry order.
                    if (left < right) best = i;
                }
                taken[best] = true;
                ranked.Add(candidates[best]);
            }

            candidates.Clear();
            candidates.AddRange(ranked);
        }
```

`System.Text` and `System.Threading.Tasks` are already imported in `AutoConnector.cs`; use the fully-qualified `System.Text.StringBuilder` as shown if not.

- [ ] **Step 6: Guard check for the common case**

Re-read the method: with `candidates.Count <= 1`, `RankByLatencyAsync` is never called, so a private single-server setup opens exactly the connections it opened before. Confirm by reading the method top-to-bottom; this is the Review Focus 1 and 3 pin.

- [ ] **Step 7: Run the gates**

Run: `node tools/check-csharp5.js && node tools/check-fire-and-forget.js && node tools/check-project-files.js`
Expected: all OK.

- [ ] **Step 8: Build for ARM**

Run: `MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/Bin/MSBuild.exe" WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=ARM /v:m`
Expected: ends with `Your package has been successfully created.` Then restore the generated manifest: `git checkout -- WhatsappApp/Package.appxmanifest` (never the `.csproj`).

- [ ] **Step 9: Read the failure paths**

Re-read `RankByLatencyAsync` and the connect loop below it. Confirm: all probes failing leaves every candidate with `NoAnswer` in the original order; the loop still fails over one by one; when every connect returns false the existing discovery fallback runs and `NotifyServerUnavailable()` is called once. This is the Review Focus 2 and 4 pin.

- [ ] **Step 10: Commit**

```bash
git add WhatsappApp/Services/ServerPinger.cs WhatsappApp/Services/AutoConnector.cs WhatsappApp/WhatsappApp.csproj
git commit -m "feat: measure every server and connect to the fastest"
```

---

### Task 4: Document the ping selection (root READMEs)

**Files:**
- Modify: `README.md` (section "Shared server and the public endpoint", lines 556-628)
- Modify: `README.it.md` (same section)

**Interfaces:** none (docs only).

- [ ] **Step 1: Describe the choice in both languages**

In the paragraph that explains what `AutoConnector` does (EN line 595 / the IT equivalent), state, simply: the app reads every address in the registry, pings them **at the same time**, and connects to the fastest that answers; if none answers it falls back to the single server found on the network. Keep the existing "keep working when a server goes down" sentence. Mention that with only one server there is no ranking and nothing changes.

- [ ] **Step 2: Check the docs pair, then commit**

Run: `node tools/check-docs.js`
Expected: OK.

```bash
git add README.md README.it.md
git commit -m "docs: the app now connects to the server that answers fastest"
```

---

### Task 5: Mirror the adapter and document gh publishing in the deployment

**Files:**
- Modify (mirror `docker-whatsappforwp`): `server/` (via `tools/sync.js`), `README.md`, `README.it.md`, `.env.example`

**Interfaces:**
- Consumes: Task 1's `endpoint-publisher.js` (the mirror copies it verbatim).

- [ ] **Step 1: Sync the adapter into the mirror**

Run (in the mirror checkout): `node tools/sync.js --from "<path to the WhatsappForWP checkout>"`
Expected: `copied N file(s) ...` and `server/SOURCE_COMMIT` updated to Task 1's commit.

- [ ] **Step 2: Document the adapter's `gh` transport**

In the mirror `README.md` / `README.it.md`, in the section that explains the server registry and "More than one server": add that the adapter publishes its own row with a token **or**, with no token, through the `gh` CLI already installed in the tunnel image — so a deployment can publish both rows from one `gh auth`. In `.env.example`, add the matching comment beside `ENDPOINT_TOKEN` / `GH_TOKEN`.

- [ ] **Step 3: Verify the copy is reproducible, then commit and push**

Run: `node tools/sync.js --check --from "<path to the WhatsappForWP checkout>"`
Expected: reports the copy is up to date (exit 0).

```bash
git add server README.md README.it.md .env.example
git commit -m "Mirror the adapter that publishes with gh and document it"
git push
```

---

### Task 6: The by-hand publisher keeps the list

**Files:**
- Modify (repo `whatsappforwp-endpoint`): `publish.js`, `README.md`

**Interfaces:**
- Produces: `publish.js` gains `--id <id>` and `--name <name>` (defaults `public` / `Public server`) and, instead of writing only the top-level pair, it keeps the existing `servers` list from the `endpoint.json` on disk, replaces the row whose `id` matches, and writes the top-level pair equal to `servers[0]`. `parseArgs` gains `id`/`name`; `build` gains the id/name; a new `merge(existingText, entry) -> registry` reads the current file (`fs.readFileSync`, `{}` when absent) and returns the same shape the adapter writes.
- The output must remain readable by the adapter and the app: `updatedAt`, `host`, `port`, `tls`, `fingerprint`, `servers`.

- [ ] **Step 1: Make the merge preserve the list**

Implement `merge` in `publish.js` (it cannot import `endpoint-registry.js`: different repository). Rules: parse the file or start from `{}`; normalise each row to `{ id, name, host, port, updatedAt }`, dropping rows without a host or with a port outside 1..65535; drop the row with this `id` and prepend this one; set `updatedAt`, `tls`, `fingerprint` on the top level and `host`/`port` from the first row. Route `write` and `renderMd` through it, and add the id/name to the usage comment.

- [ ] **Step 2: Verify by hand**

Run in the endpoint checkout, with the current file in place:
`node publish.js --id nas --name NAS --host 192.168.0.108 --port 8585`
Expected: the printed JSON has two `servers` (the existing `public` row kept, the `nas` row updated in place) and a top-level `host`/`port` equal to the first row. Then `git checkout -- endpoint.json endpoint.md` to undo. Do **not** pass `--commit` in this step.

- [ ] **Step 3: Document it, then commit and push**

In `README.md`, replace the "Publishing the current address" examples with the `--id`/`--name` form and state that the file is a list, that running it without an id updates the `public` row, and that the other servers' rows are preserved.

```bash
git add publish.js README.md endpoint.json endpoint.md
git commit -m "Keep the servers list when publishing by hand"
git push
```

---

## Self-Review

- **Spec coverage:** publishing automatically (Task 1, `gh` transport), to another or the same repo (`ENDPOINT_REPO`, documented in Task 2), the list of its IP and the other servers (the merge in Task 1 and Task 6), detailed-but-simple READMEs (Tasks 2, 4, 5, 6), the app choosing by ping (Task 3, docs in Task 4). Every requirement has a task.
- **Review Focus pins:** 1 and 3 → Task 3 Steps 5-6; 2 and 4 → Task 3 Step 9; 5 → Task 1 Step 1 (third test).
- **Type consistency:** `ServerPinger.MeasureAsync`/`NoAnswer` are named once and used only in Task 3; the transport helpers (`readViaApi`/`writeViaApi`/`readViaGh`/`writeViaGh`, `execImpl`) are named once, in Task 1.
- **Proportion:** the only large code blocks are the three tests, the `gh` command shapes the implementer cannot guess, and the ranking algorithm the tests do not determine. Everything else is signatures and file paths.
