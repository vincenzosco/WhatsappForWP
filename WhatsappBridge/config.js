'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');

// Reads the configuration from the environment with sensible defaults.
// It holds no secrets: those stay in .env / environment variables.

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
  CHATS_AVATARS: 'on',
  MESSAGES_LIMIT: '50',
  FFMPEG_ENABLED: 'on',
  FFMPEG_PATH: '',
  AUTH_REQUIRED: 'off',
  USERS_FILE: ''
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
    auth: {
      // On a private instance it is not needed, and stays off: turning it on
      // without handing a token to the phone would shut out the only user. It is
      // turned on for the shared service, after creating the user.
      required: pick(env, 'AUTH_REQUIRED').toLowerCase() === 'on',
      usersFile: pick(env, 'USERS_FILE')
    },
    pollIntervalMs: parseInt(pick(env, 'POLL_INTERVAL_MS'), 10),
    discovery: {
      enabled: pick(env, 'DISCOVERY_ENABLED').toLowerCase() !== 'off',
      port: parseInt(pick(env, 'DISCOVERY_PORT'), 10),
      // Name the app shows in the list of found servers.
      name: pick(env, 'DISCOVERY_NAME') || os.hostname()
    },
    calls: {
      // How many chats to scan and how many messages per chat: the scan makes
      // one HTTP request per chat, so the limit is the duration.
      chatLimit: parseInt(pick(env, 'CALLS_CHAT_LIMIT'), 10),
      messagesPerChat: parseInt(pick(env, 'CALLS_MESSAGES_PER_CHAT'), 10),
      limit: parseInt(pick(env, 'CALLS_LIMIT'), 10)
    },
    chats: {
      // How many conversations to list. The avatars cost one HTTP request per
      // chat (groups included), and can be turned off.
      limit: parseInt(pick(env, 'CHATS_LIMIT'), 10),
      avatars: pick(env, 'CHATS_AVATARS').toLowerCase() !== 'off'
    },
    messages: {
      // How many messages to load when opening a chat. A single read, but the
      // answer is one frame per message: the limit is how many frames pass, not
      // how long the read takes.
      limit: parseInt(pick(env, 'MESSAGES_LIMIT'), 10)
    },
    ffmpeg: {
      // The conversion is optional: without ffmpeg the app receives the
      // original audio and cannot read it, so the voice note stays silent.
      enabled: pick(env, 'FFMPEG_ENABLED').toLowerCase() !== 'off',
      path: pick(env, 'FFMPEG_PATH') || 'ffmpeg'
    }
  };
}

/**
 * Loads a .env file (KEY=value format, # for comments) into `env`.
 * Variables already present in the environment win, so the ones passed by hand
 * or by the start script are not overridden. Without this, the
 * `cp .env.example .env` documented in the README would have no effect at all:
 * the process read only the environment.
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
