'use strict';
const test = require('node:test');
const assert = require('node:assert');
const dgram = require('node:dgram');
const { ipv4Broadcast, broadcastTargets, createDiscoveryBeacon } = require('../discovery');

test('ipv4Broadcast calcola l indirizzo di broadcast dalla netmask', () => {
  assert.strictEqual(ipv4Broadcast('192.168.0.86', '255.255.255.0'), '192.168.0.255');
  assert.strictEqual(ipv4Broadcast('10.211.55.2', '255.255.0.0'), '10.211.255.255');
  assert.strictEqual(ipv4Broadcast('127.0.0.1', '255.255.255.255'), '127.0.0.1');
  assert.strictEqual(ipv4Broadcast('192.168.1.5', undefined), '192.168.1.255');
});

test('broadcastTargets salta le interfacce virtuali e aggiunge il broadcast globale', () => {
  const targets = broadcastTargets({
    en0: [{ family: 'IPv4', internal: false, address: '192.168.0.86', netmask: '255.255.255.0' }],
    utun3: [{ family: 'IPv4', internal: false, address: '10.8.0.2', netmask: '255.255.255.255' }],
    lo0: [{ family: 'IPv4', internal: true, address: '127.0.0.1', netmask: '255.0.0.0' }],
  });
  assert.deepStrictEqual(targets, ['192.168.0.255', '255.255.255.255']);
});

test('il beacon invia il payload su ogni interfaccia e si chiude', () => {
  const sent = [];
  const fakeSocket = {
    on() {},
    bind(callback) { callback(); },
    setBroadcast() {},
    send(payload, offset, length, port, target, callback) {
      sent.push({ json: JSON.parse(payload.toString('utf8')), port, target });
      callback(null);
    },
    close() { sent.push({ closed: true }); },
  };

  const beacon = createDiscoveryBeacon({
    port: 8587,
    intervalMs: 1000000,
    getPayload: () => ({
      service: 'whatsapp-wp8-adapter', version: 1, name: 'mac-di-vincenzo',
      port: 8585, state: 'disconnected', account: '',
    }),
    interfaces: { en0: [{ family: 'IPv4', internal: false, address: '192.168.0.86', netmask: '255.255.255.0' }] },
    socketFactory: () => fakeSocket,
    log: () => {},
  });

  assert.strictEqual(sent.length, 2, 'un invio per target');
  assert.strictEqual(sent[0].target, '192.168.0.255');
  assert.strictEqual(sent[0].port, 8587);
  assert.strictEqual(sent[0].json.service, 'whatsapp-wp8-adapter');
  assert.strictEqual(sent[0].json.port, 8585);
  assert.strictEqual(sent[0].json.version, 1);

  beacon.stop();
  assert.strictEqual(sent[sent.length - 1].closed, true);
});

test('il beacon arriva davvero su un socket UDP in ascolto', async () => {
  const received = [];
  const listener = dgram.createSocket('udp4');
  await new Promise((resolve) => listener.bind(0, '127.0.0.1', resolve));
  listener.on('message', (buffer) => received.push(JSON.parse(buffer.toString('utf8'))));

  const beacon = createDiscoveryBeacon({
    port: listener.address().port,
    intervalMs: 50,
    getPayload: () => ({
      service: 'whatsapp-wp8-adapter', version: 1, name: 'test-mac',
      port: 8585, state: 'connected', account: '39@s.whatsapp.net',
    }),
    // una /32 su 127.0.0.1: il broadcast di quella rete e' l'indirizzo stesso,
    // quindi il test e' un giro UDP vero senza uscire dalla loopback
    interfaces: { lo0: [{ family: 'IPv4', internal: false, address: '127.0.0.1', netmask: '255.255.255.255' }] },
    log: () => {},
  });

  await new Promise((resolve) => setTimeout(resolve, 300));
  beacon.stop();
  listener.close();

  assert.ok(received.length >= 1, 'nessun beacon ricevuto');
  assert.strictEqual(received[0].service, 'whatsapp-wp8-adapter');
  assert.strictEqual(received[0].name, 'test-mac');
  assert.strictEqual(received[0].account, '39@s.whatsapp.net');
});
