'use strict';

const { displayNameForJid } = require('./message-format');

/**
 * Elenco delle conversazioni presenti nell'account collegato.
 *
 * L'app non puo' usare /user/my/contacts per questo: quella e' la *rubrica*
 * di WhatsApp, e su un dispositivo appena collegato e' vuota anche se in
 * /chats ci sono decine di conversazioni. Si legge quindi /chats, e per ogni
 * conversazione si prende l'ultimo messaggio (per l'anteprima) e, per le
 * persone, l'immagine del profilo da /user/avatar.
 *
 * Costi: una richiesta HTTP per chat per l'ultimo messaggio, piu' una per
 * l'avatar. Il numero di chat lette e' quindi un limite di configurazione, non
 * un dettaglio.
 */

// Nomi mostrati quando il messaggio non ha testo: il tipo lo dice GOWA, la
// parola la scegliamo qui perche' l'anteprima e' testo destinato a una persona.
const MEDIA_LABEL = {
  image: '[Image]',
  video: '[Video]',
  audio: '[Audio]',
  document: '[Document]',
  sticker: '[Sticker]'
};

/** L'anteprima di una riga: il testo, o il nome del media quando il testo non c'e'. */
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

/** Il messaggio piu' recente della lista, qualunque ordine usi GOWA. */
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

/**
 * Scorre le conversazioni indicate da GOWA. Una chat illeggibile, o un avatar
 * che non si scarica, non fermano l'elenco: si perde quel dettaglio.
 */
async function collectChats(options) {
  const opts = options || {};
  const gowa = opts.gowa;
  const log = typeof opts.log === 'function' ? opts.log : () => {};
  const limit = opts.limit || 25;
  const withAvatars = opts.avatars === true;

  const chats = await gowa.chats(limit);
  const rows = [];

  for (const chat of chats) {
    if (!chat || !chat.jid) continue;

    let last = null;
    try {
      last = newestMessage(await gowa.chatMessages(chat.jid, 10));
    } catch (err) {
      log('DEBUG', `Chats: messages of ${chat.jid} not readable (${err.message})`);
    }

    const isGroup = isGroupJid(chat.jid);
    const name = chat.name || displayNameForJid(chat.jid);

    let avatar = null;
    if (withAvatars && !isGroup) {
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

module.exports = { previewForMessage, collectChats };
