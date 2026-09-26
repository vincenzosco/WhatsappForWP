/**
 * ============================================================================
 *  WhatsApp Community Adapter v2.0
 * ============================================================================
 *  Sostituisce il vecchio bridge whatsapp-web.js.
 *
 *  Fa da ponte tra l'app Windows Phone 8.1 e un server GOWA self-hosted
 *  (github.com/vincenzosco/go-whatsapp-web-multidevice):
 *
 *   - TCP cifrato (AES-256-CBC + HMAC-SHA256) verso l'app WP8, protocollo
 *     invariato salvo il tag cifrario in testa al payload: l'app WP8.1 non
 *     implementa AES-GCM. L'adapter accetta anche i frame GCM e risponde a
 *     ciascun client con il cifrario del client.
 *   - HTTP verso l'API REST di GOWA (login QR / login con numero, stato,
 *     invio testo e immagini, contatti).
 *   - Server HTTP webhook che riceve da GOWA i messaggi in arrivo e li
 *     inoltra all'app WP8.
 *
 *  Protocollo di controllo (frame Type = 3, ChatId = "system"):
 *    app -> adapter : hello | status | login.qr | login.code | contacts | logout
 *    adapter -> app : state | qr | paircode | contact | error
 *
 *  Avvio:  npm install && npm start
 * ============================================================================
 */

'use strict';

const net = require('net');
const os = require('os');

const cryptoHelper = require('./crypto-helper');
const { loadConfig, applyDotEnv } = require('./config');
const { GowaClient } = require('./gowa-client');
const { buildChatMessage, mapWebhookMessage } = require('./message-format');
const { createWebhookServer } = require('./webhook-server');
const { createDiscoveryBeacon, buildPayload } = require('./discovery');

const LOG_TAGS = { INFO: '[INFO]', OK: '[OK]', WARN: '[WARN]', ERR: '[ERR]', MSG: '[MSG]', QR: '[QR]', NET: '[NET]' };

function makeLogger(enabled) {
  return function log(level, ...args) {
    if (level === 'DEBUG' && !enabled) return;
    const ts = new Date().toISOString().replace('T', ' ').substring(0, 19);
    console.log(`${ts} ${LOG_TAGS[level] || ''}`, ...args);
  };
}

function createBridge({ config, gowa, log, debug }) {
  const logger = typeof log === 'function' ? log : () => {};
  const dbg = typeof debug === 'function' ? debug : () => {};

  const wp8Clients = new Set();
  const pendingOutgoing = [];
  let state = { status: 'disconnected', jid: '' };
  let qrCache = null;

  // ─── Invio verso l'app WP8 ────────────────────────────────────────────────
  //
  // Ogni socket ricorda con quale cifrario il client ha scritto (wp8Cipher) e
  // riceve le risposte con lo stesso: un telefono WP8.1 non sa fare AES-GCM e
  // deve poter leggere tutto, un client capace di GCM non deve degradare.

  function frameFor(socket, jsonObject) {
    const tag = socket.wp8Cipher || cryptoHelper.DEFAULT_CIPHER_TAG;
    return cryptoHelper.buildFrame(JSON.stringify(jsonObject), tag);
  }

  function sendToClient(socket, msg) {
    try { socket.write(frameFor(socket, msg)); } catch (e) { /* socket morto */ }
  }

  function sendToClients(msg) {
    if (wp8Clients.size === 0) return;
    const json = JSON.stringify(msg);
    // Un frame per cifrario distinto, non uno per socket: i client CBC (in
    // pratica tutti) condividono lo stesso buffer.
    const packets = {};
    const dead = [];
    for (const socket of wp8Clients) {
      const tag = socket.wp8Cipher || cryptoHelper.DEFAULT_CIPHER_TAG;
      if (!packets[tag]) packets[tag] = cryptoHelper.buildFrame(json, tag);
      try { socket.write(packets[tag]); } catch (e) { dead.push(socket); }
    }
    for (const socket of dead) wp8Clients.delete(socket);
  }

  function broadcastState() {
    sendToClients(buildChatMessage({
      command: 'state',
      state: state.status,
      accountJid: state.jid || undefined,
      chatId: 'system',
      isIncoming: true
    }));
  }

  function sendControl(fields) {
    sendToClients(buildChatMessage(Object.assign({ chatId: 'system', isIncoming: true }, fields)));
  }

  // ─── Stato e login ────────────────────────────────────────────────────────

  async function refreshStatus() {
    try {
      const s = await gowa.status();
      const next = s.isLoggedIn ? 'connected' : (state.status === 'waiting' ? 'waiting' : 'disconnected');
      const changed = next !== state.status || (s.jid || '') !== state.jid;
      state = { status: next, jid: s.jid || '' };

      if (next === 'connected') {
        qrCache = null;
        if (changed) {
          broadcastState();
          logger('OK', `WhatsApp connesso come ${state.jid || 'sconosciuto'}`);
          await flushPending();
          await syncContacts();
        }
      } else if (changed) {
        broadcastState();
      }
    } catch (err) {
      dbg(`Stato non disponibile: ${err.message}`);
    }
  }

  async function requestQr() {
    if (state.status === 'connected') { broadcastState(); return; }
    try {
      const now = Date.now();
      if (qrCache && qrCache.expiresAt > now) {
        sendControl({ command: 'qr', qrImageData: qrCache.base64, qrDuration: qrCache.duration });
        return;
      }
      const { qrLink, duration } = await gowa.loginQr();
      const image = await gowa.fetchBinary(qrLink);
      const base64 = image.buffer.toString('base64');
      qrCache = { base64, duration, expiresAt: now + duration * 1000 };
      state = { status: 'waiting', jid: '' };
      // L'immagine va inviata prima dello stato, così l'app la mostra subito.
      sendControl({ command: 'qr', qrImageData: base64, qrDuration: duration });
      broadcastState();
      logger('QR', 'Nuovo QR code inviato all\'app');
    } catch (err) {
      logger('ERR', `Login QR fallito: ${err.message}`);
      sendControl({ command: 'error', text: `Login QR fallito: ${err.message}` });
    }
  }

  async function requestPairCode(phone) {
    if (!phone) {
      sendControl({ command: 'error', text: 'Numero di telefono mancante' });
      return;
    }
    if (state.status === 'connected') { broadcastState(); return; }
    try {
      const code = await gowa.loginWithCode(phone);
      state = { status: 'waiting', jid: '' };
      sendControl({ command: 'paircode', pairCode: code });
      broadcastState();
      logger('QR', `Codice di abbinamento inviato all'app per ${phone}`);
    } catch (err) {
      logger('ERR', `Login con codice fallito: ${err.message}`);
      sendControl({ command: 'error', text: `Login con codice fallito: ${err.message}` });
    }
  }

  async function syncContacts() {
    try {
      const contacts = await gowa.contacts();
      for (const contact of contacts) {
        if (!contact.jid) continue;
        sendControl({ command: 'contact', chatId: contact.jid, senderName: contact.name || undefined });
      }
      logger('INFO', `Sincronizzati ${contacts.length} contatti`);
    } catch (err) {
      logger('WARN', `Sincronizzazione contatti fallita: ${err.message}`);
    }
  }

  // ─── Messaggi dall'app verso WhatsApp ─────────────────────────────────────

  async function sendOutgoing(msg) {
    const hasMedia = !!msg.MediaData;
    try {
      if (hasMedia) {
        const buffer = Buffer.from(msg.MediaData, 'base64');
        await gowa.sendImage(msg.ChatId, msg.Text || '', buffer, msg.MediaMimeType || 'image/jpeg', msg.MediaFileName);
      } else if (msg.Text && msg.Text.trim()) {
        await gowa.sendText(msg.ChatId, msg.Text);
      }
      logger('MSG', `Inviato a ${msg.ChatId}: ${(msg.Text || '[media]').substring(0, 40)}`);
    } catch (err) {
      logger('ERR', `Invio a ${msg.ChatId} fallito: ${err.message}`);
      sendControl({ command: 'error', chatId: msg.ChatId, text: `Invio non riuscito: ${err.message}` });
    }
  }

  async function flushPending() {
    if (pendingOutgoing.length === 0) return;
    const queued = pendingOutgoing.splice(0, pendingOutgoing.length);
    logger('INFO', `Invio ${queued.length} messaggi in coda...`);
    for (const msg of queued) await sendOutgoing(msg);
  }

  async function handleUserMessage(msg) {
    if ((!msg.Text || !msg.Text.trim()) && !msg.MediaData) {
      logger('WARN', 'Messaggio WP8 senza contenuto, ignorato');
      return;
    }
    if (state.status !== 'connected') {
      pendingOutgoing.push(msg);
      logger('INFO', 'WhatsApp non pronto: messaggio messo in coda');
      sendControl({ chatId: msg.ChatId, text: 'WhatsApp non ancora connesso. Il messaggio verrà inviato automaticamente.' });
      return;
    }
    await sendOutgoing(msg);
  }

  // ─── Messaggi da WhatsApp verso l'app ─────────────────────────────────────

  async function handleWebhookEvent(event) {
    if (!event || event.event !== 'message') return;
    const fields = mapWebhookMessage(event.payload || {});
    if (!fields) return;

    let mediaData = null;
    let mediaMimeType = fields.mediaMimeType;
    if (fields.mediaPath) {
      try {
        const media = await gowa.fetchBinary(fields.mediaPath);
        mediaData = media.buffer.toString('base64');
        if (!mediaMimeType) mediaMimeType = media.contentType;
      } catch (err) {
        logger('WARN', `Media non scaricato (${fields.mediaPath}): ${err.message}`);
      }
    }

    logger('MSG', `Da ${fields.senderName}: ${(fields.text || '[media]').substring(0, 60)}`);
    sendToClients(buildChatMessage({
      id: fields.id,
      text: fields.text,
      senderId: fields.senderId,
      senderName: fields.senderName,
      chatId: fields.chatId,
      timestamp: fields.timestamp,
      status: 3,
      type: fields.type,
      isIncoming: true,
      mediaData,
      mediaMimeType,
      mediaFileName: fields.mediaFileName
    }));
  }

  // ─── Protocollo di controllo ──────────────────────────────────────────────

  async function handleControl(msg) {
    switch (msg.Command) {
      case 'hello':
        logger('NET', `Handshake da "${msg.SenderName || 'Sconosciuto'}"`);
        broadcastState();
        break;
      case 'status':
        broadcastState();
        break;
      case 'login.qr':
        await requestQr();
        break;
      case 'login.code':
        await requestPairCode((msg.Text || '').trim());
        break;
      case 'contacts':
        if (state.status === 'connected') await syncContacts();
        break;
      case 'logout':
        try { await gowa.logout(); } catch (e) { /* ignora */ }
        state = { status: 'disconnected', jid: '' };
        qrCache = null;
        broadcastState();
        break;
      default:
        dbg(`Comando sconosciuto: ${msg.Command}`);
    }
  }

  // ─── Server TCP ───────────────────────────────────────────────────────────

  const tcpServer = net.createServer((socket) => {
    const remote = `${socket.remoteAddress}:${socket.remotePort}`;
    logger('NET', `Client WP8 connesso: ${remote}`);
    wp8Clients.add(socket);

    // Finche' il client non scrive non sappiamo cosa sa leggere: si parte dal
    // cifrario che tutti leggono.
    socket.wp8Cipher = cryptoHelper.DEFAULT_CIPHER_TAG;

    // Stato immediato al collegamento.
    sendToClient(socket, buildChatMessage({
      command: 'state',
      state: state.status,
      accountJid: state.jid || undefined,
      chatId: 'system',
      isIncoming: true
    }));

    let buffer = Buffer.alloc(0);
    socket.on('data', (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      while (buffer.length >= 4) {
        const msgLen = buffer.readUInt32LE(0);
        if (buffer.length < 4 + msgLen) break;
        const payload = buffer.slice(4, 4 + msgLen);
        buffer = buffer.slice(4 + msgLen);
        try {
          // Il tag del frame appena arrivato dice con che cifrario e' stato
          // scritto: da qui in poi gli si risponde con lo stesso.
          const tag = cryptoHelper.cipherTagOf(payload);
          if (tag) socket.wp8Cipher = tag;

          const msg = JSON.parse(cryptoHelper.decodePayload(payload));
          if (msg.Type === 3) handleControl(msg).catch((e) => logger('ERR', e.message));
          else handleUserMessage(msg).catch((e) => logger('ERR', e.message));
        } catch (err) {
          logger('ERR', `Frame non valido da WP8: ${err.message}`);
        }
      }
    });

    socket.on('close', () => { logger('NET', `Client WP8 disconnesso: ${remote}`); wp8Clients.delete(socket); });
    socket.on('error', (err) => { logger('NET', `Errore socket [${remote}]: ${err.message}`); wp8Clients.delete(socket); });
  });

  return {
    tcpServer,
    getState: () => state,
    refreshStatus,
    handleWebhookEvent,
    handleControl,
    syncContacts,
    // Usato solo dai test: forza lo stato "connected" senza passare da GOWA.
    setConnectedForTest() { state = { status: 'connected', jid: '39@s.whatsapp.net' }; },
    stop() { /* il timer di polling è gestito da main() */ }
  };
}

// ─── Avvio ──────────────────────────────────────────────────────────────────

async function main() {
  const debug = process.argv.includes('--debug');
  const log = makeLogger(debug);
  const dbg = (...args) => { if (debug) console.log('  [DEBUG]', ...args); };

  const config = loadConfig(applyDotEnv(process.env));
  log('INFO', 'WhatsApp Community Adapter v2.0 (GOWA)');
  log('INFO', `GOWA:        ${config.gowa.url}`);
  log('INFO', `Device GOWA: ${config.gowa.deviceId || '(default)'}`);
  log('INFO', `TCP app:     ${config.bridge.port}`);
  log('INFO', `Webhook:     ${config.webhook.publicUrl}`);
  log('INFO', `Cifratura:   ${cryptoHelper.ModeDescription} ${cryptoHelper.ENCRYPTION_ENABLED ? 'ATTIVA' : 'DISATTIVATA'}`);

  const gowa = new GowaClient({
    baseUrl: config.gowa.url,
    deviceId: config.gowa.deviceId,
    user: config.gowa.user,
    pass: config.gowa.pass
  });

  const bridge = createBridge({ config, gowa, log, debug: dbg });

  const webhookServer = createWebhookServer({
    path: config.webhook.path,
    secret: config.webhook.secret,
    log,
    onEvent: (event) => bridge.handleWebhookEvent(event)
  });

  try {
    const deviceId = await gowa.ensureDevice();
    log('OK', `Device GOWA pronto: ${deviceId || '(default)'}`);
    const registered = await gowa.setDeviceWebhook(config.webhook.publicUrl);
    log(registered ? 'OK' : 'WARN',
      registered
        ? `Webhook registrato su GOWA: ${config.webhook.publicUrl}`
        : `Registrazione webhook automatica non riuscita: avvia GOWA con --webhook=${config.webhook.publicUrl}`);
  } catch (err) {
    log('ERR', `GOWA non raggiungibile su ${config.gowa.url}: ${err.message}`);
    log('ERR', 'Avvia GOWA con: ./whatsapp rest --basic-auth=utente:password');
  }

  bridge.tcpServer.listen(config.bridge.port, '0.0.0.0', () => {
    log('OK', `Server TCP in ascolto sulla porta ${config.bridge.port}`);
    const addresses = [];
    const interfaces = os.networkInterfaces();
    Object.keys(interfaces).forEach((name) => {
      (interfaces[name] || []).forEach((iface) => {
        if (iface.family === 'IPv4' && !iface.internal) addresses.push(iface.address);
      });
    });
    log('INFO', `   Connetti l'app WP8 a: ${addresses.join(', ') || '(IP non trovato)'}:${config.bridge.port}`);
  });

  webhookServer.listen(config.webhook.port, '0.0.0.0', () => {
    log('OK', `Webhook in ascolto sulla porta ${config.webhook.port}${config.webhook.path}`);
  });

  // Il corpo del beacon si costruisce con l'unico builder del modulo di
  // discovery: sei chiavi, le stesse che l'app legge in BeaconPayload.cs.
  let beacon = null;
  if (config.discovery.enabled) {
    beacon = createDiscoveryBeacon({
      port: config.discovery.port,
      getPayload: () => {
        const current = bridge.getState();
        return buildPayload({
          name: config.discovery.name,
          port: config.bridge.port,
          state: current.status,
          account: current.jid
        });
      },
      log
    });
    log('OK', `Discovery attivo sulla porta UDP ${config.discovery.port} (nome: ${config.discovery.name})`);
  }

  bridge.tcpServer.on('error', (err) => {
    log('ERR', `Errore server TCP: ${err.message}`);
    if (err.code === 'EADDRINUSE') log('ERR', `Porta ${config.bridge.port} già in uso (usa BRIDGE_PORT=...).`);
    process.exit(1);
  });

  await bridge.refreshStatus();
  const timer = setInterval(() => bridge.refreshStatus(), config.pollIntervalMs);

  const shutdown = () => {
    clearInterval(timer);
    if (beacon) beacon.stop();
    bridge.stop();
    try { webhookServer.close(); } catch (e) { /* ignora */ }
    try { bridge.tcpServer.close(); } catch (e) { /* ignora */ }
    log('OK', 'Adapter arrestato.');
    process.exit(0);
  };
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
}

if (require.main === module) {
  main().catch((err) => {
    console.error('ERRORE FATALE:', err);
    process.exit(1);
  });
}

module.exports = { createBridge, main, makeLogger };
