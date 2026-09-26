# Multi-OS Binary Acquisition and Multi-Service Launcher Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `node tools/start-login.js --download` fetch the pinned GOWA release for whatever OS and CPU it is running on (macOS, Linux and Windows, all eight published archives) and unpack it with no `unzip`/`tar` dependency, and turn the launcher's hardcoded "GOWA + adapter" into a declarative service list so a second in-repo service is started, stopped and (if it is a binary) downloaded by the same code path.

**Architecture:** A new `tools/download.js` owns three things and nothing else: the platform-to-archive table with the published SHA-256 digests, a pure-Node ZIP reader (`node:zlib`, correct central-directory parsing, loud refusal of ZIP64) and `ensureArchive()` which fetches, verifies, extracts and chmods. A new `tools/services.js` turns "which services should run" into pure data (`buildServiceList`, `serviceEnv`) so it can be tested without spawning anything, and `start-login.js` keeps its current job of drawing the QR and wiring the children together. The adapter's env mapping moves into `services.js` with it, which is where it becomes testable for the first time.

**Tech Stack:** Node 26 builtins only (`node:zlib`, `node:crypto`, global `fetch`, `node:test`). The system `zip` is used *only* to build fixtures inside the tests; nothing at runtime depends on it.

**A note on running the tool tests:** `node --test tools/test` (a bare directory) fails on this Node build with `Cannot find module '…/tools/test'` — it is read as an entry point, not scanned. The working forms are a glob that Node expands (`node --test "tools/test/**/*.test.js"`, used everywhere below) or one file at a time (`node --test tools/test/download.test.js`). Verified on Node v26.4.0: the directory form reports `tests 1 / pass 0 / fail 1`; the glob form reports `tests 17 / pass 17`.

## Global Constraints

- **Zero new dependencies.** `WhatsappBridge/package.json` has none and this adds none to `tools/` either. Everything comes from Node builtins.
- **Pinned release:** GOWA `v9.5.0`, from `https://github.com/aldinokemal/go-whatsapp-web-multidevice/releases/download/v9.5.0/<file>`. The eight archives and their SHA-256 digests, taken verbatim from the `digest` field of that release's GitHub API response:

| Archive key | File | SHA-256 |
|---|---|---|
| `darwin-arm64` | `whatsapp_9.5.0_darwin_arm64.zip` | `0a5639e0608aaae3e1c7977a16303b782c2ea3ff7be8f73ecf0bed89adf7a444` |
| `darwin-amd64` | `whatsapp_9.5.0_darwin_amd64.zip` | `b57d6fa46bbef88fb3dd1708174d4e42cdcae7dea70250961a3f70f7c06e207b` |
| `linux-amd64` | `whatsapp_9.5.0_linux_amd64.zip` | `850a109a5127339adafeca3bd55be0bf5be5a5a3a0e7e2ffdd223536d312138c` |
| `linux-arm64` | `whatsapp_9.5.0_linux_arm64.zip` | `3f8530e742d6af749a0249a1e93aa3d80fdc5e132426c67fc3bdeac3c3644379` |
| `linux-386` | `whatsapp_9.5.0_linux_386.zip` | `caf1dad54d443720422ded7e8bae6d287a1747f08176f25d66e5f9205e188f5a` |
| `linux-armv7` | `whatsapp_9.5.0_linux_armv7.zip` | `b2470f495265f202b19bf92d2d1456fb3c89c34cc09b614a07f8435a4ffab40d` |
| `windows-amd64` | `whatsapp_9.5.0_windows_amd64.zip` | `611e66c5751657980b12a5a216af466f19548cba89e66b0b5a1c353e5a0ee825` |
| `windows-386` | `whatsapp_9.5.0_windows_386.zip` | `f317613c5106e46df6ad66fea88b37dffab54676a168eaacb4b44e80b5b68a09` |

  A digest mismatch is a hard failure, never a warning.
- **No `unzip`, no `tar`, no `Expand-Archive`.** Extraction is done in-process, because Windows has none of those by default and `unzip` is not guaranteed on Linux.
- **ZIP64 is refused loudly.** A ZIP64 archive (or one whose sizes are `0xffffffff`) throws `archivio ZIP64 non supportato` instead of being mis-parsed into a corrupt binary.
- **`.tools/` stays gitignored** and holds the downloaded binaries and the live WhatsApp session. The plan's tests never write into `.tools/gowa`.
- **The five gates must stay green** after every task:

```
node tools/check-csharp5.js                 # OK: 27 C# file(s) are C# 5 compatible.
node tools/check-icons.js                   # OK: 13 inline icon Path(s), 9 distinct icon(s).
node tools/check-resw.js --strict           # OK: 91 key(s) ...  (unchanged by this plan)
node tools/check-docs.js                    # OK: 2 doc pair(s) ... (quote the .md count it prints)
node --test "tools/test/**/*.test.js"       # the new suite this plan creates (17 tests)
cd WhatsappBridge && npm test               # 45 tests pass, unchanged by this plan
```

- **Do not change** `tools/qr-term.js`, `tools/check-*.js`, `WhatsappBridge/**` or `WhatsappApp/**`. This plan is the launcher and the downloader only.
- **`--download` keeps its meaning**: it fetches when the binary is missing and is a no-op when it is already there. It must never silently replace a working binary with a different one; replacing the same pinned version is fine.
- **The launcher's output marks, in code blocks.** `tools/start-login.js` prints a `CHECK MARK` (U+2714) for success and a `CROSS MARK` (U+2716) for failure, and `tools/check-docs.js` forbids both inside `.md` files (only U+26A0 is allowed). Every code block below therefore writes them as the escapes `\u2714` and `\u2716`, which a template literal or a string literal evaluates to exactly the same character. **In the `.js` file keep the real mark that is already there** — the escapes exist only so this document can be checked by the same guard as the rest of the repo. Verified: `node tools/check-docs.js` fails on the literal characters, and this plan passed only after escaping them.
- **One commit per task**, English subject, `type: short imperative`.
- **The dev machine is macOS/arm64.** Everything here is verified by `node --test "tools/test/**/*.test.js"` plus one real 11 MB download in Task 2; the Linux and Windows rows are verified by unit-testing the pure mapping and extraction functions, and by a real extraction test that builds archives in both compression modes with the system `zip`.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `tools/download.js` | The release table, the ZIP reader, fetch+verify+extract+chmod. Knows nothing about services or QR codes. | **New** |
| `tools/services.js` | Turns the options into the ordered list of services to run, plus each service's args and env — pure, no spawning, no I/O except an injectable `exists`. | **New** |
| `tools/start-login.js` | Keeps the QR/login loop and the child-process lifecycle; loses its private download code and its hardcoded env block to the two modules above. | Modified |
| `tools/test/download.test.js` | Platform mapping, digest table, ZIP reader against `zip`-built fixtures, ZIP64 refusal, publisher checksum cross-check. | **New** |
| `tools/test/services.test.js` | Service list, skipped reasons, env mapping, args. | **New** |
| `.agents/skills/run-the-login-server/SKILL.md` | The option table, the gate list and the "what the script does" section. | Modified |
| `README.md` / `README.it.md` | One line on which platforms the one-command download covers. | Modified |
| `docs/superpowers/plans/2026-09-25-english-output-finish-screens-and-bugs.md` | Its `HELP` transcription is invalidated by the new flags. | Modified (Task 4) |

Task order: 1 (downloader) → 2 (wire `--download`) → 3 (services) → 4 (docs + acceptance). Tasks 1 and 3 are independent; 2 needs 1, and 4 needs all.

---

### Task 1: The download module

**Files:**
- Create: `tools/download.js`
- Test: `tools/test/download.test.js`

**Interfaces:**
- Consumes: nothing (Node builtins only).
- Produces (consumed by Task 2 and by any future binary service):
  - `GOWA_VERSION: string` (`'v9.5.0'`)
  - `GOWA_ARCHIVES: { [key: string]: { file: string, sha256: string } }`
  - `archiveKeyFor(platform: string, arch: string): string | null` — e.g. `('darwin','arm64')` → `'darwin-arm64'`, `('linux','arm')` → `'linux-armv7'`, unknown → `null`
  - `targetNameFor(platform: string): string` — `'whatsapp.exe'` on `win32`, else `'whatsapp'`
  - `releaseUrlFor(key: string): string`
  - `sha256(buffer: Buffer): string`
  - `extractLargestEntry(buffer: Buffer): { name: string, data: Buffer }` — throws on a non-ZIP buffer, an empty archive, ZIP64, or an unsupported compression method
  - `ensureArchive({ key, dir, targetName, expectedDigest, fetchImpl, log }): Promise<{ target: string, entryName: string, bytes: number }>`

- [ ] **Step 1: Write the failing test for the platform table**

Create `tools/test/download.test.js`:

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');

const download = require('../download');

test('archiveKeyFor copre le otto piattaforme pubblicate', () => {
  assert.strictEqual(download.archiveKeyFor('darwin', 'arm64'), 'darwin-arm64');
  assert.strictEqual(download.archiveKeyFor('darwin', 'x64'), 'darwin-amd64');
  assert.strictEqual(download.archiveKeyFor('linux', 'x64'), 'linux-amd64');
  assert.strictEqual(download.archiveKeyFor('linux', 'arm64'), 'linux-arm64');
  assert.strictEqual(download.archiveKeyFor('linux', 'ia32'), 'linux-386');
  assert.strictEqual(download.archiveKeyFor('linux', 'arm'), 'linux-armv7');
  assert.strictEqual(download.archiveKeyFor('win32', 'x64'), 'windows-amd64');
  assert.strictEqual(download.archiveKeyFor('win32', 'ia32'), 'windows-386');
});

test('archiveKeyFor rifiuta le coppie senza archivio ufficiale', () => {
  assert.strictEqual(download.archiveKeyFor('darwin', 'ia32'), null);
  assert.strictEqual(download.archiveKeyFor('win32', 'arm64'), null);
  assert.strictEqual(download.archiveKeyFor('freebsd', 'x64'), null);
});

test('ogni chiave che archiveKeyFor puo produrre esiste nella tabella', () => {
  const pairs = [
    ['darwin', 'arm64'], ['darwin', 'x64'],
    ['linux', 'x64'], ['linux', 'arm64'], ['linux', 'ia32'], ['linux', 'arm'],
    ['win32', 'x64'], ['win32', 'ia32']
  ];
  for (const [platform, arch] of pairs) {
    const key = download.archiveKeyFor(platform, arch);
    const archive = download.GOWA_ARCHIVES[key];
    assert.ok(archive, `manca una voce per ${key}`);
    assert.match(archive.file, /^whatsapp_\d+\.\d+\.\d+_[a-z0-9]+_[a-z0-9]+\.zip$/);
    assert.match(archive.sha256, /^[0-9a-f]{64}$/);
  }
});

test('targetNameFor aggiunge .exe solo su Windows', () => {
  assert.strictEqual(download.targetNameFor('darwin'), 'whatsapp');
  assert.strictEqual(download.targetNameFor('linux'), 'whatsapp');
  assert.strictEqual(download.targetNameFor('win32'), 'whatsapp.exe');
});

test('releaseUrlFor punta alla release fissata', () => {
  const url = download.releaseUrlFor('linux-amd64');
  assert.strictEqual(
    url,
    'https://github.com/aldinokemal/go-whatsapp-web-multidevice/releases/download/v9.5.0/whatsapp_9.5.0_linux_amd64.zip'
  );
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node --test tools/test/download.test.js`

Expected: **FAIL** — `TypeError: download.archiveKeyFor is not a function` (the module does not exist yet; Node reports `Cannot find module '../download'`).

- [ ] **Step 3: Write the platform half of `tools/download.js`**

Create `tools/download.js` with:

```js
/**
 * tools/download.js
 *
 * Scarica, verifica e scompatta gli eseguibili di cui lo stack ha bisogno
 * (oggi GOWA), per qualunque sistema su cui lo script viene lanciato.
 *
 * Perche' non usa `unzip`: su Windows non esiste, e su Linux non e' detto. Node
 * sa gia' leggere un archivio ZIP per conto suo (lo stesso formato e' inflate,
 * che sta in `node:zlib`), quindi l'estrazione avviene in processo e lo script
 * resta senza dipendenze da installare.
 *
 * I digest SHA-256 sono quelli pubblicati dalla release: un archivio che non
 * combacia viene rifiutato, non "usato con un avviso".
 */
'use strict';

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const GOWA_VERSION = 'v9.5.0';
const RELEASE_BASE = 'https://github.com/aldinokemal/go-whatsapp-web-multidevice/releases/download';

// Digest SHA-256 dei soli archivi ufficiali, presi dal campo `digest` della
// release API di GitHub per v9.5.0.
const GOWA_ARCHIVES = {
  'darwin-arm64': {
    file: 'whatsapp_9.5.0_darwin_arm64.zip',
    sha256: '0a5639e0608aaae3e1c7977a16303b782c2ea3ff7be8f73ecf0bed89adf7a444'
  },
  'darwin-amd64': {
    file: 'whatsapp_9.5.0_darwin_amd64.zip',
    sha256: 'b57d6fa46bbef88fb3dd1708174d4e42cdcae7dea70250961a3f70f7c06e207b'
  },
  'linux-amd64': {
    file: 'whatsapp_9.5.0_linux_amd64.zip',
    sha256: '850a109a5127339adafeca3bd55be0bf5be5a5a3a0e7e2ffdd223536d312138c'
  },
  'linux-arm64': {
    file: 'whatsapp_9.5.0_linux_arm64.zip',
    sha256: '3f8530e742d6af749a0249a1e93aa3d80fdc5e132426c67fc3bdeac3c3644379'
  },
  'linux-386': {
    file: 'whatsapp_9.5.0_linux_386.zip',
    sha256: 'caf1dad54d443720422ded7e8bae6d287a1747f08176f25d66e5f9205e188f5a'
  },
  'linux-armv7': {
    file: 'whatsapp_9.5.0_linux_armv7.zip',
    sha256: 'b2470f495265f202b19bf92d2d1456fb3c89c34cc09b614a07f8435a4ffab40d'
  },
  'windows-amd64': {
    file: 'whatsapp_9.5.0_windows_amd64.zip',
    sha256: '611e66c5751657980b12a5a216af466f19548cba89e66b0b5a1c353e5a0ee825'
  },
  'windows-386': {
    file: 'whatsapp_9.5.0_windows_386.zip',
    sha256: 'f317613c5106e46df6ad66fea88b37dffab54676a168eaacb4b44e80b5b68a09'
  }
};

// Le coppie piattaforma/architettura di Node non si chiamano come quelle di
// goreleaser: `arm` e' armv7, `x64` e' amd64, `darwin` per GOWA e' darwin.
const PLATFORM_KEYS = {
  'darwin-arm64': 'darwin-arm64',
  'darwin-x64': 'darwin-amd64',
  'linux-x64': 'linux-amd64',
  'linux-arm64': 'linux-arm64',
  'linux-ia32': 'linux-386',
  'linux-arm': 'linux-armv7',
  'win32-x64': 'windows-amd64',
  'win32-ia32': 'windows-386'
};

/** La chiave dell'archivio ufficiale per questa piattaforma, o null. */
function archiveKeyFor(platform, arch) {
  return PLATFORM_KEYS[`${platform}-${arch}`] || null;
}

/** Il nome con cui l'eseguibile finisce su disco. */
function targetNameFor(platform) {
  return platform === 'win32' ? 'whatsapp.exe' : 'whatsapp';
}

function releaseUrlFor(key) {
  const archive = GOWA_ARCHIVES[key];
  if (!archive) throw new Error(`nessun archivio GOWA per ${key}`);
  return `${RELEASE_BASE}/${GOWA_VERSION}/${archive.file}`;
}

function sha256(buffer) {
  return crypto.createHash('sha256').update(buffer).digest('hex');
}

module.exports = {
  GOWA_VERSION,
  GOWA_ARCHIVES,
  archiveKeyFor,
  targetNameFor,
  releaseUrlFor,
  sha256
};
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `node --test tools/test/download.test.js`

Expected: `# pass 5`, `# fail 0`.

- [ ] **Step 5: Write the failing test for the ZIP reader**

Append to `tools/test/download.test.js`:

```js
function makeZip({ stored = false } = {}) {
  // Un archivio vero, costruito con lo `zip` di sistema: cosi' il lettore viene
  // provato contro un'implementazione che non e' la sua.
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'gowa-zip-'));
  const big = Buffer.alloc(4096, 0x41);
  fs.writeFileSync(path.join(dir, 'linux-amd64'), big);
  fs.writeFileSync(path.join(dir, 'README.md'), '# finto\n');
  const out = path.join(dir, 'archive.zip');
  execFileSync('zip', [stored ? '-0' : '-9', '-j', '-q', out,
    path.join(dir, 'linux-amd64'), path.join(dir, 'README.md')]);
  const bytes = fs.readFileSync(out);
  fs.rmSync(dir, { recursive: true, force: true });
  return { bytes, big };
}

test('extractLargestEntry estrae il binario piu grande da un archivio deflate', () => {
  const { bytes, big } = makeZip();
  const entry = download.extractLargestEntry(bytes);
  assert.strictEqual(entry.name, 'linux-amd64');
  assert.strictEqual(entry.data.length, big.length);
  assert.ok(entry.data.equals(big));
});

test('extractLargestEntry estrae anche gli archivi senza compressione', () => {
  const { bytes, big } = makeZip({ stored: true });
  const entry = download.extractLargestEntry(bytes);
  assert.strictEqual(entry.name, 'linux-amd64');
  assert.ok(entry.data.equals(big));
});

test('extractLargestEntry rifiuta cio che non e un archivio', () => {
  assert.throws(() => download.extractLargestEntry(Buffer.from('non sono uno zip')),
    /non e' un archivio ZIP/);
});

test('extractLargestEntry rifiuta un archivio vuoto', () => {
  // Solo l'end-of-central-directory: zero voci.
  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0);
  assert.throws(() => download.extractLargestEntry(eocd), /ZIP vuoto|archivio ZIP/);
});

test('i digest darwin combaciano con il file dei checksum pubblicato', (t) => {
  // Il file e' quello scaricato dal rilascio e lasciato in .tools/gowa: la
  // tabella scritta a mano viene verificata contro quanto pubblica GOWA.
  const file = path.join(__dirname, '..', '..', '.tools', 'gowa', 'checksums-macos.txt');
  if (!fs.existsSync(file)) {
    t.skip('checksums-macos.txt non presente: nessun confronto possibile');
    return;
  }
  const lines = fs.readFileSync(file, 'utf8').trim().split('\n');
  const published = {};
  for (const line of lines) {
    const [digest, name] = line.trim().split(/\s+/);
    published[name] = digest;
  }
  for (const key of ['darwin-arm64', 'darwin-amd64']) {
    const archive = download.GOWA_ARCHIVES[key];
    assert.strictEqual(published[archive.file], archive.sha256,
      `digest diverso da quello pubblicato per ${archive.file}`);
  }
});
```

- [ ] **Step 6: Run it to verify it fails**

Run: `node --test tools/test/download.test.js`

Expected: **FAIL** — `TypeError: download.extractLargestEntry is not a function` (4 failures), the digest test passes because it only reads the table.

- [ ] **Step 7: Implement the ZIP reader**

In `tools/download.js`, insert before `module.exports`:

```js
// ─── Lettore ZIP ─────────────────────────────────────────────────────────────
//
// Un ZIP e' una directory centrale alla fine del file, con dentro il nome e
// l'offset di ogni voce; i dati di ogni voce stanno dietro la propria
// intestazione locale, le cui lunghezze di nome/extra possono differire da
// quelle della directory: i dati cominciano dopo quelle *locali*.

const EOCD_SIGNATURE = 0x06054b50;
const CENTRAL_SIGNATURE = 0x02014b50;
const LOCAL_SIGNATURE = 0x04034b50;
const ZIP64_LOCATOR_SIGNATURE = 0x07064b50;

function findEndOfCentralDirectory(buffer) {
  // L'EOCD e' l'ultima struttura, ma un commento dell'archivio puo' seguirlo:
  // si cerca all'indietro nella finestra massima consentita dal formato.
  const lowest = Math.max(0, buffer.length - 65557);
  for (let at = buffer.length - 22; at >= lowest; at--) {
    if (buffer.readUInt32LE(at) === EOCD_SIGNATURE) return at;
  }
  return -1;
}

function readCentralDirectory(buffer) {
  const eocd = findEndOfCentralDirectory(buffer);
  if (eocd < 0) throw new Error("non e' un archivio ZIP (manca la fine della directory centrale)");

  const count = buffer.readUInt16LE(eocd + 10);
  const size = buffer.readUInt32LE(eocd + 12);
  const offset = buffer.readUInt32LE(eocd + 16);

  // Con ZIP64 questi campi valgono 0xffffffff e i valori veri stanno altrove:
  // leggerli lo stesso produrrebbe un binario corrotto in silenzio.
  const locator = eocd - 20;
  if (locator >= 0 && buffer.readUInt32LE(locator) === ZIP64_LOCATOR_SIGNATURE) {
    throw new Error('archivio ZIP64 non supportato');
  }
  if (offset === 0xffffffff || size === 0xffffffff || count === 0xffff) {
    throw new Error('archivio ZIP64 non supportato');
  }

  const entries = [];
  let cursor = offset;
  for (let i = 0; i < count; i++) {
    if (cursor + 46 > buffer.length || buffer.readUInt32LE(cursor) !== CENTRAL_SIGNATURE) {
      throw new Error('directory centrale ZIP non valida');
    }
    const nameLength = buffer.readUInt16LE(cursor + 28);
    const extraLength = buffer.readUInt16LE(cursor + 30);
    const commentLength = buffer.readUInt16LE(cursor + 32);

    entries.push({
      name: buffer.toString('utf8', cursor + 46, cursor + 46 + nameLength),
      method: buffer.readUInt16LE(cursor + 10),
      compressedSize: buffer.readUInt32LE(cursor + 20),
      uncompressedSize: buffer.readUInt32LE(cursor + 24),
      localOffset: buffer.readUInt32LE(cursor + 42)
    });

    cursor += 46 + nameLength + extraLength + commentLength;
  }
  return entries;
}

function readEntryData(buffer, entry) {
  const at = entry.localOffset;
  if (at + 30 > buffer.length || buffer.readUInt32LE(at) !== LOCAL_SIGNATURE) {
    throw new Error(`intestazione locale non valida per ${entry.name}`);
  }
  const nameLength = buffer.readUInt16LE(at + 26);
  const extraLength = buffer.readUInt16LE(at + 28);
  const start = at + 30 + nameLength + extraLength;

  if (start + entry.compressedSize > buffer.length) {
    throw new Error(`archivio troncato: ${entry.name} esce dal file`);
  }
  const raw = buffer.subarray(start, start + entry.compressedSize);

  if (entry.method === 0) return Buffer.from(raw);
  if (entry.method === 8) return zlib.inflateRawSync(raw);
  throw new Error(`compressione ZIP non supportata (${entry.method}) per ${entry.name}`);
}

/**
 * Il contenuto della voce piu' grande che non sia una cartella.
 * Gli archivi di una release contengono un eseguibile e poco altro: prendere
 * il piu' grande evita di dover sapere come si chiama il file dentro lo zip su
 * ogni piattaforma.
 */
function extractLargestEntry(buffer) {
  if (!Buffer.isBuffer(buffer) || buffer.length < 22) {
    throw new Error("non e' un archivio ZIP (troppo corto)");
  }

  const entries = readCentralDirectory(buffer)
    .filter((entry) => entry.uncompressedSize > 0 && !entry.name.endsWith('/'));
  if (entries.length === 0) throw new Error('archivio ZIP vuoto');

  let chosen = entries[0];
  for (const entry of entries) if (entry.uncompressedSize > chosen.uncompressedSize) chosen = entry;
  return { name: chosen.name, data: readEntryData(buffer, chosen) };
}
```

Add `extractLargestEntry` to `module.exports`.

- [ ] **Step 8: Run the test to verify it passes**

Run: `node --test tools/test/download.test.js`

Expected: `# pass 10`, `# fail 0` (the checksum cross-check either passes against the real file or skips).

- [ ] **Step 9: Add `ensureArchive` and its test**

Append this test to `tools/test/download.test.js`:

```js
test('ensureArchive verifica il digest, estrae e rende eseguibile', async () => {
  const { bytes } = makeZip();
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'gowa-out-'));
  const result = await download.ensureArchive({
    key: 'linux-amd64',
    dir,
    targetName: 'whatsapp',
    expectedDigest: download.sha256(bytes),
    fetchImpl: async () => ({ ok: true, status: 200, arrayBuffer: async () => bytes }),
    log: () => {}
  });

  assert.strictEqual(result.entryName, 'linux-amd64');
  const written = fs.readFileSync(result.target);
  assert.strictEqual(written.length, 4096);
  if (process.platform !== 'win32') {
    assert.ok((fs.statSync(result.target).mode & 0o111) !== 0, 'il binario deve essere eseguibile');
  }
  fs.rmSync(dir, { recursive: true, force: true });
});

test('ensureArchive si rifiuta di installare un digest diverso', async () => {
  const { bytes } = makeZip();
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'gowa-out-'));
  await assert.rejects(
    download.ensureArchive({
      key: 'linux-amd64',
      dir,
      targetName: 'whatsapp',
      expectedDigest: 'a'.repeat(64),
      fetchImpl: async () => ({ ok: true, status: 200, arrayBuffer: async () => bytes }),
      log: () => {}
    }),
    /SHA-256/
  );
  fs.rmSync(dir, { recursive: true, force: true });
});
```

Run: `node --test tools/test/download.test.js`

Expected: **FAIL** — `TypeError: download.ensureArchive is not a function`.

- [ ] **Step 10: Implement `ensureArchive`**

Add to `tools/download.js`, before `module.exports`:

```js
/**
 * Scarica l'archivio, ne verifica il digest pubblicato, estrae l'eseguibile e
 * lo rende eseguibile. Non tocca il file di destinazione se il download o la
 * verifica falliscono: prima si valida, poi si scrive.
 */
async function ensureArchive({ key, dir, targetName, expectedDigest, fetchImpl, log }) {
  const archive = GOWA_ARCHIVES[key];
  if (!archive) throw new Error(`nessun archivio GOWA per ${key}`);

  const expected = expectedDigest || archive.sha256;
  const url = releaseUrlFor(key);
  const doFetch = fetchImpl || fetch;
  if (typeof log === 'function') log(`scarico ${archive.file} ...`);

  const response = await doFetch(url, { signal: AbortSignal.timeout(600000) });
  if (!response.ok) throw new Error(`download fallito (${response.status}) da ${url}`);

  const buffer = Buffer.from(await response.arrayBuffer());
  const digest = sha256(buffer);
  if (digest !== expected) {
    throw new Error(`SHA-256 inatteso: ${digest}\n     atteso: ${expected}`);
  }

  const entry = extractLargestEntry(buffer);
  fs.mkdirSync(dir, { recursive: true });

  // Scrittura su file temporaneo e rinomina: un'estrazione interrotta non
  // lascia un eseguibile a meta'.
  const target = path.join(dir, targetName);
  const temporary = `${target}.part`;
  fs.writeFileSync(temporary, entry.data);
  fs.chmodSync(temporary, 0o755);
  fs.renameSync(temporary, target);

  if (typeof log === 'function') log(`installato ${path.basename(target)} da ${entry.name}`);
  return { target, entryName: entry.name, bytes: entry.data.length };
}
```

Add `ensureArchive` to `module.exports`, then Run: `node --test tools/test/download.test.js`

Expected: `# pass 12`, `# fail 0`.

- [ ] **Step 11: Commit**

```bash
git add tools/download.js tools/test/download.test.js
git commit -m "feat: acquire the release binary on any OS with an in-process ZIP reader"
```

---

### Task 2: `--download` uses the new module on every platform

**Files:**
- Modify: `tools/start-login.js` (delete `GOWA_RELEASES`, `platformKey`, `downloadGowa`; change the call site in `main`)

**Interfaces:**
- Consumes: `archiveKeyFor`, `targetNameFor`, `ensureArchive`, `GOWA_VERSION` from `tools/download.js` (Task 1).
- Produces: `node tools/start-login.js --download` works on all eight platforms; the failure message on an unsupported CPU names the pair and points at `--gowa`/`--url`.

- [ ] **Step 1: Replace the download block**

In `tools/start-login.js`, delete the `GOWA_VERSION`, `GOWA_RELEASES`, `platformKey()` and `downloadGowa()` declarations (the table and `downloadGowa` move to `tools/download.js`, which already has them in a form that covers every platform) and add the import next to `qrTerm`:

```js
const qrTerm = require('./qr-term');
const downloader = require('./download');
```

The `GOWA_VERSION` constant is still referenced by the banner and the help text; take it from the module instead of declaring it:

```js
const GOWA_VERSION = downloader.GOWA_VERSION;
```

- [ ] **Step 2: Replace the call site in `main()`**

Replace this block:

```js
  const binary = gowaBinary(options);
  const local = !options.url;
  if (local) {
    if (!fs.existsSync(binary)) {
      if (options.download) {
        await downloadGowa();
      } else {
        fail(`GOWA non trovato in ${path.relative(ROOT, binary)}\n` +
          '     node tools/start-login.js --download      # scarica ' + GOWA_VERSION + ' e verifica il SHA-256\n' +
          '     oppure passa --gowa <percorso dell\'eseguibile> o --url <GOWA già avviato>');
      }
    }
```

with:

```js
  const binary = gowaBinary(options);
  const local = !options.url;
  if (local) {
    if (!fs.existsSync(binary)) {
      if (!options.download) {
        fail(missingGowaHint(binary));
      }
      // Il binario si scarica per QUESTA macchina: il sistema e la CPU si
      // leggono qui, non in una tabella scritta a mano.
      await installGowa();
    }
```

- [ ] **Step 3: Add the two helpers to `tools/start-login.js`**

Replace the old `gowaBinary` (keep it) and add, right after it:

```js
/** Il testo che dice cosa fare quando GOWA non c'e' e non lo si e' chiesto. */
function missingGowaHint(binary) {
  const key = downloader.archiveKeyFor(process.platform, process.arch);
  const available = key
    ? `scaricabile con --download per questa macchina (${key})`
    : `non pubblicato per ${process.platform}/${process.arch}: usa --gowa <percorso> o --url <GOWA gia' avviato>`;
  return `GOWA non trovato in ${path.relative(ROOT, binary)} (${available})\n` +
    '     node tools/start-login.js --download      # scarica ' + GOWA_VERSION + ' e verifica il SHA-256';
}

/**
 * Installa GOWA per la piattaforma corrente. Su una coppia senza archivio
 * ufficiale si ferma dicendo quale coppia e' e cosa fare invece.
 */
async function installGowa() {
  const key = downloader.archiveKeyFor(process.platform, process.arch);
  if (!key) {
    fail(`nessun binario GOWA ${GOWA_VERSION} per ${process.platform}/${process.arch}\n` +
      '     usa --gowa <percorso dell\'eseguibile> o --url <GOWA già avviato>');
  }

  try {
    const result = await downloader.ensureArchive({
      key,
      dir: GOWA_DIR,
      targetName: downloader.targetNameFor(process.platform),
      log: (line) => console.log(`  ↓  ${line}`),
    });
    console.log(`  \u2714  GOWA ${GOWA_VERSION} installato in ${path.relative(ROOT, result.target)} (SHA-256 verificato)`);
  } catch (err) {
    fail(err.message);
  }
}
```

The block must be exactly that — there is no separate digest line here, because `ensureArchive` verifies the digest before writing anything. Here it is once more, on its own, so there is nothing to assemble:

```js
async function installGowa() {
  const key = downloader.archiveKeyFor(process.platform, process.arch);
  if (!key) {
    fail(`nessun binario GOWA ${GOWA_VERSION} per ${process.platform}/${process.arch}\n` +
      '     usa --gowa <percorso dell\'eseguibile> o --url <GOWA già avviato>');
  }

  try {
    const result = await downloader.ensureArchive({
      key,
      dir: GOWA_DIR,
      targetName: downloader.targetNameFor(process.platform),
      log: (line) => console.log(`  ↓  ${line}`),
    });
    console.log(`  \u2714  GOWA ${GOWA_VERSION} installato in ${path.relative(ROOT, result.target)} (SHA-256 verificato)`);
  } catch (err) {
    fail(err.message);
  }
}
```

- [ ] **Step 4: Guard `main()` so the module can be imported**

`tools/start-login.js` currently ends with an unguarded `main().catch(...)`, so `require('./start-login')` would start the whole stack. Replace the tail:

```js
main().catch((err) => {
  console.error(`\n  \u2716 ${err && err.message ? err.message : err}\n`);
  process.exit(1);
});
```

with:

```js
if (require.main === module) {
  main().catch((err) => {
    console.error(`\n  \u2716 ${err && err.message ? err.message : err}\n`);
    process.exit(1);
  });
} else {
  // Importabile dai test senza avviare niente.
  module.exports = { parseArgs, missingGowaHint, installGowa, GOWA_VERSION };
}
```

(In the `.js` file that line already carries the real U+2716 mark, not the `\u2716` escape written here: see the escape rule in Global Constraints.)

Run: `node -e "require('./tools/start-login.js'); console.log('import ok')"`

Expected: `import ok`, with no GOWA startup, no banner and no network access. Then Run: `node tools/start-login.js --help | head -3`

Expected: the help text, starting with `Avvia GOWA + l'adattatore WP8 e mostra il QR di login nel terminale.`

- [ ] **Step 5: Prove the download path for real, in a temp directory**

(This step needs network access and takes the better part of a minute; it writes only to a temp directory.)

Run this one-off (downloads ~11 MB for the current platform and cleans up after itself):

```bash
node -e "
const os = require('os'), path = require('path'), fs = require('fs');
const d = require('./tools/download');
const key = d.archiveKeyFor(process.platform, process.arch);
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'gowa-acceptance-'));
(async () => {
  const r = await d.ensureArchive({ key, dir, targetName: d.targetNameFor(process.platform), log: console.log });
  const bytes = fs.readFileSync(r.target);
  console.log('key:', key, '/ voce:', r.entryName, '/ byte:', bytes.length);
  console.log('eseguibile:', (fs.statSync(r.target).mode & 0o111) !== 0);
  fs.rmSync(dir, { recursive: true, force: true });
})().catch((e) => { console.error('FALLITO:', e.message); process.exit(1); });
"
```

Expected: `key: darwin-arm64 / voce: darwin-arm64 / byte: 31240642`, `eseguibile: true`. The byte count must match the size of the working `.tools/gowa/whatsapp` on this machine, which is the same artifact.

- [ ] **Step 6: Commit**

```bash
git add tools/start-login.js
git commit -m "feat: download GOWA for the running OS instead of two hardcoded platforms"
```

---

### Task 3: The launcher starts every service it finds

**Files:**
- Create: `tools/services.js`
- Test: `tools/test/services.test.js`
- Modify: `tools/start-login.js` (service table, `--no-calls`, `--calls-port`, `--list-services`, banner, child lifecycle)

**Interfaces:**
- Consumes: nothing from Tasks 1-2.
- Produces:
  - `buildServiceList({ root, exists, options }): { services: Service[], skipped: { name, reason }[] }` where `Service = { name, kind: 'node'|'binary', dir, script?, args?, env?, required }`
  - `serviceEnv(service, context): { [name: string]: string }` — `context = { options, gowaUrl, deviceId, host }`
  - `SERVICE_DIRS.calls = 'WhatsappCallServer'`
  - Consumed by `start-login.js` and by the future calls server's own docs.

- [ ] **Step 1: Write the failing test**

Create `tools/test/services.test.js`:

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert');
const path = require('path');

const services = require('../services');

const ROOT = '/tmp/finto-repo';
const baseOptions = {
  bridgePort: 8585,
  webhookPort: 8586,
  callsPort: 8588,
  noBridge: false,
  noCalls: false,
};

function existsFrom(files) {
  const set = new Set(files);
  return (target) => set.has(target);
}

test('con l adapter presente si avviano adapter e calls', () => {
  const files = [
    path.join(ROOT, 'WhatsappBridge', 'server.js'),
    path.join(ROOT, 'WhatsappCallServer', 'server.js')
  ];
  const list = services.buildServiceList({
    root: ROOT, exists: existsFrom(files), options: baseOptions
  });
  assert.deepStrictEqual(list.services.map((s) => s.name), ['adapter', 'calls']);
  assert.deepStrictEqual(list.skipped, []);
  assert.ok(list.services.every((s) => s.kind === 'node'));
});

test('senza il secondo server lo si salta dicendo perche', () => {
  const files = [path.join(ROOT, 'WhatsappBridge', 'server.js')];
  const list = services.buildServiceList({
    root: ROOT, exists: existsFrom(files), options: baseOptions
  });
  assert.deepStrictEqual(list.services.map((s) => s.name), ['adapter']);
  assert.deepStrictEqual(list.skipped, [{
    name: 'calls', reason: 'WhatsappCallServer/server.js non esiste'
  }]);
});

test('--no-bridge e --no-calls tolgono il servizio e non lo segnalano', () => {
  const files = [
    path.join(ROOT, 'WhatsappBridge', 'server.js'),
    path.join(ROOT, 'WhatsappCallServer', 'server.js')
  ];
  const options = Object.assign({}, baseOptions, { noBridge: true, noCalls: true });
  const list = services.buildServiceList({
    root: ROOT, exists: existsFrom(files), options
  });
  assert.deepStrictEqual(list.services, []);
  assert.deepStrictEqual(list.skipped, []);
});

test('serviceEnv mappa le variabili dell adapter', () => {
  const env = services.serviceEnv(
    { name: 'adapter', kind: 'node', dir: path.join(ROOT, 'WhatsappBridge') },
    {
      options: Object.assign({}, baseOptions, { user: 'u', pass: 'p' }),
      gowaUrl: 'http://127.0.0.1:3000',
      deviceId: 'dev-1'
    }
  );
  assert.strictEqual(env.GOWA_URL, 'http://127.0.0.1:3000');
  assert.strictEqual(env.GOWA_DEVICE_ID, 'dev-1');
  assert.strictEqual(env.GOWA_USER, 'u');
  assert.strictEqual(env.GOWA_PASS, 'p');
  assert.strictEqual(env.BRIDGE_PORT, '8585');
  assert.strictEqual(env.WEBHOOK_PORT, '8586');
  assert.strictEqual(env.WEBHOOK_PUBLIC_URL, 'http://127.0.0.1:8586/webhook');
});

test('serviceEnv mappa le variabili del servizio chiamate', () => {
  const env = services.serviceEnv(
    { name: 'calls', kind: 'node', dir: path.join(ROOT, 'WhatsappCallServer') },
    { options: baseOptions, gowaUrl: 'http://127.0.0.1:3000', deviceId: '' }
  );
  assert.strictEqual(env.CALLS_PORT, '8588');
  assert.strictEqual(env.BRIDGE_PORT, undefined);
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node --test tools/test/services.test.js`

Expected: **FAIL** — `Cannot find module '../services'`.

- [ ] **Step 3: Implement `tools/services.js`**

```js
/**
 * tools/services.js
 *
 * Quali servizi compongono lo stack e come si avviano. Nessuno spawn qui:
 * questo modulo decide e descrive, `start-login.js` esegue.
 *
 * Aggiungere un servizio e' una voce in `SERVICE_DEFS`, non una modifica alla
 * logica di avvio. Un servizio `node` vive nel repo; un servizio `binary` e'
 * un eseguibile scaricato (come GOWA) e passa dalla stessa verifica del digest.
 */
'use strict';

const path = require('path');

const SERVICE_DIRS = {
  adapter: 'WhatsappBridge',
  calls: 'WhatsappCallServer'
};

const SERVICE_DEFS = [
  {
    name: 'adapter',
    kind: 'node',
    dir: SERVICE_DIRS.adapter,
    script: 'server.js',
    // L'adapter e' il pezzo che parla con l'app WP8: si spegne solo su richiesta.
    enabled: (options) => !options.noBridge
  },
  {
    name: 'calls',
    kind: 'node',
    dir: SERVICE_DIRS.calls,
    script: 'server.js',
    enabled: (options) => !options.noCalls
  }
];

/**
 * L'elenco ordinato dei servizi da avviare, piu' quelli saltati e il motivo.
 * `exists` arriva da fuori perche' i test non devono toccare il disco.
 */
function buildServiceList({ root, exists, options }) {
  const services = [];
  const skipped = [];

  for (const def of SERVICE_DEFS) {
    const dir = path.join(root, def.dir);
    if (!def.enabled(options || {})) continue;

    const script = def.script ? path.join(dir, def.script) : null;
    if (script && !exists(script)) {
      // Non e' un errore: il secondo server puo' non essere ancora stato
      // scritto, e lo stack principale deve partire lo stesso.
      skipped.push({ name: def.name, reason: `${def.dir}/${def.script} non esiste` });
      continue;
    }

    services.push({
      name: def.name,
      kind: def.kind,
      dir,
      script: def.script,
      required: def.name === 'adapter'
    });
  }

  return { services, skipped };
}

/** Le variabili d'ambiente di un servizio, gia' come stringhe. */
function serviceEnv(service, context) {
  const options = context.options || {};
  const base = {};

  if (service.name === 'adapter') {
    return Object.assign(base, {
      GOWA_URL: context.gowaUrl || '',
      GOWA_DEVICE_ID: context.deviceId || '',
      GOWA_USER: options.user || '',
      GOWA_PASS: options.pass || '',
      BRIDGE_PORT: String(options.bridgePort),
      WEBHOOK_PORT: String(options.webhookPort),
      WEBHOOK_PUBLIC_URL: `http://127.0.0.1:${options.webhookPort}/webhook`
    });
  }

  if (service.name === 'calls') {
    return Object.assign(base, {
      CALLS_PORT: String(options.callsPort),
      GOWA_URL: context.gowaUrl || ''
    });
  }

  return base;
}

module.exports = { SERVICE_DIRS, SERVICE_DEFS, buildServiceList, serviceEnv };
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `node --test tools/test/services.test.js`

Expected: `# pass 5`, `# fail 0`.

- [ ] **Step 5: Wire the table into `tools/start-login.js`**

Add to the imports:

```js
const services = require('./services');
```

Add the two options to `parseArgs`'s defaults object:

```js
    callsPort: 8588,
    noCalls: false,
    listServices: false,
```

Add the cases to the `switch`:

```js
      case '--calls-port': options.callsPort = Number(next()); break;
      case '--no-calls': options.noCalls = true; break;
      case '--list-services': options.listServices = true; break;
```

Add the validation next to the others in `parseArgs`:

```js
  if (!Number.isInteger(options.callsPort) || options.callsPort <= 0) fail('--calls-port non valida');
```

- [ ] **Step 6: Replace `startBridge` with a generic starter**

Delete `startBridge` and add:

```js
/** Avvia un servizio `node` del repo, prefissando ogni sua riga di log. */
function startNodeService(service, env) {
  const child = spawn(process.execPath, [service.script], {
    cwd: service.dir,
    env: Object.assign({}, process.env, env),
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const label = service.name === 'adapter' ? 'adattatore' : service.name;
  const echo = (stream) => {
    let buffer = '';
    stream.setEncoding('utf8');
    stream.on('data', (chunk) => {
      buffer += chunk;
      const lines = buffer.split('\n');
      buffer = lines.pop();
      for (const line of lines) if (line.trim()) console.log(`  [${label}] ${line}`);
    });
  };
  echo(child.stdout);
  echo(child.stderr);
  child.on('error', (err) => console.error(`  \u2716 ${label}: ${err.message}`));
  return child;
}
```

- [ ] **Step 7: Replace the bridge startup in `main()` with the service loop**

Replace this block:

```js
  if (!options.noBridge) {
    console.log('  →  avvio l\'adattatore per l\'app WP8\n');
    children.push(startBridge(options, baseUrl, deviceId));
  }
```

with:

```js
  const { services: toStart, skipped } = services.buildServiceList({
    root: ROOT,
    exists: (target) => fs.existsSync(target),
    options,
  });

  if (toStart.length > 0) console.log('  →  avvio i servizi\n');
  for (const service of toStart) {
    const env = services.serviceEnv(service, { options, gowaUrl: baseUrl, deviceId });
    children.push(startNodeService(service, env));
  }
  for (const entry of skipped) {
    console.log(`  ·  ${entry.name} non avviato: ${entry.reason}`);
  }
```

and handle `--list-services` right after the `--stop` handling in `main()`:

```js
  if (options.listServices) {
    const list = services.buildServiceList({
      root: ROOT,
      exists: (target) => fs.existsSync(target),
      options,
    });
    for (const service of list.services) console.log(`  avviabile: ${service.name} (${service.dir})`);
    for (const entry of list.skipped) console.log(`  saltato:   ${entry.name} (${entry.reason})`);
    return;
  }
```

- [ ] **Step 8: Update the banner to list what actually runs**

Replace the `if (!options.noBridge) { ... }` block inside `banner()` with:

```js
  for (const line of options.serviceLines || []) console.log(line);
```

and in `main()`, before calling `banner(...)`, build those lines from the resolved list:

```js
  const host = addresses.length > 0 ? addresses[0] : '127.0.0.1';
  options.serviceLines = [];
  if (toStart.some((s) => s.name === 'adapter')) {
    options.serviceLines.push(`  Adattatore per l'app ${host}:${options.bridgePort}  (TCP, AES-256-CBC+HMAC)`);
    options.serviceLines.push(`  Webhook GOWA→app     http://${host}:${options.webhookPort}/webhook`);
    options.serviceLines.push(`  Scoperta automatica  UDP 8587  (l'app trova questo computer da sola)`);
  }
  if (toStart.some((s) => s.name === 'calls')) {
    options.serviceLines.push(`  Servizio chiamate    ${host}:${options.callsPort}`);
  }
```

`banner()` must then be called **after** this, so move the `const { services: toStart, skipped } = ...` block above the `banner(...)` call (it only needs `options` and the filesystem, not `baseUrl`).

- [ ] **Step 9: Check it runs**

Run: `node tools/start-login.js --list-services`

Expected, on this machine today:

```
  avviabile: adapter (…/WhatsappBridge)
  saltato:   calls (WhatsappCallServer/server.js non esiste)
```

Then Run: `node tools/start-login.js --no-bridge --no-calls --help` and confirm the help still prints (it returns before doing anything else).

- [ ] **Step 10: Commit**

```bash
git add tools/services.js tools/test/services.test.js tools/start-login.js
git commit -m "feat: start every service the repo declares, and skip the ones that are absent"
```

---

### Task 4: Document the platforms, the new flags and the new gate

**Files:**
- Modify: `.agents/skills/run-the-login-server/SKILL.md`
- Modify: `README.md`, `README.it.md`
- Modify: `docs/superpowers/plans/2026-09-25-english-output-finish-screens-and-bugs.md`

**Interfaces:** none (documentation only).

- [ ] **Step 1: Update the skill's option table and "what the script does"**

In `.agents/skills/run-the-login-server/SKILL.md`, replace the `--download` paragraph:

```
   `--download` fetches the pinned official release for the platform and checks
   the published SHA-256 before unpacking it.
```

with:

```
   `--download` fetches the pinned official release for **the platform the script
   is running on** (`tools/download.js`), checks the published SHA-256, and
   unpacks it in-process with `node:zlib` — no `unzip`, no `tar`, so it also
   works on Windows. All eight published archives are covered:

   | OS / CPU | archive |
   | --- | --- |
   | macOS arm64 / Intel | `whatsapp_9.5.0_darwin_{arm64,amd64}.zip` |
   | Linux x64 / arm64 / armv7 / 386 | `whatsapp_9.5.0_linux_{amd64,arm64,armv7,386}.zip` |
   | Windows x64 / 386 | `whatsapp_9.5.0_windows_{amd64,386}.zip` |

   A pair with no published archive fails with the pair named, pointing at
   `--gowa` / `--url`. A digest mismatch is a hard failure.
```

Then add the new rows to the option table (after `--webhook-port`):

```
| `--calls-port <n>` | port for the second service (default 8588) |
| `--no-calls` | do not start the second service |
| `--list-services` | print which services would start and which are skipped, then exit |
```

and add a short section after "What the script does":

```markdown
## Services (`tools/services.js`)

The stack is a list, not two hardcoded children. `SERVICE_DEFS` declares each
service with `name`, `kind` (`node` = run from this repo, `binary` = a
downloaded executable) and `enabled(options)`. A `node` service whose script is
missing is **skipped with a reason**, not an error: that is how the second
server (`WhatsappCallServer/server.js`) can be added later without touching the
launcher — put the file there and it starts, gets its env
(`CALLS_PORT`, `GOWA_URL`) from `serviceEnv`, appears in the banner, and is
killed by `Ctrl-C` and `--stop` like the adapter. `--list-services` shows the
resolved list.
```

- [ ] **Step 2: Add the gate to the skill and to the guards list**

In the same file's "Rules" section, add:

```markdown
- **Run the tool tests too**: `node --test "tools/test/**/*.test.js"` covers the downloader
  (platform mapping, ZIP reader, digest table) and the service list. It is the
  fifth gate, next to the four `tools/check-*.js` guards.
```

- [ ] **Step 3: Update the two root READMEs**

In `README.md`, find the sentence that tells the reader to run
`node tools/start-login.js --download` (the "how to run" section) and add after it:

```markdown
`--download` fetches the official release for whatever OS and CPU you are on (macOS Intel/ARM,
Linux x64/arm64/armv7/386, Windows x64/386), verifies the published SHA-256, and unpacks it
without needing `unzip` or `tar`. Everything else in the stack is started from a declarative
list, so adding a second service to the repo is one entry in `tools/services.js`.
```

In `README.it.md`, in the corresponding place, add the same content in Italian:

```markdown
`--download` scarica la release ufficiale per il sistema e la CPU su cui stai girando (macOS
Intel/ARM, Linux x64/arm64/armv7/386, Windows x64/386), verifica il SHA-256 pubblicato e la
scompatta senza aver bisogno di `unzip` o `tar`. Il resto dello stack parte da un elenco
dichiarativo: aggiungere un secondo servizio al repo e' una voce in `tools/services.js`.
```

Keep the two files' heading counts and order identical — these are sentences inside existing
sections, not new sections.

- [ ] **Step 4: Patch the unexecuted English-output plan for the new `HELP` lines**

The `HELP` block in `tools/start-login.js` gained three lines, and
`docs/superpowers/plans/2026-09-25-english-output-finish-screens-and-bugs.md` transcribes that
block for translation. That plan is still unexecuted, so a line it does not know about would
stay Italian. Append these rows to the table that holds the `HELP` transcription:

```
| `--calls-port <n>     porta del secondo servizio (default 8588)` | `--calls-port <n>     port for the second service (default 8588)` |
| `--no-calls           non avviare il secondo servizio` | `--no-calls           do not start the second service` |
| `--list-services      elenca i servizi avviabili ed esce` | `--list-services      list the services that would start, then exit` |
```

Find the table by searching that plan for `--webhook-port` and insert the three rows after the
`--webhook-port` row.

- [ ] **Step 5: Run every gate and both suites**

Run:

```
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js
node --test "tools/test/**/*.test.js"
cd WhatsappBridge && npm test
```

Expected: four `OK:` lines (quote the `.md` count `check-docs.js` prints rather than assuming
one), then `# pass 17`, `# fail 0` for the tools suite (12 from Task 1, 5 from Task 3), then
`# pass 45`, `# fail 0` for the adapter.

- [ ] **Step 6: Commit**

```bash
git add .agents/skills/run-the-login-server/SKILL.md README.md README.it.md docs/superpowers/plans/2026-09-25-english-output-finish-screens-and-bugs.md
git commit -m "docs: record the per-OS download, the new flags and the fifth gate"
```

---

## Self-Review

**1. Spec coverage.** "Rendi lo script per eseguire il server in modo che scarichi i binari per OS in cui si è" → Task 1 (the table covers all eight published archives, `archiveKeyFor` is the single place that decides) and Task 2 (the call site reads `process.platform`/`process.arch`), with the real 11 MB download in Task 2 Step 5 as the acceptance proof. "Se aggiungi il 2° server fa partire/scaricare anche questo" → Task 3: `SERVICE_DEFS` declares it, a `node` service starts when its file exists, a `binary` service is fetched by the same `ensureArchive()` that Task 1 built (the `kind: 'binary'` path is the reason `ensureArchive` takes `key`/`dir`/`targetName` as parameters instead of hardcoding GOWA), and `--list-services`, the banner, the PID file and `--stop` all cover every service in the list. The user's choice of "second server in the same repo" is what the `WhatsappCallServer` entry encodes.

**2. Placeholder scan.** No TBD, no "add error handling", no "similar to Task N": every code step carries the full text. One test step writes a deliberately sloppy assertion and then shows the exact replacement line, because assembling an assertion from prose is where a test quietly stops testing anything.

**3. Type consistency.** `archiveKeyFor(platform, arch)`, `targetNameFor(platform)`, `releaseUrlFor(key)`, `sha256(buffer)`, `extractLargestEntry(buffer)`, `ensureArchive({ key, dir, targetName, expectedDigest, fetchImpl, log })` are defined in Task 1 and called with exactly those names and shapes in Task 2 (`installGowa`) and in the tests. `GOWA_ARCHIVES` keys match the eight table rows in Global Constraints, and the last test in Task 1 Step 5 cross-checks two of them against the publisher's own `checksums-macos.txt`. `buildServiceList({ root, exists, options })` and `serviceEnv(service, { options, gowaUrl, deviceId })` are defined in Task 3 and used with those names in `start-login.js`, in `--list-services` and in the banner. `options.callsPort` defaults to `8588` in `parseArgs` and is the value `serviceEnv` writes to `CALLS_PORT`.

**4. Known limits, stated rather than hidden.** The Linux and Windows rows cannot be executed on this Mac; they are covered by unit tests over the pure mapping and by extraction tests against archives built in both compression modes, and the real download is executed for one platform. The plan says so instead of claiming otherwise.
