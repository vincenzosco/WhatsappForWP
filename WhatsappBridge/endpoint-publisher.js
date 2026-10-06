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
 * How it writes: a token in the configuration means the GitHub Contents API,
 * which is what a container without gh can use. With no token, the machine's
 * own GitHub CLI (`gh api`) is used instead, so a server that has run
 * `gh auth login` publishes without storing a secret anywhere. Nothing here is
 * a dependency: gh is an OS binary.
 *
 * A `fetchImpl` and an `execImpl` can be passed in for the tests; on a real run
 * the global `fetch` of Node 18 and `child_process.execFile` are used. The gh
 * call is asynchronous for a reason: a synchronous child process would block
 * the adapter's event loop, and with it every WhatsApp frame.
 */

const os = require('os');
const { execFile } = require('child_process');

const { VIRTUAL } = require('./discovery');
const { parseRegistry, upsertServer, serializeRegistry } = require('./endpoint-registry');

const DEFAULT_REPO = 'vincenzosco/whatsappforwp-endpoint';
const FILE = 'endpoint.json';
const API = 'https://api.github.com';
// The second way to write the file: the GitHub CLI, already logged in on the
// machine. `gh` is an OS binary, not a package: the adapter keeps zero
// dependencies.
const GH = 'gh';

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
  // Asynchronous on purpose: this is the adapter's own event loop. A
  // synchronous child process would freeze every WhatsApp frame for the whole
  // round trip to GitHub, at startup and at every refresh.
  const execImpl = typeof opts.execImpl === 'function'
    ? opts.execImpl
    : (command, args) => new Promise((resolve, reject) => {
      execFile(command, args, { encoding: 'utf8' }, (err, stdout) => {
        if (err) reject(err);
        else resolve(stdout);
      });
    });
  const now = typeof opts.now === 'function' ? opts.now : () => new Date().toISOString();
  const bridgePort = opts.bridgePort;

  function repo() {
    return endpoint.repo || DEFAULT_REPO;
  }

  function contentsUrl() {
    return `${API}/repos/${repo()}/contents/${FILE}`;
  }

  function headers() {
    return {
      Authorization: `Bearer ${endpoint.token}`,
      Accept: 'application/vnd.github+json',
      'Content-Type': 'application/json',
      'User-Agent': 'whatsappforwp-adapter',
    };
  }

  /** Reads the current file through the API: `{ text, sha }`, empty when it is not there. */
  async function readViaApi() {
    const response = await fetchImpl(contentsUrl(), { headers: headers() });
    if (!response) return { text: '', sha: '' };
    if (response.status === 404) return { text: '', sha: '' };
    if (!response.ok) throw new Error(`GitHub answered ${response.status}`);

    const payload = await response.json();
    return {
      text: payload && payload.content ? Buffer.from(payload.content, 'base64').toString('utf8') : '',
      sha: payload && payload.sha ? payload.sha : '',
    };
  }

  /** The same read through the CLI, for a machine that has run `gh auth login`. */
  async function readViaGh() {
    const payload = JSON.parse(await execImpl(GH, ['api', `repos/${repo()}/contents/${FILE}`]));
    return {
      text: payload && payload.content ? Buffer.from(payload.content, 'base64').toString('utf8') : '',
      sha: payload && payload.sha ? payload.sha : '',
    };
  }

  async function writeViaApi(body) {
    const response = await fetchImpl(contentsUrl(), {
      method: 'PUT',
      headers: headers(),
      body: JSON.stringify(body),
    });
    if (!response || !response.ok) {
      throw new Error(`GitHub answered ${response ? response.status : 'nothing'}`);
    }
  }

  async function writeViaGh(body) {
    const args = [
      'api', '--method', 'PUT', `repos/${repo()}/contents/${FILE}`,
      '-f', `message=${body.message}`,
      '-f', `content=${body.content}`,
    ];
    if (body.sha) args.push('-f', `sha=${body.sha}`);
    await execImpl(GH, args);
  }

  /** The registry's door for a report. */
  function registryEndpoint() {
    return String(endpoint.registryUrl).replace(/\/+$/, '') + '/register';
  }

  /**
   * Tells the registry service on the VM where this server is. With a registry
   * configured this replaces writing the file: the credential that writes
   * `endpoint.json` belongs to the service, not to every container that reports
   * to it, so a deployment that uses one needs no GitHub token at all.
   *
   * It never throws: a registry that cannot be reached is a row that does not
   * appear, not a server that stops working, and it is deliberately not a
   * fallback to the GitHub path - two writers on one file is how rows disappear.
   */
  async function reportToRegistry() {
    if (!fetchImpl) {
      log('WARN', '[endpoint] no fetch available on this Node (18+ needed): not reported');
      return false;
    }

    const host = endpoint.host || detectLocalAddress(opts.interfaces);
    const port = isValidPort(endpoint.port) ? endpoint.port : bridgePort;
    if (!host) {
      log('WARN', '[endpoint] no address to report: set ENDPOINT_HOST');
      return false;
    }
    if (!isValidPort(port)) {
      log('WARN', '[endpoint] no port to report: set ENDPOINT_PORT (or BRIDGE_PORT)');
      return false;
    }

    const id = endpoint.serverId || os.hostname();
    const name = endpoint.serverName || id;

    try {
      const response = await fetchImpl(registryEndpoint(), {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'User-Agent': 'whatsappforwp-adapter',
        },
        body: JSON.stringify({ id, name, host, port, secret: endpoint.registrySecret }),
      });
      if (!response || !response.ok) {
        throw new Error(`the registry answered ${response ? response.status : 'nothing'}`);
      }
      log('OK', `[endpoint] reported ${host}:${port} as ${id} to ${endpoint.registryUrl}`);
      return true;
    } catch (err) {
      log('WARN', `[endpoint] could not report ${host}:${port} to the registry ` +
        `(not reported): ${err.message}`);
      return false;
    }
  }

  async function publish() {
    if (!endpoint.publish) return false;

    // The registry on the VM, when this deployment has one: this server reports
    // its address and nothing else. There is no repository and no token here.
    if (endpoint.registryUrl) return reportToRegistry();

    // Two ways to write the file. A token means the API, which works inside a
    // container where gh is not installed; with no token the machine's own
    // `gh auth login` is used, so the server stores no secret at all. The repo
    // falls back to DEFAULT_REPO, the repository the startup banner names.
    const viaApi = Boolean(endpoint.token);
    if (viaApi && !fetchImpl) {
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

    try {
      const current = viaApi ? await readViaApi() : await readViaGh();
      const updated = upsertServer(current.text, { id, name, host, port }, { now: now() });

      const body = {
        message: `Publish the server ${host}:${port}`,
        content: Buffer.from(serializeRegistry(updated), 'utf8').toString('base64'),
      };
      if (current.sha) body.sha = current.sha;

      if (viaApi) await writeViaApi(body);
      else await writeViaGh(body);

      log('OK', `[endpoint] published ${host}:${port} as ${id}`);
      return true;
    } catch (err) {
      log('WARN', `[endpoint] could not publish ${host}:${port} (not published): ${err.message}`);
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

  return { publish, start, reportToRegistry };
}

module.exports = {
  DEFAULT_REPO,
  FILE,
  detectLocalAddress,
  createEndpointPublisher,
};
