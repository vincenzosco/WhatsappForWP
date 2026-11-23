'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const client = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Protocol/Media/DtlsClient.cs'), 'utf8');
const server = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Protocol/Media/DtlsServer.cs'), 'utf8');

test('a handshake that did not agree on the srtp profile is not trusted', () => {
  // RFC 5764, section 5: a handshake that did not select the SRTP protection profile is
  // one to abort. The flag was computed from the peer's answer and then read by nobody,
  // so a peer that declined the profile got a call that was ready, trusted, and could
  // only ever be silence.
  assert.match(client, /public bool IsTrusted[\s\S]{0,600}&& _srtpAgreed/,
    'the client half counts the agreement as part of being trusted');
  assert.match(server, /public bool IsTrusted[\s\S]{0,600}_server\.IsComplete && _server\.SrtpAgreed/,
    'and so does the server half');
});
