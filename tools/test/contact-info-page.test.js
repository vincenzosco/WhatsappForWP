'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const xaml = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ContactInfoPage.xaml'), 'utf8');
const code = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ContactInfoPage.xaml.cs'), 'utf8');
const english = fs.readFileSync(path.join(ROOT, 'WhatsappApp.Core/Strings/Resources.resx'), 'utf8');
const italian = fs.readFileSync(path.join(ROOT, 'WhatsappApp.Core/Strings/Resources.it-IT.resx'), 'utf8');

function namedElement(name) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = new RegExp(`<([\\w:.]+)\\b[^>]*x:Name="${escaped}"[^>]*>`).exec(xaml);
  assert.ok(match, `XAML element ${name} exists`);
  return match[0];
}

test('contact info presents the contact name as its page title without duplicating it beside the photo', () => {
  assert.match(code, /TitleText\.Text = _contact\.Name\.ToUpperInvariant\(\)/);
  assert.doesNotMatch(xaml, /x:Name="NameText"/);
});

test('contact profile photo follows the reference 160-pixel square layout', () => {
  const avatar = namedElement('BigAvatar');
  assert.match(avatar, /Width="160"/);
  assert.match(avatar, /Height="160"/);
  assert.doesNotMatch(avatar, /CornerRadius=/);
  assert.match(xaml, /<Rectangle x:Name="BigAvatarCircle" Width="160" Height="160"/);
  assert.match(xaml, /<Rectangle x:Name="BigAvatarPicture" Width="160" Height="160"/);
});

test('contact info labels its supported profile section in both languages', () => {
  assert.match(xaml, /x:Name="ProfileHeading"/);
  assert.match(code, /ProfileHeading\.Text = Loc\.Get\("ContactInfoPage_ProfileHeading"/);
  assert.match(english, /<data name="ContactInfoPage_ProfileHeading"[\s\S]*?<value>Profile<\/value>/);
  assert.match(italian, /<data name="ContactInfoPage_ProfileHeading"[\s\S]*?<value>Profilo<\/value>/);
});
