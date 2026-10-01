#!/usr/bin/env node
/**
 * tools/check-memory.js
 *
 * Guard for the memory budget of a 512 MB phone.
 *
 * Perche' esiste: BitmapImage decodifica alla misura del file. Un'immagine del
 * profilo da 640x640 sono ~1,6 MB di pixel per disegnare un cerchio da 52 px, e
 * l'elenco chat ne tiene una per conversazione. Non c'e' nessuna eccezione e
 * nessun log: si vede solo un telefono che chiude l'app. Su WP8.1 non esiste una
 * dichiarazione di memoria nel manifest (lo schema non la prevede), quindi la
 * difesa e' questa: nessuna bitmap decodificata piu' grande di quanto viene
 * mostrata.
 *
 * Regole:
 *  1. ogni chiamata a ImageHelper.From*Async passa la misura di decodifica;
 *  2. nessuna misura di decodifica supera la larghezza dello schermo (720 copre
 *     un 480 px a 1,5x);
 *  3. ImageHelper imposta DecodePixelWidth prima di SetSourceAsync (dopo non ha
 *     effetto);
 *  4. la copia dell'elenco chat (ChatCache) non tiene i byte di un'immagine:
 *     quelli stanno nella cache degli avatar, che ha dei tetti ed e' letta
 *     prima che le righe salvate vengano applicate.
 *
 * Usage:
 *   node tools/check-memory.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { stripComments } = require('./csharp');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');
const HELPER = 'WhatsappApp/Services/ImageHelper.cs';

const CALL = /ImageHelper\.From(Base64|Bytes|File|Stream)Async\(/;
const MAX_DECODE = 720;

/** Il testo fra la parentesi aperta a `open` e la sua chiusa. */
function argumentsOf(source, open) {
  let depth = 0;
  for (let i = open; i < source.length; i++) {
    const ch = source[i];
    if (ch === '(') depth++;
    else if (ch === ')') {
      depth--;
      if (depth === 0) return source.slice(open + 1, i);
    }
  }
  return null;
}

/** Divide gli argomenti di una chiamata sulle virgole di primo livello. */
function splitArguments(text) {
  const parts = [];
  let depth = 0;
  let current = '';
  for (const ch of text) {
    if (ch === '(' || ch === '<') depth++;
    else if (ch === ')' || ch === '>') depth--;
    if (ch === ',' && depth === 0) {
      parts.push(current.trim());
      current = '';
      continue;
    }
    current += ch;
  }
  if (current.trim() !== '') parts.push(current.trim());
  return parts;
}

/** Problemi delle chiamate di decodifica in un sorgente. */
function decodeProblems(source, file) {
  const problems = [];
  // I commenti non sono codice: una chiamata commentata non decodifica niente,
  // e un commento non e' una chiamata.
  const code = stripComments(source);
  for (const match of code.matchAll(new RegExp(CALL.source, 'g'))) {
    const open = match.index + match[0].length - 1;
    const args = argumentsOf(code, open);
    if (args === null) continue;
    const parts = splitArguments(args);
    if (parts.length !== 2) {
      problems.push(`${file}: ImageHelper.From${match[1]}Async takes two arguments ` +
        '(the encoded image or stream and the width it is shown at): without the ' +
        'second one the bitmap is decoded at the size of the file');
      continue;
    }
    const width = parts[1];
    if (/^\d+$/.test(width) && Number(width) > MAX_DECODE) {
      problems.push(`${file}: decodes at ${width}, wider than ${MAX_DECODE}: the image is ` +
        'shown on a 480 px screen, so the extra pixels are only memory');
    }
  }
  return problems;
}

/** Problemi della forma di ImageHelper: l'ordine delle due chiamate conta. */
function sourceShapeProblems(source, file) {
  const problems = [];
  // Senza togliere i commenti, la frase che spiega l'ordine delle due chiamate
  // ("va impostato PRIMA di SetSourceAsync") verrebbe letta come l'ordine vero.
  const code = stripComments(source);
  if (code.indexOf('BitmapImage') < 0) return problems;
  if (code.indexOf('DecodePixelWidth') < 0) {
    problems.push(`${file}: creates a BitmapImage without a DecodePixelWidth: it will be ` +
      'decoded at the size of the file');
    return problems;
  }
  const decode = code.indexOf('DecodePixelWidth');
  const setSource = code.indexOf('SetSourceAsync');
  if (setSource >= 0 && decode > setSource) {
    problems.push(`${file}: DecodePixelWidth is set after SetSourceAsync, where it has no effect`);
  }
  return problems;
}

const ATTACHMENT_INBOX = 'WhatsappApp/Services/AttachmentInbox.cs';

/**
 * Problemi di come entra un file scelto o condiviso.
 *
 * Perche' esiste: l'inbox leggeva il file intero in un byte[] e poi lo
 * convertiva in base64. Una foto piccola passa, un video no: due allocazioni da
 * decine di MB su un telefono da 512 MB fanno chiudere l'app, e la condivisione
 * sembra un crash. Il file si copia nella cartella dell'app e si legge a pezzi
 * quando si spedisce (vedi ChatPage.SendAttachmentAsync).
 */
function attachmentProblems(source, file) {
  const problems = [];
  if (file !== ATTACHMENT_INBOX) return problems;
  const code = stripComments(source);
  if (!/\.CopyAsync\s*\(/.test(code)) {
    problems.push(`${file}: a picked or shared file must be copied into the app folder ` +
      'with StorageFile.CopyAsync; reading it into a byte array holds the whole file ' +
      'in memory and takes the app down on a 512 MB phone');
  }
  if (/new\s+byte\s*\[\s*\(?(uint|int)\)?\s*\w+\.Size/.test(code)) {
    problems.push(`${file}: reads a whole file into a byte array instead of copying it`);
  }
  return problems;
}

const CHAT_CACHE = 'WhatsappApp/Services/ChatCache.cs';
const AVATAR_CACHE = 'WhatsappApp/Services/AvatarCache.cs';
const DATA_SERVICE = 'WhatsappApp/Services/DataService.cs';

/**
 * Problemi delle due cache dell'elenco chat.
 *
 * Perche' esiste: le due cache si dividono lo stesso lavoro e si difendono a
 * vicenda. ChatCache e' il file che l'app legge prima che la connessione
 * esista, e deve restare piccolo: i byte di un'immagine non sono suoi, e da
 * quando c'e' AvatarCache non c'e' piu' nessuna ragione per rimetterli li'.
 * I byte stanno in AvatarCache, che per questo ha bisogno di tetti (una cache
 * senza tetto e' una crescita lenta che nessuno vede) e della stessa coda di
 * scrittura delle preferenze (due scritture sullo stesso file, lanciate senza
 * aspettarsi, finiscono fuori ordine - e' il difetto che ChatPreferences ha
 * gia' avuto).
 */
function avatarCacheProblems(source, file) {
  const problems = [];
  const code = stripComments(source);

  if (file === CHAT_CACHE) {
    if (/AvatarData\s*=/.test(code)) {
      problems.push(`${file}: the row cache saves AvatarData into chats.json: that file ` +
        'is read before the connection exists, holds one row per chat, and the pictures ' +
        'have a bounded cache of their own (AvatarCache, avatar-cache.json)');
    }
    return problems;
  }

  if (file !== AVATAR_CACHE) return problems;

  if (!/MaxChats\s*=\s*\d+/.test(code) || !/MaxTotalChars\s*=\s*\d+/.test(code)) {
    problems.push(`${file}: no MaxChats/MaxTotalChars cap: the avatar cache would grow ` +
      'with the account, one picture per conversation, and nothing would ever drop one');
  }
  if (!/Writes\.RunAsync\s*\(/.test(code)) {
    problems.push(`${file}: writes the file without the serial queue: two pictures ` +
      'arriving together start two writes on the same file, and the older snapshot can ' +
      'win (this is the bug ChatPreferences had)');
  }
  return problems;
}

/**
 * Problemi dell'ordine con cui si carica la copia locale.
 *
 * Perche' esiste: la copia locale non ha i byte delle immagini (ChatCache li
 * lascia fuori), quindi ApplyChat li chiede ad AvatarCache. Se la cache non e'
 * ancora stata letta risponde di no a tutte, e un riavvio mostra le iniziali
 * finche' l'adapter non rimanda ogni riga - che e' esattamente il difetto che
 * questa cache esiste per togliere. Il file viene letto dal disco una volta
 * sola, e ci vuole un await: l'ordine e' il contenuto di questa regola.
 */
function cachedRowsProblems(source, file) {
  const problems = [];
  if (file !== DATA_SERVICE) return problems;

  const code = stripComments(source);
  const start = code.indexOf('LoadCachedChatsAsync');
  if (start < 0) {
    problems.push(`${file}: LoadCachedChatsAsync is gone (did the cached rows move? ` +
      'then this guard must move too)');
    return problems;
  }

  const body = code.slice(start, start + 1500);
  const load = body.indexOf('AvatarCache.LoadAsync');
  const apply = body.indexOf('ApplyChat(');

  if (load < 0) {
    problems.push(`${file}: LoadCachedChatsAsync does not await AvatarCache.LoadAsync: ` +
      'the cached rows are applied with the pictures still unread, so a restart shows ' +
      'initials until the adapter answers');
    return problems;
  }
  if (apply >= 0 && load > apply) {
    problems.push(`${file}: AvatarCache.LoadAsync comes after the first ApplyChat: the ` +
      'rows are applied with the pictures still unread');
  }
  return problems;
}

function walk(dir, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'obj' || entry.name === 'bin') continue;
      walk(path.join(dir, entry.name), out);
    } else if (entry.name.endsWith('.cs')) {
      out.push(path.join(dir, entry.name));
    }
  }
  return out;
}

function main() {
  const problems = [];
  for (const file of walk(APP, [])) {
    const rel = path.relative(ROOT, file).replace(/\\/g, '/');
    const source = fs.readFileSync(file, 'utf8');
    problems.push(...decodeProblems(source, rel));
    problems.push(...attachmentProblems(source, rel));
    problems.push(...avatarCacheProblems(source, rel));
    problems.push(...cachedRowsProblems(source, rel));
    if (rel === HELPER) problems.push(...sourceShapeProblems(source, rel));
  }

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} memory problem(s).`);
    process.exit(1);
  }
  console.log('OK: every decoded bitmap asks for the width it is shown at, the row cache ' +
    'stays slim, and the pictures have a bounded cache read before the rows.');
}

if (require.main === module) main();

module.exports = {
  decodeProblems,
  attachmentProblems,
  avatarCacheProblems,
  cachedRowsProblems,
  sourceShapeProblems,
  splitArguments,
  MAX_DECODE
};
