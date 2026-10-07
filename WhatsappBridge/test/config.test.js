'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { loadConfig, applyDotEnv } = require('../config');

test('call scan limits have defaults and can be overridden', () => {
  const defaults = loadConfig({});
  assert.strictEqual(defaults.calls.chatLimit, 25);
  assert.strictEqual(defaults.calls.messagesPerChat, 100);
  assert.strictEqual(defaults.calls.limit, 50);

  const custom = loadConfig({ CALLS_CHAT_LIMIT: '5', CALLS_MESSAGES_PER_CHAT: '20', CALLS_LIMIT: '10' });
  assert.strictEqual(custom.calls.chatLimit, 5);
  assert.strictEqual(custom.calls.messagesPerChat, 20);
  assert.strictEqual(custom.calls.limit, 10);
});

test('il recupero del pairing e acceso se non lo si spegne', () => {
  assert.strictEqual(loadConfig({}).pairing.recover, true,
    'un telefono che ha perso la chiave non ha nessun altro a cui chiedere');
  assert.strictEqual(loadConfig({ PAIRING_RECOVER: 'on' }).pairing.recover, true);
  assert.strictEqual(loadConfig({ PAIRING_RECOVER: 'off' }).pairing.recover, false);
});

test('loadConfig fornisce i valori di default', () => {
  const c = loadConfig({});
  assert.strictEqual(c.gowa.url, 'http://127.0.0.1:3000');
  assert.strictEqual(c.gowa.deviceId, '');
  assert.strictEqual(c.bridge.port, 8585);
  assert.strictEqual(c.webhook.port, 8586);
  assert.strictEqual(c.webhook.path, '/webhook');
  assert.strictEqual(c.webhook.publicUrl, 'http://127.0.0.1:8586/webhook');
  // GOWA firma con "secret" di default: l'adapter lo eredita cosi' la verifica
  // della firma resta accesa invece di essere saltata.
  assert.strictEqual(c.webhook.secret, 'secret');
  assert.strictEqual(c.pollIntervalMs, 5000);
});

test('loadConfig espone la configurazione di ffmpeg', () => {
  const c = loadConfig({});
  assert.strictEqual(c.ffmpeg.enabled, true);
  assert.strictEqual(c.ffmpeg.path, 'ffmpeg');

  const off = loadConfig({ FFMPEG_ENABLED: 'off', FFMPEG_PATH: '/usr/local/bin/ffmpeg' });
  assert.strictEqual(off.ffmpeg.enabled, false);
  assert.strictEqual(off.ffmpeg.path, '/usr/local/bin/ffmpeg');
});

test('loadConfig legge e normalizza le variabili d\'ambiente', () => {
  const c = loadConfig({
    GOWA_URL: 'http://192.168.1.50:3000/',
    GOWA_DEVICE_ID: 'org_1',
    GOWA_USER: 'admin',
    GOWA_PASS: 'secret',
    BRIDGE_PORT: '9000',
    WEBHOOK_PORT: '9001',
    WEBHOOK_SECRET: 's3cr3t',
    POLL_INTERVAL_MS: '2500'
  });
  assert.strictEqual(c.gowa.url, 'http://192.168.1.50:3000');
  assert.strictEqual(c.gowa.deviceId, 'org_1');
  assert.strictEqual(c.gowa.user, 'admin');
  assert.strictEqual(c.gowa.pass, 'secret');
  assert.strictEqual(c.bridge.port, 9000);
  assert.strictEqual(c.webhook.port, 9001);
  assert.strictEqual(c.webhook.publicUrl, 'http://127.0.0.1:9001/webhook');
  assert.strictEqual(c.webhook.secret, 's3cr3t');
  assert.strictEqual(c.pollIntervalMs, 2500);
});

test('loadConfig accetta WEBHOOK_PUBLIC_URL esplicita', () => {
  const c = loadConfig({ WEBHOOK_PORT: '9001', WEBHOOK_PUBLIC_URL: 'http://10.0.0.5:9001/hook' });
  assert.strictEqual(c.webhook.publicUrl, 'http://10.0.0.5:9001/hook');
});

test('applyDotEnv legge il file .env e non scavalca l\'ambiente', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'bridge-env-'));
  fs.writeFileSync(path.join(dir, '.env'), [
    '# commento',
    '',
    'GOWA_URL=http://10.0.0.9:3000',
    'BRIDGE_PORT = "9001"',
    'GOWA_PASS=\'segreto\'',
    'SENZA_VALORE=',
  ].join('\n'));

  const env = { BRIDGE_PORT: '8585' };
  applyDotEnv(env, dir);

  assert.strictEqual(env.GOWA_URL, 'http://10.0.0.9:3000');
  assert.strictEqual(env.BRIDGE_PORT, '8585', 'le variabili gia\' presenti vincono');
  assert.strictEqual(env.GOWA_PASS, 'segreto');
  assert.strictEqual(env.SENZA_VALORE, '');
  assert.strictEqual(env['# commento'], undefined);

  const config = loadConfig(env);
  assert.strictEqual(config.gowa.url, 'http://10.0.0.9:3000');
  assert.strictEqual(config.bridge.port, 8585);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('loadConfig espone gli interruttori di sicurezza del bridge', () => {
  const defaults = loadConfig({});
  assert.strictEqual(defaults.bridge.requireKey, false);
  assert.strictEqual(defaults.auth.strictDevice, false);

  const on = loadConfig({ BRIDGE_REQUIRE_KEY: 'on', AUTH_STRICT_DEVICE: 'on' });
  assert.strictEqual(on.bridge.requireKey, true);
  assert.strictEqual(on.auth.strictDevice, true);
});

test('loadConfig espone la pubblicazione del registro dei server', () => {
  const defaults = loadConfig({});
  assert.strictEqual(defaults.endpoint.publish, false);
  assert.strictEqual(defaults.endpoint.repo, '');
  assert.strictEqual(defaults.endpoint.intervalMinutes, 30);

  const on = loadConfig({
    ENDPOINT_PUBLISH: 'on',
    ENDPOINT_REPO: 'me/repo',
    ENDPOINT_TOKEN: 'tok',
    ENDPOINT_SERVER_ID: 'nas',
    ENDPOINT_SERVER_NAME: 'NAS',
    ENDPOINT_HOST: '192.168.0.108',
    ENDPOINT_PORT: '8585',
    ENDPOINT_PUBLISH_MINUTES: '5',
  });
  assert.strictEqual(on.endpoint.publish, true);
  assert.strictEqual(on.endpoint.repo, 'me/repo');
  assert.strictEqual(on.endpoint.token, 'tok');
  assert.strictEqual(on.endpoint.serverId, 'nas');
  assert.strictEqual(on.endpoint.serverName, 'NAS');
  assert.strictEqual(on.endpoint.host, '192.168.0.108');
  assert.strictEqual(on.endpoint.port, 8585);
  assert.strictEqual(on.endpoint.intervalMinutes, 5);
});

test('ENDPOINT_TOKEN vuoto eredita GH_TOKEN', () => {
  const c = loadConfig({ GH_TOKEN: 'gh-token' });
  assert.strictEqual(c.endpoint.token, 'gh-token');
});

test('loadConfig espone la configurazione di discovery', () => {
  const defaults = loadConfig({});
  assert.strictEqual(defaults.discovery.enabled, true);
  assert.strictEqual(defaults.discovery.port, 8587);
  assert.ok(defaults.discovery.name.length > 0, 'nome di default = hostname');

  const custom = loadConfig({
    DISCOVERY_ENABLED: 'off',
    DISCOVERY_PORT: '9000',
    DISCOVERY_NAME: 'studio',
  });
  assert.strictEqual(custom.discovery.enabled, false);
  assert.strictEqual(custom.discovery.port, 9000);
  assert.strictEqual(custom.discovery.name, 'studio');
});

test('the chat list limits have defaults and can be overridden', () => {
  const defaults = loadConfig({});
  assert.strictEqual(defaults.chats.limit, 25);
  assert.strictEqual(defaults.chats.avatars, true);

  const custom = loadConfig({ CHATS_LIMIT: '5', CHATS_AVATARS: 'off' });
  assert.strictEqual(custom.chats.limit, 5);
  assert.strictEqual(custom.chats.avatars, false);
});

test('the history limit has a default and can be overridden', () => {
  const defaults = loadConfig({});
  assert.strictEqual(defaults.messages.limit, 50);

  const custom = loadConfig({ MESSAGES_LIMIT: '200' });
  assert.strictEqual(custom.messages.limit, 200);
});

test('i default dell adapter stanno nel budget di un telefono da 512 MB', () => {
  // Su un telefono da 512 MB ogni conversazione e' un frame che arriva e
  // un'immagine che viene decodificata: il tetto e' quante righe l'elenco puo'
  // mostrare, non quante il server saprebbe mandarne. Se qualcuno alza un
  // default, questo test lo ferma prima del telefono.
  const defaults = loadConfig({});
  assert.ok(defaults.chats.limit <= 30, `CHATS_LIMIT=${defaults.chats.limit}`);
  assert.ok(defaults.messages.limit <= 60, `MESSAGES_LIMIT=${defaults.messages.limit}`);
});

test('applyDotEnv non fallisce se .env non esiste', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'bridge-env-'));
  const env = {};
  assert.strictEqual(applyDotEnv(env, dir), env);
  assert.deepStrictEqual(env, {});
  fs.rmSync(dir, { recursive: true, force: true });
});
