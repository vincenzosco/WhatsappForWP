'use strict';

// The profile pictures already downloaded, kept in memory for a while.
//
// Why it exists: the chat list costs one HTTP request per conversation for the
// last message and two for its picture (one for the address, one for the bytes
// from the WhatsApp CDN), and the app asks for it on every reconnection and on
// every section change once the minute of cache in server.js has expired. The
// same twenty avatars were therefore downloaded several times a day: slow, and
// they are the requests WhatsApp watches when it decides to rate-limit an
// account.
//
// The bytes of a picture do not change under your feet, so they are worth a few
// minutes. A JID that has no picture is worth less, because one can be added
// later: its answer expires sooner.
//
// Limits: never more than maxEntries entries - a phone with fifty conversations
// still draws only CHATS_LIMIT of them, so the rest is wasted memory. An expired
// entry is dropped on first access: no timer runs, and the memory is freed when
// somebody looks.

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

  // Map keeps insertion order: the first key is the oldest, and it is the one
  // that goes out when the ceiling is passed.
  const entries = new Map();

  return {
    /** The kept picture, null if the answer was "it has none", undefined if unknown. */
    get(key) {
      const entry = entries.get(key);
      if (!entry) return undefined;
      if (now() - entry.at > entry.ttl) {
        entries.delete(key);
        return undefined;
      }
      return entry.value;
    },

    /** Keeps an answer. null is an answer too: "this JID has no picture". */
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

    /** How many entries are kept right now. For the tests and for diagnosis. */
    size() {
      return entries.size;
    }
  };
}

module.exports = { createAvatarCache, DEFAULT_TTL_MS, DEFAULT_MISSING_TTL_MS, DEFAULT_MAX_ENTRIES };
