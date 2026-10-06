'use strict';

/**
 * Publishes this server's address into the shared registry
 * (`whatsappforwp-endpoint/endpoint.json`), so the phone always knows where the
 * server is and has more than one address to try when one goes down.
 *
 * Why the adapter and not the tunnel: the tunnel knows the *public* address and
 * publishes that, but a server that is reachable directly - a machine running
 * `node server.js`, or a container on a LAN - has a local address of its own
 * that nobody else can guess. The adapter is the only component that is running
 * on the server itself, so it is the only one that can announce that address.
 *
 * The file is a list now: this module reads it, replaces this server's own row
 * (matched by id) and writes it back, leaving the other servers' rows alone.
 * Everything is reported and nothing throws: a server that cannot publish is
 * still a working server, and the address it failed to announce is one the
 * tunnel or the operator can publish by hand.
 *
 * A `fetchImpl` can be passed in for the tests; on a real run the global
 * `fetch` of Node 18 is used.
 */

const os = require('os');

const { VIRTUAL } = require('./discovery');
const { parseRegistry, upsertServer, serializeRegistry } = require('./endpoint-registry');

const DEFAULT_REPO = 'vincenzosco/whatsappforwp-endpoint';
const FILE = 'endpoint.json';
const API = 'https://api.github.com';

/**
 * The address another machine on the same network can dial: the first
 * non-internal IPv4 on a physical interface. The virtual ones (docker, VPN,
 * hotspot) lead nowhere for the phone, exactly as in discovery.js.
 */
function detectLocalAddress(interfaces) {
  const found = interfaces || os.networkInterfaces();
  for (const name of Object.keys(found || {})) {
    if (VIRTUAL.test(name)) continue;
    for (const info of found[name] || []) {
      if (info.family !== 'IPv4' || info.internal) continue;
      return info.address;
    }
  }
  return '';
}

function isValidPort(value) {
  return Number.isInteger(value) && value >= 1 && value <= 65535;
}

/**
 * @param {object} options
 * @param {object} options.endpoint   config.endpoint ({ publish, repo, token, serverId, serverName, host, port, intervalMinutes })
 * @param {number} [options.bridgePort]  the port the app dials (fallback for endpoint.port)
 * @param {function} [options.log]     (level, message)
 * @param {function} [options.fetchImpl]
 * @param {function} [options.now]     () => ISO string
 * @param {object}   [options.interfaces]
 */
function createEndpointPublisher(options) {
  const opts = options || {};
  const endpoint = opts.endpoint || {};
  const log = typeof opts.log === 'function' ? opts.log : () => {};
  const fetchImpl = opts.fetchImpl || (typeof fetch === 'function' ? fetch : null);
  const now = typeof opts.now === 'function' ? opts.now : () => new Date().toISOString();
  const bridgePort = opts.bridgePort;

  function contentsUrl() {
    const repo = endpoint.repo || DEFAULT_REPO;
    return `${API}/repos/${repo}/contents/${FILE}`;
  }

  function headers() {
    return {
      Authorization: `Bearer ${endpoint.token}`,
      Accept: 'application/vnd.github+json',
      'Content-Type': 'application/json',
      'User-Agent': 'whatsappforwp-adapter',
    };
  }

  /** Reads the current file: `{ text, sha }`, with an empty text when it is not there. */
  async function readCurrent(url) {
    const response = await fetchImpl(url, { headers: headers() });
    if (!response) return { text: '', sha: '' };
    if (response.status === 404) return { text: '', sha: '' };
    if (!response.ok) throw new Error(`GitHub answered ${response.status}`);

    const payload = await response.json();
    return {
      text: payload && payload.content ? Buffer.from(payload.content, 'base64').toString('utf8') : '',
      sha: payload && payload.sha ? payload.sha : '',
    };
  }

  async function publish() {
    if (!endpoint.publish) return false;
    // Only the token is required: the repo falls back to DEFAULT_REPO in
    // contentsUrl(), which is the repository the startup banner already names.
    if (!endpoint.token) {
      log('WARN', '[endpoint] ENDPOINT_PUBLISH is on but no token is set: not published');
      return false;
    }
    if (!fetchImpl) {
      log('WARN', '[endpoint] no fetch available on this Node (18+ needed): not published');
      return false;
    }

    const host = endpoint.host || detectLocalAddress(opts.interfaces);
    const port = isValidPort(endpoint.port) ? endpoint.port : bridgePort;
    if (!host) {
      log('WARN', '[endpoint] no address to publish: set ENDPOINT_HOST');
      return false;
    }
    if (!isValidPort(port)) {
      log('WARN', '[endpoint] no port to publish: set ENDPOINT_PORT (or BRIDGE_PORT)');
      return false;
    }

    const id = endpoint.serverId || os.hostname();
    const name = endpoint.serverName || id;
    const url = contentsUrl();

    try {
      const current = await readCurrent(url);
      const updated = upsertServer(current.text, { id, name, host, port }, { now: now() });

      const body = {
        message: `Publish the server ${host}:${port}`,
        content: Buffer.from(serializeRegistry(updated), 'utf8').toString('base64'),
      };
      if (current.sha) body.sha = current.sha;

      const response = await fetchImpl(url, {
        method: 'PUT',
        headers: headers(),
        body: JSON.stringify(body),
      });

      if (response && response.ok) {
        log('OK', `[endpoint] published ${host}:${port} as ${id}`);
        return true;
      }
      log('WARN', `[endpoint] could not publish ${host}:${port} (GitHub answered ${response ? response.status : 'nothing'})`);
      return false;
    } catch (err) {
      log('WARN', `[endpoint] could not publish ${host}:${port}: ${err.message}`);
      return false;
    }
  }

  /**
   * Publishes once, then keeps the row fresh so a DHCP change is picked up
   * without a restart. The timer is unref'd: it must not keep the process alive.
   */
  function start() {
    publish();
    const minutes = Math.max(1, Number(endpoint.intervalMinutes) || 30);
    const timer = setInterval(() => { publish(); }, minutes * 60000);
    if (typeof timer.unref === 'function') timer.unref();
    return timer;
  }

  return { publish, start };
}

module.exports = {
  DEFAULT_REPO,
  FILE,
  detectLocalAddress,
  createEndpointPublisher,
};
