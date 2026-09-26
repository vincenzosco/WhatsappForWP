'use strict';
const test = require('node:test');
const assert = require('node:assert');
const path = require('path');

const services = require('../services');

const ROOT = '/tmp/finto-repo';
const baseOptions = {
  bridgePort: 8585,
  webhookPort: 8586,
  callsPort: 8588,
  noBridge: false,
  noCalls: false
};

function existsFrom(files) {
  const set = new Set(files);
  return (target) => set.has(target);
}

test('con l adapter presente si avviano adapter e calls', () => {
  const files = [
    path.join(ROOT, 'WhatsappBridge', 'server.js'),
    path.join(ROOT, 'WhatsappCallServer', 'server.js')
  ];
  const list = services.buildServiceList({
    root: ROOT, exists: existsFrom(files), options: baseOptions
  });
  assert.deepStrictEqual(list.services.map((s) => s.name), ['adapter', 'calls']);
  assert.deepStrictEqual(list.skipped, []);
  assert.ok(list.services.every((s) => s.kind === 'node'));
});

test('senza il secondo server lo si salta dicendo perche', () => {
  const files = [path.join(ROOT, 'WhatsappBridge', 'server.js')];
  const list = services.buildServiceList({
    root: ROOT, exists: existsFrom(files), options: baseOptions
  });
  assert.deepStrictEqual(list.services.map((s) => s.name), ['adapter']);
  assert.deepStrictEqual(list.skipped, [{
    name: 'calls', reason: 'WhatsappCallServer/server.js non esiste'
  }]);
});

test('--no-bridge e --no-calls tolgono il servizio e non lo segnalano', () => {
  const files = [
    path.join(ROOT, 'WhatsappBridge', 'server.js'),
    path.join(ROOT, 'WhatsappCallServer', 'server.js')
  ];
  const options = Object.assign({}, baseOptions, { noBridge: true, noCalls: true });
  const list = services.buildServiceList({
    root: ROOT, exists: existsFrom(files), options
  });
  assert.deepStrictEqual(list.services, []);
  assert.deepStrictEqual(list.skipped, []);
});

test('serviceEnv mappa le variabili dell adapter', () => {
  const env = services.serviceEnv(
    { name: 'adapter', kind: 'node', dir: path.join(ROOT, 'WhatsappBridge') },
    {
      options: Object.assign({}, baseOptions, { user: 'u', pass: 'p' }),
      gowaUrl: 'http://127.0.0.1:3000',
      deviceId: 'dev-1'
    }
  );
  assert.strictEqual(env.GOWA_URL, 'http://127.0.0.1:3000');
  assert.strictEqual(env.GOWA_DEVICE_ID, 'dev-1');
  assert.strictEqual(env.GOWA_USER, 'u');
  assert.strictEqual(env.GOWA_PASS, 'p');
  assert.strictEqual(env.BRIDGE_PORT, '8585');
  assert.strictEqual(env.WEBHOOK_PORT, '8586');
  assert.strictEqual(env.WEBHOOK_PUBLIC_URL, 'http://127.0.0.1:8586/webhook');
});

test('serviceEnv mappa le variabili del servizio chiamate', () => {
  const env = services.serviceEnv(
    { name: 'calls', kind: 'node', dir: path.join(ROOT, 'WhatsappCallServer') },
    { options: baseOptions, gowaUrl: 'http://127.0.0.1:3000', deviceId: '' }
  );
  assert.strictEqual(env.CALLS_PORT, '8588');
  assert.strictEqual(env.BRIDGE_PORT, undefined);
});
