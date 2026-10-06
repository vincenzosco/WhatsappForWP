'use strict';

/**
 * The server registry published in `whatsappforwp-endpoint/endpoint.json`.
 *
 * Why it is a list now: a deployment can run more than one server, and the
 * phone has to keep working when the one it is using goes down by trying
 * another. The old file held a single address and nothing else; that shape is
 * still read (the top-level `host`/`port` are always present and mirror the
 * first entry), so an app built before this change keeps working.
 *
 *   {
 *     "updatedAt": "2026-10-06T10:00:00.000Z",
 *     "host": "bore.pub",        // = servers[0], kept for the old app
 *     "port": 41417,
 *     "tls": false,
 *     "fingerprint": "",
 *     "servers": [
 *       { "id": "public", "name": "Public server", "host": "bore.pub", "port": 41417, "updatedAt": "..." },
 *       { "id": "nas-lan",  "name": "NAS",           "host": "192.168.0.108", "port": 8585, "updatedAt": "..." }
 *     ]
 *   }
 *
 * Every server upserts its own entry by `id`, so two servers writing the file
 * do not have to know about each other, and the same server republishing
 * updates in place instead of appending a second row.
 *
 * Nothing here throws: the file is written by several machines and read by a
 * phone, so a malformed value is dropped field by field instead of costing the
 * whole list.
 */

const MIN_PORT = 1;
const MAX_PORT = 65535;

function cleanString(value) {
  return typeof value === 'string' ? value.trim() : '';
}

function toPort(value) {
  const port = typeof value === 'number' ? value : parseInt(value, 10);
  return Number.isInteger(port) && port >= MIN_PORT && port <= MAX_PORT ? port : 0;
}

/** One entry, or null when it carries no usable address. */
function normalizeServer(raw, fallbackId) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null;

  const host = cleanString(raw.host);
  const port = toPort(raw.port);
  if (!host || port === 0) return null;

  const id = cleanString(raw.id) || cleanString(fallbackId);
  return {
    id: id || 'server',
    name: cleanString(raw.name),
    host,
    port,
    updatedAt: cleanString(raw.updatedAt),
  };
}

function emptyRegistry() {
  return { updatedAt: '', host: '', port: 0, tls: false, fingerprint: '', servers: [] };
}

/**
 * The file as an object with a `servers` array, whether it is a string or an
 * object and whether it uses the list form or the old single-address form.
 */
function parseRegistry(input) {
  let raw = input;
  if (typeof input === 'string') {
    try {
      raw = JSON.parse(input);
    } catch (e) {
      raw = null;
    }
  }
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return emptyRegistry();

  const servers = [];
  if (Array.isArray(raw.servers)) {
    for (let i = 0; i < raw.servers.length; i++) {
      const server = normalizeServer(raw.servers[i], 'server-' + (i + 1));
      if (!server) continue;
      servers.push(server);
    }
  }

  // The old single-address file, or a list whose top-level pair is still the
  // preferred one: it is a server of its own, and it goes first.
  const top = normalizeServer(
    { id: 'public', host: raw.host, port: raw.port, updatedAt: raw.updatedAt }, 'public');
  const alreadyListed = top
    && servers.some((s) => s.host === top.host && s.port === top.port);
  if (top && !alreadyListed) servers.unshift(top);

  const first = servers.length ? servers[0] : null;
  return {
    updatedAt: cleanString(raw.updatedAt),
    host: first ? first.host : '',
    port: first ? first.port : 0,
    tls: raw.tls === true,
    fingerprint: cleanString(raw.fingerprint),
    servers,
  };
}

/**
 * The registry with `entry` added or replaced. Replaced by `id`, so a server
 * that republishes edits its own row instead of adding another one. With
 * `options.preferred` the entry is moved to the front, which is what the public
 * tunnel does: the top-level pair the old app reads mirrors the first entry.
 */
function upsertServer(registry, entry, options) {
  const opts = options || {};
  const now = opts.now || new Date().toISOString();
  const base = parseRegistry(registry);
  const server = normalizeServer(
    {
      id: entry && entry.id,
      name: entry && entry.name,
      host: entry && entry.host,
      port: entry && entry.port,
      updatedAt: (entry && entry.updatedAt) || now,
    },
    entry && entry.id);
  if (!server) return base;

  const servers = base.servers.filter((s) => s.id !== server.id);
  if (opts.preferred) servers.unshift(server);
  else servers.push(server);

  const first = servers[0];
  return {
    updatedAt: now,
    host: first.host,
    port: first.port,
    tls: base.tls,
    fingerprint: base.fingerprint,
    servers,
  };
}

/** The registry as the text to write to `endpoint.json`. */
function serializeRegistry(registry) {
  return JSON.stringify(parseRegistry(registry), null, 2) + '\n';
}

module.exports = {
  MIN_PORT,
  MAX_PORT,
  normalizeServer,
  parseRegistry,
  upsertServer,
  serializeRegistry,
};
