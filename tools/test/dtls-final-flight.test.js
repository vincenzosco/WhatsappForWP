'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const server = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Protocol/Media/DtlsServer.cs'), 'utf8');
const transport = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Protocol/Media/CallTransport.cs'), 'utf8');

test('the server keeps its final flight until the client proves it got one', () => {
  // RFC 6347 4.2.4.1: the Finished flight is the one datagram whose loss cannot be
  // recovered by the client alone - both sides already agree on the keys, and the
  // client repeats its Finished until something of ours lands. Emitting the flight
  // once and marking the handshake done in the same call left every answered call
  // one lost datagram away from dying.
  assert.match(server, /private byte\[\] _finalFlight;/, 'the flight is retained');
  const done = server.slice(server.indexOf('private byte[] RepeatFinalFlight'));
  assert.match(done, /HandshakeFinished\) return _finalFlight;/,
    'and a repeated client Finished brings the same bytes back');
});

test('a done handshake still answers the repeat', () => {
  const receive = server.slice(server.indexOf('public byte[] Receive'),
    server.indexOf('public byte[] Receive') + 700);
  assert.doesNotMatch(receive, /_stage == StageFailed \|\| _stage == StageDone\) return null;/,
    'the done gate no longer swallows the repeat');
  assert.match(receive, /RepeatFinalFlight\(datagram\)/,
    'and the datagram is offered to the retransmission instead');
});

test('the transport lets the repeat through on a live path', () => {
  // The Ready-state drop exists to keep renegotiation off a live call; the repeat
  // of the Finished is the one handshake datagram a done path must still answer,
  // so the handshake sees the datagram and only what it declines is dropped.
  assert.match(transport, /byte\[\] flight = _handshake\.Receive\(datagram\);/,
    'the handshake sees the datagram on a live path');
  assert.match(transport, /if \(flight != null && flight\.Length > 0\)[\s\S]{0,160}else[\s\S]{0,60}Dropped\+\+;/,
    'and only what brings no answer is dropped');
});

test('the flight that is repeated is the one that was sent', () => {
  const verify = server.slice(server.indexOf('private byte[] VerifyClientFinished'),
    server.indexOf('private byte[] VerifyClientFinished') + 3000);
  assert.match(verify, /_finalFlight = outbound\.ToArray\(\);/,
    'the retained bytes are the emitted flight - a rebuilt one would hash over a moved transcript');
});
