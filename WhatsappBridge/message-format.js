'use strict';

// Pure formatting functions: no I/O, no network dependency.
// The JSON produced must match EXACTLY the [DataMember] names of
// WhatsappApp/Models/ChatMessage.cs (DataContractJsonSerializer is case-sensitive).

const DEFAULT_SENDER = 'Unknown';

// The value the Timestamp field must have *after* JSON.parse: Microsoft writes
// /Date(ms)/ and DataContractJsonSerializer expects it that way. The \/ seen in
// the JSON text is an escape of the reader, not part of the value: putting it in
// the value doubles it and the phone answers "String was not recognized as a
// valid DateTime" (0x8013150C), throwing away the whole frame.
const WP8_DATE = /^\/Date\((-?\d+)\)\/$/;

/**
 * Milliseconds from the epoch, from whatever arrives in the timestamp field. It
 * never throws and never returns NaN: a crooked timestamp is one timestamp less,
 * not one message less.
 */
function epochMillis(value) {
  if (value === undefined || value === null || value === '') return Date.now();

  if (value instanceof Date) {
    const time = value.getTime();
    return Number.isFinite(time) ? time : Date.now();
  }

  if (typeof value === 'number') {
    if (!Number.isFinite(value)) return Date.now();
    // GOWA sometimes sends seconds: 1.7e9 instead of 1.7e12.
    return Math.round(Math.abs(value) < 1e12 ? value * 1000 : value);
  }

  // The backslashes are escapes of the JSON reader: they are not needed here.
  const text = String(value).replace(/\\/g, '').trim();

  // Already in the Microsoft format (a value sent back, for example).
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

// Old media stay a word in the bubble, as in the preview of the chat-list row:
// the bytes of a photo that arrived months ago are not among the ones the
// webhook delivered, and an empty bubble is worse than a word that says what
// was there.
const HISTORY_MEDIA_LABEL = {
  image: '[Image]',
  video: '[Video]',
  audio: '[Audio]',
  document: '[Document]',
  sticker: '[Sticker]'
};

/**
 * One message from a chat history (`GET /chat/:chat_jid/messages`).
 *
 * Always text, never an image: `media_type` gives the media type, but the bytes
 * are not there, and a message of type image with no data would draw an empty
 * bubble.
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
    // Control frames are always of type System (3).
    Type: typeof f.type === 'number' ? f.type : (f.command ? 3 : 0),
    IsIncoming: typeof f.isIncoming === 'boolean' ? f.isIncoming : true
  };

  // The token of the shared service: it travels in the handshake, and for every
  // other frame it stays empty.
  if (f.token) msg.Token = f.token;
  if (f.command) msg.Command = f.command;
  if (f.state) msg.State = f.state;
  if (f.pairCode) msg.PairCode = f.pairCode;
  if (f.qrImageData) msg.QrImageData = f.qrImageData;
  if (typeof f.qrDuration === 'number') msg.QrDuration = f.qrDuration;
  if (f.accountJid) msg.AccountJid = f.accountJid;

  // Fields of calls and revocations (see calls.js and server.js).
  if (f.callId) msg.CallId = f.callId;
  if (f.callReason) msg.CallReason = f.callReason;
  if (typeof f.callDurationSeconds === 'number') msg.CallDurationSeconds = f.callDurationSeconds;
  if (typeof f.callIsVideo === 'boolean') msg.CallIsVideo = f.callIsVideo;
  if (f.relatedMessageId) msg.RelatedMessageId = f.relatedMessageId;
  // Chat-list row: the group and its picture (see chats.js).
  if (typeof f.isGroup === 'boolean') msg.IsGroup = f.isGroup;
  if (f.avatarData) msg.AvatarData = f.avatarData;
  // Chat-list row: how many messages of this conversation are still unread. The
  // adapter counts them, because GOWA does not say and because messages that
  // arrived with the phone off are seen by nobody else.
  if (typeof f.unreadCount === 'number') msg.UnreadCount = f.unreadCount;

  // History: an old message, sent when the chat is opened (see server.js).
  // It is a normal message - it must be drawn - but it did not arrive now, and
  // the app does not count it as unread or notify for each one.
  if (f.isHistory === true) msg.IsHistory = true;

  // The declared media type ("image", "video"): it tells that a history row
  // *is* an image even when the bytes did not arrive.
  if (f.mediaType) msg.MediaType = f.mediaType;

  if (f.mediaData) {
    msg.MediaData = f.mediaData;
    msg.MediaMimeType = f.mediaMimeType || 'image/jpeg';
    if (f.mediaFileName) msg.MediaFileName = f.mediaFileName;
  }

  // A media that does not fit in a single frame travels in pieces (see
  // server.js, sendMediaChunks): the piece says which one it is and how many
  // there are in all, and the app reassembles them by RelatedMessageId.
  if (typeof f.mediaChunkIndex === 'number') msg.MediaChunkIndex = f.mediaChunkIndex;
  if (typeof f.mediaChunkTotal === 'number') msg.MediaChunkTotal = f.mediaChunkTotal;

  return msg;
}

// Extracts path/caption/type from a GOWA webhook payload.
function mediaFromPayload(p) {
  const result = { type: 0, path: null, mimeType: null, fileName: null, fallbackText: '' };

  if (p.image !== undefined) {
    if (typeof p.image === 'string') { result.type = 1; result.path = p.image; }
    else if (p.image && typeof p.image.path === 'string') { result.type = 1; result.path = p.image.path; }
    else { result.fallbackText = '[Image not downloaded]'; }
  } else if (p.audio !== undefined) {
    // The word is always there: with the bytes or without, an empty bubble says
    // nothing, and an audio is not drawn.
    result.type = 2;
    result.fallbackText = '[Audio]';
    if (typeof p.audio === 'string') {
      result.path = p.audio; result.mimeType = 'audio/ogg'; result.fileName = 'audio.ogg';
    } else if (p.audio && typeof p.audio.path === 'string') {
      result.path = p.audio.path; result.mimeType = 'audio/ogg'; result.fileName = 'audio.ogg';
    }
  } else if (p.video !== undefined) {
    // A video is not drawn in a bubble: the word stays, and a caption wins over
    // it as for images. Type 4 is the one the app knows as Video (see
    // MessageType in ChatMessage.cs).
    result.type = 4;
    result.fallbackText = '[Video]';
    if (p.video && typeof p.video.path === 'string') {
      result.path = p.video.path; result.mimeType = 'video/mp4';
    } else {
      result.fallbackText = '[Video not downloaded]';
    }
  } else if (p.document !== undefined) {
    // A document stays a document even when it was not downloaded: the app must
    // know it can ask for it.
    result.kind = 'document';
    result.fallbackText = '[Document]';
    if (p.document && typeof p.document.path === 'string') {
      result.path = p.document.path;
      result.fileName = p.document.filename || null;
      // The file name is more useful than a word: it is what the user sent.
      if (result.fileName) result.fallbackText = result.fileName;
    } else {
      result.fallbackText = '[Document not downloaded]';
    }
  } else if (typeof p.sticker === 'string') {
    result.path = p.sticker; result.mimeType = 'image/webp'; result.fileName = 'sticker.webp';
  }

  return result;
}

/// The word of the media type, or empty for a text-only message.
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
  // A channel is not a conversation: its messages are not shown and do not
  // raise an unread (see chats.js, isChannelJid).
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
