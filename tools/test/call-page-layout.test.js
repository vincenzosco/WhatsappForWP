'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const xaml = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/CallPage.xaml'), 'utf8');
const code = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/CallPage.xaml.cs'), 'utf8');

function button(name) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = new RegExp(`<Button\\b(?=[^>]*x:Name="${escaped}")[^>]*>`).exec(xaml);
  assert.ok(match, `XAML button ${name} exists`);
  return match[0];
}

test('call screen controls use the reference rectangular action tiles', () => {
  const styleStart = xaml.indexOf('<Style x:Key="CallTileStyle"');
  const style = xaml.slice(styleStart, xaml.indexOf('</Style>', styleStart) + 8);
  assert.doesNotMatch(style, /Property="Height"/);
  assert.doesNotMatch(style, /Property="Width"/);
  assert.match(style, /<Grid x:Name="Tile">[\s\S]*?<Rectangle Fill="\{TemplateBinding Background\}"\/>/);
  assert.doesNotMatch(style, /<Ellipse Fill="\{TemplateBinding Background\}"/);
});

test('incoming call has two rectangular actions and the live call uses two reference rows', () => {
  const incomingStart = xaml.indexOf('<Grid x:Name="CallIncomingButtons">');
  const incomingEnd = xaml.indexOf('</Grid>', xaml.indexOf('<Button x:Name="DeclineCallButton"', incomingStart));
  const incoming = xaml.slice(incomingStart, incomingEnd);
  assert.match(incoming, /<RowDefinition Height="84"\/>/);
  assert.doesNotMatch(incoming, /TextReplyButton|Grid\.Row="2"/);
  assert.match(button('AcceptCallButton'), /Grid.Row="0" Grid.Column="0"/);
  assert.match(button('DeclineCallButton'), /Grid.Row="0" Grid.Column="2"/);
  const liveStart = xaml.indexOf('<Grid x:Name="CallInProgressButtons">');
  const liveEnd = xaml.indexOf('</Grid>', xaml.indexOf('<Button x:Name="EndCallButton"', liveStart));
  const live = xaml.slice(liveStart, liveEnd);
  assert.match(live, /<RowDefinition Height="94"\/>[\s\S]*?<RowDefinition Height="6"\/>[\s\S]*?<RowDefinition Height="94"\/>/);
  assert.match(button('MuteCallButton'), /Grid.Row="0" Grid.Column="2"/);
  assert.match(button('SpeakerCallButton'), /Grid.Row="0" Grid.Column="0"/);
  assert.match(button('MessageCallButton'), /Grid.Row="2" Grid.Column="4"/);
  assert.match(button('EndCallButton'), /Grid.Row="2" Grid.Column="0" Grid.ColumnSpan="3"/);
});

test('the ended call keeps a single full-width close action and existing call operations remain wired', () => {
  assert.match(button('EndCallButton'), /Grid.Row="2"/);
  assert.match(code, /Grid\.SetColumnSpan\(EndCallButton, over \? 5 : 3\)/);
  for (const handler of ['AcceptCallButton_Click', 'DeclineCallButton_Click', 'MuteCallButton_Click',
    'SpeakerCallButton_Click', 'MessageCallButton_Click', 'EndCallButton_Click']) {
    assert.match(code, new RegExp(`private void ${handler}\\(`), `${handler} remains implemented`);
  }
  assert.match(code, /Call_NoAudio/);
});

test('the call screen carries the reference WhatsApp call header', () => {
  assert.match(xaml, /<Image x:Name="CallHeaderIcon"[\s\S]*?phone-solid\.png/);
  assert.match(xaml, /<TextBlock x:Name="CallHeaderText"[\s\S]*?FontSize="15"/);
  assert.match(code, /CallHeaderText\.Text = Loc\.Get\("Call_Header"/);
});
