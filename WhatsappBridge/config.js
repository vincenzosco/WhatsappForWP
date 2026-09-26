'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');

// Legge la configurazione dall'ambiente con valori di default sensati.
// Non contiene segreti: quelli restano in .env / variabili d'ambiente.

const DEFAULTS = {
  GOWA_URL: 'http://127.0.0.1:3000',
  GOWA_DEVICE_ID: '',
  GOWA_USER: '',
  GOWA_PASS: '',
  BRIDGE_PORT: '8585',
  WEBHOOK_PORT: '8586',
  WEBHOOK_PATH: '/webhook',
  WEBHOOK_PUBLIC_URL: '',
  WEBHOOK_SECRET: '',
  POLL_INTERVAL_MS: '5000',
  DISCOVERY_PORT: '8587',
  DISCOVERY_ENABLED: 'on',
  DISCOVERY_NAME: '',
  CALLS_CHAT_LIMIT: '25',
  CALLS_MESSAGES_PER_CHAT: '100',
  CALLS_LIMIT: '50',
  CHATS_LIMIT: '25',
  CHATS_AVATARS: 'on'
};

function pick(env, key) {
  const value = env[key];
  return (value === undefined || value === null || value === '') ? DEFAULTS[key] : String(value);
}

function loadConfig(env = process.env) {
  const gowaUrl = pick(env, 'GOWA_URL').replace(/\/+$/, '');
  const webhookPort = parseInt(pick(env, 'WEBHOOK_PORT'), 10);
  const webhookPath = pick(env, 'WEBHOOK_PATH');
  const publicUrl = pick(env, 'WEBHOOK_PUBLIC_URL')
    || `http://127.0.0.1:${webhookPort}${webhookPath}`;

  return {
    gowa: {
      url: gowaUrl,
      deviceId: pick(env, 'GOWA_DEVICE_ID'),
      user: pick(env, 'GOWA_USER'),
      pass: pick(env, 'GOWA_PASS')
    },
    bridge: {
      port: parseInt(pick(env, 'BRIDGE_PORT'), 10)
    },
    webhook: {
      port: webhookPort,
      path: webhookPath,
      publicUrl,
      secret: pick(env, 'WEBHOOK_SECRET')
    },
    pollIntervalMs: parseInt(pick(env, 'POLL_INTERVAL_MS'), 10),
    discovery: {
      enabled: pick(env, 'DISCOVERY_ENABLED').toLowerCase() !== 'off',
      port: parseInt(pick(env, 'DISCOVERY_PORT'), 10),
      // Nome che l'app mostra nella lista dei server trovati.
      name: pick(env, 'DISCOVERY_NAME') || os.hostname()
    },
    calls: {
      // Quante chat scansionare e quanti messaggi per chat: la scansione fa
      // una richiesta HTTP per chat, quindi il limite e' la durata.
      chatLimit: parseInt(pick(env, 'CALLS_CHAT_LIMIT'), 10),
      messagesPerChat: parseInt(pick(env, 'CALLS_MESSAGES_PER_CHAT'), 10),
      limit: parseInt(pick(env, 'CALLS_LIMIT'), 10)
    },
    chats: {
      // Quante conversazioni elencare. Gli avatar costano una richiesta HTTP
      // per persona, e si possono spegnere.
      limit: parseInt(pick(env, 'CHATS_LIMIT'), 10),
      avatars: pick(env, 'CHATS_AVATARS').toLowerCase() !== 'off'
    }
  };
}

/**
 * Carica un file .env (formato CHIAVE=valore, # per i commenti) dentro `env`.
 * Le variabili gia' presenti nell'ambiente hanno la precedenza, cosi' che le
 * variabili passate a mano o dallo script di avvio non vengano scavalcate.
 * Senza questo, il `cp .env.example .env` documentato nel README non avrebbe
 * alcun effetto: il processo leggeva solo l'ambiente.
 */
function applyDotEnv(env = process.env, dir = __dirname) {
  let content;
  try {
    content = fs.readFileSync(path.join(dir, '.env'), 'utf8');
  } catch (err) {
    return env;
  }

  for (const rawLine of content.split('\n')) {
    const line = rawLine.trim();
    if (!line || line.startsWith('#')) continue;
    const separator = line.indexOf('=');
    if (separator < 1) continue;
    const key = line.slice(0, separator).trim();
    let value = line.slice(separator + 1).trim();
    const quoted = (value.startsWith('"') && value.endsWith('"'))
      || (value.startsWith("'") && value.endsWith("'"));
    if (quoted && value.length >= 2) value = value.slice(1, -1);
    if (env[key] === undefined) env[key] = value;
  }
  return env;
}

module.exports = { loadConfig, applyDotEnv, DEFAULTS };
