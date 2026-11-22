// A hold menu has to survive the finger lifting. The Toolkit closes a menu whose
// opening animation is still playing when a manipulation completes at the root, and
// that completion is exactly the release of the long-press that opened it, so the
// menu flashes and vanishes. The fix is to mark that release handled while a menu is
// open. The official client's own menus run without the zoom effect
// (`<toolkit:ContextMenu IsZoomEnabled="False">`), so ours do too.
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');

function read(rel) {
  return fs.readFileSync(path.join(ROOT, rel), 'utf8');
}

test('every hold menu runs without the zoom effect, like the official client', () => {
  const sites = [
    ['WhatsappApp/Pages/HomePage.xaml.cs', 'ShowChatMenu'],
    ['WhatsappApp/Pages/ChatPage.xaml.cs', 'ShowActionPopup'],
  ];
  for (const [file, method] of sites) {
    const text = read(file);
    const start = text.indexOf('void ' + method + '(');
    assert.ok(start > 0, method + ' exists in ' + file);
    const slice = text.slice(start, text.indexOf('\n        private ', start + 10));
    const body = slice.slice(slice.indexOf('{'));
    assert.match(body, /new Microsoft\.Phone\.Controls\.ContextMenu\(\)/,
      method + ' builds a Toolkit ContextMenu');
    assert.match(body, /menu\.IsZoomEnabled\s*=\s*false/,
      method + ' turns the zoom effect off');
  }
});

test('the release after a long-press is marked handled so the Toolkit cannot dismiss the menu', () => {
  const home = read('WhatsappApp/Pages/HomePage.xaml.cs');
  const homeHandler = home.slice(
    home.indexOf('private void ChatRow_ReleaseGuard'),
    home.indexOf('\n        private ', home.indexOf('private void ChatRow_ReleaseGuard') + 10));
  assert.match(homeHandler, /_holdMenu\s*==\s*null/, 'HomePage checks the open menu');
  assert.match(homeHandler, /e\.Handled\s*=\s*true/, 'HomePage marks the release handled');

  const chat = read('WhatsappApp/Pages/ChatPage.xaml.cs');
  const chatHandler = chat.slice(
    chat.indexOf('private void Bubble_ManipulationCompleted'),
    chat.indexOf('\n        private ', chat.indexOf('private void Bubble_ManipulationCompleted') + 10));
  assert.match(chatHandler, /_actionMenu\s*!=\s*null/, 'ChatPage checks the open menu');
  assert.match(chatHandler, /e\.Handled\s*=\s*true/, 'ChatPage marks the release handled');
});

test('the home chat row hooks the release gesture', () => {
  const xaml = read('WhatsappApp/Pages/HomePage.xaml');
  assert.match(xaml, /ManipulationCompleted="ChatRow_ReleaseGuard"/,
    'the row grid handles ManipulationCompleted');
});

test('a menu that closes forgets its handle, so a later tap cannot reopen it', () => {
  const home = read('WhatsappApp/Pages/HomePage.xaml.cs');
  assert.match(home, /menu\.Closed\s*\+=/, 'HomePage clears the menu on Closed');
  const chat = read('WhatsappApp/Pages/ChatPage.xaml.cs');
  assert.match(chat, /menu\.Closed\s*\+=/, 'ChatPage clears the menu on Closed');
});