'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const xaml = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ChatPage.xaml'), 'utf8');
const code = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ChatPage.xaml.cs'), 'utf8');
const project = fs.readFileSync(path.join(ROOT, 'WhatsappApp/WhatsApp.csproj'), 'utf8');

function namedElement(source, name) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = new RegExp(`<([\\w:.]+)\\b[^>]*x:Name="${escaped}"[^>]*>`).exec(source);
  assert.ok(match, `XAML element ${name} exists`);
  return match[0];
}

test('chat composer follows the four-column XAP layout with the active action at the right', () => {
  const record = namedElement(xaml, 'RecordButton');
  const attach = namedElement(xaml, 'AttachButton');
  const input = namedElement(xaml, 'MessageTextBox');
  const send = namedElement(xaml, 'SendButton');
  const emoji = namedElement(xaml, 'SmileyButton');
  const inputRowStart = xaml.indexOf('<Grid Grid.Row="1">', xaml.indexOf('<!-- Input row -->'));
  const inputPanelEnd = xaml.indexOf('<!-- The voice-note player', inputRowStart);
  const inputPanel = xaml.slice(inputRowStart, inputPanelEnd);
  const definitionsStart = inputPanel.indexOf('<Grid.ColumnDefinitions>');
  const definitionsEnd = inputPanel.indexOf('</Grid.ColumnDefinitions>', definitionsStart);
  assert.ok(inputRowStart >= 0 && definitionsStart >= 0 && definitionsEnd > definitionsStart,
    'composer declares columns');
  const definitions = inputPanel.slice(definitionsStart, definitionsEnd);
  const columns = definitions.match(/<ColumnDefinition\s+Width="([^"]+)"\s*\/>/g) || [];
  assert.deepStrictEqual(columns.map((entry) => /Width="([^"]+)"/.exec(entry)[1]),
    ['Auto', '*', 'Auto', 'Auto'], 'composer has attach, text, emoji, and action columns');
  assert.ok(record.includes('Grid.Column="3"'));
  assert.ok(attach.includes('Grid.Column="0"'));
  assert.ok(input.includes('Grid.Column="1"'));
  assert.ok(emoji.includes('Grid.Column="2"'));
  assert.ok(send.includes('Grid.Column="3"'));
  assert.ok(record.includes('Visibility="Collapsed"'));
  assert.ok(send.includes('Visibility="Collapsed"'));
  assert.ok(code.includes('private void UpdateComposerAction('));
  assert.ok(code.includes('SendButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed'));
  assert.ok(code.includes('RecordButton.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible'));
});

test('chat contextual actions use the native bar and selection controls report unavailable operations truthfully', () => {
  assert.match(xaml, /<shell:ApplicationBar x:Key="ChatApplicationBar"/);
  assert.match(xaml, /<shell:ApplicationBar x:Key="SelectedMessagesApplicationBar"/);
  assert.match(xaml, /ApplicationBarMenuItem[^>]*Click="ContactInfoAppBarMenuItem_Click"/);
  assert.match(xaml, /ApplicationBarMenuItem[^>]*Click="SelectMessagesAppBarMenuItem_Click"/);
  assert.ok(code.includes('ApplicationBar = ChatApplicationBar'));
  assert.ok(code.includes('ApplicationBar = SelectedMessagesApplicationBar'));
  assert.ok(code.includes('private void UpdateSelectionActions()'));
  assert.ok(code.includes('CopyMessagesAppBarButton.IsEnabled = any && AllSelectedMessagesAreText()'));
  assert.match(code, /private void ForwardMessagesAppBarButton_Click[\s\S]*forwarding is unavailable/);
  assert.match(code, /private void CopyMessagesAppBarButton_Click[\s\S]*copying is unavailable/);
  assert.match(code, /private void DeleteMessagesAppBarButton_Click[\s\S]*not available/);
});

test('chat composer artwork and native context bar use packaged XAP resources', () => {
  const assets = [
    'search.png', 'newgroup.png', 'camera-icon.png',
    'inputbar-attach.png', 'inputbar-emoji.png', 'inputbar-keyboard.png',
    'inputbar-mic.png', 'inputbar-send.png', 'forward.png', 'copy.png', 'delete.png'
  ];
  for (const asset of assets) {
    assert.ok(project.includes(`Assets\\Reference\\${asset}`), `${asset} is packaged by the app project`);
  }
  for (const asset of ['inputbar-attach', 'inputbar-emoji', 'inputbar-keyboard', 'inputbar-send', 'inputbar-mic']) {
    assert.ok(xaml.includes(`/Assets/Reference/${asset}.png`), `${asset} is used by the composer`);
  }
  assert.ok(xaml.includes('<shell:ApplicationBar'));
  assert.match(xaml, /ApplicationBarMenuItem[^>]*Click="[A-Za-z_][A-Za-z0-9_]*"/);
  assert.ok(code.includes('ApplicationBar = _chatApplicationBar'));
});
