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
- Connection settings: connect to a PC bridge server, or self-host as a TCP relay
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

> **Note:** this relay is now merged into `WhatsappBridge/server.js`. The Node.js
> unified server does both the TCP relay *and* the WhatsApp bridge in one process.
> The .NET project is kept for reference / standalone use.

### WhatsappBridge (Node.js Unified Server)

A Node.js server that combines the TCP relay (`WhatsappServer`) and the WhatsApp bridge in a single process. It connects your Windows Phone 8.1 app to actual WhatsApp servers using the `whatsapp-web.js` library.

**Features:**
- Authenticates with WhatsApp Web via QR code scanning
- Maintains session (no re-scan required after first login)
- Relays text messages between your WP8 app and WhatsApp contacts
- Relays messages between connected WP8 clients (local/community chats)
- Encrypted connection (AES-256-GCM) between the app and the server
- Image/media message support: sends and receives photos
- Message queue: messages sent before WhatsApp is ready are queued and sent automatically
- Graceful shutdown handling

**Setup:**

```bash
cd WhatsappBridge
npm install
npm start
```

On first run, scan the QR code with WhatsApp > Linked Devices.

**Requirements:** Node.js 18+, Google Chrome (or Chromium downloaded by Puppeteer).

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
  "SenderId": "393401234567",
  "SenderName": "Mario",
  "ChatId": "wa_393401234567",
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

## Building

### WP8 App

Open `WhatsappApp.sln` in Visual Studio 2015 with Windows Phone 8.1 SDK. Build and deploy to a Windows Phone 8.1 device or emulator.

### Bridge Server

```bash
cd WhatsappBridge
npm install
npm start
```

## Disclaimer

- This is an unofficial project not affiliated with WhatsApp or Meta.
- The bridge server uses unofficial methods to connect to WhatsApp Web, which violates WhatsApp's Terms of Service.
- Using this bridge may result in a permanent ban of your phone number.
- Only use with test/secondary phone numbers.
- For production use, refer to the official WhatsApp Business API.

## License

MIT - Community maintained project. Use at your own risk.
