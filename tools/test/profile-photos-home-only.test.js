'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const home = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/HomePage.xaml'), 'utf8');
const chat = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ChatPage.xaml'), 'utf8');
const chatCode = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ChatPage.xaml.cs'), 'utf8');

test('the home list keeps the profile photos, drawn as the reference draws them', () => {
  // The reference's chat rows carry the profile photo as a rounded square, not a
  // circle: a rectangle with soft corners, 68 px like the XAP list items.
  const avatar = home.match(/<Grid x:Name="ChatAvatar"[\s\S]*?<\/Grid>/);
  assert.ok(avatar, 'the home rows still draw a profile photo');
  assert.match(avatar[0], /Width="68" Height="68"/);
  assert.doesNotMatch(avatar[0], /<Ellipse/, 'the photo is not clipped to a circle');
  assert.match(avatar[0], /CornerRadius="5"/, 'the photo is a rounded square');
  assert.match(avatar[0], /<ImageBrush ImageSource="\{Binding Avatar\}"/,
    'the photo shows the avatar and not only the initials');
});

test('the chat shows no profile photo: they belong to the home list alone', () => {
  assert.doesNotMatch(chat, /ContactAvatar/, 'the chat header carries no avatar');
  assert.doesNotMatch(chat, /\{Binding Avatar\}/, 'no row of the chat draws one either');
});

test('the chat header is still the way into the contact card', () => {
  assert.match(chat, /<Grid x:Name="ContactHeader"[^>]*Tap="ContactHeader_Tapped"/);
  assert.match(chatCode, /private void ContactHeader_Tapped/);
});
