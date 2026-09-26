/**
 * tools/services.js
 *
 * Quali servizi compongono lo stack e come si avviano. Nessuno spawn qui:
 * questo modulo decide e descrive, `start-login.js` esegue.
 *
 * Aggiungere un servizio e' una voce in `SERVICE_DEFS`, non una modifica alla
 * logica di avvio. Un servizio `node` vive nel repo; un servizio `binary` e' un
 * eseguibile scaricato (come GOWA) e passa dalla stessa verifica del digest.
 */
'use strict';

const path = require('path');

const SERVICE_DIRS = {
  adapter: 'WhatsappBridge',
  calls: 'WhatsappCallServer'
};

const SERVICE_DEFS = [
  {
    name: 'adapter',
    kind: 'node',
    dir: SERVICE_DIRS.adapter,
    script: 'server.js',
    // L'adapter e' il pezzo che parla con l'app WP8: si spegne solo su richiesta.
    enabled: (options) => !options.noBridge
  },
  {
    name: 'calls',
    kind: 'node',
    dir: SERVICE_DIRS.calls,
    script: 'server.js',
    enabled: (options) => !options.noCalls
  }
];

/**
 * L'elenco ordinato dei servizi da avviare, piu' quelli saltati e il motivo.
 * `exists` arriva da fuori perche' i test non devono toccare il disco.
 */
function buildServiceList({ root, exists, options }) {
  const services = [];
  const skipped = [];

  for (const def of SERVICE_DEFS) {
    const dir = path.join(root, def.dir);
    if (!def.enabled(options || {})) continue;

    const script = def.script ? path.join(dir, def.script) : null;
    if (script && !exists(script)) {
      // Non e' un errore: il secondo server puo' non essere ancora stato
      // scritto, e lo stack principale deve partire lo stesso.
      skipped.push({ name: def.name, reason: `${def.dir}/${def.script} does not exist` });
      continue;
    }

    services.push({
      name: def.name,
      kind: def.kind,
      dir,
      script: def.script,
      required: def.name === 'adapter'
    });
  }

  return { services, skipped };
}

/** Le variabili d'ambiente di un servizio, gia' come stringhe. */
function serviceEnv(service, context) {
  const options = context.options || {};

  if (service.name === 'adapter') {
    return {
      GOWA_URL: context.gowaUrl || '',
      GOWA_DEVICE_ID: context.deviceId || '',
      GOWA_USER: options.user || '',
      GOWA_PASS: options.pass || '',
      BRIDGE_PORT: String(options.bridgePort),
      WEBHOOK_PORT: String(options.webhookPort),
      WEBHOOK_PUBLIC_URL: `http://127.0.0.1:${options.webhookPort}/webhook`
    };
  }

  if (service.name === 'calls') {
    return {
      CALLS_PORT: String(options.callsPort),
      GOWA_URL: context.gowaUrl || ''
    };
  }

  return {};
}

module.exports = { SERVICE_DIRS, SERVICE_DEFS, buildServiceList, serviceEnv };
