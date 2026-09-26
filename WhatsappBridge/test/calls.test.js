'use strict';

const test = require('node:test');
const assert = require('node:assert');

const { parseCallMetadata, collectCalls } = require('../calls');

test('parseCallMetadata reads the keys GOWA stores', () => {
  const meta = parseCallMetadata('{"call_id":"ABC123","reason":"timeout","duration":42}');
  assert.strictEqual(meta.callId, 'ABC123');
  assert.strictEqual(meta.reason, 'timeout');
  assert.strictEqual(meta.durationSeconds, 42);
  assert.strictEqual(meta.isVideo, false);
});

test('parseCallMetadata keeps working when keys are missing', () => {
  const meta = parseCallMetadata('{"call_id":"ABC123"}');
  assert.strictEqual(meta.callId, 'ABC123');
  assert.strictEqual(meta.reason, '');
  assert.strictEqual(meta.durationSeconds, 0);
});

test('parseCallMetadata survives malformed JSON and empty input', () => {
  for (const bad of ['{not json', '', null, undefined, 42, '[]']) {
    const meta = parseCallMetadata(bad);
    assert.strictEqual(meta.callId, '');
    assert.strictEqual(meta.reason, '');
    assert.strictEqual(meta.durationSeconds, 0);
    assert.strictEqual(meta.isVideo, false);
  }
});

test('parseCallMetadata reads the video flag when it is there', () => {
  assert.strictEqual(parseCallMetadata('{"is_video":true}').isVideo, true);
  assert.strictEqual(parseCallMetadata('{"video":true}').isVideo, true);
  assert.strictEqual(parseCallMetadata('{"is_video":false}').isVideo, false);
});

function fakeGowa({ chats, messagesByJid, failFor }) {
  return {
    chats: async (limit) => chats.slice(0, limit),
    chatMessages: async (jid) => {
      if (failFor && failFor.indexOf(jid) !== -1) throw new Error('chatstorage unavailable');
      return messagesByJid[jid] || [];
    },
  };
}

test('collectCalls keeps only call rows and sorts them newest first', async () => {
  const gowa = fakeGowa({
    chats: [{ jid: 'a@s.whatsapp.net', name: 'Anna' }, { jid: 'b@s.whatsapp.net', name: 'Bruno' }],
    messagesByJid: {
      'a@s.whatsapp.net': [
        { id: 'm1', chat_jid: 'a@s.whatsapp.net', media_type: 'text', content: 'ciao', timestamp: '2026-09-24T10:00:00Z' },
        { id: 'm2', chat_jid: 'a@s.whatsapp.net', media_type: 'call', call_metadata: '{"call_id":"C1","reason":"timeout"}', timestamp: '2026-09-24T09:00:00Z' },
      ],
      'b@s.whatsapp.net': [
        { id: 'm3', chat_jid: 'b@s.whatsapp.net', media_type: 'call', call_metadata: '{"call_id":"C2"}', timestamp: '2026-09-25T08:00:00Z' },
      ],
    },
  });

  const calls = await collectCalls({ gowa, chatLimit: 10, messagesPerChat: 50, limit: 10, log: () => {} });
  assert.strictEqual(calls.length, 2);
  assert.strictEqual(calls[0].callId, 'C2');
  assert.strictEqual(calls[0].chatName, 'Bruno');
  assert.strictEqual(calls[1].callId, 'C1');
  assert.strictEqual(calls[1].reason, 'timeout');
});

test('collectCalls skips a chat it cannot read and keeps going', async () => {
  const gowa = fakeGowa({
    chats: [{ jid: 'a@s.whatsapp.net', name: 'Anna' }, { jid: 'b@s.whatsapp.net', name: 'Bruno' }],
    messagesByJid: {
      'b@s.whatsapp.net': [
        { id: 'm3', chat_jid: 'b@s.whatsapp.net', media_type: 'call', call_metadata: '{"call_id":"C2"}', timestamp: '2026-09-25T08:00:00Z' },
      ],
    },
    failFor: ['a@s.whatsapp.net'],
  });

  const calls = await collectCalls({ gowa, chatLimit: 10, messagesPerChat: 50, limit: 10, log: () => {} });
  assert.strictEqual(calls.length, 1);
  assert.strictEqual(calls[0].chatId, 'b@s.whatsapp.net');
});

test('collectCalls caps the result and tolerates a missing chat name', async () => {
  const rows = [];
  for (let i = 0; i < 5; i++) {
    rows.push({ id: 'm' + i, chat_jid: 'a@s.whatsapp.net', media_type: 'call', call_metadata: '{}', timestamp: '2026-09-2' + i + 'T08:00:00Z' });
  }
  const gowa = fakeGowa({ chats: [{ jid: 'a@s.whatsapp.net' }], messagesByJid: { 'a@s.whatsapp.net': rows } });

  const calls = await collectCalls({ gowa, chatLimit: 10, messagesPerChat: 50, limit: 3, log: () => {} });
  assert.strictEqual(calls.length, 3);
  assert.strictEqual(calls[0].chatId, 'a@s.whatsapp.net');
  assert.strictEqual(calls[0].chatName, '');
});
