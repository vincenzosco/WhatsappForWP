#!/usr/bin/env node
/**
 * tools/check-chat-list-source.js
 *
 * Guard for a second writer on the conversation list.
 *
 * Perche' esiste: `MessagesListView` prende la sua sorgente da
 * `ConversationView.Bind`, in codice, e da nessun altro posto. Un
 * `ItemsSource="{Binding}"` lasciato in XAML punta al `DataContext` della
 * pagina, che li' e' un `Contact` e non una collezione: la binding combatte il
 * writer in codice, la lista resta vuota, e l'unico segno sono decine di
 * `first chance exception System.Exception` dentro `SYSTEM.NI.DLL`, senza una
 * riga `DIAG` perche' l'eccezione nasce dentro WinRT. E' successo davvero, ed e'
 * il motivo per cui questo guard esiste: un attributo in XAML e' invisibile a
 * ogni altro controllo.
 *
 * Regola: il tag che dichiara `x:Name="MessagesListView"` non ha `ItemsSource`.
 *
 * Usage:
 *   node tools/check-chat-list-source.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const XAML = path.join(ROOT, 'WhatsappApp', 'Pages', 'ChatPage.xaml');

/** I problemi di un testo XAML: vuoto quando la conversazione ha una sola sorgente. */
function problemsFor(xaml) {
  const problems = [];
  const tag = xaml.match(/<ListView\b[^>]*x:Name="MessagesListView"[^>]*>/);
  if (tag && /ItemsSource\s*=/.test(tag[0])) {
    problems.push('WhatsappApp/Pages/ChatPage.xaml: MessagesListView sets ItemsSource in XAML, '
      + 'but ConversationView.Bind is the one writer: a binding there is resolved against '
      + 'DataContext (a Contact, not a collection) and fights the code-set source, so the '
      + 'conversation opens empty with a run of SYSTEM.NI.DLL exceptions and no DIAG line');
  }
  return problems;
}

function main() {
  const problems = problemsFor(fs.readFileSync(XAML, 'utf8'));

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} second item source(s).`);
    process.exit(1);
  }
  console.log('OK: the conversation list is bound from code only.');
}

if (require.main === module) main();

module.exports = { problemsFor };
