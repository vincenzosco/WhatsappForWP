'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const page = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ChatPage.xaml.cs'), 'utf8');
const data = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/DataService.cs'), 'utf8');

test('the page recognises its chat by its canonical id', () => {
  // A message is keyed as `ChatIdFor` spells it and a row can be opened under the
  // other spelling (`@lid`, a device suffix): the raw comparison never matched and
  // the scroll, the read mark and the history end were dropped in silence while
  // the chat was on screen.
  assert.match(page, /private bool IsThisChat\(string chatId\)/,
    'the page has one canonical comparison');
  assert.match(page, /ChatIdFor\(_contact\.Id\)[\s\S]{0,120}ChatIdFor\(chatId\)|ChatIdFor\(chatId\)[\s\S]{0,120}ChatIdFor\(_contact\.Id\)/,
    'and folds both sides');
  assert.doesNotMatch(page, /message\.ChatId != _contact\.Id/,
    'no handler compares the raw row id');
  assert.doesNotMatch(page, /chatId != _contact\.Id/,
    'and the history end does not either');

  const history = page.slice(page.indexOf('private void OnHistoryCompleted'),
    page.indexOf('private void OnHistoryCompleted') + 600);
  assert.match(history, /ChatIdFor\(_contact\.Id\)|IsThisChat\(chatId\)/,
    'the active chat check is canonical too');
});

test('the wait for a history burst matches the canonical id', () => {
  const wait = page.slice(page.indexOf('private async System.Threading.Tasks.Task WaitForHistoryAsync'),
    page.indexOf('private async System.Threading.Tasks.Task WaitForHistoryAsync') + 1200);
  assert.match(wait, /ChatIdFor\(/,
    'the event id and the asked id are folded before they are compared');
});

test('a merge keeps the collection the open chat holds', () => {
  // Two rows of one person are merged into one conversation. The rows are
  // rewritten into a surviving collection in one go, and the survivor is the one
  // the open chat holds: the page watches that instance, and a merge that rebuilt
  // the other one left it attached to a collection that receives nothing more.
  const merge = data.slice(data.indexOf('private void MergeMessages'),
    data.indexOf('private void MergeMessages') + 2800);
  assert.match(merge, /_activeChatId/,
    'the merge asks which chat is on screen');
  assert.match(merge, /_chatMessages\[to\] = survivor/,
    'and re-keys the collection the open chat holds when that is the one kept');
});
