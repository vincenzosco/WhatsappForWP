#!/usr/bin/env node
/**
 * tools/check-attachment-file.js
 *
 * Guard per il file di un allegato in uscita e per il lettore che lo riapre.
 *
 * Perche' esiste: ogni allegato in uscita veniva copiato con lo stesso nome,
 * `outgoing_attachment`, quindi ogni vocale registrato sostituiva il file del
 * vocale precedente. Le bolle puntavano tutte allo stesso file, e il lettore
 * della pagina si fidava del solo nome: `if (_voiceLoadedFile != MediaFilePath)`
 * non ricaricava niente quando il nome non cambiava, perche' non cambiava mai.
 * Il glifo diventava "in riproduzione" e non si sentiva nulla. Due fatti
 * strutturali che nessun altro guard poteva vedere: un nome di copia unico per
 * allegato, e un lettore che decide sulla bolla e non solo sul nome.
 *
 * Regole:
 *  1. in AttachmentInbox.cs la copia si chiama NextCopyName(...) e il nome
 *     condiviso `"outgoing_attachment"` non compare;
 *  2. in ChatPage.xaml.cs ToggleVoice confronta la bolla prima del nome
 *     (`bool anotherMessage = _voiceMessage != message`).
 *
 * Usage:
 *   node tools/check-attachment-file.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const INBOX = 'WhatsappApp/Services/AttachmentInbox.cs';
const CHAT = 'WhatsappApp/Pages/ChatPage.xaml.cs';

/**
 * I problemi del file di un allegato in uscita e del suo lettore.
 * Restituisce un array di frasi, vuoto quando non c'e' niente di storto.
 */
function attachmentFileProblems(source, file) {
  const problems = [];
  const text = String(source || '');

  if (file === INBOX) {
    if (!/NextCopyName\s*\(/.test(text)) {
      problems.push(`${file}: the copied attachment must be named by NextCopyName(...): ` +
        'a fixed name makes every recording replace the one before it, and every ' +
        'bubble that points at that name plays the last file');
    }
    if (/"outgoing_attachment"(?!_)/.test(text)) {
      problems.push(`${file}: the copy target is the shared name "outgoing_attachment": ` +
        'the next attachment replaces this one under every bubble that used it');
    }
  }

  if (file === CHAT) {
    if (!/bool\s+anotherMessage\s*=\s*_voiceMessage\s*!=\s*message/.test(text)) {
      problems.push(`${file}: ToggleVoice must decide on the bubble as well as the ` +
        'file name (bool anotherMessage = _voiceMessage != message): a replaced file ' +
        'keeps the same name, so a name-only check never reloads the player');
    }
  }

  return problems;
}

function main() {
  const problems = [];
  const files = [
    { rel: INBOX, file: path.join(ROOT, INBOX) },
    { rel: CHAT, file: path.join(ROOT, CHAT) }
  ];

  for (const entry of files) {
    const source = fs.readFileSync(entry.file, 'utf8');
    problems.push(...attachmentFileProblems(source, entry.rel));
  }

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} attachment problem(s).`);
    process.exit(1);
  }
  console.log('OK: the attachment copy name is unique and the player loads the bubble it '
    + 'was asked for.');
}

if (require.main === module) main();

module.exports = { attachmentFileProblems };
