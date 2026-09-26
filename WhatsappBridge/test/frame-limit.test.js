'use strict';

const test = require('node:test');
const assert = require('node:assert');
const net = require('node:net');
const { createBridge, MAX_FRAME_LENGTH } = require('../server');
const cryptoHelper = require('../crypto-helper');

function startBridge() {
  const lines = [];
  const log = (level, ...args) => lines.push(`${level} ${args.join(' ')}`);
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({ config, gowa: {}, log, debug: () => {} });
  return new Promise((resolve) => {
    bridge.tcpServer.listen(0, '127.0.0.1', () => {
      resolve({ bridge, lines, port: bridge.tcpServer.address().port });
    });
  });
}

function waitForClose(socket) {
  // Un socket in pausa non emette 'end' ne' 'close': senza resume il test
  // aspetterebbe per sempre una chiusura gia' avvenuta.
  socket.resume();
  return new Promise((resolve, reject) => {
    const timer = setTimeout(
      () => reject(new Error('il server non ha chiuso la connessione')), 2000);
    socket.once('close', () => { clearTimeout(timer); resolve(); });
  });
}

test('MAX_FRAME_LENGTH e\' 8 MiB, la stessa soglia dell\'app WP8', () => {
  assert.strictEqual(MAX_FRAME_LENGTH, 8 * 1024 * 1024);
});

test('una lunghezza annunciata oltre il limite chiude la connessione, non attende i byte', async () => {
  const { bridge, lines, port } = await startBridge();
  const socket = net.connect(port, '127.0.0.1');
  try {
    await new Promise((resolve) => socket.once('connect', resolve));

    // Solo il prefisso: se il server lo accettasse resterebbe in attesa di
    // MAX_FRAME_LENGTH + 1 byte che non arrivano mai.
    const header = Buffer.alloc(4);
    header.writeUInt32LE(MAX_FRAME_LENGTH + 1, 0);
    socket.write(header);

    await waitForClose(socket);
    assert.ok(
      lines.some((line) => line.includes('length')),
      'the reason for the close must reach the log: ' + JSON.stringify(lines));
  } finally {
    socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('una lunghezza annunciata a zero chiude la connessione invece di girare a vuoto', async () => {
  const { bridge, port } = await startBridge();
  const socket = net.connect(port, '127.0.0.1');
  try {
    await new Promise((resolve) => socket.once('connect', resolve));

    const header = Buffer.alloc(4);
    header.writeUInt32LE(0, 0);
    socket.write(header);

    await waitForClose(socket);
  } finally {
    socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('un frame normale resta accettato dopo il limite', async () => {
  const { bridge, port } = await startBridge();
  const socket = net.connect(port, '127.0.0.1');
  const messages = [];
  let buffer = Buffer.alloc(0);
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
  try {
    await new Promise((resolve) => socket.once('connect', resolve));

    // Il primo frame che il bridge manda da solo: lo stato al collegamento.
    const first = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('timeout')), 2000);
      waiters.push((m) => { clearTimeout(timer); resolve(m); });
      if (messages.length) { clearTimeout(timer); resolve(messages.shift()); }
    });
    assert.strictEqual(first.Command, 'state');
  } finally {
    socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});
