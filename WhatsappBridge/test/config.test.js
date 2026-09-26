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

test('loadConfig fornisce i valori di default', () => {
  const c = loadConfig({});
  assert.strictEqual(c.gowa.url, 'http://127.0.0.1:3000');
  assert.strictEqual(c.gowa.deviceId, '');
  assert.strictEqual(c.bridge.port, 8585);
  assert.strictEqual(c.webhook.port, 8586);
  assert.strictEqual(c.webhook.path, '/webhook');
  assert.strictEqual(c.webhook.publicUrl, 'http://127.0.0.1:8586/webhook');
  assert.strictEqual(c.webhook.secret, '');
  assert.strictEqual(c.pollIntervalMs, 5000);
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

test('applyDotEnv non fallisce se .env non esiste', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'bridge-env-'));
  const env = {};
  assert.strictEqual(applyDotEnv(env, dir), env);
  assert.deepStrictEqual(env, {});
  fs.rmSync(dir, { recursive: true, force: true });
});
