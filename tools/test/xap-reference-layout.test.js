'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '../..');
const home = fs.readFileSync(path.join(root, 'WhatsappApp/Pages/HomePage.xaml'), 'utf8');
const chat = fs.readFileSync(path.join(root, 'WhatsappApp/Pages/ChatPage.xaml'), 'utf8');
const chatCode = fs.readFileSync(path.join(root, 'WhatsappApp/Pages/ChatPage.xaml.cs'), 'utf8');
const status = fs.readFileSync(path.join(root, 'WhatsappApp/Controls/StatusPane.xaml'), 'utf8');
const calls = fs.readFileSync(path.join(root, 'WhatsappApp/Controls/CallsPane.xaml'), 'utf8');

function named(sourceText, name) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const found = new RegExp(`<([\\w:.]+)\\b(?=[^>]*x:Name="${escaped}")[^>]*>`).exec(sourceText);
  assert.ok(found, `XAML element ${name} exists`);
  return found[0];
}

test('home chat rows use XAP-sized circular avatars and WP list typography', () => {
  assert.match(home, /<Grid Height="68" Margin="0,24,24,0" Background="Transparent"/);
  assert.match(home, /<ColumnDefinition Width="68"\/>/);
  assert.match(home, /x:Name="ChatAvatar"[^>]*Width="68" Height="68"/);
  assert.match(home, /<Ellipse Width="68" Height="68"[\s\S]*?Fill="\{Binding Initials, Converter=\{StaticResource InitialToColor\}\}"\/>/);
  assert.match(home, /<Ellipse Width="68" Height="68"[\s\S]*?<ImageBrush ImageSource="\{Binding Avatar\}" Stretch="UniformToFill"\/>/);
  assert.match(home, /TextBlock Text="\{Binding Name\}"[^>]*FontSize="32"/);
  assert.match(home, /TextBlock Text="\{Binding LastMessage\}"[^>]*FontSize="20"/);
  assert.match(home, /Text="\{Binding LastMessageTime\}"[^>]*FontSize="18\.667"/);
});

test('calls and status list measurements remain aligned with the reference list controls', () => {
  assert.match(calls, /<Grid Grid\.Column="0" Width="68" Height="68"/);
  assert.match(calls, /Text="\{Binding Name\}" FontSize="32"/);
  assert.match(calls, /Text="\{Binding Detail\}" Foreground="#FF939393" FontSize="20"/);
  assert.match(status, /<Grid Grid\.Column="0" Width="68" Height="68"/);
  assert.match(status, /Text="\{Binding Latest\.SenderName\}" Foreground="White" FontSize="32"/);
  assert.match(status, /Text="\{Binding Detail\}" Foreground="#FF939393" FontSize="20"/);
});

test('chat title is the XAP 60-pixel header with a 54-pixel avatar and 23-pixel name', () => {
  const header = named(chat, 'ContactHeader');
  assert.match(chat, /<Grid Grid.Row="0" Background="Black" Height="60">/);
  // The chevron moved out of the header (the hardware key goes back), so the
  // contact column starts after the avatar and the name and subtitle share a line.
  assert.match(header, /Grid\.Column="1"/);
  assert.match(chat, /<Grid x:Name="ContactHeader"[\s\S]{0,400}?<StackPanel Orientation="Horizontal"/);
  assert.match(chat, /<TextBlock x:Name="OnlineStatusText"[^>]*FontStyle="Italic"/);
  assert.match(chat, /x:Name="ContactAvatar"[^>]*Width="54" Height="54"/);
  assert.match(chat, /TextBlock x:Name="ContactNameText"[^>]*FontSize="23"/);
  assert.match(chat, /TextBlock x:Name="OnlineStatusText"[^>]*FontSize="20"/);
  assert.match(chatCode, /ContactNameText\.Text = contact\.Name\.ToUpperInvariant\(\)/);
});

test('the chat list starts at the reference leading margin', () => {
  // The leading inset is carried by the pivot item now: the list fills it and the
  // rows keep their own 24 px right margin.
  assert.match(home, /<phone:LongListSelector x:Name="ChatListView"[^>]*Margin="0,0,0,0"/);
  assert.match(home, /<Grid Background="Transparent" Margin="12,0,12,0">/);
});

test('the conversation fills the row under the title bar', () => {
  assert.match(chat, /<phone:LongListSelector x:Name="MessagesListView" Grid\.Row="1"/);
  assert.doesNotMatch(chat, /MessagesListView" Grid\.Row="1"[\s\S]{0,120}Grid\.RowSpan="2"/);
});

test('the attach media panel is the reference bottom sheet above the action bar', () => {
  assert.match(chat, /<Grid x:Name="AttachmentPickerOverlay" Grid\.Row="1" Grid\.RowSpan="3"/);
  assert.match(chat, /<Border Background="#FF1F1F1F"[\s\S]*?VerticalAlignment="Bottom"/);
  assert.match(chat, /x:Name="AttachmentPickerTitle"[\s\S]*?FontSize="15" FontWeight="Bold"/);
  const tiles = chat.match(/<Button x:Name="Attachment\w+Button"[\s\S]*?<\/Button>/g) || [];
  assert.equal(tiles.length, 6);
  for (const tile of tiles) {
    assert.match(tile, /Height="150"/, "the reference tiles are square and 150 px high");
    assert.match(tile, /HorizontalAlignment="Stretch"/, "the three columns fill the sheet");
    assert.match(tile, /Background="\{StaticResource WhatsAppAccentBrush\}"/);
    assert.match(tile, /HorizontalAlignment="Left" VerticalAlignment="Bottom"/, "the label sits at the bottom-left");
  }
  assert.match(chat, /attach-picture\.png/, "Albums keeps the picture mark");
  assert.match(chat, /attach-camera\.png/, "Photo is the camera, not a second picture mark");
  // The chat's ring bar is the home page's bar: the #171717 strip, its one-pixel
  // top edge, the shared 64 px button styles and the overflow at the right edge.
  assert.match(chat, /<Grid Grid\.Row="4" Background="#FF171717" MinHeight="64">/);
  assert.match(chat, /<Rectangle Height="1" VerticalAlignment="Top" Fill="#FF303030"\/>/);
  assert.match(chat, /x:Name="SendButton" Style="\{StaticResource ChatBarButtonStyle\}"/);
  assert.match(chat, /x:Name="MoreButton" Style="\{StaticResource ChatBarOverflowStyle\}"/);
  assert.match(chatCode, /AttachmentPickerTitle\.Text = Loc\.Get\("ChatPage_AttachMediaTitle"[\s\S]*?ToUpper/);
});
