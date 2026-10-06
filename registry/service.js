'use strict';

/**
 * The shared registry: the service that runs on the VM.
 *
 * Why it exists: the address of every server used to be written into
 * `whatsappforwp-endpoint/endpoint.json` by the server itself, which meant every
 * deployment - including other people's - needed a GitHub token with write
 * access to that repository. Here the servers only report to one small service,
 * the service holds the list, and it is the only thing that writes the file. A
 * container needs no GitHub credential at all, and the credential exists in one
 * place instead of in every `.env` on the internet.
 *
 * The rules that make the list safe live in `registry.js` and are pure; this
 * file is the plumbing around them: an HTTP door, a file to survive a reboot,
 * and a timer that pushes the list to GitHub.
 *
 * Nothing here throws to the caller: a GitHub outage, a full disk or a bad
 * request must leave the process serving, because the servers reporting to it
 * have nowhere else to go.
 *
 * Environment:
 *   REGISTRY_PORT           8787
 *   REGISTRY_FILE           /var/lib/whatsappforwp-registry/registry.json
 *   REGISTRY_TOKEN          the shared secret a report must carry; empty = open
 *   REGISTRY_TTL_MINUTES    1440  (a row nobody refreshes disappears)
 *   REGISTRY_DEBOUNCE_MS    5000  (how long after a report the file is written)
 *   REGISTRY_PUBLISH_MINUTES 15   (the heartbeat, which also sweeps the TTL)
 *   REGISTRY_REPO           vincenzosco/whatsappforwp-endpoint
 *   REGISTRY_GH_TOKEN       the credential to write the file with
 */

const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const {
  normalizeEntry,
  upsertReported,
  expire,
  toPublished,
  mergeRegistry,
  serializeRegistry,
  renderMarkdown,
} = require('./registry');

const API = 'https://api.github.com';
const FILE_JSON = 'endpoint.json';
const FILE_MD = 'endpoint.md';

const DEFAULTS = {
  REGISTRY_PORT: '8787',
  REGISTRY_FILE: '/var/lib/whatsappforwp-registry/registry.json',
  REGISTRY_TOKEN: '',
  REGISTRY_TTL_MINUTES: '1440',
  REGISTRY_DEBOUNCE_MS: '5000',
  REGISTRY_PUBLISH_MINUTES: '15',
  REGISTRY_REPO: 'vincenzosco/whatsappforwp-endpoint',
  REGISTRY_GH_TOKEN: '',
};

/** A request body larger than this is not a server address. */
const MAX_BODY = 8 * 1024;

function pick(env, key) {
  const value = env[key];
  return value === undefined || value === null || value === '' ? DEFAULTS[key] : String(value);
}

/** The same string, compared without telling an attacker how far it got. */
function sameSecret(expected, given) {
  if (!expected) return true;
  const a = Buffer.from(String(expected), 'utf8');
  const b = Buffer.from(String(given === undefined || given === null ? '' : given), 'utf8');
  if (a.length !== b.length) return false;
  return crypto.timingSafeEqual(a, b);
}

/**
 * @param {object} options
 * @param {string} options.token    the GitHub credential (empty = publishing off)
 * @param {string} options.repo     owner/name of the endpoint repository
 * @param {string} options.secret   the shared secret a report must carry
 * @param {string} options.file     where the list is kept between restarts
 * @param {number} options.ttlMs
 * @param {number} options.debounceMs
 * @param {number} options.publishMinutes
 * @param {function} [options.fetchImpl]
 * @param {function} [options.now]  () => ISO string
 * @param {function} [options.nowMs] () => epoch ms
 * @param {function} [options.log]  (level, message)
 */
function createRegistryService(options) {
  const opts = options || {};
  const log = typeof opts.log === 'function' ? opts.log : () => {};
  const fetchImpl = opts.fetchImpl || (typeof fetch === 'function' ? fetch : null);
  const now = typeof opts.now === 'function' ? opts.now : () => new Date().toISOString();
  const nowMs = typeof opts.nowMs === 'function' ? opts.nowMs : () => Date.now();

  const file = opts.file || DEFAULTS.REGISTRY_FILE;
  const repo = opts.repo || DEFAULTS.REGISTRY_REPO;
  const token = opts.token || '';
  const secret = opts.secret || '';
  const ttlMs = typeof opts.ttlMs === 'number' ? opts.ttlMs : 1440 * 60000;
  const debounceMs = typeof opts.debounceMs === 'number' ? opts.debounceMs : 5000;
  const publishMinutes = typeof opts.publishMinutes === 'number' ? opts.publishMinutes : 15;

  /** The live rows, each with the `seenAt` that makes it expire. */
  let rows = [];
  /** The ids that went stale: they are taken out of the file once, on the next write. */
  let dropped = [];
  let lastPublish = { at: '', ok: false, error: '' };
  let publishTimer = null;
  let heartbeatTimer = null;

  // ── the list on disk ────────────────────────────────────────────────────

  function load() {
    try {
      const raw = JSON.parse(fs.readFileSync(file, 'utf8'));
      rows = Array.isArray(raw.servers) ? raw.servers : [];
      dropped = Array.isArray(raw.dropped) ? raw.dropped : [];
      log('INFO', `registry: ${rows.length} server(s) read back from ${file}`);
    } catch (err) {
      // A file that is not there is a first start, not a fault.
      if (err && err.code !== 'ENOENT') log('WARN', `registry: could not read ${file}: ${err.message}`);
    }
    return rows;
  }

  function save() {
    try {
      fs.mkdirSync(path.dirname(file), { recursive: true });
      const tmp = file + '.tmp';
      fs.writeFileSync(tmp, JSON.stringify({ servers: rows, dropped }, null, 2));
      fs.renameSync(tmp, file);
    } catch (err) {
      log('WARN', `registry: could not write ${file}: ${err.message}`);
    }
  }

  // ── what the app reads ─────────────────────────────────────────────────

  function contentsUrl(name) {
    return `${API}/repos/${repo}/contents/${name}`;
  }

  function headers() {
    return {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'Content-Type': 'application/json',
      'User-Agent': 'whatsappforwp-registry',
    };
  }

  async function readRemote(name) {
    const response = await fetchImpl(contentsUrl(name), { headers: headers() });
    if (!response) return { text: '', sha: '' };
    if (response.status === 404) return { text: '', sha: '' };
    if (!response.ok) throw new Error(`GitHub answered ${response.status} reading ${name}`);
    const payload = await response.json();
    return {
      text: payload && payload.content ? Buffer.from(payload.content, 'base64').toString('utf8') : '',
      sha: payload && payload.sha ? payload.sha : '',
    };
  }

  async function writeRemote(name, text, sha, message) {
    const body = { message, content: Buffer.from(text, 'utf8').toString('base64') };
    if (sha) body.sha = sha;
    const response = await fetchImpl(contentsUrl(name), {
      method: 'PUT',
      headers: headers(),
      body: JSON.stringify(body),
    });
    if (!response || !response.ok) {
      throw new Error(`GitHub answered ${response ? response.status : 'nothing'} writing ${name}`);
    }
  }

  /**
   * The list, as the file the app downloads. It is written only when the text
   * really changed: the heartbeat runs every quarter of an hour and must not
   * leave a commit every quarter of an hour.
   */
  async function publishNow() {
    if (!token) {
      lastPublish = { at: now(), ok: false, error: 'REGISTRY_GH_TOKEN is not set' };
      return false;
    }
    if (!fetchImpl) {
      lastPublish = { at: now(), ok: false, error: 'no fetch on this Node (18+ needed)' };
      return false;
    }

    try {
      const remote = await readRemote(FILE_JSON);
      let remoteParsed = null;
      try { remoteParsed = JSON.parse(remote.text); } catch (e) { remoteParsed = null; }

      const merged = mergeRegistry(remoteParsed, rows, dropped);
      const json = serializeRegistry(merged);

      if (json === remote.text) {
        lastPublish = { at: now(), ok: true, error: '' };
        return false;
      }

      const message = `Publish ${merged.servers.length} server(s)`;
      await writeRemote(FILE_JSON, json, remote.sha, message);

      const remoteMd = await readRemote(FILE_MD);
      const md = renderMarkdown(merged);
      if (md !== remoteMd.text) await writeRemote(FILE_MD, md, remoteMd.sha, message);

      // The rows taken out of the file are gone from it now: forgetting them
      // here is what keeps the next publish from touching the file again.
      if (dropped.length) { dropped = []; save(); }

      lastPublish = { at: now(), ok: true, error: '' };
      log('OK', `registry: published ${merged.servers.length} server(s) to ${repo}`);
      return true;
    } catch (err) {
      lastPublish = { at: now(), ok: false, error: err.message };
      log('WARN', `registry: could not publish (${err.message})`);
      return false;
    }
  }

  function schedulePublish() {
    if (publishTimer) return;
    publishTimer = setTimeout(() => {
      publishTimer = null;
      publishNow();
    }, debounceMs);
    if (typeof publishTimer.unref === 'function') publishTimer.unref();
  }

  /** The rows nobody refreshes, taken out of the list and out of the file. */
  function sweep() {
    const result = expire(rows, ttlMs, nowMs());
    rows = result.live;
    if (result.dropped.length) {
      dropped = dropped.concat(result.dropped);
      log('INFO', `registry: ${result.dropped.length} stale row(s): ${result.dropped.join(', ')}`);
      save();
      schedulePublish();
    }
  }

  // ── the HTTP door ──────────────────────────────────────────────────────

  function answer(res, status, payload) {
    const body = JSON.stringify(payload) + '\n';
    res.writeHead(status, {
      'Content-Type': 'application/json; charset=utf-8',
      'Content-Length': Buffer.byteLength(body),
      'Cache-Control': 'no-store',
    });
    res.end(body);
  }

  function readBody(req, callback) {
    let body = '';
    let tooBig = false;
    req.on('data', (chunk) => {
      if (tooBig) return;
      body += chunk;
      if (body.length > MAX_BODY) {
        tooBig = true;
        body = '';
      }
    });
    req.on('end', () => callback(tooBig ? null : body));
    req.on('error', () => callback(null));
  }

  /** `POST /register`: one server's address. */
  function register(body, res) {
    if (body === null) {
      answer(res, 413, { ok: false, error: 'body too large' });
      return;
    }

    let report = null;
    try { report = JSON.parse(body); } catch (e) { report = null; }
    if (!report || typeof report !== 'object') {
      answer(res, 400, { ok: false, error: 'expected a JSON object' });
      return;
    }

    if (!sameSecret(secret, report.secret)) {
      log('WARN', 'registry: a report with the wrong secret was refused');
      answer(res, 401, { ok: false, error: 'unauthorized' });
      return;
    }

    const entry = normalizeEntry(report);
    if (!entry) {
      answer(res, 400, { ok: false, error: 'expected id, host and port (port 1-65535)' });
      return;
    }

    rows = upsertReported(rows, entry, now());
    dropped = dropped.filter((id) => id !== entry.id);
    save();
    schedulePublish();

    log('OK', `registry: ${entry.host}:${entry.port} registered as ${entry.id}`);
    answer(res, 200, { ok: true, id: entry.id, servers: rows.length });
  }

  function handleRequest(req, res) {
    const url = (req.url || '').split('?')[0];

    if (req.method === 'POST' && url === '/register') {
      readBody(req, (body) => register(body, res));
      return;
    }

    if (req.method === 'GET' && url === '/health') {
      answer(res, 200, {
        ok: true,
        servers: rows.length,
        updatedAt: now(),
        ttlMinutes: Math.round(ttlMs / 60000),
        publishMinutes,
        lastPublish,
      });
      return;
    }

    // The list as this service holds it, for a person and for the deploy check.
    if (req.method === 'GET' && url === '/servers') {
      answer(res, 200, { updatedAt: now(), servers: rows.map(toPublished) });
      return;
    }

    answer(res, 404, { ok: false, error: 'not found' });
  }

  const server = http.createServer(handleRequest);

  function listen(port) {
    return new Promise((resolve) => {
      server.listen(port, () => resolve(server.address()));
    });
  }

  function start(port) {
    server.on('error', (err) => log('ERR', `registry: ${err.message}`));
    listen(port).then((address) => {
      log('OK', `registry: listening on port ${address.port} (${rows.length} server(s) known)`);
    });

    sweep();
    // The first publish is immediate: a service that has just been restarted
    // must not wait a quarter of an hour to say where the servers are.
    publishNow();
    heartbeatTimer = setInterval(() => {
      sweep();
      publishNow();
    }, Math.max(1, publishMinutes) * 60000);
    if (typeof heartbeatTimer.unref === 'function') heartbeatTimer.unref();

    return server;
  }

  function stop() {
    if (publishTimer) clearTimeout(publishTimer);
    if (heartbeatTimer) clearInterval(heartbeatTimer);
    publishTimer = null;
    heartbeatTimer = null;
    try { server.close(); } catch (e) { /* nothing to close */ }
  }

  return {
    server,
    load,
    save,
    sweep,
    start,
    stop,
    listen,
    publishNow,
    handleRequest,
    state: () => ({ servers: rows.map(toPublished), dropped: dropped.slice(), lastPublish }),
  };
}

function main() {
  const env = process.env;
  const number = (key, fallback) => {
    const value = parseInt(pick(env, key), 10);
    return Number.isFinite(value) && value > 0 ? value : fallback;
  };

  const log = (level, message) => {
    const stamp = new Date().toISOString();
    // English, like every other line an operator reads.
    process.stdout.write(`${stamp} [${level}] ${message}\n`);
  };

  const service = createRegistryService({
    token: pick(env, 'REGISTRY_GH_TOKEN'),
    repo: pick(env, 'REGISTRY_REPO'),
    secret: pick(env, 'REGISTRY_TOKEN'),
    file: pick(env, 'REGISTRY_FILE'),
    ttlMs: number('REGISTRY_TTL_MINUTES', 1440) * 60000,
    debounceMs: number('REGISTRY_DEBOUNCE_MS', 5000),
    publishMinutes: number('REGISTRY_PUBLISH_MINUTES', 15),
    log,
  });

  if (!pick(env, 'REGISTRY_GH_TOKEN')) {
    log('WARN', 'registry: REGISTRY_GH_TOKEN is not set: the list is served but never published');
  }
  if (!pick(env, 'REGISTRY_TOKEN')) {
    log('WARN', 'registry: REGISTRY_TOKEN is not set: any server that can reach this port may register');
  }

  service.load();
  service.start(number('REGISTRY_PORT', 8787));

  const shutdown = () => {
    log('INFO', 'registry: stopping');
    service.stop();
    process.exit(0);
  };
  process.on('SIGTERM', shutdown);
  process.on('SIGINT', shutdown);
}

if (require.main === module) main();

module.exports = { createRegistryService, sameSecret, DEFAULTS, MAX_BODY };
