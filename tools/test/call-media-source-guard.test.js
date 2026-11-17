'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const source = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Protocol/Transport/CallMediaSocket.cs'), 'utf8');

const method = source.slice(source.indexOf('private async void OnMessageReceived('));
const body = method.slice(0, method.indexOf('private void OnTick('));

test('the outbound destination moves only on the peer the signalling nominated', () => {
  // The UDP port is advertised in every offer, so any host that learns it can
  // send one datagram to it. The transport already refuses datagrams from an
  // address that is not the nominated pair (`NotFromPeer`) - and until this
  // guard, such a datagram still re-pointed the audio and handshake sends at its
  // own source before the verdict. Only the nominated peer (the pair the ICE
  // check named, which the transport reports as `Peer`) may move the destination;
  // a refused datagram changes nothing.
  const receive = body.indexOf('transport.Receive(');
  const redirect = body.indexOf('_remoteAddress = args.RemoteAddress');
  assert.ok(receive >= 0, 'the datagram goes through the transport');
  assert.ok(redirect >= 0, 'the outbound destination is written somewhere');
  assert.ok(redirect > receive,
    'the destination is decided only after the transport has judged the datagram');

  const verdict = body.slice(receive, redirect);
  assert.match(verdict, /SameEndpoint\(/,
    'the guard compares the datagram\'s endpoint with the nominated peer');
  assert.match(verdict, /Peer/,
    'the guard reads the transport\'s nominated peer');

  const guard = body.slice(Math.max(0, redirect - 300), redirect + 200);
  assert.match(guard, /if \(/,
    'the update is conditional, not unconditional');
  assert.match(guard, /if \([^\n]*SameEndpoint\(/,
    'the condition is the endpoint match against the nominated peer');
});

test('a datagram with no call attached moves nothing', () => {
  // The port is held from the moment the app opens. A stray datagram arriving
  // before a call exists has no source verdict at all, so it must leave the
  // destination where it is - not claim it.
  const unattached = body.indexOf('transport == null');
  const redirect = body.indexOf('_remoteAddress = args.RemoteAddress');
  assert.ok(unattached >= 0, 'the unattached case is handled');
  assert.ok(redirect > unattached,
    'the unattached early return happens before any destination update');
});

test('the peer comparison is address and port, like the transport\'s own', () => {
  // The transport refuses on the endpoint (address bytes and port). The socket
  // has to agree with that verdict exactly, or a datagram the transport counted
  // as foreign could still pass the socket's own, looser idea of the peer.
  const at = source.indexOf('SameEndpoint(');
  assert.ok(at >= 0, 'an endpoint comparison exists');
  const helper = source.slice(at);
  assert.match(helper, /\.Address/,
    'the comparison looks at the address bytes');
  assert.match(helper, /\.Port/,
    'and at the port');
});
