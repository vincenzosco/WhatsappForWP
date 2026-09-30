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
const { buildChatMessage, mapWebhookMessage, mapHistoryMessage } = require('./message-format');
const { createWebhookServer } = require('./webhook-server');
const { createDiscoveryBeacon, buildPayload } = require('./discovery');
const { collectCalls } = require('./calls');
const { collectChats } = require('./chats');
const { createTranscoder } = require('./ffmpeg');

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

// I nomi dei gruppi in una richiesta. Un nome che manca costa un nome, non
// l'elenco: se GOWA non risponde si torna una mappa vuota e le righe dei gruppi
// restano con quello che l'elenco delle conversazioni diceva.
async function groupNamesOrEmpty(client, logger) {
  try {
    return await client.myGroups();
  } catch (err) {
    logger('DEBUG', `Chats: group names not readable (${err.message})`);
    return new Map();
  }
}

function createBridge({ config, gowa, log, debug, transcoder }) {
  const logger = typeof log === 'function' ? log : () => {};
  const dbg = typeof debug === 'function' ? debug : () => {};

  // La conversione dei vocali: un ffmpeg trovato all'avvio, oppure quello che
  // i test iniettano. `enabled` viene dalla configurazione.
  const mediaTools = transcoder || createTranscoder({
    enabled: !config || !config.ffmpeg || config.ffmpeg.enabled !== false,
    path: config && config.ffmpeg ? config.ffmpeg.path : undefined,
    log: logger
  });

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

  // Quanti messaggi di ogni chat non sono ancora stati letti. Vive qui e non in
  // GOWA: il suo elenco chat non ha questo campo, e un messaggio che arriva col
  // telefono spento non lo vede nessun altro. Si azzera con il comando `read`.
  const unreadByChat = new Map();

  // Un allegato in arrivo dall'app, pezzo per pezzo. Il frame ha un tetto di
  // 8 MiB e una foto o un video sono piu' grandi: i pezzi si accumulano qui e
  // si mandano a GOWA una volta sola, alla fine.
  const mediaTransfers = new Map();
  // WhatsApp rifiuta oltre 64 MB (senza compressione): oltre quel numero i byte
  // in memoria non servono a nessuno, quindi si fermano prima.
  const MAX_MEDIA_BYTES = 64 * 1024 * 1024;
  // Quante spedizioni possono essere aperte insieme. L'app ne apre una alla
  // volta; il numero serve a non tenere in memoria i pezzi di un client che
  // apre una spedizione e non la chiude mai (non c'e' nessun media.end che
  // ripulisca, e ogni pezzo resta li').
  const MAX_LIVE_TRANSFERS = 4;
  // Quanti caratteri base64 per frame verso l'app. Lo stesso numero che usa
  // ChatPage per spedire (media.begin/chunk/end): un video non sta in un frame
  // solo, e il base64 aggiunge un terzo. E' un multiplo di 4, cosi' ogni pezzo
  // e' base64 valido da solo e l'app puo' decodificarlo senza aspettare il resto.
  const MEDIA_CHUNK_CHARS = 700000;

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
        const groupNames = await groupNamesOrEmpty(gowa, logger);
        const rows = await collectChats({
          gowa,
          limit: limits.limit,
          avatars: limits.avatars,
          groupNames,
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
          avatarData: row.avatar || undefined,
          unreadCount: unreadByChat.get(row.chatId) || 0
        });
      }
    } catch (err) {
      logger('ERR', `chat list failed: ${err.message}`);
      sendControl({ command: 'error', text: `Chat list failed: ${err.message}` });
    } finally {
      sendControl({ command: 'chats.done' });
    }
  }

  /**
   * Lo storico di una chat, come frame di messaggio.
   *
   * Non e' un frame per chat come `chats`: e' un frame per messaggio, quindi il
   * limite e' quanti frame passano. Ogni frame porta `IsHistory`, perche' l'app
   * deve disegnarlo ma non contarlo fra i non letti.
   */
  async function sendMessages(chatId) {
    if (!chatId) return;

    if (state.status !== 'connected') {
      sendControl({ command: 'error', chatId, text: 'WhatsApp is not connected: the chat history is unavailable.' });
      return;
    }

    const limits = (config && config.messages) || {};
    try {
      const list = await gowa.chatMessages(chatId, limits.limit || 50);
      let sent = 0;

      for (const raw of list) {
        const mapped = mapHistoryMessage(raw);
        // Senza l'id di WhatsApp l'app non puo' riconoscere un doppione, e
        // riaprendo la chat si accumulerebbero copie: meglio un messaggio in
        // meno di una lista che si allunga da sola.
        if (!mapped.id) continue;
        if (!mapped.chatId) mapped.chatId = chatId;

        sendControl(mapped);
        sent++;
      }

      logger('INFO', `history: ${sent} message(s) for ${chatId}`);
    } catch (err) {
      logger('ERR', `history failed for ${chatId}: ${err.message}`);
      sendControl({ command: 'error', chatId, text: `Chat history failed: ${err.message}` });
    }
  }

  /**
   * I byte del media di un messaggio che l'app ha gia' (una riga di cronologia
   * arrivata come parola). Viaggia come frame di controllo, non di messaggio:
   * e' un pezzo che completa un messaggio esistente, non uno nuovo, e come
   * messaggio alzerebbe il conteggio dei non letti e un avviso.
   */
  /**
   * I byte di un media che l'app ha gia' (una riga di cronologia arrivata come
   * parola), a pezzi. Un video non sta in un frame solo: il tetto e' 8 MiB e il
   * base64 aggiunge un terzo. Ogni frame e' un `media` con lo stesso
   * RelatedMessageId, il pezzo e quanti sono in tutto; l'app li ricompone. Sono
   * frame di controllo e non messaggi, perche' completano un messaggio che
   * esiste gia' e un messaggio in piu' alzerebbe i non letti.
   */
  function sendMediaChunks(chatId, messageId, mediaType, mimeType, fileName, base64) {
    const total = Math.max(1, Math.ceil(base64.length / MEDIA_CHUNK_CHARS));
    for (let i = 0; i < total; i++) {
      sendControl({
        command: 'media',
        chatId,
        relatedMessageId: messageId,
        mediaType,
        mediaData: base64.substr(i * MEDIA_CHUNK_CHARS, MEDIA_CHUNK_CHARS),
        mediaMimeType: mimeType,
        mediaFileName: fileName || undefined,
        mediaChunkIndex: i,
        mediaChunkTotal: total
      });
    }
  }

  /**
   * I byte da mandare all'app per un media ricevuto. Un audio che WP8.1 non
   * legge (Ogg/Opus) diventa MP3; tutto il resto passa invariato, e cosi' fa
   * anche un vocale quando ffmpeg non c'e' o la conversione fallisce.
   */
  async function playableMedia(buffer, mediaType, mimeType, fileName) {
    if (mediaType !== 'audio') return { buffer, mimeType, fileName };
    const converted = await mediaTools.toPlayable(buffer, mimeType, fileName);
    if (!converted) return { buffer, mimeType, fileName };
    return converted;
  }

  async function sendMedia(chatId, messageId) {
    if (!chatId || !messageId) return;

    if (state.status !== 'connected') {
      sendControl({ command: 'error', chatId, text: 'WhatsApp is not connected: the media is unavailable.' });
      return;
    }

    try {
      const media = await gowa.downloadMedia(chatId, messageId);
      if (!media) {
        // Il file non c'e' piu': si dice, invece di lasciare la bolla in attesa
        // per sempre (vedi il test del comando).
        sendControl({
          command: 'error',
          chatId,
          relatedMessageId: messageId,
          text: 'This media is no longer available on the server.'
        });
        return;
      }

      const kind = mediaKindOf(media.mimeType, media.fileName);
      const playable = await playableMedia(Buffer.from(media.base64, 'base64'), kind,
        media.mimeType, media.fileName);
      sendMediaChunks(chatId, messageId, kind, playable.mimeType, playable.fileName,
        playable.buffer.toString('base64'));
      logger('INFO', `media downloaded for ${messageId} (${media.base64.length} chars)`);
    } catch (err) {
      logger('ERR', `media download failed for ${messageId}: ${err.message}`);
      sendControl({
        command: 'error',
        chatId,
        relatedMessageId: messageId,
        text: `Media download failed: ${err.message}`
      });
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

  /// Il numero leggibile di un JID (es. +393401234567). Vuoto per un gruppo.
  function numberForJid(jid) {
    const user = String(jid || '').split('@')[0].split(':')[0];
    return /^\d+$/.test(user) ? '+' + user : '';
  }

  /// Il profilo aziendale nella forma che l'app si aspetta, o null.
  function businessFrom(profile) {
    if (!profile) return null;
    return {
      Email: profile.email || '',
      Address: profile.address || '',
      Categories: profile.categories || [],
      Timezone: profile.timezone || '',
      Hours: (profile.hours || []).map(function (h) {
        return {
          Day: h.day_of_week === undefined || h.day_of_week === null ? '' : String(h.day_of_week),
          Mode: h.mode || '',
          Open: h.open_time || '',
          Close: h.close_time || ''
        };
      })
    };
  }

  /// Un membro di un gruppo come lo mostra l'app: un nome c'e' sempre, anche
  /// quando WhatsApp ne manda uno solo per un numero.
  function groupMember(p) {
    return {
      Jid: p.jid || '',
      Number: p.phoneNumber || numberForJid(p.jid),
      Name: p.displayName || numberForJid(p.jid) || p.jid || '',
      IsAdmin: p.isAdmin === true,
      IsSuperAdmin: p.isSuperAdmin === true
    };
  }

  /**
   * Le informazioni di un profilo, in un solo frame di controllo.
   *
   * Tre richieste a monte (il profilo e, se e' un business, il profilo
   * aziendale; per un gruppo i membri e la descrizione) e una sola risposta:
   * l'app chiede una cosa e aspetta una cosa. L'immagine la porta `avatar`, che
   * ha gia' la sua cache, cosi' la pagina grande la ha anche per una chat il cui
   * elenco non l'aveva.
   *
   * Qualunque guasto diventa un profilo vuoto: la pagina deve smettere di
   * aspettare, non restare in caricamento per sempre.
   */
  async function sendContactInfo(jid) {
    if (!jid) return;

    const info = {
      Name: '', About: '', Number: '', AvatarData: '', Business: null, Group: null
    };

    try {
      info.AvatarData = (await gowa.avatar(jid)) || '';

      if (jid.endsWith('@g.us')) {
        const participants = await gowa.groupParticipants(jid);
        const description = await gowa.groupInfo(jid);
        info.Group = {
          Description: (description && description.topic) || '',
          Members: participants && participants.participants
            ? participants.participants.map(groupMember)
            : []
        };
        if (participants && participants.name) info.Name = participants.name;
      } else {
        const user = await gowa.userInfo(jid);
        if (user) {
          info.Name = user.name || user.verifiedName || '';
          info.About = user.status || '';
        }
        info.Number = numberForJid(jid);
        info.Business = businessFrom(await gowa.businessProfile(jid));
      }
    } catch (err) {
      logger('WARN', `contact info failed for ${jid}: ${err.message}`);
    }

    sendControl({ command: 'contact.info', chatId: jid, text: JSON.stringify(info) });
  }

  // ─── Messaggi dall'app verso WhatsApp ─────────────────────────────────────

  /// La strada giusta per un allegato, dal tipo MIME (o dall'estensione quando
  /// il tipo non c'e'): image, video, audio, altrimenti document. Il tipo che
  /// ne esce viaggia anche verso l'app, che da esso decide come disegnare la
  /// bolla (vedi ChatMessage.IsAudio / IsDocument).
  function mediaKindOf(mimeType, fileName) {
    const mime = String(mimeType || '').toLowerCase();
    const name = String(fileName || '').toLowerCase();
    if (mime.indexOf('video/') === 0 || /\.(mp4|mov|3gp|avi|mkv|webm)$/.test(name)) return 'video';
    if (mime.indexOf('image/') === 0) return 'image';
    if (mime.indexOf('audio/') === 0 || /\.(ogg|opus|oga|mp3|m4a|aac|amr|wav)$/.test(name)) return 'audio';
    return 'document';
  }

  async function sendMediaToGowa(chatId, caption, buffer, mimeType, fileName) {
    const kind = mediaKindOf(mimeType, fileName);
    if (kind === 'video') return gowa.sendVideo(chatId, caption || '', buffer, mimeType || 'video/mp4', fileName);
    if (kind === 'image') return gowa.sendImage(chatId, caption || '', buffer, mimeType || 'image/jpeg', fileName);
    return gowa.sendFile(chatId, caption || '', buffer, mimeType || 'application/octet-stream', fileName);
  }

  function mediaBegin(msg) {
    if (!msg.MediaTransferId) return;

    // Il totale dichiarato e' quello che rende verificabile la fine: senza,
    // un allegato a cui manca un pezzo e' indistinguibile da uno intero.
    const declared = Number(msg.MediaChunkTotal);
    const chunkTotal = Number.isInteger(declared) && declared > 0 ? declared : null;

    // Lo stesso id due volte: il secondo comando riparte da zero invece di
    // sommarsi al primo.
    mediaTransfers.delete(msg.MediaTransferId);

    // Una spedizione mai chiusa non si accumula all'infinito: la piu' vecchia
    // paga per la nuova.
    if (mediaTransfers.size >= MAX_LIVE_TRANSFERS) {
      const oldest = mediaTransfers.keys().next();
      if (!oldest.done) {
        logger('WARN', `too many open attachments: dropping ${oldest.value}`);
        mediaTransfers.delete(oldest.value);
      }
    }

    mediaTransfers.set(msg.MediaTransferId, {
      chatId: msg.ChatId,
      messageId: msg.RelatedMessageId || null,
      fileName: msg.MediaFileName || null,
      mimeType: msg.MediaMimeType || null,
      chunkTotal,
      bytes: 0,
      parts: []
    });
  }

  function mediaChunk(msg) {
    const transfer = mediaTransfers.get(msg.MediaTransferId);
    if (!transfer) return;

    const index = Number(msg.MediaChunkIndex);
    if (!Number.isInteger(index) || index < 0) return;

    // Fuori dall'intervallo dichiarato non e' un pezzo di questo file: usarlo
    // come indice di un array voleva dire un array con due miliardi di buchi,
    // che filter percorre tutti.
    if (transfer.chunkTotal !== null && index >= transfer.chunkTotal) {
      logger('WARN', `attachment piece ${index} is outside 0..${transfer.chunkTotal - 1}, ignored`);
      return;
    }

    // Ogni pezzo e' un multiplo di 4 caratteri base64: decodificarlo da solo e
    // concatenare i byte da' esattamente il file intero.
    const part = Buffer.from(msg.MediaData || '', 'base64');

    // Il tetto si controlla mentre i byte arrivano, non dopo averli tenuti
    // tutti in memoria.
    if (transfer.bytes + part.length > MAX_MEDIA_BYTES) {
      mediaTransfers.delete(msg.MediaTransferId);
      logger('WARN', `attachment over ${MAX_MEDIA_BYTES} bytes, refused while arriving`);
      sendControl({
        command: 'error',
        chatId: transfer.chatId,
        relatedMessageId: transfer.messageId || undefined,
        text: 'The file is too large to send.'
      });
      return;
    }

    transfer.bytes += part.length;
    transfer.parts[index] = part;
  }

  async function mediaEnd(msg) {
    const transfer = mediaTransfers.get(msg.MediaTransferId);
    if (!transfer) return;
    mediaTransfers.delete(msg.MediaTransferId);

    const parts = transfer.parts.filter((part) => part);
    if (parts.length === 0) return;

    // Un pezzo mancante e' un guasto, non un file piu' corto: mandare meta'
    // video senza dirlo e' peggio che non mandarlo.
    if (transfer.chunkTotal !== null && parts.length !== transfer.chunkTotal) {
      logger('WARN', `attachment incomplete: ${parts.length} of ${transfer.chunkTotal} pieces`);
      sendControl({
        command: 'error',
        chatId: transfer.chatId,
        relatedMessageId: transfer.messageId || undefined,
        text: 'The attachment did not arrive complete: send it again.'
      });
      return;
    }

    const buffer = Buffer.concat(parts);

    if (buffer.length > MAX_MEDIA_BYTES) {
      logger('WARN', `attachment too large (${buffer.length} bytes), refused`);
      sendControl({ command: 'error', chatId: transfer.chatId, text: 'The file is too large to send.' });
      return;
    }

    if (state.status !== 'connected') {
      // Come un messaggio di testo: si tiene da parte e parte alla connessione.
      pendingOutgoing.push({
        ChatId: transfer.chatId,
        Text: msg.Text || '',
        MediaData: buffer.toString('base64'),
        MediaMimeType: transfer.mimeType,
        MediaFileName: transfer.fileName
      });
      logger('INFO', 'WhatsApp not ready: attachment queued');
      return;
    }

    try {
      logger('MSG', `attachment to ${transfer.chatId}: ${buffer.length} bytes (${mediaKindOf(transfer.mimeType, transfer.fileName)})`);
      await sendMediaToGowa(transfer.chatId, msg.Text, buffer, transfer.mimeType, transfer.fileName);
    } catch (err) {
      logger('ERR', `attachment to ${transfer.chatId} failed: ${err.message}`);
      sendControl({ command: 'error', chatId: transfer.chatId, text: `Send failed: ${err.message}` });
    }
  }

  async function sendOutgoing(msg) {
    try {
      if (msg.MediaData) {
        // Una versione vecchia dell'app manda l'allegato dentro il messaggio:
        // si accetta ancora, ma sulla strada giusta.
        await sendMediaToGowa(msg.ChatId, msg.Text, Buffer.from(msg.MediaData, 'base64'),
          msg.MediaMimeType, msg.MediaFileName);
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

    // Un messaggio che non e' mio e' arrivato adesso: la sua chat ha una cosa
    // in piu' da leggere, anche se l'app non e' collegata in questo momento.
    unreadByChat.set(fields.chatId, (unreadByChat.get(fields.chatId) || 0) + 1);

    let mediaBuffer = null;
    let mediaMimeType = fields.mediaMimeType;
    if (fields.mediaPath) {
      try {
        const media = await gowa.fetchBinary(fields.mediaPath);
        mediaBuffer = media.buffer;
        if (!mediaMimeType) mediaMimeType = media.contentType;
      } catch (err) {
        logger('WARN', `media not downloaded (${fields.mediaPath}): ${err.message}`);
      }
    }

    // Un vocale arriva Ogg/Opus e il telefono non lo legge: si converte prima
    // di spezzarlo verso l'app.
    if (mediaBuffer && fields.mediaType === 'audio') {
      const playable = await playableMedia(mediaBuffer, 'audio', mediaMimeType, fields.mediaFileName);
      mediaBuffer = playable.buffer;
      mediaMimeType = playable.mimeType;
      fields.mediaFileName = playable.fileName;
    }

    // Un media grande si manda a pezzi, dopo il messaggio e legato al suo id
    // (sendMediaChunks). Solo un media senza id - che l'app non potrebbe
    // nemmeno chiedere - viaggia dentro il messaggio, come prima.
    const inlineMedia = mediaBuffer && !fields.id;

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
      mediaType: fields.mediaType,
      mediaData: inlineMedia ? mediaBuffer.toString('base64') : null,
      mediaMimeType: inlineMedia ? mediaMimeType : undefined,
      mediaFileName: fields.mediaFileName
    }));

    if (mediaBuffer && fields.id) {
      sendMediaChunks(fields.chatId, fields.id, fields.mediaType, mediaMimeType,
        fields.mediaFileName, mediaBuffer.toString('base64'));
    }
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
      case 'messages':
        // Il JID viaggia in `Text`, come per `login.code`: e' il campo che il
        // protocollo di controllo usa per il dato di accompagnamento, e cosi'
        // non serve un secondo tipo di frame in uscita.
        await sendMessages((msg.Text || '').trim());
        break;
      case 'contact.info':
        // Il JID viaggia in `Text`, come per `messages`: e' il campo che il
        // protocollo di controllo usa per il dato di accompagnamento.
        await sendContactInfo((msg.Text || '').trim());
        break;
      case 'read':
        // L'app ha mostrato quella conversazione: da adesso non ha piu' niente
        // da leggere. La chat non deve esistere per forza nell'elenco.
        unreadByChat.delete((msg.Text || '').trim());
        break;
      case 'media.begin':
        mediaBegin(msg);
        break;
      case 'media.chunk':
        mediaChunk(msg);
        break;
      case 'media.end':
        await mediaEnd(msg);
        break;
      case 'media.get':
        // Il JID della chat in Text (come `messages`), l'id del messaggio in
        // RelatedMessageId: e' il campo che dice a cosa si riferisce un frame.
        await sendMedia((msg.Text || '').trim(), msg.RelatedMessageId);
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
    probeFfmpeg: () => mediaTools.probe(),
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
  await bridge.probeFfmpeg();

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
