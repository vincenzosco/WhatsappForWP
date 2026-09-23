'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { loadConfig } = require('../config');

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
