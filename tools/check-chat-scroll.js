#!/usr/bin/env node
/**
 * tools/check-chat-scroll.js
 *
 * Guard per l'apertura di una chat sui messaggi nuovi e per la freccia che ci
 * riporta.
 *
 * Perche' esiste: la pagina lega la lista e chiede una volta sola l'ultima riga
 * (ChatPage.BindAfterCacheAsync), e poi conta su un'altra scrollata per il burst
 * di cronologia - il commento in ConversationView.QueueScroll la nomina ("The
 * next scroll - the one the history burst produces - runs when the list is up").
 * Quella scrollata non esiste: OnMessageReceived esce subito su ogni riga
 * `IsHistory`, quindi quando il burst arriva dopo il bind nessuno muove piu' la
 * vista e la chat resta sulla riga piu' vecchia che ha caricato. E' quello che
 * l'operatore ha visto come "non vanno direttamente ai nuovi messaggi, ma quelli
 * di una volta".
 *
 * Regole:
 *  1. la pagina segue il burst che si chiude: sottoscrive
 *     DataService.Instance.HistoryCompleted, si toglie, e nel gestore chiede la
 *     chat in primo piano (ActiveChatId) prima di muovere la vista
 *     (ScrollToBottom);
 *  2. la freccia in fondo alla conversazione e' cablata al suo gestore, sta
 *     sopra la lista, parte nascosta, e il suo gestore chiede il fondo alla
 *     vista.
 *
 * Usage:
 *   node tools/check-chat-scroll.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const PAGE = 'WhatsappApp/Pages/ChatPage.xaml.cs';
const XAML = 'WhatsappApp/Pages/ChatPage.xaml';

/**
 * Il corpo di un metodo, dalla sua firma alla graffa di chiusura sulla stessa
 * indentazione. Una stringa vuota quando il metodo non c'e', che e' il caso che
 * ogni regola deve poter nominare senza esplodere.
 */
function methodBody(text, signature) {
  const at = text.indexOf(signature);
  if (at < 0) return '';

  const rest = text.slice(at + signature.length);
  const end = rest.indexOf('\n        }');
  return end < 0 ? rest : rest.slice(0, end);
}

/**
 * Il blocco di un pulsante: dal suo <Button al </Button>. Una stringa vuota
 * quando il pulsante non c'e' o quando il blocco si mangerebbe il pulsante dopo
 * di lui, che e' il caso di un <Button ... /> chiuso in se'.
 */
function buttonBlock(xaml, name) {
  const at = xaml.indexOf('x:Name="' + name + '"');
  if (at < 0) return '';

  const start = xaml.lastIndexOf('<Button', at);
  if (start < 0) return '';

  const end = xaml.indexOf('</Button>', at);
  if (end < 0) return '';
  if (xaml.indexOf('<Button', start + 7) < end) return '';

  return xaml.slice(start, end);
}

/** Regola 2: la freccia che riporta ai messaggi recenti. */
function jumpButtonProblems(page, xaml) {
  const problems = [];
  const block = buttonBlock(xaml, 'RecentMessagesButton');

  if (!block) {
    problems.push(`${XAML}: there is no Button x:Name="RecentMessagesButton": WhatsApp's ` +
      'arrow is the way back to the newest message once the reader has scrolled away, and ' +
      'without it the only way back is dragging the conversation by hand');
  } else {
    const tag = block.slice(0, block.indexOf('>'));

    if (!/Click="RecentMessagesButton_Click"/.test(tag)) {
      problems.push(`${XAML}: RecentMessagesButton must be wired to ` +
        'Click="RecentMessagesButton_Click": the handler of a button carries its name, so a ' +
        'second button cannot take this one over (check-actions.js refuses it too)');
    }
    if (!/Grid\.Row="1"/.test(tag)) {
      problems.push(`${XAML}: RecentMessagesButton must be Grid.Row="1", the conversation's ` +
        'row: anywhere else it draws over the header or under the message box');
    }
    if (!/Visibility="Collapsed"/.test(tag)) {
      problems.push(`${XAML}: RecentMessagesButton must start Visibility="Collapsed": it is ` +
        'shown only while the newest message is off screen, and the position callback is what ' +
        'decides that');
    }
    if (block.indexOf('<!-- IconArrowDown -->') < 0) {
      problems.push(`${XAML}: RecentMessagesButton draws no <!-- IconArrowDown -->: an icon ` +
        'is a named inline PathGeometry, and check-icons.js cannot tell one from another ' +
        'without the comment');
    }
  }

  const body = methodBody(page, 'private void RecentMessagesButton_Click');
  if (!body) {
    problems.push(`${PAGE}: RecentMessagesButton_Click is missing: the arrow's tap has ` +
      'nowhere to land');
  } else if (!body.includes('_view.ScrollToBottom()')) {
    problems.push(`${PAGE}: RecentMessagesButton_Click must call _view.ScrollToBottom(): the ` +
      'jump back to the newest message is the view\'s own scroll, not a second one written here');
  }

  if (page.includes('ScrollableHeight')) {
    problems.push(`${PAGE}: the page must take the position from ConversationView and never ` +
      'from ScrollableHeight: a second opinion about the bottom is the one that goes stale');
  }

  return problems;
}

/** Regola 1: la pagina segue il burst di cronologia che si chiude. */
function historyFollowProblems(page) {
  const problems = [];

  if (!page.includes('DataService.Instance.HistoryCompleted += OnHistoryCompleted;')
    || !page.includes('DataService.Instance.HistoryCompleted -= OnHistoryCompleted;')) {
    problems.push(`${PAGE}: the page must subscribe to HistoryCompleted and take itself off ` +
      'again (OnHistoryCompleted): the burst that closes is what puts the newest messages ' +
      'in, and a page that is not told about it stays at its oldest row');
  }

  const body = methodBody(page, 'private void OnHistoryCompleted');

  if (!body.includes('DataService.Instance.ActiveChatId')) {
    problems.push(`${PAGE}: OnHistoryCompleted must ask ActiveChatId before it acts: a burst ` +
      "for a chat that is no longer in front would move another page's list");
  }
  if (!body.includes('_view.ScrollToBottom()')) {
    problems.push(`${PAGE}: OnHistoryCompleted must call _view.ScrollToBottom(): the ` +
      'conversation is bound before some bursts arrive, so nothing else moves it to its ' +
      'newest message');
  }

  return problems;
}

/**
 * I problemi della pagina e del suo XAML.
 * Restituisce un array di frasi, vuoto quando non c'e' niente di storto.
 */
function chatScrollProblems(files) {
  const page = String((files && files.page) || '');
  const xaml = String((files && files.xaml) || '');

  return historyFollowProblems(page).concat(jumpButtonProblems(page, xaml));
}

function main() {
  const problems = chatScrollProblems({
    page: fs.readFileSync(path.join(ROOT, PAGE), 'utf8'),
    xaml: fs.readFileSync(path.join(ROOT, XAML), 'utf8')
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} chat scroll problem(s).`);
    process.exit(1);
  }
  console.log('OK: the page follows the history burst that closes, and the arrow back to the '
    + 'newest message is wired to the view.');
}

if (require.main === module) main();

module.exports = { chatScrollProblems, methodBody };
