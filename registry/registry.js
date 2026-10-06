'use strict';

/**
 * The registry logic: the rows the servers report, and how they are merged into
 * the file the app reads.
 *
 * Why this file exists: `endpoint.json` used to be written by every server on
 * its own, each with a GitHub token of its own. With one writer - the small
 * service on the VM - the list has to be collected in one place, and the rules
 * that make that safe belong somewhere testable and free of I/O. Everything
 * here is a pure function.
 *
 * Two properties matter more than the rest:
 *
 *  - **a row never moves.** The order of `servers` is the order the app tries
 *    the addresses, and `servers[0]` is also the top-level `host`/`port` that an
 *    app built before the list reads. A merge that filters and re-pushes would
 *    move the tunnel's row to the end and point an old app at a LAN address.
 *  - **the text does not change when nothing changed.** A server reports the
 *    same address every half hour; `updatedAt` therefore records when the
 *    address last changed, while `seenAt` - never published - records the last
 *    report and is what makes a row expire. Without that split, every heartbeat
 *    would be a commit.
 */

const MAX_ID = 64;
const MAX_NAME = 64;
const MAX_HOST = 253;
const MIN_PORT = 1;
const MAX_PORT = 65535;

// The id ends up in a JSON file, in a markdown bullet and in a log line: the
// conservative alphabet keeps it out of every quoting problem at once.
const ID_PATTERN = /^[A-Za-z0-9._-]{1,64}$/;

function cleanText(value, max) {
  if (typeof value !== 'string') return '';
  const text = value.trim();
  return text.length > max ? text.slice(0, max) : text;
}

function toPort(value) {
  const port = typeof value === 'number' ? value : parseInt(value, 10);
  return Number.isInteger(port) && port >= MIN_PORT && port <= MAX_PORT ? port : 0;
}

/**
 * One reported row, or null when it carries no usable address. Refused rather
 * than repaired: a host with a space in it, or an id that a URL or a shell
 * would choke on, is a client bug and must not reach the published file.
 */
function normalizeEntry(raw) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null;

  const id = typeof raw.id === 'string' ? raw.id.trim() : '';
  if (!ID_PATTERN.test(id)) return null;

  const host = typeof raw.host === 'string' ? raw.host.trim() : '';
  if (!host || host.length > MAX_HOST || /\s/.test(host)) return null;

  const port = toPort(raw.port);
  if (port === 0) return null;

  return {
    id,
    name: cleanText(raw.name, MAX_NAME),
    host,
    port,
    updatedAt: cleanText(raw.updatedAt, 40),
  };
}

/**
 * The list with `entry` added or replaced. Replaced **at the index it already
 * had**, so the order of the file never changes; a new row goes last.
 *
 * `updatedAt` is kept from the previous row while host, port and name are the
 * same: the file is the address, not the heartbeat. `seenAt` is the heartbeat.
 */
function upsertReported(list, entry, nowIso) {
  const out = Array.isArray(list) ? list.slice() : [];
  if (!entry) return out;

  const at = out.findIndex((row) => row && row.id === entry.id);
  const previous = at >= 0 ? out[at] : null;
  const sameAddress = previous
    && previous.host === entry.host
    && previous.port === entry.port
    && (previous.name || '') === (entry.name || '');

  const row = {
    id: entry.id,
    name: entry.name || '',
    host: entry.host,
    port: entry.port,
    updatedAt: sameAddress
      ? (previous.updatedAt || nowIso)
      : (entry.updatedAt || nowIso),
    seenAt: nowIso,
  };

  if (at >= 0) out[at] = row;
  else out.push(row);
  return out;
}

/**
 * The rows nobody has reported within the TTL, and the ones still alive. A row
 * whose `seenAt` cannot be read is kept: a malformed timestamp is not evidence
 * that a server went away.
 */
function expire(list, ttlMs, nowMs) {
  const live = [];
  const dropped = [];
  for (const row of Array.isArray(list) ? list : []) {
    const at = Date.parse(row && row.seenAt ? row.seenAt : '');
    if (Number.isFinite(at) && nowMs - at > ttlMs) dropped.push(row.id);
    else live.push(row);
  }
  return { live, dropped };
}

/** A row as it is published: `seenAt` is ours, not the app's. */
function toPublished(row) {
  return {
    id: row.id,
    name: row.name || '',
    host: row.host,
    port: row.port,
    updatedAt: row.updatedAt || '',
  };
}

/** One row of the remote file, or null when it is not usable as it stands. */
function readRemoteRow(raw, fallbackId) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null;
  const id = cleanText(raw.id, MAX_ID) || fallbackId;
  const host = cleanText(raw.host, MAX_HOST);
  const port = toPort(raw.port);
  if (!id || !host || port === 0) return null;
  return {
    id,
    name: cleanText(raw.name, MAX_NAME),
    host,
    port,
    updatedAt: cleanText(raw.updatedAt, 40),
  };
}

/**
 * The registry to publish: the remote rows in their own order, this registry's
 * rows replacing or extending them, the expired ones gone.
 *
 * A remote file that is missing, unreadable or malformed is not an error: it is
 * a first publish, and the rows reported here are written on their own. Rows
 * nobody reported are never touched, so a file written by hand keeps whatever
 * it had.
 */
function mergeRegistry(remote, reported, dropped) {
  const base = remote && typeof remote === 'object' && !Array.isArray(remote) ? remote : {};
  const remoteRows = Array.isArray(base.servers) ? base.servers : [];

  const out = [];
  for (let i = 0; i < remoteRows.length; i++) {
    const row = readRemoteRow(remoteRows[i], 'server-' + (i + 1));
    if (row) out.push(row);
  }

  const reportedIds = new Set();
  for (const entry of Array.isArray(reported) ? reported : []) {
    if (!entry) continue;
    reportedIds.add(entry.id);
    const at = out.findIndex((row) => row.id === entry.id);
    if (at >= 0) out[at] = toPublished(entry);
    else out.push(toPublished(entry));
  }

  const droppedIds = new Set(Array.isArray(dropped) ? dropped : []);
  const kept = out.filter((row) => !(droppedIds.has(row.id) && !reportedIds.has(row.id)));

  const first = kept.length ? kept[0] : null;

  // The top of the file says when the list last changed, so it is derived from
  // the rows: a heartbeat that changes nothing must produce the same text.
  let newest = typeof base.updatedAt === 'string' ? base.updatedAt : '';
  for (const row of kept) {
    if (Date.parse(row.updatedAt || '') > Date.parse(newest || '')) newest = row.updatedAt;
  }

  return {
    updatedAt: newest,
    host: first ? first.host : '',
    port: first ? first.port : 0,
    tls: base.tls === true,
    fingerprint: typeof base.fingerprint === 'string' ? base.fingerprint : '',
    servers: kept,
  };
}

/** The registry as the text of `endpoint.json`. */
function serializeRegistry(registry) {
  return JSON.stringify(registry, null, 2) + '\n';
}

/**
 * The same content for people, in the shape `publish-endpoint.sh` has always
 * written. The file changed hands; it must not change look.
 */
function renderMarkdown(registry) {
  const servers = Array.isArray(registry.servers) ? registry.servers : [];
  const lines = [
    '# The servers',
    '',
    'The app reads `endpoint.json` and tries the addresses in order; this file is',
    'for people. Every server announces its own row, so none of them owns the file.',
    '',
    '- Updated: ' + (registry.updatedAt || ''),
  ];
  for (const server of servers) {
    lines.push('- ' + (server.name || server.id) + ' (`' + server.id + '`): `'
      + server.host + ':' + server.port + '`');
  }
  lines.push(
    '',
    'The public address changes whenever the tunnel is restarted, so it is written',
    'by the deployment and never by hand.',
    '');
  return lines.join('\n');
}

module.exports = {
  MAX_ID,
  MAX_NAME,
  MAX_HOST,
  MIN_PORT,
  MAX_PORT,
  ID_PATTERN,
  normalizeEntry,
  upsertReported,
  expire,
  toPublished,
  mergeRegistry,
  serializeRegistry,
  renderMarkdown,
};
