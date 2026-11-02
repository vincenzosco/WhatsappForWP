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
 *     (`bool anotherMessage = _voiceMessage != message`);
 *  3. in ChatPage.xaml.cs Downloadable non rifiuta ogni messaggio in uscita:
 *     un vocale mandato da un altro dispositivo arriva come riga di cronologia
 *     in uscita, senza byte qui, e il tocco deve poterlo chiedere
 *     (`!message.IsIncoming && !message.IsHistory`).
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
 * Le due tabelle di AttachmentInbox.cs: quella che classifica un'estensione
 * (KindName) e quella che le da' un MIME (MimeFor).
 *
 * Perche' esiste: `.jpg` e `.jpeg` non erano in MimeFor, quindi ogni foto della
 * galleria usciva come `application/octet-stream` in un messaggio che diceva di
 * essere un'immagine. KindName la classificava lo stesso, dal nome del file, e
 * quindi nulla in questo repository se ne accorgeva: la bolla locale la foto la
 * mostrava, e i telefoni che la ricevevano non la disegnavano e non la
 * confermavano. Un'estensione che l'app sa classificare e che la tabella dei MIME
 * non conosce e' un file che parte con il tipo sbagliato.
 */
function untypedExtensions(text, file) {
  const problems = [];
  const mimeBody = between(text, 'string MimeFor(string extension)', 'string ExtensionFor(');
  const kindBody = between(text, 'string KindName(string mimeType, string fileName)', null);

  const table = {};
  const entry = /if \(value == "(\.[a-z0-9]+)"\) return "([^"]+)";/g;
  let match;

  while ((match = entry.exec(mimeBody))) table[match[1]] = match[2];

  const seen = {};
  const extension = /name\.EndsWith\("(\.[a-z0-9]+)"\)/g;

  while ((match = extension.exec(kindBody))) {
    const ext = match[1];
    if (seen[ext]) continue;
    seen[ext] = true;

    // The kind of this extension is the first `return "..."` after it: KindName's
    // branches are lists of EndsWith followed by their answer.
    const answer = kindBody.slice(match.index).match(/return "(image|video|audio|document)"/);
    const kind = answer ? answer[1] : 'document';
    const mime = table[ext];

    if (!mime) {
      problems.push(`${file}: KindName knows ${ext} and MimeFor does not, so a file with ` +
        'that extension leaves as application/octet-stream: the receiving phones take the ' +
        'type from the message and do not draw it, and no device acknowledges it');
      continue;
    }

    const family = mime.indexOf('image/') === 0 ? 'image'
      : mime.indexOf('video/') === 0 ? 'video'
      : mime.indexOf('audio/') === 0 ? 'audio'
      : 'document';

    if (family !== kind) {
      problems.push(`${file}: ${ext} is a ${kind} to KindName and "${mime}" to MimeFor: ` +
        'the message says one thing and the bubble another');
    }
  }

  return problems;
}

/** The text between two signatures, or to the end of the file when there is no second. */
function between(text, from, to) {
  const at = text.indexOf(from);
  if (at < 0) return '';

  const end = to ? text.indexOf(to, at + from.length) : -1;

  return text.slice(at, end < 0 ? text.length : end);
}

/**
 * I problemi del file di un allegato in uscita e del suo lettore.
 * Restituisce un array di frasi, vuoto quando non c'e' niente di storto.
 */
function attachmentFileProblems(source, file) {
  const problems = [];
  const text = String(source || '');

  if (file === INBOX) {
    // The declaration `private static string NextCopyName(string extension)` matched the
    // rule on its own: a fixed name at both call sites still printed OK. What has to be
    // there is a call.
    const calls = text.replace(/private\s+static\s+string\s+NextCopyName\s*\([^)]*\)/g, '');

    if (!/NextCopyName\s*\(/.test(calls)) {
      problems.push(`${file}: the copied attachment must be named by NextCopyName(...): ` +
        'a fixed name makes every recording replace the one before it, and every ' +
        'bubble that points at that name plays the last file');
    }
    if (/"outgoing_attachment"(?!_)/.test(text)) {
      problems.push(`${file}: the copy target is the shared name "outgoing_attachment": ` +
        'the next attachment replaces this one under every bubble that used it');
    }

    problems.push(...untypedExtensions(text, file));
  }

  if (file === CHAT) {
    if (!/bool\s+anotherMessage\s*=\s*_voiceMessage\s*!=\s*message/.test(text)) {
      problems.push(`${file}: ToggleVoice must decide on the bubble as well as the ` +
        'file name (bool anotherMessage = _voiceMessage != message): a replaced file ' +
        'keeps the same name, so a name-only check never reloads the player');
    }
    if (!/!\s*message\.IsIncoming\s*&&\s*!\s*message\.IsHistory/.test(text)) {
      problems.push(`${file}: Downloadable must not refuse every outgoing message ` +
        '(!message.IsIncoming && !message.IsHistory): a voice note sent from another ' +
        'device arrives as an outgoing history row with no bytes here, and a tap that ' +
        'cannot request it does nothing at all');
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
  console.log('OK: the attachment copy name is unique, the player loads the bubble it '
    + 'was asked for, and an outgoing history row can still be requested.');
}

if (require.main === module) main();

module.exports = { attachmentFileProblems, untypedExtensions };
