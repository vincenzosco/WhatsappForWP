/**
 * tools/download.js
 *
 * Scarica, verifica e scompatta gli eseguibili di cui lo stack ha bisogno
 * (oggi GOWA), per qualunque sistema su cui lo script viene lanciato.
 *
 * Perche' non usa `unzip`: su Windows non esiste, e su Linux non e' detto. Node
 * sa gia' leggere un archivio ZIP per conto suo (il metodo dei dati compressi e'
 * lo stesso inflate di `node:zlib`), quindi l'estrazione avviene in processo e
 * lo script resta senza dipendenze da installare.
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
// goreleaser: `arm` e' armv7, `x64` e' amd64.
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
  if (!archive) throw new Error(`no GOWA archive for ${key}`);
  return `${RELEASE_BASE}/${GOWA_VERSION}/${archive.file}`;
}

function sha256(buffer) {
  return crypto.createHash('sha256').update(buffer).digest('hex');
}

// ─── Lettore ZIP ─────────────────────────────────────────────────────────────
//
// Un ZIP e' una directory centrale alla fine del file, con dentro nome e offset
// di ogni voce; i dati di ogni voce stanno dietro la propria intestazione
// locale, le cui lunghezze di nome/extra possono differire da quelle della
// directory: i dati cominciano dopo quelle *locali*.

const EOCD_SIGNATURE = 0x06054b50;
const CENTRAL_SIGNATURE = 0x02014b50;
const LOCAL_SIGNATURE = 0x04034b50;
const ZIP64_LOCATOR_SIGNATURE = 0x07064b50;

function findEndOfCentralDirectory(buffer) {
  // L'EOCD e' l'ultima struttura, ma un commento dell'archivio puo' seguirlo:
  // si cerca all'indietro nella finestra massima consentita dal formato.
  const lowest = Math.max(0, buffer.length - 65557);
  for (let at = buffer.length - 22; at >= lowest; at--) {
    if (at >= 0 && buffer.readUInt32LE(at) === EOCD_SIGNATURE) return at;
  }
  return -1;
}

function readCentralDirectory(buffer) {
  const eocd = findEndOfCentralDirectory(buffer);
  if (eocd < 0) throw new Error('not a ZIP archive (the end of the central directory is missing)');

  const count = buffer.readUInt16LE(eocd + 10);
  const size = buffer.readUInt32LE(eocd + 12);
  const offset = buffer.readUInt32LE(eocd + 16);

  // Con ZIP64 questi campi valgono 0xffffffff e i valori veri stanno altrove:
  // leggerli lo stesso produrrebbe un binario corrotto in silenzio.
  const locator = eocd - 20;
  if (locator >= 0 && buffer.readUInt32LE(locator) === ZIP64_LOCATOR_SIGNATURE) {
    throw new Error('ZIP64 archive not supported');
  }
  if (offset === 0xffffffff || size === 0xffffffff || count === 0xffff) {
    throw new Error('ZIP64 archive not supported');
  }

  const entries = [];
  let cursor = offset;
  for (let i = 0; i < count; i++) {
    if (cursor + 46 > buffer.length || buffer.readUInt32LE(cursor) !== CENTRAL_SIGNATURE) {
      throw new Error('invalid ZIP central directory');
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
    throw new Error(`invalid local header for ${entry.name}`);
  }
  const nameLength = buffer.readUInt16LE(at + 26);
  const extraLength = buffer.readUInt16LE(at + 28);
  const start = at + 30 + nameLength + extraLength;

  if (start + entry.compressedSize > buffer.length) {
    throw new Error(`truncated archive: ${entry.name} falls outside the file`);
  }
  const raw = buffer.subarray(start, start + entry.compressedSize);

  if (entry.method === 0) return Buffer.from(raw);
  if (entry.method === 8) return zlib.inflateRawSync(raw);
  throw new Error(`ZIP compression method not supported (${entry.method}) for ${entry.name}`);
}

/**
 * Il contenuto della voce piu' grande che non sia una cartella.
 * Gli archivi di una release contengono un eseguibile e poco altro: prendere il
 * piu' grande evita di dover sapere come si chiama il file dentro lo zip su ogni
 * piattaforma.
 */
function extractLargestEntry(buffer) {
  if (!Buffer.isBuffer(buffer) || buffer.length < 22) {
    throw new Error('not a ZIP archive (too short)');
  }

  const entries = readCentralDirectory(buffer)
    .filter((entry) => entry.uncompressedSize > 0 && !entry.name.endsWith('/'));
  if (entries.length === 0) throw new Error('empty ZIP archive');

  let chosen = entries[0];
  for (const entry of entries) {
    if (entry.uncompressedSize > chosen.uncompressedSize) chosen = entry;
  }
  return { name: chosen.name, data: readEntryData(buffer, chosen) };
}

// ─── Installazione ───────────────────────────────────────────────────────────

/**
 * Scarica l'archivio, ne verifica il digest pubblicato, estrae l'eseguibile e
 * lo rende eseguibile. Non tocca il file di destinazione se il download o la
 * verifica falliscono: prima si valida, poi si scrive.
 */
async function ensureArchive({ key, dir, targetName, expectedDigest, fetchImpl, log }) {
  const archive = GOWA_ARCHIVES[key];
  if (!archive) throw new Error(`no GOWA archive for ${key}`);

  const expected = expectedDigest || archive.sha256;
  const url = releaseUrlFor(key);
  const doFetch = fetchImpl || fetch;
  if (typeof log === 'function') log(`downloading ${archive.file} ...`);

  const response = await doFetch(url, { signal: AbortSignal.timeout(600000) });
  if (!response.ok) throw new Error(`download failed (${response.status}) from ${url}`);

  const buffer = Buffer.from(await response.arrayBuffer());
  const digest = sha256(buffer);
  if (digest !== expected) {
    throw new Error(`unexpected SHA-256: ${digest}\n     expected: ${expected}`);
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

  if (typeof log === 'function') log(`installed ${path.basename(target)} from ${entry.name}`);
  return { target, entryName: entry.name, bytes: entry.data.length };
}

module.exports = {
  GOWA_VERSION,
  GOWA_ARCHIVES,
  archiveKeyFor,
  targetNameFor,
  releaseUrlFor,
  sha256,
  extractLargestEntry,
  ensureArchive
};
