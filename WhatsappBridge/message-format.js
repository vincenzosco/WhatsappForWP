'use strict';

// Funzioni pure di formattazione: nessuna I/O, nessuna dipendenza da rete.
// Il JSON prodotto deve combaciare ESATTAMENTE con i [DataMember] di
// WhatsappApp/Models/ChatMessage.cs (DataContractJsonSerializer è case-sensitive).

const DEFAULT_SENDER = 'Unknown';

// Il valore che il campo Timestamp deve avere *dopo* JSON.parse: Microsoft scrive
// /Date(ms)/ e DataContractJsonSerializer se lo aspetta cosi'. Il \/ che si vede
// nel testo JSON e' un escape del lettore, non parte del valore: metterlo nel
// valore lo raddoppia e il telefono risponde "String was not recognized as a
// valid DateTime" (0x8013150C), buttando via l'intero frame.
const WP8_DATE = /^\/Date\((-?\d+)\)\/$/;

/**
 * Millisecondi dall'epoch, da qualunque cosa arrivi nel campo timestamp. Non
 * lancia e non restituisce mai NaN: un timestamp storto e' un timestamp
 * in meno, non un messaggio in meno.
 */
function epochMillis(value) {
  if (value === undefined || value === null || value === '') return Date.now();

  if (value instanceof Date) {
    const time = value.getTime();
    return Number.isFinite(time) ? time : Date.now();
  }

  if (typeof value === 'number') {
    if (!Number.isFinite(value)) return Date.now();
    // GOWA a volte manda i secondi: 1.7e9 invece di 1.7e12.
    return Math.round(Math.abs(value) < 1e12 ? value * 1000 : value);
  }

  // I backslash sono escape del lettore JSON: qui non servono.
  const text = String(value).replace(/\\/g, '').trim();

  // Gia' nel formato Microsoft (un valore rispedito indietro, per esempio).
  const microsoft = WP8_DATE.exec(text);
  if (microsoft) return Number(microsoft[1]);

  if (/^-?\d+$/.test(text)) {
    const n = Number(text);
    return Math.round(Math.abs(n) < 1e12 ? n * 1000 : n);
  }

  const parsed = Date.parse(text);
  return Number.isNaN(parsed) ? Date.now() : parsed;
}

function formatDateForWp8(value) {
  return `/Date(${epochMillis(value)})/`;
}

function displayNameForJid(jid) {
  if (!jid) return '?';
  const user = String(jid).split('@')[0];
  if (String(jid).endsWith('@g.us')) return `Group ${user}`;
  if (/^\d+$/.test(user)) return `+${user}`;
  return user || '?';
}

// I media vecchi restano una parola nel fumetto, come nell'anteprima della riga
// dell'elenco chat: i byte di una foto che e' arrivata mesi fa non sono fra
// quelli che il webhook ha consegnato, e un fumetto vuoto e' peggio di una
// parola che dice cosa c'era.
const HISTORY_MEDIA_LABEL = {
  image: '[Image]',
  video: '[Video]',
  audio: '[Audio]',
  document: '[Document]',
  sticker: '[Sticker]'
};

/**
 * Un messaggio dello storico di una chat (`GET /chat/:chat_jid/messages`).
 *
 * Sempre testo, mai immagine: il tipo del media lo dice `media_type`, ma i byte
 * non ci sono, e un messaggio di tipo immagine senza dati disegnerebbe un
 * fumetto vuoto.
 */
function mapHistoryMessage(raw) {
  const m = raw || {};
  const media = typeof m.media_type === 'string' ? m.media_type.trim().toLowerCase() : '';
  const content = typeof m.content === 'string' ? m.content.trim() : '';
  const isFromMe = m.is_from_me === true;

  return {
    id: m.id || null,
    text: content || HISTORY_MEDIA_LABEL[media] || '',
    senderId: isFromMe ? 'me' : (m.sender_jid || ''),
    senderName: m.sender_display_name || '',
    chatId: m.chat_jid || '',
    timestamp: formatDateForWp8(m.timestamp),
    type: 0,
    mediaType: media,
    isIncoming: !isFromMe,
    isHistory: true
  };
}

function buildChatMessage(fields) {
  const f = fields || {};
  const msg = {
    Id: f.id || `msg_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`,
    Text: f.text || '',
    SenderId: f.senderId || 'unknown',
    SenderName: f.senderName || DEFAULT_SENDER,
    ChatId: f.chatId || '0',
    Timestamp: formatDateForWp8(f.timestamp),
    Status: typeof f.status === 'number' ? f.status : 1,
    // I frame di controllo sono sempre di tipo System (3).
    Type: typeof f.type === 'number' ? f.type : (f.command ? 3 : 0),
    IsIncoming: typeof f.isIncoming === 'boolean' ? f.isIncoming : true
  };

  // Il token del servizio condiviso: viaggia nell'handshake, e per ogni altro
  // frame resta vuoto.
  if (f.token) msg.Token = f.token;
  if (f.command) msg.Command = f.command;
  if (f.state) msg.State = f.state;
  if (f.pairCode) msg.PairCode = f.pairCode;
  if (f.qrImageData) msg.QrImageData = f.qrImageData;
  if (typeof f.qrDuration === 'number') msg.QrDuration = f.qrDuration;
  if (f.accountJid) msg.AccountJid = f.accountJid;

  // Campi delle chiamate e delle revoche (vedi calls.js e server.js).
  if (f.callId) msg.CallId = f.callId;
  if (f.callReason) msg.CallReason = f.callReason;
  if (typeof f.callDurationSeconds === 'number') msg.CallDurationSeconds = f.callDurationSeconds;
  if (typeof f.callIsVideo === 'boolean') msg.CallIsVideo = f.callIsVideo;
  if (f.relatedMessageId) msg.RelatedMessageId = f.relatedMessageId;
  // Riga dell'elenco chat: il gruppo e la sua immagine (vedi chats.js).
  if (typeof f.isGroup === 'boolean') msg.IsGroup = f.isGroup;
  if (f.avatarData) msg.AvatarData = f.avatarData;
  // Riga dell'elenco chat: quanti messaggi di questa conversazione non sono
  // ancora stati letti. Lo conta l'adapter, perche' GOWA non lo dice e perche'
  // i messaggi arrivati col telefono spento non li vede nessun altro.
  if (typeof f.unreadCount === 'number') msg.UnreadCount = f.unreadCount;

  // Cronologia: un messaggio vecchio, mandato aprendo la chat (vedi server.js).
  // E' un messaggio normale - va disegnato - ma non e' arrivato adesso, e l'app
  // non lo conta come non letto ne' avvisa per ognuno.
  if (f.isHistory === true) msg.IsHistory = true;

  // Il tipo di media dichiarato ("image", "video"): serve a sapere che una riga
  // di cronologia *e'* un'immagine anche quando i byte non sono arrivati.
  if (f.mediaType) msg.MediaType = f.mediaType;

  if (f.mediaData) {
    msg.MediaData = f.mediaData;
    msg.MediaMimeType = f.mediaMimeType || 'image/jpeg';
    if (f.mediaFileName) msg.MediaFileName = f.mediaFileName;
  }

  // Un media che non sta in un frame solo viaggia a pezzi (vedi
  // server.js, sendMediaChunks): il pezzo dice quale e' e quanti sono in tutto,
  // e l'app li ricompone per RelatedMessageId.
  if (typeof f.mediaChunkIndex === 'number') msg.MediaChunkIndex = f.mediaChunkIndex;
  if (typeof f.mediaChunkTotal === 'number') msg.MediaChunkTotal = f.mediaChunkTotal;

  return msg;
}

// Estrae path/didascalia/tipo da un payload webhook GOWA.
function mediaFromPayload(p) {
  const result = { type: 0, path: null, mimeType: null, fileName: null, fallbackText: '' };

  if (p.image !== undefined) {
    if (typeof p.image === 'string') { result.type = 1; result.path = p.image; }
    else if (p.image && typeof p.image.path === 'string') { result.type = 1; result.path = p.image.path; }
    else { result.fallbackText = '[Image not downloaded]'; }
  } else if (p.audio !== undefined) {
    // La parola c'e' sempre: con i byte o senza, un fumetto vuoto non dice
    // niente, e un audio non si disegna.
    result.type = 2;
    result.fallbackText = '[Audio]';
    if (typeof p.audio === 'string') {
      result.path = p.audio; result.mimeType = 'audio/ogg'; result.fileName = 'audio.ogg';
    } else if (p.audio && typeof p.audio.path === 'string') {
      result.path = p.audio.path; result.mimeType = 'audio/ogg'; result.fileName = 'audio.ogg';
    }
  } else if (p.video !== undefined) {
    // Un video non si disegna in un fumetto: la parola resta, e una didascalia
    // vince su di essa come per le immagini. Il tipo 4 e' quello che l'app
    // conosce come Video (vedi MessageType in ChatMessage.cs).
    result.type = 4;
    result.fallbackText = '[Video]';
    if (p.video && typeof p.video.path === 'string') {
      result.path = p.video.path; result.mimeType = 'video/mp4';
    } else {
      result.fallbackText = '[Video not downloaded]';
    }
  } else if (p.document !== undefined) {
    // Un documento resta un documento anche quando non e' stato scaricato:
    // l'app deve sapere che puo' chiederlo.
    result.kind = 'document';
    result.fallbackText = '[Document]';
    if (p.document && typeof p.document.path === 'string') {
      result.path = p.document.path;
      result.fileName = p.document.filename || null;
      // Il nome del file e' piu' utile di una parola: e' quello che l'utente
      // ha mandato.
      if (result.fileName) result.fallbackText = result.fileName;
    } else {
      result.fallbackText = '[Document not downloaded]';
    }
  } else if (typeof p.sticker === 'string') {
    result.path = p.sticker; result.mimeType = 'image/webp'; result.fileName = 'sticker.webp';
  }

  return result;
}

/// La parola del tipo di media, o vuota per un messaggio di solo testo.
function mediaKind(media) {
  if (media && media.kind) return media.kind;
  const type = media && media.type;
  if (type === 1) return 'image';
  if (type === 2) return 'audio';
  if (type === 4) return 'video';
  return media && media.path ? 'document' : '';
}

function mapWebhookMessage(payload) {
  const p = payload || {};
  if (p.is_from_me === true) return null;
  const chatId = p.chat_id || p.from;
  // Un canale non e' una conversazione: i suoi messaggi non si mostrano e non
  // alzano un non letto (vedi chats.js, isChannelJid).
  if (!chatId || chatId === 'status@broadcast' || chatId.endsWith('@newsletter')) return null;

  const senderId = p.from || chatId;
  const senderName = p.sender_display_name || p.from_name || displayNameForJid(senderId);
  const media = mediaFromPayload(p);

  let text = p.body || '';
  if (!text && media.fallbackText) text = media.fallbackText;

  return {
    id: p.id || null,
    text,
    senderId,
    senderName,
    chatId,
    timestamp: p.timestamp ? new Date(p.timestamp) : new Date(),
    type: media.type,
    mediaType: mediaKind(media),
    mediaPath: media.path,
    mediaFileName: media.fileName,
    mediaMimeType: media.mimeType
  };
}

module.exports = {
  DEFAULT_SENDER,
  formatDateForWp8,
  displayNameForJid,
  buildChatMessage,
  mapWebhookMessage,
  mapHistoryMessage
};
