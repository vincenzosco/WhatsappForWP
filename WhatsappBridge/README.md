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
- The adapter binds to the GOWA device that is **logged in**, not to the first one in
  `/devices`: the list is in creation order, so a server that has accumulated devices
  would otherwise report `disconnected` for an account that is in fact linked. The
  WhatsApp session itself lives in the `/data` volume, so it survives a restart and a
  container rebuild. When it is gone, the app asks for `login.qr` by itself and the
  QR is scanned again from the phone.
- The adapter broadcasts a discovery beacon on the LAN, so the app finds it without being
  configured with an address.
- `message.revoked` and `message.edited` are forwarded to the app as `revoked` and
  `edited` control frames, so a message deleted or changed on the phone does not stay
  frozen in the app. `message.reaction` is ignored: there is nowhere to draw it.
- Opening a chat asks for its stored messages (`messages`), and the adapter answers
  with ordinary message frames marked `IsHistory`. The app inserts them in date order
  and keeps them out of the unread count and the toasts: they are not arriving now. A
  message whose media is not among the bytes the webhook delivered is sent as text -
  `[Image]`, `[Video]`, ... - because an empty bubble is worse than a word. A voice
  note is the exception: its word is `Audio`, unbracketed, because the adapter can
  measure its length. GOWA sends no duration anywhere, so the chat-list preview
  downloads the last message's bytes (the same `/message/:id/download` route the app
  uses) and reads the length out of the container - Ogg/Opus, MP4/M4A, MP3 - with no
  new dependency, then keeps it per message id. The row reads `Audio 0:10`; when the
  bytes cannot be fetched it stays `Audio`.
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
  A document is the fourth case and it has its own route already: the picker on the
  phone now offers PDFs and office files, the app declares the file name and the real
  MIME type, and the adapter sends it with `/send/file`. Voice notes are played inside
  their bubble: the phone asks the adapter for the bytes, the adapter hands back an MP3
  (see below), and the app draws play/pause and a bar without leaving the conversation.
- A video is shrunk before it travels. The phone does it first, while the bubble says
  "sending"; when it could not (no transcoder for that file, no space, the platform
  refusing) the adapter does it with ffmpeg before handing the video to GOWA, so what
  reaches WhatsApp is never the whole camera file. The adapter leaves a video under 4 MB
  alone - the conversion would cost more than it saves - and a conversion that came out
  bigger is thrown away. With `FFMPEG_ENABLED=off` the adapter does not touch it; the
  phone still shrinks its own videos.
- Typing indicators exist in both directions, and they are the only piece of presence this
  project shows. A contact who starts or stops writing arrives as a `chat_presence`
  webhook event (GOWA 9.5), which the adapter turns into a `typing` control frame; the
  app draws it as a bubble with three dots and takes it away on `paused`. While you
  write, the app sends `typing` the other way and the adapter passes it on as
  `/send/chat-presence`. Nothing is invented: the state on screen comes from WhatsApp, and
  the bubble goes away by itself when the message it was waiting for arrives.
- The account is online exactly while a phone is watching it. WhatsApp sends typing
  notifications only to a client that is marked online, and GOWA connects as
  `unavailable` with a five-minute pulse once a day, so the adapter marks the account
  `available` when the first app client connects and `unavailable` when the last one
  leaves (`POST /send/presence`). That is also what the contacts see: online while the app
  is in use, offline when it is not. Going to the background counts as leaving: WP8.1
  freezes the process without closing the socket, so the app sends a `presence` control
  frame - `paused` while it suspends, `active` when it comes back - and the adapter stops
  counting that phone until it returns. That is what turns the account into a last access
  time instead of leaving it online for a frozen phone.
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
| app -> adapter | `typing` | `Text` = chat JID, `State` = `composing` or `paused` (what the contact sees while you write) |
| app -> adapter | `diag` | `Text` = the phone's own report, one line per newline (the adapter writes every line to its log as `[DIAG] ...`, so the app's side of a run is readable from the container) |
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
| adapter -> app | `typing` | `ChatId`, `State` = `composing` or `paused` (someone is writing in that chat) |
| adapter -> app | `media` | `ChatId`, `RelatedMessageId`, `MediaData` = one base64 piece, `MediaMimeType`, `MediaFileName`, `MediaType`, `MediaChunkIndex`, `MediaChunkTotal`: a piece of a message the app already has |
| adapter -> app | `error` | `Text` |

One frame is `[4-byte little-endian length][payload]`. A length of `0`, or one
above `MAX_FRAME_LENGTH` (8 MiB, exported from `server.js` and equal to
`FrameCodec.MaxFrameLength` in the app), is treated as a fault: the
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
| `WEBHOOK_SECRET` | `secret` | Must match GOWA's `--webhook-secret`; with no secret the webhook refuses every request instead of trusting it |
| `BRIDGE_KEY` | `WhatsAppCommunityWP8-2026` | The frame cipher key. It must match `CryptoHelper.cs`, or the key typed in the app (see below). The default is compiled into the public app, so it is not a secret |
| `BRIDGE_REQUIRE_KEY` | `off` | refuse to start while the cipher still uses the compiled default (`on` for any reachable deployment) |
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
| `AUTH_REQUIRED` | `off` | require a token in `hello` (`on` for a shared service) |
| `AUTH_REGISTER` | `on` | a phone that arrives without a token is given one on its first connection (`off` closes the service: the tokens are handed out by hand) |
| `AUTH_STRICT_DEVICE` | `off` | a device the store already knows must present a verifying token; the device id alone is not enough (`on` closes the device-id impersonation path, at the cost of the reinstall-keeps-your-account behaviour) |
| `AUTH_MAX_USERS` | `50` | ceiling on the devices that can register themselves |
| `USERS_FILE` | — | where the users live; empty keeps them in memory, a path survives a restart |

The token is the only thing that distinguishes a phone on a shared service, and a
phone that has none is given one: with `AUTH_REGISTER=on` (the default) the adapter
creates the user on the first handshake and answers with a `registered` frame
carrying the token, which the app keeps. The switch on the connection page is the
whole configuration - the token identifies the device, it is not a password to be
typed. Creating a user by hand is still there for a service that must stay closed,
with `AUTH_REGISTER=off`:

The token is **derived from the device**, not drawn at random: it is
`HMAC-SHA256(secret, deviceId)` with a secret the store generates once and keeps
in `USERS_FILE`. The app presents one device id (`SenderId` in `hello`) and it
outlives the install: it is the package-specific hardware token
(`HardwareIdentification.GetPackageSpecificToken`), the same on the same phone for
the same package, with the stored value as a cache and a random id only as the
fallback of a phone that will not answer. A phone that reconnects - or that
reinstalls the app - is therefore the same user with the same token, and the users
file does not grow one row per connection or per reinstall. (An earlier version
used a random id kept in `LocalSettings`, which WP8.1 deletes on uninstall: a
reinstall became a new device, which is why the WhatsApp login had to be redone.)
A device the service has never seen is still given a new one; a device it knows is
handed its own token back and told nothing, because it already has it. `AUTH_MAX_USERS` counts devices, and
re-registering a known device never consumes a slot. The secret is a credential:
a copy of the file can derive every device's token, which is the trade-off this
store makes for a token that stays the same. `AUTH_STRICT_DEVICE=on` removes the
trade-off for the device id: a device the store already knows must bring its
token, so a phone that reinstalls and lost it is refused and needs a new token
from `create-user.js`, instead of being handed the account back by whoever knows
the device id.

The frame cipher key can also be set in the app: the settings page has a *Server
key* field, and a phone that fills it uses that value instead of the compiled
default. A deployment that sets its own `BRIDGE_KEY` and `BRIDGE_REQUIRE_KEY=on`
is reached by typing the same value there; with the field empty the compiled
default is used, which is what a private server that never set `BRIDGE_KEY`
expects.

```bash
node create-user.js vincenzo            # prints id, name and the token, once
# inside the container:
docker exec whatsapp-for-wp8 node /opt/adapter/create-user.js vincenzo
```

Every user gets a GOWA device of their own, created on the first handshake, and
every command, cache and webhook is scoped to it: a message for one device never
reaches another user's socket. With `AUTH_REQUIRED=off` the adapter behaves as
before, with a single account and no token. The webhook routing uses the
top-level `device_id` GOWA puts on every event. Tokens are kept only as scrypt
hashes; a lost token is replaced, not recovered, and a phone that loses one and
comes back is simply registered again as a new device.

### Voice notes need ffmpeg

WhatsApp voice notes are Ogg with the Opus codec, and Windows Phone 8.1 has no Opus
decoder (Opus only arrived on Windows 10). The adapter therefore runs **ffmpeg**, if it
is installed, to convert a received voice note to a small mono MP3 before sending it to
the app. ffmpeg is an external program on the machine running the adapter, not a
dependency of the adapter. Without it the adapter logs a warning at startup and forwards
the original bytes, which the phone cannot play; the voice note still arrives and shows
that it cannot be played.

The other direction needs no ffmpeg. A recorded voice note arrives from the app
as an M4A/AAC payload; `sendMediaToGowa` gives an `audio` payload to
`session.gowa.sendAudio`, which posts it to `POST /send/audio` (form field
`audio`). That route is what makes GOWA send a WhatsApp voice note rather than a
file with an audio MIME type. An adapter built against a GOWA without the route
falls back to `POST /send/file`.

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
