'use strict';

// I test del guard sui nomi: il XAML buono e quello rotto vengono dal fixture,
// cosi' nameProblems si prova senza compilare l app.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');
const { nameProblems } = require('../check-xaml-names.js');

const fixture = (name) => fs.readFileSync(
  path.join(__dirname, '..', 'xaml-names-fixtures', name), 'utf8');

const problems = (name) => nameProblems(fixture(name), name).join('\n');

test('nomi unici: nessun problema', () => {
  assert.deepStrictEqual(nameProblems(fixture('unique-names.xaml'), 'unique-names.xaml'), []);
});

test('lo stesso nome in due DataTemplate diversi e legittimo', () => {
  assert.deepStrictEqual(
    nameProblems(fixture('same-name-in-two-templates.xaml'), 'same-name-in-two-templates.xaml'),
    []);
});

test('un ControlTemplate dentro un DataTemplate ha il suo namescope', () => {
  assert.deepStrictEqual(
    nameProblems(fixture('nested-templates.xaml'), 'nested-templates.xaml'), []);
});

test('un nome dentro un commento non conta', () => {
  assert.deepStrictEqual(
    nameProblems(fixture('commented-name.xaml'), 'commented-name.xaml'), []);
});

test('due volte lo stesso nome in un DataTemplate e un problema', () => {
  const found = problems('duplicate-in-template.xaml');
  assert.match(found, /PlayAudioButton/);
  assert.match(found, /4 and 5/);
});

test('due volte lo stesso nome nella radice della pagina e un problema', () => {
  const found = nameProblems('<Page>\n<Button x:Name="A"/>\n<Button x:Name="A"/>\n</Page>',
    'inline.xaml').join('\n');
  assert.match(found, /"A"/);
  assert.match(found, /2 and 3/);
});
