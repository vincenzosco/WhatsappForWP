'use strict';

/**
 * Registro delle chiamate ricavato dalla history di GOWA.
 *
 * GOWA non manda le chiamate nei webhook (gli eventi sono solo message,
 * message.reaction, message.revoked, message.edited) e non ha una rotta per
 * elencarle: le registra pero' nella chat storage come messaggi con
 * media_type = "call" e una colonna call_metadata in JSON. L'unico modo di
 * leggerle e' scorrere le chat una per una, quindi la scansione e' limitata:
 * le prime N chat e i primi M messaggi di ognuna.
 *
 * Attenzione: GOWA registra solo le chiamate *in entrata* (vedi
 * CreateIncomingCallRecord nel suo codice). Una chiamata fatta da qui non
 * compare.
 */

// Chiavi lette da call_metadata. "call_id" e' certo (compare come struct tag
// `json:"call_id"` nel binario di GOWA); "reason", "duration" e "is_video"
// sono opzionali: se non ci sono, la voce resta valida e la UI mostra
// semplicemente meno dettagli.
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
 * Scorre le chat indicate da GOWA e raccoglie le chiamate.
 * Una chat illeggibile (storage assente, jid sbagliato) non ferma la raccolta.
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
