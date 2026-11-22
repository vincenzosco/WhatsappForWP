// The hold menus are the platform's own: the `ContextMenu` of the Windows Phone
// Toolkit, which is the menu the rest of the phone opens on a long press - not a popup
// drawn by hand. The headers are lowercase because that is how the platform writes its
// menu items, and the one notice title that shares a key with a menu item keeps its own
// capitalised key. This pins the two hold surfaces, the toolkit menu, and the strings.
const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..', '..');
const read = (p) => fs.readFileSync(path.join(root, p), 'utf8');

const MENU_KEYS = [
  'ChatPage_Reply', 'ChatPage_DeleteEveryone', 'ChatPage_DeleteHere',
  'ChatsPage_Pin', 'ChatsPage_Unpin', 'ChatsPage_Mute', 'ChatsPage_Unmute',
  'ChatsPage_AddTile', 'ChatsPage_RemoveTile', 'ChatsPage_ShowInPeople', 'ChatsPage_Delete',
];

test('the message menu is the toolkit ContextMenu, not a hand-drawn popup', () => {
  const page = read(path.join('WhatsappApp', 'Pages', 'ChatPage.xaml.cs'));
  assert.match(page, /new Microsoft\.Phone\.Controls\.ContextMenu\(\)/);
  assert.match(page, /new Microsoft\.Phone\.Controls\.MenuItem\(\)/);
  assert.match(page, /ContextMenuService\.SetContextMenu\(anchor, menu\)/);
  assert.ok(!/_actionPopup/.test(page), 'the custom Popup is gone');
  // The menu draws nothing of its own; the prompt is a Popup and stays one.
  const menu = page.slice(page.indexOf('private void ShowActionPopup'), page.indexOf('private void CloseActionMenu'));
  assert.ok(!/new Popup\(\)/.test(menu), 'and nothing draws a menu of its own');
});

test('the chat row menu is the toolkit ContextMenu too', () => {
  const home = read(path.join('WhatsappApp', 'Pages', 'HomePage.xaml.cs'));
  assert.match(home, /new Microsoft\.Phone\.Controls\.ContextMenu\(\)/);
  assert.match(home, /ContextMenuService\.SetContextMenu\(row, menu\)/);
  assert.match(home, /private static void AddHoldItem\(/);
  const body = home.slice(home.indexOf('ShowChatMenu'), home.indexOf('private static void AddHoldItem'));
  assert.ok(!/AddModalOption\(/.test(body), 'the row menu does not use the modal list');
});

test('both hold surfaces still take the platform hold', () => {
  assert.match(read(path.join('WhatsappApp', 'Pages', 'ChatPage.xaml')), /Hold="Bubble_Holding"/);
  assert.match(read(path.join('WhatsappApp', 'Pages', 'HomePage.xaml')), /Hold="ChatRow_Holding"/);
});

test('the menu headers are lowercase in every language and every store', () => {
  const files = [
    path.join('WhatsappApp', 'Strings', 'en-US', 'Resources.resw'),
    path.join('WhatsappApp', 'Strings', 'it-IT', 'Resources.resw'),
    path.join('WhatsappApp.Core', 'Strings', 'Resources.resx'),
    path.join('WhatsappApp.Core', 'Strings', 'Resources.it-IT.resx'),
  ];
  for (const file of files) {
    const text = fs.readFileSync(path.join(root, file), 'utf8');
    for (const key of MENU_KEYS) {
      const found = text.match(new RegExp('<data name="' + key + '"[^>]*>\\s*<value>([^<]*)</value>'));
      assert.ok(found, file + ' still has ' + key);
      assert.equal(found[1], found[1].toLowerCase(),
        file + ': the header of ' + key + ' must be lowercase, found "' + found[1] + '"');
    }
  }
});

test('the notice that shared the menu key keeps a capitalised title of its own', () => {
  const page = read(path.join('WhatsappApp', 'Pages', 'ChatPage.xaml.cs'));
  assert.match(page, /ChatPage_RevokeFailedTitle/, 'the failure notice has its own title');
  const en = fs.readFileSync(path.join(root, 'WhatsappApp', 'Strings', 'en-US', 'Resources.resw'), 'utf8');
  const title = en.match(/<data name="ChatPage_RevokeFailedTitle"[^>]*>\s*<value>([^<]*)</);
  assert.ok(title && /^[A-Z]/.test(title[1]), 'a notice title starts with a capital');
});

test('the code fallbacks are lowercase too', () => {
  const home = read(path.join('WhatsappApp', 'Pages', 'HomePage.xaml.cs'));
  const menu = home.slice(home.indexOf('ShowChatMenu'), home.indexOf('private static void AddHoldItem'));
  for (const word of ['"pin"', '"unpin"', '"mute"', '"unmute"', '"delete chat"']) {
    assert.ok(menu.includes('Loc.Get(') && menu.includes(word), 'the fallback ' + word + ' is lowercase');
  }
});