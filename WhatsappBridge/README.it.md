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
  perche' un fumetto vuoto e' peggio di una parola.
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
| adapter -> app | `state` | `State`, `AccountJid` |
| adapter -> app | `qr` | `QrImageData` (base64 PNG), `QrDuration` |
| adapter -> app | `paircode` | `PairCode` |
| adapter -> app | `contact` | `ChatId` = JID, `SenderName` = nome |
| adapter -> app | `call` | `ChatId`, `SenderName`, `Timestamp`, `CallId`, `CallReason`, `CallDurationSeconds`, `CallIsVideo` |
| adapter -> app | `calls.done` | — (la scansione è finita, anche senza chiamate) |
| adapter -> app | `chat` | `ChatId`, `SenderName`, `Text` = ultimo messaggio, `Timestamp`, `IsGroup`, `AvatarData` (base64), `UnreadCount` |
| adapter -> app | `chats.done` | — (l'elenco è finito) |
| adapter -> app | `revoked` | `ChatId`, `RelatedMessageId` = id del messaggio cancellato |
| adapter -> app | `edited` | `ChatId`, `RelatedMessageId`, `Text` = il nuovo testo |
| adapter -> app | `media` | `ChatId`, `RelatedMessageId`, `MediaData` = un pezzo base64, `MediaMimeType`, `MediaFileName`, `MediaType`, `MediaChunkIndex`, `MediaChunkTotal`: un pezzo di un messaggio che l'app ha gia' |
| adapter -> app | `error` | `Text` |

Un frame e' `[lunghezza 4 byte little-endian][payload]`. Una lunghezza uguale a
`0`, o sopra `MAX_FRAME_LENGTH` (8 MiB, esportato da `server.js` e uguale a
`CommunicationService.MaxFrameLength` nell'app), viene trattata come un guasto:
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
| `WEBHOOK_SECRET` | — | Deve combaciare con `--webhook-secret` di GOWA |
| `BRIDGE_KEY` | `WhatsAppCommunityWP8-2026` | Deve combaciare con `CryptoHelper.cs` |
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
| `AUTH_MAX_USERS` | `50` | tetto ai device che possono registrarsi da soli |
| `USERS_FILE` | — | dove vivono gli utenti; vuoto li tiene in memoria, un percorso sopravvive a un riavvio |

Il token e' l'unica cosa che distingue un telefono su un servizio condiviso, e un
telefono che non ne ha uno lo riceve: con `AUTH_REGISTER=on` (il valore
predefinito) l'adapter crea l'utente al primo handshake e risponde con un frame
`registered` che porta il token, che l'app conserva. L'interruttore nella pagina
di connessione e' tutta la configurazione - il token identifica il dispositivo,
non e' una password da digitare. Creare l'utente a mano resta per un servizio che
deve restare chiuso, con `AUTH_REGISTER=off`:

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
solo come hash scrypt; un token perso si sostituisce, non si recupera, e un
telefono che lo perde e torna viene semplicemente registrato come device nuovo.

### I vocali hanno bisogno di ffmpeg

I messaggi vocali di WhatsApp sono Ogg con codec Opus, e Windows Phone 8.1 non ha un
decoder Opus (arriva solo su Windows 10). L'adapter esegue quindi **ffmpeg**, se e'
installato, per convertire un vocale ricevuto in un piccolo MP3 mono prima di mandarlo
all'app. ffmpeg e' un programma esterno alla macchina che esegue l'adapter, non una
dipendenza dell'adapter. Senza di esso l'adapter scrive un avviso all'avvio e inoltra i
byte originali, che il telefono non sa leggere; il vocale arriva lo stesso e dice che
non si puo' riprodurre.

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
