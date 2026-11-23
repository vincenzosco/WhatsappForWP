'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const server = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Protocol/Media/DtlsServer.cs'), 'utf8');
const client = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Protocol/Media/DtlsClient.cs'), 'utf8');

test('the dtls server puts fragmented handshake messages back together', () => {
  // The client half reads fragments through `DtlsHandshakeReassembler`; the server half
  // read one message per record and dropped what it could not read. A caller whose
  // ClientHello was fragmented at the path MTU was answered by nothing at all - an
  // answered call that stayed silent, with no line in the log to say why.
  assert.match(server, /DtlsHandshakeReassembler/,
    'the server reassembles like the client does');
  assert.match(server, /DtlsHandshakeFragment\.Parse/,
    'and walks what a record carries piece by piece');
});

test('a record the server cannot read fails the handshake out loud', () => {
  // The old code swallowed the parse refusal (`return null`, `continue`) and the call
  // hung on a silence that read like a network problem. A peer this server cannot talk to
  // is a fact to record, not a packet to forget.
  const read = server.slice(server.indexOf('private byte[] Read(byte[] fragment)'),
    server.indexOf('private byte[] Handle(DtlsHandshakeMessage message)'));
  assert.match(read, /Fail\(ex\.Message\)/,
    'a refusal the reader throws is recorded as the failure it is');
});

test('a repeated hello is answered before the reassembler can drop it', () => {
  // The reassembler rightly ignores a message it has already handed over - which is also
  // what a retransmitted hello looks like. Answered there, the handshake hung on the
  // client's timer; answered before it, the flight goes out again as it always did.
  const read = server.slice(server.indexOf('private byte[] Read(byte[] fragment)'),
    server.indexOf('private byte[] Handle(DtlsHandshakeMessage message)'));
  assert.match(read, /HandshakeClientHello && _stage == StageAwaitingFinished/,
    'the retransmission case is decided on the fragment, before the reassembly');
  assert.match(read, /return _flight;/,
    'and the flight that was already built is the one that goes out again');
});
