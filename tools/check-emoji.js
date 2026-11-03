#!/usr/bin/env node
/**
 * tools/check-emoji.js
 *
 * Guard on the emoji rule: the phone draws the ones it has, and what it draws is
 * `EmojiText.Compatible` - never the message itself.
 *
 * Perche' esiste: Windows Phone 8.1 si e' fermato a Unicode 6.1 e non ha mai avuto un
 * aggiornamento del font, quindi ogni emoji piu' nuova e' un codepoint senza glifo, e il
 * sistema disegna un quadratino. Il 28 ottobre 2026 le righe vere del telefono erano
 * `Benny🩵`, `robotica🦾🦾🦿` e `I bro di hip hop 🪐✨🪐`: tre cuori, un braccio meccanico
 * e un pianeta, tutti quadrati. La regola che li sostituisce e' in un file solo
 * (`EmojiText`), e questo guardiano tiene i due modi in cui una regola cosi' si perde:
 *
 *  1. **sparisce dal disegno**: una bolla che torna a legare `Text` invece di `ShownText`,
 *     o una riga di stato o un messaggio in notifica che leggono il testo grezzo -
 *     l'emoji torna a essere un quadratino e nessuno se ne accorge finche' non la manda
 *     qualcuno con un telefono nuovo;
 *  2. **finisce sul filo**: se `ChatMessage.Text` viene sostituito all'ingresso, il
 *     messaggio che parte non e' quello che e' stato scritto - si manda un cuore blu al
 *     posto di un cuore azzurro, e il destinatario con un telefono nuovo vede la
 *     sostituzione di un altro.
 *
 * E una regola sulla tastiera, che e' la richiesta gemella del 29 ottobre 2026 ("si
 * possano mandare emoji o :) dalla tastiera Microsoft"): su WP8.1 la faccina sulla
 * tastiera compare solo per lo scope `Chat`. Senza, il composer e' una casella di testo
 * qualsiasi e il telefono non offre ne' l'emoji ne' la conversione di `:)`.
 *
 * Usage:
 *   node tools/check-emoji.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');

const RULE = 'WhatsappApp/Protocol/Client/EmojiText.cs';
const MESSAGE = 'WhatsappApp/Models/ChatMessage.cs';
const CHAT_XAML = 'WhatsappApp/Pages/ChatPage.xaml';
const CHAT_CODE = 'WhatsappApp/Pages/ChatPage.xaml.cs';
const VIEWER = 'WhatsappApp/Pages/StatusViewerPage.xaml.cs';
const GROUP = 'WhatsappApp/Models/StatusGroup.cs';
const DATA = 'WhatsappApp/Services/DataService.cs';

/** Il corpo di una proprieta': dalla firma alla graffa che la chiude. */
function propertyBody(text, signature) {
  const start = text.indexOf(signature);
  if (start < 0) return null;

  const open = text.indexOf('{', start);
  if (open < 0) return null;

  let depth = 0;
  for (let i = open; i < text.length; i++) {
    if (text[i] === '{') depth++;
    else if (text[i] === '}') {
      depth--;
      if (depth === 0) return text.slice(start, i + 1);
    }
  }
  return null;
}

/**
 * I problemi, dati i testi dei file. Puro: i test gli passano copie manomesse.
 */
function emojiProblems(files) {
  files = files || {};
  const problems = [];
  const rule = String(files[RULE] || '');
  const message = String(files[MESSAGE] || '');
  const chat = String(files[CHAT_XAML] || '');
  const composer = String(files[CHAT_CODE] || '');
  const viewer = String(files[VIEWER] || '');
  const group = String(files[GROUP] || '');
  const data = String(files[DATA] || '');

  // 1. La regola c'e', e ha una tabella: un `Compatible` vuoto e' un file che promette
  //    una sostituzione che non fa.
  if (!rule) {
    problems.push(`${RULE}: the emoji rule is missing - the emoji this phone has no glyph `
      + 'for (the heart of 15.0, the mechanical arm, the ringed planet) are boxes again');
  } else {
    if (!/public static string Compatible\s*\(/.test(rule)) {
      problems.push(`${RULE}: no Compatible(string) - nothing can ask what this phone draws`);
    }
    if (!/Dictionary<int, string>/.test(rule)) {
      problems.push(`${RULE}: no table of replacements - every emoji newer than the font `
        + 'would be dropped, including the ones that have an older cousin');
    }
  }

  // 2. Il messaggio disegnato chiede la regola, e quello mandato no.
  if (!/public string ShownText/.test(message)) {
    problems.push(`${MESSAGE}: no ShownText - the bubbles have nothing to bind but the raw `
      + 'message, and the raw message is a box where the font has no glyph');
  }

  const textSetter = propertyBody(message, 'public string Text');
  if (textSetter && /EmojiText\.Compatible/.test(textSetter)) {
    problems.push(`${MESSAGE}: the Text setter substitutes the emoji - that is the message `
      + 'itself: it goes to the wire and into the cache, and the person on the other side '
      + 'would receive the older emoji instead of the one that was written');
  }

  // 3. Le due bolle e il testo grande dello stato leggono la proprieta' disegnata.
  if (/Text="\{Binding Text\}"/.test(chat)) {
    problems.push(`${CHAT_XAML}: a bubble binds Text - the message text, not ShownText: the `
      + 'emoji of a newer phone is drawn as an empty box in the conversation');
  }

  if (/_status\.Text\b/.test(viewer)) {
    problems.push(`${VIEWER}: the status viewer reads _status.Text - the caption of an `
      + 'update is the largest text in the app, and it is where the new emoji are');
  }

  if (!/Latest\.ShownText/.test(group)) {
    problems.push(`${GROUP}: Finish() does not read Latest.ShownText - the caption under the `
      + 'author name on the status tab is the raw update');
  }

  // 4. Le notifiche e la tile: il corpo passa dalla regola.
  if (/NotificationService\.ShowMessage\([^)]*message\.Text\s*\)/.test(data)) {
    problems.push(`${DATA}: the toast body is message.Text - the same box, now on the lock `
      + 'screen, where it is the only thing to read');
  }  // 5. La tastiera: lo scope Chat e' l'unica cosa che mette la faccina sulla tastiera.
  if (!/InputScopeName[^>]*NameValue="Chat"/.test(chat)) {
    problems.push(`${CHAT_XAML}: the composer has no InputScope Chat - Windows Phone 8.1 `
      + 'shows the emoji key (and its own conversion of a typed ":)") only for that scope, '
      + 'so the composer offers neither');
  }

  // 5b. Il tasto di ritorno della tastiera manda il messaggio. Su WP8.1 la tastiera su
  //     schermo consegna il tasto alla pagina solo con `KeyUp` (il `KeyDown` lo consuma
  //     il TextBox), quella fisica solo con `KeyDown`: con uno solo dei due, l'Invio del
  //     telefono in mano non manda niente (3 novembre 2026). E `AcceptsReturn="False"`,
  //     perche' un a capo non deve finire dentro un messaggio che la bolla disegna su una
  //     riga sola.
  const composerTag = /<TextBox[^>]*x:Name="MessageTextBox"[^>]*>/.exec(chat);
  if (!composerTag) {
    problems.push(`${CHAT_XAML}: no MessageTextBox - the composer this rule is about is gone`);
  } else {
    const box = composerTag[0];
    if (!/KeyUp="MessageTextBox_KeyUp"/.test(box)) {
      problems.push(`${CHAT_XAML}: the composer has no KeyUp - Windows Phone 8.1's onboard `
        + 'keyboard gives the return key to the page only through that event, so Enter '
        + 'sends nothing on a phone without a hardware keyboard');
    }
    if (!/KeyDown="MessageTextBox_KeyDown"/.test(box)) {
      problems.push(`${CHAT_XAML}: the composer has no KeyDown - a hardware keyboard's `
        + 'return would not send either');
    }
    if (!/AcceptsReturn="False"/.test(box)) {
      problems.push(`${CHAT_XAML}: the composer does not say AcceptsReturn="False" - the `
        + 'return key could put a newline in a message the bubble draws as one line');
    }
  }

  if (!/private void SendOnReturn\s*\(/.test(composer)) {
    problems.push(`${CHAT_CODE}: no SendOnReturn - the two events the return key arrives `
      + 'with have no single send to share, so one of them silently does nothing');
  }

  // 6. E quello che la tastiera non fa lo fa l'app: un `:)` digitato parte come faccia.
  if (!/public static string Emoticons\s*\(/.test(rule)) {
    problems.push(`${RULE}: no Emoticons(string) - a keyboard without the emoji key leaves `
      + '":)" a colon and a bracket, and the person on the other side reads three characters '
      + 'where a face was meant');
  } else if (!/text\s*=\s*EmojiText\.Emoticons\s*\(/.test(composer)) {
    problems.push(`${CHAT_CODE}: the composer sends the text it was given, not the emoticons `
      + 'turned into faces - the table in EmojiText.Emoticons exists and nothing on the send '
      + 'path asks it, so a typed ":)" still leaves as a colon');
  }

  return problems;
}

/** Ogni file citato, letto dal progetto. */
function readSources() {
  const files = {};

  for (const file of [RULE, MESSAGE, CHAT_XAML, CHAT_CODE, VIEWER, GROUP, DATA]) {
    files[file] = fs.readFileSync(path.join(ROOT, file), 'utf8');
  }

  return files;
}

function main() {
  const problems = emojiProblems(readSources());

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} emoji problem(s).`);
    process.exit(1);
  }

  console.log('OK: the emoji this phone has no glyph for are replaced where the text is drawn, '
    + 'the message itself is untouched, and the composer asks the keyboard for its emoji key.');
}

if (require.main === module) main();

module.exports = { emojiProblems };
