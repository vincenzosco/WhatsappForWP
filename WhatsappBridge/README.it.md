# Adapter GOWA

[English](README.md) | **Italiano**

Ponte tra l'app WhatsApp per Windows Phone 8.1 e un server GOWA self-hosted
([go-whatsapp-web-multidevice](https://github.com/vincenzosco/go-whatsapp-web-multidevice)).

## Come funziona

```
 App WP8  ⇄  (TCP cifrato AES-256-CBC + HMAC)  ⇄  Adapter  ⇄  (HTTP REST + webhook)  ⇄  GOWA  ⇄  WhatsApp
```

- Il login (QR code o codice di abbinamento) e' richiesto **dall'app** tramite frame di
  controllo; l'app mostra il codice a tutto schermo e tiene lo schermo acceso finche'
  resta visibile.
- I messaggi in arrivo da WhatsApp arrivano via webhook e vengono inoltrati all'app sul
  canale TCP.
- I messaggi in uscita sono inviati alle API REST di GOWA.
- L'adapter si lega al device GOWA che e' **collegato**, non al primo di `/devices`:
  la lista e' in ordine di creazione, quindi un server con piu' device finirebbe per
  dichiarare `disconnected` un account che invece e' collegato. La sessione WhatsApp
  vive nel volume `/data`, quindi sopravvive a un riavvio e a una ricostruzione del
  container. Quando non c'e' piu', l'app chiede da sola `login.qr` e il QR si scansiona
  di nuovo dal telefono.
- L'adapter si annuncia sulla rete locale con un beacon di scoperta, cosi' l'app lo trova
  senza che le venga configurato un indirizzo.
- `message.revoked` e `message.edited` vengono inoltrati all'app come frame di controllo
  `revoked` ed `edited`, cosi' un messaggio cancellato o modificato dal telefono non
  resta congelato nell'app. `message.reaction` resta ignorato: non c'e' dove disegnarlo.
- Aprendo una chat l'app ne chiede i messaggi gia' in memoria al server (`messages`),
  e l'adapter risponde con frame di messaggio normali marcati `IsHistory`. L'app li
  inserisce in ordine di data e li tiene fuori dal conteggio dei non letti e dagli
  avvisi: non stanno arrivando adesso. Un messaggio il cui media non e' fra i byte che
  il webhook ha consegnato viene mandato come testo - `[Image]`, `[Video]`, ... -
  perche' un fumetto vuoto e' peggio di una parola. Un vocale fa eccezione: la sua
  parola e' `Audio`, senza parentesi, perche' l'adapter ne sa misurare la durata.
  GOWA non manda la durata da nessuna parte, quindi l'anteprima della riga scarica i
  byte dell'ultimo messaggio (la stessa rotta `/message/:id/download` che usa l'app) e
  legge la lunghezza dal contenitore - Ogg/Opus, MP4/M4A, MP3 - senza nuove dipendenze,
  poi la tiene per id di messaggio. La riga legge `Audio 0:10`; quando i byte non
  arrivano resta `Audio`.
- Ogni riga dell'elenco porta quanti messaggi non ha ancora letto (`UnreadCount`).
  Quel conteggio lo tiene l'adapter, perche' l'elenco chat di GOWA non ha questo campo e
  perche' un messaggio che arriva col telefono spento raggiunge il webhook dell'adapter e
  nessun altro; l'app lo azzera con `read` quando la conversazione viene mostrata.Il conteggio vive in memoria: riavviare l'adapter lo riparte da zero.
  I JID dei canali (`...@newsletter`) si saltano qui e nel webhook: un canale non e'
  una conversazione e non si puo' rispondere.
- Una foto, un video o un file si manda a pezzi (`media.begin` / `media.chunk` /
  `media.end`): il tetto di un frame e' 8 MiB e il base64 aggiunge un terzo, quindi un
  video non ci sta in un frame solo. Ogni pezzo e' un multiplo di 4 caratteri base64,
  cosi' l'adapter concatena i byte decodificati senza ricodificare niente. La porta di
  GOWA la decide il tipo MIME (o l'estensione): `/send/image`, `/send/video` e
  `/send/file` sono tre rotte diverse, e prima di questo un video partiva come immagine.
  Un documento e' il quarto caso e ha gia' la sua rotta: il picker sul telefono adesso
  offre PDF e file di ufficio, l'app dichiara il nome del file e il tipo MIME vero, e
  l'adapter lo manda con `/send/file`. I vocali si ascoltano dentro il loro fumetto: il
  telefono chiede i byte all'adapter, l'adapter risponde con un MP3 (vedi sotto), e l'app
  disegna play/pausa e una barra senza uscire dalla conversazione.
- Un video viene rimpicciolito prima di viaggiare. Lo fa prima il telefono, mentre il
  fumetto dice "invio"; quando non ha potuto (nessun transcoder per quel file, niente
  spazio, la piattaforma che rifiuta) lo fa l'adapter con ffmpeg prima di passare il
  video a GOWA, quindi a WhatsApp non arriva mai tutto il file della fotocamera.
  L'adapter lascia stare un video sotto i 4 MB - la conversione costerebbe piu' di quanto
  risparmia - e una conversione venuta piu' grande viene buttata. Con `FFMPEG_ENABLED=off`
  l'adapter non lo tocca; il telefono rimpicciolisce comunque i suoi video.
- Gli indicatori di scrittura adesso ci sono, nelle due direzioni, ed e' l'unico pezzo di
  presenza che questo progetto mostra. Chi comincia o smette di scrivere arriva come
  evento webhook `chat_presence` (GOWA 9.5), che l'adapter gira all'app come frame di
  controllo `typing`; l'app lo disegna come un fumetto con tre puntini e lo toglie al
  `paused`. Mentre scrivi tu, l'app manda `typing` nel verso opposto e l'adapter lo passa
  a `/send/chat-presence`. Non si inventa niente: lo stato sullo schermo arriva da
  WhatsApp, e il fumetto sparisce da solo quando arriva il messaggio che aspettava.
- L'account e' online esattamente mentre un telefono lo guarda. WhatsApp manda le
  notifiche di scrittura solo a un client marcato online, e GOWA si collega come
  `unavailable` con un impulso di cinque minuti una volta al giorno: per questo l'adapter
  mette l'account `available` quando si collega il primo client dell'app e `unavailable`
  quando esce l'ultimo (`POST /send/presence`). E' anche quello che vedono i contatti:
  online mentre usi l'app, offline quando non la usi. Andare in background conta come
  uscire: WP8.1 congela il processo senza chiudere il socket, quindi l'app manda un frame
  di controllo `presence` - `paused` quando si sospende, `active` quando torna - e
  l'adapter smette di contare quel telefono finche' non ritorna. E' quello che trasforma
  l'account in un orario di ultimo accesso invece di lasciarlo online per un telefono
  congelato.
- Una foto o un video nella cronologia di una chat e' arrivato col telefono spento: i suoi
  byte sono stati consegnati all'adapter e a nessun altro, quindi la riga e' una parola
  (`[Image]`). Toccarla lo chiede all'adapter (`media.get`), che legge
  `GET /message/:id/download` da GOWA e risponde con i byte in frame `media`; l'app li
  mette sul messaggio che ha gia'. Non si scarica niente finche' non viene chiesto.
- Quei byte tornano a pezzi anche loro, un frame `media` ogni `MEDIA_CHUNK_CHARS`
  (700000) caratteri base64, ognuno con `MediaChunkIndex`, `MediaChunkTotal` e
  `MediaType`. Un video e' molto piu' grande di un frame, e il telefono lo ricompone: un
  video in un file locale, una foto in memoria. Un messaggio in arrivo il cui media il
  webhook ha gia' consegnato segue la stessa strada: il frame del messaggio lo annuncia
  (con `MediaType`), poi i frame `media` lo portano.

## Protocollo di controllo

Frame `Type = System`, `ChatId = "system"`.

| Direzione | `Command` | Campi usati |
|---|---|---|
| app -> adapter | `hello` | `Text` = nome utente |
| app -> adapter | `status` | — |
| app -> adapter | `login.qr` | — |
| app -> adapter | `login.code` | `Text` = numero con prefisso |
| app -> adapter | `pair.code` | — (l'app chiede il codice di accoppiamento; risponde `pair.info`) |
| app -> adapter | `pair` | `PairingPayload` = la chiave sigillata con il codice, `SenderId` = device id |
| app -> adapter | `contacts` | — |
| app -> adapter | `logout` | — |
| app -> adapter | `calls` | — (solo in entrata, dalle `CALLS_CHAT_LIMIT` chat più recenti) |
| app -> adapter | `chats` | — (le conversazioni dell'account collegato, dalla più recente) |
| app -> adapter | `messages` | `Text` = JID della chat (fino a `MESSAGES_LIMIT` messaggi, come frame normali marcati `IsHistory`) |
| app -> adapter | `read` | `Text` = JID della chat (quella conversazione e' stata letta; azzera il suo conteggio dei non letti) |
| app -> adapter | `media.begin` | `Text` = JID della chat, `MediaTransferId`, `MediaFileName`, `MediaMimeType`, `MediaChunkTotal` (segue un allegato) |
| app -> adapter | `media.chunk` | `MediaTransferId`, `MediaChunkIndex`, `MediaData` = un pezzo in base64 (multiplo di 4 caratteri) |
| app -> adapter | `media.end` | `MediaTransferId`, `Text` = didascalia (ricompone e spedisce) |
| app -> adapter | `media.get` | `Text` = JID della chat, `RelatedMessageId` = id del messaggio (scarica il media di quel messaggio e risponde con un frame `media` per pezzo) |
| app -> adapter | `contact.info` | `Text` = JID della chat (l'adapter mette insieme nome, about, immagine, profilo aziendale e, per un gruppo, descrizione e membri in un solo `Text` JSON) |
| app -> adapter | `typing` | `Text` = JID della chat, `State` = `composing` o `paused` (quello che vede il contatto mentre scrivi) |
| app -> adapter | `diag` | `Text` = il report del telefono, una riga per a-capo (l'adapter scrive ogni riga nel suo log come `[DIAG] ...`, cosi' il lato app di una esecuzione si legge dal container) |
| adapter -> app | `state` | `State`, `AccountJid` |
| adapter -> app | `qr` | `QrImageData` (base64 PNG), `QrDuration` |
| adapter -> app | `paircode` | `PairCode` |
| adapter -> app | `pair.info` | `PairingCode`, `PairingSeconds` (il codice di accoppiamento del bridge e per quanto resta valido) |
| adapter -> app | `paired` | `Token` (derivato dal device id) |
| adapter -> app | `contact` | `ChatId` = JID, `SenderName` = nome |
| adapter -> app | `call` | `ChatId`, `SenderName`, `Timestamp`, `CallId`, `CallReason`, `CallDurationSeconds`, `CallIsVideo` |
| adapter -> app | `calls.done` | — (la scansione è finita, anche senza chiamate) |
| adapter -> app | `chat` | `ChatId`, `SenderName`, `Text` = ultimo messaggio, `Timestamp`, `IsGroup`, `AvatarData` (base64), `UnreadCount` |
| adapter -> app | `chats.done` | — (l'elenco è finito) |
| adapter -> app | `revoked` | `ChatId`, `RelatedMessageId` = id del messaggio cancellato |
| adapter -> app | `edited` | `ChatId`, `RelatedMessageId`, `Text` = il nuovo testo |
| adapter -> app | `typing` | `ChatId`, `State` = `composing` o `paused` (qualcuno sta scrivendo in quella chat) |
| adapter -> app | `media` | `ChatId`, `RelatedMessageId`, `MediaData` = un pezzo base64, `MediaMimeType`, `MediaFileName`, `MediaType`, `MediaChunkIndex`, `MediaChunkTotal`: un pezzo di un messaggio che l'app ha gia' |
| adapter -> app | `error` | `Text` |

Un frame e' `[lunghezza 4 byte little-endian][payload]`. Una lunghezza uguale a
`0`, o sopra `MAX_FRAME_LENGTH` (8 MiB, esportato da `server.js` e uguale a
`FrameCodec.MaxFrameLength` nell'app), viene trattata come un guasto:
l'adapter la scrive nel log e chiude il socket, invece di accumulare.

Un allegato va a WhatsApp solo quando sono arrivati tutti i pezzi che aveva
annunciato: `media.begin` porta `MediaChunkTotal`, e una spedizione a cui manca
un pezzo viene rifiutata con un frame `error` invece di partire piu' corta. Un
pezzo singolo puo' arrivare a 8 MiB (il tetto del frame); un allegato intero si
ferma a 64 MiB mentre sta ancora arrivando, e al massimo quattro spedizioni
possono restare aperte insieme.

## Configurazione

Vedi `.env.example`. Le variabili principali:

| Variabile | Default | Descrizione |
|---|---|---|
| `GOWA_URL` | `http://127.0.0.1:3000` | URL del server GOWA |
| `GOWA_USER` / `GOWA_PASS` | — | Credenziali Basic Auth di GOWA |
| `GOWA_DEVICE_ID` | — | Device GOWA (multi-device); vuoto = default |
| `BRIDGE_PORT` | `8585` | Porta TCP per l'app WP8 |
| `WEBHOOK_PORT` | `8586` | Porta HTTP del webhook |
| `WEBHOOK_PUBLIC_URL` | `http://127.0.0.1:8586/webhook` | URL con cui GOWA raggiunge l'adapter |
| `WEBHOOK_SECRET` | `secret` | Deve combaciare con `--webhook-secret` di GOWA; senza un segreto il webhook rifiuta ogni richiesta invece di fidarsi |
| `BRIDGE_KEY` | `WhatsAppCommunityWP8-2026` | La chiave del cifrario dei frame. Deve combaciare con `CryptoHelper.cs`, o con la chiave digitata nell'app (vedi sotto). Il default e' compilato nell'app pubblica, quindi non e' un segreto |
| `BRIDGE_REQUIRE_KEY` | `off` | rifiuta di partire finche' il cifrario usa ancora il default compilato (`on` per un deployment raggiungibile) |
| `PAIRING` | `off` | accetta una chiave generata dal telefono finche' non ne esiste ancora una (vedi *Accoppiamento* sotto) |
| `PAIRING_TTL_MIN` | `15` | quanto resta aperta la finestra di accoppiamento |
| `BRIDGE_KEY_FILE` | — | dove si conserva la chiave ricevuta, cosi' un riavvio non chiede un nuovo accoppiamento; vuoto la tiene solo nell'ambiente |
| `POLL_INTERVAL_MS` | `5000` | Ogni quanto viene interrogato lo stato WhatsApp |
| `DISCOVERY_ENABLED` | `on` | Annuncia l'adapter sulla rete locale (`off` lo spegne) |
| `DISCOVERY_PORT` | `8587` | Porta UDP del beacon di scoperta |
| `DISCOVERY_NAME` | nome host | Nome mostrato nell'elenco dei server trovati dell'app |
| `CALLS_CHAT_LIMIT` | `25` | quante chat recenti legge la scansione delle chiamate |
| `CALLS_MESSAGES_PER_CHAT` | `100` | messaggi letti per ogni chat scansionata |
| `CALLS_LIMIT` | `50` | numero massimo di chiamate inviate all'app |
| `CHATS_LIMIT` | `25` | quante conversazioni restituisce l'elenco chat |
| `CHATS_AVATARS` | `on` | scarica le immagini del profilo, una richiesta per chat, gruppi compresi (`off` le spegne). Un'immagine gia' scaricata si tiene per cinque minuti, un JID senza immagine per uno, cosi' un elenco chat riletto non torna da WhatsApp (vedi `avatar-cache.js`) |
| `FFMPEG_ENABLED` | `on` | converte i vocali Ogg/Opus in MP3 per WP8.1 (`off` la spegne) |
| `FFMPEG_PATH` | `ffmpeg` | l'eseguibile di ffmpeg, quando non e' nel PATH |
| `AUTH_REQUIRED` | `off` | chiede un token in `hello` (`on` per un servizio condiviso) |
| `AUTH_REGISTER` | `on` | un telefono che arriva senza token ne riceve uno alla prima connessione (`off` chiude il servizio: i token si consegnano a mano) |
| `AUTH_STRICT_DEVICE` | `off` | un dispositivo che il deposito conosce gia' deve presentare un token valido; il solo device id non basta (`on` chiude la strada dell'impersonificazione via device id, al costo del comportamento "reinstalla e tieni l'account") |
| `AUTH_MAX_USERS` | `50` | tetto ai device che possono registrarsi da soli |
| `USERS_FILE` | — | dove vivono gli utenti; vuoto li tiene in memoria, un percorso sopravvive a un riavvio |
| `ENDPOINT_PUBLISH` | `off` | aggiunge la riga di questo server al registro condiviso, cosi' l'app lo trova e puo' ripiegare su di lui (vedi *Il registro dei server*) |
| `ENDPOINT_REPO` | `vincenzosco/whatsappforwp-endpoint` | il repository dove vive il registro |
| `ENDPOINT_TOKEN` | — | un token GitHub che puo' scrivere su `ENDPOINT_REPO`; vuoto riprende `GH_TOKEN` |
| `ENDPOINT_SERVER_ID` | nome host | la riga che questo server possiede; due server non devono condividerla |
| `ENDPOINT_SERVER_NAME` | = id | il nome che il registro mostra |
| `ENDPOINT_HOST` | primo IPv4 locale | l'indirizzo da annunciare; mettilo quando l'IP della macchina non e' quello che il telefono chiama |
| `ENDPOINT_PORT` | `BRIDGE_PORT` | la porta da annunciare |
| `ENDPOINT_PUBLISH_MINUTES` | `30` | ogni quanto la riga viene riscritta, cosi' un indirizzo che si sposta viene ripreso |

Il token e' l'unica cosa che distingue un telefono su un servizio condiviso, e un
telefono che non ne ha uno lo riceve: con `AUTH_REGISTER=on` (il valore
predefinito) l'adapter crea l'utente al primo handshake e risponde con un frame
`registered` che porta il token, che l'app conserva. L'interruttore nella pagina
di connessione e' tutta la configurazione - il token identifica il dispositivo,
non e' una password da digitare. Creare l'utente a mano resta per un servizio che
deve restare chiuso, con `AUTH_REGISTER=off`:

Il token e' **derivato dal dispositivo**, non estratto a caso: e'
`HMAC-SHA256(segreto, deviceId)` con un segreto che il deposito genera una volta e
conserva in `USERS_FILE`. L'app presenta un solo id di dispositivo (`SenderId` in
`hello`) e questo sopravvive all'installazione: e' il token hardware specifico del
pacchetto (`HardwareIdentification.GetPackageSpecificToken`), lo stesso sullo
stesso telefono per lo stesso pacchetto, con il valore salvato come cache e un id
casuale solo come ripiego di un telefono che non risponde. Un telefono che si
ricollega - o che reinstalla l'app - e' quindi lo stesso utente con lo stesso
token, e il file degli utenti non cresce piu' di una riga per collegamento ne' per
reinstallazione. (Una versione precedente usava un id casuale tenuto in
`LocalSettings`, che WP8.1 cancella alla disinstallazione: una reinstallazione
diventava un dispositivo nuovo, ed e' per questo che l'accesso a WhatsApp andava
rifatto.) Un dispositivo mai visto ne riceve uno nuovo; a uno che il servizio
conosce si restituisce il suo token, senza dirgli niente, perche' ce l'ha gia'.
`AUTH_MAX_USERS` conta i dispositivi, e riregistrare un dispositivo noto non
consuma un posto. Il segreto e' una credenziale: una copia del file puo' derivare
il token di ogni dispositivo, ed e' il compromesso che questo deposito accetta per
un token che resta lo stesso. Con `AUTH_STRICT_DEVICE=on` il solo device id non
basta piu' per un dispositivo che il deposito conosce: un telefono che reinstalla
e ha perso il token viene rifiutato e deve riceverne uno nuovo con
`create-user.js`, invece di ricevere l'account indietro da chiunque conosca il
device id.

La chiave del cifrario dei frame si puo' impostare anche nell'app: la pagina
delle impostazioni ha un campo *Chiave del server*, e un telefono che lo compila
usa quel valore invece del default compilato. Un deployment che imposta un
`BRIDGE_KEY` suo e `BRIDGE_REQUIRE_KEY=on` si raggiunge digitando lo stesso
valore li'; con il campo vuoto si usa il default compilato, che e' quello che si
aspetta un server privato che non ha mai impostato `BRIDGE_KEY`.

```bash
node create-user.js vincenzo            # stampa id, nome e token, una volta sola
# dentro il container:
docker exec whatsapp-for-wp8 node /opt/adapter/create-user.js vincenzo
```

Ogni utente ha un device GOWA suo, creato al primo handshake, e ogni comando,
cache e webhook e' limitato a quello: un messaggio per un device non arriva mai
al socket di un altro utente. Con `AUTH_REQUIRED=off` l'adapter si comporta come
prima, con un account solo e nessun token. L'instradamento dei webhook usa il
`device_id` di primo livello che GOWA mette su ogni evento. I token sono tenuti
solo come hash scrypt; un token perso non si recupera, ma un telefono che torna con
lo stesso device id riceve di nuovo lo stesso token, perche' e' derivato da quell'id.

### Accoppiamento

La chiave dei frame di questo telefono puo' essere generata dal telefono stesso e
inviata al server una volta sola: sul server non c'e' niente di segreto da
digitare. Il token non lo disegna il telefono: lo deriva il server dal device id,
esattamente come per ogni altro dispositivo, quindi ogni telefono tiene un solo
token legato al proprio device id. Con `PAIRING=on` e nessuna chiave sua, l'adapter
apre una finestra monouso e ne stampa il codice per l'operatore:

```
[WARN] PAIRING CODE: ABCD-EFGH-JKLM-NPQR
```

L'app non ha bisogno di quella riga. Chiede il codice con il comando `pair.code`
e il server risponde con un frame `pair.info` che porta il codice e per quanto
resta valido, quindi *Invia la chiave al server* funziona da solo. Il telefono
estrae 32 byte casuali per la chiave, la sigilla con il codice in un unico blocco
e lo spedisce dentro il normale frame `pair`, insieme al suo device id in
`SenderId`. Il frame esterno e' il default pubblico - non c'e' ancora altro con
cui scriverlo - ma il blocco dentro e' cifrato con il codice. Se la finestra e'
scaduta tra la risposta e l'accoppiamento, l'app chiede di nuovo: la richiesta
apre una finestra nuova, quindi un codice scaduto viene rimpiazzato invece di far
fallire tutto. Il server adotta la chiave (scrivedola in `BRIDGE_KEY_FILE`, quando
e' impostato), deriva il token del dispositivo da quell'id, lo restituisce nel
frame `paired` e chiude la finestra; un riavvio rilegge la chiave e non chiede
altro.

Il codice ha 80 bit casuali e la finestra si chiude dopo cinque tentativi
sbagliati o `PAIRING_TTL_MIN` minuti, quello che arriva prima. Un server che ha
gia' una chiave non la apre mai: per accoppiare di nuovo, ferma il container,
cancella `BRIDGE_KEY_FILE` e riavvialo con `PAIRING=on`.

Siccome ora il codice arriva al telefono sullo stesso canale che protegge, non
separa piu' l'operatore da uno sconosciuto che raggiunge la porta: finche' il
server non ha una chiave sua, il primo telefono che chiede di accoppiarsi e'
quello che lo fa. La finestra dura solo fino al primo accoppiamento, quindi su un
host raggiungibile da internet conviene accoppiare subito dopo il primo avvio.

L'accoppiamento registra il dispositivo nello stesso passo, ed e' questo che ammette
un telefono appena accoppiato su un servizio con `AUTH_REGISTER=off`. Il token resta
derivato dal device id e dal segreto del deposito, quindi l'accoppiamento non
cambia il modello di fiducia di `users.json`: serve a stabilire la chiave dei
frame, non a consegnare una credenziale casuale.

### I vocali hanno bisogno di ffmpeg

I messaggi vocali di WhatsApp sono Ogg con codec Opus, e Windows Phone 8.1 non ha un
decoder Opus (arriva solo su Windows 10). L'adapter esegue quindi **ffmpeg**, se e'
installato, per convertire un vocale ricevuto in un piccolo MP3 mono prima di mandarlo
all'app. ffmpeg e' un programma esterno alla macchina che esegue l'adapter, non una
dipendenza dell'adapter. Senza di esso l'adapter scrive un avviso all'avvio e inoltra i
byte originali, che il telefono non sa leggere; il vocale arriva lo stesso e dice che
non si puo' riprodurre.

L'altra direzione non ha bisogno di ffmpeg. Un vocale registrato arriva dall'app
come payload M4A/AAC; `sendMediaToGowa` passa un payload `audio` a
`session.gowa.sendAudio`, che lo pubblica su `POST /send/audio` (campo del form
`audio`). E' quella rotta a far spedire da GOWA un vocale WhatsApp invece di un
file con un MIME audio. Un adapter costruito su un GOWA senza la rotta ripiega
su `POST /send/file`.

## Avvio

Da solo:

```bash
cd WhatsappBridge
cp .env.example .env   # opzionale; le variabili gia' esportate hanno la precedenza
npm start
```

Insieme a GOWA, che e' il modo normale per provare l'app e l'unico che non richiede di
sapere a memoria le porte:

```bash
node tools/start-login.js --download            # scarica GOWA la prima volta
node tools/start-login.js --no-qr               # lancio normale: il QR lo mostra il telefono
node tools/start-login.js --code 393401234567   # oppure con codice di abbinamento
node tools/start-login.js --stop                # ferma tutto
```

Lo script avvia GOWA (sessione in `.tools/gowa/storages/whatsapp.db`, ignorata da git),
avvia questo adattatore, stampa l'IP e le porte da dare all'app e, quando lo si chiede,
disegna il QR di login nel terminale (ora e' opzionale: il login si fa dal telefono).
Dettagli e diagnosi: `.agents/skills/run-the-login-server/SKILL.md`.

Il webhook viene registrato automaticamente su GOWA. In alternativa avvia GOWA con:

```bash
./whatsapp rest --webhook=http://<indirizzo-adapter>:8586/webhook
```

## Il registro dei server

L'app non ha un indirizzo solo: legge una lista e prova i server in ordine
finche' uno risponde, cosi' un server che va giu' non porta giu' l'app. La lista
e' `endpoint.json` in
[whatsappforwp-endpoint](https://github.com/vincenzosco/whatsappforwp-endpoint),
il cui array `servers` contiene una riga per server (`id`, `name`, `host`, `port`,
`updatedAt`). I campi in cima `host`/`port` rispecchiano la prima riga, ed e' quello
che legge un'app costruita prima della lista.

Un adapter aggiunge la sua riga con `ENDPOINT_PUBLISH=on`: all'avvio, e ogni
`ENDPOINT_PUBLISH_MINUTES`, legge il file, sostituisce la propria riga (individuata
da `ENDPOINT_SERVER_ID`) e lo riscrive, lasciando intatte le righe degli altri
server. Non lancia mai e non blocca il server: un deployment senza token si limita
a scrivere nel log che non ha potuto pubblicare.

L'IP di un container bridged e' quello di Docker, non quello che il telefono
chiama, quindi li' `ENDPOINT_HOST` deve portare l'indirizzo reale. Il container
tunnel del deployment Docker pubblica da solo l'indirizzo pubblico; i due
scrivono righe diverse dello stesso file.

## Scoperta automatica

L'adapter annuncia la sua presenza ogni 2 secondi in UDP sulla porta 8587
(`DISCOVERY_PORT`): l'app WP8 ascolta quella porta e usa l'indirizzo *del mittente* per
connettersi, quindi non serve piu' digitare IP e porta. L'annuncio esce su ogni
interfaccia fisica (VPN e bridge di macchine virtuali sono esclusi).

Il beacon non contiene segreti: solo hostname, porta TCP e stato WhatsApp.

```json
{"service":"whatsapp-wp8-adapter","version":1,"name":"mac-di-vincenzo","port":8585,"state":"disconnected","account":""}
```

Metti `DISCOVERY_ENABLED=off` per spegnerlo: l'inserimento manuale dell'indirizzo
nell'app continua a funzionare.

## Test

```bash
npm test
```

I test coprono la configurazione, la formattazione dei messaggi WP8, il client REST GOWA
(con `fetch` simulato), la verifica HMAC del webhook, il beacon di scoperta, il modulo di
cifratura (entrambi i cifrari, il tag e un vettore di prova fisso) e il protocollo TCP
end-to-end (client WP8 simulato).
