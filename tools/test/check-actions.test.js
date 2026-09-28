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

test('un pulsante della barra del titolo senza minimi si segnala', () => {
  const xaml = '<Button x:Name="MoreButton" MinWidth="0" MinHeight="0" Click="MoreButton_Click">\n' +
    '  <!-- IconOverflow -->\n</Button>\n' +
    '<Button x:Name="NewChatButton" Click="NewChatButton_Click">\n' +
    '  <!-- IconNewChat -->\n</Button>';
  const problems = actions.titleBarProblems(xaml, FILE);
  assert.strictEqual(problems.length, 2);
  assert.ok(problems.every((problem) => problem.includes('NewChatButton')));
});
