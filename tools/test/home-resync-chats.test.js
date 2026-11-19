'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

test('the resync command sits under the home menus three dots', () => {
  const xaml = read('WhatsappApp/Pages/HomePage.xaml');
  const bar = xaml.slice(xaml.indexOf('ChatsApplicationBar'),
    xaml.indexOf('StatusApplicationBar'));
  assert.match(bar, /ApplicationBarMenuItem[^>]*Click="ResyncChatsAppBarMenuItem_Click"/,
    'a menu item, the rows under the three dots, not another button on the bar');
  assert.doesNotMatch(bar, /ApplicationBarIconButton[^>]*Resync/);
});

test('one tap runs the whole repair', () => {
  const page = read('WhatsappApp/Pages/HomePage.xaml.cs');
  assert.match(page, /Guarded\.RunGuardedAsync\("HomePage\/resync chats", DataService\.Instance\.ResyncChatsAsync\(\)\)/,
    'the tap is the only step the person does');
});

test('the repair drops the photograph and rebuilds from the tails', () => {
  const data = read('WhatsappApp/Services/DataService.cs');
  const resync = data.slice(data.indexOf('public async Task ResyncChatsAsync'),
    data.indexOf('public async Task ResyncChatsAsync') + 1600);
  const drop = resync.indexOf('ChatCache.DeleteAsync');
  const restore = resync.indexOf('RestoreRowsFromTheirTailsAsync');
  const save = resync.indexOf('SaveSnapshotAsync');
  assert.ok(drop >= 0, 'the saved list goes first: it is a photograph, not the truth');
  assert.ok(restore > drop, 'the rows come back from their tails');
  assert.ok(save > restore, 'and the list as it stands is written back through the one writer');
  assert.doesNotMatch(resync, /MessageCache\.(Delete|DeleteAll)/,
    'a repair never drops a tail: the messages are not the part that was inaccurate');
});

test('the download is the login sync running again', () => {
  const data = read('WhatsappApp/Services/DataService.cs');
  const resync = data.slice(data.indexOf('public async Task ResyncChatsAsync'),
    data.indexOf('public async Task ResyncChatsAsync') + 1600);
  assert.match(resync, /NativeConnection\.Instance\.RestartAsync\(\)/,
    'WhatsApp names the list after every login, and that is the one download this protocol has');
});

test('the delete also drops the temp a killed write left', () => {
  const cache = read('WhatsappApp/Services/ChatCache.cs');
  const del = cache.slice(cache.indexOf('public static async Task DeleteAsync'),
    cache.indexOf('public static async Task DeleteAsync') + 1100);
  assert.match(del, /TempName/,
    'a repair must not leave a stale chats.json.new to race the next write');
});