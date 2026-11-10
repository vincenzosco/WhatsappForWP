'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const xaml = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/HomePage.xaml'), 'utf8');
const code = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/HomePage.xaml.cs'), 'utf8');

function namedElement(source, name, tag) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const open = new RegExp(`<${tag}\\b[^>]*x:Name="${escaped}"[^>]*>`).exec(source);
  assert.ok(open, `${tag} ${name} exists`);
  const tokens = new RegExp(`<${tag}\\b[^>]*>|</${tag}>`, 'g');
  tokens.lastIndex = open.index;
  let depth = 0;
  for (let match; (match = tokens.exec(source));) {
    if (match[0].startsWith('</')) depth--;
    else depth++;
    if (depth === 0) return source.slice(open.index, tokens.lastIndex);
  }
  assert.fail(`${tag} ${name} is not closed`);
}

test('home app-bar actions are centered and overflow stays at the right edge', () => {
  const sections = [
    ['ChatsBar', 'StatusBar'],
    ['StatusBar', 'CallsBar'],
    ['CallsBar', 'ModalOverlay']
  ];
  for (const [name, next] of sections) {
    const start = xaml.indexOf(`x:Name="${name}"`);
    const end = xaml.indexOf(`x:Name="${next}"`, start + name.length);
    const section = xaml.slice(start, end);
    assert.match(section, /<StackPanel[^>]*HorizontalAlignment="Center"/,
      `${name} action group is centered`);
    assert.match(section, /<Button[^>]*x:Name="(?:MoreButton|StatusMoreButton|CallsMoreButton)"[^>]*HorizontalAlignment="Right"/,
      `${name} overflow stays on the right`);
  }
});

test('overflow ellipses use one color in chats, status and calls', () => {
  const bars = ['MoreButton', 'StatusMoreButton', 'CallsMoreButton'];
  for (const name of bars) {
    const start = xaml.indexOf(`x:Name="${name}"`);
    const end = xaml.indexOf('</Button>', start);
    const button = xaml.slice(start, end);
    assert.equal((button.match(/<Ellipse/g) || []).length, 3, `${name} has three dots`);
    assert.doesNotMatch(button, /Fill="#FF20C4BC"/, `${name} uses the same white dots`);
  }
});

test('home overflow and row action menus open as a dismissible Metro menu above the bar', () => {
  const showMenu = code.slice(code.indexOf('private void ShowActionMenu('), code.indexOf('private void AddModalOptionHandler('));
  assert.match(showMenu, /ModalCard\.HorizontalAlignment\s*=\s*HorizontalAlignment\.Right/);
  assert.match(showMenu, /ModalCard\.VerticalAlignment\s*=\s*VerticalAlignment\.Bottom/);
  assert.match(showMenu, /ModalOverlay\.Background\s*=\s*new SolidColorBrush\(Colors\.Transparent\)/);
  assert.match(showMenu, /ModalCard\.BorderThickness\s*=\s*new Thickness\(0\)/);
  assert.match(showMenu, /ModalDismissButton\.Visibility\s*=\s*Visibility\.Visible/);
  assert.match(xaml, /<Button x:Name="ModalDismissButton"[\s\S]*?Click="ModalDismissButton_Click"/);
  assert.match(xaml, /<Border x:Name="ModalCard"/);
  assert.match(code, /private void ModalDismissButton_Click\(/);
  assert.match(code, /SetAutomationName\(ModalDismissButton, Loc\.Get\("ChatsPage_CloseMenu", "Close menu"\)\)/);
  assert.match(code, /private void StatusMoreButton_Click/);
  assert.match(code, /private void CallsMoreButton_Click/);
  assert.match(code, /ModalOverlay\.Background\s*=\s*new SolidColorBrush\(Color\.FromArgb\(217, 0, 0, 0\)\)/);
  assert.match(code, /ModalCard\.BorderThickness\s*=\s*new Thickness\(1\)/);
});
