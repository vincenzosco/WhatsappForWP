'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const native = fs.readFileSync(path.join(ROOT, 'WhatsappApp', 'Services', 'NativeConnection.cs'), 'utf8');

test('a first send waits for the device list as long as a question may take', () => {
  // The comment says the question "is worth its whole deadline" - and the code
  // gave it the 1-3 seconds left of the send budget, so the question that timed
  // out left no stanza to write and no list to fall back on: every message to a
  // chat this phone had not asked about yet failed, which is "the messages do
  // not send". A chat with a remembered list still goes at once.
  const block = /int secondsForList = [^;]+;[\s\S]{0,2400}?RememberDevices\(address, devicesAnswer\)/.exec(native);
  assert.ok(block, 'the device-list question is still there');
  assert.match(block[0], /LastDevices\(address\)[\s\S]*?AnswerSeconds/,
    'and a chat with no remembered list gets the question\'s own deadline');
});

test('the follow of the bottom is not scared of the row it is following', () => {
  // The extent grows after the bar is sent to its end; that growth looks like a
  // value change far from the end and the follow disarmed itself on it - so the
  // re-apply after the layout never ran and a message just sent stayed below
  // the fold. While the layout is still settling, a value change is the page,
  // not the reader walking away.
  const view = fs.readFileSync(path.join(ROOT, 'WhatsappApp', 'Services', 'ConversationView.cs'), 'utf8');
  const handler = /private void OnScrollBarValueChanged[\s\S]*?\n        \}/.exec(view);
  assert.ok(handler, 'the handler exists');
  assert.match(handler[0], /_followBottom && !_waitingForLayout/,
    'the disarm waits for the layout to settle first');
});

test('a sent message scrolls to the bottom the way an arriving one does', () => {
  const page = fs.readFileSync(path.join(ROOT, 'WhatsappApp', 'Pages', 'ChatPage.xaml.cs'), 'utf8');
  const send = /private async void AddAndSendMessage[\s\S]*?ScrollToMessage\(message\);/.exec(page);
  assert.ok(send, 'the send still scrolls to the message it added');
  assert.match(send[0], /AddMessage\(_contact\.Id, message\);[\s\S]*?ScrollToMessage\(message\);/,
    'and it scrolls after the row is in the collection');
});