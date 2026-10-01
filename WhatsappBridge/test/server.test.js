'use strict';
const test = require('node:test');
const assert = require('node:assert');
const net = require('node:net');
const { createBridge } = require('../server');
const cryptoHelper = require('../crypto-helper');

const noop = () => {};

function fakeGowa(overrides = {}) {
  return Object.assign({
    status: async () => ({ isConnected: true, isLoggedIn: false, jid: '' }),
    loginQr: async () => ({ qrLink: 'http://g/statics/qr.png', duration: 30 }),
    fetchBinary: async () => ({ buffer: Buffer.from([1, 2, 3]), contentType: 'image/png' }),
    loginWithCode: async (phone) => `CODE-${phone}`,
    logout: async () => {},
    contacts: async () => [{ jid: '39@s.whatsapp.net', name: 'Mario' }],
    sendText: async () => 'M1',
    sendImage: async () => 'M2',
    setDeviceWebhook: async () => true
  }, overrides);
}

function connectClient(port) {
  const socket = net.connect(port, '127.0.0.1');
  let buffer = Buffer.alloc(0);
  const messages = [];
  // Il primo byte di ogni payload e' il tag cifrario: e' cosi' che si vede
  // con quale cifrario il server ha risposto.
  const tags = [];
  const waiters = [];
  socket.on('data', (chunk) => {
    buffer = Buffer.concat([buffer, chunk]);
    while (buffer.length >= 4) {
      const len = buffer.readUInt32LE(0);
      if (buffer.length < 4 + len) break;
      const payload = buffer.slice(4, 4 + len);
      buffer = buffer.slice(4 + len);
      tags.push(payload[0]);
      const json = JSON.parse(cryptoHelper.decodePayload(payload));
      messages.push(json);
      while (waiters.length) waiters.shift()(json);
    }
  });
  return {
    socket,
    messages,
    tags,
    send(msg, tag) {
      socket.write(cryptoHelper.buildFrame(JSON.stringify(msg), tag));
    },
    next(timeoutMs = 2000) {
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('timeout')), timeoutMs);
        waiters.push((m) => { clearTimeout(timer); resolve(m); });
      });
    }
  };
}

test('il bridge invia lo stato al collegamento e gestisce login.qr', async () => {
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({ config, gowa: fakeGowa(), log: noop, debug: noop });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);
  try {
    const state = await client.next();
    assert.strictEqual(state.Command, 'state');
    assert.strictEqual(state.State, 'disconnected');

    client.send({ Type: 3, ChatId: 'system', Command: 'login.qr', Timestamp: '\\/Date(0)\\/' });
    const qr = await client.next();
    assert.strictEqual(qr.Command, 'qr');
    assert.strictEqual(qr.QrImageData, Buffer.from([1, 2, 3]).toString('base64'));
    assert.strictEqual(qr.QrDuration, 30);
  } finally {
    client.socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('il bridge risponde a login.code con il codice di abbinamento', async () => {
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({ config, gowa: fakeGowa(), log: noop, debug: noop });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);
  try {
    await client.next(); // stato iniziale
    client.send({ Type: 3, ChatId: 'system', Command: 'login.code', Text: '393401234567' });
    const reply = await client.next();
    assert.strictEqual(reply.Command, 'paircode');
    assert.strictEqual(reply.PairCode, 'CODE-393401234567');
  } finally {
    client.socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('il bridge inoltra a WhatsApp un messaggio utente ricevuto via TCP', async () => {
  let sent = null;
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({
    config,
    gowa: fakeGowa({ sendText: async (phone, text) => { sent = { phone, text }; return 'M1'; } }),
    log: noop, debug: noop
  });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);
  try {
    await client.next();
    bridge.setConnectedForTest();
    client.send({ Type: 0, ChatId: '39@s.whatsapp.net', Text: 'ciao', SenderName: 'Io' });
    await new Promise((r) => setTimeout(r, 50));
    assert.deepStrictEqual(sent, { phone: '39@s.whatsapp.net', text: 'ciao' });
  } finally {
    client.socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('il bridge inoltra ai client WP8 i messaggi ricevuti dal webhook', async () => {
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({ config, gowa: fakeGowa(), log: noop, debug: noop });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);
  try {
    await client.next();
    await bridge.handleWebhookEvent({
      event: 'message',
      payload: {
        id: 'X1', chat_id: '39@s.whatsapp.net', from: '39@s.whatsapp.net',
        sender_display_name: 'Mario', timestamp: '2026-01-02T03:04:05Z', is_from_me: false, body: 'Ehi'
      }
    });
    const msg = await client.next();
    assert.strictEqual(msg.Type, 0);
    assert.strictEqual(msg.Text, 'Ehi');
    assert.strictEqual(msg.IsIncoming, true);
    assert.strictEqual(msg.ChatId, '39@s.whatsapp.net');
    assert.strictEqual(msg.Status, 3);
  } finally {
    client.socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('il server risponde in CBC al primo stato e a un client che scrive in CBC', async () => {
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({ config, gowa: fakeGowa(), log: noop, debug: noop });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);
  try {
    await client.next();
    // Il primo frame parte prima che il client abbia scritto: deve essere
    // quello leggibile da tutti, cioe' CBC.
    assert.deepStrictEqual(client.tags, [cryptoHelper.CIPHER_CBC_HMAC]);

    client.send({ Type: 3, ChatId: 'system', Command: 'status' }, cryptoHelper.CIPHER_CBC_HMAC);
    await client.next();
    assert.strictEqual(client.tags[1], cryptoHelper.CIPHER_CBC_HMAC);
  } finally {
    client.socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('il server passa a GCM se il client scrive in GCM', async () => {
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({ config, gowa: fakeGowa(), log: noop, debug: noop });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);
  try {
    await client.next();
    assert.strictEqual(client.tags[0], cryptoHelper.CIPHER_CBC_HMAC);

    client.send({ Type: 3, ChatId: 'system', Command: 'status' }, cryptoHelper.CIPHER_GCM);
    await client.next();
    assert.strictEqual(client.tags[1], cryptoHelper.CIPHER_GCM);
  } finally {
    client.socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('the calls command sends one frame per call and then calls.done', async () => {
  const sent = [];
  const gowa = {
    chats: async () => [{ jid: 'a@s.whatsapp.net', name: 'Anna' }],
    chatMessages: async () => ([
      { id: 'm2', chat_jid: 'a@s.whatsapp.net', media_type: 'call', call_metadata: '{"call_id":"C1","reason":"timeout"}', timestamp: '2026-09-24T09:00:00Z' },
    ]),
    status: async () => ({ isConnected: true, isLoggedIn: true, jid: '39@s.whatsapp.net' }),
  };
  const config = { calls: { chatLimit: 10, messagesPerChat: 10, limit: 10 }, bridge: { port: 8585 } };
  const bridge = createBridge({ config, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();

  bridge.addClientForTest({ write: (packet) => sent.push(packet) });
  await bridge.handleControl({ Type: 3, Command: 'calls', SenderName: 'test' });

  // Le chiavi arrivano cifrate nel frame: si controllano i comandi con un
  // decodificatore, non con un confronto testuale sul buffer.
  const commands = sent.map((packet) => decodeFrame(packet)).map((msg) => msg.Command);
  assert.deepStrictEqual(commands, ['call', 'calls.done']);
});

test('the calls command answers with an error and calls.done when WhatsApp is not connected', async () => {
  const sent = [];
  const gowa = { status: async () => ({ isConnected: false, isLoggedIn: false, jid: '' }) };
  const bridge = createBridge({ config: { calls: {} }, gowa, log: () => {}, debug: () => {} });

  bridge.addClientForTest({ write: (packet) => sent.push(packet) });
  await bridge.handleControl({ Type: 3, Command: 'calls', SenderName: 'test' });

  const commands = sent.map((packet) => decodeFrame(packet)).map((msg) => msg.Command);
  assert.deepStrictEqual(commands, ['error', 'calls.done']);
});

test('the chats command sends one frame per chat and then chats.done', async () => {
  const sent = [];
  const gowa = {
    chats: async () => [{ jid: 'a@s.whatsapp.net', name: 'Anna' }],
    chatMessages: async () => [{ content: 'ciao', timestamp: '2026-09-26T09:00:00Z' }],
    avatar: async () => 'AAAA',
    status: async () => ({ isConnected: true, isLoggedIn: true, jid: '39@s.whatsapp.net' })
  };
  const config = { chats: { limit: 10, avatars: true }, calls: {}, bridge: { port: 8585 } };
  const bridge = createBridge({ config, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(packet) });

  await bridge.handleControl({ Type: 3, Command: 'chats', SenderName: 'test' });

  const frames = sent.map((packet) => decodeFrame(packet));
  assert.deepStrictEqual(frames.map((f) => f.Command), ['chat', 'chats.done']);
  assert.strictEqual(frames[0].ChatId, 'a@s.whatsapp.net');
  assert.strictEqual(frames[0].SenderName, 'Anna');
  assert.strictEqual(frames[0].Text, 'ciao');
  assert.strictEqual(frames[0].AvatarData, 'AAAA');
});

test('the chats command answers with an error and chats.done when WhatsApp is not connected', async () => {
  const sent = [];
  const bridge = createBridge({ config: { chats: {} }, gowa: {}, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(packet) });

  await bridge.handleControl({ Type: 3, Command: 'chats', SenderName: 'test' });

  const frames = sent.map((packet) => decodeFrame(packet));
  assert.deepStrictEqual(frames.map((f) => f.Command), ['error', 'chats.done']);
});

function decodeFrame(packet) {
  const length = packet.readUInt32LE(0);
  const payload = packet.slice(4, 4 + length);
  return JSON.parse(cryptoHelper.decodePayload(payload));
}

test('a revoked message becomes a revoked control frame', async () => {
  const sent = [];
  const bridge = createBridge({ config: {}, gowa: {}, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleWebhookEvent({
    event: 'message.revoked',
    payload: { revoked_message_id: 'ABC', revoked_from_me: false, revoked_chat: 'a@s.whatsapp.net' }
  });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'revoked');
  assert.strictEqual(sent[0].ChatId, 'a@s.whatsapp.net');
  assert.strictEqual(sent[0].RelatedMessageId, 'ABC');
  assert.strictEqual(sent[0].Type, 3);
});

test('an edited message becomes an edited control frame with the new text', async () => {
  const sent = [];
  const bridge = createBridge({ config: {}, gowa: {}, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleWebhookEvent({
    event: 'message.edited',
    payload: { original_message_id: 'ABC', chat_id: 'a@s.whatsapp.net', body: 'testo nuovo' }
  });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'edited');
  assert.strictEqual(sent[0].RelatedMessageId, 'ABC');
  assert.strictEqual(sent[0].Text, 'testo nuovo');
});

test('reactions and incomplete events are ignored without sending anything', async () => {
  const sent = [];
  const bridge = createBridge({ config: {}, gowa: {}, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleWebhookEvent({ event: 'message.reaction', payload: { reaction: 'X' } });
  await bridge.handleWebhookEvent({ event: 'message.revoked', payload: {} });
  await bridge.handleWebhookEvent(null);

  assert.strictEqual(sent.length, 0);
});

// La sessione di un utente con il suo client gia' collegato: le presenze hanno
// bisogno di sapere chi e' collegato, e questo e' l unico posto che lo prepara.
async function connectedUserSession() {
  const shared = sharedBridge();
  const socket = collectingSocket();
  shared.bridge.addClientForTest(socket);
  await shared.bridge.handleControl(
    { Type: 3, Command: 'hello', Token: shared.a.token, SenderName: 'anna' }, socket);
  // GOWA sa che l account e' collegato: l adapter lo scopre alla rilettura.
  shared.setLoggedIn(true);
  await shared.bridge.refreshStatus();
  socket.frames.length = 0;
  return Object.assign({}, shared, { socket: socket });
}

test('un contatto che scrive diventa un frame typing per il suo utente', async () => {
  const { bridge, a, socket } = await connectedUserSession();

  await bridge.handleWebhookEvent({
    event: 'chat_presence',
    device_id: a.user.deviceId,
    payload: {
      from: 'b@s.whatsapp.net', chat_id: 'b@s.whatsapp.net',
      state: 'composing', media: '', is_group: false
    }
  });
  await bridge.handleWebhookEvent({
    event: 'chat_presence',
    device_id: a.user.deviceId,
    payload: { from: 'b@s.whatsapp.net', chat_id: 'b@s.whatsapp.net', state: 'paused' }
  });

  const typing = socket.frames.filter((f) => f.Command === 'typing');
  assert.strictEqual(typing.length, 2);
  assert.strictEqual(typing[0].Type, 3);
  assert.strictEqual(typing[0].ChatId, 'b@s.whatsapp.net');
  assert.strictEqual(typing[0].State, 'composing');
  assert.strictEqual(typing[1].State, 'paused');
});

test('la presenza del nostro stesso account non torna indietro all app', async () => {
  const { bridge, socket } = await connectedUserSession();

  // GOWA firma gli eventi con il JID dell account, non con l uuid del device:
  // un contatto che scrive deve comunque arrivare, ed e' la stessa strada.
  await bridge.handleWebhookEvent({
    event: 'chat_presence',
    device_id: '39@s.whatsapp.net',
    payload: { from: 'b@s.whatsapp.net', chat_id: 'b@s.whatsapp.net', state: 'composing' }
  });
  assert.strictEqual(socket.frames.filter((f) => f.Command === 'typing').length, 1);

  // Il nostro telefono, o un altro device collegato: WhatsApp non lo mostra a
  // chi scrive, e nemmeno l app deve mostrarlo.
  socket.frames.length = 0;
  await bridge.handleWebhookEvent({
    event: 'chat_presence',
    device_id: '39@s.whatsapp.net',
    payload: { from: '39@s.whatsapp.net', chat_id: 'b@s.whatsapp.net', state: 'composing' }
  });
  assert.strictEqual(socket.frames.filter((f) => f.Command === 'typing').length, 0);
});

test('una presenza senza chat o con uno stato inventato non manda niente', async () => {
  const { bridge, a, socket } = await connectedUserSession();

  await bridge.handleWebhookEvent({
    event: 'chat_presence', device_id: a.user.deviceId, payload: { state: 'composing' }
  });
  await bridge.handleWebhookEvent({
    event: 'chat_presence',
    device_id: a.user.deviceId,
    payload: { chat_id: 'b@s.whatsapp.net', state: 'sto scrivendo' }
  });

  assert.strictEqual(socket.frames.filter((f) => f.Command === 'typing').length, 0);
});

test('l account e online mentre un telefono guarda, e offline quando se ne va', async () => {
  const presence = [];
  const gowa = fakeGowa({
    status: async () => ({ isConnected: true, isLoggedIn: true, jid: '39@s.whatsapp.net' }),
    sendPresence: async (type) => { presence.push(type); return true; }
  });
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({ config, gowa, log: noop, debug: noop });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);
  try {
    await client.next();

    // Un socket che apre la porta e basta non e' un telefono che guarda: la
    // healthcheck del container fa esattamente questo ogni trenta secondi, e
    // per lei l account non deve andare online e ritornare offline subito dopo.
    await bridge.refreshStatus();
    assert.deepStrictEqual(presence, [],
      'una connessione senza handshake non e un client che guarda');

    // Il telefono si presenta: da qui in poi e' un client, e l account e online.
    client.send({ Type: 3, Command: 'hello', SenderName: 'WP8', ChatId: 'system' });
    await new Promise((r) => setTimeout(r, 50));
    assert.deepStrictEqual(presence, ['available'],
      'un telefono che si e presentato, su un account connesso: si e online');

    // Lo stesso stato non si ripete a ogni giro di polling.
    await bridge.refreshStatus();
    assert.deepStrictEqual(presence, ['available']);

    // Il telefono se ne va: l account non ha piu nessuno che lo guarda.
    client.socket.destroy();
    await new Promise((r) => setTimeout(r, 50));
    assert.deepStrictEqual(presence, ['available', 'unavailable']);
  } finally {
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('un telefono in background dice presence e l account va offline', async () => {
  const presence = [];
  const gowa = fakeGowa({
    status: async () => ({ isConnected: true, isLoggedIn: true, jid: '39@s.whatsapp.net' }),
    sendPresence: async (type) => { presence.push(type); return true; }
  });
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({ config, gowa, log: noop, debug: noop });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);
  try {
    await client.next();
    client.send({ Type: 3, Command: 'hello', SenderName: 'WP8', ChatId: 'system' });
    await new Promise((r) => setTimeout(r, 50));
    assert.deepStrictEqual(presence, ['available']);

    // L app va in background: il telefono si congela ma il socket resta
    // aperto. Il solo conteggio dei socket non se ne accorgerebbe, e WhatsApp
    // continuerebbe a mostrare l account online.
    client.send({ Type: 3, Command: 'presence', State: 'paused', ChatId: 'system' });
    await new Promise((r) => setTimeout(r, 50));
    assert.deepStrictEqual(presence, ['available', 'unavailable'],
      'un telefono sospeso non e piu un telefono che guarda');

    // Lo stato non si ripete a ogni giro di polling.
    await bridge.refreshStatus();
    assert.deepStrictEqual(presence, ['available', 'unavailable']);

    // Torna in primo piano: di nuovo online.
    client.send({ Type: 3, Command: 'presence', State: 'active', ChatId: 'system' });
    await new Promise((r) => setTimeout(r, 50));
    assert.deepStrictEqual(presence, ['available', 'unavailable', 'available']);

    // Chiudere l app finisce la storia per la via normale: il socket sparisce.
    client.socket.destroy();
    await new Promise((r) => setTimeout(r, 50));
    assert.deepStrictEqual(presence, ['available', 'unavailable', 'available', 'unavailable']);
  } finally {
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('typing porta a GOWA start e stop, e niente quando non e collegato', async () => {
  const chatPresence = [];
  const gowa = fakeGowa({
    status: async () => ({ isConnected: true, isLoggedIn: true, jid: '39@s.whatsapp.net' }),
    sendChatPresence: async (jid, action) => { chatPresence.push({ jid, action }); return true; }
  });
  const bridge = createBridge({ config: {}, gowa, log: noop, debug: noop });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: () => {} });

  await bridge.handleControl({ Type: 3, Command: 'typing', Text: 'b@s.whatsapp.net', State: 'composing' });
  await bridge.handleControl({ Type: 3, Command: 'typing', Text: 'b@s.whatsapp.net', State: 'paused' });
  assert.deepStrictEqual(chatPresence, [
    { jid: 'b@s.whatsapp.net', action: 'start' },
    { jid: 'b@s.whatsapp.net', action: 'stop' }
  ], 'i nomi del webhook (composing/paused) diventano quelli di GOWA (start/stop)');

  // Uno stato che non esiste, o una chat vuota, non diventano una chiamata.
  await bridge.handleControl({ Type: 3, Command: 'typing', Text: 'b@s.whatsapp.net', State: 'forse' });
  await bridge.handleControl({ Type: 3, Command: 'typing', Text: '', State: 'composing' });
  assert.strictEqual(chatPresence.length, 2);

  // Su una sessione non collegata non c e nessuna presenza da mandare.
  const offline = [];
  const away = createBridge({
    config: {},
    gowa: fakeGowa({ sendChatPresence: async () => { offline.push(1); } }),
    log: noop, debug: noop
  });
  await away.handleControl({ Type: 3, Command: 'typing', Text: 'b@s.whatsapp.net', State: 'composing' });
  assert.strictEqual(offline.length, 0);
});

test('the messages command sends one frame per stored message, marked as history', async () => {
  const sent = [];
  const gowa = {
    chatMessages: async (jid, limit) => {
      assert.strictEqual(jid, 'a@s.whatsapp.net');
      assert.strictEqual(limit, 5);
      return [
        {
          id: 'A1', chat_jid: jid, sender_jid: 'a@s.whatsapp.net', sender_display_name: 'Anna',
          content: 'ciao', timestamp: '2026-09-26T09:00:00Z', is_from_me: false
        },
        {
          id: 'A2', chat_jid: jid, sender_display_name: 'Anna', media_type: 'image',
          timestamp: '2026-09-26T09:05:00Z', is_from_me: true
        }
      ];
    },
    status: async () => ({ isConnected: true, isLoggedIn: true, jid: '39@s.whatsapp.net' })
  };
  const config = { messages: { limit: 5 }, chats: {}, calls: {}, bridge: { port: 8585 } };
  const bridge = createBridge({ config, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(packet) });

  await bridge.handleControl({ Type: 3, Command: 'messages', Text: 'a@s.whatsapp.net', SenderName: 'test' });

  const frames = sent.map((packet) => decodeFrame(packet));
  assert.strictEqual(frames.length, 2);
  assert.strictEqual(frames[0].ChatId, 'a@s.whatsapp.net');
  assert.strictEqual(frames[0].Text, 'ciao');
  assert.strictEqual(frames[0].SenderName, 'Anna');
  // La mappatura formatta la data e buildChatMessage la formatta di nuovo: la
  // seconda passata deve restituire lo stesso epoch, non una data diversa.
  assert.strictEqual(frames[0].Timestamp, `/Date(${Date.parse('2026-09-26T09:00:00Z')})/`);
  assert.strictEqual(frames[0].IsHistory, true);
  assert.strictEqual(frames[0].Type, 0);
  assert.strictEqual(frames[0].Command, undefined);
  assert.strictEqual(frames[1].Text, '[Image]');
  assert.strictEqual(frames[1].IsIncoming, false);
  assert.strictEqual(frames[1].ChatId, 'a@s.whatsapp.net');
});

test('the messages command drops a message GOWA has no id for, and reports a failure once', async () => {
  const sent = [];
  const gowa = {
    chatMessages: async () => [
      { chat_jid: 'a@s.whatsapp.net', content: 'senza id' },
      { id: 'B2', chat_jid: 'a@s.whatsapp.net', content: 'con id' }
    ],
    status: async () => ({ isConnected: true, isLoggedIn: true, jid: '39@s.whatsapp.net' })
  };
  const config = { messages: { limit: 5 }, bridge: { port: 8585 } };
  const bridge = createBridge({ config, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({ Type: 3, Command: 'messages', Text: 'a@s.whatsapp.net' });

  // Senza l'id di WhatsApp non si puo' riconoscere un doppione: si perde quel
  // messaggio, non si duplica tutta la chat.
  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Text, 'con id');

  sent.length = 0;
  const failing = createBridge({
    config,
    gowa: {
      chatMessages: async () => { throw new Error('chat non trovata'); },
      status: async () => ({ isConnected: true, isLoggedIn: true, jid: '39@x' })
    },
    log: () => {},
    debug: () => {}
  });
  failing.setConnectedForTest();
  failing.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await failing.handleControl({ Type: 3, Command: 'messages', Text: 'a@s.whatsapp.net' });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'error');
  assert.strictEqual(sent[0].ChatId, 'a@s.whatsapp.net');
});

test('the messages command answers with an error when WhatsApp is not connected', async () => {
  const sent = [];
  const bridge = createBridge({ config: { messages: {} }, gowa: {}, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({ Type: 3, Command: 'messages', Text: 'a@s.whatsapp.net' });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'error');
  assert.strictEqual(sent[0].ChatId, 'a@s.whatsapp.net');
});

test('un messaggio in arrivo conta come non letto, e read lo azzera', async () => {
  const sent = [];
  const gowa = {
    chats: async () => [{ jid: 'a@s.whatsapp.net', name: 'Anna' }],
    chatMessages: async () => [],
    avatar: async () => null
  };
  const bridge = createBridge({
    config: { chats: { limit: 5, avatars: false } },
    gowa,
    log: () => {},
    debug: () => {}
  });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleWebhookEvent({
    event: 'message',
    payload: {
      id: 'M1',
      chat_id: 'a@s.whatsapp.net',
      from: 'a@s.whatsapp.net',
      body: 'ciao',
      timestamp: '2026-09-27T08:00:00Z'
    }
  });

  sent.length = 0;
  await bridge.handleControl({ Type: 3, Command: 'chats', SenderName: 'test' });
  const rows = sent.filter((f) => f.Command === 'chat');
  assert.strictEqual(rows.length, 1);
  assert.strictEqual(rows[0].UnreadCount, 1);

  await bridge.handleControl({ Type: 3, Command: 'read', Text: 'a@s.whatsapp.net' });

  sent.length = 0;
  bridge.resetChatsCacheForTest();
  await bridge.handleControl({ Type: 3, Command: 'chats' });
  const after = sent.filter((f) => f.Command === 'chat');
  assert.strictEqual(after[0].UnreadCount, 0);
});

function mediaBridge(gowa) {
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: () => {} });
  return bridge;
}

test('i pezzi di un video si ricompongono e vanno a sendVideo', async () => {
  const delivered = [];
  const gowa = {
    sendVideo: async (phone, caption, buffer, mimeType, fileName) => {
      delivered.push({ phone, caption, size: buffer.length, mimeType, fileName });
      return 'V1';
    },
    sendImage: async () => { throw new Error('un video non passa da sendImage'); },
    sendFile: async () => { throw new Error('un video non passa da sendFile'); }
  };
  const bridge = mediaBridge(gowa);

  const bytes = Buffer.from('un video finto, lungo abbastanza da dividersi in due');
  const base64 = bytes.toString('base64');
  const middle = Math.ceil((base64.length / 2) / 4) * 4;   // multiplo di 4

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 't1', MediaFileName: 'clip.mp4', MediaMimeType: 'video/mp4', MediaChunkTotal: 2 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 't1', MediaChunkIndex: 0, MediaData: base64.slice(0, middle) });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 't1', MediaChunkIndex: 1, MediaData: base64.slice(middle) });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 't1', Text: 'guarda' });

  assert.strictEqual(delivered.length, 1);
  assert.strictEqual(delivered[0].phone, 'a@s.whatsapp.net');
  assert.strictEqual(delivered[0].caption, 'guarda');
  assert.strictEqual(delivered[0].mimeType, 'video/mp4');
  assert.strictEqual(delivered[0].fileName, 'clip.mp4');
  assert.strictEqual(delivered[0].size, bytes.length);
});

test('un video non rimpicciolito dal telefono lo rimpicciolisce l adapter', async () => {
  const delivered = [];
  const gowa = {
    sendVideo: async (phone, caption, buffer, mimeType, fileName) => {
      delivered.push({ size: buffer.length, mimeType, fileName });
      return 'V1';
    },
    sendImage: async () => { throw new Error('non e un video'); },
    sendFile: async () => { throw new Error('non e un video'); }
  };
  const transcoder = {
    probe: async () => true,
    toPlayable: async () => null,
    toSmallerVideo: async () => ({
      buffer: Buffer.alloc(10),
      mimeType: 'video/mp4',
      fileName: 'clip.mp4'
    })
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {}, transcoder });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: () => {} });

  const bytes = Buffer.from('un video grande finto');
  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 's1', MediaFileName: 'clip.mov', MediaMimeType: 'video/quicktime', MediaChunkTotal: 1 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 's1', MediaChunkIndex: 0, MediaData: bytes.toString('base64') });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 's1' });

  assert.strictEqual(delivered.length, 1);
  assert.strictEqual(delivered[0].size, 10, 'va a WhatsApp il video ridotto, non l originale');
  assert.strictEqual(delivered[0].mimeType, 'video/mp4');
  assert.strictEqual(delivered[0].fileName, 'clip.mp4');
});

test('senza compressore il video parte come sta', async () => {
  const delivered = [];
  const gowa = {
    sendVideo: async (phone, caption, buffer, mimeType, fileName) => {
      delivered.push({ size: buffer.length, mimeType, fileName });
      return 'V1';
    },
    sendImage: async () => { throw new Error('non e un video'); },
    sendFile: async () => { throw new Error('non e un video'); }
  };
  // Il transcoder dei vocali, senza il metodo dei video: la chiamata non deve
  // rompersi solo perche' non c'e'.
  const transcoder = { probe: async () => true, toPlayable: async () => null };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {}, transcoder });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: () => {} });

  const bytes = Buffer.from('un video grande finto');
  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 's2', MediaFileName: 'clip.mov', MediaMimeType: 'video/quicktime', MediaChunkTotal: 1 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 's2', MediaChunkIndex: 0, MediaData: bytes.toString('base64') });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 's2' });

  assert.strictEqual(delivered.length, 1);
  assert.strictEqual(delivered[0].size, bytes.length);
  assert.strictEqual(delivered[0].fileName, 'clip.mov');
});

test('un immagine non passa dal compressore dei video', async () => {
  let compressed = 0;
  const gowa = {
    sendImage: async () => 'I1',
    sendVideo: async () => { throw new Error('non e un video'); },
    sendFile: async () => { throw new Error('non e un video'); }
  };
  const transcoder = {
    probe: async () => true,
    toPlayable: async () => null,
    toSmallerVideo: async () => { compressed++; return null; }
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {}, transcoder });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: () => {} });

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 's3', MediaFileName: 'foto.jpg', MediaMimeType: 'image/jpeg', MediaChunkTotal: 1 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 's3', MediaChunkIndex: 0, MediaData: Buffer.from('foto').toString('base64') });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 's3' });

  assert.strictEqual(compressed, 0);
});

test('un allegato immagine va a sendImage e uno sconosciuto a sendFile', async () => {
  const delivered = [];
  const gowa = {
    sendImage: async (phone, caption, buffer, mimeType) => { delivered.push({ door: 'image', mimeType }); return 'I1'; },
    sendVideo: async () => { throw new Error('non e un video'); },
    sendFile: async (phone, caption, buffer, mimeType) => { delivered.push({ door: 'file', mimeType }); return 'F1'; }
  };
  const bridge = mediaBridge(gowa);

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'i1', MediaFileName: 'foto.jpg', MediaMimeType: 'image/jpeg', MediaChunkTotal: 1 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'i1', MediaChunkIndex: 0, MediaData: Buffer.from('foto').toString('base64') });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'i1' });

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'd1', MediaFileName: 'doc.pdf', MediaMimeType: 'application/pdf', MediaChunkTotal: 1 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'd1', MediaChunkIndex: 0, MediaData: Buffer.from('pdf').toString('base64') });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'd1' });

  assert.deepStrictEqual(delivered, [
    { door: 'image', mimeType: 'image/jpeg' },
    { door: 'file', mimeType: 'application/pdf' }
  ]);
});

test('un pdf inviato va a sendFile con il suo nome', async () => {
  const delivered = [];
  const gowa = {
    sendFile: async (phone, caption, buffer, mimeType, fileName) => {
      delivered.push({ door: 'file', mimeType, fileName });
      return 'F1';
    },
    sendImage: async () => { throw new Error('un pdf non e un immagine'); },
    sendVideo: async () => { throw new Error('un pdf non e un video'); }
  };
  const bridge = mediaBridge(gowa);

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'p1', MediaFileName: 'contratto.pdf', MediaMimeType: 'application/pdf', MediaChunkTotal: 1 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'p1', MediaChunkIndex: 0, MediaData: Buffer.from('pdf-bytes').toString('base64') });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'p1' });

  assert.deepStrictEqual(delivered, [
    { door: 'file', mimeType: 'application/pdf', fileName: 'contratto.pdf' }
  ]);
});

test('un vocale inviato va a sendAudio, non a sendFile', async () => {
  const delivered = [];
  const gowa = {
    sendAudio: async (phone, caption, buffer, mimeType, fileName) => {
      delivered.push({ door: 'audio', mimeType, fileName });
      return 'A1';
    },
    sendFile: async () => { throw new Error('un vocale non passa da sendFile'); },
    sendImage: async () => { throw new Error('un vocale non e un immagine'); },
    sendVideo: async () => { throw new Error('un vocale non e un video'); }
  };
  const bridge = mediaBridge(gowa);

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'v1', MediaFileName: 'voce.m4a', MediaMimeType: 'audio/mp4', MediaChunkTotal: 1 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'v1', MediaChunkIndex: 0, MediaData: Buffer.from('voce').toString('base64') });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'v1' });

  assert.deepStrictEqual(delivered, [
    { door: 'audio', mimeType: 'audio/mp4', fileName: 'voce.m4a' }
  ]);
});

test('senza sendAudio nel client un vocale ripiega su sendFile', async () => {
  const delivered = [];
  const gowa = {
    sendFile: async (phone, caption, buffer, mimeType, fileName) => {
      delivered.push({ door: 'file', mimeType, fileName });
      return 'F1';
    },
    sendImage: async () => { throw new Error('un vocale non e un immagine'); },
    sendVideo: async () => { throw new Error('un vocale non e un video'); }
  };
  const bridge = mediaBridge(gowa);

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'v2', MediaFileName: 'voce.m4a', MediaMimeType: 'audio/mp4', MediaChunkTotal: 1 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'v2', MediaChunkIndex: 0, MediaData: Buffer.from('voce').toString('base64') });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'v2' });

  assert.deepStrictEqual(delivered, [
    { door: 'file', mimeType: 'audio/mp4', fileName: 'voce.m4a' }
  ]);
});

test('un allegato a cui manca un pezzo non viene mandato, e lo dice', async () => {
  let sent = 0;
  const gowa = {
    sendVideo: async () => { sent++; return 'V1'; },
    sendImage: async () => { sent++; return 'I1'; },
    sendFile: async () => { sent++; return 'F1'; }
  };
  const handles = [];
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => handles.push(decodeFrame(packet)) });

  const base64 = Buffer.from('un video finto che si divide in tre').toString('base64');
  const third = Math.ceil((base64.length / 3) / 4) * 4;

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'c1', RelatedMessageId: 'M7', MediaFileName: 'clip.mp4', MediaMimeType: 'video/mp4', MediaChunkTotal: 3 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'c1', MediaChunkIndex: 0, MediaData: base64.slice(0, third) });
  // il pezzo 1 non arriva mai
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'c1', MediaChunkIndex: 2, MediaData: base64.slice(third * 2) });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'c1' });

  assert.strictEqual(sent, 0, 'un allegato incompleto non deve partire');
  const errors = handles.filter((f) => f.Command === 'error');
  assert.strictEqual(errors.length, 1);
  assert.match(errors[0].Text, /complete/i);
  assert.strictEqual(errors[0].RelatedMessageId, 'M7');
});

test('un pezzo con indice fuori dal totale dichiarato viene ignorato', async () => {
  let size = 0;
  const gowa = {
    sendVideo: async (phone, caption, buffer) => { size = buffer.length; return 'V1'; },
    sendImage: async () => { throw new Error('non e un video'); },
    sendFile: async () => { throw new Error('non e un video'); }
  };
  const bridge = mediaBridge(gowa);
  const bytes = Buffer.from('due pezzi e due soli');
  const base64 = bytes.toString('base64');

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'x1', MediaFileName: 'clip.mp4', MediaMimeType: 'video/mp4', MediaChunkTotal: 2 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'x1', MediaChunkIndex: 900000000, MediaData: base64 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'x1', MediaChunkIndex: 0, MediaData: base64 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'x1', MediaChunkIndex: 1, MediaData: '' });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'x1' });

  assert.strictEqual(size, bytes.length, 'il pezzo fuori intervallo non deve entrare nel file');
});

test('un allegato oltre il tetto si ferma mentre arriva, non alla fine', async () => {
  let sent = 0;
  const gowa = {
    sendVideo: async () => { sent++; return 'V1'; },
    sendImage: async () => { sent++; return 'I1'; },
    sendFile: async () => { sent++; return 'F1'; }
  };
  const handles = [];
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => handles.push(decodeFrame(packet)) });

  // Il tetto e' 64 MiB: si supera con pezzi da 1 MiB, e il guard deve fermarsi
  // mentre arrivano, senza mai chiamare Buffer.concat su tutto.
  const piece = Buffer.alloc(1024 * 1024, 7);
  const base64 = piece.toString('base64');
  const repeats = 68;

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'h1', MediaFileName: 'big.mp4', MediaMimeType: 'video/mp4', MediaChunkTotal: repeats });
  for (let i = 0; i < repeats; i++) {
    await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'h1', MediaChunkIndex: i, MediaData: base64 });
    if (handles.some((f) => f.Command === 'error')) break;
  }

  // L'errore deve essere arrivato mentre i pezzi arrivavano: se si aspetta
  // media.end, si e' tenuto in memoria tutto il file per decidere.
  assert.ok(handles.some((f) => f.Command === 'error'),
    'il tetto deve fermare il file mentre arriva, non alla fine');

  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'h1' });
  assert.strictEqual(sent, 0);
});

test('media.get scarica il media del messaggio e lo manda come frame di controllo', async () => {
  const sent = [];
  const gowa = {
    downloadMedia: async (phone, messageId) => {
      assert.strictEqual(phone, 'a@s.whatsapp.net');
      assert.strictEqual(messageId, 'M9');
      return { base64: Buffer.from([1, 2, 3]).toString('base64'), mimeType: 'image/jpeg', fileName: 'foto.jpg' };
    }
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({
    Type: 3, Command: 'media.get', Text: 'a@s.whatsapp.net', RelatedMessageId: 'M9'
  });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'media');
  assert.strictEqual(sent[0].ChatId, 'a@s.whatsapp.net');
  assert.strictEqual(sent[0].RelatedMessageId, 'M9');
  assert.strictEqual(sent[0].MediaData, Buffer.from([1, 2, 3]).toString('base64'));
  assert.strictEqual(sent[0].MediaMimeType, 'image/jpeg');
  assert.strictEqual(sent[0].Type, 3);
});

test('media.get dice che il media non c e piu invece di restare muto', async () => {
  const sent = [];
  const gowa = { downloadMedia: async () => null };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({
    Type: 3, Command: 'media.get', Text: 'a@s.whatsapp.net', RelatedMessageId: 'M9'
  });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'error');
  assert.strictEqual(sent[0].ChatId, 'a@s.whatsapp.net');
});

test('un media che non c e piu dice a quale messaggio si riferisce', async () => {
  const sent = [];
  const gowa = { downloadMedia: async () => null };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({
    Type: 3, Command: 'media.get', Text: 'a@s.whatsapp.net', RelatedMessageId: 'M9'
  });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'error');
  assert.strictEqual(sent[0].RelatedMessageId, 'M9');
});

test('un media troppo grande per un frame si scarica a pezzi, e l ordine si legge', async () => {
  const bytes = Buffer.alloc(600000, 7);          // base64: ~800000 caratteri, due pezzi
  const whole = bytes.toString('base64');
  const sent = [];
  const gowa = {
    downloadMedia: async () => ({ base64: whole, mimeType: 'video/mp4', fileName: 'clip.mp4' })
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({
    Type: 3, Command: 'media.get', Text: 'a@s.whatsapp.net', RelatedMessageId: 'M9'
  });

  const parts = sent.filter((f) => f.Command === 'media');
  assert.ok(parts.length > 1, 'un media grande deve viaggiare in piu di un frame');
  assert.strictEqual(parts[0].MediaType, 'video');
  assert.strictEqual(parts[0].RelatedMessageId, 'M9');
  assert.strictEqual(parts[0].MediaChunkTotal, parts.length);
  parts.forEach((part, i) => assert.strictEqual(part.MediaChunkIndex, i));
  // I pezzi si concatenano e danno il file intero: e' quello che fara' l app.
  assert.strictEqual(parts.map((p) => p.MediaData).join(''), whole);
});

test('un video in arrivo si annuncia come video e i byte seguono a pezzi', async () => {
  const sent = [];
  const video = Buffer.from('video finto che sta nel frame');
  const gowa = {
    fetchBinary: async () => ({ buffer: video, contentType: 'video/mp4' })
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleWebhookEvent({
    event: 'message',
    payload: {
      id: 'V1', chat_id: 'a@s.whatsapp.net', from: 'a@s.whatsapp.net',
      video: { path: 'statics/media/v.mp4' }, timestamp: '2026-09-27T08:00:00Z'
    }
  });

  const announced = sent[0];
  assert.strictEqual(announced.Type, 4);
  assert.strictEqual(announced.MediaType, 'video');
  assert.strictEqual(announced.MediaData, undefined);

  const bytes = sent.filter((f) => f.Command === 'media');
  assert.strictEqual(bytes.length, 1);
  assert.strictEqual(bytes[0].RelatedMessageId, 'V1');
  assert.strictEqual(bytes[0].MediaChunkTotal, 1);
  assert.strictEqual(bytes[0].MediaData, video.toString('base64'));
});

test('un vocale Ogg in arrivo arriva come MP3', async () => {
  const sent = [];
  const gowa = {
    fetchBinary: async () => ({ buffer: Buffer.from('vocali-opus'), contentType: 'audio/ogg' })
  };
  const transcoder = {
    probe: async () => true,
    toPlayable: async () => ({ buffer: Buffer.from('mp3-convertito'), mimeType: 'audio/mpeg', fileName: 'voce.mp3' })
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {}, transcoder });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleWebhookEvent({
    event: 'message',
    payload: {
      id: 'A1', chat_id: 'a@s.whatsapp.net', from: 'a@s.whatsapp.net',
      audio: { path: 'statics/media/v.ogg' }, timestamp: '2026-09-27T08:00:00Z'
    }
  });

  assert.strictEqual(sent[0].Type, 2);
  assert.strictEqual(sent[0].MediaType, 'audio');

  const bytes = sent.filter((f) => f.Command === 'media');
  assert.strictEqual(bytes.length, 1);
  assert.strictEqual(bytes[0].MediaType, 'audio');
  assert.strictEqual(bytes[0].MediaMimeType, 'audio/mpeg');
  assert.strictEqual(bytes[0].MediaFileName, 'voce.mp3');
  assert.strictEqual(bytes[0].MediaData, Buffer.from('mp3-convertito').toString('base64'));
});

test('senza ffmpeg un vocale in arrivo resta quello che e', async () => {
  const sent = [];
  const gowa = {
    fetchBinary: async () => ({ buffer: Buffer.from('vocali-opus'), contentType: 'audio/ogg' })
  };
  const transcoder = { probe: async () => false, toPlayable: async () => null };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {}, transcoder });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleWebhookEvent({
    event: 'message',
    payload: {
      id: 'A2', chat_id: 'a@s.whatsapp.net', from: 'a@s.whatsapp.net',
      audio: { path: 'statics/media/v.ogg' }, timestamp: '2026-09-27T08:00:00Z'
    }
  });

  const bytes = sent.filter((f) => f.Command === 'media');
  assert.strictEqual(bytes[0].MediaType, 'audio');
  assert.strictEqual(bytes[0].MediaMimeType, 'audio/ogg');
});

test('un vocale scaricato a richiesta diventa MP3', async () => {
  const sent = [];
  const gowa = {
    downloadMedia: async () => ({ base64: Buffer.from('opus').toString('base64'), mimeType: 'audio/ogg', fileName: 'voce.ogg' })
  };
  const transcoder = {
    probe: async () => true,
    toPlayable: async () => ({ buffer: Buffer.from('mp3'), mimeType: 'audio/mpeg', fileName: 'voce.mp3' })
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {}, transcoder });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({ Type: 3, Command: 'media.get', Text: 'a@s.whatsapp.net', RelatedMessageId: 'A3' });

  assert.strictEqual(sent[0].MediaType, 'audio');
  assert.strictEqual(sent[0].MediaMimeType, 'audio/mpeg');
  assert.strictEqual(sent[0].MediaFileName, 'voce.mp3');
});

test('un documento scaricato si dichiara documento', async () => {
  const sent = [];
  const gowa = {
    downloadMedia: async () => ({ base64: Buffer.from('pdf').toString('base64'), mimeType: 'application/pdf', fileName: 'contratto.pdf' })
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({ Type: 3, Command: 'media.get', Text: 'a@s.whatsapp.net', RelatedMessageId: 'D1' });

  assert.strictEqual(sent[0].MediaType, 'document');
  assert.strictEqual(sent[0].MediaFileName, 'contratto.pdf');
});

test('contact.info composes the profile of a person into one frame', async () => {
  const sent = [];
  const gowa = {
    avatar: async (jid) => {
      assert.strictEqual(jid, 'a@s.whatsapp.net');
      return 'AAAA';
    },
    userInfo: async () => ({ name: 'Anna', verifiedName: '', status: 'in giro', pictureId: 'P1' }),
    businessProfile: async () => ({
      email: 'info@bar.it', address: 'Via Roma 1', categories: ['Bar'], timezone: 'Europe/Rome', hours: []
    })
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({ Type: 3, Command: 'contact.info', Text: 'a@s.whatsapp.net' });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'contact.info');
  assert.strictEqual(sent[0].ChatId, 'a@s.whatsapp.net');
  assert.strictEqual(sent[0].Type, 3);
  const info = JSON.parse(sent[0].Text);
  assert.strictEqual(info.Name, 'Anna');
  assert.strictEqual(info.About, 'in giro');
  assert.strictEqual(info.Number, '');
  assert.strictEqual(info.AvatarData, 'AAAA');
  assert.strictEqual(info.Business.Email, 'info@bar.it');
  assert.strictEqual(info.Group, null);
});

test('contact.info reads the members and the description of a group', async () => {
  const sent = [];
  const gowa = {
    avatar: async () => null,
    groupParticipants: async () => ({
      name: 'Famiglia',
      participants: [
        { jid: '1@s.whatsapp.net', phoneNumber: '', displayName: 'Anna', isAdmin: true, isSuperAdmin: false },
        { jid: '2@s.whatsapp.net', phoneNumber: '', displayName: '', isAdmin: false, isSuperAdmin: false }
      ]
    }),
    groupInfo: async () => ({ name: 'Famiglia', topic: 'solo foto' })
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({ Type: 3, Command: 'contact.info', Text: '123@g.us' });

  const info = JSON.parse(sent[0].Text);
  assert.strictEqual(info.Name, 'Famiglia');
  assert.strictEqual(info.Group.Description, 'solo foto');
  assert.strictEqual(info.Group.Members.length, 2);
  assert.strictEqual(info.Group.Members[0].IsAdmin, true);
  assert.strictEqual(info.Group.Members[1].Name, '+2');
});

test('contact.info answers an empty profile instead of staying silent', async () => {
  const sent = [];
  const gowa = {
    avatar: async () => { throw new Error('senza rete'); },
    userInfo: async () => null,
    businessProfile: async () => null
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({ Type: 3, Command: 'contact.info', Text: 'a@s.whatsapp.net' });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'contact.info');
  assert.strictEqual(sent[0].ChatId, 'a@s.whatsapp.net');
  assert.deepStrictEqual(JSON.parse(sent[0].Text), {
    Name: '', About: '', Number: '', AvatarData: '', Business: null, Group: null
  });
});

test('un handshake senza token viene rifiutato se il servizio non registra', async () => {
  const crypto = require('crypto');
  const { createUserStore } = require('../users');

  // scrypt vero su un token ogni test e' lento: la logica e' la stessa, la
  // robustezza della funzione non e' quello che questo test misura.
  const users = createUserStore({
    scryptSync: (token, salt) => crypto.createHash('sha256').update(String(token) + salt).digest(),
    randomBytes: crypto.randomBytes
  });
  const { token } = users.register('vincenzo');

  // Registration off: this is the closed service, where the tokens are handed
  // out by hand and a phone without one has nothing to do here.
  const bridge = createBridge({
    config: { auth: { required: true, register: false } },
    gowa: {},
    users,
    log: () => {},
    debug: () => {}
  });

  const written = [];
  const destroyed = [];
  const socket = {
    write: (packet) => written.push(decodeFrame(packet)),
    destroy: () => destroyed.push(true)
  };
  bridge.addClientForTest(socket);

  await bridge.handleControl({ Type: 3, Command: 'hello', SenderName: 'x' }, socket);
  assert.strictEqual(written.length, 1);
  assert.strictEqual(written[0].Command, 'unauthorized');
  assert.strictEqual(destroyed.length, 1);

  written.length = 0;
  await bridge.handleControl({ Type: 3, Command: 'hello', Token: token, SenderName: 'x' }, socket);
  assert.ok(written.some((f) => f.Command === 'state'),
    'un token valido deve ricevere lo stato');
});

test('senza auth richiesta un handshake passa come prima', async () => {
  const sent = [];
  const bridge = createBridge({
    config: {},
    gowa: { status: async () => ({ isConnected: false, isLoggedIn: false, jid: '' }) },
    log: () => {},
    debug: () => {}
  });
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({ Type: 3, Command: 'hello', SenderName: 'x' });
  assert.ok(sent.some((f) => f.Command === 'state'));
  assert.ok(!sent.some((f) => f.Command === 'unauthorized'));
});

/**
 * Un servizio condiviso: due utenti, due token, due device GOWA. Il client di
 * base crea un device per ogni utente e ne restituisce uno con il suo id.
 */
function sharedBridge() {
  const crypto = require('crypto');
  const { createUserStore } = require('../users');

  const users = createUserStore({
    scryptSync: (token, salt) => crypto.createHash('sha256').update(String(token) + salt).digest(),
    randomBytes: crypto.randomBytes
  });
  const a = users.register('anna');
  const b = users.register('bruno');

  const created = [];
  // What GOWA answers about the login. It is a variable because the tests that
  // watch a user session change state flip it, the way a finished QR login does.
  let loggedIn = false;
  const status = async () => ({
    isConnected: true,
    isLoggedIn: loggedIn,
    jid: loggedIn ? '39@s.whatsapp.net' : ''
  });
  const base = {
    status: status,
    createDevice: async (label) => {
      const id = `dev-${created.length + 1}`;
      created.push({ id, label });
      return id;
    },
    // The presence of a user session: it is answered instead of missing so that
    // the tests do not depend on a swallowed TypeError.
    sendPresence: async () => true,
    withDevice: (id) => ({
      deviceId: id,
      setDeviceWebhook: async () => true,
      status: status,
      sendPresence: async () => true,
      sendChatPresence: async () => true
    })
  };

  // The lines the adapter logs, kept so that a test can assert that something was
  // routed (a message is accepted and logged) or was not (an unknown device).
  const logs = [];
  const bridge = createBridge({
    config: { auth: { required: true } },
    gowa: base,
    users,
    log: (level, message) => { logs.push(`${level} ${message}`); },
    debug: () => {}
  });
  return {
    bridge, a, b, users, created, logs,
    setLoggedIn: (value) => { loggedIn = value; }
  };
}

function collectingSocket() {
  const frames = [];
  return {
    frames,
    write: (packet) => frames.push(decodeFrame(packet)),
    destroy: () => {}
  };
}

test('ogni utente ha un device GOWA suo, creato al primo handshake', async () => {
  const { bridge, a, b, created } = sharedBridge();
  const socketA = collectingSocket();
  const socketB = collectingSocket();
  bridge.addClientForTest(socketA);
  bridge.addClientForTest(socketB);

  await bridge.handleControl({ Type: 3, Command: 'hello', Token: a.token, SenderName: 'a' }, socketA);
  await bridge.handleControl({ Type: 3, Command: 'hello', Token: b.token, SenderName: 'b' }, socketB);

  assert.strictEqual(created.length, 2, 'un device per utente');
  assert.notStrictEqual(a.user.deviceId, b.user.deviceId);
  // L'etichetta e' il nome dell'utente: senza, i device sono anonimi.
  assert.ok(created[0].label.indexOf('anna') !== -1);
  assert.ok(created[1].label.indexOf('bruno') !== -1);
});

test('lo stato di una sessione utente viene riletto a ogni giro di polling', async () => {
  const { bridge, a, setLoggedIn } = sharedBridge();
  const socket = collectingSocket();
  bridge.addClientForTest(socket);
  await bridge.handleControl({ Type: 3, Command: 'hello', Token: a.token, SenderName: 'anna' }, socket);

  // Il telefono ha appena finito il login QR: GOWA lo sa, l adapter no.
  setLoggedIn(true);
  await bridge.refreshStatus();

  const states = socket.frames.filter((f) => f.Command === 'state');
  assert.strictEqual(states[states.length - 1].State, 'connected',
    'senza questa rilettura il telefono resta su waiting e non chiede mai la lista');
});

test('status chiede a GOWA lo stato della sessione invece di quello che ricorda', async () => {
  const { bridge, a, setLoggedIn } = sharedBridge();
  const socket = collectingSocket();
  bridge.addClientForTest(socket);
  await bridge.handleControl({ Type: 3, Command: 'hello', Token: a.token, SenderName: 'anna' }, socket);

  setLoggedIn(true);
  await bridge.handleControl({ Type: 3, Command: 'status' }, socket);

  const states = socket.frames.filter((f) => f.Command === 'state');
  assert.strictEqual(states[states.length - 1].State, 'connected');
});

test('un messaggio per il device di un utente non arriva all altro', async () => {
  const { bridge, a, b } = sharedBridge();
  const socketA = collectingSocket();
  const socketB = collectingSocket();
  bridge.addClientForTest(socketA);
  bridge.addClientForTest(socketB);

  await bridge.handleControl({ Type: 3, Command: 'hello', Token: a.token, SenderName: 'a' }, socketA);
  await bridge.handleControl({ Type: 3, Command: 'hello', Token: b.token, SenderName: 'b' }, socketB);

  await bridge.handleWebhookEvent({
    event: 'message',
    device_id: a.user.deviceId,
    payload: {
      id: 'X1', chat_id: '39@s.whatsapp.net', from: '39@s.whatsapp.net',
      sender_display_name: 'Mario', body: 'solo per Anna', timestamp: '2026-01-02T03:04:05Z'
    }
  });

  const annaMessages = socketA.frames.filter((f) => f.Text === 'solo per Anna');
  const brunoMessages = socketB.frames.filter((f) => f.Text === 'solo per Anna');
  assert.strictEqual(annaMessages.length, 1, 'il messaggio arriva al suo utente');
  assert.strictEqual(brunoMessages.length, 0, 'e a nessun altro');
});

test('un webhook con il JID dell account come device_id arriva al suo utente', async () => {
  const { bridge, a, setLoggedIn } = sharedBridge();
  const socket = collectingSocket();
  bridge.addClientForTest(socket);
  await bridge.handleControl({ Type: 3, Command: 'hello', Token: a.token, SenderName: 'anna' }, socket);

  // GOWA mette il JID dell'account sul webhook, non l'UUID che elenca in
  // /devices: senza l'indice per JID ogni messaggio finisce nel vuoto, ed e'
  // quello che si vede come "le chat non si aggiornano".
  setLoggedIn(true);
  await bridge.refreshStatus();

  await bridge.handleWebhookEvent({
    event: 'message',
    device_id: '39@s.whatsapp.net',
    payload: {
      id: 'X3', chat_id: '39@s.whatsapp.net', from: '39@s.whatsapp.net',
      sender_display_name: 'Mario', body: 'via JID', timestamp: '2026-01-02T03:04:05Z'
    }
  });

  assert.strictEqual(socket.frames.filter((f) => f.Text === 'via JID').length, 1);
});

test('il device_id con la parte device del JID viene comunque instradato', async () => {
  const { bridge, a, setLoggedIn } = sharedBridge();
  const socket = collectingSocket();
  bridge.addClientForTest(socket);
  await bridge.handleControl({ Type: 3, Command: 'hello', Token: a.token, SenderName: 'anna' }, socket);

  // WhatsApp scrive la parte device nel JID (`39:92@s.whatsapp.net`): i due nomi
  // sono lo stesso account e devono instradare allo stesso modo.
  setLoggedIn(true);
  await bridge.refreshStatus();

  await bridge.handleWebhookEvent({
    event: 'message',
    device_id: '39:92@s.whatsapp.net',
    payload: {
      id: 'X4', chat_id: '39@s.whatsapp.net', from: '39@s.whatsapp.net',
      sender_display_name: 'Mario', body: 'via JID con device', timestamp: '2026-01-02T03:04:05Z'
    }
  });

  assert.strictEqual(socket.frames.filter((f) => f.Text === 'via JID con device').length, 1);
});

test('un webhook per un device sconosciuto non arriva a nessuno', async () => {
  const { bridge, a } = sharedBridge();
  const socketA = collectingSocket();
  bridge.addClientForTest(socketA);
  await bridge.handleControl({ Type: 3, Command: 'hello', Token: a.token, SenderName: 'a' }, socketA);

  await bridge.handleWebhookEvent({
    event: 'message',
    device_id: 'dev-sconosciuto',
    payload: {
      id: 'X2', chat_id: '39@s.whatsapp.net', from: '39@s.whatsapp.net', body: 'per nessuno'
    }
  });

  assert.strictEqual(socketA.frames.filter((f) => f.Text === 'per nessuno').length, 0);
});

test('un webhook arriva al suo utente anche senza nessun telefono collegato', async () => {
  const { bridge, setLoggedIn, logs } = sharedBridge();
  setLoggedIn(true);

  // Nessun socket: e' il caso del telefono spento, quello per cui esiste il
  // conteggio dei non letti. Le sessioni degli utenti conosciuti si aprono lo
  // stesso, cosi' il JID dell account e' noto prima che arrivi il webhook.
  await bridge.openKnownSessions();
  await bridge.refreshStatus();

  await bridge.handleWebhookEvent({
    event: 'message',
    device_id: '39@s.whatsapp.net',
    payload: {
      id: 'M1', chat_id: 'b@s.whatsapp.net', from: 'b@s.whatsapp.net',
      body: 'ciao', timestamp: '2026-09-30T19:00:00Z', is_from_me: false
    }
  });

  assert.ok(logs.some((l) => l.indexOf('MSG') === 0),
    'il messaggio deve essere accettato: senza instradamento veniva scartato');
  assert.ok(!logs.some((l) => l.indexOf('unknown device') !== -1),
    'nessun webhook deve finire fra i device sconosciuti');
});

test('un comando prima del token viene rifiutato sul servizio condiviso', async () => {
  const { bridge } = sharedBridge();
  const socket = collectingSocket();
  bridge.addClientForTest(socket);

  await bridge.handleControl({ Type: 3, Command: 'chats' }, socket);

  assert.strictEqual(socket.frames.length, 1);
  assert.strictEqual(socket.frames[0].Command, 'unauthorized');
});

test('un telefono senza token viene registrato al primo handshake', async () => {
  const { bridge, users, created } = sharedBridge();
  const socket = collectingSocket();
  bridge.addClientForTest(socket);

  await bridge.handleControl({ Type: 3, Command: 'hello', SenderName: 'carla' }, socket);

  assert.ok(!socket.frames.some((f) => f.Command === 'unauthorized'), 'non viene rifiutato');
  const registered = socket.frames.find((f) => f.Command === 'registered');
  assert.ok(registered, 'il token arriva in un frame registered');
  assert.ok(registered.Token, 'il frame porta il token');
  assert.ok(users.verify(registered.Token), 'il token e gia valido');
  assert.strictEqual(users.count(), 3, 'il terzo utente e questo telefono');
  assert.ok(socket.frames.some((f) => f.Command === 'state'), 'la sessione parte subito');
  assert.strictEqual(created.length, 1, 'con il suo device GOWA');

  // The handshake authenticated the socket in the same step: the command the app
  // sends right after it is accepted instead of refused.
  await bridge.handleControl({ Type: 3, Command: 'chats' }, socket);
  assert.ok(!socket.frames.some((f) => f.Command === 'unauthorized'));
});

test('il tetto degli utenti ferma la registrazione automatica', async () => {
  const crypto = require('crypto');
  const { createUserStore } = require('../users');

  const users = createUserStore({
    scryptSync: (token, salt) => crypto.createHash('sha256').update(String(token) + salt).digest(),
    randomBytes: crypto.randomBytes
  });

  const bridge = createBridge({
    config: { auth: { required: true, maxUsers: 1 } },
    gowa: { status: async () => ({ isConnected: true, isLoggedIn: false, jid: '' }) },
    users,
    log: () => {},
    debug: () => {}
  });

  const first = collectingSocket();
  const second = collectingSocket();
  bridge.addClientForTest(first);
  bridge.addClientForTest(second);

  await bridge.handleControl({ Type: 3, Command: 'hello', SenderName: 'uno' }, first);
  assert.ok(first.frames.some((f) => f.Command === 'registered'), 'il primo entra');

  await bridge.handleControl({ Type: 3, Command: 'hello', SenderName: 'due' }, second);
  assert.strictEqual(users.count(), 1, 'il tetto non viene superato');
  assert.strictEqual(second.frames[0].Command, 'unauthorized', 'il secondo viene rifiutato');
});

test('i frame di un socket vengono gestiti nell ordine in cui arrivano', async () => {
  const crypto = require('crypto');
  const { createUserStore } = require('../users');

  const users = createUserStore({
    scryptSync: (token, salt) => crypto.createHash('sha256').update(String(token) + salt).digest(),
    randomBytes: crypto.randomBytes
  });
  const anna = users.register('anna');

  const created = [];
  const gowa = {
    status: async () => ({ isConnected: true, isLoggedIn: false, jid: '' }),
    // Slow on purpose: the handshake is what the frame after it must wait for.
    createDevice: async (label) => {
      await new Promise((r) => setTimeout(r, 20));
      const id = 'dev-' + (created.length + 1);
      created.push({ id, label });
      return id;
    },
    withDevice: (id) => ({ deviceId: id, setDeviceWebhook: async () => true })
  };

  const bridge = createBridge({
    config: { bridge: { port: 0 }, webhook: {}, auth: { required: true } },
    gowa,
    users,
    log: noop,
    debug: noop
  });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);

  try {
    // The handshake and the command leave together, as the app sends them.
    client.send({ Type: 3, ChatId: 'system', Command: 'hello', Token: anna.token, SenderName: 'anna' });
    client.send({ Type: 3, ChatId: 'system', Command: 'chats' });

    await new Promise((r) => setTimeout(r, 150));

    assert.strictEqual(client.messages.filter((m) => m.Command === 'unauthorized').length, 0,
      'nessun rifiuto: il comando aspetta l handshake');
    assert.ok(client.messages.some((m) => m.Command === 'chats.done'),
      'il comando dopo l handshake viene eseguito');
  } finally {
    client.socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});
