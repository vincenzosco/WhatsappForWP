'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { GowaClient, errorMessage } = require('../gowa-client');

test('chats() asks for a bounded list and reads results.data', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      seen.push(url);
      return { ok: true, status: 200, text: async () => JSON.stringify({ results: { data: [{ jid: 'a@s.whatsapp.net' }] } }) };
    }
  });

  const chats = await client.chats(25);
  assert.strictEqual(seen[0], 'http://127.0.0.1:3000/chats?limit=25');
  assert.strictEqual(chats.length, 1);
  assert.strictEqual(chats[0].jid, 'a@s.whatsapp.net');
});

test('chatMessages() encodes the jid in the path', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      seen.push(url);
      return { ok: true, status: 200, text: async () => JSON.stringify({ results: { data: [] } }) };
    }
  });

  await client.chatMessages('393401234567@s.whatsapp.net', 100);
  assert.strictEqual(seen[0], 'http://127.0.0.1:3000/chat/393401234567%40s.whatsapp.net/messages?limit=100');
});

function jsonResponse(body, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: () => 'application/json' },
    text: async () => JSON.stringify(body),
    arrayBuffer: async () => Buffer.from(JSON.stringify(body))
  };
}

function makeFetch(handler) {
  const calls = [];
  const impl = async (url, options) => {
    calls.push({ url, options });
    return handler(url, options);
  };
  impl.calls = calls;
  return impl;
}

test('errorMessage preferisce il campo message della risposta GOWA', () => {
  assert.strictEqual(errorMessage({ message: 'Boom' }, 'fallback'), 'Boom');
  assert.strictEqual(errorMessage(null, 'fallback'), 'fallback');
});

test('ensureDevice crea un device quando la lista è vuota', async () => {
  const fetchImpl = makeFetch(async (url, options) => {
    if (url.endsWith('/devices')) {
      if (options.method === 'POST') return jsonResponse({ status: 200, results: { id: 'org_1' } });
      return jsonResponse({ status: 200, results: [] });
    }
    return jsonResponse({});
  });
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const id = await client.ensureDevice();
  assert.strictEqual(id, 'org_1');
  assert.strictEqual(fetchImpl.calls[0].url, 'http://g/devices');
  assert.strictEqual(fetchImpl.calls[0].options.method, 'GET');
  assert.strictEqual(fetchImpl.calls[1].options.method, 'POST');
});

test('ensureDevice riusa il primo device esistente', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 200, results: [{ id: 'org_9' }] }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  assert.strictEqual(await client.ensureDevice(), 'org_9');
  assert.strictEqual(fetchImpl.calls.length, 1);
});

test('loginQr restituisce qr_link e qr_duration', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({
    status: 200, results: { device_id: 'd', qr_link: 'http://g/statics/qr.png', qr_duration: 30 }
  }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const qr = await client.loginQr();
  assert.strictEqual(qr.qrLink, 'http://g/statics/qr.png');
  assert.strictEqual(qr.duration, 30);
});

test('loginWithCode passa phone e legge pair_code', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 200, results: { pair_code: 'ABCD-EFGH' } }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const code = await client.loginWithCode('393401234567');
  assert.strictEqual(code, 'ABCD-EFGH');
  assert.match(fetchImpl.calls[0].url, /login-with-code\?phone=393401234567/);
});

test('sendText invia il body JSON e legge message_id', async () => {
  const fetchImpl = makeFetch(async (url, options) => {
    assert.strictEqual(url, 'http://g/send/message');
    assert.strictEqual(options.body, JSON.stringify({ phone: '39@s.whatsapp.net', message: 'ciao' }));
    return jsonResponse({ status: 200, results: { message_id: 'M1', status: 'PENDING' } });
  });
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  assert.strictEqual(await client.sendText('39@s.whatsapp.net', 'ciao'), 'M1');
});

test('status tollera gli errori HTTP', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 400, code: 'DEVICE_ID_REQUIRED' }, 400));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const s = await client.status();
  assert.deepStrictEqual(s, { isConnected: false, isLoggedIn: false, jid: '' });
});

test('aggiunge gli header di autenticazione e X-Device-Id', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 200, results: { is_logged_in: true, jid: '39@x' } }));
  const client = new GowaClient({ baseUrl: 'http://g', user: 'admin', pass: 'secret', deviceId: 'd1', fetchImpl });
  await client.status();
  const headers = fetchImpl.calls[0].options.headers;
  assert.strictEqual(headers.Authorization, 'Basic ' + Buffer.from('admin:secret').toString('base64'));
  assert.strictEqual(headers['X-Device-Id'], 'd1');
});

test('avatar() asks GOWA for the person, never for a group, and returns base64', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      seen.push(url);
      return {
        ok: true,
        status: 200,
        headers: { get: () => 'image/jpeg' },
        arrayBuffer: async () => new Uint8Array([1, 2, 3]).buffer
      };
    }
  });

  const picture = await client.avatar('393401234567@s.whatsapp.net');
  assert.strictEqual(seen[0], 'http://127.0.0.1:3000/user/avatar?phone=393401234567&is_preview=true');
  assert.strictEqual(picture, Buffer.from([1, 2, 3]).toString('base64'));

  assert.strictEqual(await client.avatar('123456789012345678@g.us'), null);
  assert.strictEqual(seen.length, 1);
});

test('avatar() returns null when GOWA has no picture', async () => {
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async () => ({ ok: false, status: 404, headers: { get: () => null } })
  });

  assert.strictEqual(await client.avatar('393401234567@s.whatsapp.net'), null);
});
