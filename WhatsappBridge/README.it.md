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
| adapter -> app | `state` | `State`, `AccountJid` |
| adapter -> app | `qr` | `QrImageData` (base64 PNG), `QrDuration` |
| adapter -> app | `paircode` | `PairCode` |
| adapter -> app | `contact` | `ChatId` = JID, `SenderName` = nome |
| adapter -> app | `call` | `ChatId`, `SenderName`, `Timestamp`, `CallId`, `CallReason`, `CallDurationSeconds`, `CallIsVideo` |
| adapter -> app | `calls.done` | — (la scansione è finita, anche senza chiamate) |
| adapter -> app | `revoked` | `ChatId`, `RelatedMessageId` = id del messaggio cancellato |
| adapter -> app | `edited` | `ChatId`, `RelatedMessageId`, `Text` = il nuovo testo |
| adapter -> app | `error` | `Text` |

Un frame e' `[lunghezza 4 byte little-endian][payload]`. Una lunghezza uguale a
`0`, o sopra `MAX_FRAME_LENGTH` (8 MiB, esportato da `server.js` e uguale a
`CommunicationService.MaxFrameLength` nell'app), viene trattata come un guasto:
l'adapter la scrive nel log e chiude il socket, invece di accumulare.

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
