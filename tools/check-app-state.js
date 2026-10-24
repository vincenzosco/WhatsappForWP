#!/usr/bin/env node
/**
 * tools/check-app-state.js
 *
 * Guard for the app state: the names saved in the primary phone's address book,
 * and the cables that carry them to this screen.
 *
 * Perche' esiste: un dispositivo collegato non ha un registro dei contatti, e la
 * history sync porta solo i push name di chi scrive. I nomi che l'utente ha
 * salvato (`Mamma`, `Papa'`) viaggiano nell'app state, come azioni `contact`, e
 * l'unico modo per leggerli e' la chiave che il telefono condivide al momento del
 * link (`APP_STATE_SYNC_KEY_SHARE`). Ogni pezzo di questo filo e' in una riga che
 * nessun test del protocollo puo' vedere, perche' vive nell'app:
 *
 *  1. `NativeConnection.cs` legge la condivisione **prima** della regola che
 *     scarta le copie di questo account. La condivisione e' una copia nostra (la
 *     scrive il telefono), quindi applicare quella regola prima significa buttare
 *     via l'unico modo di leggere il proprio registro;
 *  2. le chiavi si salvano con l'account (`_store.Save`): il telefono le condivide
 *     **una volta**, e una chiave tenuta solo in memoria rende il registro
 *     leggibile per un avvio solo;
 *  3. al login si rileggono dal disco (`RememberStoredAppStateKeys`), o la chiave
 *     salvata non serve a niente;
 *  4. la sync si chiede sia dopo il login sia dopo la condivisione: un client che
 *     chiede solo al login aspetta un riavvio per mostrare i nomi, e uno che
 *     chiede solo dopo la condivisione non chiede niente al primo avvio dopo un
 *     re-link;
 *  5. una collezione che non verifica **non** porta via le altre: la sync sono
 *     cinque collezioni, e un catch che le chiude tutte lascia il registro vuoto
 *     per una collezione che questo telefono non sa leggere;
 *  6. i nomi si scrivono nel libro sotto tutti e tre gli indirizzi che l'azione
 *     nomina (l'indice, il numero e il `@lid`), e la lista si ridisegna
 *     (`AnnounceRows`): le righe sono gia' a schermo, un nome che non le tocca non
 *     si vede;
 *  7. `AppStateSync.cs` verifica la snapshot MAC e la patch MAC, e applica **solo**
 *     i contatti: la stessa collezione porta pin, mute e archivio, che sono di
 *     questo telefono (`ChatPreferences`) e che una snapshot non deve riscrivere.
 *
 * Usage:
 *   node tools/check-app-state.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const CONNECTION = 'WhatsappApp/Services/NativeConnection.cs';
const APP_STATE = 'WhatsappApp/Protocol/Client/AppStateSync.cs';

const LEARN_CALL = 'LearnAppStateKeys(session, incoming, account);';
const ECHO_RETURN = 'if (incoming.IsOurEcho) return;';
const STORED_CALL = 'RememberStoredAppStateKeys(account);';
const SYNC_CALL = 'Guarded.RunGuardedAsync("NativeConnection/app state", SyncAppStateAsync(session));';
const LEARN = 'private bool LearnAppStateKeys(';
const SYNC = 'private async Task SyncAppStateAsync(';
const REMEMBER = 'private static bool RememberSavedContact(';

const SNAPSHOT_MAC = 'if (!Hmac.FixedTimeEquals(expected, snapshot.Mac))';
const PATCH_MAC = 'if (!Hmac.FixedTimeEquals(expectedPatchMac, patch.PatchMac))';
const CONTACT_ONLY = 'if (IndexType(action.Index) == ContactIndex) ReadContact(action, result);';

/** Il testo senza commenti: questa guardia legge il codice, la prosa racconta il perche'. */
function stripComments(text) {
  return String(text || '')
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/\/\/[^\n]*/g, '');
}

/**
 * Il corpo di un metodo: dalla firma fino alla riga che apre il membro dopo. Serve
 * a guardare l'ordine delle righe di un metodo e non di tutto il file.
 */
function methodBody(text, signature) {
  const start = text.indexOf(signature);
  if (start < 0) return '';

  const rest = text.slice(start + signature.length);
  const next = rest.search(/\r?\n        (?:private|public|internal|protected) /);

  return next < 0 ? rest : rest.slice(0, next);
}

function count(text, phrase) {
  let seen = 0;
  let at = text.indexOf(phrase);
  while (at >= 0) {
    seen++;
    at = text.indexOf(phrase, at + phrase.length);
  }
  return seen;
}

/**
 * I problemi del filo. Un array di frasi: vuoto quando le chiavi arrivano, si
 * salvano, si rileggono, la sync si chiede, e i nomi si scrivono sotto tutti gli
 * indirizzi e si ridisegnano.
 */
function appStateProblems(files) {
  const problems = [];
  const connection = stripComments(String((files && files.connection) || ''));
  const appState = stripComments(String((files && files.appState) || ''));

  const learnAt = connection.indexOf(LEARN_CALL);
  const echoAt = connection.indexOf(ECHO_RETURN);

  if (learnAt < 0) {
    problems.push(`${CONNECTION}: nothing calls ${LEARN_CALL}: the key the primary shares with ` +
      'a device it has just linked is read by nobody, and the address book stays unreadable');
  } else if (echoAt >= 0 && learnAt > echoAt) {
    problems.push(`${CONNECTION}: the key share is read after \`${ECHO_RETURN}\`: the share is ` +
      'one of this account\'s own copies (the phone writes it), so that rule drops the only ' +
      'way this device has to read its own app state');
  }

  const learnBody = methodBody(connection, LEARN);
  if (learnAt >= 0 && learnBody.indexOf('_store.Save(account)') < 0) {
    problems.push(`${CONNECTION}: a learned key is not written with the account ` +
      '(`_store.Save(account)`): the primary shares the keys once, so a key held in memory only ' +
      'reads the address book for one run and never again');
  }

  if (connection.indexOf(STORED_CALL) < 0) {
    problems.push(`${CONNECTION}: the login does not call ${STORED_CALL}: the keys on disk are ` +
      'never read back, so the share of the last run is worth nothing');
  }

  const syncs = count(connection, SYNC_CALL);
  if (syncs < 2) {
    problems.push(`${CONNECTION}: ${SYNC_CALL} appears ${syncs} time(s) and must appear twice - ` +
      'after the login (the keys of a previous run) and after a share (the first run after a ' +
      're-link): with one of the two, the names of the primary\'s address book wait for a restart');
  }

  const syncBody = methodBody(connection, SYNC);
  if (syncBody.indexOf('AppStateSync.Query(') < 0) {
    problems.push(`${CONNECTION}: the sync asks for nothing ("AppStateSync.Query("): the ` +
      'collections have to be requested from the server before they can be read');
  }
  if (!/catch \(ProtocolException[\s\S]*?continue;/.test(syncBody)) {
    problems.push(`${CONNECTION}: a collection that does not verify is not skipped ` +
      '(`catch (ProtocolException ...)` then `continue`): the sync is five collections, and one ' +
      'this device cannot read must not take the names of the others with it');
  }
  if (syncBody.indexOf('RememberSavedContact(') < 0) {
    problems.push(`${CONNECTION}: the names the app state carries go nowhere ` +
      '(`RememberSavedContact`): the sync verifies and is then thrown away');
  }

  const rememberBody = methodBody(connection, REMEMBER);
  const addresses = count(rememberBody, 'NameCache.RememberSavedName(');
  if (rememberBody.length === 0) {
    problems.push(`${CONNECTION}: no ${REMEMBER}): there is nothing that writes a saved name ` +
      'into the book the rows are drawn from');
  } else if (addresses < 3) {
    problems.push(`${CONNECTION}: a saved name is written under ${addresses} address(es) and not ` +
      'three: the same person is the index\'s JID, a phone number and a `@lid`, and a row keyed on ' +
      'whichever the server used that day has to find the name');
  }
  if (rememberBody.indexOf('NameCache.RememberMapping(') < 0) {
    problems.push(`${CONNECTION}: the LID/phone pair of a saved contact is not remembered ` +
      '(`NameCache.RememberMapping`): a name learned for one of the two is then the name of ' +
      'nobody, because nothing says they are the same person');
  }
  if (learnAt >= 0 && syncBody.indexOf('AnnounceRows();') < 0) {
    problems.push(`${CONNECTION}: the rows are not told about the names (AnnounceRows()): the ` +
      'list is already on screen, and a name that does not touch it is a name nobody sees');
  }

  if (appState.indexOf(SNAPSHOT_MAC) < 0) {
    problems.push(`${APP_STATE}: the snapshot's LT-Hash is not checked against its MAC: an ` +
      'unchecked MAC is a client that believes whatever the server says about who it knows');
  }
  if (appState.indexOf(PATCH_MAC) < 0) {
    problems.push(`${APP_STATE}: the patch's MAC is not verified over its value MACs: the changes ` +
      'after a snapshot would be applied as if they came from this account');
  }
  if (appState.indexOf(CONTACT_ONLY) < 0) {
    problems.push(`${APP_STATE}: the reader applies more than the saved names (${CONTACT_ONLY}): ` +
      'the same collection carries pins, mutes and the archive setting, and those are this ' +
      'phone\'s own - a snapshot that set them would undo what a person did here');
  }

  return problems;
}

function main() {
  const read = (file) => fs.readFileSync(path.join(ROOT, file), 'utf8');
  const problems = appStateProblems({
    connection: read(CONNECTION),
    appState: read(APP_STATE)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} app-state problem(s).`);
    process.exit(1);
  }

  console.log('OK: the keys the primary shares are kept with the account, the sync is asked for ' +
    'after the login and after a share, and the saved names reach the list.');
}

if (require.main === module) main();

module.exports = {
  appStateProblems,
  stripComments,
  methodBody,
  CONNECTION,
  APP_STATE,
  LEARN_CALL,
  ECHO_RETURN,
  STORED_CALL,
  SYNC_CALL,
  LEARN,
  SYNC,
  REMEMBER,
  SNAPSHOT_MAC,
  PATCH_MAC,
  CONTACT_ONLY
};
