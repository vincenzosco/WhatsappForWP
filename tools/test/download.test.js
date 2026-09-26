'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');

const download = require('../download');

// ─── Tabella delle piattaforme ───────────────────────────────────────────────

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

// ─── Lettore ZIP ─────────────────────────────────────────────────────────────

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
  assert.throws(() => download.extractLargestEntry(eocd), /ZIP vuoto|non e' un archivio ZIP/);
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

// ─── Installazione ───────────────────────────────────────────────────────────

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
