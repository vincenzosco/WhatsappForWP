# GOWA Adapter

**English** | [Italiano](README.it.md)

The bridge between the WhatsApp app for Windows Phone 8.1 and a self-hosted GOWA
server ([go-whatsapp-web-multidevice](https://github.com/vincenzosco/go-whatsapp-web-multidevice)).

## How it works

```
 WP8 app  ⇄  (AES-256-CBC + HMAC TCP)  ⇄  Adapter  ⇄  (HTTP REST + webhook)  ⇄  GOWA  ⇄  WhatsApp
```

- The login (QR code or pairing code) is requested **by the app** through control frames;
  the app shows the code full screen and keeps the screen on while it is visible.
- Messages arriving from WhatsApp come in through a webhook and are forwarded to the app on
  the TCP channel.
- Outgoing messages are sent to GOWA's REST API.
- The adapter broadcasts a discovery beacon on the LAN, so the app finds it without being
  configured with an address.

## Control protocol

Frames with `Type = System`, `ChatId = "system"`.

| Direction | `Command` | Fields used |
|---|---|---|
| app -> adapter | `hello` | `Text` = user name |
| app -> adapter | `status` | — |
| app -> adapter | `login.qr` | — |
| app -> adapter | `login.code` | `Text` = number with country code |
| app -> adapter | `contacts` | — |
| app -> adapter | `logout` | — |
| adapter -> app | `state` | `State`, `AccountJid` |
| adapter -> app | `qr` | `QrImageData` (base64 PNG), `QrDuration` |
| adapter -> app | `paircode` | `PairCode` |
| adapter -> app | `contact` | `ChatId` = JID, `SenderName` = name |
| adapter -> app | `error` | `Text` |

## Configuration

See `.env.example`. The main variables:

| Variable | Default | Description |
|---|---|---|
| `GOWA_URL` | `http://127.0.0.1:3000` | URL of the GOWA server |
| `GOWA_USER` / `GOWA_PASS` | — | GOWA Basic Auth credentials |
| `GOWA_DEVICE_ID` | — | GOWA device (multi-device); empty = default |
| `BRIDGE_PORT` | `8585` | TCP port for the WP8 app |
| `WEBHOOK_PORT` | `8586` | HTTP port of the webhook |
| `WEBHOOK_PUBLIC_URL` | `http://127.0.0.1:8586/webhook` | URL GOWA uses to reach the adapter |
| `WEBHOOK_SECRET` | — | Must match GOWA's `--webhook-secret` |
| `BRIDGE_KEY` | `WhatsAppCommunityWP8-2026` | Must match `CryptoHelper.cs` |
| `POLL_INTERVAL_MS` | `5000` | How often the WhatsApp state is polled |
| `DISCOVERY_ENABLED` | `on` | Announce the adapter on the LAN (`off` disables it) |
| `DISCOVERY_PORT` | `8587` | UDP port of the discovery beacon |
| `DISCOVERY_NAME` | host name | Name shown in the app's list of found servers |

## Starting it

On its own:

```bash
cd WhatsappBridge
cp .env.example .env   # optional; variables already exported win over it
npm start
```

Together with GOWA, which is the normal way to try the app and the only one that does
not require knowing the ports by heart:

```bash
node tools/start-login.js --download    # downloads GOWA the first time
node tools/start-login.js --no-qr       # normal run: the phone shows its own QR
node tools/start-login.js --code 393401234567   # or with a pairing code
node tools/start-login.js --stop        # stops everything
```

The script starts GOWA (session in `.tools/gowa/storages/whatsapp.db`, git ignored),
starts this adapter, prints the IP and ports for the app, and draws the login QR in the
terminal when that is what you asked for (it is optional now: the login is done on the
phone). Details and diagnosis: `.agents/skills/run-the-login-server/SKILL.md`.

The webhook is registered on GOWA automatically. As an alternative, start GOWA with:

```bash
./whatsapp rest --webhook=http://<adapter-address>:8586/webhook
```

## Automatic discovery

The adapter announces itself every 2 seconds over UDP on port 8587
(`DISCOVERY_PORT`): the WP8 app listens on that port and uses the *sender's* address to
connect, so there is no IP and port to type any more. The announcement goes out on every
physical interface (VPNs and virtual-machine bridges are excluded).

The beacon carries no secrets: only the host name, the TCP port and the WhatsApp state.

```json
{"service":"whatsapp-wp8-adapter","version":1,"name":"mac-di-vincenzo","port":8585,"state":"disconnected","account":""}
```

Set `DISCOVERY_ENABLED=off` to switch it off: entering the address by hand in the app
keeps working.

## Tests

```bash
npm test
```

The tests cover the configuration, the WP8 message formatting, the GOWA REST client
(with a simulated `fetch`), the HMAC verification of the webhook, the discovery beacon,
the cipher module (both ciphers, the cipher tag and a fixed test vector) and the
end-to-end TCP protocol (with a simulated WP8 client).
