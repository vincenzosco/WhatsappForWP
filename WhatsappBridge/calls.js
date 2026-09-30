'use strict';

/**
 * Call records drawn from the GOWA history.
 *
 * GOWA does not send calls in the webhooks (the events are only message,
 * message.reaction, message.revoked, message.edited) and has no route to list
 * them: it does record them in the chat storage as messages with
 * media_type = "call" and a call_metadata column in JSON. The only way to read
 * them is to walk the chats one by one, so the scan is bounded: the first N
 * chats and the first M messages of each.
 *
 * Careful: GOWA records only *incoming* calls (see CreateIncomingCallRecord in
 * its source). A call made from here does not appear.
 */

// Keys read from call_metadata. "call_id" is certain (it appears as the struct
// tag `json:"call_id"` in the GOWA binary); "reason", "duration" and "is_video"
// are optional: without them the record is still valid and the UI simply shows
// fewer details.
function parseCallMetadata(raw) {
  const result = { callId: '', reason: '', durationSeconds: 0, isVideo: false };
  if (typeof raw !== 'string' || !raw.trim()) return result;

  let data;
  try {
    data = JSON.parse(raw);
  } catch (err) {
    return result;
  }
  if (!data || typeof data !== 'object' || Array.isArray(data)) return result;

  if (typeof data.call_id === 'string') result.callId = data.call_id;
  if (typeof data.reason === 'string') result.reason = data.reason;

  const duration = Number(data.duration);
  if (isFinite(duration) && duration > 0) result.durationSeconds = Math.round(duration);

  result.isVideo = data.is_video === true || data.video === true;
  return result;
}

function timeOf(value) {
  const parsed = Date.parse(value);
  return isNaN(parsed) ? 0 : parsed;
}

/**
 * Walks the chats GOWA returns and collects the calls.
 * An unreadable chat (missing storage, wrong jid) does not stop the collection.
 */
async function collectCalls(options) {
  const opts = options || {};
  const gowa = opts.gowa;
  const log = typeof opts.log === 'function' ? opts.log : () => {};
  const chatLimit = opts.chatLimit || 25;
  const messagesPerChat = opts.messagesPerChat || 100;
  const limit = opts.limit || 50;

  const chats = await gowa.chats(chatLimit);
  const found = [];

  for (const chat of chats) {
    if (!chat || !chat.jid) continue;

    let messages;
    try {
      messages = await gowa.chatMessages(chat.jid, messagesPerChat);
    } catch (err) {
      log('DEBUG', `Calls: chat ${chat.jid} not readable (${err.message})`);
      continue;
    }

    for (const message of messages) {
      if (!message || message.media_type !== 'call') continue;
      const meta = parseCallMetadata(message.call_metadata);
      found.push({
        chatId: message.chat_jid || chat.jid,
        chatName: chat.name || '',
        timestamp: message.timestamp || '',
        callId: meta.callId,
        reason: meta.reason,
        durationSeconds: meta.durationSeconds,
        isVideo: meta.isVideo,
      });
    }
  }

  found.sort((a, b) => timeOf(b.timestamp) - timeOf(a.timestamp));

  const result = found.slice(0, limit);
  log('INFO', `Calls: ${result.length} call record(s) from ${chats.length} chat(s)`);
  return result;
}

module.exports = { parseCallMetadata, collectCalls };
