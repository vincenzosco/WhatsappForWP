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
  // GOWA signs every webhook with this and its own default is "secret"; matching
  // it here keeps the signature check on instead of leaving it open.
  WEBHOOK_SECRET: 'secret',
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
  AUTH_REGISTER: 'on',
  AUTH_STRICT_DEVICE: 'off',
  AUTH_MAX_USERS: '50',
  USERS_FILE: '',
  // The passphrase compiled into the public app. It is the prefix every device's
  // frame key is derived from (crypto-helper.framePassphraseFor): there is no
  // global BRIDGE_KEY to set and no pairing window. Kept as a name because it is
  // the shared half of the derivation, and it must equal the app's constant.
  BRIDGE_KEY: 'WhatsAppCommunityWP8-2026',
  // The server registry: this server publishes its own address into
  // whatsappforwp-endpoint/endpoint.json so the app can find it, and so the app
  // has more than one server to try when one goes down. Off by default: a
  // private instance needs no registry, and publishing needs a token.
  ENDPOINT_PUBLISH: 'off',
  ENDPOINT_REPO: '',
  ENDPOINT_TOKEN: '',
  GH_TOKEN: '',
  ENDPOINT_SERVER_ID: '',
  ENDPOINT_SERVER_NAME: '',
  ENDPOINT_HOST: '',
  ENDPOINT_PORT: '',
  ENDPOINT_PUBLISH_MINUTES: '30',
  // The shared registry on the VM, for a deployment that uses one: with a URL
  // set the adapter reports its address there and writes nothing to GitHub, so
  // the container needs no token of its own. Empty keeps the old behaviour.
  ENDPOINT_REGISTRY_URL: '',
  ENDPOINT_REGISTRY_SECRET: ''
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
    endpoint: {
      // On when this server should advertise itself in the shared registry. The
      // host is the one the phone has to dial: left empty, the first local IPv4
      // is used (a machine running the adapter directly, not a bridged
      // container, whose IP is the host's). The id is what makes the server
      // replace its own row instead of adding another one.
      publish: pick(env, 'ENDPOINT_PUBLISH').toLowerCase() === 'on',
      repo: pick(env, 'ENDPOINT_REPO'),
      // ENDPOINT_TOKEN wins; GH_TOKEN is the one the tunnel container already
      // uses, so a deployment sets it once.
      token: pick(env, 'ENDPOINT_TOKEN') || pick(env, 'GH_TOKEN'),
      serverId: pick(env, 'ENDPOINT_SERVER_ID'),
      serverName: pick(env, 'ENDPOINT_SERVER_NAME'),
      host: pick(env, 'ENDPOINT_HOST'),
      port: parseInt(pick(env, 'ENDPOINT_PORT'), 10),
      intervalMinutes: Math.max(1, parseInt(pick(env, 'ENDPOINT_PUBLISH_MINUTES'), 10) || 30),
      // The registry on the VM. With a URL set this server reports its address
      // there and the credential that writes `endpoint.json` stays on the VM:
      // that is what lets somebody else's container announce itself without
      // being handed write access to the repository.
      registryUrl: pick(env, 'ENDPOINT_REGISTRY_URL'),
      registrySecret: pick(env, 'ENDPOINT_REGISTRY_SECRET')
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
      usersFile: pick(env, 'USERS_FILE'),
      // On the shared service a phone that arrives without a token is given one
      // on its first connection (see the `hello` case in server.js): nobody who
      // cannot read the server console can be asked to type a token by hand. A
      // service that must stay closed turns this off and hands the tokens out
      // itself.
      register: pick(env, 'AUTH_REGISTER').toLowerCase() !== 'off',
      // When on, only a device the store already knows may register itself: a
      // stranger cannot join. A known device is always let back in and handed
      // its derived token again, which is what a reinstall needs, so this flag
      // no longer closes the door on the phone that lost its storage - only on
      // devices the store has never seen.
      strictDevice: pick(env, 'AUTH_STRICT_DEVICE').toLowerCase() === 'on',
      // Ceiling on the devices that can register themselves, so an open service
      // cannot fill its disk one handshake at a time.
      maxUsers: parseInt(pick(env, 'AUTH_MAX_USERS'), 10)
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
