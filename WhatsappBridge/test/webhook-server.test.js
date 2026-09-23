'use strict';
const test = require('node:test');
const assert = require('node:assert');
const crypto = require('node:crypto');
const { createWebhookServer, verifySignature } = require('../webhook-server');

const noopLog = () => {};

function listen(server) {
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(server.address().port)));
}

test('verifySignature accetta quando non c\'è secret', () => {
  assert.strictEqual(verifySignature(Buffer.from('x'), undefined, ''), true);
});

test('verifySignature convalida l\'HMAC sha256', () => {
  const body = Buffer.from(JSON.stringify({ event: 'message' }));
  const sig = 'sha256=' + crypto.createHmac('sha256', 'k').update(body).digest('hex');
  assert.strictEqual(verifySignature(body, sig, 'k'), true);
  assert.strictEqual(verifySignature(body, 'sha256=deadbeef', 'k'), false);
  assert.strictEqual(verifySignature(body, undefined, 'k'), false);
});

test('il webhook consegna gli eventi firmati e risponde 200', async () => {
  const received = [];
  const server = createWebhookServer({
    path: '/webhook', secret: 'k', onEvent: async (e) => received.push(e), log: noopLog
  });
  const port = await listen(server);
  try {
    const body = JSON.stringify({ event: 'message', payload: { id: '1', body: 'hi' } });
    const sig = 'sha256=' + crypto.createHmac('sha256', 'k').update(body).digest('hex');
    const res = await fetch(`http://127.0.0.1:${port}/webhook`, {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Hub-Signature-256': sig }, body
    });
    assert.strictEqual(res.status, 200);
    await new Promise((r) => setTimeout(r, 20));
    assert.strictEqual(received.length, 1);
    assert.strictEqual(received[0].payload.body, 'hi');
  } finally {
    server.close();
  }
});

test('il webhook rifiuta firme errate e path sconosciuti', async () => {
  const received = [];
  const server = createWebhookServer({
    path: '/webhook', secret: 'k', onEvent: async (e) => received.push(e), log: noopLog
  });
  const port = await listen(server);
  try {
    const bad = await fetch(`http://127.0.0.1:${port}/webhook`, {
      method: 'POST', headers: { 'X-Hub-Signature-256': 'sha256=00' }, body: '{}'
    });
    assert.strictEqual(bad.status, 401);
    const missing = await fetch(`http://127.0.0.1:${port}/nope`, { method: 'POST', body: '{}' });
    assert.strictEqual(missing.status, 404);
    assert.strictEqual(received.length, 0);
  } finally {
    server.close();
  }
});
