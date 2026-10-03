/**
 * ============================================================================
 *  WhatsApp Community Adapter v2.0
 * ============================================================================
 *  Replaces the old whatsapp-web.js bridge.
 *
 *  It sits between the Windows Phone 8.1 app and a self-hosted GOWA server
 *  (github.com/vincenzosco/go-whatsapp-web-multidevice):
 *
 *   - Encrypted TCP (AES-256-CBC + HMAC-SHA256) to the WP8 app, protocol
 *     unchanged except for the cipher tag at the head of the payload: the WP8.1
 *     app does not implement AES-GCM. The adapter also accepts GCM frames and
 *     answers each client with the cipher of that client.
 *   - HTTP to the GOWA REST API (QR login / phone login, status, text and image
 *     sending, contacts).
 *   - The webhook HTTP server that receives incoming messages from GOWA and
 *     forwards them to the WP8 app.
 *
 *  On the shared service one instance holds several accounts: every user has
 *  a GOWA device of their own (an `X-Device-Id`), and all the state of an
 *  account (chats, unread, login, caches) lives in a separate session. A
 *  webhook carries the `device_id` that produced it, so a message for one
 *  device cannot reach the sockets of another.
 *
 *  Control protocol (frame Type = 3, ChatId = "system"):
 *    app -> adapter : hello | status | login.qr | login.code | contacts | logout
 *    adapter -> app : state | qr | paircode | contact | error
 *
 *  Start:  npm install && npm start
 * ============================================================================
 */

'use strict';

const net = require('net');
const os = require('os');

const cryptoHelper = require('./crypto-helper');
const { loadConfig, applyDotEnv } = require('./config');
const { GowaClient } = require('./gowa-client');
const { buildChatMessage, mapWebhookMessage, mapHistoryMessage } = require('./message-format');
const { createWebhookServer } = require('./webhook-server');
const { createDiscoveryBeacon, buildPayload } = require('./discovery');
const { collectCalls } = require('./calls');
const { collectChats } = require('./chats');
const { createTranscoder } = require('./ffmpeg');
const { authenticate } = require('./auth');
const { createUserStore } = require('./users');

const LOG_TAGS = { INFO: '[INFO]', OK: '[OK]', WARN: '[WARN]', ERR: '[ERR]', MSG: '[MSG]', QR: '[QR]', NET: '[NET]' };

/**
 * Past this length the 4-byte prefix is not a payload, it is a fault (a
 * misaligned or hostile client). It must stay equal to
 * FrameCodec.MaxFrameLength in the WP8.1 app: the two sides speak
 * of the same frame, so they share the same ceiling.
 */
const MAX_FRAME_LENGTH = 8 * 1024 * 1024;

/**
 * How many base64 characters per frame to the app. The same number ChatPage
 * uses to send (media.begin/chunk/end): a video does not fit in a single frame,
 * and base64 adds a third. It is a multiple of 4, so every piece is valid
 * base64 on its own and the app can decode it without waiting for the rest.
 */
const MEDIA_CHUNK_CHARS = 700000;

function makeLogger(enabled) {
  return function log(level, ...args) {
    if (level === 'DEBUG' && !enabled) return;
    const ts = new Date().toISOString().replace('T', ' ').substring(0, 19);
    console.log(`${ts} ${LOG_TAGS[level] || ''}`, ...args);
  };
}

// The group names in one request. A missing name costs a name, not the whole
// list: if GOWA does not answer, an empty map comes back and the group rows keep
// whatever the conversation list said.
async function groupNamesOrEmpty(client, logger) {
  try {
    return await client.myGroups();
  } catch (err) {
    logger('DEBUG', `Chats: group names not readable (${err.message})`);
    return new Map();
  }
}

function createBridge({ config, gowa, log, debug, transcoder, users }) {
  const logger = typeof log === 'function' ? log : () => {};
  const dbg = typeof debug === 'function' ? debug : () => {};

  const authRequired = !!(config && config.auth && config.auth.required);
  const webhookPublicUrl = (config && config.webhook && config.webhook.publicUrl) || '';

  // Whether a phone that arrives without a token is handed one (see the `hello`
  // case). On by default: the shared service is meant to be usable by someone
  // who has no way to run `create-user.js` on the machine that hosts it. The
  // ceiling keeps an open service from growing a user per connection forever.
  const authRegister = !(config && config.auth && config.auth.register === false);
  const authMaxUsers = config && config.auth && Number.isFinite(config.auth.maxUsers)
    ? config.auth.maxUsers
    : 50;

  // Voice-note conversion: an ffmpeg found at startup, or the one the tests
  // inject. `enabled` comes from the configuration.
  const mediaTools = transcoder || createTranscoder({
    enabled: !config || !config.ffmpeg || config.ffmpeg.enabled !== false,
    path: config && config.ffmpeg ? config.ffmpeg.path : undefined,
    log: logger
  });

  // A session is one WhatsApp account with its GOWA device. The key '' is the
  // anonymous one: the private instance (or a client that has not handshaked
  // yet) and, in the tests, the only account that exists.
  const sessions = new Map();

  // The same sessions, by the account they are logged in as. GOWA names a
  // device by its UUID in `/devices`, but the `device_id` it puts on a webhook
  // is the WhatsApp JID of the logged-in account (`393892672185@s.whatsapp.net`),
  // and WhatsApp itself writes the device part into that JID
  // (`393892672185:92@s.whatsapp.net`). The two have to be matched as well as
  // the UUID, or every message arriving from WhatsApp is dropped for a device
  // the bridge has never heard of.
  const sessionsByJid = new Map();

  // The device part (`:92`) is not part of the account, so it is dropped before
  // two JIDs are compared.
  function jidKey(jid) {
    return String(jid || '').replace(/:[0-9]+@/, '@');
  }

  function newSession(key) {
    return {
      key,
      user: null,
      gowa,
      state: { status: 'disconnected', jid: '' },
      qrCache: null,
      callsCache: null,
      chatsCache: null,
      // The presence last sent toward WhatsApp ("available"/"unavailable"), so
      // that the polling loop does not repeat the same call. null = not known
      // yet, and a failed call puts it back here so the next round tries again.
      presenceSent: null,
      unreadByChat: new Map(),
      mediaTransfers: new Map(),
      pendingOutgoing: [],
      sockets: new Set()
    };
  }

  function sessionFor(key) {
    const k = key || '';
    let session = sessions.get(k);
    if (!session) {
      session = newSession(k);
      sessions.set(k, session);
    }
    return session;
  }

  const anonymous = sessionFor('');

  function userSessionKey(user) {
    return 'user:' + (user && user.id ? user.id : 'unknown');
  }

  /**
   * The session of an authenticated user, creating the GOWA device on the first
   * connection. The label is the user name: it is what shows in the GOWA
   * interface, and without a name the devices turn anonymous.
   *
   * If the GOWA client cannot create devices (in the tests, or on an old
   * server), the base client is used: the session is still separate by state
   * and by socket, which is what keeps the two conversations apart.
   */
  /**
   * The sessions of the users the store remembers, opened now instead of at the
   * first handshake.
   *
   * A webhook is routed to its user by the account JID, which is learned in
   * `refreshSession`, and a session no phone had ever opened had no JID at all:
   * after a restart every message was dropped as an unknown device until the app
   * connected once. That is precisely the case the unread counter exists for -
   * messages that arrive while the phone is off.
   */
  async function openKnownSessions() {
    if (!users || typeof users.all !== 'function') return;

    for (const user of users.all()) {
      try {
        await sessionForUser(user);
      } catch (err) {
        const who = user && user.name ? user.name : 'a user';
        logger('WARN', `session of ${who} not opened: ${err.message}`);
      }
    }
  }

  async function sessionForUser(user) {
    if (!user) return anonymous;

    const known = user.deviceId ? sessions.get(user.deviceId) : sessions.get(userSessionKey(user));
    if (known) {
      known.user = user;
      return known;
    }

    let deviceId = user.deviceId || '';
    if (!deviceId && typeof gowa.createDevice === 'function') {
      try {
        deviceId = await gowa.createDevice('wp8-' + (user.name || user.id));
        user.deviceId = deviceId;
        if (users && typeof users.save === 'function') users.save();
        logger('OK', `GOWA device created for ${user.name || user.id}: ${deviceId}`);
      } catch (err) {
        logger('WARN', `device creation failed for ${user.name || user.id}: ${err.message}`);
        deviceId = '';
      }
    }

    const client = deviceId && typeof gowa.withDevice === 'function'
      ? gowa.withDevice(deviceId)
      : gowa;

    const session = newSession(deviceId || userSessionKey(user));
    session.user = user;
    session.gowa = client;
    sessions.set(session.key, session);
    if (deviceId) sessions.set(deviceId, session);

    // The webhook must be registered on the new device, or this user's
    // messages arrive nowhere.
    if (deviceId && client && typeof client.setDeviceWebhook === 'function' && webhookPublicUrl) {
      try { await client.setDeviceWebhook(webhookPublicUrl); }
      catch (err) { logger('WARN', `webhook registration failed for device ${deviceId}: ${err.message}`); }
    }

    return session;
  }

  // How many messages of every chat are still unread. It lives here and not in
  // GOWA: its chat list has no such field, and a message that arrives with the
  // phone off is seen by nobody else. It is cleared by the `read` command.

  // WhatsApp refuses past 64 MB (uncompressed): past that number the bytes in
  // memory are of use to no one, so they stop earlier.
  const MAX_MEDIA_BYTES = 64 * 1024 * 1024;
  // How many transfers may be open at once. The app opens one at a time; the
  // number keeps the pieces of a client that opens a transfer and never closes
  // it out of memory (there is no media.end that cleans it up, and every piece
  // stays there).
  const MAX_LIVE_TRANSFERS = 4;

  async function sendChats(session) {
    const limits = (config && config.chats) || {};

    if (session.state.status !== 'connected') {
      sendControl(session, { command: 'error', text: 'WhatsApp is not connected: the chat list is unavailable.' });
      sendControl(session, { command: 'chats.done' });
      return;
    }

    try {
      const fresh = !session.chatsCache || Date.now() - session.chatsCache.at > 60000;
      if (fresh) {
        logger('INFO', `reading up to ${limits.limit || 25} conversation(s)...`);
        const groupNames = await groupNamesOrEmpty(session.gowa, logger);
        const rows = await collectChats({
          gowa: session.gowa,
          limit: limits.limit,
          avatars: limits.avatars,
          groupNames,
          log: logger
        });
        session.chatsCache = { at: Date.now(), rows };
      }

      for (const row of session.chatsCache.rows) {
        sendControl(session, {
          command: 'chat',
          chatId: row.chatId,
          senderName: row.name || undefined,
          text: row.preview || '',
          timestamp: row.timestamp || undefined,
          isGroup: row.isGroup,
          avatarData: row.avatar || undefined,
          unreadCount: session.unreadByChat.get(row.chatId) || 0
        });
      }
    } catch (err) {
      logger('ERR', `chat list failed: ${err.message}`);
      sendControl(session, { command: 'error', text: `Chat list failed: ${err.message}` });
    } finally {
      sendControl(session, { command: 'chats.done' });
    }
  }

  /**
   * The history of one chat, as message frames.
   *
   * It is not one frame per chat like `chats`: it is one frame per message, so
   * the limit is how many frames pass. Every frame carries `IsHistory`, because
   * the app must draw it but not count it as unread.
   */
  async function sendMessages(session, chatId) {
    if (!chatId) return;

    if (session.state.status !== 'connected') {
      sendControl(session, { command: 'error', chatId, text: 'WhatsApp is not connected: the chat history is unavailable.' });
      return;
    }

    const limits = (config && config.messages) || {};
    try {
      const list = await session.gowa.chatMessages(chatId, limits.limit || 50);
      let sent = 0;

      for (const raw of list) {
        const mapped = mapHistoryMessage(raw);
        // Without the WhatsApp id the app cannot recognize a duplicate, and
        // reopening the chat would pile up copies: better one message less than
        // a list that grows on its own.
        if (!mapped.id) continue;
        if (!mapped.chatId) mapped.chatId = chatId;

        sendControl(session, mapped);
        sent++;
      }

      // The burst has an end on the wire, so the app can bind the list once,
      // after it, instead of once per frame: a bound ListView re-lays out on
      // every insert, and a whole burst of inserts inside one navigation is what
      // left the phone unresponsive. ChatId carries the chat, like the other
      // control frames; an empty history still sends it, so the list binds at once.
      sendControl(session, { command: 'history.done', chatId });

      logger('INFO', `history: ${sent} message(s) for ${chatId}`);
    } catch (err) {
      logger('ERR', `history failed for ${chatId}: ${err.message}`);
      sendControl(session, { command: 'error', chatId, text: `Chat history failed: ${err.message}` });
    }
  }

  /**
   * The bytes of a media the app already has (a history row that arrived as a
   * word), in pieces. A video does not fit in one frame: the ceiling is 8 MiB
   * and base64 adds a third. Every frame is a `media` with the same
   * RelatedMessageId, the piece and how many there are in all; the app reassembles
   * them. These are control frames and not messages, because they complete a
   * message that already exists and one more message would raise the unread count.
   */
  function sendMediaChunks(session, chatId, messageId, mediaType, mimeType, fileName, base64) {
    const total = Math.max(1, Math.ceil(base64.length / MEDIA_CHUNK_CHARS));
    for (let i = 0; i < total; i++) {
      sendControl(session, {
        command: 'media',
        chatId,
        relatedMessageId: messageId,
        mediaType,
        mediaData: base64.substr(i * MEDIA_CHUNK_CHARS, MEDIA_CHUNK_CHARS),
        mediaMimeType: mimeType,
        mediaFileName: fileName || undefined,
        mediaChunkIndex: i,
        mediaChunkTotal: total
      });
    }
  }

  /**
   * The bytes to send to the app for a received media. An audio WP8.1 cannot
   * read (Ogg/Opus) becomes MP3; everything else passes unchanged, and a voice
   * note does the same when ffmpeg is absent or the conversion fails.
   */
  async function playableMedia(buffer, mediaType, mimeType, fileName) {
    if (mediaType !== 'audio') return { buffer, mimeType, fileName };
    const converted = await mediaTools.toPlayable(buffer, mimeType, fileName);
    if (!converted) return { buffer, mimeType, fileName };
    return converted;
  }

  async function sendMedia(session, chatId, messageId) {
    if (!chatId || !messageId) return;

    if (session.state.status !== 'connected') {
      sendControl(session, { command: 'error', chatId, text: 'WhatsApp is not connected: the media is unavailable.' });
      return;
    }

    try {
      const media = await session.gowa.downloadMedia(chatId, messageId);
      if (!media) {
        // The file is gone: say so, instead of leaving the bubble waiting
        // forever (see the command test).
        sendControl(session, {
          command: 'error',
          chatId,
          relatedMessageId: messageId,
          text: 'This media is no longer available on the server.'
        });
        return;
      }

      const kind = mediaKindOf(media.mimeType, media.fileName);
      const playable = await playableMedia(Buffer.from(media.base64, 'base64'), kind,
        media.mimeType, media.fileName);
      sendMediaChunks(session, chatId, messageId, kind, playable.mimeType, playable.fileName,
        playable.buffer.toString('base64'));
      logger('INFO', `media downloaded for ${messageId} (${media.base64.length} chars)`);
    } catch (err) {
      logger('ERR', `media download failed for ${messageId}: ${err.message}`);
      sendControl(session, {
        command: 'error',
        chatId,
        relatedMessageId: messageId,
        text: `Media download failed: ${err.message}`
      });
    }
  }

  async function sendCalls(session) {
    const limits = (config && config.calls) || {};

    if (session.state.status !== 'connected') {
      sendControl(session, { command: 'error', text: 'WhatsApp is not connected: call records are unavailable.' });
      sendControl(session, { command: 'calls.done' });
      return;
    }

    try {
      const fresh = !session.callsCache || Date.now() - session.callsCache.at > 60000;
      if (fresh) {
        logger('INFO', `scanning up to ${limits.chatLimit || 25} chats for call records...`);
        const entries = await collectCalls({
          gowa: session.gowa,
          chatLimit: limits.chatLimit,
          messagesPerChat: limits.messagesPerChat,
          limit: limits.limit,
          log: logger
        });
        session.callsCache = { at: Date.now(), entries };
      }

      for (const call of session.callsCache.entries) {
        sendControl(session, {
          command: 'call',
          chatId: call.chatId,
          senderName: call.chatName || undefined,
          timestamp: call.timestamp || undefined,
          callId: call.callId || undefined,
          callReason: call.reason || undefined,
          callDurationSeconds: call.durationSeconds,
          callIsVideo: call.isVideo
        });
      }
    } catch (err) {
      logger('ERR', `call scan failed: ${err.message}`);
      sendControl(session, { command: 'error', text: `Call scan failed: ${err.message}` });
    } finally {
      sendControl(session, { command: 'calls.done' });
    }
  }

  // ─── Sending to the WP8 app ───────────────────────────────────────────────
  //
  // Every socket remembers which cipher the client wrote with (wp8Cipher) and
  // receives replies with the same: a WP8.1 phone cannot do AES-GCM and must be
  // able to read everything, a client capable of GCM must not degrade.

  function frameFor(socket, jsonObject) {
    const tag = socket.wp8Cipher || cryptoHelper.DEFAULT_CIPHER_TAG;
    return cryptoHelper.buildFrame(JSON.stringify(jsonObject), tag);
  }

  function sendToClient(socket, msg) {
    try { socket.write(frameFor(socket, msg)); } catch (e) { /* socket morto */ }
  }

  function sendToClients(session, msg) {
    if (!session || session.sockets.size === 0) return;
    const json = JSON.stringify(msg);
    // One frame per distinct cipher, not one per socket: the CBC clients (in
    // practice all of them) share the same buffer.
    const packets = {};
    const dead = [];
    for (const socket of session.sockets) {
      const tag = socket.wp8Cipher || cryptoHelper.DEFAULT_CIPHER_TAG;
      if (!packets[tag]) packets[tag] = cryptoHelper.buildFrame(json, tag);
      try { socket.write(packets[tag]); } catch (e) { dead.push(socket); }
    }
    for (const socket of dead) session.sockets.delete(socket);
  }

  function broadcastState(session) {
    sendToClients(session, buildChatMessage({
      command: 'state',
      state: session.state.status,
      accountJid: session.state.jid || undefined,
      chatId: 'system',
      isIncoming: true
    }));
  }

  function sendControl(session, fields) {
    sendToClients(session, buildChatMessage(Object.assign({ chatId: 'system', isIncoming: true }, fields)));
  }

  // ─── State and login ──────────────────────────────────────────────────────

  /**
   * How many phones are really watching this account: the sockets that got as
   * far as the handshake.
   *
   * A connection that only opens the port and goes away is not a client. The
   * healthcheck of the container does exactly that every thirty seconds, and so
   * would a stray connection: counting them would make our presence blink to
   * `available` and back for the contacts, which is a state nobody asked for -
   * the same reason the green "online" dot was taken off the screens.
   *
   * A phone that went to the background is not watching either. WP8.1 freezes
   * the process without closing the socket, so a suspended app and a connected
   * one look identical from here: the phone says `presence`/paused as it
   * suspends, and this flag is what turns that sentence into "offline".
   */
  function watchingCount(session) {
    let count = 0;
    for (const socket of session.sockets) if (socket.handshaken && !socket.paused) count++;
    return count;
  }

  /**
   * Our own presence on WhatsApp: online while a phone is watching the account,
   * unavailable when nobody is.
   *
   * It is what makes typing notifications arrive at all: WhatsApp sends
   * `chat_presence` only to a client that is marked online, and GOWA connects as
   * "unavailable" with a daily five-minute pulse. Being online exactly while the
   * app is connected is also the honest thing for the contacts to see.
   *
   * A failure is not fatal: without presence the bridge works as before, only
   * without typing indicators, so it is logged and forgotten.
   */
  async function updatePresence(session) {
    if (!session || !session.gowa) return;

    // GOWA cannot set a presence without a PushName, which only exists once the
    // account is linked: asking anyway makes it panic in its middleware every
    // poll and fills the log with a failure that means nothing. The account is
    // not online yet, so there is nothing to say either way.
    if (session.state.status !== 'connected') return;

    const watching = watchingCount(session) > 0;
    // Nobody is watching and nothing was ever said: there is nothing to correct,
    // because GOWA connects as "unavailable" by itself. It is what keeps the
    // anonymous session of a shared instance from talking to WhatsApp at all.
    if (!watching && session.presenceSent === null) return;

    const wanted = watching && session.state.status === 'connected' ? 'available' : 'unavailable';
    if (session.presenceSent === wanted) return;

    session.presenceSent = wanted;
    try {
      await session.gowa.sendPresence(wanted);
      logger('INFO', `presence sent: ${wanted}`);
    } catch (err) {
      session.presenceSent = null;
      logger('WARN', `presence ${wanted} not sent: ${err.message}`);
    }
  }

  async function refreshSession(session) {
    try {
      const s = await session.gowa.status();
      const next = s.isLoggedIn ? 'connected' : (session.state.status === 'waiting' ? 'waiting' : 'disconnected');
      const changed = next !== session.state.status || (s.jid || '') !== session.state.jid;
      session.state = { status: next, jid: s.jid || '' };

      // Keep the JID index current: it is what routes this account's webhooks,
      // and refreshSession is where the account becomes known.
      const account = jidKey(session.state.jid);
      if (account && session.jidKey !== account) {
        if (session.jidKey) sessionsByJid.delete(session.jidKey);
        sessionsByJid.set(account, session);
        session.jidKey = account;
      }

      if (next === 'connected') {
        session.qrCache = null;
        session.callsCache = null;
        session.chatsCache = null;
        if (changed) {
          broadcastState(session);
          logger('OK', `WhatsApp connected as ${session.state.jid || 'unknown'}`);
          await flushPending(session);
          await sendChats(session);
          await syncContacts(session);
        }
      } else if (changed) {
        broadcastState(session);
      }

      // Right after the state is learned, and only when it really is: the
      // account goes online while a phone watches it and offline when the last
      // one leaves. `updatePresence` does nothing when it has already been said.
      await updatePresence(session);
    } catch (err) {
      dbg(`status unavailable: ${err.message}`);
    }
  }

  // Every session a phone is attached to, every session whose account is not
  // known yet, plus the anonymous one of a private instance.
  //
  // This used to be the anonymous session only, and a user session was never
  // read: a phone that had just finished the QR login stayed on "waiting"
  // forever - the app asks for the chat list only once it is told the account is
  // connected -, its messages were queued as "WhatsApp not ready", and the list
  // on screen stayed the cached one of the previous session.
  //
  // A session with no JID is read even with no phone on it, because the JID is
  // what routes its webhooks: one that is skipped stays unroutable, and an
  // account that logs in while the phone is off would never be found. Once the
  // JID is known, only a watched session is read - there is no one to tell, and
  // reading it again would fetch the chat list and its pictures for nobody.
  function sessionsToRefresh() {
    const live = new Set();
    live.add(anonymous);
    for (const session of sessions.values()) {
      if (session.sockets.size > 0 || !session.jidKey) live.add(session);
    }
    return Array.from(live);
  }

  // The state of the private instance and of every session in use: the polling
  // loop of main() watches this.
  const refreshStatus = async () => {
    for (const session of sessionsToRefresh()) await refreshSession(session);
  };

  async function requestQr(session) {
    if (session.state.status === 'connected') { broadcastState(session); return; }
    try {
      const now = Date.now();
      if (session.qrCache && session.qrCache.expiresAt > now) {
        sendControl(session, { command: 'qr', qrImageData: session.qrCache.base64, qrDuration: session.qrCache.duration });
        return;
      }
      const { qrLink, duration } = await session.gowa.loginQr();
      const image = await session.gowa.fetchBinary(qrLink);
      const base64 = image.buffer.toString('base64');
      session.qrCache = { base64, duration, expiresAt: now + duration * 1000 };
      session.state = { status: 'waiting', jid: '' };
      // The picture is sent before the state, so the app shows it right away.
      sendControl(session, { command: 'qr', qrImageData: base64, qrDuration: duration });
      broadcastState(session);
      logger('QR', 'new QR code sent to the app');
    } catch (err) {
      logger('ERR', `QR login failed: ${err.message}`);
      sendControl(session, { command: 'error', text: `Login QR fallito: ${err.message}` });
    }
  }

  async function requestPairCode(session, phone) {
    if (!phone) {
      sendControl(session, { command: 'error', text: 'Numero di telefono mancante' });
      return;
    }
    if (session.state.status === 'connected') { broadcastState(session); return; }
    try {
      const code = await session.gowa.loginWithCode(phone);
      session.state = { status: 'waiting', jid: '' };
      sendControl(session, { command: 'paircode', pairCode: code });
      broadcastState(session);
      logger('QR', `pairing code sent to the app for ${phone}`);
    } catch (err) {
      logger('ERR', `code login failed: ${err.message}`);
      sendControl(session, { command: 'error', text: `Login con codice fallito: ${err.message}` });
    }
  }

  async function syncContacts(session) {
    try {
      const contacts = await session.gowa.contacts();
      for (const contact of contacts) {
        if (!contact.jid) continue;
        sendControl(session, { command: 'contact', chatId: contact.jid, senderName: contact.name || undefined });
      }
      logger('INFO', `synced ${contacts.length} contacts`);
    } catch (err) {
      logger('WARN', `contact sync failed: ${err.message}`);
    }
  }

  /// The readable number of a JID (e.g. +393401234567). Empty for a group.
  function numberForJid(jid) {
    const user = String(jid || '').split('@')[0].split(':')[0];
    return /^\d+$/.test(user) ? '+' + user : '';
  }

  /// The business profile in the shape the app expects, or null.
  function businessFrom(profile) {
    if (!profile) return null;
    return {
      Email: profile.email || '',
      Address: profile.address || '',
      Categories: profile.categories || [],
      Timezone: profile.timezone || '',
      Hours: (profile.hours || []).map(function (h) {
        return {
          Day: h.day_of_week === undefined || h.day_of_week === null ? '' : String(h.day_of_week),
          Mode: h.mode || '',
          Open: h.open_time || '',
          Close: h.close_time || ''
        };
      })
    };
  }

  /// A group member as the app shows it: there is always a name, even when
  /// WhatsApp sends only one for a number.
  function groupMember(p) {
    return {
      Jid: p.jid || '',
      Number: p.phoneNumber || numberForJid(p.jid),
      Name: p.displayName || numberForJid(p.jid) || p.jid || '',
      IsAdmin: p.isAdmin === true,
      IsSuperAdmin: p.isSuperAdmin === true
    };
  }

  /**
   * The information of one profile, in a single control frame.
   *
   * Three upstream requests (the profile and, for a business, the business
   * profile; for a group, the members and the description) and one answer only:
   * the app asks one thing and waits for one thing. The picture is carried by
   * `avatar`, which already has its cache, so the large page has it even for a
   * chat whose list did not.
   *
   * Any failure becomes an empty profile: the page must stop waiting, not stay
   * loading forever.
   */
  async function sendContactInfo(session, jid) {
    if (!jid) return;

    const info = {
      Name: '', About: '', Number: '', AvatarData: '', Business: null, Group: null
    };

    try {
      info.AvatarData = (await session.gowa.avatar(jid)) || '';

      if (jid.endsWith('@g.us')) {
        const participants = await session.gowa.groupParticipants(jid);
        const description = await session.gowa.groupInfo(jid);
        info.Group = {
          Description: (description && description.topic) || '',
          Members: participants && participants.participants
            ? participants.participants.map(groupMember)
            : []
        };
        if (participants && participants.name) info.Name = participants.name;
      } else {
        const user = await session.gowa.userInfo(jid);
        if (user) {
          info.Name = user.name || user.verifiedName || '';
          info.About = user.status || '';
        }
        info.Number = numberForJid(jid);
        info.Business = businessFrom(await session.gowa.businessProfile(jid));
      }
    } catch (err) {
      logger('WARN', `contact info failed for ${jid}: ${err.message}`);
    }

    sendControl(session, { command: 'contact.info', chatId: jid, text: JSON.stringify(info) });
  }

  // ─── Messages from the app to WhatsApp ────────────────────────────────────

  /// The right path for an attachment, from the MIME type (or the extension
  /// when the type is missing): image, video, audio, otherwise document. The
  /// type that comes out also travels to the app, which uses it to decide how
  /// to draw the bubble (see ChatMessage.IsAudio / IsDocument).
  function mediaKindOf(mimeType, fileName) {
    const mime = String(mimeType || '').toLowerCase();
    const name = String(fileName || '').toLowerCase();
    if (mime.indexOf('video/') === 0 || /\.(mp4|mov|3gp|avi|mkv|webm)$/.test(name)) return 'video';
    if (mime.indexOf('image/') === 0) return 'image';
    if (mime.indexOf('audio/') === 0 || /\.(ogg|opus|oga|mp3|m4a|aac|amr|wav)$/.test(name)) return 'audio';
    return 'document';
  }

  async function sendMediaToGowa(session, chatId, caption, buffer, mimeType, fileName) {
    const kind = mediaKindOf(mimeType, fileName);

    // A video that is still large here was not shrunk on the phone: the app
    // could not (no transcoder on the device, or the conversion failed), and
    // what would reach WhatsApp is the whole camera file. It is shrunk now,
    // and only when that makes it smaller: see ffmpeg.toSmallerVideo.
    if (kind === 'video' && mediaTools && typeof mediaTools.toSmallerVideo === 'function') {
      const smaller = await mediaTools.toSmallerVideo(buffer, mimeType, fileName);
      if (smaller) {
        logger('INFO', `video shrunk: ${buffer.length} -> ${smaller.buffer.length} bytes`);
        buffer = smaller.buffer;
        mimeType = smaller.mimeType;
        fileName = smaller.fileName;
      }
    }

    if (kind === 'video') return session.gowa.sendVideo(chatId, caption || '', buffer, mimeType || 'video/mp4', fileName);
    if (kind === 'image') return session.gowa.sendImage(chatId, caption || '', buffer, mimeType || 'image/jpeg', fileName);
    // An audio payload is a voice note, and only /send/audio makes it one: it
    // is sent there when the client knows the route, and as a file otherwise
    // (an adapter talking to an older GOWA must keep working).
    if (kind === 'audio' && typeof session.gowa.sendAudio === 'function') {
      return session.gowa.sendAudio(chatId, caption || '', buffer, mimeType || 'audio/ogg', fileName || 'voice-note.ogg');
    }
    return session.gowa.sendFile(chatId, caption || '', buffer, mimeType || 'application/octet-stream', fileName);
  }

  function mediaBegin(session, msg) {
    if (!msg.MediaTransferId) return;

    // The declared total is what makes the end verifiable: without it, an
    // attachment missing a piece is indistinguishable from a whole one.
    const declared = Number(msg.MediaChunkTotal);
    const chunkTotal = Number.isInteger(declared) && declared > 0 ? declared : null;

    // The same id twice: the second command starts from scratch instead of
    // adding to the first.
    session.mediaTransfers.delete(msg.MediaTransferId);

    // A transfer never closed does not pile up forever: the oldest pays for
    // the new one.
    if (session.mediaTransfers.size >= MAX_LIVE_TRANSFERS) {
      const oldest = session.mediaTransfers.keys().next();
      if (!oldest.done) {
        logger('WARN', `too many open attachments: dropping ${oldest.value}`);
        session.mediaTransfers.delete(oldest.value);
      }
    }

    session.mediaTransfers.set(msg.MediaTransferId, {
      chatId: msg.ChatId,
      messageId: msg.RelatedMessageId || null,
      fileName: msg.MediaFileName || null,
      mimeType: msg.MediaMimeType || null,
      chunkTotal,
      bytes: 0,
      parts: []
    });
  }

  function mediaChunk(session, msg) {
    const transfer = session.mediaTransfers.get(msg.MediaTransferId);
    if (!transfer) return;

    const index = Number(msg.MediaChunkIndex);
    if (!Number.isInteger(index) || index < 0) return;

    // Outside the declared range it is not a piece of this file: using it as
    // an array index meant an array with two billion holes, which filter walks
    // through all of.
    if (transfer.chunkTotal !== null && index >= transfer.chunkTotal) {
      logger('WARN', `attachment piece ${index} is outside 0..${transfer.chunkTotal - 1}, ignored`);
      return;
    }

    // Every piece is a multiple of 4 base64 characters: decoding it alone and
    // concatenating the bytes gives exactly the whole file.
    const part = Buffer.from(msg.MediaData || '', 'base64');

    // The ceiling is checked while the bytes arrive, not after holding them
    // all in memory.
    if (transfer.bytes + part.length > MAX_MEDIA_BYTES) {
      session.mediaTransfers.delete(msg.MediaTransferId);
      logger('WARN', `attachment over ${MAX_MEDIA_BYTES} bytes, refused while arriving`);
      sendControl(session, {
        command: 'error',
        chatId: transfer.chatId,
        relatedMessageId: transfer.messageId || undefined,
        text: 'The file is too large to send.'
      });
      return;
    }

    transfer.bytes += part.length;
    transfer.parts[index] = part;
  }

  async function mediaEnd(session, msg) {
    const transfer = session.mediaTransfers.get(msg.MediaTransferId);
    if (!transfer) return;
    session.mediaTransfers.delete(msg.MediaTransferId);

    const parts = transfer.parts.filter((part) => part);
    if (parts.length === 0) return;

    // A missing piece is a fault, not a shorter file: sending half a video
    // without saying so is worse than not sending it.
    if (transfer.chunkTotal !== null && parts.length !== transfer.chunkTotal) {
      logger('WARN', `attachment incomplete: ${parts.length} of ${transfer.chunkTotal} pieces`);
      sendControl(session, {
        command: 'error',
        chatId: transfer.chatId,
        relatedMessageId: transfer.messageId || undefined,
        text: 'The attachment did not arrive complete: send it again.'
      });
      return;
    }

    const buffer = Buffer.concat(parts);

    if (buffer.length > MAX_MEDIA_BYTES) {
      logger('WARN', `attachment too large (${buffer.length} bytes), refused`);
      sendControl(session, { command: 'error', chatId: transfer.chatId, text: 'The file is too large to send.' });
      return;
    }

    if (session.state.status !== 'connected') {
      // Like a text message: held aside and sent at connection time.
      session.pendingOutgoing.push({
        ChatId: transfer.chatId,
        Text: msg.Text || '',
        MediaData: buffer.toString('base64'),
        MediaMimeType: transfer.mimeType,
        MediaFileName: transfer.fileName
      });
      logger('INFO', 'WhatsApp not ready: attachment queued');
      return;
    }

    try {
      logger('MSG', `attachment to ${transfer.chatId}: ${buffer.length} bytes (${mediaKindOf(transfer.mimeType, transfer.fileName)})`);
      await sendMediaToGowa(session, transfer.chatId, msg.Text, buffer, transfer.mimeType, transfer.fileName);
    } catch (err) {
      logger('ERR', `attachment to ${transfer.chatId} failed: ${err.message}`);
      sendControl(session, { command: 'error', chatId: transfer.chatId, text: `Send failed: ${err.message}` });
    }
  }

  async function sendOutgoing(session, msg) {
    try {
      if (msg.MediaData) {
        // An old version of the app sends the attachment inside the message:
        // still accepted, but on the right path.
        await sendMediaToGowa(session, msg.ChatId, msg.Text, Buffer.from(msg.MediaData, 'base64'),
          msg.MediaMimeType, msg.MediaFileName);
      } else if (msg.Text && msg.Text.trim()) {
        await session.gowa.sendText(msg.ChatId, msg.Text);
      }
      logger('MSG', `sent to ${msg.ChatId}: ${(msg.Text || '[media]').substring(0, 40)}`);
    } catch (err) {
      logger('ERR', `send to ${msg.ChatId} failed: ${err.message}`);
      sendControl(session, { command: 'error', chatId: msg.ChatId, text: `Send failed: ${err.message}` });
    }
  }

  async function flushPending(session) {
    if (session.pendingOutgoing.length === 0) return;
    const queued = session.pendingOutgoing.splice(0, session.pendingOutgoing.length);
    logger('INFO', `flushing ${queued.length} queued message(s)...`);
    for (const msg of queued) await sendOutgoing(session, msg);
  }

  async function handleUserMessage(msg, socket) {
    const session = socket && socket.session ? socket.session : anonymous;

    if ((!msg.Text || !msg.Text.trim()) && !msg.MediaData) {
      logger('WARN', 'empty message from the app, ignored');
      return;
    }
    if (session.state.status !== 'connected') {
      session.pendingOutgoing.push(msg);
      logger('INFO', 'WhatsApp not ready: message queued');
      sendControl(session, { chatId: msg.ChatId, text: 'WhatsApp is not connected yet. The message will be sent automatically.' });
      return;
    }
    await sendOutgoing(session, msg);
  }

  // ─── Messages from WhatsApp to the app ────────────────────────────────────

  /**
   * The session that produced an event. GOWA puts `device_id` at the top of
   * every webhook (docs/webhook-payload.md): that is what keeps the accounts
   * apart. Without it, on a private instance it falls back to the only session;
   * on the shared service it is not guessed and the event is dropped, because
   * sending it to everyone would be worse than not sending it.
   */
  function sessionForEvent(event) {
    const deviceId = event && (event.device_id || event.deviceId);
    if (deviceId && sessions.has(deviceId)) return sessions.get(deviceId);
    // GOWA sends the account JID here, not the UUID it lists in `/devices`:
    // both names have to be tried before giving up on the event.
    if (deviceId) {
      const byJid = sessionsByJid.get(jidKey(deviceId));
      if (byJid) return byJid;
    }
    if (!authRequired) return anonymous;
    if (deviceId) logger('WARN', `webhook for an unknown device (${deviceId}), ignored`);
    else logger('WARN', 'webhook without a device id on a shared server, ignored');
    return null;
  }

  async function handleWebhookEvent(event) {
    if (!event) return;
    const session = sessionForEvent(event);
    if (!session) return;

    if (event.event === 'message.revoked') {
      const payload = event.payload || {};
      const id = payload.revoked_message_id;
      if (!id) return;
      const chatId = payload.revoked_chat || payload.chat_id || payload.from || '0';
      logger('MSG', `message revoked on WhatsApp: ${id}`);
      sendControl(session, { command: 'revoked', chatId, relatedMessageId: id });
      return;
    }

    if (event.event === 'message.edited') {
      const payload = event.payload || {};
      const id = payload.original_message_id;
      if (!id || typeof payload.body !== 'string') return;
      const chatId = payload.chat_id || payload.from || '0';
      logger('MSG', `message edited on WhatsApp: ${id}`);
      sendControl(session, { command: 'edited', chatId, relatedMessageId: id, text: payload.body });
      return;
    }

    // Typing notifications, in either direction of the same notion: composing /
    // paused here, and the app draws them as a bubble with three dots.
    if (event.event === 'chat_presence') {
      const payload = event.payload || {};
      const chatId = payload.chat_id || payload.from;
      const state = payload.state;
      if (!chatId || (state !== 'composing' && state !== 'paused')) return;

      // Typing on our own account - our phone, or another linked device - is not
      // something the app shows, and WhatsApp does not show it either.
      if (session.jidKey && jidKey(payload.from) === session.jidKey) return;

      sendControl(session, { command: 'typing', chatId, state });
      return;
    }

    // message.reaction and future types stay ignored: the app has nowhere to
    // show them.
    if (event.event !== 'message') return;
    const fields = mapWebhookMessage(event.payload || {});
    if (!fields) return;

    // A message that is not mine has arrived now: its chat has one more thing
    // to read, even if the app is not connected at this moment.
    session.unreadByChat.set(fields.chatId, (session.unreadByChat.get(fields.chatId) || 0) + 1);

    let mediaBuffer = null;
    let mediaMimeType = fields.mediaMimeType;
    if (fields.mediaPath) {
      try {
        const media = await session.gowa.fetchBinary(fields.mediaPath);
        mediaBuffer = media.buffer;
        if (!mediaMimeType) mediaMimeType = media.contentType;
      } catch (err) {
        logger('WARN', `media not downloaded (${fields.mediaPath}): ${err.message}`);
      }
    }

    // A voice note arrives Ogg/Opus and the phone cannot read it: it is
    // converted before being split toward the app.
    if (mediaBuffer && fields.mediaType === 'audio') {
      const playable = await playableMedia(mediaBuffer, 'audio', mediaMimeType, fields.mediaFileName);
      mediaBuffer = playable.buffer;
      mediaMimeType = playable.mimeType;
      fields.mediaFileName = playable.fileName;
    }

    // A large media is sent in pieces, after the message and tied to its id
    // (sendMediaChunks). Only a media without an id - which the app could not
    // even ask for - travels inside the message, as before.
    const inlineMedia = mediaBuffer && !fields.id;

    logger('MSG', `from ${fields.senderName}: ${(fields.text || '[media]').substring(0, 60)}`);
    sendToClients(session, buildChatMessage({
      id: fields.id,
      text: fields.text,
      senderId: fields.senderId,
      senderName: fields.senderName,
      chatId: fields.chatId,
      timestamp: fields.timestamp,
      status: 3,
      type: fields.type,
      isIncoming: true,
      mediaType: fields.mediaType,
      mediaData: inlineMedia ? mediaBuffer.toString('base64') : null,
      mediaMimeType: inlineMedia ? mediaMimeType : undefined,
      mediaFileName: fields.mediaFileName
    }));

    if (mediaBuffer && fields.id) {
      sendMediaChunks(session, fields.chatId, fields.id, fields.mediaType, mediaMimeType,
        fields.mediaFileName, mediaBuffer.toString('base64'));
    }
  }

  // ─── Control protocol ─────────────────────────────────────────────────────

  function refuseUnauthorized(socket) {
    const refusal = buildChatMessage({ command: 'unauthorized', chatId: 'system', isIncoming: true });
    if (socket) {
      sendToClient(socket, refusal);
      if (typeof socket.destroy === 'function') socket.destroy();
    } else {
      sendToClients(anonymous, refusal);
    }
  }

  /// Moves a socket from one session to another (successful handshake).
  /**
   * A socket that is gone leaves the anonymous list and the session it had been
   * moved to.
   *
   * It used to be dropped from `anonymous` only: the session of a user kept the
   * dead socket, so it was counted as a watching client - and every frame
   * written to it was a write to a closed connection. That count is what decides
   * whether the account is online, so it has to be true.
   */
  function dropSocket(socket) {
    const session = socket && socket.session ? socket.session : anonymous;
    anonymous.sockets.delete(socket);
    if (session) session.sockets.delete(socket);
    updatePresence(session);
  }

  function moveSocket(socket, from, to) {
    if (!socket) return;
    if (from && from !== to) from.sockets.delete(socket);
    to.sockets.add(socket);
    socket.session = to;
  }

  async function handleControl(msg, socket) {
    switch (msg.Command) {
      case 'hello': {
        let verdict = authenticate({ frame: msg, users, authRequired });
        let created = null;

        // The phone's own id, stable for the life of the install. It is what the
        // token is derived from, so the same device always gets the same token
        // back instead of being registered again on every connection.
        const clientId = typeof msg.SenderId === 'string' ? msg.SenderId.trim() : '';

        // A phone with no token on a service that hands them out: the token is
        // created here, and the socket is authenticated in the same step. Waiting
        // for a second handshake would let the commands already in flight (the app
        // asks for the QR code right after connecting) arrive while the socket has
        // no user yet, and those are refused by closing the connection.
        if (!verdict.ok && authRegister && users
            && (verdict.reason === 'missing token' || verdict.reason === 'unknown token')) {
          // Room is only needed for a device the service has never seen: a device
          // it already knows is handed its own token again and never counts
          // against the ceiling.
          const known = clientId ? users.findByClientId(clientId) : null;
          if (known || users.count() < authMaxUsers) {
            created = users.register(clientId, msg.SenderName || 'device');
            verdict = { ok: true, user: created.user };
            logger('OK', created.existing
              ? `device returned: ${created.user.id} (${created.user.name})`
              : `device registered: ${created.user.id} (${created.user.name})`);
          }
        }

        if (!verdict.ok) {
          logger('WARN', `refused a handshake: ${verdict.reason}`);
          refuseUnauthorized(socket);
          break;
        }
        if (socket) socket.user = verdict.user || null;
        // A device that authenticated with its token is remembered as the
        // client of this user, so the derivation has something to key on if the
        // phone ever comes back without a token. Records written before the
        // token was derived have no client id, and this is what gives them one
        // on the next connect instead of letting that phone become a new user.
        if (verdict.user && clientId && users && typeof users.bindClientId === 'function'
            && !verdict.user.clientId) {
          users.bindClientId(verdict.user, clientId);
        }
        // The handshake is what turns a connection into a phone watching this
        // account: it is the only thing presence and the unread counter are
        // allowed to be based on.
        if (socket) socket.handshaken = true;
        // A fresh handshake is also the end of any previous suspension: the
        // reconnected phone is watching again.
        if (socket) socket.paused = false;
        // With a valid token the socket moves to the user's session; without
        // auth it stays (or returns) to the anonymous one of the private
        // instance.
        const session = verdict.user ? await sessionForUser(verdict.user) : anonymous;
        if (socket) moveSocket(socket, anonymous, session);

        // The token leaves exactly once per device: it is not stored, so this
        // frame is the only copy the phone will ever have. It is sent after the
        // session exists, so the app holds a token that already works. A device
        // that was already known is told nothing: it still holds its own token,
        // and re-sending it would only invite the app to overwrite what it has.
        if (created && !created.existing && socket) {
          sendToClient(socket, buildChatMessage({
            command: 'registered',
            token: created.token,
            senderName: created.user.name,
            chatId: 'system',
            isIncoming: true
          }));
        }

        logger('NET', `handshake from "${msg.SenderName || 'unknown'}"`);
        // Read GOWA now: this is where the account JID becomes known, and
        // without it an incoming message is routed to no session at all until
        // the next poll.
        await refreshSession(session);
        broadcastState(session);
        break;
      }
      default: {
        // A command before the handshake, on a service that asks for one, has
        // no session to belong to: it is refused instead of guessed.
        if (authRequired && (!socket || !socket.user)) {
          logger('WARN', `command ${msg.Command} before authentication, refused`);
          refuseUnauthorized(socket);
          break;
        }
        const session = socket && socket.session ? socket.session : anonymous;
        await handleCommand(session, msg, socket);
      }
    }
  }

  /**
   * Our own typing state toward WhatsApp: the chat JID travels in `Text` (as for
   * `messages`) and `State` is "composing" or "paused", the names of the
   * webhook. GOWA wants "start" or "stop": that is the only translation.
   */
  async function sendTypingPresence(session, chatId, state) {
    if (!chatId) return;
    if (state !== 'composing' && state !== 'paused') return;
    if (session.state.status !== 'connected') return;

    try {
      await session.gowa.sendChatPresence(chatId, state === 'composing' ? 'start' : 'stop');
    } catch (err) {
      logger('WARN', `typing ${state} not sent for ${chatId}: ${err.message}`);
    }
  }

  async function handleCommand(session, msg, socket) {
    switch (msg.Command) {
      case 'status':
        // The remembered state can be older than the truth, and after a QR or
        // code login only the phone knows it worked: this command is the moment
        // the adapter is told to look at GOWA. The frame is sent either way,
        // even when nothing changed: the app's watchdog reads it as the answer
        // that proves the socket is alive.
        await refreshSession(session);
        broadcastState(session);
        break;
      case 'login.qr':
        await requestQr(session);
        break;
      case 'login.code':
        await requestPairCode(session, (msg.Text || '').trim());
        break;
      case 'contacts':
        if (session.state.status === 'connected') await syncContacts(session);
        break;
      case 'calls':
        await sendCalls(session);
        break;
      case 'chats':
        await sendChats(session);
        break;
      case 'messages':
        // The JID travels in `Text`, as for `login.code`: that is the field the
        // control protocol uses for the accompanying datum, so a second kind of
        // outgoing frame is not needed.
        await sendMessages(session, (msg.Text || '').trim());
        break;
      case 'contact.info':
        // The JID travels in `Text`, as for `messages`: that is the field the
        // control protocol uses for the accompanying datum.
        await sendContactInfo(session, (msg.Text || '').trim());
        break;
      case 'read':
        // The app has shown that conversation: from now on it has nothing left
        // to read. The chat does not have to exist in the list.
        session.unreadByChat.delete((msg.Text || '').trim());
        break;
      case 'typing':
        await sendTypingPresence(session, (msg.Text || '').trim(), msg.State);
        break;
      case 'presence':
        // The phone tells us whether it is still watching. It is the only way
        // to tell a suspended app from an active one: the OS keeps the socket
        // open while it freezes the process, so without this frame the count
        // would stay at one and WhatsApp would keep showing the account online.
        if (socket) socket.paused = msg.State === 'paused';
        await updatePresence(session);
        break;
      case 'media.begin':
        mediaBegin(session, msg);
        break;
      case 'media.chunk':
        mediaChunk(session, msg);
        break;
      case 'media.end':
        await mediaEnd(session, msg);
        break;
      case 'media.get':
        // The chat JID in Text (as for `messages`), the message id in
        // RelatedMessageId: that is the field that says what a frame refers to.
        await sendMedia(session, (msg.Text || '').trim(), msg.RelatedMessageId);
        break;
      case 'logout':
        try { await session.gowa.logout(); } catch (e) { /* ignora */ }
        session.state = { status: 'disconnected', jid: '' };
        session.qrCache = null;
        broadcastState(session);
        // The account is not connected any more: it must not stay "online"
        // toward WhatsApp.
        await updatePresence(session);
        break;
      default:
        dbg(`unknown command: ${msg.Command}`);
    }
  }

  // ─── TCP server ───────────────────────────────────────────────────────────

  const tcpServer = net.createServer((socket) => {
    const remote = `${socket.remoteAddress}:${socket.remotePort}`;
    logger('NET', `app client connected: ${remote}`);
    socket.session = anonymous;
    anonymous.sockets.add(socket);

    // Until the client writes we do not know what it can read: we start from
    // the cipher everyone can read.
    socket.wp8Cipher = cryptoHelper.DEFAULT_CIPHER_TAG;

    // The frames of one socket are handled in the order they arrived (see the
    // `data` handler): this is the chain that enforces it.
    socket.chain = Promise.resolve();

    // On the private instance the state arrives at once. On the shared one it
    // does not: before the handshake we do not know whose socket it is, and the
    // instance state is not its own.
    if (!authRequired) {
      sendToClient(socket, buildChatMessage({
        command: 'state',
        state: anonymous.state.status,
        accountJid: anonymous.state.jid || undefined,
        chatId: 'system',
        isIncoming: true
      }));
    }

    let buffer = Buffer.alloc(0);
    socket.on('data', (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      while (buffer.length >= 4) {
        const msgLen = buffer.readUInt32LE(0);

        // A misaligned client announces a huge length: without a ceiling the
        // server would wait for gigabytes and the buffer would grow until the
        // process falls over. Zero is the other degenerate case: an empty frame
        // would spin the loop without consuming anything.
        if (msgLen === 0 || msgLen > MAX_FRAME_LENGTH) {
          logger('ERR', `unacceptable frame from ${remote} (length ${msgLen}): closing the connection`);
          socket.destroy();
          return;
        }

        if (buffer.length < 4 + msgLen) break;
        const payload = buffer.slice(4, 4 + msgLen);
        buffer = buffer.slice(4 + msgLen);
        try {
          // The tag of the frame just received says which cipher it was
          // written with: from here on it is answered with the same one.
          const tag = cryptoHelper.cipherTagOf(payload);
          if (tag) socket.wp8Cipher = tag;

          const msg = JSON.parse(cryptoHelper.decodePayload(payload));
          // In the order they arrived, one at a time. The handlers used to be
          // started together, and `hello` awaits the GOWA device (an HTTP call):
          // a command sent right after the handshake could be handled before it
          // had finished, find no user on the socket, and be refused by closing a
          // connection whose client had done nothing wrong.
          socket.chain = socket.chain.then(() => (
            msg.Type === 3 ? handleControl(msg, socket) : handleUserMessage(msg, socket)
          )).catch((e) => logger('ERR', e.message));
        } catch (err) {
          logger('ERR', `invalid frame from the app: ${err.message}`);
        }
      }
    });

    socket.on('close', () => { logger('NET', `app client disconnected: ${remote}`); dropSocket(socket); });
    socket.on('error', (err) => { logger('NET', `socket error [${remote}]: ${err.message}`); dropSocket(socket); });
  });

  return {
    tcpServer,
    getState: () => anonymous.state,
    refreshStatus,
    handleWebhookEvent,
    probeFfmpeg: () => mediaTools.probe(),
    handleControl,
    openKnownSessions,
    syncContacts: () => syncContacts(anonymous),
    // Used only by the tests: forces the "connected" state without going through GOWA.
    setConnectedForTest() { anonymous.state = { status: 'connected', jid: '39@s.whatsapp.net' }; },
    // Used only by the tests: adds a fake client to the recipient list.
    addClientForTest(socket) {
      socket.session = anonymous;
      anonymous.sockets.add(socket);
    },
    sendCalls: () => sendCalls(anonymous),
    resetCallsCacheForTest() { anonymous.callsCache = null; },
    sendChats: () => sendChats(anonymous),
    resetChatsCacheForTest() { anonymous.chatsCache = null; },
    stop() { /* the polling timer is managed by main() */ }
  };
}

// ─── Startup ────────────────────────────────────────────────────────────────

async function main() {
  const debug = process.argv.includes('--debug');
  const log = makeLogger(debug);
  const dbg = (...args) => { if (debug) console.log('  [DEBUG]', ...args); };

  const config = loadConfig(applyDotEnv(process.env));
  log('INFO', 'WhatsApp Community Adapter v2.0 (GOWA)');
  log('INFO', `GOWA:        ${config.gowa.url}`);
  log('INFO', `Device GOWA: ${config.gowa.deviceId || '(default)'}`);
  log('INFO', `TCP app:     ${config.bridge.port}`);
  log('INFO', `Webhook:     ${config.webhook.publicUrl}`);
  log('INFO', `Encryption:  ${cryptoHelper.ModeDescription} ${cryptoHelper.ENCRYPTION_ENABLED ? 'ON' : 'OFF'}`);

  const gowa = new GowaClient({
    baseUrl: config.gowa.url,
    deviceId: config.gowa.deviceId,
    user: config.gowa.user,
    pass: config.gowa.pass
  });

  // The users of the service, if it is shared. With no file configured, memory
  // is enough and no disk is needed: that is the private-instance case.
  const users = createUserStore({
    file: config.auth.usersFile || undefined
  });
  if (config.auth.required) {
    log('INFO', `Auth:        required, ${users.count()} user(s)`
      + (config.auth.register === false ? '' : ', new devices register on first connection'));
  }

  const bridge = createBridge({ config, gowa, log, debug: dbg, users });
  await bridge.probeFfmpeg();

  const webhookServer = createWebhookServer({
    path: config.webhook.path,
    secret: config.webhook.secret,
    log,
    onEvent: (event) => bridge.handleWebhookEvent(event)
  });

  try {
    const deviceId = await gowa.ensureDevice();
    log('OK', `GOWA device ready: ${deviceId || '(default)'}`);
    const registered = await gowa.setDeviceWebhook(config.webhook.publicUrl);
    log(registered ? 'OK' : 'WARN',
      registered
        ? `webhook registered with GOWA: ${config.webhook.publicUrl}`
        : `automatic webhook registration failed: start GOWA with --webhook=${config.webhook.publicUrl}`);
  } catch (err) {
    log('ERR', `GOWA not reachable at ${config.gowa.url}: ${err.message}`);
    log('ERR', 'start GOWA with: ./whatsapp rest --basic-auth=user:password');
  }

  bridge.tcpServer.listen(config.bridge.port, '0.0.0.0', () => {
    log('OK', `TCP server listening on port ${config.bridge.port}`);
    const addresses = [];
    const interfaces = os.networkInterfaces();
    Object.keys(interfaces).forEach((name) => {
      (interfaces[name] || []).forEach((iface) => {
        if (iface.family === 'IPv4' && !iface.internal) addresses.push(iface.address);
      });
    });
    log('INFO', `   connect the WP8 app to: ${addresses.join(', ') || '(no IP found)'}:${config.bridge.port}`);
  });

  webhookServer.listen(config.webhook.port, '0.0.0.0', () => {
    log('OK', `webhook listening on port ${config.webhook.port}${config.webhook.path}`);
  });

  // The beacon body is built with the single builder of the discovery module:
  // six keys, the same ones the app reads in BeaconPayload.cs.
  let beacon = null;
  if (config.discovery.enabled) {
    beacon = createDiscoveryBeacon({
      port: config.discovery.port,
      getPayload: () => {
        const current = bridge.getState();
        return buildPayload({
          name: config.discovery.name,
          port: config.bridge.port,
          state: current.status,
          account: current.jid
        });
      },
      log
    });
    log('OK', `discovery beacon on UDP port ${config.discovery.port} (name: ${config.discovery.name})`);
  }

  bridge.tcpServer.on('error', (err) => {
    log('ERR', `TCP server error: ${err.message}`);
    if (err.code === 'EADDRINUSE') log('ERR', `port ${config.bridge.port} is already in use (set BRIDGE_PORT=...).`);
    process.exit(1);
  });

  // Before the first read of the state, so that the account JID of every user is
  // known when the first webhook arrives, and not only when a phone connects.
  await bridge.openKnownSessions();
  await bridge.refreshStatus();
  const timer = setInterval(() => bridge.refreshStatus(), config.pollIntervalMs);

  const shutdown = () => {
    clearInterval(timer);
    if (beacon) beacon.stop();
    bridge.stop();
    try { webhookServer.close(); } catch (e) { /* ignora */ }
    try { bridge.tcpServer.close(); } catch (e) { /* ignora */ }
    log('OK', 'adapter stopped.');
    process.exit(0);
  };
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
}

if (require.main === module) {
  main().catch((err) => {
    console.error('FATAL ERROR:', err);
    process.exit(1);
  });
}

module.exports = { createBridge, main, makeLogger, MAX_FRAME_LENGTH };
