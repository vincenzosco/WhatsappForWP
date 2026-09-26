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

const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const qrTerm = require('./qr-term');
const downloader = require('./download');
const services = require('./services');

const ROOT = path.resolve(__dirname, '..');
const GOWA_DIR = path.join(ROOT, '.tools', 'gowa');
const GOWA_LOG = path.join(GOWA_DIR, 'gowa.log');
const GOWA_QR_DIR = path.join(GOWA_DIR, 'statics', 'qrcode');
const PID_FILE = path.join(GOWA_DIR, 'login-stack.pid');


// La tabella dei binari per sistema e CPU, e il download verificato, stanno in
// tools/download.js: qui resta solo il nome della versione da mostrare.
const GOWA_VERSION = downloader.GOWA_VERSION;

const HELP = `Starts GOWA plus the WP8 adapter and shows the login QR code in the terminal.

Usage: node tools/start-login.js [options]

  --download            downloads GOWA ${GOWA_VERSION} into .tools/gowa (verifies SHA-256)
  --code <number>       login with a pair code (international prefix)
  --url <base>          use an already running GOWA instead of starting one
  --port <n>            GOWA port (default 3000)
  --bridge-port <n>     TCP port for the WP8 app (default 8585)
  --webhook-port <n>    GOWA -> adapter webhook port (default 8586)
  --gowa <path>         alternative path to the GOWA binary
  --no-bridge           do not start the adapter (GOWA + QR only)
  --once                draw one QR code and exit
  --no-qr               start the stack without drawing the QR code: log in
                        from the phone, inside the app (recommended)
  --open-qr             open the QR PNG in Preview (it refreshes by itself)
  --plain / --ansi      drawing without colours / with colours (default: colours on a TTY)
  --quiet-zone <n>      white margin around the QR code (default 4)
  --ui                  also serve the GOWA web dashboard (default: no)
  --gowa-user <user>    GOWA Basic Auth (together with --gowa-pass)
  --gowa-pass <pass>
  --calls-port <n>      port for the second service (default 8588)
  --no-calls            do not start the second service
  --list-services       list the services that would start, then exit
  --stop                stop the stack started by this script
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
    callsPort: 8588,
    noCalls: false,
    listServices: false,
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
      if (value === undefined) fail(`${arg} requires a value`);
      return value;
    };
    switch (arg) {
      case '--download': options.download = true; break;
      case '--code': options.phone = next(); break;
      case '--url': options.url = next().replace(/\/+$/, ''); break;
      case '--port': options.port = Number(next()); break;
      case '--bridge-port': options.bridgePort = Number(next()); break;
      case '--webhook-port': options.webhookPort = Number(next()); break;
      case '--calls-port': options.callsPort = Number(next()); break;
      case '--no-calls': options.noCalls = true; break;
      case '--list-services': options.listServices = true; break;
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
      default: fail(`unknown option: ${arg} (use --help)`);
    }
  }

  if (!Number.isInteger(options.port) || options.port <= 0) fail('--port is not valid');
  if (!Number.isInteger(options.bridgePort) || options.bridgePort <= 0) fail('--bridge-port is not valid');
  if (!Number.isInteger(options.webhookPort) || options.webhookPort <= 0) fail('--webhook-port is not valid');
  if (!Number.isInteger(options.callsPort) || options.callsPort <= 0) fail('--calls-port is not valid');
  if (!Number.isInteger(options.quietZone) || options.quietZone < 0) fail('--quiet-zone is not valid');
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
  if (!id) fail(`GOWA did not create any device: ${JSON.stringify(created.data)}`);
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

function gowaBinary(options) {
  if (options.binary) return options.binary;
  return path.join(GOWA_DIR, downloader.targetNameFor(process.platform));
}

/** Il testo che dice cosa fare quando GOWA non c'e' e non lo si e' chiesto. */
function missingGowaHint(binary) {
  const key = downloader.archiveKeyFor(process.platform, process.arch);
  const available = key
    ? `downloadable with --download for this machine (${key})`
    : `not published for ${process.platform}/${process.arch}: use --gowa <path> or --url <GOWA already running>`;
  return `GOWA not found in ${path.relative(ROOT, binary)} (${available})\n` +
    '     node tools/start-login.js --download      # downloads ' + GOWA_VERSION + ' and verifies the SHA-256';
}

/**
 * Installa GOWA per la piattaforma corrente. Su una coppia senza archivio
 * ufficiale si ferma dicendo quale coppia e' e cosa fare invece.
 */
async function installGowa() {
  const key = downloader.archiveKeyFor(process.platform, process.arch);
  if (!key) {
    fail(`no GOWA ${GOWA_VERSION} binary for ${process.platform}/${process.arch}\n` +
      '     use --gowa <path to the executable> or --url <GOWA already running>');
  }

  try {
    const result = await downloader.ensureArchive({
      key,
      dir: GOWA_DIR,
      targetName: downloader.targetNameFor(process.platform),
      log: (line) => console.log(`  ↓  ${line}`),
    });
    console.log(`  ✔  GOWA ${GOWA_VERSION} installed in ${path.relative(ROOT, result.target)} (SHA-256 verified)`);
  } catch (err) {
    fail(err.message);
  }
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
  child.on('error', (err) => fail(`could not start GOWA: ${err.message}`));
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

// ─── Servizi ─────────────────────────────────────────────────────────────────

/** Avvia un servizio `node` del repo, prefissando ogni sua riga di log. */
function startNodeService(service, env) {
  const child = spawn(process.execPath, [service.script], {
    cwd: service.dir,
    env: Object.assign({}, process.env, env),
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const label = service.name === 'adapter' ? 'adapter' : service.name;
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
  child.on('error', (err) => console.error(`  ✖ ${label}: ${err.message}`));
  return child;
}

// ─── Disegno ─────────────────────────────────────────────────────────────────

/** Apre il PNG nel visualizzatore di sistema: quello che si vede in Anteprima
 *  viene ricaricato dal disco a ogni rotazione del codice. */
function openInViewer(file) {
  if (process.platform !== 'darwin') {
    console.log(`  ·  --open-qr: open it by hand: ${path.relative(ROOT, file)}`);
    return;
  }
  try {
    spawn('open', [file], { stdio: 'ignore', detached: true }).unref();
  } catch (err) {
    console.log(`  ·  --open-qr: could not open the QR code (${err.message})`);
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
    console.log(`     The code needs ${lines.length} rows and ${lines[0].length} columns;`);
    console.log(`     this window has ${rows} x ${columns}.`);
    console.log('     Resize it, or press Cmd - to shrink the text: the next code will be');
    console.log('     drawn here.');
  }
  if (hint) console.log(`     Alternatively: ${hint}`);
  console.log('     Alternatively: --no-qr, and log in from the phone, inside the app.');
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
          console.log(`  · new code at ${stamp()} (not drawn: the window is too small)`);
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
  console.log('  WhatsApp for Windows Phone 8.1 — local login server');
  console.log(line);
  console.log(`  GOWA (WhatsApp)      ${baseUrl}  (${GOWA_VERSION})`);
  for (const line of options.serviceLines || []) console.log(line);
  console.log(`  Sessions              .tools/gowa/storages/whatsapp.db`);
  console.log(`  GOWA log              .tools/gowa/gowa.log`);
  console.log(line);
  if (addresses.length > 1) {
    console.log(`  Addresses of this machine: ${addresses.join(', ')}`);
  }
  if (deviceId) console.log(`  GOWA device: ${deviceId}`);
  console.log(line);
}

// ─── Ciclo di login ──────────────────────────────────────────────────────────

/** Porta il PNG del QR su disco: file locale oppure scaricato dal link di GOWA. */
async function loadQrPng(source) {
  if (source.file) return source.file;
  const target = path.join(os.tmpdir(), 'gowa-qr.png');
  const response = await fetch(source.url, { signal: AbortSignal.timeout(15000) });
  if (!response.ok) throw new Error(`QR could not be downloaded (${response.status})`);
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

  printer.render(qr.lines, `QR updated at ${stamp()} — ${qr.count} modules, ` +
    `reconstruction ${(qr.error * 100).toFixed(2)}%`,
    `open ${path.relative(ROOT, stable)} in Preview (it refreshes by itself), or ${imageUrl}`);
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
    fail(`pair code unavailable: ${message}`);
  }
  const code = response.data.results.pair_code;
  const lines = [
    '',
    `        Pair code:  ${code}`,
    '',
    '      On the phone: WhatsApp → Settings → Linked devices →',
    "      Link a device → Link with phone number.",
    '',
  ];
  printer.render(lines, `pair code generated at ${stamp()} (valid ~2 minutes)`);
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
        console.log(`\n  Log in from the phone: open the app and scan the code it shows.` +
          `\n  Waiting for the link...`);
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
    console.log('No stack to stop (no PID file).');
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
      console.log(`  ✔ stopped process ${pid}`);
    } catch (err) {
      console.log(`  · process ${pid} is not running`);
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
  if (options.listServices) {
    const list = services.buildServiceList({
      root: ROOT,
      exists: (target) => fs.existsSync(target),
      options,
    });
    for (const service of list.services) console.log(`  startable: ${service.name} (${service.dir})`);
    for (const entry of list.skipped) console.log(`  skipped:   ${entry.name} (${entry.reason})`);
    return;
  }

  const children = [];
  let stopping = false;
  const shutdown = () => {
    if (stopping) return;
    stopping = true;
    console.log('\n  … shutting down');
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
      if (!options.download) {
        fail(missingGowaHint(binary));
      }
      // Il binario si scarica per QUESTA macchina: sistema e CPU si leggono
      // qui, non in una tabella scritta a mano.
      await installGowa();
    }
    console.log(`  →  starting GOWA ${GOWA_VERSION} on port ${options.port} (log: .tools/gowa/gowa.log)`);
    const stale = clearQrFiles();
    if (stale > 0) console.log(`  ·  removed ${stale} expired QR codes from the previous session`);
    children.push(startGowa(binary, options));
    if (!(await waitForHealth(`http://127.0.0.1:${options.port}`, 20000))) {
      console.error(`\n  ✖ GOWA is not responding. Last lines of ${path.relative(ROOT, GOWA_LOG)}:`);
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

  // Cosa avviare lo decide tools/services.js: qui si esegue e si descrive.
  const resolved = services.buildServiceList({
    root: ROOT,
    exists: (target) => fs.existsSync(target),
    options,
  });
  const host = addresses.length > 0 ? addresses[0] : '127.0.0.1';
  options.serviceLines = [];
  if (resolved.services.some((s) => s.name === 'adapter')) {
    options.serviceLines.push(`  Adapter for the app   ${host}:${options.bridgePort}  (TCP, AES-256-CBC+HMAC)`);
    options.serviceLines.push(`  GOWA→app webhook      http://${host}:${options.webhookPort}/webhook`);
    options.serviceLines.push(`  Automatic discovery   UDP 8587  (the app finds this computer by itself)`);
  }
  if (resolved.services.some((s) => s.name === 'calls')) {
    options.serviceLines.push(`  Calls service         ${host}:${options.callsPort}`);
  }

  banner(options, addresses, baseUrl, deviceId);

  if (resolved.services.length > 0) console.log('  →  starting the services\n');
  for (const service of resolved.services) {
    const env = services.serviceEnv(service, { options, gowaUrl: baseUrl, deviceId });
    children.push(startNodeService(service, env));
  }
  for (const entry of resolved.skipped) {
    console.log(`  ·  ${entry.name} not started: ${entry.reason}`);
  }
  writePidFile(children);
  console.log(`  To stop everything: Ctrl-C (or node tools/start-login.js --stop)`);

  const status = await statusOf(baseUrl, deviceId, options).catch(() => null);
  if (status && status.isLoggedIn) {
    console.log(`\n  ✔ WhatsApp is already linked as ${status.jid || 'unknown'}: no QR code to scan.`);
  } else {
    console.log('\n  On the phone: WhatsApp → Settings → Linked devices → Link a device');
    console.log('  and scan the code below. The first QR code lasts about 60 s, then a new one');
    console.log('  arrives every ~20 s and the drawing refreshes by itself.');
    if (options.noQr) {
      console.log('\n  No QR code here: log in from the phone, inside the app (Ctrl-C to stop).');
    }
  }

  if (options.once) {
    const printer = makePrinter(options);
    if (options.phone) await showPairCode(baseUrl, deviceId, options, printer);
    else {
      const login = await requestLogin(baseUrl, deviceId, options);
      if (login.alreadyLoggedIn) {
        console.log('  ✔ already linked');
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
  console.log(`\n  ✔ WhatsApp linked as ${result.jid || 'unknown'}`);
  if (!options.noBridge) {
    const host = addresses.length > 0 ? addresses[0] : '127.0.0.1';
    console.log(`     The app can now connect to ${host}:${options.bridgePort} and use this session.`);
  }
  console.log('     The stack stays up: Ctrl-C to stop it.');
}

if (require.main === module) {
  main().catch((err) => {
    console.error(`\n  ✖ ${err && err.message ? err.message : err}\n`);
    process.exit(1);
  });
} else {
  // Importabile dai test senza avviare niente.
  module.exports = { parseArgs, missingGowaHint, installGowa, GOWA_VERSION };
}
