#!/usr/bin/env node
/**
 * tools/check-client-state.js
 *
 * Guard per le frasi che una companion deve al server dopo il login, e per il
 * canale delle notifiche che le puo' rifiutare.
 *
 * Perche' esiste: sul telefono, il 17 ottobre 2026, ogni avvio faceva la stessa
 * cosa - login riuscito, due nodi `ib` (`native: Notification (ib) arrived, and
 * nothing reads it yet`), e la connessione persa circa due secondi dopo, quattro
 * run su quattro. Nessun test del protocollo puo' vederlo, perche' i due pezzi
 * mancanti vivono nell'app:
 *
 *  1. `ClientPayload.Login` chiede di essere portata su **passiva** (`passive` e
 *     `pull` a 1), e il riferimento completa quel login con `sendPassiveIq('active')`
 *     sul nodo `success` (`Socket/socket.js`, il gestore `CB:success`). Un client
 *     che si logga e poi tace resta la meta' passiva del proprio login;
 *  2. il canale `ib`/`notification` deve essere **letto**: il figlio e' cio' su cui
 *     il riferimento fa dispatch, e `downgrade_webclient` e' l'unico che rifiuta il
 *     client (`ws.on('CB:ib,,downgrade_webclient', ...)` chiude la connessione). Non
 *     leggerlo lascia `ib` indistinguibile da un bootstrap normale, e nella lista
 *     dei run c'e' solo la riga "nothing reads it yet";
 *  3. una lettura che fallisce deve dire **quanti e quali byte** erano in volo: sul
 *     telefono la fine della conversazione e' sempre la stessa forma (pochi byte,
 *     poi `0x80072EFE`), e senza quei byte il log di un run morto al bootstrap e'
 *     un login e un'eccezione;
 *  4. il login legge **fino a `success`**, non un nodo solo: il bootstrap parla su
 *     `ib` e puo' farlo prima della risposta, e il nodo letto dal login e' l'unico
 *     che nessun altro vedra' mai. Quelli arrivati prima vanno messi in coda e
 *     ridati al lettore (`_beforeLogin`);
 *  5. un messaggio si **conferma dopo** averlo letto: l'`ack` ferma i reinvii, e un
 *     ack su un'apertura fallita rende la perdita definitiva (e' cosi' che la sync
 *     della history poteva sparire per sempre). Il fallimento risponde con
 *     `IncomingMessage.RetryReceipt`;
 *  6. un pairing **dimentica** lo stato del protocollo prima di salvare il nuovo
 *     account: il MAC di Signal copre le due chiavi d'identita', quindi le sessioni
 *     scritte con quella vecchia non leggono piu' niente. Il run del 18 ottobre 2026
 *     e' esattamente questo - `the message's MAC does not verify` su ogni messaggio,
 *     dal telefono dello stesso account e da un contatto;
 *  7. il login **adotta** la signed pre-key dell'account: quelle chiavi partono da uno,
 *     quindi uno store rimasto dal pairing precedente ha lo stesso id su un'altra
 *     chiave privata, mentre il path responder puo' confrontare solo l'id. Il mittente
 *     usa la chiave pubblicata (quella nuova) e noi ne usiamo un'altra: stesso MAC rotto.
 *
 * Regole:
 *  1. `NativeConnection.cs` manda la passive iq `active` dopo il login
 *     (`AnnounceClientStateAsync`);
 *  2. `NativeConnection.cs` instrada `InboundKind.Notification` su
 *     `ReadNotificationAsync`, che nomina il figlio con
 *     `InboundRouter.NotificationType`;
 *  3. `NativeConnection.cs` riconosce il rifiuto con `ClientState.IsClientRefusal`;
 *  4. `WhatsAppSocket.cs` nomina i byte rimasti quando una lettura fallisce
 *     (`PendingHex`).
 *
 * Usage:
 *   node tools/check-client-state.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const CONNECTION = 'WhatsappApp/Services/NativeConnection.cs';
const SOCKET = 'WhatsappApp/Protocol/Client/WhatsAppSocket.cs';
const SESSION = 'WhatsappApp/Protocol/Client/NativeSession.cs';
const PAIRING = 'WhatsappApp/Pages/PairPage.xaml.cs';

/**
 * I problemi dei due file. Un array di frasi, vuoto quando non c'e' niente di
 * storto.
 */
function clientStateProblems(files) {
  const problems = [];
  const connection = String((files && files.connection) || '');
  const socket = String((files && files.socket) || '');
  const session = String((files && files.session) || '');
  const pairing = String((files && files.pairing) || '');

  if (!connection.includes('await AnnounceClientStateAsync(session);')) {
    problems.push(`${CONNECTION}: the login must be completed with the client-state sentences ` +
      '(`await AnnounceClientStateAsync(session);` right after the login): this client asks to ' +
      'be brought up passively, and one that says nothing afterwards is the passive half of its ' +
      'own login - the shape the phone shows as a logout two seconds after the login');
  }
  if (!connection.includes('ClientState.PassiveIq(ClientState.Active')) {
    problems.push(`${CONNECTION}: the client-state sentences must include the passive iq with ` +
      '`active` (`ClientState.PassiveIq(ClientState.Active, ...)`): it is the node the reference ' +
      'sends on `success`, and without it nothing tells the server this companion is here');
  }

  if (!connection.includes('await ReadNotificationAsync(session, node);')) {
    problems.push(`${CONNECTION}: an InboundKind.Notification must be read ` +
      '(`await ReadNotificationAsync(session, node);`), not only named: the child of that `ib` ' +
      'is what the reference dispatches on, and a client that drops it cannot tell a refusal ' +
      'from its own bootstrap');
  }
  if (!connection.includes('InboundRouter.NotificationType(node)')) {
    problems.push(`${CONNECTION}: the notification handler must name the child it read ` +
      '(`InboundRouter.NotificationType(node)`): the log line that only prints the parent tag ' +
      'is why the run of 2026-10-17 could not say which notification arrived');
  }
  if (!connection.includes('ClientState.IsClientRefusal(node)')) {
    problems.push(`${CONNECTION}: the notification handler must act on the client refusal ` +
      '(`ClientState.IsClientRefusal(node)`): the reference ends the connection on ' +
      '`downgrade_webclient`, and ignoring it leaves the app retrying a version problem as if ' +
      'it were a network one');
  }

  if (!session.includes('answer.Tag == "success"') || !session.includes('_beforeLogin.Enqueue(answer)')) {
    problems.push(`${SESSION}: the login must read up to \`success\` and queue everything that ` +
      'arrived before it (`answer.Tag == "success"` and `_beforeLogin.Enqueue(answer)`): the ' +
      'bootstrap travels on `ib` and can speak first, and a login that reads one node throws ' +
      'that frame away before any loop can answer it');
  }

  if (!connection.includes('await AdoptSignedPreKeyAsync(account);')) {
    problems.push(`${CONNECTION}: the login must make the store's signed pre-key the ` +
      'account\'s (`await AdoptSignedPreKeyAsync(account);`): a store left by a previous pairing ' +
      'holds the same id over another key, and the responder path can only compare the id - so ' +
      'every message of every session started against the published key fails its MAC ' +
      '(2026-10-18)');
  }

  const readAt = connection.indexOf('IncomingMessage.Read(node');
  const ackAt = connection.indexOf('IncomingMessage.Ack(node)');
  if (readAt < 0 || ackAt < 0 || ackAt < readAt) {
    problems.push(`${CONNECTION}: a message must be acknowledged only after it has been read ` +
      '(`IncomingMessage.Read(node` before `IncomingMessage.Ack(node)`): the ack is what stops ' +
      'the server re-sending, so acknowledging a stanza whose open threw loses it forever');
  }
  if (!connection.includes('IncomingMessage.RetryReceipt(node')) {
    problems.push(`${CONNECTION}: a message that did not read must be answered with a retry ` +
      '(`IncomingMessage.RetryReceipt(node, ...)`), which is the reference\'s own answer and ' +
      'the only thing that makes the sender encrypt it again');
  }

  const wipeAt = pairing.indexOf('FileSessionStorage.DeleteAllAsync()');
  const saveAt = pairing.indexOf('_store.Save(paired)');
  if (wipeAt < 0 || saveAt < 0 || saveAt < wipeAt) {
    problems.push(`${PAIRING}: a pairing must forget the previous identity's protocol state ` +
      '(`FileSessionStorage.DeleteAllAsync()` before `_store.Save(paired)`): every stored ' +
      'session was built with the old identity key, a Signal MAC covers both identities, and ' +
      'keeping them makes every message of every chat fail with "the message\'s MAC does not ' +
      'verify" - which is the run of 2026-10-18');
  }

  if (!socket.includes('_accumulator.PendingHex(')) {
    problems.push(`${SOCKET}: a read that fails must name the bytes of the frame it ` +
      'interrupted (`_accumulator.PendingHex(...)`): a connection that ends with a piece of a ' +
      'frame in hand is the shape every run on this phone has, and the bytes are the only ' +
      'evidence of where the server stopped talking');
  }

  return problems;
}

function main() {
  const problems = clientStateProblems({
    connection: fs.readFileSync(path.join(ROOT, CONNECTION), 'utf8'),
    socket: fs.readFileSync(path.join(ROOT, SOCKET), 'utf8'),
    session: fs.readFileSync(path.join(ROOT, SESSION), 'utf8'),
    pairing: fs.readFileSync(path.join(ROOT, PAIRING), 'utf8')
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} client-state problem(s).`);
    process.exit(1);
  }
  console.log('OK: the login is completed with the active iq, it reads up to `success` and keeps '
    + 'what came before it, the notification channel is read and its refusal acted on, a message '
    + 'is acknowledged only after it was read (a failure asks for a retry), a pairing forgets the '
    + 'previous identity\'s sessions before it stores the new account, the login makes the store\'s '
    + 'signed pre-key the account\'s, and a broken read names the bytes it interrupted.');
}

if (require.main === module) main();

module.exports = { clientStateProblems };
