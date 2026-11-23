'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const data = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/DataService.cs'), 'utf8');

test('deleting a chat takes the messages down by the one spelling they are filed under', () => {
  // Arriving messages are filed under the canonical spelling (`message.ChatId` is folded
  // before it is stored) while the delete was handed the id the row is filed under. For a
  // chat under a `@lid` or with a device still on it the two differ: the deletion found no
  // messages, hid nothing, left the tail file alive - and the chat came back at the next
  // refresh and again at the next start.
  const del = data.slice(data.indexOf('public void DeleteChat'),
    data.indexOf('public void ForgetAccount'));
  assert.match(del, /string canonical = ChatIdFor\(chatId\)/,
    'the deletion folds the id first');
  assert.match(del, /_chatMessages\.TryGetValue\(canonical/,
    'and takes the messages down under the spelling they are held under');
  assert.match(del, /ChatPreferences\.Hide\(canonical\)/,
    'and hides the row under the spelling the list reads');
});

test('the tail snapshot finds the messages of a row filed under a lid', () => {
  // The snapshot that survives a killed app walked the rows and looked each one up under
  // its own id - so the chats whose row id is not their canonical id were skipped and
  // their messages were lost in the kill the snapshot exists to survive.
  const tails = data.slice(data.indexOf('private List<KeyValuePair<string, ChatMessage[]>> TailsForTheConversations'),
    data.indexOf('private List<KeyValuePair<string, ChatMessage[]>> TailsForTheConversations') + 900);
  assert.match(tails, /ChatIdFor\(contact\.Id\)/,
    'the row id is folded before the messages are looked up');
});

test('the history asked-for keeps the id the page asks with', () => {
  // The other half of the same confusion: `_historyRequested` is written with the raw id
  // (`MarkHistoryRequested(contact.Id)`), so the delete has to take it down with that one
  // or the map grows an entry for every chat ever deleted.
  const del = data.slice(data.indexOf('public void DeleteChat'),
    data.indexOf('public void ForgetAccount'));
  assert.match(del, /_historyRequested\.Remove\(chatId\)/,
    'the raw id is the one that was asked with, and the one removed');
});

test('the conversation snapshot is saved under the canonical id too', () => {
  // The chat page saved its tail under the row's own id while the rewrite named the same
  // tail canonically: one chat, two files, and two readers that disagreed about which one
  // held the messages.
  const chatPage = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ChatPage.xaml.cs'), 'utf8');
  assert.match(chatPage,
    /MessageCache\.SaveAsync\(DataService\.Instance\.ChatIdFor\(_contact\.Id\), _messages\)/,
    'the save folds the id before it names the file');
});
