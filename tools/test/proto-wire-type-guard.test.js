'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (f) => fs.readFileSync(path.join(ROOT, f), 'utf8');
const proto = read('WhatsappApp/Protocol/WaProto/Proto.cs');
const message = read('WhatsappApp/Protocol/WaProto/Message.cs');
const sync = read('WhatsappApp/Protocol/WaProto/HistorySync.cs');
const media = read('WhatsappApp/Protocol/WaProto/MediaMessages.cs');
const appState = read('WhatsappApp/Protocol/WaProto/AppStateMessages.cs');

test('the reader hands out bytes only from a length-delimited field', () => {
  // A tag names its field and its layout, and the two are independent: a field
  // read with the layout of another one is not a value but a misparse - a varint
  // read as a length eats the bytes that follow it and the rest of the message is
  // then read out of step. The checked read steps over what it cannot take.
  assert.match(proto, /public byte\[\] ReadBytes\(int wireType\)/,
    'the reader takes bytes with a wire type to check');
  assert.match(proto, /ReadBytes\(int wireType\)[\s\S]{0,160}SkipField\(wireType\)[\s\S]{0,40}return null/,
    'a differently laid out field is stepped over and carries no value');
});

test('every field consumption in the message decoders is laid out correctly', () => {
  // HistorySync, MediaMessages and AppStateMessages parse server-given nested
  // blobs: a field laid out otherwise is stepped over and simply absent there.
  // Message.Decode has a duty of its own and its own test below.
  for (const [name, src] of [['HistorySync', sync],
    ['MediaMessages', media], ['AppStateMessages', appState]]) {
    assert.doesNotMatch(src, /reader\.ReadBytes\(\)/,
      name + ': every bytes read carries the wire type it must have');
    const varints = (src.match(/reader\.ReadVarint\(\)/g) || []).length;
    const guards = (src.match(/field\.WireType == 0/g) || []).length;
    assert.ok(guards >= varints,
      name + ': ' + varints + ' varint read(s) behind ' + guards + ' wire-type check(s)');
    const texts = (src.match(/Text\(reader\)/g) || []).length;
    assert.equal(texts, 0, name + ': the text helper takes the wire type too');
  }
});

test('Message.Decode refuses a field laid out otherwise', () => {
  // The read comes first, with the layout the field's number gives it: that is
  // where a padded copy trips (`a protobuf field announces 9 bytes with 7 left`
  // names the padding) and the pinned refusals of the echo and of the re-link
  // stay byte for byte. A read that lands on the wrong layout is refused just
  // the same - the misparse must not become a silently wrong field.
  const decode = message.slice(message.indexOf('public static Message Decode'));
  const reads = (decode.match(/ReadBytesLaidOutAsBytes\(reader, field\)/g) || []).length;
  assert.ok(reads >= 10, 'every field of the message is read through the checked read');
  assert.match(decode, /byte\[\] value = reader\.ReadBytes\(\);[\s\S]{0,240}field\.WireType != 2/,
    'the check sits after the read the padded copies are pinned to trip on');
});

test('RevokedId answers null on any malformed key and never an exception', () => {
  // The contract this method documents is "the id or null", and both walks of the
  // key bytes can meet a truncated field. Only the first was covered: the second
  // one threw out of a caller that had no reason to expect it.
  const revoked = appState.slice(appState.indexOf('public static string RevokedId'),
    appState.indexOf('public static string RevokedId') + 1600);
  const catches = (revoked.match(/catch \(ProtocolException\)/g) || []).length;
  assert.equal(catches, 2, 'both walks over the key bytes are covered');
});
