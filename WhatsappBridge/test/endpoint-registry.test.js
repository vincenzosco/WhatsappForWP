'use strict';
const test = require('node:test');
const assert = require('node:assert');
const {
  parseRegistry,
  upsertServer,
  serializeRegistry,
} = require('../endpoint-registry');

// The file the old deployment wrote: one address and nothing else. An app
// built before the list existed must keep reading it.
const LEGACY = '{\n  "updatedAt": "2026-09-30T12:00:00.000Z",\n'
  + '  "host": "bore.pub",\n  "port": 41234,\n  "tls": false,\n  "fingerprint": ""\n}\n';

const LIST = JSON.stringify({
  updatedAt: '2026-10-06T10:00:00.000Z',
  host: 'bore.pub',
  port: 41417,
  tls: false,
  fingerprint: '',
  servers: [
    { id: 'public', name: 'Public server', host: 'bore.pub', port: 41417, updatedAt: '2026-10-06T10:00:00.000Z' },
    { id: 'nas-lan', name: 'NAS', host: '192.168.0.108', port: 8585, updatedAt: '2026-10-05T08:00:00.000Z' },
  ],
});

test('parseRegistry legge il file a indirizzo singolo come un server solo', () => {
  const registry = parseRegistry(LEGACY);
  assert.strictEqual(registry.servers.length, 1);
  assert.strictEqual(registry.servers[0].host, 'bore.pub');
  assert.strictEqual(registry.servers[0].port, 41234);
  assert.strictEqual(registry.host, 'bore.pub');
  assert.strictEqual(registry.port, 41234);
});

test('parseRegistry legge la lista e tiene il primo in testa', () => {
  const registry = parseRegistry(LIST);
  assert.strictEqual(registry.servers.length, 2);
  assert.strictEqual(registry.servers[0].id, 'public');
  assert.strictEqual(registry.servers[1].id, 'nas-lan');
  assert.strictEqual(registry.host, 'bore.pub');
  assert.strictEqual(registry.port, 41417);
  assert.strictEqual(registry.servers[1].host, '192.168.0.108');
});

test('parseRegistry non lancia su testo non JSON e torna vuoto', () => {
  const registry = parseRegistry('not json at all');
  assert.deepStrictEqual(registry.servers, []);
  assert.strictEqual(registry.host, '');
});

test('un valore servers che non e\' una lista viene ignorato, resta il top-level', () => {
  const registry = parseRegistry({ host: 'bore.pub', port: 41234, servers: 'oops' });
  assert.strictEqual(registry.servers.length, 1);
  assert.strictEqual(registry.servers[0].host, 'bore.pub');
});

test('una voce senza porta o con host vuoto viene scartata, le altre restano', () => {
  const registry = parseRegistry({
    host: 'bore.pub',
    port: 41234,
    servers: [
      { id: 'broken', host: '10.0.0.1' },
      { id: 'empty', host: '', port: 8585 },
      { id: 'good', host: '10.0.0.2', port: 8585 },
    ],
  });
  const ids = registry.servers.map((s) => s.id);
  assert.ok(ids.indexOf('broken') === -1, 'senza porta: scartata');
  assert.ok(ids.indexOf('empty') === -1, 'senza host: scartata');
  assert.ok(ids.indexOf('good') !== -1, 'valida: tenuta');
});

test('upsertServer sostituisce la voce con lo stesso id', () => {
  const before = parseRegistry(LIST);
  const after = upsertServer(before, { id: 'nas-lan', name: 'NAS', host: '192.168.0.200', port: 8585 });

  assert.strictEqual(after.servers.length, 2);
  const nas = after.servers.find((s) => s.id === 'nas-lan');
  assert.strictEqual(nas.host, '192.168.0.200');
  // Il top-level resta il preferito: il tunnel pubblico.
  assert.strictEqual(after.host, 'bore.pub');
  assert.strictEqual(after.port, 41417);
});

test('upsertServer aggiunge una voce nuova in coda', () => {
  const after = upsertServer(parseRegistry(LIST), { id: 'pc', host: '10.0.0.9', port: 8585 });
  assert.strictEqual(after.servers.length, 3);
  assert.strictEqual(after.servers[2].id, 'pc');
});

test('upsertServer preferito mette la voce in testa e riscrive il top-level', () => {
  const after = upsertServer(
    parseRegistry(LIST),
    { id: 'new-public', host: 'bore.pub', port: 49999 },
    { preferred: true, now: '2026-10-06T11:00:00.000Z' });

  assert.strictEqual(after.servers[0].id, 'new-public');
  assert.strictEqual(after.host, 'bore.pub');
  assert.strictEqual(after.port, 49999);
  assert.strictEqual(after.updatedAt, '2026-10-06T11:00:00.000Z');
});

test('serializeRegistry produce un file che si rilegge uguale (round-trip)', () => {
  const written = serializeRegistry(parseRegistry(LIST));
  assert.ok(written.endsWith('\n'));
  const reread = parseRegistry(written);
  assert.deepStrictEqual(reread.servers, parseRegistry(LIST).servers);
  assert.strictEqual(reread.host, 'bore.pub');
  assert.strictEqual(reread.port, 41417);
});

test('upsertServer con una voce senza indirizzo non tocca il registro', () => {
  const before = parseRegistry(LIST);
  const after = upsertServer(before, { id: 'nope', host: '', port: 0 });
  assert.deepStrictEqual(after.servers, before.servers);
});
