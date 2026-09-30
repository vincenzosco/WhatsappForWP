'use strict';

/**
 * UDP announcement of the adapter.
 *
 * The WP8 app has no way to know which address the computer is on, and typing
 * it by hand is the only manual step left. Here the adapter announces itself on
 * every physical interface every two seconds; the app listens and uses the
 * *sender's* address as the server address: the computer may have several
 * interfaces (Wi-Fi, Ethernet, Parallels), and only the one the packet came from
 * is reachable from the phone by definition.
 *
 * The payload is deliberately minimal and holds no secrets: hostname, port and
 * state.
 *
 * Note: this module knows nothing about WhatsApp and does not import `server.js`;
 * the beacon content arrives from `getPayload`, so it is testable on its own.
 */

const dgram = require('dgram');
const os = require('os');

const SERVICE_ID = 'whatsapp-wp8-adapter';
const SERVICE_VERSION = 1;
const DEFAULT_INTERVAL_MS = 2000;
const DEFAULT_NETMASK = '255.255.255.0';
const GLOBAL_BROADCAST = '255.255.255.255';

// Interfaces that lead nowhere for a phone on the same network: VPN, AirDrop,
// hotspot, virtual-machine bridges.
const VIRTUAL = /^(utun|awdl|llw|bridge|ap\d|gif|stf|xhci|anpi|vmenet|docker)/;

function ipv4ToInt(ip) {
  const parts = String(ip).split('.');
  if (parts.length !== 4) return null;
  let value = 0;
  for (const part of parts) {
    const octet = Number(part);
    if (!Number.isInteger(octet) || octet < 0 || octet > 255) return null;
    value = (value << 8) + octet;
  }
  return value >>> 0;
}

function intToIpv4(value) {
  return [24, 16, 8, 0].map((shift) => (value >>> shift) & 0xff).join('.');
}

/** Broadcast address (to every host) of the network of `address`. */
function ipv4Broadcast(address, netmask) {
  const host = ipv4ToInt(address);
  const mask = ipv4ToInt(netmask || DEFAULT_NETMASK);
  if (host === null || mask === null) return null;
  return intToIpv4(((host & mask) | (~mask >>> 0)) >>> 0);
}

/** Addresses to send the beacon to, one per physical interface. */
function broadcastTargets(interfaces) {
  const targets = [];
  let physical = false;
  for (const name of Object.keys(interfaces || {})) {
    if (VIRTUAL.test(name)) continue;
    for (const info of interfaces[name] || []) {
      if (info.family !== 'IPv4' || info.internal) continue;
      physical = true;
      const address = ipv4Broadcast(info.address, info.netmask);
      if (address && targets.indexOf(address) === -1) targets.push(address);
    }
  }
  // Last resort: some networks filter the local broadcast but accept this one.
  if (physical && targets.indexOf(GLOBAL_BROADCAST) === -1) targets.push(GLOBAL_BROADCAST);
  return targets;
}

/**
 * Beacon body. The six keys must stay identical to
 * WhatsappApp/Models/BeaconPayload.cs: DataContractJsonSerializer is
 * case-sensitive and a field that does not match stays at its default without
 * an error.
 */
function buildPayload(fields) {
  const source = fields || {};
  return {
    service: SERVICE_ID,
    version: SERVICE_VERSION,
    name: source.name || '',
    port: source.port,
    state: source.state || 'disconnected',
    account: source.account || '',
  };
}

function createDiscoveryBeacon(options) {
  const opts = options || {};
  const log = typeof opts.log === 'function' ? opts.log : () => {};
  const socket = typeof opts.socketFactory === 'function'
    ? opts.socketFactory()
    : dgram.createSocket({ type: 'udp4', reuseAddr: true });

  socket.on('error', (err) => log('WARN', `Discovery: ${err.message}`));

  function sendOnce() {
    const payload = Buffer.from(JSON.stringify(opts.getPayload() || {}));
    const interfaces = opts.interfaces || os.networkInterfaces();
    for (const target of broadcastTargets(interfaces)) {
      socket.send(payload, 0, payload.length, opts.port, target, (err) => {
        if (err) log('DEBUG', `Discovery: send to ${target} failed (${err.message})`);
      });
    }
  }

  // The first send waits for the bind: before the bind the socket does not have
  // SO_BROADCAST yet and the packet would be refused.
  socket.bind(() => {
    try {
      socket.setBroadcast(true);
    } catch (err) {
      log('WARN', `Discovery: broadcast could not be enabled (${err.message})`);
    }
    sendOnce();
  });

  const timer = setInterval(sendOnce, opts.intervalMs || DEFAULT_INTERVAL_MS);

  return {
    socket,
    sendOnce,
    stop() {
      clearInterval(timer);
      try {
        socket.close();
      } catch (err) {
        // already closed
      }
    },
  };
}

module.exports = {
  SERVICE_ID,
  SERVICE_VERSION,
  VIRTUAL,
  ipv4Broadcast,
  broadcastTargets,
  buildPayload,
  createDiscoveryBeacon,
};
