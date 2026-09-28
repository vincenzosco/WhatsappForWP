'use strict';

// Le immagini del profilo gia' scaricate, tenute in memoria per un po'.
//
// Perche' esiste: l'elenco chat costa una richiesta HTTP per conversazione per
// l'ultimo messaggio e due per la sua immagine (una per l'indirizzo, una per i
// byte dal CDN di WhatsApp), e l'app lo richiede a ogni riconnessione e a ogni
// cambio di sezione una volta scaduto il minuto di cache di server.js. Gli
// stessi venti avatar si riscaricavano quindi piu' volte al giorno: lenti, e
// sono le richieste che WhatsApp guarda quando decide di limitare un account.
//
// I byte di una foto non cambiano sotto i piedi, quindi valgono qualche
// minuto. Un JID che una foto non ce l'ha vale meno, perche' quella si puo'
// aggiungere: la sua risposta scade prima.
//
// Limiti: mai piu' di maxEntries voci - un telefono con cinquanta
// conversazioni ne disegna comunque CHATS_LIMIT, quindi il resto e' memoria
// buttata. Una voce scaduta si butta al primo accesso: non c'e' nessun timer
// che gira, e la memoria si libera quando qualcuno guarda.

const DEFAULT_TTL_MS = 5 * 60 * 1000;
const DEFAULT_MISSING_TTL_MS = 60 * 1000;
const DEFAULT_MAX_ENTRIES = 60;

function createAvatarCache(options) {
  const opts = options || {};
  const ttlMs = typeof opts.ttlMs === 'number' ? opts.ttlMs : DEFAULT_TTL_MS;
  const missingTtlMs = typeof opts.missingTtlMs === 'number'
    ? opts.missingTtlMs
    : DEFAULT_MISSING_TTL_MS;
  const maxEntries = typeof opts.maxEntries === 'number' ? opts.maxEntries : DEFAULT_MAX_ENTRIES;
  const now = typeof opts.now === 'function' ? opts.now : () => Date.now();

  // Map conserva l'ordine di inserimento: la prima chiave e' la piu' vecchia,
  // ed e' quella che esce quando si e' sopra il tetto.
  const entries = new Map();

  return {
    /** L'immagine tenuta, null se la risposta era "non ce l'ha", undefined se non si sa. */
    get(key) {
      const entry = entries.get(key);
      if (!entry) return undefined;
      if (now() - entry.at > entry.ttl) {
        entries.delete(key);
        return undefined;
      }
      return entry.value;
    },

    /** Tiene una risposta. null e' una risposta: "questo JID non ha una foto". */
    put(key, value) {
      if (typeof key !== 'string' || key === '') return;

      entries.delete(key);
      entries.set(key, {
        value,
        at: now(),
        ttl: value === null || value === undefined ? missingTtlMs : ttlMs
      });

      while (entries.size > maxEntries) {
        const oldest = entries.keys().next().value;
        entries.delete(oldest);
      }
    },

    /** Quante voci si tengono adesso. Per i test e per la diagnosi. */
    size() {
      return entries.size;
    }
  };
}

module.exports = { createAvatarCache, DEFAULT_TTL_MS, DEFAULT_MISSING_TTL_MS, DEFAULT_MAX_ENTRIES };
