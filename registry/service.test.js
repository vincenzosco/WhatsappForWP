'use strict';

/**
 * Tests of the registry service: the HTTP door, the shared secret, the file
 * that survives a restart, and what it publishes.
 *
 * The HTTP tests speak to a real server on an ephemeral port instead of poking
 * at `handleRequest`: the door is the whole contract with the containers, and a
 * test that hands the function a fake request would not have caught the body
 * cap or the status codes.
 *
 * GitHub is a `Map` behind a fake `fetchImpl`: nothing here talks to the
 * internet.
 */

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const { createRegistryService } = require('./service');

/** A GitHub Contents API in memory. */
function fakeGithub(initial) {
  const files = new Map(Object.entries(initial || {}));
  const calls = [];
  const fetchImpl = async (url, init) => {
    const method = (init && init.method) || 'GET';
    const name = decodeURIComponent(/\/contents\/([^/?]+)/.exec(url)[1]);
    calls.push({ method, name });
    if (method === 'GET') {
      if (!files.has(name)) return { ok: false, status: 404, json: async () => ({}) };
      return {
        ok: true,
        status: 200,
        json: async () => ({
          content: Buffer.from(files.get(name), 'utf8').toString('base64'),
          sha: 'sha-' + name,
        }),
      };
    }
    const body = JSON.parse(init.body);
    files.set(name, Buffer.from(body.content, 'base64').toString('utf8'));
    return { ok: true, status: 200, json: async () => ({}) };
  };
  return { fetchImpl, files, calls };
}

function tempFile() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'wp8-registry-'));
  return path.join(dir, 'registry.json');
}

/** A service with a real HTTP server on an ephemeral port. */
async function withService(options, work) {
  const github = fakeGithub(options.remote);
  const file = tempFile();
  const service = createRegistryService({
    token: 'gh-token',
    repo: 'owner/endpoint',
    secret: options.secret === undefined ? 's3cret' : options.secret,
    file,
    fetchImpl: github.fetchImpl,
    log: () => {},
    ...options.extra,
  });
  service.load();
  const address = await service.listen(0);
  const base = 'http://127.0.0.1:' + address.port;
  try {
    await work({ service, base, github, file });
  } finally {
    service.stop();
  }
}

function post(base, body) {
  return fetch(base + '/register', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: typeof body === 'string' ? body : JSON.stringify(body),
  });
}

test('una segnalazione registra il server e la riga finisce in /servers e su disco', async () => {
  await withService({}, async ({ service, base, file }) => {
    const response = await post(base, { id: 'nas', name: 'NAS', host: '192.168.0.108', port: 8585, secret: 's3cret' });
    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), { ok: true, id: 'nas', servers: 1 });

    const listed = await (await fetch(base + '/servers')).json();
    assert.deepEqual(listed.servers, [
      { id: 'nas', name: 'NAS', host: '192.168.0.108', port: 8585, updatedAt: listed.servers[0].updatedAt },
    ]);

    const onDisk = JSON.parse(fs.readFileSync(file, 'utf8'));
    assert.equal(onDisk.servers.length, 1);
    assert.ok(onDisk.servers[0].seenAt, 'seenAt e salvato: e quello che fa scadere la riga');
    assert.equal(service.state().servers.length, 1);
  });
});

test('la stessa id si aggiorna al suo posto invece di aggiungere una riga', async () => {
  await withService({}, async ({ base }) => {
    await post(base, { id: 'public', name: 'Public', host: 'bore.pub', port: 41417, secret: 's3cret' });
    await post(base, { id: 'nas', host: '192.168.0.108', port: 8585, secret: 's3cret' });
    await post(base, { id: 'public', name: 'Public', host: 'bore.pub', port: 41500, secret: 's3cret' });

    const listed = await (await fetch(base + '/servers')).json();
    assert.deepEqual(listed.servers.map((s) => s.id), ['public', 'nas']);
    assert.equal(listed.servers[0].port, 41500);
    assert.equal(listed.servers[0].name, 'Public');
  });
});

test('senza il segreto giusto la riga non entra', async () => {
  await withService({}, async ({ base }) => {
    assert.equal((await post(base, { id: 'nas', host: '192.168.0.108', port: 8585 })).status, 401);
    assert.equal((await post(base, { id: 'nas', host: '192.168.0.108', port: 8585, secret: 'no' })).status, 401);

    const health = await (await fetch(base + '/health')).json();
    assert.equal(health.servers, 0, 'una richiesta rifiutata non lascia righe');
  });
});

test('senza segreto configurato il registro e aperto', async () => {
  await withService({ secret: '' }, async ({ base }) => {
    const response = await post(base, { id: 'nas', host: '192.168.0.108', port: 8585 });
    assert.equal(response.status, 200);
  });
});

test('una riga rotta e un corpo enorme sono rifiutati senza fermare il servizio', async () => {
  await withService({}, async ({ base }) => {
    assert.equal((await post(base, 'non json')).status, 400);
    assert.equal((await post(base, { id: 'nas', host: '192.168.0.108', port: 0, secret: 's3cret' })).status, 400);
    assert.equal((await post(base, { id: 'nas due', host: '192.168.0.108', port: 8585, secret: 's3cret' })).status, 400);
    assert.equal((await post(base, 'x'.repeat(9000))).status, 413);

    // The service is still there, and still accepts a good report.
    assert.equal((await post(base, { id: 'nas', host: '192.168.0.108', port: 8585, secret: 's3cret' })).status, 200);
    assert.equal((await fetch(base + '/health')).status, 200);
    assert.equal((await fetch(base + '/nope')).status, 404);
  });
});

test('la pubblicazione scrive endpoint.json e endpoint.md e lascia public in cima', async () => {
  const remote = {
    'endpoint.json': JSON.stringify({
      updatedAt: '2026-10-06T09:00:00.000Z',
      host: 'bore.pub',
      port: 41417,
      tls: false,
      fingerprint: '',
      servers: [
        { id: 'public', name: 'Public server', host: 'bore.pub', port: 41417, updatedAt: '2026-10-06T09:00:00.000Z' },
        { id: 'vecchio', name: 'Vecchio', host: '10.9.9.9', port: 8585, updatedAt: '2026-10-06T09:00:00.000Z' },
      ],
    }, null, 2) + '\n',
    'endpoint.md': '# The servers\n',
  };

  await withService({ remote }, async ({ service, base, github }) => {
    await post(base, { id: 'nas', name: 'NAS', host: '192.168.0.108', port: 8585, secret: 's3cret' });
    const wrote = await service.publishNow();
    assert.equal(wrote, true);

    const published = JSON.parse(github.files.get('endpoint.json'));
    assert.deepEqual(published.servers.map((s) => s.id), ['public', 'vecchio', 'nas'],
      'le righe che non gestiamo restano, e restano in ordine');
    assert.equal(published.host, 'bore.pub', 'il pair di cima resta il tunnel');
    assert.equal(published.port, 41417);

    const services = published.servers.map((s) => Object.keys(s).sort().join(','));
    assert.ok(services.every((keys) => keys === 'host,id,name,port,updatedAt'),
      'seenAt non finisce nel file pubblicato: ' + services.join(' | '));

    assert.match(github.files.get('endpoint.md'), /`nas`/);
    assert.match(github.files.get('endpoint.md'), /`bore\.pub:41417`/);
  });
});

test('senza cambiamenti il file non viene riscritto', async () => {
  await withService({}, async ({ service, base, github }) => {
    await post(base, { id: 'nas', host: '192.168.0.108', port: 8585, secret: 's3cret' });
    assert.equal(await service.publishNow(), true);
    assert.equal(await service.publishNow(), false, 'un secondo publish senza novita non scrive');
    assert.equal(await service.publishNow(), false);

    const writes = github.calls.filter((c) => c.method === 'PUT');
    assert.equal(writes.length, 2, 'endpoint.json e endpoint.md, una volta sola: ' + JSON.stringify(writes));
  });
});

test('una riga che questo servizio aveva e non sente piu sparisce dal file', async () => {
  const remote = {
    // A row nobody ever reported here stays where it is: the file can hold a row
    // written by hand, and this service is not its owner.
    'endpoint.json': JSON.stringify({
      servers: [{ id: 'esterno', name: 'Esterno', host: '10.1.1.1', port: 8585, updatedAt: '2026-10-01T00:00:00.000Z' }],
    }, null, 2) + '\n',
  };

  let clock = Date.parse('2026-10-06T12:00:00.000Z');
  await withService({
    remote,
    extra: { ttlMs: 60000, nowMs: () => clock, now: () => new Date(clock).toISOString() },
  }, async ({ service, base, github }) => {
    await post(base, { id: 'nas', name: 'NAS', host: '192.168.0.108', port: 8585, secret: 's3cret' });
    await service.publishNow();
    let published = JSON.parse(github.files.get('endpoint.json'));
    assert.deepEqual(published.servers.map((s) => s.id), ['esterno', 'nas']);

    // The NAS stops reporting: five minutes later its row is stale.
    clock += 5 * 60000;
    service.sweep();
    assert.deepEqual(service.state().dropped, ['nas']);
    await service.publishNow();

    published = JSON.parse(github.files.get('endpoint.json'));
    assert.deepEqual(published.servers.map((s) => s.id), ['esterno'],
      'la riga non piu sentita sparisce anche dal file, quella scritta da altri resta');
    assert.equal(published.host, '10.1.1.1');
  });
});
