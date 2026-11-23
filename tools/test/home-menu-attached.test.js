// The row menu has to exist before the hold, not after. A menu built in the hold
// handler never came up: the list's gesture handling takes the hold first, and the
// toolkit's `ContextMenuService` - what the official app's own lists open their menus
// with (`<toolkit:ContextMenuService.ContextMenu>` inside the item template) - only
// answers a hold on an element that already carries a menu.
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..', '..');
const read = (p) => fs.readFileSync(path.join(root, p), 'utf8');

const xaml = read('WhatsappApp/Pages/HomePage.xaml');
const page = read('WhatsappApp/Pages/HomePage.xaml.cs');

function slice(text, marker, span) {
  const at = text.indexOf(marker);
  assert.ok(at >= 0, 'the code still has ' + marker);
  return text.slice(at, at + span);
}

test('every row carries its menu from the moment it is on the screen', () => {
  assert.match(xaml, /Loaded="ChatRow_Loaded"/, 'the row attaches its menu when it loads');
  const loaded = slice(page, 'private void ChatRow_Loaded(', 700);
  assert.match(loaded, /ShowChatMenu\(row, row\.DataContext as Contact, false\)/,
    'and is built, not opened');
});

test('the toolkit opens the menu the row already carries', () => {
  const menu = slice(page, 'private void ShowChatMenu(', 3600);
  assert.match(menu, /ContextMenuService\.SetContextMenu\(row, menu\)/,
    'the menu is the row\u2019s, so a hold on the row is what opens it');
  assert.match(menu, /if \(open\)[\s\S]{0,80}menu\.IsOpen = true/,
    'and the page may still open it itself');
});

test('the release is kept out of the menu\u2019s way whatever opened it', () => {
  const guard = slice(page, 'private void ChatRow_ReleaseGuard(', 900);
  assert.match(guard, /GetContextMenu\(row\)/, 'the row\u2019s own menu is asked too');
  assert.match(guard, /e\.Handled = true;/, 'and the release is consumed while one is open');
});