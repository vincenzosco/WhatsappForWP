#!/usr/bin/env node
/**
 * tools/check-no-server.js
 *
 * La guardia che tiene fuori il server.
 *
 * Perche' esiste: l'app parlava con WhatsApp attraverso un server self-hosted
 * (GOWA) e un adattatore Node.js, e la revisione del 2026-10-11 ha spostato
 * tutto quel lavoro dentro il telefono - il client parla il protocollo da solo.
 * Il codice del vecchio percorso e' stato cancellato in un commit solo, e questo
 * e' il motivo per cui poteva tornare: niente lo impediva. E' successo davvero
 * sul Lumia - il telefono connesso al client nativo registrava ancora
 * `UDP discovery beacon listening on port 8587` e `EndpointService.ResolveAllAsync:
 * 404`, cioe' una chiamata a un server che non esiste piu', fatta da codice che
 * nessuno aveva tolto.
 *
 * Regole:
 *  1. nessuno dei dieci file del vecchio percorso esiste;
 *  2. nessuna cartella del server esiste (`WhatsappBridge/`, `WhatsappServer/`,
 *     `registry/`, la skill `run-the-login-server`);
 *  3. nessuna chiave delle impostazioni e' rimasta: il telefono non ha un
 *     indirizzo, una porta, un endpoint o un token di servizio da conservare;
 *  4. i README e le skill non nominano piu' l'adattatore, GOWA o il server.
 *
 * La parola inglese "adapter" da sola non si segnala: la skill
 * `improve-codebase-architecture` la usa nel senso di "ports & adapters", che non
 * ha niente a che vedere con questo progetto. Si segnala il nome vero -
 * `WhatsappBridge`, `WhatsappServer`, `GOWA`, `start-login`,
 * `run-the-login-server` - e, nei due README del progetto, l'adattatore nominato
 * come tale.
 *
 * Usage:
 *   node tools/check-no-server.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');

/** I dieci file del vecchio percorso, tutti cancellati da questo commit. */
const SERVER_FILES = [
  'WhatsappApp/Services/CommunicationService.cs',
  'WhatsappApp/Services/FrameCodec.cs',
  'WhatsappApp/Services/CryptoHelper.cs',
  'WhatsappApp/Services/EndpointService.cs',
  'WhatsappApp/Services/DiscoveryService.cs',
  'WhatsappApp/Services/ServerPinger.cs',
  'WhatsappApp/Services/AutoConnector.cs',
  'WhatsappApp/Services/ConnectionWatchdog.cs',
  'WhatsappApp/Services/SelfCheck.cs',
  'WhatsappApp/Models/DiscoveredServer.cs',
];

/** Le cartelle che appartengono al server e non all'app. */
const SERVER_DIRS = [
  'WhatsappBridge',
  'WhatsappServer',
  'registry',
  '.agents/skills/run-the-login-server',
];

/**
 * Le chiavi che l'app conservava per parlare col server. Ognuna e' un indirizzo,
 * una porta, un endpoint o un token: niente di tutto questo esiste piu', e una
 * chiave rimasta e' un percorso che qualcuno puo' ancora percorrere.
 */
const SERVER_KEYS = [
  'ServerAddress',
  'ServerPort',
  'UsePublicServer',
  'EndpointUrl',
  'ServiceToken',
];

/** I due README del progetto: qui "adapter" e "adattatore" sono l'adattatore. */
const README_FILES = ['README.md', 'README.it.md'];

/** I nomi veri del server, come compaiono nei testi. */
const SERVER_TOKENS = [
  /WhatsappBridge/,
  /WhatsappServer/,
  /\bGOWA\b/,
  /\badattatore\b/i,
  /run-the-login-server/,
  /start-login/,
];

/** "adapter" come sostantivo solo nei README, dove non ha altri significati. */
const README_TOKENS = [/adapter/i];

function exists(root, rel) {
  return fs.existsSync(path.join(root, rel));
}

/** Ogni file sotto dir, con percorso relativo alla radice, senza obj/ e bin/. */
function walk(root, dir, out) {
  const absolute = path.join(root, dir);
  if (!fs.existsSync(absolute)) return out;

  for (const entry of fs.readdirSync(absolute, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'obj' || entry.name === 'bin' || entry.name === '.git') continue;
      walk(root, path.join(dir, entry.name), out);
    } else {
      out.push(path.join(dir, entry.name).replace(/\\/g, '/'));
    }
  }
  return out;
}

/** Le righe di un file, o un elenco vuoto quando il file non c'e'. */
function lines(root, rel) {
  try {
    return fs.readFileSync(path.join(root, rel), 'utf8').split('\n');
  } catch (err) {
    return [];
  }
}

/** Il primo token che una riga nomina, o null. */
function tokenIn(text, tokens) {
  for (const token of tokens) {
    if (token.test(text)) return token.source;
  }
  return null;
}

/**
 * Ogni testo che un lettore legge: i due README e tutte le skill del progetto.
 * E' l'elenco di cio' che deve smettere di raccontare il server.
 */
function documentation(root) {
  const files = README_FILES.filter((rel) => exists(root, rel));
  return files.concat(walk(root, '.agents/skills', []).filter((rel) => rel.endsWith('.md')));
}

/**
 * Tutto quello che il repository ha ancora del server, una riga per problema.
 *
 * Non esce dal processo: chi legge decide. `main` e' il chiamante che traduce
 * l'elenco in un exit code, e il test lo chiama su un albero di prova.
 */
function findProblems(root) {
  const problems = [];

  for (const rel of SERVER_FILES) {
    if (exists(root, rel)) {
      problems.push(`${rel}: one of the ten files of the retired server path is back`);
    }
  }

  for (const rel of SERVER_DIRS) {
    if (exists(root, rel)) {
      problems.push(`${rel}: the server belongs to another repository, not to this one`);
    }
  }

  const sources = walk(root, 'WhatsappApp', []).filter(
    (rel) => rel.endsWith('.cs') || rel.endsWith('.resw')
  );
  for (const rel of sources) {
    const found = lines(root, rel);
    for (let i = 0; i < found.length; i++) {
      const key = tokenIn(found[i], SERVER_KEYS.map((k) => new RegExp(`\\b${k}\\b`)));
      if (key !== null) {
        problems.push(`${rel}:${i + 1}: the settings key ${key} is a server the phone no longer has, so "${found[i].trim()}" goes`);
      }
    }
  }

  for (const rel of documentation(root)) {
    const tokens = rel.indexOf('.agents/skills/') === 0 ? SERVER_TOKENS : SERVER_TOKENS.concat(README_TOKENS);
    const found = lines(root, rel);
    for (let i = 0; i < found.length; i++) {
      const token = tokenIn(found[i], tokens);
      if (token !== null) {
        problems.push(`${rel}:${i + 1}: names the retired server ("${token}"): "${found[i].trim()}"`);
      }
    }
  }

  return problems;
}

function main() {
  const problems = findProblems(ROOT);
  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} trace(s) of the server this app no longer talks to.`);
    process.exit(1);
  }
  console.log('OK: the repository has no server');
}

if (require.main === module) main();

module.exports = { SERVER_FILES, SERVER_DIRS, SERVER_KEYS, walk, documentation, findProblems };
