'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const xaml = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/HomePage.xaml'), 'utf8');
const code = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/HomePage.xaml.cs'), 'utf8');
const app = fs.readFileSync(path.join(ROOT, 'WhatsappApp/App.xaml'), 'utf8');

function buttonMarkup(name) {
  const start = xaml.indexOf(`x:Name="${name}"`);
  assert.notEqual(start, -1, `button ${name} exists`);
  const end = xaml.indexOf('</Button>', start);
  return xaml.slice(start, end + '</Button>'.length);
}

test('the home bottom bar draws the original circles: ring, artwork, no label', () => {
  // What the screen shows is the ring bar, not a native app bar: the row is on
  // screen and nothing in the page assigns an ApplicationBar any more.
  const row = /<Grid x:Name="HomeBarRow"[^>]*>/.exec(xaml);
  assert.ok(row, 'the ring-bar row exists');
  assert.doesNotMatch(row[0], /Collapsed/, 'the ring bar is on screen');
  assert.doesNotMatch(xaml, /<shell:ApplicationBar\b/, 'no native bars are declared for the home');
  assert.doesNotMatch(code, /ApplicationBar\s*=/, 'the home draws no native app bar');

  // Each section shows its own rings; the search screen hides the whole row.
  assert.match(code, /ChatsBar\.Visibility/);
  assert.match(code, /StatusBar\.Visibility/);
  assert.match(code, /CallsBar\.Visibility/);

  // The circles carry the original artwork: magnifier, plus, the group mark,
  // a handset and the refresh arrow, each drawn by the extracted XAP asset.
  const glyphs = {
    SearchButton: '/Assets/Reference/search.png',
    NewChatButton: '/Assets/Reference/add.png',
    NewGroupButton: '/Assets/Reference/newgroup.png',
    NewCallButton: '/Assets/Reference/ThemeDark/phone-solid.png',
    RefreshStatusButton: '/Assets/Reference/refresh.png',
    RefreshCallsButton: '/Assets/Reference/refresh.png',
  };
  for (const [name, uri] of Object.entries(glyphs)) {
    const markup = buttonMarkup(name);
    assert.match(markup, new RegExp(`<Image Source="${uri.replace(/\//g, '\\/')}"`),
      `${name} draws ${uri}`);
    assert.match(markup, /Width="24" Height="24"/, `${name} scales the glyph like the original`);
  }

  // This client cannot create groups - the protocol has no such call - so the
  // third circle is drawn to complete the design and disabled, not faked.
  assert.match(buttonMarkup('NewGroupButton'), /IsEnabled="False"/);

  // No substitute vector artwork is left in the bar.
  const rowStart = xaml.indexOf('x:Name="HomeBarRow"');
  const rowEnd = xaml.indexOf('x:Name="ModalOverlay"');
  assert.notEqual(rowStart, -1, 'the ring-bar row is named HomeBarRow');
  assert.doesNotMatch(xaml.slice(rowStart, rowEnd), /<Path\b/, 'no hand-drawn glyphs remain');

  // The ring itself: a circle around the glyph, bold like the original's.
  const style = app.slice(app.indexOf('x:Key="AppBarButtonStyle"'), app.indexOf('x:Key="OverflowButtonStyle"'));
  assert.match(style, /x:Name="Tile" Width="40" Height="40"/);
  assert.match(style, /<Ellipse[^>]*Stroke="White" StrokeThickness="3"/);

  // No label under the circle.
  assert.match(xaml, /x:Key="AppBarLabelStyle"[\s\S]{0,400}?Visibility" Value="Collapsed"/);

  // The dots overflow keeps the working commands, settings included.
  assert.match(code, /AddModalOptionHandler\(Loc\.Get\("ChatsPage_PinChat"/);
  assert.match(code, /AddModalOptionHandler\(Loc\.Get\("ChatsPage_UnpinAll"/);
  assert.match(code, /AddModalOptionHandler\(Loc\.Get\("ChatsPage_ReadAll"/);
  assert.match(code, /AddModalOptionHandler\(Loc\.Get\("[^"]+", "Settings"\)/);
  assert.match(code, /private void NewCallButton_Click\(object sender, RoutedEventArgs e\)/);
});
