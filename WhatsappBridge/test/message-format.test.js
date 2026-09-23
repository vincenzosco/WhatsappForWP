'use strict';
const test = require('node:test');
const assert = require('node:assert');
const {
  formatDateForWp8,
  displayNameForJid,
  buildChatMessage,
  mapWebhookMessage
} = require('../message-format');

test('formatDateForWp8 usa il formato Microsoft /Date(ms)/', () => {
  assert.strictEqual(formatDateForWp8(new Date(0)), '\\/Date(0)\\/');
  assert.strictEqual(formatDateForWp8(new Date(1700000000000)), '\\/Date(1700000000000)\\/');
});

test('displayNameForJid gestisce numeri e gruppi', () => {
  assert.strictEqual(displayNameForJid('393401234567@s.whatsapp.net'), '+393401234567');
  assert.strictEqual(displayNameForJid('123456789012345678@g.us'), 'Gruppo 123456789012345678');
});

test('buildChatMessage applica i default e i campi di controllo', () => {
  const m = buildChatMessage({ command: 'state', state: 'connected', accountJid: '39@s.whatsapp.net' });
  assert.strictEqual(m.Type, 3);
  assert.strictEqual(m.Command, 'state');
  assert.strictEqual(m.State, 'connected');
  assert.strictEqual(m.AccountJid, '39@s.whatsapp.net');
  assert.strictEqual(m.Status, 1);
  assert.strictEqual(m.IsIncoming, true);
  assert.strictEqual(m.MediaData, undefined);
});

test('buildChatMessage include i campi media solo quando presenti', () => {
  const m = buildChatMessage({ text: 'ciao', mediaData: 'AAAA', mediaMimeType: 'image/png' });
  assert.strictEqual(m.MediaData, 'AAAA');
  assert.strictEqual(m.MediaMimeType, 'image/png');
});

test('mapWebhookMessage ignora i messaggi inviati da me', () => {
  assert.strictEqual(mapWebhookMessage({ is_from_me: true, body: 'x' }), null);
});

test('mapWebhookMessage ignora status broadcast', () => {
  assert.strictEqual(mapWebhookMessage({ chat_id: 'status@broadcast' }), null);
});

test('mapWebhookMessage mappa un messaggio di testo', () => {
  const f = mapWebhookMessage({
    id: 'ABC', chat_id: '393401234567@s.whatsapp.net', from: '393401234567@s.whatsapp.net',
    sender_display_name: 'Mario', timestamp: '2026-01-02T03:04:05Z', is_from_me: false, body: 'Ciao!'
  });
  assert.strictEqual(f.text, 'Ciao!');
  assert.strictEqual(f.chatId, '393401234567@s.whatsapp.net');
  assert.strictEqual(f.senderName, 'Mario');
  assert.strictEqual(f.type, 0);
  assert.strictEqual(f.mediaPath, null);
  assert.strictEqual(f.timestamp.toISOString(), '2026-01-02T03:04:05.000Z');
});

test('mapWebhookMessage mappa un\'immagine con path stringa', () => {
  const f = mapWebhookMessage({ id: '1', chat_id: 'a@s.whatsapp.net', image: 'statics/media/x.jpeg' });
  assert.strictEqual(f.type, 1);
  assert.strictEqual(f.mediaPath, 'statics/media/x.jpeg');
});

test('mapWebhookMessage mappa un\'immagine con didascalia (oggetto)', () => {
  const f = mapWebhookMessage({
    id: '1', chat_id: 'a@s.whatsapp.net', body: 'guarda',
    image: { path: 'statics/media/y.png', caption: 'guarda' }
  });
  assert.strictEqual(f.type, 1);
  assert.strictEqual(f.mediaPath, 'statics/media/y.png');
  assert.strictEqual(f.text, 'guarda');
});

test('mapWebhookMessage degrada a testo quando il media non è scaricato', () => {
  const f = mapWebhookMessage({ id: '1', chat_id: 'a@s.whatsapp.net', image: { url: 'https://mmg/x' } });
  assert.strictEqual(f.type, 0);
  assert.strictEqual(f.mediaPath, null);
  assert.strictEqual(f.text, '[Immagine non scaricata]');
});
