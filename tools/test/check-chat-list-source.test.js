'use strict';
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');
const fs = require('node:fs');

const guard = require('../check-chat-list-source');

test('un ItemsSource sul ListView della conversazione e un problema', () => {
  const xaml = '<ListView x:Name="MessagesListView" Grid.Row="1"\n'
    + '          ItemsSource="{Binding}"\n'
    + '          SelectionMode="None">';
  const problems = guard.problemsFor(xaml);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /MessagesListView/);
});

test('lo stesso ListView senza ItemsSource non e un problema', () => {
  const xaml = '<ListView x:Name="MessagesListView" Grid.Row="1"\n'
    + '          SelectionMode="None">';
  assert.deepStrictEqual(guard.problemsFor(xaml), []);
});

test('un altro ListView con ItemsSource non e un problema', () => {
  const xaml = '<ListView x:Name="OtherList" ItemsSource="{Binding}">';
  assert.deepStrictEqual(guard.problemsFor(xaml), []);
});

test('il ChatPage.xaml del progetto ha una sola sorgente', () => {
  const file = path.join(__dirname, '..', '..', 'WhatsappApp', 'Pages', 'ChatPage.xaml');
  assert.deepStrictEqual(guard.problemsFor(fs.readFileSync(file, 'utf8')), []);
});
