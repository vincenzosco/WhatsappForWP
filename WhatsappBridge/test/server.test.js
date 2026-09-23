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
  const waiters = [];
  socket.on('data', (chunk) => {
    buffer = Buffer.concat([buffer, chunk]);
    while (buffer.length >= 4) {
      const len = buffer.readUInt32LE(0);
      if (buffer.length < 4 + len) break;
      const payload = buffer.slice(4, 4 + len);
      buffer = buffer.slice(4 + len);
      const json = JSON.parse(cryptoHelper.decodePayload(payload));
      messages.push(json);
      while (waiters.length) waiters.shift()(json);
    }
  });
  return {
    socket,
    messages,
    send(msg) {
      socket.write(cryptoHelper.buildFrame(JSON.stringify(msg)));
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
