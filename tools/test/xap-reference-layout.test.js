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
  assert.match(chat, /x:Name="SendButton" Style="\{StaticResource AppBarButtonStyle\}"/);
  assert.match(chat, /x:Name="MoreButton" Style="\{StaticResource OverflowButtonStyle\}"/);
  assert.match(chatCode, /AttachmentPickerTitle\.Text = Loc\.Get\("ChatPage_AttachMediaTitle"[\s\S]*?ToUpper/);
});

test('the composer sits on the 64 px line of the bar, flush to the right edge', () => {
  // The reference: the microphone has no circle and the same 64 px square as a
  // circle of the bar, the field fills the rest of the line to the screen edge, and
  // the tail hangs under that corner.
  assert.match(chat, /<!-- Input row[\s\S]{0,400}?<Grid Grid\.Row="1" MinHeight="64">/,
    'the composer line is 64 px at rest and may grow with the text');

  const record = named(chat, 'RecordButton');
  assert.match(record, /Width="64" Height="64"/);
  assert.match(record, /Margin="0"/);
  assert.match(chat, /x:Name="RecordButton"[\s\S]{0,400}?inputbar-mic\.png"[^>]*Width="26" Height="26"/);

  const bubble = named(chat, 'InputBubble');
  assert.match(bubble, /Margin="0,8,0,8"/, 'the field is inset 8 px above and below, and flush on both sides');

  const tail = /<Path Fill="#FFCCCCCC" Width="10" Height="8"[\s\S]{0,200}?Margin="0,0,0,0"/.exec(chat);
  assert.ok(tail, 'the tail hangs at the right edge, under the field');
});

test('the ring bar is defined once, for the home page and the chat alike', () => {
  const application = fs.readFileSync(path.join(root, 'WhatsappApp/App.xaml'), 'utf8');

  assert.match(application, /<Style x:Key="AppBarButtonStyle" TargetType="Button">/);
  assert.match(application, /<Style x:Key="OverflowButtonStyle" TargetType="Button">/);
  assert.match(application, /<Grid x:Name="Tile" Width="46" Height="46">/,
    'the shared ring is the 46 px circle both bars drew');

  assert.doesNotMatch(home, /x:Key="AppBarButtonStyle"/, 'the home page does not carry its own copy');
  assert.doesNotMatch(chat, /x:Key="(?:AppBar|Overflow|ChatBar)\w*Style"/, 'nor does the chat');

  assert.match(home, /x:Name="MoreButton" Style="\{StaticResource OverflowButtonStyle\}"/);
  assert.match(chat, /x:Name="SendButton" Style="\{StaticResource AppBarButtonStyle\}"/);
  assert.match(chat, /x:Name="MoreButton" Style="\{StaticResource OverflowButtonStyle\}"/);
});
