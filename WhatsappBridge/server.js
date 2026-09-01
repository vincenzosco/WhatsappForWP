/**
 * ============================================================================
 *  WhatsApp Community Unified Server v1.1
 * ============================================================================
 *  Single server that combines the two original components:
 *
 *   1. WhatsappServer (.NET relay)  -> relays messages between connected
 *      Windows Phone 8.1 clients, so phones can chat with each other.
 *   2. WhatsappBridge (Node.js)     -> connects the WP8 app to real WhatsApp
 *      servers via the whatsapp-web.js library.
 *
 *  Routing of messages coming from the WP8 app:
 *   - Chat IDs mapped to a WhatsApp contact ("wa_...") are sent to WhatsApp.
 *   - Any other chat ID is relayed to the other connected WP8 clients.
 *
 *  DISCLAIMER: This is an unofficial bridge. Using it violates WhatsApp's
 *     Terms of Service. Your account may be permanently banned. Use at your
 *     own risk and only with test/secondary phone numbers.
 *
 *  Protocol: TCP with length-prefixed frames (same as WP8 app)
 *  ┌─────────────────────────────────────────────┐
 *  │  4 bytes (UInt32 LE) = payload length       │
 *  │  N bytes (UTF-8)     = encrypted payload    │
 *  └─────────────────────────────────────────────┘
 *
 *  The payload is AES-256-GCM encrypted (12-byte random IV + ciphertext with
 *  a 16-byte auth tag) using a pre-shared key. See crypto-helper.js.
 *
 *  ChatMessage JSON uses DataContractJsonSerializer format:
 *  - Timestamps: "\/Date(epochMs)\/" (not ISO 8601!)
 *  - Type 3 = System (ignored by WP8 DataService, used for status)
 *
 *  Run:
 *    npm install
 *    npm start
 * ============================================================================
 */

const net = require('net');
const path = require('path');
const fs = require('fs');
const cryptoHelper = require('./crypto-helper');
const { Client, LocalAuth, MessageMedia } = require('whatsapp-web.js');

// ─── Configuration ──────────────────────────────────────────────────────────

const TCP_PORT = parseInt(process.env.BRIDGE_PORT, 10) || 8585;
const SESSION_DIR = path.join(__dirname, '.wwebjs_auth');
const DEBUG = process.argv.includes('--debug');

// Connected WP8 clients
const wp8Clients = new Set();

// Chat ID mapping: WhatsApp remote ID ↔ WP8 internal ID
// WhatsApp uses "393401234567@c.us" format
// WP8 app uses "wa_393401234567" format
const whatsappToChatId = new Map();
const chatIdToWhatsapp = new Map();

// Pending messages (sent before WhatsApp was ready)
const pendingMessages = [];

let whatsappClient = null;
let isWhatsAppReady = false;
let qrCodeData = null;

// ─── Logging ────────────────────────────────────────────────────────────────

function log(level, ...args) {
  const ts = new Date().toISOString().replace('T', ' ').substring(0, 19);
  const tags = { INFO: '[INFO]', OK: '[OK]', WARN: '[WARN]', ERR: '[ERR]', MSG: '[MSG]', QR: '[QR]', NET: '[NET]' };
  console.log(`${ts} ${tags[level] || ''}`, ...args);
}

function debug(...args) { if (DEBUG) console.log('  [DEBUG]', ...args); }

// ─── Date Format ────────────────────────────────────────────────────────────
// CRITICAL: WP8 uses DataContractJsonSerializer which ONLY understands
// the Microsoft "\/Date(epochMs)\/" format, NOT ISO 8601.
function formatDateForWp8(date) {
  const d = date || new Date();
  const epoch = d.getTime(); // milliseconds since Unix epoch
  return `\\/Date(${epoch})\\\\`;
}

// ─── TCP Protocol ───────────────────────────────────────────────────────────
// Protocol: [4-byte UInt32 LE message length][UTF-8 JSON body]

function sendToWp8Clients(messageJson) {
  if (wp8Clients.size === 0) {
    debug('Nessun client WP8 connesso, messaggio non inviato');
    return;
  }

  const jsonStr = JSON.stringify(messageJson);
  const packet = cryptoHelper.buildFrame(jsonStr);

  const deadSockets = [];
  for (const sock of wp8Clients) {
    try {
      sock.write(packet);
    } catch (err) {
      deadSockets.push(sock);
    }
  }

  // Clean up dead sockets
  for (const dead of deadSockets) {
    wp8Clients.delete(dead);
  }

  debug(`Inviato a ${wp8Clients.size} client(i) WP8`);
}

function sendToClient(socket, msg) {
  const jsonStr = JSON.stringify(msg);
  try { socket.write(cryptoHelper.buildFrame(jsonStr)); } catch (e) { /* ignore */ }
}

/**
 * Relays a raw message (exact bytes) from one WP8 client to all the other
 * connected WP8 clients. This is the classic WhatsappServer (.NET) relay
 * behavior, now merged into the unified server.
 */
function relayToWp8Clients(jsonStr, senderSocket) {
  let relayed = 0;
  const deadSockets = [];
  for (const sock of wp8Clients) {
    if (sock === senderSocket) continue;
    try {
      // Each recipient gets its own freshly encrypted frame (new random IV)
      sock.write(cryptoHelper.buildFrame(jsonStr));
      relayed++;
    } catch (err) {
      deadSockets.push(sock);
    }
  }
  for (const dead of deadSockets) wp8Clients.delete(dead);

  if (relayed > 0) {
    debug(`Inoltrato a ${relayed} client(i) WP8 (relay locale)`);
  }
}

// ─── ChatMessage Builder ────────────────────────────────────────────────────
// Builds a JSON object compatible with the WP8 ChatMessage model.
// The field names MUST match exactly (DataContractJsonSerializer is case-sensitive).

function buildChatMessage({ id, text, senderId, senderName, chatId, timestamp, status, type, isIncoming, mediaData, mediaMimeType, mediaFileName }) {
  const msg = {
    Id: id || `msg_${Date.now()}_${Math.random().toString(36).substring(2, 8)}`,
    Text: text || '',
    SenderId: senderId || 'unknown',
    SenderName: senderName || 'Sconosciuto',
    ChatId: chatId || '0',
    Timestamp: formatDateForWp8(timestamp),  // MUST be "\/Date()\/" format!
    Status: typeof status === 'number' ? status : 1,
    Type: typeof type === 'number' ? type : 0,
    IsIncoming: typeof isIncoming === 'boolean' ? isIncoming : true
  };

  // Media fields (only included if media is present)
  if (mediaData) {
    msg.MediaData = mediaData;
    msg.MediaMimeType = mediaMimeType || 'image/jpeg';
    if (mediaFileName) msg.MediaFileName = mediaFileName;
  }

  return msg;
}

/**
 * Maps a WhatsApp contact ID (e.g., "393401234567@c.us") to a WP8-friendly
 * chat ID (e.g., "wa_393401234567"). Creates a new mapping if needed.
 */
function getChatIdForWhatsApp(waContactId) {
  if (!waContactId) return '0';
  if (whatsappToChatId.has(waContactId)) {
    return whatsappToChatId.get(waContactId);
  }
  // Extract digits and create a short ID
  const digits = waContactId.replace(/[^0-9]/g, '').slice(-12);
  const chatId = `wa_${digits}`;
  whatsappToChatId.set(waContactId, chatId);
  chatIdToWhatsapp.set(chatId, waContactId);
  log('INFO', `Mappato contatto: ${waContactId} <-> ${chatId}`);
  return chatId;
}

/**
 * Gets the display name for a WhatsApp contact.
 */
async function getContactName(waContactId) {
  try {
    const contact = await whatsappClient.getContactById(waContactId);
    if (contact) {
      return contact.name || contact.pushname || contact.shortName || contact.number || waContactId.split('@')[0];
    }
  } catch (err) {
    debug(`Nome non trovato per ${waContactId}: ${err.message}`);
  }
  // Fallback: format the phone number nicely
  const number = waContactId.split('@')[0].replace(/(\d{3})(\d{3})(\d{4})/, '+$1 $2 $3');
  return number || waContactId;
}

// ─── TCP Server ─────────────────────────────────────────────────────────────

function startTcpServer() {
  const server = net.createServer((socket) => {
    const remoteAddr = `${socket.remoteAddress}:${socket.remotePort}`;
    log('NET', `Nuovo client WP8 connesso: ${remoteAddr}`);
    wp8Clients.add(socket);

    // Send initial connection status
    const statusText = isWhatsAppReady
      ? 'Connesso a WhatsApp! Pronto per inviare e ricevere messaggi.'
      : qrCodeData
        ? 'Scansiona il QR code con WhatsApp > Dispositivi collegati > Collega dispositivo.'
        : 'Avvio client WhatsApp in corso...';

    sendToClient(socket, buildChatMessage({
      text: statusText,
      chatId: 'system',
      type: 3,
      isIncoming: true
    }));

    // Buffer for incoming TCP data (stream reassembly)
    let dataBuffer = Buffer.alloc(0);

    socket.on('data', (chunk) => {
      dataBuffer = Buffer.concat([dataBuffer, chunk]);

      while (dataBuffer.length >= 4) {
        const msgLen = dataBuffer.readUInt32LE(0);
        const totalLen = 4 + msgLen;

        if (dataBuffer.length < totalLen) break; // Wait for more data

        const payload = dataBuffer.slice(4, totalLen);
        dataBuffer = dataBuffer.slice(totalLen);

        try {
          const jsonStr = cryptoHelper.decodePayload(payload);
          const msg = JSON.parse(jsonStr);

          // Skip handshake messages (Type = System, ChatId = system)
          if (msg.Type === 3 && msg.ChatId === 'system') {
            log('NET', `Handshake ricevuto da "${msg.SenderName || 'Sconosciuto'}"`);
            // Send current status after handshake
            sendToClient(socket, buildChatMessage({
              text: isWhatsAppReady
                ? 'Connesso a WhatsApp! Invia un messaggio per iniziare.'
                : 'WhatsApp non ancora connesso. Scannerizza il QR code quando appare.',
              chatId: 'system',
              type: 3,
              isIncoming: true
            }));
            continue;
          }

          handleMessageFromWp8(msg, jsonStr, socket);
        } catch (err) {
          log('ERR', `Errore parsing JSON dal client WP8: ${err.message}`);
        }
      }
    });

    socket.on('close', () => {
      log('NET', `Client WP8 disconnesso: ${remoteAddr}`);
      wp8Clients.delete(socket);
    });

    socket.on('error', (err) => {
      log('NET', `Errore socket [${remoteAddr}]: ${err.message}`);
      wp8Clients.delete(socket);
    });
  });

  server.listen(TCP_PORT, '0.0.0.0', () => {
    log('OK', `Server TCP in ascolto sulla porta ${TCP_PORT}`);
    const interfaces = require('os').networkInterfaces();
    const addresses = [];
    Object.keys(interfaces).forEach(name => {
      (interfaces[name] || []).forEach(iface => {
        if (iface.family === 'IPv4' && !iface.internal) addresses.push(iface.address);
      });
    });
    log('INFO', `   IP del server: ${addresses.join(', ') || '(non trovato)'}`);
    log('INFO', `   Connetti l'app WP8 a uno di questi IP:${TCP_PORT}`);
  });

  server.on('error', (err) => {
    log('ERR', `Errore server TCP: ${err.message}`);
    if (err.code === 'EADDRINUSE') {
      log('ERR', `La porta ${TCP_PORT} è già in uso. Cambiala con BRIDGE_PORT=8586`);
    }
    process.exit(1);
  });

  return server;
}

// ─── WhatsApp Web Client ────────────────────────────────────────────────────

async function startWhatsAppClient() {
  log('INFO', 'Avvio client WhatsApp Web...');

  if (!fs.existsSync(SESSION_DIR)) {
    fs.mkdirSync(SESSION_DIR, { recursive: true });
  }

  // Check for existing session
  const sessionFiles = fs.readdirSync(SESSION_DIR).filter(f => f.includes('session'));
  if (sessionFiles.length > 0) {
    log('INFO', `Trovata sessione salvata (${sessionFiles.length} file). Non serve scannerizzare di nuovo.`);
  } else {
    log('INFO', 'Nessuna sessione trovata. Scannerizza il QR code al primo avvio.');
  }

  whatsappClient = new Client({
    authStrategy: new LocalAuth({ dataPath: SESSION_DIR }),
    puppeteer: {
      headless: true,
      args: [
        '--no-sandbox',
        '--disable-setuid-sandbox',
        '--disable-dev-shm-usage',
        '--disable-accelerated-2d-canvas',
        '--no-first-run',
        '--no-zygote',
        '--single-process',
        '--disable-gpu'
      ]
    }
  });

  // ── QR Code Event ──
  whatsappClient.on('qr', (qr) => {
    qrCodeData = qr;
    log('QR', 'NUOVO QR CODE - Scansiona con WhatsApp!');

    // Display QR in terminal
    console.log('');
    console.log('╔══════════════════════════════════════════════════════════╗');
    console.log('║  SCANSIONA IL QR CODE CON WHATSAPP:                    ║');
    console.log('║  WhatsApp > ... > Dispositivi collegati                 ║');
    console.log('╚══════════════════════════════════════════════════════════╝');
    try {
      const qrcode = require('qrcode-terminal');
      qrcode.generate(qr, { small: true });
    } catch (err) {
      log('QR', `QR URL: https://api.qrserver.com/v1/create-qr-code/?size=300x300&data=${encodeURIComponent(qr)}`);
    }
    console.log('');

    // Notify WP8 clients
    sendToWp8Clients(buildChatMessage({
      text: 'QR Code pronto! Apri WhatsApp sul telefono > Dispositivi collegati > Collega dispositivo.\n\nInquadra il QR che appare nel terminale del server.',
      chatId: 'system',
      type: 3,
      isIncoming: true
    }));
  });

  // ── Ready Event ──
  whatsappClient.on('ready', () => {
    isWhatsAppReady = true;
    qrCodeData = null;
    log('OK', 'CLIENT WHATSAPP PRONTO!');

    sendToWp8Clients(buildChatMessage({
      text: 'Connesso a WhatsApp! Ora puoi inviare e ricevere messaggi in tempo reale.',
      chatId: 'system',
      type: 3,
      isIncoming: true
    }));

    // Send any queued messages
    if (pendingMessages.length > 0) {
      log('INFO', `Invio ${pendingMessages.length} messaggi in coda...`);
      const queued = [...pendingMessages];
      pendingMessages.length = 0;
      for (const qMsg of queued) {
        sendMessageToWhatsApp(qMsg.chatId, qMsg.text, qMsg.originalMsg).catch(err =>
          log('ERR', `Errore invio messaggio in coda: ${err.message}`)
        );
      }
    }
  });

  // ── Authentication Events ──
  whatsappClient.on('authenticated', () => log('OK', 'Autenticazione WhatsApp completata!'));

  whatsappClient.on('auth_failure', (err) => {
    log('ERR', `Autenticazione WhatsApp fallita: ${err.message}`);
    sendToWp8Clients(buildChatMessage({
      text: `Autenticazione WhatsApp fallita: ${err.message}\nRiavvia il server per riprovare.`,
      chatId: 'system',
      type: 3,
      isIncoming: true
    }));
  });

  // ── Incoming Message Event ──
  whatsappClient.on('message', async (msg) => {
    try {
      // Skip status broadcasts and own messages (already displayed optimistically)
      if (msg.isStatus || msg.fromMe) return;

      const chatId = getChatIdForWhatsApp(msg.from);
      const contactName = await getContactName(msg.from);
      const timestamp = new Date((msg.timestamp || Math.floor(Date.now()/1000)) * 1000);

      if (msg.hasMedia) {
        log('MSG', `Da ${contactName}: ${msg.body ? msg.body.substring(0, 40) : '(media)'} [${msg.type}]`);

        // Download media data
        const media = await msg.downloadMedia().catch(err => {
          log('ERR', `Errore download media da ${contactName}: ${err.message}`);
          return null;
        });

        if (media) {
          // Determine message type based on mime type
          let msgType = 0; // Text (default)
          if (media.mimetype?.startsWith('image/')) msgType = 1; // Image
          else if (media.mimetype?.startsWith('audio/')) msgType = 2; // Audio

          const mediaFileName = msg.filename || `${Date.now()}.${media.mimetype?.split('/')[1] || 'bin'}`;

          log('MSG', `   Media: ${media.mimetype} (${(media.data.length * 0.75).toFixed(0)} bytes)`);

          sendToWp8Clients(buildChatMessage({
            text: msg.body || '',  // caption text
            senderId: msg.from,
            senderName: contactName,
            chatId: chatId,
            timestamp: timestamp,
            status: 3,     // Read
            type: msgType,  // Image (1) or Audio (2)
            isIncoming: true,
            mediaData: media.data,  // base64 data from whatsapp-web.js
            mediaMimeType: media.mimetype,
            mediaFileName: mediaFileName
          }));
        } else {
          // Media download failed — send as text placeholder
          sendToWp8Clients(buildChatMessage({
            text: msg.body || '[Media non supportato]',
            senderId: msg.from,
            senderName: contactName,
            chatId: chatId,
            timestamp: timestamp,
            status: 3,
            type: 0,  // Text
            isIncoming: true
          }));
        }
      } else if (msg.body) {
        log('MSG', `Da ${contactName}: ${msg.body.substring(0, 60)}`);

        sendToWp8Clients(buildChatMessage({
          text: msg.body,
          senderId: msg.from,
          senderName: contactName,
          chatId: chatId,
          timestamp: timestamp,
          status: 3,    // Read
          type: 0,      // Text
          isIncoming: true
        }));
      }
    } catch (err) {
      log('ERR', `Errore elaborazione messaggio in arrivo: ${err.message}`);
    }
  });

  // ── Disconnected Event ──
  whatsappClient.on('disconnected', (reason) => {
    isWhatsAppReady = false;
    log('WARN', `Client WhatsApp disconnesso: ${reason}`);
    log('WARN', '   Il client tenterà di riconnettersi automaticamente.');
    sendToWp8Clients(buildChatMessage({
      text: `Disconnesso da WhatsApp: ${reason}\nRiconnessione automatica in corso...`,
      chatId: 'system',
      type: 3,
      isIncoming: true
    }));
  });

  // Initialize
  try {
    await whatsappClient.initialize();
  } catch (err) {
    log('ERR', `Errore inizializzazione WhatsApp: ${err.message}`);
    log('ERR', '   Verifica che Google Chrome/Chromium sia installato.');
    sendToWp8Clients(buildChatMessage({
      text: `Errore inizializzazione WhatsApp: ${err.message}\nVerifica che Chrome sia installato.`,
      chatId: 'system',
      type: 3,
      isIncoming: true
    }));
  }
}

// ─── Send Message to WhatsApp ───────────────────────────────────────────────

async function sendMessageToWhatsApp(waContactId, text, originalMsg) {
  const hasMedia = originalMsg && originalMsg.MediaData && originalMsg.MediaMimeType;

  if (!text && !hasMedia) return;

  try {
    if (hasMedia) {
      // Send media message (image, audio, etc.)
      const media = new MessageMedia(
        originalMsg.MediaMimeType,
        originalMsg.MediaData,
        originalMsg.MediaFileName || undefined
      );

      // If there's caption text, send media with caption
      if (text && text.trim()) {
        await whatsappClient.sendMessage(waContactId, media, { caption: text });
        log('MSG', `Inviata immagine con didascalia a ${waContactId}`);
      } else {
        await whatsappClient.sendMessage(waContactId, media);
        log('MSG', `Inviata immagine a ${waContactId}`);
      }
    } else if (text && text.trim()) {
      // Send text message
      await whatsappClient.sendMessage(waContactId, text);
      log('MSG', `Inviato a ${waContactId}: ${text.substring(0, 40)}`);
    }
  } catch (err) {
    log('ERR', `Errore invio a ${waContactId}: ${err.message}`);
    // Only notify WP8 if the original message wasn't queued
    if (isWhatsAppReady) {
      sendToWp8Clients(buildChatMessage({
        text: `Errore invio: ${err.message}`,
        chatId: originalMsg?.ChatId || 'system',
        type: 3,
        isIncoming: true
      }));
    }
  }
}

// ─── Handle Messages from WP8 App ──────────────────────────────────────────

async function handleMessageFromWp8(msg, jsonStr, senderSocket) {
  // Validate message has text OR media
  const hasMedia = msg.MediaData && msg.MediaMimeType;
  if ((!msg.Text || !msg.Text.trim()) && !hasMedia) {
    log('WARN', 'Messaggio WP8 senza contenuto, ignorato');
    return;
  }

  // Map WP8 chat ID to WhatsApp contact ID
  let waContactId = chatIdToWhatsapp.get(msg.ChatId);

  // If not found, try to search by name (only for WhatsApp-style chat IDs)
  if (!waContactId && msg.SenderName && String(msg.ChatId).startsWith('wa_')) {
    debug(`Chat ID "${msg.ChatId}" sconosciuto, cerco per nome "${msg.SenderName}"...`);
    try {
      const chats = await whatsappClient.getChats();
      for (const chat of chats) {
        const contact = await chat.getContact().catch(() => null);
        if (!contact) continue;
        const name = (contact.name || contact.pushname || contact.shortName || '').toLowerCase();
        if (name.includes(msg.SenderName.toLowerCase())) {
          waContactId = chat.id._serialized;
          getChatIdForWhatsApp(waContactId); // Ensure mapping exists
          break;
        }
      }
    } catch (err) {
      log('ERR', `Errore ricerca contatto: ${err.message}`);
    }
  }

  if (!waContactId) {
    // Not a WhatsApp chat: relay it to the other connected WP8 clients.
    // This is the merged WhatsappServer (.NET relay) behavior.
    const otherClients = [...wp8Clients].filter(s => s !== senderSocket);
    if (otherClients.length > 0) {
      log('NET', `Relay locale: "${msg.SenderName || 'Sconosciuto'}" -> chat "${msg.ChatId}" inoltrato a ${otherClients.length} client(i)`);
      relayToWp8Clients(jsonStr, senderSocket);
    } else {
      log('WARN', `Contatto WhatsApp non trovato per "${msg.ChatId}" e nessun altro client WP8 connesso (messaggio non inoltrato)`);
    }
    return;
  }

  // WhatsApp not ready yet — queue the message
  if (!isWhatsAppReady) {
    log('INFO', `Messaggio in coda (WhatsApp non pronto): ${msg.Text.substring(0, 40)}`);
    pendingMessages.push({ chatId: waContactId, text: msg.Text, originalMsg: msg });
    sendToWp8Clients(buildChatMessage({
      text: 'WhatsApp non ancora connesso. Il messaggio verrà inviato automaticamente appena pronto.',
      chatId: msg.ChatId,
      type: 3,
      isIncoming: true
    }));
    return;
  }

  // Send the message — no echo back to WP8 (WP8 already added it locally)
  // This prevents duplicate messages in the chat view.
  await sendMessageToWhatsApp(waContactId, msg.Text, msg);
}

// ─── Main ───────────────────────────────────────────────────────────────────

async function main() {
  console.log('');
  console.log('╔═══════════════════════════════════════════════════════════╗');
  console.log('║                                                           ║');
  console.log('║   WhatsApp Community Unified Server v1.1                  ║');
  console.log('║   -------------------------------------------             ║');
  console.log('║   Collega la tua app Windows Phone 8.1                    ║');
  console.log('║   ai server WhatsApp reali e ad altri telefoni!           ║');
  console.log('║                                                           ║');
  console.log('║   Attenzione: uso non ufficiale                           ║');
  console.log('║      Rischio ban dell\'account WhatsApp                    ║');
  console.log('║      Usa solo con numeri secondari/test                   ║');
  console.log('║                                                           ║');
  console.log('╚═══════════════════════════════════════════════════════════╝');
  console.log('');

  log('INFO', `Node.js ${process.version}`);
  log('INFO', `Sessione: ${SESSION_DIR}`);
  log('INFO', `Porta:    ${TCP_PORT}`);
  log('INFO', `Cifratura: AES-256-GCM ${cryptoHelper.ENCRYPTION_ENABLED ? 'ATTIVA' : 'DISATTIVATA (BRIDGE_ENCRYPTION=off)'}`);
  log('INFO', `Debug:   ${DEBUG ? 'ON' : 'OFF'}`);
  console.log('');

  // Start TCP server (accepts WP8 client connections + relay)
  startTcpServer();

  // Start WhatsApp Web client (connects to real WhatsApp)
  await startWhatsAppClient();

  // ── Graceful Shutdown ──
  const shutdown = async () => {
    log('INFO', 'Arresto in corso...');
    if (whatsappClient) {
      try { await whatsappClient.destroy(); } catch (e) { /* ignore */ }
    }
    log('OK', 'Server arrestato.');
    process.exit(0);
  };

  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
}

// ─── Start ──────────────────────────────────────────────────────────────────

main().catch(err => {
  console.error('ERRORE FATALE:', err);
  process.exit(1);
});
