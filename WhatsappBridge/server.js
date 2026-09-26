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
const { collectCalls } = require('./calls');
const { collectChats } = require('./chats');

const LOG_TAGS = { INFO: '[INFO]', OK: '[OK]', WARN: '[WARN]', ERR: '[ERR]', MSG: '[MSG]', QR: '[QR]', NET: '[NET]' };

/**
 * Oltre questa lunghezza il prefisso di 4 byte non e' un payload, e' un
 * guasto (client disallineato o ostile). Deve restare uguale a
 * CommunicationService.MaxFrameLength nell'app WP8.1: le due parti parlano
 * dello stesso frame, quindi ne hanno lo stesso tetto.
 */
const MAX_FRAME_LENGTH = 8 * 1024 * 1024;

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
  // La scansione costa una richiesta HTTP per chat: si tiene il risultato per
  // un minuto, cosi' passare avanti e indietro tra le sezioni non la ripete.
  let callsCache = null;
  const CALLS_CACHE_MS = 60000;
  // Stessa regola delle chiamate: la scansione costa una richiesta per chat piu'
  // una per avatar, quindi il risultato si tiene per un minuto.
  let chatsCache = null;
  const CHATS_CACHE_MS = 60000;

  async function sendChats() {
    const limits = (config && config.chats) || {};

    if (state.status !== 'connected') {
      sendControl({ command: 'error', text: 'WhatsApp is not connected: the chat list is unavailable.' });
      sendControl({ command: 'chats.done' });
      return;
    }

    try {
      const fresh = !chatsCache || Date.now() - chatsCache.at > CHATS_CACHE_MS;
      if (fresh) {
        logger('INFO', `reading up to ${limits.limit || 25} conversation(s)...`);
        const rows = await collectChats({
          gowa,
          limit: limits.limit,
          avatars: limits.avatars,
          log: logger
        });
        chatsCache = { at: Date.now(), rows };
      }

      for (const row of chatsCache.rows) {
        sendControl({
          command: 'chat',
          chatId: row.chatId,
          senderName: row.name || undefined,
          text: row.preview || '',
          timestamp: row.timestamp || undefined,
          isGroup: row.isGroup,
          avatarData: row.avatar || undefined
        });
      }
    } catch (err) {
      logger('ERR', `chat list failed: ${err.message}`);
      sendControl({ command: 'error', text: `Chat list failed: ${err.message}` });
    } finally {
      sendControl({ command: 'chats.done' });
    }
  }

  async function sendCalls() {
    const limits = (config && config.calls) || {};

    if (state.status !== 'connected') {
      sendControl({ command: 'error', text: 'WhatsApp is not connected: call records are unavailable.' });
      sendControl({ command: 'calls.done' });
      return;
    }

    try {
      const fresh = !callsCache || Date.now() - callsCache.at > CALLS_CACHE_MS;
      if (fresh) {
        logger('INFO', `scanning up to ${limits.chatLimit || 25} chats for call records...`);
        const entries = await collectCalls({
          gowa,
          chatLimit: limits.chatLimit,
          messagesPerChat: limits.messagesPerChat,
          limit: limits.limit,
          log: logger
        });
        callsCache = { at: Date.now(), entries };
      }

      for (const call of callsCache.entries) {
        sendControl({
          command: 'call',
          chatId: call.chatId,
          senderName: call.chatName || undefined,
          timestamp: call.timestamp || undefined,
          callId: call.callId || undefined,
          callReason: call.reason || undefined,
          callDurationSeconds: call.durationSeconds,
          callIsVideo: call.isVideo
        });
      }
    } catch (err) {
      logger('ERR', `call scan failed: ${err.message}`);
      sendControl({ command: 'error', text: `Call scan failed: ${err.message}` });
    } finally {
      sendControl({ command: 'calls.done' });
    }
  }

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
        callsCache = null;
        chatsCache = null;
        if (changed) {
          broadcastState();
          logger('OK', `WhatsApp connected as ${state.jid || 'unknown'}`);
          await flushPending();
          await sendChats();
          await syncContacts();
        }
      } else if (changed) {
        broadcastState();
      }
    } catch (err) {
      dbg(`status unavailable: ${err.message}`);
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
      logger('QR', 'new QR code sent to the app');
    } catch (err) {
      logger('ERR', `QR login failed: ${err.message}`);
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
      logger('QR', `pairing code sent to the app for ${phone}`);
    } catch (err) {
      logger('ERR', `code login failed: ${err.message}`);
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
      logger('INFO', `synced ${contacts.length} contacts`);
    } catch (err) {
      logger('WARN', `contact sync failed: ${err.message}`);
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
      logger('MSG', `sent to ${msg.ChatId}: ${(msg.Text || '[media]').substring(0, 40)}`);
    } catch (err) {
      logger('ERR', `send to ${msg.ChatId} failed: ${err.message}`);
      sendControl({ command: 'error', chatId: msg.ChatId, text: `Send failed: ${err.message}` });
    }
  }

  async function flushPending() {
    if (pendingOutgoing.length === 0) return;
    const queued = pendingOutgoing.splice(0, pendingOutgoing.length);
    logger('INFO', `flushing ${queued.length} queued message(s)...`);
    for (const msg of queued) await sendOutgoing(msg);
  }

  async function handleUserMessage(msg) {
    if ((!msg.Text || !msg.Text.trim()) && !msg.MediaData) {
      logger('WARN', 'empty message from the app, ignored');
      return;
    }
    if (state.status !== 'connected') {
      pendingOutgoing.push(msg);
      logger('INFO', 'WhatsApp not ready: message queued');
      sendControl({ chatId: msg.ChatId, text: 'WhatsApp is not connected yet. The message will be sent automatically.' });
      return;
    }
    await sendOutgoing(msg);
  }

  // ─── Messaggi da WhatsApp verso l'app ─────────────────────────────────────

  async function handleWebhookEvent(event) {
    if (!event) return;

    if (event.event === 'message.revoked') {
      const payload = event.payload || {};
      const id = payload.revoked_message_id;
      if (!id) return;
      const chatId = payload.revoked_chat || payload.chat_id || payload.from || '0';
      logger('MSG', `message revoked on WhatsApp: ${id}`);
      sendControl({ command: 'revoked', chatId, relatedMessageId: id });
      return;
    }

    if (event.event === 'message.edited') {
      const payload = event.payload || {};
      const id = payload.original_message_id;
      if (!id || typeof payload.body !== 'string') return;
      const chatId = payload.chat_id || payload.from || '0';
      logger('MSG', `message edited on WhatsApp: ${id}`);
      sendControl({ command: 'edited', chatId, relatedMessageId: id, text: payload.body });
      return;
    }

    // message.reaction e i tipi futuri restano ignorati: nell'app non c'e'
    // dove mostrarli.
    if (event.event !== 'message') return;
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
        logger('WARN', `media not downloaded (${fields.mediaPath}): ${err.message}`);
      }
    }

    logger('MSG', `from ${fields.senderName}: ${(fields.text || '[media]').substring(0, 60)}`);
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
        logger('NET', `handshake from "${msg.SenderName || 'unknown'}"`);
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
      case 'calls':
        await sendCalls();
        break;
      case 'chats':
        await sendChats();
        break;
      case 'logout':
        try { await gowa.logout(); } catch (e) { /* ignora */ }
        state = { status: 'disconnected', jid: '' };
        qrCache = null;
        broadcastState();
        break;
      default:
        dbg(`unknown command: ${msg.Command}`);
    }
  }

  // ─── Server TCP ───────────────────────────────────────────────────────────

  const tcpServer = net.createServer((socket) => {
    const remote = `${socket.remoteAddress}:${socket.remotePort}`;
    logger('NET', `app client connected: ${remote}`);
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

        // Un client disallineato annuncia una lunghezza enorme: senza un tetto
        // il server resterebbe in attesa di gigabyte e il buffer crescerebbe
        // finche' il processo non cade. Zero e' l'altro caso degenere: un frame
        // vuoto farebbe girare il ciclo senza consumare niente.
        if (msgLen === 0 || msgLen > MAX_FRAME_LENGTH) {
          logger('ERR', `unacceptable frame from ${remote} (length ${msgLen}): closing the connection`);
          socket.destroy();
          return;
        }

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
          logger('ERR', `invalid frame from the app: ${err.message}`);
        }
      }
    });

    socket.on('close', () => { logger('NET', `app client disconnected: ${remote}`); wp8Clients.delete(socket); });
    socket.on('error', (err) => { logger('NET', `socket error [${remote}]: ${err.message}`); wp8Clients.delete(socket); });
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
    // Usato solo dai test: aggiunge un client finto alla lista dei destinatari.
    addClientForTest(socket) { wp8Clients.add(socket); },
    sendCalls,
    resetCallsCacheForTest() { callsCache = null; },
    sendChats,
    resetChatsCacheForTest() { chatsCache = null; },
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
  log('INFO', `Encryption:  ${cryptoHelper.ModeDescription} ${cryptoHelper.ENCRYPTION_ENABLED ? 'ON' : 'OFF'}`);

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
    log('OK', `GOWA device ready: ${deviceId || '(default)'}`);
    const registered = await gowa.setDeviceWebhook(config.webhook.publicUrl);
    log(registered ? 'OK' : 'WARN',
      registered
        ? `webhook registered with GOWA: ${config.webhook.publicUrl}`
        : `automatic webhook registration failed: start GOWA with --webhook=${config.webhook.publicUrl}`);
  } catch (err) {
    log('ERR', `GOWA not reachable at ${config.gowa.url}: ${err.message}`);
    log('ERR', 'start GOWA with: ./whatsapp rest --basic-auth=user:password');
  }

  bridge.tcpServer.listen(config.bridge.port, '0.0.0.0', () => {
    log('OK', `TCP server listening on port ${config.bridge.port}`);
    const addresses = [];
    const interfaces = os.networkInterfaces();
    Object.keys(interfaces).forEach((name) => {
      (interfaces[name] || []).forEach((iface) => {
        if (iface.family === 'IPv4' && !iface.internal) addresses.push(iface.address);
      });
    });
    log('INFO', `   connect the WP8 app to: ${addresses.join(', ') || '(no IP found)'}:${config.bridge.port}`);
  });

  webhookServer.listen(config.webhook.port, '0.0.0.0', () => {
    log('OK', `webhook listening on port ${config.webhook.port}${config.webhook.path}`);
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
    log('OK', `discovery beacon on UDP port ${config.discovery.port} (name: ${config.discovery.name})`);
  }

  bridge.tcpServer.on('error', (err) => {
    log('ERR', `TCP server error: ${err.message}`);
    if (err.code === 'EADDRINUSE') log('ERR', `port ${config.bridge.port} is already in use (set BRIDGE_PORT=...).`);
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
    log('OK', 'adapter stopped.');
    process.exit(0);
  };
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
}

if (require.main === module) {
  main().catch((err) => {
    console.error('FATAL ERROR:', err);
    process.exit(1);
  });
}

module.exports = { createBridge, main, makeLogger, MAX_FRAME_LENGTH };
