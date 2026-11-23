'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const data = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/DataService.cs'), 'utf8');

// The rows of the messages are held under the folded id, so every look has to ask with
// that one. Two paths asked with whatever id the caller had: a reaction and the preview
// after a revocation found no messages at all on a chat filed under its `@lid` spelling -
// and both then rewrote the conversation's tail under that same unfiled id, which is where
// the colliding file names of `MessageCache` come from.

test('a reaction is applied to the messages under the folded id', () => {
  const method = data.slice(data.indexOf('private async Task ApplyReactionAsync'),
    data.indexOf('private async Task ApplyReactionAsync') + 400);
  assert.match(method, /chatId = ChatIdFor\(chatId\);/,
    'the id is folded before the map is looked at');
});

test('the preview after a revocation looks under the folded id too', () => {
  const method = data.slice(data.indexOf('private void RefreshPreview'),
    data.indexOf('private void RefreshPreview') + 400);
  assert.match(method, /chatId = ChatIdFor\(chatId\);/,
    'and so does the one that realigns the preview');
});
