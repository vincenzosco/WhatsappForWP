// A swipe on a bubble answers that message, and the message menu answers one with an
// emoji. Both go out as real protocol - a quote inside the words' context info and a
// reaction of its own - and both are drawn on the bubble that already exists. This pins
// the gesture, the reply bar, the quote and reaction blocks, the two sends, and the
// reference's own field numbers for the two new shapes.
const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..', '..');
const read = (p) => fs.readFileSync(path.join(root, p), 'utf8');

test('the bubble carries the swipe and the row draws the quote and the reaction', () => {
  const xaml = read(path.join('WhatsappApp', 'Pages', 'ChatPage.xaml'));
  assert.match(xaml, /ManipulationDelta="Bubble_ManipulationDelta"/, 'the row follows the finger');
  assert.match(xaml, /ManipulationCompleted="Bubble_ManipulationCompleted"/, 'a lift answers it');
  assert.match(xaml, /x:Name="ReplyBar"/, 'the reply bar exists');
  assert.match(xaml, /Click="ReplyClose_Tapped"/, 'and it can be closed');
  assert.equal((xaml.match(/Binding ShowsQuote/g) || []).length, 2,
    'both bubbles draw the quote');
  assert.equal((xaml.match(/Binding ShowsReaction/g) || []).length, 2,
    'both bubbles draw the reaction');
});

test('the page opens a reply and sends the quote inside the words', () => {
  const page = read(path.join('WhatsappApp', 'Pages', 'ChatPage.xaml.cs'));
  assert.match(page, /private void StartReply\(ChatMessage message\)/);
  assert.match(page, /private void Bubble_ManipulationDelta\(/);
  assert.match(page, /ReplySwipeThreshold/, 'the gesture has a threshold');
  assert.match(page, /SendReplyAsync\(message\.ChatId, message\.Text,\s*message\.QuoteId/,
    'the quote travels in the words');
  assert.match(page, /SendReactionAsync\(message\.ChatId, message\.Id/,
    'the reaction names the message it answers');
});

test('the message menu answers with the six emoji and with a reply', () => {
  const page = read(path.join('WhatsappApp', 'Pages', 'ChatPage.xaml.cs'));
  const menu = page.slice(page.indexOf('ShowMessageMenu'), page.indexOf('RevokeHours'));
  assert.match(menu, /ChatPage_Reply/, 'the menu can reply');
  assert.equal((menu.match(/react\.Text = emoji/g) || []).length, 1, 'the emoji row is offered');
  assert.match(menu, /\\U0001F44D/, 'the six are the modern ones');
  assert.equal((menu.match(/\\U0001F44D|\\u2764|\\U0001F602|\\U0001F62E|\\U0001F622|\\U0001F64F/g) || []).length, 6);
});

test('the two sends exist and the reaction leaves no row behind', () => {
  const native = read(path.join('WhatsappApp', 'Services', 'NativeConnection.cs'));
  assert.match(native, /public async Task<string> SendReplyAsync\(/);
  assert.match(native, /public async Task<string> SendReactionAsync\(/);
  const react = native.slice(native.indexOf('public async Task<string> SendReactionAsync('),
    native.indexOf('private async Task<string> SendWordsAsync('));
  assert.match(react, /Message\.React\(/, 'the proto carries the reaction');
});

test('the wire shapes carry the reference field numbers', () => {
  const proto = read(path.join('WhatsappApp', 'Protocol', 'WaProto', 'Message.cs'));
  // The reaction is field 46 (the varint 370 the reference prints is f2 02), and the
  // quote's three parts are contextInfo fields 1, 2 and 3.
  assert.match(proto, /public const int ReactionField = 46;/);
  assert.match(proto, /public const int QuoteStanzaIdField = 1;/);
  assert.match(proto, /public const int QuoteParticipantField = 2;/);
  assert.match(proto, /public const int QuoteMessageField = 3;/);
  assert.match(proto, /public static Message TextReply\(/);
  assert.match(proto, /public static Message React\(/);
});

test('the reference bytes are what the protocol tests compare against', () => {
  const fixture = read(path.join('ProtocolTests', 'Vectors', 'MessageFixture.cs'));
  assert.match(fixture, /ReactionEncoded =/);
  assert.match(fixture, /ReplyEncoded =/);
  const tests = read(path.join('ProtocolTests', 'MessageTests.cs'));
  assert.match(tests, /AReactionIsTheReferencesOwnBytes/);
  assert.match(tests, /AReplyIsTheReferencesOwnBytes/);
});

test('the two words are localized in both languages', () => {
  for (const [file, reply] of [
    [path.join('WhatsappApp', 'Strings', 'en-US', 'Resources.resw'), 'Reply'],
    [path.join('WhatsappApp', 'Strings', 'it-IT', 'Resources.resw'), 'Rispondi'],
    [path.join('WhatsappApp.Core', 'Strings', 'Resources.resx'), 'Reply'],
    [path.join('WhatsappApp.Core', 'Strings', 'Resources.it-IT.resx'), 'Rispondi'],
  ]) {
    const text = fs.readFileSync(path.join(root, file), 'utf8');
    assert.ok(text.includes('ChatPage_Reply'), file + ' names the reply');
    assert.ok(text.includes('>' + reply + '<'), file + ' says it in its own language');
    assert.ok(text.includes('ChatPage_You'), file + ' names the account');
  }
});

test('an incoming reaction lands on the row it names and is written back', () => {
  const data = read(path.join('WhatsappApp', 'Services', 'DataService.cs'));
  assert.match(data, /public void ApplyReaction\(string chatId, string messageId, string emoji\)/);
  assert.match(data, /messages\[i\]\.Reaction = emoji;/, 'the row carries it');
  assert.match(data, /RewriteTailAsync\(chatId, messages\)/, 'and the conversation is written back');
  assert.match(data, /ApplyNativeQuote\(message, incoming\.Encoded\)/, 'the quote arrives too');
});