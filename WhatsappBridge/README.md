# WhatsApp Community Bridge Server

Ponte che connette l'app **Windows Phone 8.1 WhatsApp Community** ai veri server WhatsApp, usando `whatsapp-web.js` (Puppeteer + WhatsApp Web).

## ⚠️ Importante

- **Questo NON è un prodotto ufficiale.** Viola i Termini di Servizio di WhatsApp.
- **Rischio di ban permanente** del tuo numero di telefono.
- Usa **solo con numeri secondari/test**.
- Per uso professionale, usa il [WhatsApp Business API](https://developers.facebook.com/docs/whatsapp/) ufficiale.

## Come funziona

```
┌─────────────────┐    TCP/JSON     ┌────────────────────┐    WhatsApp Web    ┌──────────────┐
│  WP8 App        │ ◄─────────────► │ Bridge Server      │ ◄────────────────► │ WhatsApp     │
│  (StreamSocket) │                 │ (Node.js + Puppe-  │                    │ Servers      │
│                 │                 │  teer + whatsapp-  │                    │              │
│                 │                 │  web.js)           │                    │              │
└─────────────────┘                 └────────────────────┘                    └──────────────┘
```

Il bridge:
1. Avvia un server TCP sulla porta `8585` (o `BRIDGE_PORT` env)
2. Usa `whatsapp-web.js` per autenticarsi con WhatsApp Web (tramite QR code)
3. Inoltra i messaggi tra la tua app WP8 e i server WhatsApp
4. Mantiene la sessione salvata (non serve scansionare il QR ogni volta)

## Requisiti

- **Node.js 18+** ([download](https://nodejs.org/))
- **Google Chrome** o Chromium (installato automaticamente da Puppeteer)
- **Connessione internet** stabile

## Installazione

```bash
# 1. Vai nella cartella del bridge
cd WhatsappBridge

# 2. Installa le dipendenze
npm install

# 3. Avvia il server
npm start
```

## Primo avvio

Al primo avvio:

1. **Il server mostrerà un QR code** nel terminale (e una notifica apparirà nell'app WP8)
2. Apri **WhatsApp sul telefono** → menu (⋮) → **Dispositivi collegati** → **Collega dispositivo**
3. **Inquadra il QR code** che appare nel terminale
4. Una volta autenticato, il server mostrerà "✅ CLIENT WHATSAPP PRONTO!"
5. L'app WP8 riceverà automaticamente la lista dei contatti

> 💡 La sessione viene salvata: al prossimo avvio non servirà scansionare di nuovo.

## Collegare l'app WP8

1. Avvia il bridge server sul PC
2. Sull'app WP8: vai in **Impostazioni Server** (icona ⚙️ in alto a destra)
3. Scegli **Modalità Client**
4. Inserisci l'**indirizzo IP del PC** (es. `192.168.1.100`)
5. Porta: `8585` (default)
6. Inserisci il tuo nome utente
7. Clicca **Connetti al server**

## Comandi

```bash
# Avvio normale
npm start

# Avvio con QR code nel terminale
npm run start:qr

# Avvio in modalità debug (log dettagliati)
npm run start:debug

# Porta personalizzata
BRIDGE_PORT=9090 npm start
```

## Struttura dei file

```
WhatsappBridge/
├── server.js          # Server principale (TCP + WhatsApp)
├── package.json       # Dipendenze
├── README.md          # Questo file
└── .wwebjs_auth/      # Cartella sessioni (generata automaticamente)
```

## Protocollo TCP

Il bridge usa lo stesso protocollo dell'app WP8:

```
┌──────────────────────┬──────────────────────────────┐
│  4 bytes (UInt32 LE) │  N bytes (UTF-8 JSON)       │
│  Lunghezza messaggio  │  ChatMessage serializzato   │
└──────────────────────┴──────────────────────────────┘
```

Formato JSON (`ChatMessage`):
```json
{
  "Id": "msg_123",
  "Text": "Ciao!",
  "SenderId": "393401234567",
  "SenderName": "Mario",
  "ChatId": "wa_393401234567",
  "Timestamp": "2026-07-13T10:00:00",
  "Status": 1,
  "Type": 0,
  "IsIncoming": true
}
```

## Messaggi di sistema

Quando il server invia un messaggio con `ChatId: "system"` e `Type: 3`, l'app WP8 lo riconosce come messaggio di sistema (non lo mostra come chat ma come notifica di stato).

## Troubleshooting

| Problema | Soluzione |
|----------|-----------|
| `Puppeteer failed to launch` | Installa Chrome o esegui `npx puppeteer install` |
| QR code non appare | Riavvia il server, potrebbe essere un problema di cache |
| Sessione scaduta | Elimina la cartella `.wwebjs_auth` e riavvia |
| Connessione rifiutata | Controlla il firewall di Windows (porta 8585) |
| Messaggi non arrivano | Verifica che il telefono abbia connessione internet |
| Ban dell'account | Usa un numero secondario. WhatsApp vieta client non ufficiali |

## Collegamento al server PC esistente

Il progetto `WhatsappServer` (.NET) nella soluzione originale è un relay TCP generico.
**Questo bridge (`WhatsappBridge/`)** lo sostituisce con un ponte reale verso WhatsApp.

## Licenza

MIT — Progetto comunitario, senza garanzie. Usalo a tuo rischio.
