'use strict';

// Funzioni pure di formattazione: nessuna I/O, nessuna dipendenza da rete.
// Il JSON prodotto deve combaciare ESATTAMENTE con i [DataMember] di
// WhatsappApp/Models/ChatMessage.cs (DataContractJsonSerializer è case-sensitive).

const DEFAULT_SENDER = 'Sconosciuto';

function formatDateForWp8(value) {
  const d = value instanceof Date ? value : new Date(value === undefined ? Date.now() : value);
  const epoch = d.getTime();
  // ATTENZIONE: il formato Microsoft è /Date(ms)/ con gli slash escapati.
  return `\\/Date(${epoch})\\/`;
}

function displayNameForJid(jid) {
  if (!jid) return '?';
  const user = String(jid).split('@')[0];
  if (String(jid).endsWith('@g.us')) return `Gruppo ${user}`;
  if (/^\d+$/.test(user)) return `+${user}`;
  return user || '?';
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

  if (f.command) msg.Command = f.command;
  if (f.state) msg.State = f.state;
  if (f.pairCode) msg.PairCode = f.pairCode;
  if (f.qrImageData) msg.QrImageData = f.qrImageData;
  if (typeof f.qrDuration === 'number') msg.QrDuration = f.qrDuration;
  if (f.accountJid) msg.AccountJid = f.accountJid;

  if (f.mediaData) {
    msg.MediaData = f.mediaData;
    msg.MediaMimeType = f.mediaMimeType || 'image/jpeg';
    if (f.mediaFileName) msg.MediaFileName = f.mediaFileName;
  }

  return msg;
}

// Estrae path/didascalia/tipo da un payload webhook GOWA.
function mediaFromPayload(p) {
  const result = { type: 0, path: null, mimeType: null, fileName: null, fallbackText: '' };

  if (p.image !== undefined) {
    if (typeof p.image === 'string') { result.type = 1; result.path = p.image; }
    else if (p.image && typeof p.image.path === 'string') { result.type = 1; result.path = p.image.path; }
    else { result.fallbackText = '[Immagine non scaricata]'; }
  } else if (p.audio !== undefined) {
    if (typeof p.audio === 'string') {
      result.type = 2; result.path = p.audio; result.mimeType = 'audio/ogg'; result.fileName = 'audio.ogg';
    } else { result.fallbackText = '[Audio non scaricato]'; }
  } else if (p.video !== undefined) {
    if (p.video && typeof p.video.path === 'string') {
      result.path = p.video.path; result.mimeType = 'video/mp4';
    } else {
      result.fallbackText = '[Video non scaricato]';
    }
  } else if (p.document !== undefined) {
    if (p.document && typeof p.document.path === 'string') {
      result.path = p.document.path;
      result.fileName = p.document.filename || null;
    } else {
      result.fallbackText = '[Documento non scaricato]';
    }
  } else if (typeof p.sticker === 'string') {
    result.path = p.sticker; result.mimeType = 'image/webp'; result.fileName = 'sticker.webp';
  }

  return result;
}

function mapWebhookMessage(payload) {
  const p = payload || {};
  if (p.is_from_me === true) return null;
  const chatId = p.chat_id || p.from;
  if (!chatId || chatId === 'status@broadcast') return null;

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
  mapWebhookMessage
};
