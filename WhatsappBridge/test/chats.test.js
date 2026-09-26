'use strict';

const test = require('node:test');
const assert = require('node:assert');

const { previewForMessage, collectChats } = require('../chats');

test('previewForMessage uses the body when there is one', () => {
  assert.strictEqual(previewForMessage({ content: 'ciao', media_type: 'text' }), 'ciao');
});

test('previewForMessage names the media when the body is empty', () => {
  assert.strictEqual(previewForMessage({ media_type: 'image' }), '[Image]');
  assert.strictEqual(previewForMessage({ media_type: 'video' }), '[Video]');
  assert.strictEqual(previewForMessage({ media_type: 'audio' }), '[Audio]');
  assert.strictEqual(previewForMessage({ media_type: 'document' }), '[Document]');
  assert.strictEqual(previewForMessage({ media_type: 'sticker' }), '[Sticker]');
  assert.strictEqual(previewForMessage({}), '');
  assert.strictEqual(previewForMessage(null), '');
});

function fakeGowa(options) {
  const opts = options || {};
  return {
    chats: async (limit) => (opts.chats || []).slice(0, limit),
    chatMessages: async (jid) => (opts.messagesByJid || {})[jid] || [],
    avatar: async (jid) => {
      if ((opts.failAvatarFor || []).indexOf(jid) !== -1) throw new Error('no picture');
      return (opts.avatarsByJid || {})[jid] || null;
    }
  };
}

test('collectChats keeps the newest message as the preview and sorts by it', async () => {
  const gowa = fakeGowa({
    chats: [
      { jid: 'a@s.whatsapp.net', name: 'Anna' },
      { jid: 'b@s.whatsapp.net', name: 'Bruno' }
    ],
    messagesByJid: {
      'a@s.whatsapp.net': [
        { content: 'vecchio', timestamp: '2026-09-24T09:00:00Z' },
        { content: 'nuovo', timestamp: '2026-09-26T09:00:00Z' }
      ],
      'b@s.whatsapp.net': [{ content: 'ciao', timestamp: '2026-09-25T08:00:00Z' }]
    },
    avatarsByJid: {}
  });

  const rows = await collectChats({ gowa, limit: 10, avatars: false, log: () => {} });
  assert.strictEqual(rows.length, 2);
  assert.strictEqual(rows[0].chatId, 'a@s.whatsapp.net');
  assert.strictEqual(rows[0].preview, 'nuovo');
  assert.strictEqual(rows[1].preview, 'ciao');
  assert.strictEqual(rows[0].isGroup, false);
});

test('collectChats fills the name from the jid when GOWA does not send one', async () => {
  const gowa = fakeGowa({
    chats: [{ jid: '393401234567@s.whatsapp.net' }, { jid: '123@g.us' }],
    messagesByJid: {},
    avatarsByJid: {}
  });

  const rows = await collectChats({ gowa, limit: 10, avatars: false, log: () => {} });
  assert.strictEqual(rows.find((r) => r.chatId === '393401234567@s.whatsapp.net').name, '+393401234567');
  assert.strictEqual(rows.find((r) => r.chatId === '123@g.us').isGroup, true);
});

test('collectChats asks for the avatar of people only, and survives a failure', async () => {
  const asked = [];
  const gowa = {
    chats: async () => [
      { jid: 'a@s.whatsapp.net', name: 'Anna' },
      { jid: '123@g.us', name: 'Gruppo' }
    ],
    chatMessages: async () => [],
    avatar: async (jid) => {
      asked.push(jid);
      return jid === 'a@s.whatsapp.net' ? 'AAAA' : null;
    }
  };

  const rows = await collectChats({ gowa, limit: 10, avatars: true, log: () => {} });
  assert.deepStrictEqual(asked, ['a@s.whatsapp.net']);
  assert.strictEqual(rows.find((r) => r.chatId === 'a@s.whatsapp.net').avatar, 'AAAA');

  // Un avatar che non si scarica e' un avatar in meno, non una chat in meno.
  const failing = fakeGowa({
    chats: [{ jid: 'a@s.whatsapp.net', name: 'Anna' }],
    messagesByJid: { 'a@s.whatsapp.net': [{ content: 'x', timestamp: '2026-09-26T09:00:00Z' }] },
    avatarsByJid: {},
    failAvatarFor: ['a@s.whatsapp.net']
  });
  const again = await collectChats({ gowa: failing, limit: 10, avatars: true, log: () => {} });
  assert.strictEqual(again.length, 1);
  assert.strictEqual(again[0].avatar, null);
});

test('collectChats caps the result and skips a chat it cannot read', async () => {
  const gowa = {
    chats: async () => [
      { jid: 'a@s.whatsapp.net', name: 'Anna' },
      { jid: 'b@s.whatsapp.net', name: 'Bruno' },
      { jid: 'c@s.whatsapp.net', name: 'Carla' }
    ],
    chatMessages: async (jid) => {
      if (jid === 'b@s.whatsapp.net') throw new Error('chatstorage unavailable');
      return [{ content: jid[0], timestamp: '2026-09-26T09:00:00Z' }];
    },
    avatar: async () => null
  };

  const rows = await collectChats({ gowa, limit: 2, avatars: false, log: () => {} });
  assert.strictEqual(rows.length, 2);
  assert.ok(rows.every((r) => r.chatId !== 'b@s.whatsapp.net'));
});
