'use strict';

/**
 * Annuncio UDP dell'adapter.
 *
 * L'app WP8 non ha modo di sapere su quale indirizzo sta il computer, e
 * scriverlo a mano e' l'unico passo manuale rimasto. Qui l'adapter si annuncia
 * su ogni interfaccia fisica ogni due secondi; l'app ascolta e usa l'indirizzo
 * del *mittente* come indirizzo del server: il computer puo' avere piu'
 * interfacce (Wi-Fi, Ethernet, Parallels), e solo quella da cui e' arrivato il
 * pacchetto e' per definizione raggiungibile dal telefono.
 *
 * Il payload e' volutamente minimo e senza segreti: hostname, porta e stato.
 *
 * Nota: questo modulo non sa nulla di WhatsApp e non importa `server.js`; il
 * contenuto del beacon arriva da `getPayload`, cosi' e' testabile da solo.
 */

const dgram = require('dgram');
const os = require('os');

const SERVICE_ID = 'whatsapp-wp8-adapter';
const SERVICE_VERSION = 1;
const DEFAULT_INTERVAL_MS = 2000;
const DEFAULT_NETMASK = '255.255.255.0';
const GLOBAL_BROADCAST = '255.255.255.255';

// Interfacce che non portano da nessuna parte per un telefono della stessa rete:
// VPN, AirDrop, hotspot, bridge di macchine virtuali.
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

/** Indirizzo di broadcast (a tutti gli host) della rete di `address`. */
function ipv4Broadcast(address, netmask) {
  const host = ipv4ToInt(address);
  const mask = ipv4ToInt(netmask || DEFAULT_NETMASK);
  if (host === null || mask === null) return null;
  return intToIpv4(((host & mask) | (~mask >>> 0)) >>> 0);
}

/** Indirizzi su cui mandare il beacon, uno per interfaccia fisica. */
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
  // Ultima risorsa: alcune reti filtrano il broadcast locale ma accettano questo.
  if (physical && targets.indexOf(GLOBAL_BROADCAST) === -1) targets.push(GLOBAL_BROADCAST);
  return targets;
}

/**
 * Corpo del beacon. Le sei chiavi devono restare identiche a
 * WhatsappApp/Models/BeaconPayload.cs: DataContractJsonSerializer e'
 * case-sensitive e un campo che non combacia resta a default senza errori.
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

  // Il primo invio aspetta il bind: prima del bind il socket non ha ancora
  // SO_BROADCAST e il pacchetto verrebbe rifiutato.
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
        // gia' chiuso
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
