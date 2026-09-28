'use strict';
const test = require('node:test');
const assert = require('node:assert');

const actions = require('../check-actions');

const FILE = 'Pages/ChatsPage.xaml';

test('un gestore che non porta il nome del pulsante si segnala', () => {
  const xaml = '<Button x:Name="NewChatButton" Click="SettingsButton_Click">\n</Button>';
  const problems = actions.buttonProblems(xaml, FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /NewChatButton_Click/);
});

test('una coppia coerente passa', () => {
  const xaml = '<Button x:Name="NewChatButton" Click="NewChatButton_Click">\n' +
    '  <!-- IconNewChat -->\n</Button>';
  assert.deepStrictEqual(actions.buttonProblems(xaml, FILE), []);
});

test('due azioni scambiate si segnalano', () => {
  const xaml = '<Button x:Name="NewChatButton" Click="SettingsButton_Click">\n' +
    '  <!-- IconNewChat -->\n</Button>\n' +
    '<Button x:Name="SettingsButton" Click="NewChatButton_Click">\n' +
    '  <!-- IconSettings -->\n</Button>';
  const problems = actions.buttonProblems(xaml, FILE);
  assert.strictEqual(problems.length, 2);
});

test("l'icona scambiata sulla barra del titolo si segnala", () => {
  const xaml = '<Button x:Name="NewChatButton" MinWidth="0" MinHeight="0" ' +
    'Click="NewChatButton_Click">\n' +
    '  <!-- IconSettings -->\n</Button>';
  const problems = actions.titleBarProblems(xaml, FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /IconNewChat/);
});

test('un pulsante a misura fissa senza minimi si segnala', () => {
  const xaml = '<Button x:Name="SendButton" Width="48" Height="48" Click="SendButton_Click">\n' +
    '  <!-- IconSend -->\n</Button>';
  const problems = actions.buttonProblems(xaml, FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /SendButton/);
  assert.match(problems[0], /MinWidth/);
});

test('un pulsante a misura fissa con i minimi a zero passa', () => {
  const xaml = '<Button x:Name="SendButton" Width="48" Height="48" MinWidth="0" MinHeight="0" ' +
    'Click="SendButton_Click">\n  <!-- IconSend -->\n</Button>';
  assert.deepStrictEqual(actions.buttonProblems(xaml, FILE), []);
});

test('un pulsante che dichiara solo Height resta fuori dalla regola', () => {
  const xaml = '<Button x:Name="ActionButton" Height="52" Click="ActionButton_Click">\n</Button>';
  assert.deepStrictEqual(actions.buttonProblems(xaml, FILE), []);
});

test('uno stile che dimensiona un pulsante senza minimi si segnala', () => {
  const xaml = '<Style x:Key="NavIconButtonStyle" TargetType="Button">\n' +
    '  <Setter Property="Height" Value="48"/>\n</Style>';
  const problems = actions.styleProblems(xaml, 'Controls/SectionNav.xaml');
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /NavIconButtonStyle/);
  assert.match(problems[0], /Height="48"/);
});

test('uno stile che dimensiona un pulsante con i minimi a zero passa', () => {
  const xaml = '<Style x:Key="NavIconButtonStyle" TargetType="Button">\n' +
    '  <Setter Property="MinWidth" Value="0"/>\n' +
    '  <Setter Property="MinHeight" Value="0"/>\n' +
    '  <Setter Property="Height" Value="48"/>\n</Style>';
  assert.deepStrictEqual(actions.styleProblems(xaml, 'Controls/SectionNav.xaml'), []);
});

test('uno stile di un altro controllo, o senza misure, resta fuori', () => {
  const items = '<Style x:Key="ChatItemContainerStyle" TargetType="ListViewItem">\n' +
    '  <Setter Property="Height" Value="72"/>\n</Style>';
  const unpadded = '<Style x:Key="PlainButtonStyle" TargetType="Button">\n' +
    '  <Setter Property="Padding" Value="0"/>\n</Style>';
  assert.deepStrictEqual(actions.styleProblems(items, FILE), []);
  assert.deepStrictEqual(actions.styleProblems(unpadded, FILE), []);
});

test('uno stile che eredita da un altro resta fuori', () => {
  const xaml = '<Style x:Key="SmallerStyle" TargetType="Button" ' +
    'BasedOn="{StaticResource NavIconButtonStyle}">\n' +
    '  <Setter Property="Height" Value="40"/>\n</Style>';
  assert.deepStrictEqual(actions.styleProblems(xaml, FILE), []);
});
