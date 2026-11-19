'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

test('a pairing asks for the whole history, not only the recent days', () => {
  // Baileys (the reference companion, `Utils/validate-connection.ts` +
  // `Defaults`) sends `requireFullSync: true`; this client sent 0 and then
  // wondered why the server wrote "sync type 5 and type 0 and no history".
  const props = read('WhatsappApp/Protocol/WaProto/DeviceProps.cs');
  assert.match(props, /Varint\(RequireFullSyncField, 1\)/,
    'the companion that wants the full history says so at the pairing');
});

test('the whole history can be asked for again, after the pairing', () => {
  // The link is not the only way: `FULL_HISTORY_SYNC_ON_DEMAND` is a
  // peer-data request this account sends to its own devices
  // (`Message.PeerDataOperationRequestMessage`, WAProto: type 1, the request
  // field 6), and the primary answers with the history again.
  const proto = read('WhatsappApp/Protocol/WaProto/AppStateMessages.cs');
  assert.match(proto, /PeerDataOperationRequest\s*=\s*16/,
    'the protocol message type is the reference field (ProtocolMessage.peerDataOperationRequestMessage)');
  const full = proto.slice(proto.indexOf('FullHistorySyncOnDemandRequest('),
    proto.indexOf('FullHistorySyncOnDemandRequest(') + 1200);
  assert.match(full, /Varint\(1, FullHistorySyncOnDemandType\)/,
    'the request names its operation type');
  assert.match(full, /Bytes\(6, /, 'and travels in field 6 of the request message');
  assert.match(full, /Bytes\(2, DeviceProps\.HistorySyncConfig\(\)\)/,
    'with the same history config the pairing sent');
});

test('one chat can be asked for on its own', () => {
  // `HISTORY_SYNC_ON_DEMAND` (type 3): chatJid 1, oldestMsgId 2, fromMe 3,
  // count 4, oldestMsgTimestampMs 5 - the answer arrives as the ON_DEMAND
  // history chunk this client already reads.
  const proto = read('WhatsappApp/Protocol/WaProto/AppStateMessages.cs');
  const one = proto.slice(proto.indexOf('HistorySyncOnDemandRequest('),
    proto.indexOf('HistorySyncOnDemandRequest(') + 1400);
  assert.match(one, /Varint\(1, HistorySyncOnDemandType\)/);
  assert.match(one, /Text\(1, chatJid\)/);
  assert.match(one, /Bytes\(4, /);
});

test('the request travels as a peer message to this accounts own devices', () => {
  // Baileys' `sendPeerDataOperationMessage` (`Socket/messages-send.ts`): a
  // message to this account's own JID with `category=peer`,
  // `push_priority=high_force` and a `meta appdata="default"` node - the
  // marks that say "not a chat message, a request for this account".
  const out = read('WhatsappApp/Protocol/Client/OutgoingMessage.cs');
  const peer = out.slice(out.indexOf('PeerNode('), out.indexOf('PeerNode(') + 1400);
  assert.match(peer, /"category", "peer"/);
  assert.match(peer, /"push_priority", "high_force"/);
  assert.match(peer, /"meta"/);
  assert.match(peer, /"appdata", "default"/);
});

test('a login that gets no history asks for it', () => {
  // The line that used to end at "this device has to be linked again" now
  // asks instead: that is the whole point of the on-demand request.
  const conn = read('WhatsappApp/Services/NativeConnection.cs');
  const backlog = conn.slice(conn.indexOf('the backlog ended with no chat and no history'),
    conn.indexOf('the backlog ended with no chat and no history') + 1200);
  assert.match(backlog, /RequestFullHistoryAsync/,
    'the empty backlog is the moment to ask');
  assert.match(conn, /SendPeerDataOperationAsync/,
    'and the peer request has a sender of its own');
});

test('a chat whose history never comes asks for it', () => {
  // WaitForHistoryAsync used to end silently at its timeout; the page now
  // asks for that chat's history instead of staying empty.
  const page = read('WhatsappApp/Pages/ChatPage.xaml.cs');
  const wait = page.slice(page.indexOf('WaitForHistoryAsync(string chatId)'),
    page.indexOf('WaitForHistoryAsync(string chatId)') + 1500);
  assert.match(wait, /RequestChatHistoryAsync/,
    'the timeout is the moment to ask');
});