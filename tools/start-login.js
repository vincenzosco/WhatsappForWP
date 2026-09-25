#!/usr/bin/env node
/**
 * tools/start-login.js
 *
 * Avvia in locale l'intero stack di login WhatsApp per il progetto e disegna il
 * QR di login nel terminale:
 *
 *   1. GOWA (go-whatsapp-web-multidevice) — è lui che parla con WhatsApp;
 *   2. l'adattatore `WhatsappBridge` — è lui che parla con l'app WP8;
 *   3. il QR (o il codice di abbinamento) da inquadrare/inserire sul telefono.
 *
 * Serve perché senza una sessione WhatsApp attiva l'app WP8 non mostra nulla, e
 * perché il QR di WhatsApp va inquadrato da un telefono: va quindi disegnato
 * grande, quadrato e con colori espliciti (vedi tools/qr-term.js). GOWA
 * pubblica un QR nuovo ogni ~20s sotto `.tools/gowa/statics/qrcode/`: lo script
 * guarda quella cartella e rinnova il disegno finché il telefono non completa il
 * collegamento.
 *
 * La sessione WhatsApp finisce in `.tools/gowa/storages/whatsapp.db`, che è
 * ignorato da git: i lanci successivi si riconnettono da soli, senza QR.
 *
 * Usage:
 *   node tools/start-login.js                       # avvia tutto e mostra il QR
 *   node tools/start-login.js --download            # scarica prima GOWA
 *   node tools/start-login.js --code 393401234567   # login con codice invece del QR
 *   node tools/start-login.js --no-bridge           # solo GOWA + QR
 *   node tools/start-login.js --once                # un solo QR ed esce
 *   node tools/start-login.js --stop                # ferma lo stack avviato prima
 */
'use strict';

const { spawn, spawnSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const qrTerm = require('./qr-term');

const ROOT = path.resolve(__dirname, '..');
const GOWA_DIR = path.join(ROOT, '.tools', 'gowa');
const GOWA_LOG = path.join(GOWA_DIR, 'gowa.log');
const GOWA_QR_DIR = path.join(GOWA_DIR, 'statics', 'qrcode');
const PID_FILE = path.join(GOWA_DIR, 'login-stack.pid');
const BRIDGE_DIR = path.join(ROOT, 'WhatsappBridge');

const GOWA_VERSION = 'v9.5.0';
// Binari ufficiali della release, con digest pubblicato da GOWA: si accetta il
// download solo se l'archivio corrisponde.
const GOWA_RELEASES = {
  'darwin-arm64': {
    file: 'whatsapp_9.5.0_darwin_arm64.zip',
    sha256: '0a5639e0608aaae3e1c7977a16303b782c2ea3ff7be8f73ecf0bed89adf7a444',
    binary: 'darwin-arm64',
  },
  'darwin-x64': {
    file: 'whatsapp_9.5.0_darwin_amd64.zip',
    sha256: 'b57d6fa46bbef88fb3dd1708174d4e42cdcae7dea70250961a3f70f7c06e207b',
    binary: 'darwin-amd64',
  },
};

const HELP = `Avvia GOWA + l'adattatore WP8 e mostra il QR di login nel terminale.

Uso: node tools/start-login.js [opzioni]

  --download            scarica GOWA ${GOWA_VERSION} in .tools/gowa (verifica SHA-256)
  --code <numero>       login con codice di abbinamento (prefisso internazionale)
  --url <base>          usa un GOWA già avviato invece di avviarne uno
  --port <n>            porta di GOWA (default 3000)
  --bridge-port <n>     porta TCP per l'app WP8 (default 8585)
  --webhook-port <n>    porta del webhook GOWA -> adattatore (default 8586)
  --gowa <percorso>     percorso alternativo dell'eseguibile GOWA
  --no-bridge           non avviare l'adattatore (solo GOWA + QR)
  --once                disegna un solo QR ed esce
  --no-qr               avvia lo stack senza disegnare il QR: il login si fa
                        dal telefono, nell'app (consigliato)
  --open-qr             apre il PNG del codice con Anteprima (si aggiorna da solo)
  --plain / --ansi      disegno senza colori / a colori (default: colori su TTY)
  --quiet-zone <n>      margine bianco attorno al QR (default 4)
  --ui                  serve anche la dashboard web di GOWA (default: no)
  --gowa-user <utente>  Basic Auth di GOWA (con --gowa-pass)
  --gowa-pass <pass>
  --stop                ferma lo stack avviato da questo script
  --help
`;

function fail(message) {
  console.error(`\n  ✖  ${message}\n`);
  process.exit(1);
}

function parseArgs(argv) {
  const options = {
    port: 3000,
    bridgePort: 8585,
    webhookPort: 8586,
    quietZone: qrTerm.DEFAULT_QUIET_ZONE,
    download: false,
    ui: false,
    once: false,
    noBridge: false,
    noQr: false,
    openQr: false,
    stop: false,
    plain: undefined,
    phone: undefined,
    url: undefined,
    binary: undefined,
    user: '',
    pass: '',
  };

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    const next = () => {
      const value = argv[++i];
      if (value === undefined) fail(`${arg} richiede un valore`);
      return value;
    };
    switch (arg) {
      case '--download': options.download = true; break;
      case '--code': options.phone = next(); break;
      case '--url': options.url = next().replace(/\/+$/, ''); break;
      case '--port': options.port = Number(next()); break;
      case '--bridge-port': options.bridgePort = Number(next()); break;
      case '--webhook-port': options.webhookPort = Number(next()); break;
      case '--gowa': options.binary = path.resolve(next()); break;
      case '--quiet-zone': options.quietZone = Number(next()); break;
      case '--gowa-user': options.user = next(); break;
      case '--gowa-pass': options.pass = next(); break;
      case '--no-bridge': options.noBridge = true; break;
      case '--once': options.once = true; break;
      case '--no-qr': options.noQr = true; break;
      case '--open-qr': options.openQr = true; break;
      case '--ui': options.ui = true; break;
      case '--plain': options.plain = true; break;
      case '--ansi': options.plain = false; break;
      case '--stop': options.stop = true; break;
      case '--help': case '-h': options.help = true; break;
      default: fail(`opzione sconosciuta: ${arg} (usa --help)`);
    }
  }

  if (!Number.isInteger(options.port) || options.port <= 0) fail('--port non valida');
  if (!Number.isInteger(options.bridgePort) || options.bridgePort <= 0) fail('--bridge-port non valida');
  if (!Number.isInteger(options.webhookPort) || options.webhookPort <= 0) fail('--webhook-port non valida');
  if (!Number.isInteger(options.quietZone) || options.quietZone < 0) fail('--quiet-zone non valida');
  if (options.plain === undefined) options.plain = !process.stdout.isTTY;
  return options;
}

// ─── Utilità ─────────────────────────────────────────────────────────────────

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function stamp() {
  return new Date().toTimeString().slice(0, 8);
}

function lanAddresses() {
  const interfaces = os.networkInterfaces();
  const nice = [];
  const other = [];
  // Le interfacce virtuali di macOS (VPN, AirDrop, hotspot) non sono raggiungibili
  // dal telefono: si tengono per ultime e si preferiscono en0/en1.
  const virtual = /^(utun|awdl|llw|bridge|ap\d|gif|stf|xhci|anpi|vmenet|docker)/;
  for (const name of Object.keys(interfaces)) {
    for (const info of interfaces[name] || []) {
      if (info.family !== 'IPv4' || info.internal) continue;
      const entry = { name, address: info.address };
      if (virtual.test(name)) other.push(entry);
      else nice.push(entry);
    }
  }
  return nice.concat(other).map((entry) => entry.address);
}

async function requestJson(baseUrl, requestPath, options) {
  const opts = options || {};
  const headers = Object.assign({}, opts.headers || {});
  if (opts.json !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch(`${baseUrl}${requestPath}`, {
    method: opts.method || 'GET',
    headers,
    body: opts.json === undefined ? undefined : JSON.stringify(opts.json),
    signal: AbortSignal.timeout(opts.timeoutMs || 30000),
  });
  const text = await response.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch (err) { data = null; }
  return { ok: response.ok, status: response.status, data };
}

function deviceHeaders(deviceId, options) {
  const headers = {};
  if (deviceId) headers['X-Device-Id'] = deviceId;
  if (options.user) {
    headers.Authorization = 'Basic ' + Buffer.from(`${options.user}:${options.pass}`, 'utf8').toString('base64');
  }
  return headers;
}

/** Crea (o riusa) il device GOWA: tutte le rotte di login lo richiedono. */
async function ensureDevice(baseUrl, options) {
  const list = await requestJson(baseUrl, '/devices', { headers: deviceHeaders(null, options) });
  const results = list.data && list.data.results;
  const devices = Array.isArray(results) ? results : (results && results.data) || [];
  if (devices.length > 0 && devices[0].id) return devices[0].id;

  const created = await requestJson(baseUrl, '/devices', {
    method: 'POST',
    json: {},
    headers: deviceHeaders(null, options),
  });
  const id = created.data && created.data.results && created.data.results.id;
  if (!id) fail(`GOWA non ha creato nessun device: ${JSON.stringify(created.data)}`);
  return id;
}

async function statusOf(baseUrl, deviceId, options) {
  const response = await requestJson(baseUrl, '/app/status', {
    headers: deviceHeaders(deviceId, options),
    timeoutMs: 10000,
  });
  const results = (response.data && response.data.results) || {};
  return {
    isLoggedIn: !!results.is_logged_in,
    isConnected: !!results.is_connected,
    jid: results.jid || '',
  };
}

async function waitForHealth(baseUrl, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`${baseUrl}/health`, { signal: AbortSignal.timeout(2000) });
      if (response.ok) return true;
    } catch (err) { /* non è ancora in ascolto */ }
    await sleep(400);
  }
  return false;
}

// ─── GOWA ────────────────────────────────────────────────────────────────────

function platformKey() {
  if (process.platform === 'darwin') {
    return process.arch === 'arm64' ? 'darwin-arm64' : (process.arch === 'x64' ? 'darwin-x64' : null);
  }
  return null;
}

async function downloadGowa() {
  const key = platformKey();
  if (!key) {
    fail(`nessun binario precompilato per ${process.platform}/${process.arch}: scarica GOWA da GitHub e usa --gowa <percorso>`);
  }
  const release = GOWA_RELEASES[key];
  const url = `https://github.com/aldinokemal/go-whatsapp-web-multidevice/releases/download/${GOWA_VERSION}/${release.file}`;
  console.log(`  ↓  scarico ${release.file} ...`);

  const response = await fetch(url, { signal: AbortSignal.timeout(180000) });
  if (!response.ok) fail(`download fallito (${response.status}) da ${url}`);
  const buffer = Buffer.from(await response.arrayBuffer());

  const crypto = require('crypto');
  const digest = crypto.createHash('sha256').update(buffer).digest('hex');
  if (digest !== release.sha256) {
    fail(`SHA-256 inatteso: ${digest}\n     atteso: ${release.sha256}`);
  }

  fs.mkdirSync(GOWA_DIR, { recursive: true });
  const zipPath = path.join(GOWA_DIR, release.file);
  fs.writeFileSync(zipPath, buffer);
  const unzip = spawnSync('unzip', ['-o', zipPath, '-d', GOWA_DIR], { encoding: 'utf8' });
  if (unzip.status !== 0) fail(`unzip non riuscito: ${unzip.stderr || unzip.stdout}`);
  fs.rmSync(zipPath, { force: true });

  const extracted = path.join(GOWA_DIR, release.binary);
  const target = gowaBinary({ binary: path.join(GOWA_DIR, 'whatsapp') });
  fs.renameSync(extracted, target);
  fs.chmodSync(target, 0o755);
  console.log(`  ✔  GOWA ${GOWA_VERSION} installato in ${path.relative(ROOT, target)} (SHA-256 verificato)`);
}

function gowaBinary(options) {
  if (options.binary) return options.binary;
  return path.join(GOWA_DIR, process.platform === 'win32' ? 'whatsapp.exe' : 'whatsapp');
}

function startGowa(binary, options) {
  fs.mkdirSync(GOWA_DIR, { recursive: true });
  const args = [
    'rest',
    `--port=${options.port}`,
    // Solo in locale: l'API di GOWA non ha autenticazione per default, e da lì
    // si possono inviare messaggi con l'account collegato.
    `--host=127.0.0.1`,
    `--ui-enabled=${options.ui ? 'true' : 'false'}`,
  ];
  if (options.user) args.push(`--basic-auth=${options.user}:${options.pass}`);

  const logFd = fs.openSync(GOWA_LOG, 'w');
  const child = spawn(binary, args, {
    cwd: GOWA_DIR, // GOWA scrive storages/ e statics/ nella cartella corrente
    stdio: ['ignore', logFd, logFd],
    detached: false,
  });
  child.on('error', (err) => fail(`non riesco ad avviare GOWA: ${err.message}`));
  return child;
}

/** I PNG dei QR vecchi restano sul disco se GOWA viene fermato prima della
 *  scadenza: all'avvio si buttano, così il disegno non mostra un codice morto. */
function clearQrFiles() {
  let entries;
  try {
    entries = fs.readdirSync(GOWA_QR_DIR);
  } catch (err) {
    return 0;
  }
  let removed = 0;
  for (const name of entries) {
    if (!/^scan-qr-.*\.png$/.test(name)) continue;
    try {
      fs.rmSync(path.join(GOWA_QR_DIR, name), { force: true });
      removed++;
    } catch (err) { /* in uso da GOWA */ }
  }
  return removed;
}

function newestQrFile(since) {
  let entries;
  try {
    entries = fs.readdirSync(GOWA_QR_DIR);
  } catch (err) {
    return null;
  }
  let newest = null;
  let newestTime = since || 0;
  for (const name of entries) {
    if (!/^scan-qr-.*\.png$/.test(name)) continue;
    const full = path.join(GOWA_QR_DIR, name);
    let stat;
    try { stat = fs.statSync(full); } catch (err) { continue; }
    if (stat.mtimeMs >= newestTime) {
      newestTime = stat.mtimeMs;
      newest = full;
    }
  }
  return newest;
}

// ─── Adattatore WP8 ──────────────────────────────────────────────────────────

function startBridge(options, baseUrl, deviceId) {
  const env = Object.assign({}, process.env, {
    GOWA_URL: baseUrl,
    GOWA_DEVICE_ID: deviceId || '',
    GOWA_USER: options.user,
    GOWA_PASS: options.pass,
    BRIDGE_PORT: String(options.bridgePort),
    WEBHOOK_PORT: String(options.webhookPort),
    WEBHOOK_PUBLIC_URL: `http://127.0.0.1:${options.webhookPort}/webhook`,
  });
  const child = spawn(process.execPath, ['server.js'], { cwd: BRIDGE_DIR, env, stdio: ['ignore', 'pipe', 'pipe'] });
  const echo = (stream) => {
    let buffer = '';
    stream.setEncoding('utf8');
    stream.on('data', (chunk) => {
      buffer += chunk;
      const lines = buffer.split('\n');
      buffer = lines.pop();
      for (const line of lines) if (line.trim()) console.log(`  [adattatore] ${line}`);
    });
  };
  echo(child.stdout);
  echo(child.stderr);
  child.on('error', (err) => console.error(`  ✖ adattatore: ${err.message}`));
  return child;
}

// ─── Disegno ─────────────────────────────────────────────────────────────────

/** Apre il PNG nel visualizzatore di sistema: quello che si vede in Anteprima
 *  viene ricaricato dal disco a ogni rotazione del codice. */
function openInViewer(file) {
  if (process.platform !== 'darwin') {
    console.log(`  ·  --open-qr: apri a mano ${path.relative(ROOT, file)}`);
    return;
  }
  try {
    spawn('open', [file], { stdio: 'ignore', detached: true }).unref();
  } catch (err) {
    console.log(`  ·  --open-qr: non riesco ad aprire il codice (${err.message})`);
  }
}

/**
 * Il codice e' piu' grande della finestra (o l'output non e' un terminale):
 * si spiega e si indica il PNG, mai un disegno incompleto: un QR tagliato non
 * si puo' inquadrare, e sembra solo un errore di visualizzazione.
 */
function printTooSmall(printer, lines, caption, hint) {
  if (printer.warned) return;
  printer.warned = true;

  const columns = process.stdout.columns || 0;
  const rows = process.stdout.rows || 0;
  console.log(`\n  ${caption}`);
  if (process.stdout.isTTY && columns && rows) {
    console.log(`     Il codice occupa ${lines.length} righe e ${lines[0].length} colonne;`);
    console.log(`     questa finestra ne ha ${rows} x ${columns}.`);
    console.log('     Ridimensionala, o premi Cmd - per rimpicciolire il testo: il prossimo');
    console.log('     codice verra\' disegnato qui.');
  }
  if (hint) console.log(`     Oppure: ${hint}`);
  console.log('     Oppure: --no-qr, e fai il login dal telefono nell\'app.');
}

function makePrinter(options) {
  const printer = {
    warned: false,
    drawnLines: 0,

    /**
     * Disegna il codice, riscrivendo sopra il precedente solo se ci sta tutto
     * nella finestra: una riga che va a capo, o un disegno piu' alto dello
     * schermo, sposterebbe il cursore e lascerebbe residui in giro.
     */
    render(lines, caption, hint) {
      const columns = process.stdout.columns || 0;
      const rows = process.stdout.rows || 0;
      const canRedraw = !options.plain && !!process.stdout.isTTY
        && (columns === 0 || columns >= lines[0].length + 2)
        && (rows === 0 || rows >= lines.length + 2);

      if (!canRedraw) {
        if (printer.warned) {
          // Senza questa riga, dopo il primo avviso il log resta muto per
          // minuti e sembra che lo script si sia piantato.
          console.log(`  · nuovo codice alle ${stamp()} (non disegnato: la finestra e' troppo piccola)`);
          printer.drawnLines = 0;
          return;
        }
        printTooSmall(printer, lines, caption, hint);
        printer.drawnLines = 0;
        return;
      }

      if (printer.drawnLines > 0) process.stdout.write(`\x1b[${printer.drawnLines}A`);
      const output = [`\x1b[2K  ${caption}\n`];
      for (const line of lines) output.push(`\x1b[2K${line}\n`);
      process.stdout.write(output.join(''));
      printer.drawnLines = lines.length + 1;
    },

    done() {
      printer.drawnLines = 0;
    },
  };
  return printer;
}

function banner(options, addresses, baseUrl, deviceId) {
  const host = addresses.length > 0 ? addresses[0] : '127.0.0.1';
  const line = '─'.repeat(66);
  console.log(`\n${line}`);
  console.log('  WhatsApp per Windows Phone 8.1 — server di login in locale');
  console.log(line);
  console.log(`  GOWA (WhatsApp)      ${baseUrl}  (${GOWA_VERSION})`);
  if (!options.noBridge) {
    console.log(`  Adattatore per l'app ${host}:${options.bridgePort}  (TCP, AES-256-GCM)`);
    console.log(`  Webhook GOWA→app     http://${host}:${options.webhookPort}/webhook`);
    console.log(`  Scoperta automatica  UDP 8587  (l'app trova questo computer da sola)`);
  }
  console.log(`  Sessioni             .tools/gowa/storages/whatsapp.db`);
  console.log(`  Log GOWA             .tools/gowa/gowa.log`);
  console.log(line);
  if (addresses.length > 1) {
    console.log(`  Indirizzi di questa macchina: ${addresses.join(', ')}`);
  }
  if (deviceId) console.log(`  Device GOWA: ${deviceId}`);
  console.log(line);
}

// ─── Ciclo di login ──────────────────────────────────────────────────────────

/** Porta il PNG del QR su disco: file locale oppure scaricato dal link di GOWA. */
async function loadQrPng(source) {
  if (source.file) return source.file;
  const target = path.join(os.tmpdir(), 'gowa-qr.png');
  const response = await fetch(source.url, { signal: AbortSignal.timeout(15000) });
  if (!response.ok) throw new Error(`QR non scaricabile (${response.status})`);
  fs.writeFileSync(target, Buffer.from(await response.arrayBuffer()));
  return target;
}

async function showQr(baseUrl, deviceId, options, printer, source) {
  const pngPath = await loadQrPng(source);
  const qr = qrTerm.qrFromPng(pngPath, { quietZone: options.quietZone, plain: options.plain });
  const imageUrl = source.url
    || `${baseUrl}/statics/qrcode/${path.basename(source.file)}`;

  // Il codice ruota ogni ~20s: il PNG si tiene in un percorso fisso, cosi'
  // chi lo apre con Anteprima lo vede aggiornarsi invece di invecchiare.
  const stable = path.join(GOWA_DIR, 'login-qr.png');
  try {
    fs.copyFileSync(pngPath, stable);
  } catch (err) {
    // il PNG di GOWA puo' sparire a meta' copia quando scade: non e' fatale
  }
  if (options.openQr && !options.qrOpened) {
    options.qrOpened = true;
    openInViewer(stable);
  }

  printer.render(qr.lines, `QR aggiornato alle ${stamp()} — ${qr.count} moduli, ` +
    `ricostruzione ${(qr.error * 100).toFixed(2)}%`,
    `apri ${path.relative(ROOT, stable)} con Anteprima (si aggiorna da solo), o ${imageUrl}`);
  return qr;
}

async function showPairCode(baseUrl, deviceId, options, printer) {
  const response = await requestJson(
    baseUrl,
    `/app/login-with-code?phone=${encodeURIComponent(options.phone)}`,
    { headers: deviceHeaders(deviceId, options), timeoutMs: 60000 },
  );
  if (!response.ok) {
    const message = (response.data && (response.data.message || response.data.code)) || `HTTP ${response.status}`;
    fail(`codice di abbinamento non disponibile: ${message}`);
  }
  const code = response.data.results.pair_code;
  const lines = [
    '',
    `        Codice di abbinamento:  ${code}`,
    '',
    '      Sul telefono: WhatsApp → Impostazioni → Dispositivi collegati →',
    "      Collega un dispositivo → Collega con numero di telefono.",
    '',
  ];
  printer.render(lines, `codice generato alle ${stamp()} (valido ~2 minuti)`);
  return code;
}

async function requestLogin(baseUrl, deviceId, options) {
  const response = await requestJson(baseUrl, '/app/login', {
    headers: deviceHeaders(deviceId, options),
    timeoutMs: 60000,
  });
  const code = response.data && response.data.code;
  if (!response.ok && code === 'ALREADY_LOGGED_IN') return { alreadyLoggedIn: true };
  if (!response.ok) {
    const message = (response.data && (response.data.message || code)) || `HTTP ${response.status}`;
    throw new Error(message);
  }
  return { qrLink: response.data.results.qr_link, duration: response.data.results.qr_duration || 30 };
}

/**
 * GOWA pubblica un PNG nuovo a ogni QR (circa ogni 20s) e cancella il vecchio:
 * si segue la cartella invece di richiamare /app/login, che riavvia il flusso.
 */
function reportLoginError(printer, err) {
  printer.done();
  console.error(`  ✖ login: ${err.message}`);
}

async function waitForLogin(state) {
  const { baseUrl, deviceId, options, printer, gowaLocal, startedAt } = state;
  // Finestra del flusso QR di GOWA: primo codice 60s + cinque da 20s.
  const WINDOW_MS = 170000;
  let lastFile = null;
  let lastRequest = 0;

  for (;;) {
    if (options.noQr) {
      const status = await statusOf(baseUrl, deviceId, options).catch(() => null);
      if (status && status.isLoggedIn) return status;
      if (!state.reportedWaiting) {
        state.reportedWaiting = true;
        console.log(`\n  Il login si fa dal telefono: apri l'app e inquadra il codice che mostra.` +
          `\n  In attesa del collegamento...`);
      }
      await sleep(2000);
      continue;
    }

    const status = await statusOf(baseUrl, deviceId, options).catch(() => null);
    if (status && status.isLoggedIn) return status;

    try {
      if (options.phone) {
        if (Date.now() - lastRequest > 90000) {
          await showPairCode(baseUrl, deviceId, options, printer);
          lastRequest = Date.now();
        }
      } else if (gowaLocal) {
        // GOWA pubblica un PNG nuovo a ogni QR: si segue la cartella invece di
        // richiamare /app/login, che riavvierebbe il flusso da capo.
        const file = newestQrFile(startedAt);
        if (file && file !== lastFile) {
          lastFile = file;
          await showQr(baseUrl, deviceId, options, printer, { file });
          lastRequest = Date.now();
        } else if (lastRequest === 0 || Date.now() - lastRequest > WINDOW_MS) {
          await requestLogin(baseUrl, deviceId, options);
          lastRequest = Date.now();
        }
      } else if (lastRequest === 0 || Date.now() - lastRequest > WINDOW_MS) {
        // GOWA remoto: la cartella non è visibile, quindi si richiede il QR (ogni
        // richiesta riavvia il flusso) e si disegna quello restituito.
        const login = await requestLogin(baseUrl, deviceId, options);
        if (login.alreadyLoggedIn) {
          const again = await statusOf(baseUrl, deviceId, options);
          if (again.isLoggedIn) return again;
        } else {
          await showQr(baseUrl, deviceId, options, printer, { url: login.qrLink });
        }
        lastRequest = Date.now();
      }
    } catch (err) {
      reportLoginError(printer, err);
      await sleep(3000);
    }

    await sleep(1000);
  }
}

// ─── Avvio ───────────────────────────────────────────────────────────────────

function writePidFile(children) {
  try {
    fs.mkdirSync(GOWA_DIR, { recursive: true });
    fs.writeFileSync(PID_FILE, JSON.stringify({
      writtenAt: new Date().toISOString(),
      // Il primo e' questo processo: fermandolo lui ferma i figli e pulisce.
      launcherPid: process.pid,
      pids: children.map((child) => child.pid).filter(Boolean),
    }, null, 2));
  } catch (err) { /* non è essenziale */ }
}

function stopStack() {
  let parsed = null;
  try {
    parsed = JSON.parse(fs.readFileSync(PID_FILE, 'utf8'));
  } catch (err) {
    console.log('Nessuno stack da fermare (file dei PID assente).');
    return;
  }

  // Prima chi ha avviato lo stack (che a sua volta ferma i figli), poi i figli:
  // così funziona anche se lo script gira in un'altra finestra del terminale.
  const targets = [];
  if (parsed.launcherPid && parsed.launcherPid !== process.pid) targets.push(parsed.launcherPid);
  for (const pid of parsed.pids || []) if (!targets.includes(pid)) targets.push(pid);

  for (const pid of targets) {
    try {
      process.kill(pid, 'SIGTERM');
      console.log(`  ✔ fermato processo ${pid}`);
    } catch (err) {
      console.log(`  · processo ${pid} non attivo`);
    }
  }
  fs.rmSync(PID_FILE, { force: true });
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    console.log(HELP);
    return;
  }
  if (options.stop) {
    stopStack();
    return;
  }

  const children = [];
  let stopping = false;
  const shutdown = () => {
    if (stopping) return;
    stopping = true;
    console.log('\n  … arresto in corso');
    for (const child of children) {
      try { child.kill('SIGTERM'); } catch (err) { /* già morto */ }
    }
    fs.rmSync(PID_FILE, { force: true });
    setTimeout(() => process.exit(0), 300);
  };
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);

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
    console.log(`  →  avvio GOWA ${GOWA_VERSION} sulla porta ${options.port} (log: .tools/gowa/gowa.log)`);
    const stale = clearQrFiles();
    if (stale > 0) console.log(`  ·  rimossi ${stale} QR scaduti dalla sessione precedente`);
    children.push(startGowa(binary, options));
    if (!(await waitForHealth(`http://127.0.0.1:${options.port}`, 20000))) {
      console.error(`\n  ✖ GOWA non risponde: ultime righe di ${path.relative(ROOT, GOWA_LOG)}`);
      try {
        console.error(fs.readFileSync(GOWA_LOG, 'utf8').trim().split('\n').slice(-8).join('\n'));
      } catch (err) { /* niente log */ }
      shutdown();
      return;
    }
  }

  const baseUrl = options.url || `http://127.0.0.1:${options.port}`;
  const deviceId = await ensureDevice(baseUrl, options);
  const addresses = lanAddresses();
  banner(options, addresses, baseUrl, deviceId);

  if (!options.noBridge) {
    console.log('  →  avvio l\'adattatore per l\'app WP8\n');
    children.push(startBridge(options, baseUrl, deviceId));
  }
  writePidFile(children);
  console.log(`  Per fermare tutto: Ctrl-C (oppure node tools/start-login.js --stop)`);

  const status = await statusOf(baseUrl, deviceId, options).catch(() => null);
  if (status && status.isLoggedIn) {
    console.log(`\n  ✔ WhatsApp è già collegato come ${status.jid || 'sconosciuto'}: nessun QR da inquadrare.`);
  } else {
    console.log('\n  Sul telefono: WhatsApp → Impostazioni → Dispositivi collegati → Collega un dispositivo');
    console.log('  e inquadra il codice qui sotto. Il primo QR dura ~60 s, poi ne arriva uno nuovo');
    console.log('  ogni ~20 s: il disegno si aggiorna da solo.');
    if (options.noQr) {
      console.log('\n  Nessun QR qui: loggati dal telefono, nell\'app (Ctrl-C per fermare).');
    }
  }

  if (options.once) {
    const printer = makePrinter(options);
    if (options.phone) await showPairCode(baseUrl, deviceId, options, printer);
    else {
      const login = await requestLogin(baseUrl, deviceId, options);
      if (login.alreadyLoggedIn) {
        console.log('  ✔ già collegato');
      } else {
        await showQr(baseUrl, deviceId, options, printer, { url: login.qrLink });
      }
    }
    shutdown();
    return;
  }

  const printer = makePrinter(options);
  const result = await waitForLogin({
    baseUrl, deviceId, options, printer, gowaLocal: local, startedAt: Date.now(),
  });
  printer.done();
  console.log(`\n  ✔ WhatsApp collegato come ${result.jid || 'sconosciuto'}`);
  if (!options.noBridge) {
    const host = addresses.length > 0 ? addresses[0] : '127.0.0.1';
    console.log(`     L'app WP8 può ora collegarsi a ${host}:${options.bridgePort} e usare questa sessione.`);
  }
  console.log('     Lo stack resta attivo: Ctrl-C per fermarlo.');
}

main().catch((err) => {
  console.error(`\n  ✖ ${err && err.message ? err.message : err}\n`);
  process.exit(1);
});
