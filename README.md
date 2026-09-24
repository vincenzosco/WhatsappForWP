# WhatsApp for Windows Phone 8.1

A community-maintained WhatsApp client for Windows Phone 8.1 (Universal Windows Platform). This project includes a full WhatsApp-like UI and a bridge server to connect to real WhatsApp servers.

## Projects

### WhatsappApp (Windows Phone 8.1 App)

The main client app with an authentic WhatsApp user interface.

**Features:**
- WhatsApp green theme (header #075E54, accent #25D366, chat bubbles)
- Chat list with avatars, unread badges, online indicators
- Message bubbles with timestamps and sent/delivered/read status
- Text messaging with Enter-to-send
- Image attachment: pick photos from gallery and send them via the bridge
- Image preview in chat bubbles (base64 over TCP)
- Connection settings: point the app at the GOWA adapter, then log in with a QR code or a phone pairing code
- Italian language UI

**Architecture:**

The app uses a TCP connection with length-prefixed JSON messages:

```
[4 bytes: UInt32 LE message length] [N bytes: UTF-8 JSON ChatMessage]
```

The `ChatMessage` model uses `DataContractJsonSerializer` for serialization (DateTime in `\/Date()\/` format).

### WhatsappServer (.NET Console App)

A simple TCP relay server that broadcasts messages between connected clients.

- Listens on port 8585 (default)
- Relays length-prefixed JSON messages between clients
- Displays connection logs and message previews
- Written in .NET Framework 4.5.1

> **Note:** this relay is superseded by `WhatsappBridge/server.js`, which now
> bridges the WP8 app to a self-hosted **GOWA** server instead of relaying to
> other phones. The .NET project is kept for reference / standalone use.

### GOWA Adapter (Node.js)

A thin adapter that connects the Windows Phone 8.1 app to a self-hosted
[GOWA](https://github.com/vincenzosco/go-whatsapp-web-multidevice) server
(`go-whatsapp-web-multidevice`). It does **not** implement its own WhatsApp
client any more: it uses GOWA's REST API and webhooks.

**Features**

- Login via **QR code** or via **phone number pairing code**, both shown in the app
- Keeps the encrypted (AES-256-GCM) TCP channel between app and adapter
- Sends text and images through `POST /send/message` and `POST /send/image`
- Receives incoming messages through a GOWA webhook (HMAC-verified)
- Syncs contacts from `GET /user/my/contacts`

**Setup**

1. Start GOWA:

```bash
git clone https://github.com/vincenzosco/go-whatsapp-web-multidevice
cd go-whatsapp-web-multidevice/src
go run . rest --basic-auth=admin:admin --port=3000
```

2. Start the adapter:

```bash
cd WhatsappBridge
cp .env.example .env   # optional, or export the variables
npm install
npm start
```

The adapter registers its webhook on GOWA automatically. If that fails, start
GOWA with `--webhook=http://<adapter-host>:8586/webhook`.

3. In the app, set the adapter address/port and tap **Connetti al server**, then
   log in with the QR code or with your phone number.

**Requirements:** Node.js 18.13+ and a reachable GOWA instance.

Environment variables are documented in `WhatsappBridge/.env.example`.

## Protocol

The TCP protocol uses length-prefixed JSON messages, compatible with Windows `DataWriter`/`DataReader`:

- 4 bytes: message length (UInt32, Little Endian)
- N bytes: encrypted payload

The payload is encrypted with **AES-256-GCM** using a pre-shared key (SHA-256 of a passphrase):
12-byte random IV, ciphertext, 16-byte auth tag. The app and the server must use the
same passphrase (`BRIDGE_KEY` env var on the server, constant in `CryptoHelper.cs` in the app).

After decryption, the JSON body follows the `ChatMessage` schema:

```json
{
  "Id": "msg_123",
  "Text": "Hello!",
  "SenderId": "393401234567@s.whatsapp.net",
  "SenderName": "Mario",
  "ChatId": "393401234567@s.whatsapp.net",
  "Timestamp": "\/Date(1750000000000)\/",
  "Status": 1,
  "Type": 0,
  "IsIncoming": true,
  "MediaData": "/9j/4AAQ...base64...",
  "MediaMimeType": "image/jpeg",
  "MediaFileName": "photo.jpg"
}
```

Types: 0=Text, 1=Image, 2=Audio, 3=System
Statuses: 0=Sending, 1=Sent, 2=Delivered, 3=Read, 4=Failed

`Type = 3` frames are **control frames** (`ChatId = "system"`) used for the
WhatsApp login flow: the app sends `login.qr` / `login.code` and the adapter
answers with `qr` / `paircode` / `state` / `contact` / `error` frames. See
`WhatsappBridge/README.md` for the full command table.

## Building

### WP8 App

Open `WhatsappApp.sln` in Visual Studio 2015 with Windows Phone 8.1 SDK. Build and deploy to a Windows Phone 8.1 device or emulator.

Il toolchain di Windows Phone 8.1 compila l'app con il compilatore **C# 5**: la
sintassi C# 6/7 (stringhe interpolate, `?.`, proprietà con corpo `=>`,
inizializzatori di proprietà automatiche, pattern matching, `out var`) non
compila. Prima di ogni build eseguire:

```bash
node tools/check-csharp5.js
```

Esce con codice 0 quando tutti i file `.cs` della soluzione sono compatibili con
C# 5, altrimenti elenca file, riga e costrutto da correggere. Lo stesso script
controlla anche i membri **assenti dalla proiezione WinRT di Windows Phone 8.1**
(es. `CryptographicBuffer.CreateFromByteArray` a 3 argomenti,
`ContentDialog.CloseButtonText`): compilano su Windows 8.1/10 ma non su WP8.1.

### GOWA Adapter

```bash
cd WhatsappBridge
npm install
npm test     # test unitari e di integrazione
npm start
```

## Disclaimer

- This is an unofficial project not affiliated with WhatsApp or Meta.
- GOWA (and therefore this adapter) uses unofficial methods to connect to WhatsApp, which violates WhatsApp's Terms of Service.
- Using this bridge may result in a permanent ban of your phone number.
- Only use with test/secondary phone numbers.
- For production use, refer to the official WhatsApp Business API.

## License

MIT - Community maintained project. Use at your own risk.
