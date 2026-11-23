'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const signaling = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Protocol', 'Client', 'CallSignaling.cs'), 'utf8');
const direct = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Protocol', 'Client', 'DirectSend.cs'), 'utf8');
const native = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Services', 'NativeConnection.cs'), 'utf8');
const sessions = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Protocol', 'Signal', 'SessionStore.cs'), 'utf8');

function slice(text, startMarker, size) {
  const at = text.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return text.slice(at, at + size);
}

test('the offer goes to the person being called, not to this account\'s devices', () => {
  // The run of 15:29 fanned one call out to five devices and four of them were this
  // account's own (`150375613116620@lid` and its companions - the same addresses the
  // "peer request" lines name as ours). A call is not a message: there is no sent copy
  // to keep, and every offer that goes to ourselves is an offer the callee's phone is
  // not being asked to answer.
  const callees = slice(direct, 'CalleeOnly', 1900);
  assert.match(callees, /ownJids/, 'the account\'s own spellings are what it drops');
  assert.match(callees, /IndexOf\(':'\)/, 'a device suffix does not hide one of our devices');

  const offer = slice(native, 'public async Task<string> OfferCallAsync(string jid, bool video)', 9000);
  assert.match(offer, /CalleeOnly/, 'the call fanout drops this account\'s devices');
  assert.match(offer, /OurJids\(\)/, 'in both spellings, the number and the lid');
});

test('the call stanza carries the two attributes a sender writes', () => {
  // Both decompiled senders wrap the verb in exactly two attributes: the official
  // WP8.1 one (`WriteVoipNode`: `list.Add(to)`, `list.Add(id)`) and the Android one
  // (`sendCallStanza` hands the sender its jid and its stanza id). The others on a
  // captured node - `from`, `version`, `platform`, `offline`, `t` - are what the server
  // stamps on delivery; a client that writes `from` itself is not what either client
  // does, and the run that wrote all five got no ack at all.
  const wrap = slice(signaling, 'private static BinaryNode Wrap(string callId, string to, BinaryNode verb)', 900);
  assert.match(wrap, /Attrs\.Add\("to", to\)/, 'the stanza says where it goes');
  assert.match(wrap, /Attrs\.Add\("id"/, 'the stanza carries an id');
  assert.doesNotMatch(wrap, /"from"/, 'the sender does not write the server\'s stamps');
  assert.doesNotMatch(wrap, /"version"/, 'nor what it is');
  assert.doesNotMatch(wrap, /"platform"/, 'nor what it runs on');
  assert.doesNotMatch(wrap, /"offline"/, 'nor whether the copy was offline');
});

test('the stanza id is a message id, not the call id', () => {
  // The official sender gives every verb a generated id (`FunXMPP.GenerateMessageId`)
  // and keeps the call id inside the verb. Two stanzas of one call sharing the call id
  // are one stanza to a server that de-duplicates by id, and a receipt answered to the
  // call id is a receipt nothing reads.
  const wrap = slice(signaling, 'private static BinaryNode Wrap(string callId, string to, BinaryNode verb)', 900);
  assert.match(wrap, /StanzaId/, 'the id comes from the seam');
  assert.match(signaling, /internal static Func<string> NewStanzaId/,
    'and the app is what names it');
  assert.match(native, /CallSignaling\.NewStanzaId = /,
    'so the id is generated the way this app generates the ones it already sends');
});

test('a session is found whatever spelling the two are talking in', () => {
  // The store is keyed on the device jid as the send spelled it. A chat that was first
  // written by number keeps its session under the number, and a call addressed by the
  // lid finds nothing there and seals its key as a pre-key message - the one shape a
  // callee has to open a session to read, on the one stanza whose silence is a phone
  // that does not ring.
  const load = slice(sessions, 'public SessionRecord Load(string peer)', 1200);
  assert.match(load, /paired|Paired/, 'a miss is asked again under the other spelling');
  assert.match(sessions, /Func<string, string>/, 'and the pairing belongs to the app');
});