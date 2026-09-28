'use strict';

const { displayNameForJid } = require('./message-format');

/**
 * Elenco delle conversazioni presenti nell'account collegato.
 *
 * L'app non puo' usare /user/my/contacts per questo: quella e' la *rubrica*
 * di WhatsApp, e su un dispositivo appena collegato e' vuota anche se in
 * /chats ci sono decine di conversazioni. Si legge quindi /chats, e per ogni
 * conversazione si prende l'ultimo messaggio (per l'anteprima) e
 * l'immagine del profilo da /user/avatar: per una persona come per un gruppo,
 * perche' quel parametro e' un JID e whatsmeow accetta qualunque JID.
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

// Un canale non e' una conversazione: non si puo' rispondere, e nell'elenco
// chat occupa il posto di una persona. GOWA li elenca, quindi si saltano qui.
function isChannelJid(jid) {
  return typeof jid === 'string' && jid.endsWith('@newsletter');
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
  const groupNames = opts.groupNames instanceof Map ? opts.groupNames : new Map();

  const chats = await gowa.chats(limit);
  const rows = [];

  for (const chat of chats) {
    if (!chat || !chat.jid) continue;
    if (isChannelJid(chat.jid)) continue;

    let last = null;
    try {
      last = newestMessage(await gowa.chatMessages(chat.jid, 10));
    } catch (err) {
      log('DEBUG', `Chats: messages of ${chat.jid} not readable (${err.message})`);
    }

    const isGroup = isGroupJid(chat.jid);
    // Per un gruppo si preferisce il nome vero da /user/my/groups: quello che
    // arriva con l'elenco delle conversazioni puo' essere il segnaposto
    // "Group <numero>" di GOWA, o il numero nudo.
    const name = (isGroup && groupNames.get(chat.jid)) || chat.name || displayNameForJid(chat.jid);

    // L'immagine del profilo si chiede per ogni conversazione: GOWA la sa dare
    // anche per un gruppo, perche' il suo parametro `phone` e' un JID e
    // whatsmeow accetta qualunque JID in una richiesta di profilo.
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

module.exports = { previewForMessage, collectChats, isChannelJid };
