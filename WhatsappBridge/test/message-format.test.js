'use strict';
const test = require('node:test');
const assert = require('node:assert');
const {
  formatDateForWp8,
  displayNameForJid,
  buildChatMessage,
  mapWebhookMessage,
  mapHistoryMessage
} = require('../message-format');

test('formatDateForWp8 produce il valore /Date(ms)/, senza backslash', () => {
  assert.strictEqual(formatDateForWp8(new Date(0)), '/Date(0)/');
  assert.strictEqual(formatDateForWp8(new Date(1700000000000)), '/Date(1700000000000)/');
});

test('il Timestamp sopravvive a JSON.stringify e torna come lo legge il telefono', () => {
  const wire = JSON.parse(JSON.stringify({ Timestamp: formatDateForWp8(new Date(1700000000000)) }));
  assert.strictEqual(wire.Timestamp, '/Date(1700000000000)/');
  assert.ok(!wire.Timestamp.includes('\\'), 'nessun backslash nel valore: ' + wire.Timestamp);
});

test('formatDateForWp8 non produce mai una data impossibile', () => {
  assert.ok(/^\/Date\(\d+\)\/$/.test(formatDateForWp8('non una data')));
  assert.ok(/^\/Date\(\d+\)\/$/.test(formatDateForWp8(new Date('x'))));
  assert.ok(/^\/Date\(\d+\)\/$/.test(formatDateForWp8(NaN)));
  assert.ok(/^\/Date\(\d+\)\/$/.test(formatDateForWp8(undefined)));
  assert.ok(/^\/Date\(\d+\)\/$/.test(formatDateForWp8('')));
});

test('i timestamp di GOWA in secondi diventano millisecondi', () => {
  assert.strictEqual(formatDateForWp8(1700000000), '/Date(1700000000000)/');
  assert.strictEqual(formatDateForWp8('1700000000'), '/Date(1700000000000)/');
  assert.strictEqual(formatDateForWp8(1700000000000), '/Date(1700000000000)/');
  assert.strictEqual(formatDateForWp8('2023-11-14T22:13:20.000Z'), '/Date(1700000000000)/');
  assert.strictEqual(formatDateForWp8('/Date(1700000000000)/'), '/Date(1700000000000)/');
});

test('buildChatMessage manda un Timestamp leggibile dal telefono', () => {
  const m = buildChatMessage({ command: 'state', state: 'disconnected' });
  assert.ok(/^\/Date\(\d+\)\/$/.test(m.Timestamp), 'Timestamp: ' + m.Timestamp);
});

test('mapWebhookMessage non propaga un timestamp impossibile', () => {
  const fields = mapWebhookMessage({
    id: 'x', from: '39@s.whatsapp.net', chat_id: '39@s.whatsapp.net',
    body: 'ciao', timestamp: 'non una data'
  });
  assert.ok(/^\/Date\(\d+\)\/$/.test(formatDateForWp8(fields.timestamp)),
    'Timestamp: ' + fields.timestamp);
});

test('displayNameForJid gestisce numeri e gruppi', () => {
  assert.strictEqual(displayNameForJid('393401234567@s.whatsapp.net'), '+393401234567');
  assert.strictEqual(displayNameForJid('123456789012345678@g.us'), 'Group 123456789012345678');
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

test('buildChatMessage carries the call fields', () => {
  const msg = buildChatMessage({
    command: 'call', chatId: 'a@s.whatsapp.net', senderName: 'Anna',
    timestamp: 0, callId: 'C1', callReason: 'timeout',
    callDurationSeconds: 12, callIsVideo: false
  });
  assert.strictEqual(msg.Command, 'call');
  assert.strictEqual(msg.CallId, 'C1');
  assert.strictEqual(msg.CallReason, 'timeout');
  assert.strictEqual(msg.CallDurationSeconds, 12);
  assert.strictEqual(msg.CallIsVideo, false);
  assert.strictEqual(msg.Type, 3);
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

test('un audio scaricato resta una parola', () => {
  const f = mapWebhookMessage({ id: 'A1', chat_id: 'a@s.whatsapp.net', audio: 'statics/media/a.ogg' });
  assert.strictEqual(f.mediaType, 'audio');
  assert.strictEqual(f.text, '[Audio]');
});

test('un documento scaricato mostra il suo nome', () => {
  const f = mapWebhookMessage({
    id: 'D1', chat_id: 'a@s.whatsapp.net',
    document: { path: 'statics/media/d.pdf', filename: 'contratto.pdf' }
  });
  assert.strictEqual(f.mediaType, 'document');
  assert.strictEqual(f.text, 'contratto.pdf');
});

test('un documento senza nome resta una parola', () => {
  const f = mapWebhookMessage({
    id: 'D2', chat_id: 'a@s.whatsapp.net', document: { path: 'statics/media/d.bin' }
  });
  assert.strictEqual(f.text, '[Document]');
});

test('un documento non scaricato si dichiara documento lo stesso', () => {
  const f = mapWebhookMessage({ id: 'D3', chat_id: 'a@s.whatsapp.net', document: {} });
  assert.strictEqual(f.mediaType, 'document');
  assert.strictEqual(f.text, '[Document not downloaded]');
});

test('un messaggio di un canale non viene inoltrato', () => {
  assert.strictEqual(
    mapWebhookMessage({ chat_id: '123456@newsletter', from: '123456@newsletter', body: 'x' }),
    null);
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
  assert.strictEqual(f.text, '[Image not downloaded]');
});

test('buildChatMessage carries the chat-row fields', () => {
  const msg = buildChatMessage({
    command: 'chat', chatId: 'a@s.whatsapp.net', senderName: 'Anna',
    text: 'ciao', timestamp: '2026-09-26T09:00:00Z', isGroup: false, avatarData: 'AAAA'
  });
  assert.strictEqual(msg.Command, 'chat');
  assert.strictEqual(msg.SenderName, 'Anna');
  assert.strictEqual(msg.Text, 'ciao');
  assert.strictEqual(msg.IsGroup, false);
  assert.strictEqual(msg.AvatarData, 'AAAA');
  assert.strictEqual(msg.Type, 3);
});

test('buildChatMessage omits an absent avatar and marks a group', () => {
  const msg = buildChatMessage({ command: 'chat', chatId: '1@g.us', isGroup: true });
  assert.strictEqual(msg.IsGroup, true);
  assert.strictEqual(msg.AvatarData, undefined);
});

test('mapHistoryMessage reads a text message and marks it as history', () => {
  const mapped = mapHistoryMessage({
    id: 'A1',
    chat_jid: '393401234567@s.whatsapp.net',
    sender_jid: '393401234567@s.whatsapp.net',
    sender_display_name: 'Anna',
    content: '  ciao  ',
    timestamp: '2026-09-26T09:00:00Z',
    is_from_me: false,
    media_type: ''
  });

  assert.strictEqual(mapped.id, 'A1');
  assert.strictEqual(mapped.text, 'ciao');
  assert.strictEqual(mapped.chatId, '393401234567@s.whatsapp.net');
  assert.strictEqual(mapped.senderName, 'Anna');
  assert.strictEqual(mapped.isIncoming, true);
  assert.strictEqual(mapped.type, 0);
  assert.strictEqual(mapped.isHistory, true);
  // Il valore atteso e' costruito da Date.parse, non da formatDateForWp8:
  // altrimenti il test direbbe solo che la funzione e' se stessa.
  assert.strictEqual(mapped.timestamp, `/Date(${Date.parse('2026-09-26T09:00:00Z')})/`);
});

test('mapHistoryMessage marks my own messages and leaves the sender alone', () => {
  const mapped = mapHistoryMessage({ id: 'A2', chat_jid: 'x@s.whatsapp.net', content: 'io', is_from_me: true });

  assert.strictEqual(mapped.isIncoming, false);
  assert.strictEqual(mapped.senderId, 'me');
});

test('mapHistoryMessage names the media it cannot download', () => {
  // Una foto vecchia non e' fra i byte che il webhook ha consegnato: resta una
  // parola, perche' un fumetto vuoto sarebbe peggio.
  assert.strictEqual(mapHistoryMessage({ id: 'A3', media_type: 'image' }).text, '[Image]');
  assert.strictEqual(mapHistoryMessage({ id: 'A4', media_type: 'video' }).text, '[Video]');
  assert.strictEqual(mapHistoryMessage({ id: 'A5', media_type: 'audio' }).text, '[Audio]');
  assert.strictEqual(mapHistoryMessage({ id: 'A6', media_type: 'document' }).text, '[Document]');
  assert.strictEqual(mapHistoryMessage({ id: 'A7', media_type: 'sticker' }).text, '[Sticker]');

  // Con una didascalia vince la didascalia.
  assert.strictEqual(mapHistoryMessage({ id: 'A8', media_type: 'image', content: 'guarda' }).text, 'guarda');

  // Un tipo che non conosciamo e nessun testo: nessuna parola inventata.
  assert.strictEqual(mapHistoryMessage({ id: 'A9', media_type: 'poll' }).text, '');
  assert.strictEqual(mapHistoryMessage({}).text, '');
});

test('mapHistoryMessage survives a timestamp it cannot read', () => {
  const mapped = mapHistoryMessage({ id: 'A10', timestamp: 'non una data' });

  assert.strictEqual(typeof mapped.timestamp, 'string');
  assert.ok(/^\/Date\(\d+\)\/$/.test(mapped.timestamp), 'una data impossibile non deve arrivare al telefono');
});

test('buildChatMessage carries IsHistory only when it is set', () => {
  const history = buildChatMessage({ command: 'history', isHistory: true, type: 0, chatId: 'x@s.whatsapp.net' });
  assert.strictEqual(history.IsHistory, true);
  assert.strictEqual(history.Type, 0);

  const live = buildChatMessage({ text: 'nuovo' });
  assert.strictEqual(Object.prototype.hasOwnProperty.call(live, 'IsHistory'), false);
});

test('una riga di cronologia dice che tipo di media e', () => {
  assert.strictEqual(mapHistoryMessage({ id: 'A1', media_type: 'image' }).mediaType, 'image');
  assert.strictEqual(mapHistoryMessage({ id: 'A2', media_type: 'video' }).mediaType, 'video');
  assert.strictEqual(mapHistoryMessage({ id: 'A3', content: 'ciao' }).mediaType, '');
});

test('un video in arrivo dichiara il suo tipo e resta una parola', () => {
  const fields = mapWebhookMessage({
    id: 'V1',
    chat_id: 'a@s.whatsapp.net',
    from: 'a@s.whatsapp.net',
    video: { path: 'statics/v.mp4' },
    timestamp: '2026-09-27T08:00:00Z'
  });

  assert.strictEqual(fields.type, 4);
  assert.strictEqual(fields.mediaType, 'video');
  assert.strictEqual(fields.text, '[Video]');

  const frame = buildChatMessage({
    id: 'V1', chatId: 'a@s.whatsapp.net', type: fields.type, mediaType: fields.mediaType, text: fields.text
  });
  assert.strictEqual(frame.Type, 4);
  assert.strictEqual(frame.MediaType, 'video');
});

test('buildChatMessage porta UnreadCount solo quando la riga lo dichiara', () => {
  const row = buildChatMessage({ command: 'chat', chatId: 'a@s.whatsapp.net', unreadCount: 3 });
  assert.strictEqual(row.UnreadCount, 3);

  const empty = buildChatMessage({ command: 'chat', chatId: 'a@s.whatsapp.net', unreadCount: 0 });
  assert.strictEqual(Object.prototype.hasOwnProperty.call(empty, 'UnreadCount'), true);
  assert.strictEqual(empty.UnreadCount, 0);

  // Un messaggio normale non porta il conteggio.
  const live = buildChatMessage({ text: 'ciao' });
  assert.strictEqual(Object.prototype.hasOwnProperty.call(live, 'UnreadCount'), false);
});
