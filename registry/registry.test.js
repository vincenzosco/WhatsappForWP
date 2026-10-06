'use strict';

/**
 * Tests of the registry logic. Only `node:test`, like the adapter's suite: this
 * project has no test runner as a dependency.
 *
 * The two properties worth more than the rest are here on purpose:
 *  - a row never moves. The order of `servers` is the order the app tries the
 *    addresses, and `servers[0]` is what an app built before the list reads;
 *  - the file does not change when nothing changed. A server that reports the
 *    same address every half hour must not create a commit every half hour.
 */

const test = require('node:test');
const assert = require('node:assert/strict');

const {
  normalizeEntry,
  upsertReported,
  expire,
  mergeRegistry,
  serializeRegistry,
  renderMarkdown,
} = require('./registry');

const T0 = '2026-10-06T10:00:00.000Z';
const T1 = '2026-10-06T11:00:00.000Z';

test('una riga valida viene normalizzata, una rotta no', () => {
  assert.deepEqual(
    normalizeEntry({ id: 'nas', name: 'NAS', host: ' 192.168.0.108 ', port: 8585 }),
    { id: 'nas', name: 'NAS', host: '192.168.0.108', port: 8585, updatedAt: '' },
  );

  const bad = [
    { id: 'nas', host: '192.168.0.108', port: 0 },
    { id: 'nas', host: '192.168.0.108', port: 70000 },
    { id: 'nas', host: '', port: 8585 },
    { id: 'nas due', host: '192.168.0.108', port: 8585 },
    { id: '', host: '192.168.0.108', port: 8585 },
    { id: 'nas', host: 'a b', port: 8585 },
    { id: 'nas', host: 'x'.repeat(254), port: 8585 },
    null,
    [],
    'nas',
  ];
  for (const entry of bad) {
    assert.equal(normalizeEntry(entry), null, JSON.stringify(entry));
  }
});

test('la stessa riga si aggiorna al suo posto, una nuova va in fondo', () => {
  let list = upsertReported([], normalizeEntry({ id: 'public', host: 'bore.pub', port: 41417 }), T0);
  list = upsertReported(list, normalizeEntry({ id: 'nas', host: '192.168.0.108', port: 8585 }), T0);
  assert.deepEqual(list.map((s) => s.id), ['public', 'nas']);

  list = upsertReported(
    list, normalizeEntry({ id: 'public', name: 'Public', host: 'bore.pub', port: 41417 }), T1);
  assert.deepEqual(list.map((s) => s.id), ['public', 'nas'], 'la prima riga resta prima');
  assert.equal(list[0].name, 'Public');

  list = upsertReported(list, normalizeEntry({ id: 'pc', host: '10.0.0.5', port: 8585 }), T1);
  assert.deepEqual(list.map((s) => s.id), ['public', 'nas', 'pc']);
});

test('un indirizzo che non cambia non cambia il testo del file', () => {
  const first = upsertReported(
    [], normalizeEntry({ id: 'nas', name: 'NAS', host: '192.168.0.108', port: 8585 }), T0);
  assert.equal(first[0].updatedAt, T0);

  const again = upsertReported(
    first, normalizeEntry({ id: 'nas', name: 'NAS', host: '192.168.0.108', port: 8585 }), T1);
  assert.equal(again[0].updatedAt, T0,
    'updatedAt dice quando l indirizzo e cambiato, non quando e arrivato l ultimo report');
  assert.equal(again[0].seenAt, T1,
    'seenAt e invece l ultimo report: e quello che fa scadere la riga');

  assert.equal(
    serializeRegistry(mergeRegistry({ servers: [] }, first, [])),
    serializeRegistry(mergeRegistry({ servers: [] }, again, [])),
    'lo stesso indirizzo riscritto produce gli stessi byte',
  );
});

test('un indirizzo che cambia aggiorna updatedAt', () => {
  const first = upsertReported(
    [], normalizeEntry({ id: 'nas', name: 'NAS', host: '192.168.0.108', port: 8585 }), T0);
  const moved = upsertReported(
    first, normalizeEntry({ id: 'nas', name: 'NAS', host: '192.168.0.109', port: 8585 }), T1);
  assert.equal(moved[0].updatedAt, T1);
  assert.equal(moved[0].host, '192.168.0.109');
});

test('le righe non piu sentite scadono, quelle sentite no', () => {
  const list = upsertReported(
    upsertReported([], normalizeEntry({ id: 'a', host: '1.1.1.1', port: 1 }), T0),
    normalizeEntry({ id: 'b', host: '2.2.2.2', port: 2 }), T1);

  const nowMs = Date.parse(T1) + 60000;
  const fresh = expire(list, 45 * 60000, nowMs);
  assert.deepEqual(fresh.live.map((s) => s.id), ['b']);
  assert.deepEqual(fresh.dropped, ['a']);
});

test('il merge lascia public al primo posto e il pair di cima uguale a lui', () => {
  const remote = {
    updatedAt: T0,
    host: 'bore.pub',
    port: 41417,
    tls: true,
    fingerprint: 'AA:BB',
    servers: [
      { id: 'public', name: 'Public server', host: 'bore.pub', port: 41417, updatedAt: T0 },
      { id: 'nas', name: 'NAS', host: '192.168.0.108', port: 8585, updatedAt: T0 },
    ],
  };
  const reported = upsertReported(
    [], normalizeEntry({ id: 'nas', name: 'NAS', host: '192.168.0.108', port: 8585 }), T1);
  const merged = mergeRegistry(remote, reported, []);

  assert.deepEqual(merged.servers.map((s) => s.id), ['public', 'nas']);
  assert.equal(merged.host, 'bore.pub', 'il pair di cima resta l indirizzo del tunnel');
  assert.equal(merged.port, 41417);
  assert.equal(merged.tls, true);
  assert.equal(merged.fingerprint, 'AA:BB');
  assert.equal(merged.servers[1].updatedAt, T1);
  assert.equal(Object.prototype.hasOwnProperty.call(merged.servers[1], 'seenAt'), false,
    'seenAt non si pubblica');
});

test('il merge aggiunge i server nuovi in fondo', () => {
  const remote = { servers: [{ id: 'public', host: 'bore.pub', port: 41417, updatedAt: T0 }] };
  const reported = upsertReported(
    [], normalizeEntry({ id: 'pc', name: 'PC', host: '10.0.0.5', port: 8585 }), T1);
  const merged = mergeRegistry(remote, reported, []);
  assert.deepEqual(merged.servers.map((s) => s.id), ['public', 'pc']);
  assert.equal(merged.host, 'bore.pub');
});

test('una riga scaduta sparisce, ma se ha appena ripubblicato resta', () => {
  const remote = { servers: [{ id: 'nas', host: '192.168.0.108', port: 8585, updatedAt: T0 }] };
  const gone = mergeRegistry(remote, [], ['nas']);
  assert.deepEqual(gone.servers, []);
  assert.equal(gone.host, '');

  const reported = upsertReported(
    [], normalizeEntry({ id: 'nas', host: '192.168.0.108', port: 8585 }), T1);
  const back = mergeRegistry(remote, reported, ['nas']);
  assert.deepEqual(back.servers.map((s) => s.id), ['nas']);
  assert.equal(back.host, '192.168.0.108');
});

test('un file remoto assente o rotto non perde le righe riportate', () => {
  const reported = upsertReported(
    [], normalizeEntry({ id: 'nas', host: '192.168.0.108', port: 8585 }), T0);
  for (const remote of [null, undefined, '', 'non json', [], { servers: 'no' }, { servers: [null, 7] }]) {
    const merged = mergeRegistry(remote, reported, []);
    assert.deepEqual(merged.servers.map((s) => s.id), ['nas'], JSON.stringify(remote));
    assert.equal(merged.host, '192.168.0.108');
  }
});

test('updatedAt di cima e quello della riga piu recente', () => {
  const remote = {
    updatedAt: T0,
    servers: [{ id: 'public', host: 'bore.pub', port: 41417, updatedAt: T0 }],
  };
  const reported = upsertReported(
    [], normalizeEntry({ id: 'nas', host: '192.168.0.108', port: 8585 }), T1);
  assert.equal(mergeRegistry(remote, reported, []).updatedAt, T1);
  assert.equal(mergeRegistry(remote, [], []).updatedAt, T0, 'senza novita resta com era');
});

test('il markdown nomina ogni server', () => {
  const merged = mergeRegistry(
    { servers: [{ id: 'public', name: 'Public server', host: 'bore.pub', port: 41417, updatedAt: T0 }] },
    upsertReported([], normalizeEntry({ id: 'nas', host: '192.168.0.108', port: 8585 }), T0),
    []);
  const md = renderMarkdown(merged);
  assert.match(md, /Public server/);
  assert.match(md, /`public`/);
  assert.match(md, /`bore\.pub:41417`/);
  assert.match(md, /`192\.168\.0\.108:8585`/);
  assert.match(md, new RegExp(merged.updatedAt));
});
