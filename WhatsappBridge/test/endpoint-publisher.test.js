'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { detectLocalAddress, createEndpointPublisher } = require('../endpoint-publisher');

function jsonResponse(status, payload) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => payload,
    text: async () => JSON.stringify(payload),
  };
}

function encode(text) {
  return Buffer.from(text, 'utf8').toString('base64');
}

test('detectLocalAddress tiene il primo IPv4 reale e salta i virtuali', () => {
  const address = detectLocalAddress({
    lo: [{ family: 'IPv4', internal: true, address: '127.0.0.1' }],
    docker0: [{ family: 'IPv4', internal: false, address: '172.17.0.1' }],
    en0: [{ family: 'IPv4', internal: false, address: '192.168.0.50' }],
  });
  assert.strictEqual(address, '192.168.0.50');
});

test('detectLocalAddress senza indirizzi utili torna vuoto', () => {
  assert.strictEqual(detectLocalAddress({ lo: [{ family: 'IPv4', internal: true, address: '127.0.0.1' }] }), '');
});

test('publish senza token e senza gh avvisa e non pubblica', async () => {
  const seen = [];
  const publisher = createEndpointPublisher({
    endpoint: { publish: true, repo: 'me/repo', token: '', host: '10.0.0.5', port: 8585 },
    log: (level, message) => seen.push(level + ' ' + message),
    // No token means the gh transport is tried: this is the machine that has
    // neither, and it must be a warning, never a crash.
    execImpl: () => {
      const err = new Error('spawn gh ENOENT');
      err.code = 'ENOENT';
      throw err;
    },
    fetchImpl: async () => { throw new Error('must not be called'); },
  });

  assert.strictEqual(await publisher.publish(), false);
  assert.ok(seen.some((line) => line.indexOf('not published') !== -1));
});

test('publish sostituisce la riga del server e lascia il resto intatto', async () => {
  const existing = JSON.stringify({
    host: 'bore.pub',
    port: 41417,
    servers: [{ id: 'public', name: 'Public server', host: 'bore.pub', port: 41417 }],
  });

  const calls = [];
  const publisher = createEndpointPublisher({
    endpoint: {
      publish: true, repo: 'me/repo', token: 'tok',
      serverId: 'pc', serverName: 'Study PC', host: '10.0.0.5', port: 8585,
    },
    log: () => {},
    now: () => '2026-10-06T12:00:00.000Z',
    fetchImpl: async (url, options) => {
      calls.push({ url, options });
      if (options && options.method === 'PUT') return jsonResponse(200, {});
      return jsonResponse(200, { sha: 'abc', content: encode(existing) });
    },
  });

  assert.strictEqual(await publisher.publish(), true);

  const put = calls.find((c) => c.options && c.options.method === 'PUT');
  assert.ok(put, 'una PUT e\' stata fatta');
  const body = JSON.parse(put.options.body);
  assert.strictEqual(body.sha, 'abc');

  const written = JSON.parse(Buffer.from(body.content, 'base64').toString('utf8'));
  assert.strictEqual(written.host, 'bore.pub', 'il pubblico resta il preferito');
  assert.strictEqual(written.port, 41417);
  assert.strictEqual(written.servers.length, 2);
  assert.deepStrictEqual(written.servers[0].id, 'public');
  assert.deepStrictEqual(written.servers[1], {
    id: 'pc', name: 'Study PC', host: '10.0.0.5', port: 8585, updatedAt: '2026-10-06T12:00:00.000Z',
  });
});

test('publish crea il file quando non esiste ancora', async () => {
  const calls = [];
  const publisher = createEndpointPublisher({
    endpoint: {
      publish: true, repo: 'me/repo', token: 'tok',
      serverId: 'first', host: '192.168.0.50', port: 8585,
    },
    log: () => {},
    fetchImpl: async (url, options) => {
      calls.push({ url, options });
      if (options && options.method === 'PUT') return jsonResponse(201, {});
      return jsonResponse(404, { message: 'Not Found' });
    },
  });

  assert.strictEqual(await publisher.publish(), true);

  const put = calls.find((c) => c.options && c.options.method === 'PUT');
  const body = JSON.parse(put.options.body);
  assert.strictEqual(body.sha, undefined, 'un file nuovo non ha sha da passare');
  const written = JSON.parse(Buffer.from(body.content, 'base64').toString('utf8'));
  assert.strictEqual(written.servers.length, 1);
  assert.strictEqual(written.servers[0].host, '192.168.0.50');
  assert.strictEqual(written.host, '192.168.0.50');
});

test('publish senza indirizzo avvisa e non pubblica', async () => {
  const seen = [];
  const publisher = createEndpointPublisher({
    endpoint: { publish: true, repo: 'me/repo', token: 'tok', serverId: 'pc', port: 8585 },
    interfaces: { lo: [{ family: 'IPv4', internal: true, address: '127.0.0.1' }] },
    log: (level, message) => seen.push(level + ' ' + message),
    fetchImpl: async () => { throw new Error('must not be called'); },
  });

  assert.strictEqual(await publisher.publish(), false);
  assert.ok(seen.some((line) => line.indexOf('set ENDPOINT_HOST') !== -1));
});

test('publish con ENDPOINT_PUBLISH spento non fa nulla', async () => {
  const publisher = createEndpointPublisher({
    endpoint: { publish: false, repo: 'me/repo', token: 'tok', host: '10.0.0.5', port: 8585 },
    fetchImpl: async () => { throw new Error('must not be called'); },
  });
  assert.strictEqual(await publisher.publish(), false);
});

test('publish senza token usa gh e fonde la lista', async () => {
  const existing = JSON.stringify({
    host: 'bore.pub',
    port: 41417,
    servers: [{ id: 'public', name: 'Public server', host: 'bore.pub', port: 41417 }],
  });

  const calls = [];
  const publisher = createEndpointPublisher({
    endpoint: {
      publish: true, repo: 'me/repo', token: '',
      serverId: 'nas', serverName: 'NAS', host: '192.168.0.108', port: 8585,
    },
    log: () => {},
    now: () => '2026-10-06T12:00:00.000Z',
    execImpl: (command, args) => {
      calls.push({ command, args });
      if (args.indexOf('--method') !== -1) return '';
      return JSON.stringify({ sha: 'abc', content: encode(existing) });
    },
  });

  assert.strictEqual(await publisher.publish(), true);

  const put = calls.find((c) => c.args.indexOf('--method') !== -1);
  assert.ok(put, 'una scrittura e stata fatta');
  assert.strictEqual(put.command, 'gh');
  const contentArg = put.args.find((a) => a.indexOf('content=') === 0);
  const written = JSON.parse(Buffer.from(contentArg.slice('content='.length), 'base64').toString('utf8'));
  assert.strictEqual(written.host, 'bore.pub', 'il pubblico resta il preferito');
  assert.strictEqual(written.servers.length, 2);
  assert.strictEqual(written.servers[1].id, 'nas');
  assert.strictEqual(written.servers[1].host, '192.168.0.108');
  assert.ok(put.args.indexOf('sha=abc') !== -1, 'lo sha letto viene rimandato');
});

test('publish senza token aspetta un execImpl asincrono', async () => {
  // Il publish reale usa child_process.execFile: l'adapter non deve bloccare il
  // suo ciclo di eventi mentre gh parla con GitHub. Un execImpl che promette,
  // come quello vero, deve funzionare esattamente come uno che ritorna subito.
  const calls = [];
  const publisher = createEndpointPublisher({
    endpoint: {
      publish: true, repo: 'me/repo', token: '',
      serverId: 'nas', serverName: 'NAS', host: '192.168.0.108', port: 8585,
    },
    log: () => {},
    execImpl: async (command, args) => {
      calls.push({ command, args });
      await Promise.resolve();
      if (args.indexOf('--method') !== -1) return '';
      return JSON.stringify({ sha: '', content: '' });
    },
  });

  assert.strictEqual(await publisher.publish(), true);
  assert.strictEqual(calls.length, 2);
});

test('publish con un registro manda la riga alla VM e non tocca GitHub', async () => {
  const calls = [];
  const seen = [];
  const publisher = createEndpointPublisher({
    endpoint: {
      publish: true,
      repo: 'me/repo',
      // The trailing slash is what an operator types: the report must not
      // become http://host//register.
      registryUrl: 'http://34.12.0.9:8787/',
      registrySecret: 's3cret',
      serverId: 'nas',
      serverName: 'NAS',
      host: '192.168.0.108',
      port: 8585,
      // A token is set and must still not be used: with a registry the file is
      // written by the service, not by this container.
      token: 'gh-token',
    },
    log: (level, message) => seen.push(level + ' ' + message),
    execImpl: async () => { throw new Error('gh must not be called'); },
    fetchImpl: async (url, init) => {
      calls.push({ url, init });
      return jsonResponse(200, { ok: true });
    },
  });

  assert.strictEqual(await publisher.publish(), true);
  assert.strictEqual(calls.length, 1, 'una sola chiamata: al registro');
  assert.strictEqual(calls[0].url, 'http://34.12.0.9:8787/register');
  assert.strictEqual(calls[0].init.method, 'POST');
  assert.deepStrictEqual(JSON.parse(calls[0].init.body), {
    id: 'nas', name: 'NAS', host: '192.168.0.108', port: 8585, secret: 's3cret',
  });
  assert.ok(seen.some((line) => line.indexOf('reported 192.168.0.108:8585 as nas') !== -1),
    seen.join(' | '));
});

test('un registro che rifiuta non fa ripiegare su GitHub', async () => {
  const calls = [];
  const seen = [];
  const publisher = createEndpointPublisher({
    endpoint: {
      publish: true,
      repo: 'me/repo',
      registryUrl: 'http://34.12.0.9:8787',
      registrySecret: 'sbagliato',
      serverId: 'nas',
      host: '192.168.0.108',
      port: 8585,
      token: 'gh-token',
    },
    log: (level, message) => seen.push(level + ' ' + message),
    execImpl: async () => { throw new Error('gh must not be called'); },
    fetchImpl: async (url, init) => {
      calls.push({ url, init });
      return jsonResponse(401, { ok: false, error: 'unauthorized' });
    },
  });

  assert.strictEqual(await publisher.publish(), false);
  assert.strictEqual(calls.length, 1);
  assert.strictEqual(calls.filter((c) => c.url.indexOf('/contents/') !== -1).length, 0,
    'un registro che rifiuta non deve far scrivere il file da qui: due scrittori, una riga persa');
  assert.ok(seen.some((line) => line.indexOf('could not report') !== -1), seen.join(' | '));
});

test('publish con token usa l API e non gh', async () => {
  const calls = [];
  const publisher = createEndpointPublisher({
    endpoint: { publish: true, repo: 'me/repo', token: 'tok', serverId: 'pc', host: '10.0.0.5', port: 8585 },
    log: () => {},
    execImpl: () => { throw new Error('must not be called'); },
    fetchImpl: async (url, options) => {
      calls.push({ url, options });
      if (options && options.method === 'PUT') return jsonResponse(200, {});
      return jsonResponse(404, { message: 'Not Found' });
    },
  });

  assert.strictEqual(await publisher.publish(), true);
  assert.strictEqual(calls.length, 2);
});
