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
