'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const source = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Protocol/Client/WhatsAppSocket.cs'), 'utf8');

const method = source.slice(source.indexOf('public async Task SendAsync(BinaryNode node)'));
const body = method.slice(0, method.indexOf('public void Close()'));

test('framing and the write are one turn of one lock', () => {
  // EncodeFrame mutates the Noise write counter and the one-time intro flag. Two
  // senders that frame at the same time can seal two frames under one counter (a
  // GCM nonce reuse the server answers with a drop) or interleave the intro with a
  // frame, and the transport's own semaphore only orders the raw socket write.
  // The gate has to span from before the frame is built to after it is written,
  // or frames can also leave in an order their counters disagree with.
  const acquire = body.search(/WaitAsync\(\)|Monitor\.Enter|lock \(/);
  assert.ok(acquire >= 0, 'SendAsync takes a gate before it frames anything');

  const encode = body.indexOf('EncodeFrame(');
  assert.ok(encode > acquire,
    'the frame is built inside the gate, so the counter cannot interleave');

  const write = body.indexOf('_transport.SendAsync(');
  assert.ok(write > encode,
    'the socket write is inside the same turn as the framing');

  const release = body.search(/Release\(\)|Monitor\.Exit/);
  assert.ok(release > write,
    'the gate is given back only after the frame is on the wire');
});

test('the gate is released even when a send throws', () => {
  // A gate that leaks on an exception is a socket that stops sending at the first
  // dropped connection - and a send is exactly where connections drop.
  const write = body.indexOf('_transport.SendAsync(');
  const tail = body.slice(write);
  assert.match(tail, /finally/,
    'the release sits in a finally');
});

test('the class holds one single-holder gate for sends', () => {
  // One sender at a time is the whole point: a gate with more holders than one
  // would let the same two sends through it at once.
  assert.match(source, /SemaphoreSlim\(1,\s*1\)|new object\(\)/,
    'the send gate is a single-holder primitive');
});
