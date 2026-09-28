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
