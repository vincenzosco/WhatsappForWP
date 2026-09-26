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
