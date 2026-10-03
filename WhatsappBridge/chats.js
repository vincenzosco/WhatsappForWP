'use strict';

const { displayNameForJid } = require('./message-format');
const { durationSecondsOf, formatDuration } = require('./audio-duration');

/**
 * List of the conversations present in the linked account.
 *
 * The app cannot use /user/my/contacts for this: that is the WhatsApp
 * *address book*, and on a freshly linked device it is empty even when /chats
 * holds dozens of conversations. So /chats is read, and for every conversation
 * the last message (for the preview) and the profile picture from /user/avatar
 * are taken: for a person as for a group, because that parameter is a JID and
 * whatsmeow accepts any JID.
 *
 * Cost: one HTTP request per chat for the last message, plus one for the
 * avatar. How many chats are read is therefore a configuration limit, not a
 * detail.
 */

// Names shown when the message has no text: GOWA gives the type, the word is
// chosen here because the preview is text aimed at a person.
const MEDIA_LABEL = {
  image: '[Image]',
  video: '[Video]',
  // An audio is not bracketed like the others: it is the one whose length is
  // known, and the row reads "Audio 0:10".
  audio: 'Audio',
  document: '[Document]',
  sticker: '[Sticker]'
};

/**
 * The preview of a row: the text, or the media name when there is no text.
 *
 * `durationSeconds` is the measured length of an audio, when there is one (see
 * collectChats): the row then reads "Audio 0:10" instead of a bare word. It is
 * a parameter and not read off the message, because GOWA does not send it.
 */
function previewForMessage(message, durationSeconds) {
  if (!message) return '';
  const text = typeof message.content === 'string' ? message.content.trim() : '';
  if (text) return text;

  const label = MEDIA_LABEL[message.media_type] || '';
  if (message.media_type === 'audio' && typeof durationSeconds === 'number'
      && isFinite(durationSeconds) && durationSeconds >= 0) {
    return `${label} ${formatDuration(durationSeconds)}`;
  }
  return label;
}

function timeOf(value) {
  const parsed = Date.parse(value);
  return isNaN(parsed) ? 0 : parsed;
}

/** The most recent message in the list, whatever order GOWA uses. */
function newestMessage(messages) {
  let best = null;
  let bestTime = -1;
  for (const message of messages || []) {
    if (!message) continue;
    const at = timeOf(message.timestamp);
    if (best === null || at > bestTime) {
      best = message;
      bestTime = at;
    }
  }
  return best;
}

function isGroupJid(jid) {
  return typeof jid === 'string' && jid.endsWith('@g.us');
}

// How long the measured durations are kept, and how many. The chat list is
// rebuilt on every reconnection and on the cache's minute, and each entry is
// one downloaded voice note: without this the same note would be downloaded
// again every time. A null answer - the bytes could not be fetched - is kept
// for less, because the next list may succeed.
const DURATION_TTL_MS = 30 * 60 * 1000;
const DURATION_MISSING_TTL_MS = 2 * 60 * 1000;
const DURATION_MAX_ENTRIES = 120;

/** The durations already measured, by message id. Shaped like avatar-cache.js. */
function createDurationCache(options) {
  const opts = options || {};
  const ttlMs = typeof opts.ttlMs === 'number' ? opts.ttlMs : DURATION_TTL_MS;
  const missingTtlMs = typeof opts.missingTtlMs === 'number'
    ? opts.missingTtlMs
    : DURATION_MISSING_TTL_MS;
  const maxEntries = typeof opts.maxEntries === 'number' ? opts.maxEntries : DURATION_MAX_ENTRIES;
  const now = typeof opts.now === 'function' ? opts.now : () => Date.now();

  const entries = new Map();

  return {
    /** The kept duration, null if it could not be measured, undefined if unknown. */
    get(key) {
      const entry = entries.get(key);
      if (!entry) return undefined;
      if (now() - entry.at > entry.ttl) {
        entries.delete(key);
        return undefined;
      }
      return entry.value;
    },

    /** Keeps a measurement. null is a measurement too: "it could not be read". */
    put(key, value) {
      if (typeof key !== 'string' || key === '') return;

      entries.delete(key);
      entries.set(key, {
        value,
        at: now(),
        ttl: value === null || value === undefined ? missingTtlMs : ttlMs
      });

      while (entries.size > maxEntries) {
        entries.delete(entries.keys().next().value);
      }
    },

    size() {
      return entries.size;
    }
  };
}

/**
 * How long the voice note of this message is, or null.
 *
 * The bytes come from the same route the app uses for a media it wants
 * (`/message/:id/download`), so nothing new is asked of GOWA. Every failure is
 * the same answer - no duration - and the row keeps the word.
 */
async function measureAudio(gowa, chatId, message, durations, log) {
  if (!message || !message.id) return null;

  const remembered = durations.get(message.id);
  if (remembered !== undefined) return remembered;

  let seconds = null;
  try {
    const media = await gowa.downloadMedia(chatId, message.id);
    if (media && media.base64) {
      const bytes = Buffer.from(media.base64, 'base64');
      seconds = durationSecondsOf(bytes, media.mimeType, media.fileName);
    }
  } catch (err) {
    log('DEBUG', `Chats: audio of ${chatId} not readable (${err.message})`);
  }

  durations.put(message.id, seconds);
  return seconds;
}

// A channel and the status broadcast are not conversations: neither can be
// answered, and in the chat list each takes the place of a person. GOWA lists
// both, so both are skipped here. The status is the same JID message-format.js
// refuses as a message, and the app draws it in its own Status section.
function isNotAConversation(jid) {
  return typeof jid === 'string'
    && (jid === 'status@broadcast' || jid.endsWith('@newsletter'));
}

/**
 * Walks the conversations GOWA returns. An unreadable chat, or an avatar that
 * does not download, does not stop the list: that one detail is lost.
 */
async function collectChats(options) {
  const opts = options || {};
  const gowa = opts.gowa;
  const log = typeof opts.log === 'function' ? opts.log : () => {};
  const limit = opts.limit || 25;
  const withAvatars = opts.avatars === true;
  const groupNames = opts.groupNames instanceof Map ? opts.groupNames : new Map();
  const durations = opts.durations || createDurationCache();

  const chats = await gowa.chats(limit);
  const rows = [];

  for (const chat of chats) {
    if (!chat || !chat.jid) continue;
    if (isNotAConversation(chat.jid)) continue;

    let last = null;
    try {
      last = newestMessage(await gowa.chatMessages(chat.jid, 10));
    } catch (err) {
      log('DEBUG', `Chats: messages of ${chat.jid} not readable (${err.message})`);
    }

    // The length of a voice note, which GOWA does not send: it is measured from
    // the bytes, once per message id (see measureAudio).
    let durationSeconds = null;
    if (last && last.media_type === 'audio') {
      durationSeconds = await measureAudio(gowa, chat.jid, last, durations, log);
    }

    const isGroup = isGroupJid(chat.jid);
    // For a group the real name from /user/my/groups is preferred: what comes
    // with the conversation list may be the "Group <number>" placeholder of
    // GOWA, or the bare number.
    const name = (isGroup && groupNames.get(chat.jid)) || chat.name || displayNameForJid(chat.jid);

    // The profile picture is asked for every conversation: GOWA can give it for
    // a group too, because its `phone` parameter is a JID and whatsmeow accepts
    // any JID in a profile request.
    let avatar = null;
    if (withAvatars) {
      try {
        avatar = await gowa.avatar(chat.jid);
      } catch (err) {
        log('DEBUG', `Chats: avatar of ${chat.jid} not readable (${err.message})`);
      }
    }

    rows.push({
      chatId: chat.jid,
      name,
      preview: previewForMessage(last, durationSeconds),
      timestamp: (last && last.timestamp) || '',
      isGroup,
      avatar: avatar || null
    });
  }

  rows.sort((a, b) => timeOf(b.timestamp) - timeOf(a.timestamp));

  const result = rows.slice(0, limit);
  log('INFO', `Chats: ${result.length} conversation(s) from ${chats.length}`);
  return result;
}

module.exports = { previewForMessage, collectChats, isNotAConversation, createDurationCache };
