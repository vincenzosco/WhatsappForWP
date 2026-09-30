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
- `message.revoked` and `message.edited` are forwarded to the app as `revoked` and
  `edited` control frames, so a message deleted or changed on the phone does not stay
  frozen in the app. `message.reaction` is ignored: there is nowhere to draw it.
- Opening a chat asks for its stored messages (`messages`), and the adapter answers
  with ordinary message frames marked `IsHistory`. The app inserts them in date order
  and keeps them out of the unread count and the toasts: they are not arriving now. A
  message whose media is not among the bytes the webhook delivered is sent as text -
  `[Image]`, `[Video]`, ... - because an empty bubble is worse than a word.
- Each conversation row carries how many messages it has not read
  (`UnreadCount`). That count is kept by the adapter, because GOWA's chat list has no such
  field and because a message that arrives while the phone is off reaches the adapter's
  webhook and nobody else; the app clears it with `read` when the conversation is shown.
  The count lives in memory: restarting the adapter starts it again from zero.
  Channel JIDs (`...@newsletter`) are skipped here and in the webhook: a channel is
  not a conversation and cannot be replied to.
- A photo, a video or a file is sent in pieces (`media.begin` / `media.chunk` /
  `media.end`): the frame ceiling is 8 MiB and base64 adds a third, so a video cannot
  travel in one frame. Each piece is a multiple of 4 base64 characters, so the adapter
  concatenates the decoded bytes without re-encoding anything. Which door GOWA gets is
  decided by the MIME type (or the extension): `/send/image`, `/send/video` and
  `/send/file` are three different routes, and before this a video went out as an image.
- A photo or a video in a chat's history arrived while the phone was off: its bytes were
  delivered to the adapter and nowhere else, so the row is a word (`[Image]`). Tapping it
  asks the adapter (`media.get`), which reads `GET /message/:id/download` from GOWA and
  answers with the bytes in `media` frames; the app puts them on the message it already
  has. Nothing is downloaded until it is asked for.
- Those bytes come back in pieces too, in one `media` frame per `MEDIA_CHUNK_CHARS`
  (700000) characters of base64, each carrying `MediaChunkIndex`, `MediaChunkTotal` and
  `MediaType`. A video is far larger than one frame, and the phone reassembles it - a
  video into a local file, a photo into memory. An incoming message whose media the
  webhook already delivered follows the same route: the message frame announces it (with
  `MediaType`), then the `media` frames carry it.

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
| app -> adapter | `calls` | — (incoming only, from the most recent `CALLS_CHAT_LIMIT` chats) |
| app -> adapter | `chats` | — (the linked account's conversations, most recent first) |
| app -> adapter | `messages` | `Text` = chat JID (up to `MESSAGES_LIMIT` messages, as ordinary frames marked `IsHistory`) |
| app -> adapter | `read` | `Text` = chat JID (that conversation has now been read; clears its unread count) |
| app -> adapter | `media.begin` | `Text` = chat JID, `MediaTransferId`, `MediaFileName`, `MediaMimeType`, `MediaChunkTotal` (an attachment follows) |
| app -> adapter | `media.chunk` | `MediaTransferId`, `MediaChunkIndex`, `MediaData` = one base64 piece (a multiple of 4 characters) |
| app -> adapter | `media.end` | `MediaTransferId`, `Text` = caption (reassemble and send) |
| app -> adapter | `media.get` | `Text` = chat JID, `RelatedMessageId` = message id (downloads that message media and answers with one `media` frame per piece) |
| app -> adapter | `contact.info` | `Text` = chat JID (the adapter composes name, about, picture, business profile and, for a group, the description and the members into one JSON `Text`) |
| adapter -> app | `state` | `State`, `AccountJid` |
| adapter -> app | `qr` | `QrImageData` (base64 PNG), `QrDuration` |
| adapter -> app | `paircode` | `PairCode` |
| adapter -> app | `contact` | `ChatId` = JID, `SenderName` = name |
| adapter -> app | `call` | `ChatId`, `SenderName`, `Timestamp`, `CallId`, `CallReason`, `CallDurationSeconds`, `CallIsVideo` |
| adapter -> app | `calls.done` | — (the scan is over, even when no call was found) |
| adapter -> app | `chat` | `ChatId`, `SenderName`, `Text` = last message, `Timestamp`, `IsGroup`, `AvatarData` (base64), `UnreadCount` |
| adapter -> app | `chats.done` | — (the list is over) |
| adapter -> app | `revoked` | `ChatId`, `RelatedMessageId` = id of the deleted message |
| adapter -> app | `edited` | `ChatId`, `RelatedMessageId`, `Text` = the new text |
| adapter -> app | `media` | `ChatId`, `RelatedMessageId`, `MediaData` = one base64 piece, `MediaMimeType`, `MediaFileName`, `MediaType`, `MediaChunkIndex`, `MediaChunkTotal`: a piece of a message the app already has |
| adapter -> app | `error` | `Text` |

One frame is `[4-byte little-endian length][payload]`. A length of `0`, or one
above `MAX_FRAME_LENGTH` (8 MiB, exported from `server.js` and equal to
`CommunicationService.MaxFrameLength` in the app), is treated as a fault: the
adapter logs the length and closes the socket instead of buffering it.

An attachment is sent to WhatsApp only when every piece it announced has
arrived: `media.begin` carries `MediaChunkTotal`, and a transfer with a piece
missing is refused with an `error` frame instead of being sent short. A single
piece may be at most 8 MiB (the frame ceiling); a whole attachment stops at
64 MiB while it is still arriving, and at most four transfers can be open at
once.

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
| `CALLS_CHAT_LIMIT` | `25` | how many of the most recent chats the call scan reads |
| `CALLS_MESSAGES_PER_CHAT` | `100` | messages read per scanned chat |
| `CALLS_LIMIT` | `50` | maximum number of call records sent to the app |
| `CHATS_LIMIT` | `25` | how many conversations the chat list returns |
| `CHATS_AVATARS` | `on` | fetch profile pictures, one request per chat, groups included (`off` disables). A picture that was downloaded is kept for five minutes, a JID with no picture for one, so a chat list that is read again does not go back to WhatsApp (see `avatar-cache.js`) |
| `FFMPEG_ENABLED` | `on` | convert Ogg/Opus voice notes to MP3 for WP8.1 (`off` disables) |
| `FFMPEG_PATH` | `ffmpeg` | the ffmpeg executable, when it is not on the PATH |

### Voice notes need ffmpeg

WhatsApp voice notes are Ogg with the Opus codec, and Windows Phone 8.1 has no Opus
decoder (Opus only arrived on Windows 10). The adapter therefore runs **ffmpeg**, if it
is installed, to convert a received voice note to a small mono MP3 before sending it to
the app. ffmpeg is an external program on the machine running the adapter, not a
dependency of the adapter. Without it the adapter logs a warning at startup and forwards
the original bytes, which the phone cannot play; the voice note still arrives and shows
that it cannot be played.

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
