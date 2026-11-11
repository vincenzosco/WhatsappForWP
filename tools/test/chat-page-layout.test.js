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
  // The row carries the bar's 64 px height now, so the marker finds it and the
  // declared height is part of the contract pinned by xap-reference-layout.
  const inputRow = /<!-- Input row[\s\S]{0,400}?<Grid Grid\.Row="1" MinHeight="64">/.exec(xaml);
  assert.ok(inputRow, 'the composer row is the 64 px line under the conversation');
  const inputRowStart = inputRow.index + inputRow[0].indexOf('<Grid');
  const inputPanelEnd = xaml.indexOf('<!-- The voice-note player', inputRowStart);
  const inputPanel = xaml.slice(inputRowStart, inputPanelEnd);
  const definitionsStart = inputPanel.indexOf('<Grid.ColumnDefinitions>');
  const definitionsEnd = inputPanel.indexOf('</Grid.ColumnDefinitions>', definitionsStart);
  assert.ok(inputRowStart >= 0 && definitionsStart >= 0 && definitionsEnd > definitionsStart,
    'composer declares columns');
  const definitions = inputPanel.slice(definitionsStart, definitionsEnd);
  const columns = definitions.match(/<ColumnDefinition\s+Width="([^"]+)"\s*\/>/g) || [];
  assert.deepStrictEqual(columns.map((entry) => /Width="([^"]+)"/.exec(entry)[1]),
    ['Auto', 'Auto', '*'], 'composer has the current recording, mention, and text columns');
  assert.ok(record.includes('Grid.Column="0"'));
  assert.ok(attach.includes('Grid.Column') === false);
  assert.ok(input.includes('Grid.Column="1"') === false);
  assert.ok(emoji.includes('Grid.Column') === false);
  assert.ok(send.includes('Grid.Column') === false);
  assert.match(code, /RecordButton\.Visibility = string\.IsNullOrEmpty\(MessageTextBox\.Text\)[\s\S]*?Visibility\.Collapsed/);
});

test('chat contextual actions use the native bar and selection controls report unavailable operations truthfully', () => {
  assert.match(xaml, /<shell:ApplicationBar x:Name="ChatAppBar" x:Key="ChatApplicationBar"/);
  assert.match(xaml, /<shell:ApplicationBar x:Name="SelectedMessagesAppBar" x:Key="SelectedMessagesApplicationBar"/);
  assert.match(xaml, /ApplicationBarMenuItem[^>]*Click="ContactInfoAppBarMenuItem_Click"/);
  assert.match(xaml, /ApplicationBarMenuItem[^>]*Click="SelectMessagesAppBarMenuItem_Click"/);
  assert.ok(code.includes('ApplicationBar = ChatAppBar'));
  assert.ok(code.includes('ApplicationBar = SelectedMessagesAppBar'));
  assert.ok(code.includes('private void UpdateSelectionActions()'));
  assert.ok(code.includes('CopyMessagesAppBarButton.IsEnabled = any && AllSelectedMessagesAreText()'));
  assert.match(code, /private void ForwardMessagesAppBarButton_Click[\s\S]*forwarding is unavailable/);
  assert.match(code, /private void CopyMessagesAppBarButton_Click[\s\S]*copying is unavailable/);
  assert.match(code, /private void DeleteMessagesAppBarButton_Click[\s\S]*not available/);
});

test('chat composer artwork and native context bar use packaged XAP resources', () => {
  const assets = [
    'search.png', 'newgroup.png', 'camera-icon.png', 'refresh.png', 'add.png',
    'ThemeDark/inputbar-attach.png', 'ThemeDark/inputbar-emoji.png', 'ThemeDark/inputbar-keyboard.png',
    'ThemeDark/inputbar-mic.png', 'ThemeDark/inputbar-send.png',
    'ThemeDark/forward.png', 'ThemeDark/copy.png', 'ThemeDark/delete.png'
  ];
  for (const asset of assets) {
    assert.ok(project.replace(/\\/g, '/').includes(`Assets/Reference/${asset}`), `${asset} is packaged by the app project`);
  }
  for (const asset of ['inputbar-attach', 'inputbar-emoji', 'inputbar-keyboard', 'inputbar-send', 'inputbar-mic']) {
    assert.ok(project.replace(/\\/g, '/').includes(`Assets/Reference/ThemeDark/${asset}.png`), `${asset} dark reference asset is packaged`);
  }
  assert.ok(xaml.includes('<shell:ApplicationBar'));
  assert.match(xaml, /ApplicationBarMenuItem[^>]*Click="[A-Za-z_][A-Za-z0-9_]*"/);
  assert.ok(code.includes('ApplicationBar = ChatAppBar'));
});
