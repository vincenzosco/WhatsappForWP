# GOWA Adapter

Ponte tra l'app WhatsApp per Windows Phone 8.1 e un server GOWA self-hosted
([go-whatsapp-web-multidevice](https://github.com/vincenzosco/go-whatsapp-web-multidevice)).

## Come funziona

```
 App WP8  ⇄  (TCP cifrato AES-256-GCM)  ⇄  Adapter  ⇄  (HTTP REST + webhook)  ⇄  GOWA  ⇄  WhatsApp
```

- Il login (QR code o codice di abbinamento) è richiesto **dall'app** tramite frame di controllo.
- I messaggi in arrivo da WhatsApp arrivano via webhook e vengono inoltrati all'app sul canale TCP.
- I messaggi in uscita sono inviati alle API REST di GOWA.

## Protocollo di controllo

Frame `Type = System`, `ChatId = "system"`.

| Direzione | `Command` | Campi usati |
|---|---|---|
| app → adapter | `hello` | `Text` = nome utente |
| app → adapter | `status` | — |
| app → adapter | `login.qr` | — |
| app → adapter | `login.code` | `Text` = numero con prefisso |
| app → adapter | `contacts` | — |
| app → adapter | `logout` | — |
| adapter → app | `state` | `State`, `AccountJid` |
| adapter → app | `qr` | `QrImageData` (base64 PNG), `QrDuration` |
| adapter → app | `paircode` | `PairCode` |
| adapter → app | `contact` | `ChatId` = JID, `SenderName` = nome |
| adapter → app | `error` | `Text` |

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
| `POLL_INTERVAL_MS` | `5000` | Polling dello stato WhatsApp |

## Avvio

Da solo:

```bash
cd WhatsappBridge
cp .env.example .env   # opzionale; le variabili già esportate hanno la precedenza
npm start
```

Insieme a GOWA, con il QR di login disegnato nel terminale — è il modo normale per
provare l'app, e l'unico che non richiede di sapere a memoria le porte:

```bash
node tools/start-login.js --download            # scarica GOWA la prima volta
node tools/start-login.js --code 393401234567   # oppure con codice di abbinamento
node tools/start-login.js --stop                # ferma tutto
```

Lo script avvia GOWA (sessione in `.tools/gowa/storages/whatsapp.db`, ignorata da
git), avvia questo adattatore, stampa l'IP e le porte da dare all'app e rinnova il
QR finché il telefono non è collegato. Dettagli e diagnosi:
`.agents/skills/run-the-login-server/SKILL.md`.

Il webhook viene registrato automaticamente su GOWA. In alternativa avvia GOWA con:

```bash
./whatsapp rest --webhook=http://<indirizzo-adapter>:8586/webhook
```

## Test

```bash
npm test
```

I test coprono la configurazione, la formattazione dei messaggi WP8, il client REST
GOWA (con `fetch` simulato), la verifica HMAC del webhook e il protocollo TCP
end-to-end (client WP8 simulato).
