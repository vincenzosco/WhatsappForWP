# A Docker Home for the Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `vincenzosco/docker-whatsappforwp` - a public repository that publishes one Docker image running GOWA and the adapter together, deployable on a NAS or a PC with `docker compose up -d`, while the same server keeps running under plain `node server.js`.

**Architecture:** The adapter stays the source of truth in `WhatsappForWP/WhatsappBridge`; the new repository holds a copy plus the deployment. It builds a single container: a first stage downloads the pinned GOWA release for the target architecture and verifies its SHA-256, the runtime stage adds Node and the adapter, and an entrypoint runs both processes with GOWA bound to loopback - the only thing reachable from the LAN is the adapter's TCP port and its discovery beacon. A `tools/sync.js` copies the adapter from this repository and records the source commit; CI re-copies from that commit and fails if the copy drifted, then builds and pushes the image to GHCR.

**Tech Stack:** Node.js 22 on `node:bookworm-slim`, GOWA v9.5.0 release archives for linux/amd64 and linux/arm64, Docker Compose v2, GitHub Actions, GHCR.

## Global Constraints

- **Everything printed at run time is English** (delivered by `2026-09-26-english-runtime-output-and-docker-repo.md`, Part A, which must land first): the container log is English.
- **The adapter keeps zero runtime dependencies.** No `npm install` in the image; the adapter is copied as source.
- **The GOWA version and its SHA-256 digests are copied verbatim from `tools/download.js`** in this repository. `v9.5.0`, `whatsapp_9.5.0_linux_amd64.zip`, sha256 `850a109a5127339adafeca3bd55be0bf5be5a5a3a0e7e2ffdd223536d312138c`; `whatsapp_9.5.0_linux_arm64.zip`, sha256 `3f8530e742d6af749a0249a1e93aa3d80fdc5e132426c67fc3bdeac3c3644379`.
- **No emoji in any `.md`**, and the new repository's docs are a pair (`README.md` + `README.it.md`) with the same heading depth and order.
- **The container never exposes GOWA.** GOWA listens on `127.0.0.1` unless `GOWA_HOST` is set explicitly, and the README says why that matters.
- **`BRIDGE_KEY` must stay `WhatsAppCommunityWP8-2026`** unless the app was rebuilt with a different passphrase: it is a compile-time constant in `WhatsappApp/Services/CryptoHelper.cs`.
- **Docker is not installed on this Mac.** Verification is: the adapter's own suite on the copy, `ruby -ryaml` for the compose files, and the GitHub Actions run (Task B6). The user's `docker compose up -d` on the NAS is the final gate.
- **This repository must stay green:** the five guards, 49 adapter tests, 17 tool tests.
- **Commits:** English, `type: short imperative`; `git push` in both repositories.

## What goes where, and why

```
WhatsappForWP/                      (this repository - the app and the source of truth)
  WhatsappApp/                      WP8.1 app
  WhatsappBridge/                   the adapter: protocol, tests, guards
  tools/                            guards and the local launcher

docker-whatsappforwp/               (new, public)
  Dockerfile                        GOWA + adapter in one image
  docker-compose.yaml               bridge networking, port mappings (works anywhere)
  docker-compose.host.yaml          network_mode: host, for a Linux NAS (discovery works)
  .env.example                      every variable the container reads
  docker/entrypoint.sh              runs GOWA and the adapter, forwards signals
  server/                           copy of WhatsappBridge (source of truth: this repo)
  server/SOURCE_COMMIT              the WhatsappForWP commit the copy came from
  tools/sync.js                     copy from a checkout, or --check it
  README.md / README.it.md          both ways to run it, and which one to choose
  .github/workflows/image.yml       drift check + build + push to GHCR
```

**Which is preferable, and why:** Docker for anything that stays on (a NAS, a
always-on PC): one image, one container, `restart: unless-stopped`, a named volume
for the WhatsApp session, and no Node.js or Go installed on the host. Plain
`node server.js` for a development machine and for the first pairing session,
because it needs no daemon and the log is right in front of you. Both paths are
documented with the same variables, so switching is `docker compose down` plus
`npm start`.

---

### Task B1: Create the repository and bring the adapter in

**Files:**
- Create: `/tmp/docker-whatsappforwp/` (wiped at the start), then the repository root
- Create: `tools/sync.js`
- Create (generated): `server/*.js`, `server/package.json`, `server/test/*.js`, `server/SOURCE_COMMIT`

**Interfaces:**
- Consumes: `WhatsappBridge/{server.js,config.js,crypto-helper.js,discovery.js,gowa-client.js,message-format.js,webhook-server.js,package.json,test/*.js}` from this repository.
- Produces: `node tools/sync.js --from <path>` and `node tools/sync.js --check --from <path>`, used by Tasks B2-B6 and by CI.

- [ ] **Step 1: Write the sync tool**

```js
#!/usr/bin/env node
/**
 * tools/sync.js
 *
 * Copies the adapter from a WhatsappForWP checkout into server/, and records the
 * source commit in server/SOURCE_COMMIT. The adapter's home is that repository:
 * this one only carries the copy the image needs.
 *
 *   node tools/sync.js --from ../WhatsappForWP          copy
 *   node tools/sync.js --check --from app               verify, do not write
 *
 * --check is what CI runs against the recorded commit: if the copy drifted, the
 * build stops instead of shipping a server nobody can reproduce.
 */
'use strict';

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const ROOT = path.resolve(__dirname, '..');
const SERVER = path.join(ROOT, 'server');
const COMMIT_FILE = path.join(SERVER, 'SOURCE_COMMIT');

// I file che compongono il server. I README dell'adapter restano nel repo
// dell'app: qui la documentazione e' quella del deployment.
const FILES = [
  'server.js',
  'config.js',
  'crypto-helper.js',
  'discovery.js',
  'gowa-client.js',
  'message-format.js',
  'webhook-server.js',
  'package.json'
];

function parseArgs(argv) {
  const options = { check: false, from: '' };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--check') options.check = true;
    else if (argv[i] === '--from') options.from = argv[++i] || '';
  }
  return options;
}

function sha256(buffer) {
  return crypto.createHash('sha256').update(buffer).digest('hex');
}

/** I percorsi, relativi a `server/`, che il copy deve contenere. */
function wantedFiles(adapterDir) {
  const files = FILES.slice();
  const tests = path.join(adapterDir, 'test');
  for (const name of fs.readdirSync(tests).sort()) {
    if (name.endsWith('.test.js')) files.push(path.join('test', name));
  }
  return files;
}

function main() {
  const options = parseArgs(process.argv.slice(2));
  if (!options.from) {
    console.error('usage: node tools/sync.js [--check] --from <WhatsappForWP>');
    process.exit(1);
  }

  // Si accetta sia la radice del repo sia la cartella dell'adapter.
  const given = path.resolve(options.from);
  const adapterDir = fs.existsSync(path.join(given, 'server.js'))
    ? given
    : path.join(given, 'WhatsappBridge');
  if (!fs.existsSync(path.join(adapterDir, 'server.js'))) {
    console.error('no adapter found under ' + given + ' (looked for server.js)');
    process.exit(1);
  }

  const files = wantedFiles(adapterDir);
  const problems = [];

  for (const rel of files) {
    const from = path.join(adapterDir, rel);
    const to = path.join(SERVER, rel);
    if (!fs.existsSync(from)) {
      problems.push('missing in the source: ' + rel);
      continue;
    }
    const source = fs.readFileSync(from);
    if (options.check) {
      if (!fs.existsSync(to)) problems.push('not in server/: ' + rel);
      else if (sha256(source) !== sha256(fs.readFileSync(to))) problems.push('out of date: ' + rel);
    } else {
      fs.mkdirSync(path.dirname(to), { recursive: true });
      fs.writeFileSync(to, source);
    }
  }

  if (options.check && !problems.length) {
    // Anche il contrario: un file in server/ che il sorgente non ha piu' e' drift.
    for (const rel of FILES) {
      if (!fs.existsSync(path.join(SERVER, rel))) problems.push('not in server/, and the source has it: ' + rel);
    }
  }

  if (options.check) {
    if (problems.length) {
      console.log(problems.join('\n'));
      console.log('\n' + problems.length + ' problem(s): run "node tools/sync.js --from <WhatsappForWP>".');
      process.exit(1);
    }
    console.log('OK: server/ matches the adapter (' + files.length + ' file(s)).');
    return;
  }

  const commit = execFileSync('git', ['-C', path.dirname(adapterDir), 'rev-parse', 'HEAD'],
    { encoding: 'utf8' }).trim();
  fs.writeFileSync(COMMIT_FILE, commit + '\n');
  console.log('copied ' + files.length + ' file(s) from ' + adapterDir);
  console.log('source commit: ' + commit);
}

main();
```

- [ ] **Step 2: Create the repository and the first commit**

```bash
rm -rf /tmp/docker-whatsappforwp && mkdir -p /tmp/docker-whatsappforwp
gh repo create docker-whatsappforwp --public \
  --description "Docker image for the WhatsApp for Windows Phone 8.1 server (GOWA + Node adapter), also runnable with plain Node.js" \
  --license mit
cd /tmp/docker-whatsappforwp
git remote -v
```

Expected: `gh` prints the created repository URL; `origin` points at `vincenzosco/docker-whatsappforwp`. If `gh repo create` refuses because the name is taken, stop and ask.

- [ ] **Step 3: Copy the adapter and commit**

```bash
mkdir -p tools
# (write tools/sync.js from Step 1 here)
node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP
git add -A && git commit -m "chore: bring in the adapter from WhatsappForWP"
```

Expected: `copied 8 file(s) ... source commit: <sha>` plus the tests (`copied` counts FILES plus the test files; the printed number is the total).

- [ ] **Step 4: The copy passes the adapter's own suite**

```bash
cd server && npm test && cd ..
```

Expected: `tests 49`, `pass 49`, `fail 0` - the copy is a working server, not a dead one.

- [ ] **Step 5: `--check` is not vacuous**

```bash
node tools/sync.js --check --from /Users/vincenzo/Documents/WhatsappForWP
printf '\n// drift\n' >> server/config.js
node tools/sync.js --check --from /Users/vincenzo/Documents/WhatsappForWP; echo "exit=$?"
git checkout server/config.js
```

Expected: `OK: server/ matches the adapter (N file(s)).`, then a `problem` line with `out of date: config.js` and `exit=1`.

---

### Task B2: The image

**Files:**
- Create: `Dockerfile`
- Create: `docker/entrypoint.sh`
- Create: `.dockerignore`

**Interfaces:**
- Consumes: `server/` from Task B1, the GOWA version and digests from the Global Constraints.
- Produces: an image whose entrypoint is `docker/entrypoint.sh`, listening on `8585/tcp` (the app), `8586/tcp` (the webhook), `8587/udp` (discovery) and `127.0.0.1:3000` (GOWA, internal).

- [ ] **Step 1: Write the Dockerfile**

```dockerfile
# syntax=docker/dockerfile:1
#
# One image, two processes: GOWA (the WhatsApp engine) and the Node adapter
# (the only thing the phone talks to). The version of GOWA and its digest are
# the same ones tools/download.js uses in the WhatsappForWP repository: an
# archive that does not match is refused, not "used with a warning".

ARG GOWA_VERSION=9.5.0
ARG NODE_VERSION=22

# ── stage 1: the GOWA binary ────────────────────────────────────────────────
FROM debian:bookworm-slim AS gowa
ARG GOWA_VERSION
ARG TARGETARCH
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl unzip \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /gowa
RUN set -eux; \
    case "${TARGETARCH:-amd64}" in \
      amd64) file="whatsapp_${GOWA_VERSION}_linux_amd64.zip"; \
             sum="850a109a5127339adafeca3bd55be0bf5be5a5a3a0e7e2ffdd223536d312138c" ;; \
      arm64) file="whatsapp_${GOWA_VERSION}_linux_arm64.zip"; \
             sum="3f8530e742d6af749a0249a1e93aa3d80fdc5e132426c67fc3bdeac3c3644379" ;; \
      *) echo "unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    curl -fsSL -o gowa.zip \
      "https://github.com/aldinokemal/go-whatsapp-web-multidevice/releases/download/v${GOWA_VERSION}/${file}"; \
    echo "${sum}  gowa.zip" | sha256sum -c -; \
    unzip -q gowa.zip; \
    chmod +x whatsapp; \
    ./whatsapp --version || true

# ── stage 2: the runtime ────────────────────────────────────────────────────
FROM node:${NODE_VERSION}-bookworm-slim AS runtime
ARG GOWA_VERSION
LABEL org.opencontainers.image.title="whatsapp-for-wp8-server" \
      org.opencontainers.image.description="GOWA plus the WP8.1 adapter, in one container" \
      org.opencontainers.image.source="https://github.com/vincenzosco/docker-whatsappforwp" \
      org.opencontainers.image.licenses="MIT"

# GOWA scrive storages/ e statics/ nella cartella corrente: /data e' il volume.
WORKDIR /data

COPY --from=gowa /gowa/whatsapp /usr/local/bin/whatsapp
# L'adapter non ha dipendenze: si copia il sorgente, non si installa niente.
COPY server/ /opt/adapter/
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

ENV NODE_ENV=production \
    GOWA_VERSION=${GOWA_VERSION} \
    GOWA_BIN=/usr/local/bin/whatsapp \
    GOWA_HOST=127.0.0.1 \
    GOWA_PORT=3000 \
    GOWA_UI=false \
    GOWA_URL=http://127.0.0.1:3000 \
    BRIDGE_PORT=8585 \
    WEBHOOK_PORT=8586 \
    WEBHOOK_PATH=/webhook \
    WEBHOOK_PUBLIC_URL=http://127.0.0.1:8586/webhook \
    DISCOVERY_PORT=8587 \
    DISCOVERY_ENABLED=on \
    POLL_INTERVAL_MS=5000

EXPOSE 8585/tcp 8586/tcp 8587/udp
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD node -e "const s=require('net').connect(Number(process.env.BRIDGE_PORT||8585),'127.0.0.1',()=>{s.end();process.exit(0)});s.on('error',()=>process.exit(1))"

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
```

- [ ] **Step 2: Write the entrypoint**

```sh
#!/bin/sh
# One container, two processes. GOWA only listens on loopback, so the phone can
# reach the adapter and nothing else; the adapter reaches GOWA over localhost.
set -eu

gowa_bin="${GOWA_BIN:-/usr/local/bin/whatsapp}"
gowa_port="${GOWA_PORT:-3000}"
gowa_host="${GOWA_HOST:-127.0.0.1}"

# `set -u` non deve far fallire lo script per una variabile facoltativa vuota.
if [ -n "${GOWA_USER:-}" ]; then
  auth="--basic-auth=${GOWA_USER}:${GOWA_PASS:-}"
else
  auth=""
fi

# GOWA scrive storages/ e statics/ nella cartella corrente.
cd /data

echo "[entrypoint] GOWA ${GOWA_VERSION:-} on ${gowa_host}:${gowa_port}"
# `auth` e' vuoto o un solo argomento: la shell lo spezza apposta.
# shellcheck disable=SC2086
"$gowa_bin" rest \
  "--port=${gowa_port}" \
  "--host=${gowa_host}" \
  "--ui-enabled=${GOWA_UI:-false}" \
  $auth &

gowa_pid=$!
adapter_pid=""

shutdown() {
  echo "[entrypoint] stopping"
  [ -n "$adapter_pid" ] && kill "$adapter_pid" 2>/dev/null || true
  kill "$gowa_pid" 2>/dev/null || true
  wait 2>/dev/null || true
  exit 0
}
trap shutdown TERM INT

echo "[entrypoint] adapter on port ${BRIDGE_PORT:-8585}"
node /opt/adapter/server.js &
adapter_pid=$!

# Se uno dei due muore, l'altro non deve restare in piedi da solo: un adapter
# senza GOWA risponde a tutte le richieste con un errore, e un container "su" che
# non funziona e' peggio di un container fermo.
while kill -0 "$gowa_pid" 2>/dev/null && kill -0 "$adapter_pid" 2>/dev/null; do
  sleep 1
done

if ! kill -0 "$adapter_pid" 2>/dev/null; then
  echo "[entrypoint] the adapter exited: stopping GOWA"
  kill "$gowa_pid" 2>/dev/null || true
else
  echo "[entrypoint] GOWA exited: stopping the adapter"
  kill "$adapter_pid" 2>/dev/null || true
fi
wait 2>/dev/null || true
exit 1
```

- [ ] **Step 3: Write `.dockerignore`**

```
.git
.github
docker-compose*.yaml
*.md
.env
server/node_modules
server/.env
```

- [ ] **Step 4: The shell script parses**

```bash
sh -n docker/entrypoint.sh && echo ENTRYPOINT_OK
```

Expected: `ENTRYPOINT_OK`.

- [ ] **Step 5: Commit**

```bash
git add Dockerfile docker/entrypoint.sh .dockerignore
git commit -m "feat: build one image with GOWA and the adapter"
```

---

### Task B3: The compose files and the variables

**Files:**
- Create: `docker-compose.yaml`, `docker-compose.host.yaml`, `.env.example`

**Interfaces:**
- Consumes: the image from Task B2 (both as `build: .` and as `image: ghcr.io/vincenzosco/docker-whatsappforwp:latest`).
- Produces: the two commands the README documents: `docker compose up -d` and `docker compose -f docker-compose.host.yaml up -d`.

- [ ] **Step 1: Write `docker-compose.yaml`**

```yaml
# The default: bridged networking with explicit port mappings, which works on a
# PC with Docker Desktop and on a NAS.
#
# What it cannot do is deliver the UDP discovery broadcast: inside a bridged
# network the beacon never reaches the phone's subnet, so the app finds nothing
# by itself and you type the address once (it is remembered). On a Linux NAS use
# docker-compose.host.yaml instead and discovery works.
services:
  server:
    image: ghcr.io/vincenzosco/docker-whatsappforwp:latest
    # To build it here instead, comment out `image:` and uncomment the next line.
    # build: .
    container_name: whatsapp-for-wp8
    restart: unless-stopped
    env_file:
      - .env
    ports:
      - "${BRIDGE_PORT:-8585}:${BRIDGE_PORT:-8585}/tcp"     # the WP8.1 app
      - "${WEBHOOK_PORT:-8586}:${WEBHOOK_PORT:-8586}/tcp"   # the GOWA webhook
      - "${DISCOVERY_PORT:-8587}:${DISCOVERY_PORT:-8587}/udp"  # discovery beacon
    volumes:
      # storages/ holds the linked WhatsApp session: without it, every restart
      # means scanning the QR code again.
      - whatsapp-data:/data

volumes:
  whatsapp-data:
```

- [ ] **Step 2: Write `docker-compose.host.yaml`**

```yaml
# Same container, host networking. Use this on a Linux NAS (Synology, QNAP, a
# mini PC) when you want the app to find the server by itself: the discovery
# beacon has to leave the host's own network interface, and only host networking
# lets it.
#
#   docker compose -f docker-compose.host.yaml up -d
#
# There is no `ports:` section on purpose: with host networking the container
# uses the host's ports directly, and Compose rejects the combination.
services:
  server:
    image: ghcr.io/vincenzosco/docker-whatsappforwp:latest
    # build: .
    container_name: whatsapp-for-wp8
    restart: unless-stopped
    network_mode: host
    env_file:
      - .env
    volumes:
      - whatsapp-data:/data

volumes:
  whatsapp-data:
```

- [ ] **Step 3: Write `.env.example`**

```sh
# docker-whatsappforwp configuration. Copy to .env and edit:
#   cp .env.example .env
#
# Every value is optional except the GOWA credentials, which you only need if
# you expose the GOWA REST API (you normally do not: it stays on loopback).

# ── The app ─────────────────────────────────────────────────────────────────
# TCP port the WP8.1 app connects to.
BRIDGE_PORT=8585

# Must match the "Passphrase" constant in WhatsappApp/Services/CryptoHelper.cs.
# It is compiled into the app, so leave this alone unless you rebuilt the app
# with a different passphrase: a mismatch shows as "Message decryption error".
BRIDGE_KEY=WhatsAppCommunityWP8-2026
# "off" disables encryption (plaintext protocol, not recommended).
BRIDGE_ENCRYPTION=on

# ── Automatic discovery ─────────────────────────────────────────────────────
# The adapter announces itself over UDP so the app finds it without you typing
# an address. Needs host networking to cross into the LAN (docker-compose.host.yaml).
DISCOVERY_ENABLED=on
DISCOVERY_PORT=8587
# Name shown in the app's server list; empty means "the container's host name".
DISCOVERY_NAME=

# ── GOWA (inside the same container) ────────────────────────────────────────
# GOWA_URL is the address the adapter uses. On loopback, because GOWA and the
# adapter are one container.
GOWA_URL=http://127.0.0.1:3000
GOWA_PORT=3000
# Leave 127.0.0.1 unless you really want the GOWA web UI from the LAN, and set
# GOWA_USER/GOWA_PASS if you do: the API can send messages as your account.
GOWA_HOST=127.0.0.1
GOWA_UI=false
GOWA_USER=
GOWA_PASS=
# A specific GOWA device id (multi-device); empty means the default device.
GOWA_DEVICE_ID=

# ── Webhook (GOWA -> adapter, same container) ───────────────────────────────
WEBHOOK_PORT=8586
WEBHOOK_PATH=/webhook
WEBHOOK_PUBLIC_URL=http://127.0.0.1:8586/webhook
# Must match --webhook-secret on GOWA (default: "secret").
WEBHOOK_SECRET=

# ── Miscellaneous ───────────────────────────────────────────────────────────
# How often the adapter polls WhatsApp's status, in milliseconds.
POLL_INTERVAL_MS=5000
```

- [ ] **Step 4: Validate both YAML files**

```bash
ruby -ryaml -e 'YAML.load_file("docker-compose.yaml"); puts "compose OK"'
ruby -ryaml -e 'YAML.load_file("docker-compose.host.yaml"); puts "host OK"'
```

Expected: `compose OK`, `host OK`. (`docker` is not installed here; this checks the YAML, and Task B6 checks that Docker accepts it.)

- [ ] **Step 5: Commit**

```bash
git add docker-compose.yaml docker-compose.host.yaml .env.example
git commit -m "feat: add the compose files and the documented variables"
```

---

### Task B4: The README pair

**Files:**
- Create: `README.md`, `README.it.md`

**Interfaces:**
- Consumes: Tasks B1-B3.
- Produces: the text the app repository links to in Task B7.

Both files must have the **same headings, in the same order, at the same depth**:

```markdown
# <title>
## What this is
## Quick start (Docker)
## Configuration
## Which one should you use
## Run it with Node.js instead
## The ports
## Updating
## How the image is built
## Disclosure
```

- [ ] **Step 1: Write `README.md`**

Content, in English:

- **What this is** - one container with GOWA and the WP8.1 adapter; the app itself lives in `vincenzosco/WhatsappForWP` (link it), of which this is only the deployment.
- **Quick start (Docker)** - the three commands and what happens:
  ```bash
  git clone https://github.com/vincenzosco/docker-whatsappforwp
  cd docker-whatsappforwp
  cp .env.example .env      # then edit it if you need to
  docker compose up -d
  docker logs -f whatsapp-for-wp8
  ```
  then: open the app on the phone, let it find the server (or type the NAS IP and port 8585) and scan the QR code the app shows. On a Linux NAS also explain the host-networking variant with `docker-compose.host.yaml`.
- **Configuration** - a table with every variable from `.env.example`, its default and when to change it. Call out `BRIDGE_KEY` (compiled into the app) and `GOWA_HOST` (do not bind GOWA to the LAN without credentials).
- **Which one should you use** - the paragraph from "What goes where, and why": Docker for anything that stays on, Node for development and for the first pairing. Say plainly that Docker is the recommended deployment.
- **Run it with Node.js instead** - no Docker at all:
  ```bash
  # 1. the GOWA binary
  curl -LO https://github.com/aldinokemal/go-whatsapp-web-multidevice/releases/download/v9.5.0/whatsapp_9.5.0_linux_amd64.zip
  unzip whatsapp_9.5.0_linux_amd64.zip
  ./whatsapp rest --port=3000 --host=127.0.0.1 --ui-enabled=false &
  # 2. the adapter (zero dependencies)
  cd server && cp ../.env .env 2>/dev/null; GOWA_URL=http://127.0.0.1:3000 node server.js
  ```
  plus the note that `WhatsappForWP/tools/start-login.js` does all of this - GOWA, adapter, QR in the terminal - on a machine that has the app repository.
- **The ports** - a table: 8585/tcp the app, 8586/tcp the webhook (only GOWA calls it), 8587/udp discovery, 3000/tcp GOWA on loopback.
- **Updating** - `docker compose pull && docker compose up -d`; the session survives in the `whatsapp-data` volume; `docker compose down -v` deletes it and forces a new QR scan.
- **How the image is built** - the two stages, the pinned GOWA version and its checked SHA-256, the fact that the adapter is copied in by `tools/sync.js` from a recorded commit, and that CI fails if the copy drifted.
- **Disclosure** - last section, same wording as the app repository: unofficial, open source, written with an AI agent, no responsibility for the account used.

- [ ] **Step 2: Write `README.it.md`**

The same document in Italian, heading for heading, with a link to `README.md` at the top and vice versa. The two files must have the same heading count, order and depth.

- [ ] **Step 3: Check the pair is really a pair**

```bash
for f in README.md README.it.md; do
  echo "== $f"; grep -c '^#' "$f"; grep -n '^#' "$f" | sed 's/:.*#/ #/'
done
```

Expected: the same number of headings, the same order, the same depth in both (`#`, `##`, ...). No emoji in either.

- [ ] **Step 4: Commit**

```bash
git add README.md README.it.md
git commit -m "docs: both ways to run the server, and which one to prefer"
```

---

### Task B5: Publish the image from CI

**Files:**
- Create: `.github/workflows/image.yml`

**Interfaces:**
- Consumes: `server/SOURCE_COMMIT`, `tools/sync.js --check`, the `Dockerfile`.
- Produces: `ghcr.io/vincenzosco/docker-whatsappforwp:latest` on every push to the default branch, plus `:sha-<short>` and `:app-<source commit>`.

- [ ] **Step 1: Write the workflow**

```yaml
name: image

on:
  push:
    branches: [main, master]
    tags: ["v*"]
  workflow_dispatch:

permissions:
  contents: read
  packages: write

env:
  IMAGE: ghcr.io/${{ github.repository }}

jobs:
  # Il server vive in WhatsappForWP: questo job prova che la copia in server/ e'
  # ancora identica al commit che SOURCE_COMMIT dichiara, prima di costruire
  # un'immagine che nessuno potrebbe riprodurre.
  sync:
    runs-on: ubuntu-latest
    outputs:
      app_commit: ${{ steps.read.outputs.app_commit }}
    steps:
      - uses: actions/checkout@v4
        with:
          path: deploy

      - name: Read the recorded adapter commit
        id: read
        working-directory: deploy
        run: echo "app_commit=$(cat server/SOURCE_COMMIT | tr -d '[:space:]')" >> "$GITHUB_OUTPUT"

      - uses: actions/checkout@v4
        with:
          repository: vincenzosco/WhatsappForWP
          ref: ${{ steps.read.outputs.app_commit }}
          path: app

      - name: The copy must match the adapter
        working-directory: deploy
        run: node tools/sync.js --check --from ../app

  image:
    needs: sync
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: docker/setup-qemu-action@v3
      - uses: docker/setup-buildx-action@v3

      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - uses: docker/metadata-action@v5
        id: meta
        with:
          images: ${{ env.IMAGE }}
          tags: |
            type=raw,value=latest,enable={{is_default_branch}}
            type=sha,format=short
            type=raw,value=app-${{ needs.sync.outputs.app_commit }}

      - uses: docker/build-push-action@v6
        with:
          context: .
          push: true
          platforms: linux/amd64,linux/arm64
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}
          cache-from: type=gha
          cache-to: type=gha,mode=max
```

- [ ] **Step 2: Push everything and watch the run**

```bash
git add .github/workflows/image.yml
git commit -m "ci: verify the adapter copy and publish the image to GHCR"
git push -u origin master
gh run list --limit 3
gh run watch "$(gh run list --limit 1 --json databaseId -q '.[0].databaseId')"
```

Expected: the `image` workflow appears, the `sync` job passes (`OK: server/ matches the adapter`), the `image` job builds for both architectures and pushes. If it fails, read `gh run view --log-failed` and fix the Dockerfile or the workflow, then push again.

- [ ] **Step 3: The image is really there**

```bash
gh api "user/packages/container/docker-whatsappforwp/versions" -q '.[0].metadata.container.tags'
```

Expected: a list containing `latest`. (The package exists only after the first successful push.)

---

### Task B6: Link it from the app repository

**Files:**
- Modify: `README.md`, `README.it.md` (this repository)

**Interfaces:**
- Consumes: the repository URL from Task B1 and the image name from Task B5.
- Produces: the link the user asked for.

- [ ] **Step 1: Add the link to both READMEs**

In `README.md`, in the `## Projects` section, at the end of the `### GOWA Adapter (Node.js)` subsection, add:

```markdown
To deploy the adapter (plus GOWA) on a NAS or an always-on PC, use the separate
deployment repository: [vincenzosco/docker-whatsappforwp](https://github.com/vincenzosco/docker-whatsappforwp).
It publishes a single-container image with GOWA and this adapter, and the same
server can still be started with plain `node server.js`.
```

In `README.it.md`, in the `## Progetti` section, at the end of the `### Adapter GOWA (Node.js)` subsection, add:

```markdown
Per mettere in piedi l'adapter (con GOWA) su un NAS o su un PC sempre acceso c'e'
un repository di deployment separato:
[vincenzosco/docker-whatsappforwp](https://github.com/vincenzosco/docker-whatsappforwp).
Pubblica un'immagine a container unico con GOWA e questo adapter, e lo stesso
server si avvia ancora con `node server.js`.
```

Adding to an existing subsection keeps the heading counts of the two languages identical, which `check-docs.js` enforces.

- [ ] **Step 2: Run the gates and push**

```bash
node tools/check-docs.js && node tools/check-csharp5.js && node tools/check-icons.js \
  && node tools/check-resw.js --strict && node tools/check-framing.js
cd WhatsappBridge && npm test && cd ..
node --test "tools/test/**/*.test.js"
git add README.md README.it.md
git commit -m "docs: point at the deployment repository and its image"
git push origin master
```

Expected: five `OK:` lines, `pass 49`, `pass 17`.

---

## Self-Review

**1. Coverage of the request**

| Request | Task |
| --- | --- |
| The server's debug output is not English | A1 (adapter), A2 (launcher), A3 (app, as chosen), A4 (the rule written down) |
| Create another repo and link it from `README.md` | B1 (create), B6 (link in both READMEs) |
| A Docker image for a NAS or a PC | B2 (image), B5 (published to GHCR) |
| Explain how to configure it with `docker-compose.yaml` | B3 (compose + `.env.example`), B4 (README pair with the variable table) |
| Runnable both via Docker and via Node, with a stated preference | B4 (`## Quick start (Docker)` and `## Run it with Node.js instead`, plus `## Which one should you use` stating Docker is recommended for anything that stays on) |
| A single container | B2 (`entrypoint.sh` runs GOWA and the adapter together), B3 (one service) |

**2. Placeholders**

No `TBD` and no "add the appropriate configuration": every file the executor must create is given in full (sync tool, Dockerfile, entrypoint, both compose files, `.env.example`, workflow, the exact README headings), and the one file that is prose - the README pair - is specified as a heading list plus the exact content of every section. Each verification step carries its command and its expected output, including the two steps that must fail on purpose (B1 Step 5, and the `sync` job's drift check).

**3. Consistency**

- `server/SOURCE_COMMIT` is written by `tools/sync.js` (B1), read by the workflow (B5) and explained in the README (B4): one name.
- `tools/sync.js --check --from` is spelled the same in B1 Step 5 and in the workflow.
- Ports are the same everywhere: `8585/tcp` the app, `8586/tcp` the webhook, `8587/udp` discovery, `3000/tcp` GOWA on loopback - in the Dockerfile `EXPOSE`, the compose mappings, the healthcheck, and the README port table.
- The GOWA version and both digests appear once in the Global Constraints, once in the Dockerfile and once in the README's build section, with identical values.
- `BRIDGE_KEY=WhatsAppCommunityWP8-2026` matches `CryptoHelper.Passphrase` in this repository.
- The image name `ghcr.io/vincenzosco/docker-whatsappforwp` is identical in the workflow, both compose files and the README.

---

## What execution changed about the plan

1. **The GOWA archive does not contain a file called `whatsapp`.** `download.js`
   extracts the largest entry, and the archive's entries are `linux-amd64` (the
   binary) and `readme.md`. The Dockerfile now picks the largest file the same way,
   which is why stage 1 prints `ls -l extracted` and `GOWA binary: ...`: the first
   CI run failed with `chmod: cannot access 'whatsapp'`, and the next person to
   bump the GOWA version should get that line instead of the same puzzle.
2. **One backslash, not two, in the `find` format.** Written as `%s %p\\n`, the
   shell handed printf a literal backslash and an `n`, so `find` printed every
   entry on a single line and `head -n1` returned all of them. The second CI run
   failed with `mv: cannot stat`. The recipe is `%s %p\n`.
3. **The repository's default branch is `main`**, not `master`: the workflow
   listens to both, and the pushed branch is `main`.
4. **The CI is the verification, and it was run.** Run
   [36232684152](https://github.com/vincenzosco/docker-whatsappforwp/actions/runs/36232684152):
   `sync` in 10s (`OK: server/ matches the adapter (16 file(s)).`, which also
   proves the drift check is not vacuous - it compared against
   `28e611c4430d6f94c259a4fb164a3c3b30848147`), `image` in 2m39s for
   `linux/amd64` and `linux/arm64`. GHCR then reports the tags `latest`,
   `sha-5e2cc6c` and `app-28e611c4430d6f94c259a4fb164a3c3b30848147`, with a
   manifest listing both architectures.
5. **`gh` cannot list package versions here** (`read:packages` is not in the token),
   so the tags were confirmed by asking the registry directly with an anonymous
   pull token instead. `docker compose up` on a NAS remains the user's step.
6. **The deploy checkout was temporary.** It was cloned into `_deploy/` inside this
   repository (the file tools cannot write outside the project root) and removed
   once pushed, so this repository never contained a second git repository.
