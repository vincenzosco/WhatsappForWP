'use strict';

// I test del guard sul XAML: i documenti buoni e quelli rotti sono stringhe qui
// dentro, cosi' xamlProblems si prova senza compilare l app e senza toccare i file.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');
const { xamlProblems, resourceKeyProblems } = require('../check-xaml.js');

const problems = (xaml) => xamlProblems(xaml, 'prova.xaml').join('\n');

const GOOD = `<?xml version="1.0" encoding="utf-8"?>
<!-- un commento con <Grid> dentro non e' markup -->
<Page x:Class="App.Page" Width="480">
  <Grid.RowDefinitions>
    <RowDefinition Height="Auto"/>
  </Grid.RowDefinitions>
  <TextBlock Text="a &amp; b &#233; &lt;c&gt;">
    <TextBlock.Resources>
      <Style x:Key="S" TargetType="Button"/>
    </TextBlock.Resources>
  </TextBlock>
  <![CDATA[ <non> </markup> ]]>
</Page>
`;

test('un documento ben formato non ha problemi', () => {
  assert.deepStrictEqual(xamlProblems(GOOD, 'prova.xaml'), []);
});

test('un tag esterno mai chiuso e un problema, con la riga', () => {
  const found = problems('<Page>\n  <Grid>\n    <TextBlock/>\n');
  assert.match(found, /prova\.xaml:1: <Page> opens and is never closed/);
  assert.match(found, /prova\.xaml:2: <Grid> opens and is never closed/);
});

test('un tag di chiusura sbagliato nomina i due elementi e la riga', () => {
  const found = problems('<Page>\n  <Grid>\n  </Border>\n</Page>');
  assert.match(found, /prova\.xaml:3: <\/Border> closes <Grid>, opened at line 2/);
});

test('un tag di chiusura senza apertura e un problema', () => {
  // Con la pila vuota non c'e' niente da chiudere; con un elemento aperto il
  // problema e' che il nome non combacia.
  assert.match(problems('<Page/>\n</Grid>'), /closes nothing/);
  assert.match(problems('<Page>\n</Grid>'), /<\/Grid> closes <Page>/);
});

test('un attributo senza virgolette e un problema', () => {
  const found = problems('<Page Width=480>\n</Page>');
  assert.match(found, /Width on <Page> has no quotes/);
});

test('un valore di attributo mai chiuso e un problema', () => {
  assert.match(problems('<Page Width="480>\n</Page>'), /never closes/);
});

test('un commento o un CDATA mai chiusi sono un problema', () => {
  assert.match(problems('<Page><!-- aperto\n</Page>'), /comment opens and never closes/);
  assert.match(problems('<Page><![CDATA[ aperto\n</Page>'), /CDATA opens and never closes/);
});

test('una & nuda nel testo o in un valore e un problema', () => {
  assert.match(problems('<Page>\n  <TextBlock Text="a & b"/>\n</Page>'),
    /carries a bare &, write &amp;/);
  assert.match(problems('<Page>\n  a & b\n</Page>'), /a bare & in the text/);
});

test('un < dentro un valore e un problema', () => {
  assert.match(problems('<Page Width="a < b"/>\n</Page>'), /carries a </);
});

test('una pagina che ridichiara una chiave di App.xaml la nasconde', () => {
  const application = '<ResourceDictionary><Style x:Key="Bar"/></ResourceDictionary>';
  const page = '<Page><Page.Resources><Style x:Key="Bar"/></Page.Resources></Page>';

  const found = resourceKeyProblems(application, [{ file: 'Pagina.xaml', text: page }]).join('\n');
  assert.match(found, /Pagina\.xaml: x:Key="Bar" shadows the key of the same name in App\.xaml/);
});

test('chiavi diverse fra applicazione e pagina non sono un problema', () => {
  const application = '<ResourceDictionary><Style x:Key="Bar"/></ResourceDictionary>';
  const page = '<Page><Page.Resources><Style x:Key="Row"/></Page.Resources></Page>';

  assert.deepStrictEqual(resourceKeyProblems(application, [{ file: 'Pagina.xaml', text: page }]), []);
});

test('la stessa chiave due volte nel dizionario dell applicazione e un problema', () => {
  const application = '<ResourceDictionary><Style x:Key="Bar"/><Style x:Key="Bar"/></ResourceDictionary>';

  assert.match(resourceKeyProblems(application, []).join('\n'), /declared twice/);
});

test('i due stili della barra vivono solo in App.xaml, non nelle pagine', () => {
  const root = path.join(__dirname, '..', '..');
  const application = fs.readFileSync(path.join(root, 'WhatsappApp/App.xaml'), 'utf8');
  const documents = ['HomePage', 'ChatPage'].map((name) => ({
    file: 'WhatsappApp/Pages/' + name + '.xaml',
    text: fs.readFileSync(path.join(root, 'WhatsappApp/Pages', name + '.xaml'), 'utf8')
  }));

  assert.deepStrictEqual(resourceKeyProblems(application, documents), []);
});

test('l ordine dei tag conta, non solo il numero', () => {
  const found = problems('<Page>\n  <Grid>\n    <TextBlock/>\n  </Grid>\n  </Grid>\n</Page>');
  assert.match(found, /prova\.xaml:5: <\/Grid> closes <Page>, opened at line 1/);
});
