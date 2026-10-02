'use strict';

const { displayNameForJid } = require('./message-format');

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
  audio: '[Audio]',
  document: '[Document]',
  sticker: '[Sticker]'
};

/** The preview of a row: the text, or the media name when there is no text. */
function previewForMessage(message) {
  if (!message) return '';
  const text = typeof message.content === 'string' ? message.content.trim() : '';
  if (text) return text;
  return MEDIA_LABEL[message.media_type] || '';
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
      preview: previewForMessage(last),
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

module.exports = { previewForMessage, collectChats, isNotAConversation };
