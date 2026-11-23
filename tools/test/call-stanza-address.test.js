// Every stanza of one call names the same address.
//
// The log of a call to MAMMA (1.3.12.76): the offer went to `179585517920463@lid`
// and the terminate to `393492556507@s.whatsapp.net`, and the server answered
// `error="439"`: a call stanzas addressed twice is a call half of which the
// server will not route. The offer is where the address is decided, so the
// offer is where it is remembered and every later stanza of the same call
// reuses it.
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..', '..');
const read = (p) => fs.readFileSync(path.join(root, p), 'utf8');

const native = read('WhatsappApp/Services/NativeConnection.cs');

function slice(text, marker, span) {
  const at = text.indexOf(marker);
  assert.ok(at >= 0, 'the code still has ' + marker);
  return text.slice(at, at + span);
}

test('the offer remembers where it went', () => {
  const offer = slice(native, 'public async Task<string> OfferCallAsync(', 24000);
  assert.match(offer, /KeepCallJid\(callId, target\.To\)/,
    'the address is kept under the call id when the offer is built');
});

test('a later stanza of the same call reuses that address', () => {
  const first = slice(native, 'public async Task TerminateCallAsync(string callId, string jid, string reason)',
    1200);
  const second = slice(native,
    'string reason, int durationSeconds, int videoState)',
    1400);

  for (const method of [first, second]) {
    assert.match(method, /CallJidFor\(callId\)/,
      'the terminate names the address the offer used');
    assert.match(method, /Calls\.Terminate\(callId, to,/,
      'and sends the terminate to it, not to whatever jid the caller had on the screen');
    assert.match(method, /ForgetCallJid\(callId\)/,
      'and the address is dropped with the call');
  }
});

test('an incoming call answers the address it came from', () => {
  const keeper = slice(native, 'private void KeepCallJid(', 1400);
  assert.match(keeper, /_callJids/, 'the addresses are kept in one place');
  assert.match(slice(native, 'private string CallJidFor(', 900), /TryGetValue/,
    'a call with no address of its own falls back to the one it was handed');
});