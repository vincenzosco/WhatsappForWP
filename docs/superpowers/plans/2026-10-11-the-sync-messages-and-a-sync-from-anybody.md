# The sync's messages, and a sync from anybody Implementation Plan

> **Status: run.** This file is the record of a defect hunt and of the fix, in the shape the
> plans of this repository take. It was written while the fix was made, not before it.

**Spec:** the operator's request, in their words: *"cerca altri difetti latenti come il doppio
open, a partire dai percorsi media e history"*. The hunt started where the last plan left off -
the history behind a media download, and the media path it shares its keys with - and looked for
the same class of failure the double `Open` was: a value whose *representation* is not what the
next function assumes it is, and a step applied twice.

**Authority:** the reference checked out on this machine, `/tmp/m4ref` (Baileys). Every claim
below names the file and the function it was read from, because a hunt that ends in "probably"
is a hunt that found nothing.

## What the hunt looked for, and what it cleared

- **A second open of an already-open blob.** The four points where bytes cross a boundary were
  read against their contracts: `MediaDownload.FetchAsync` answers *plaintext* and every caller
  treats it so (`NativeConnection.ReadMediaAsync`, `AnnounceStatusAsync`, `DownloadMediaAsync`,
  `HistoryBlob.FetchAsync`); `MediaDownload.Open` is called with a *wire body*
  (`ciphertext || mac`) by `HistoryBlob.Open`, by the tests, and by `MediaUploadTests` on what
  the uploader POSTed; `HistorySync.Decode` takes *decompressed* bytes and all three callers
  (`NativeConnection.ApplyHistoryAsync` inline and downloaded, the fixtures) inflate first;
  `IncomingMediaStore` takes plaintext, and both callers hand it `MediaArrival.Bytes`. No second
  open was left.
- **A check applied to the wrong representation.** `MediaDownload.Open` checks the MAC before
  the decrypt, then `fileSha256`/`fileEncSha256`/`fileLength`; for a history blob those three
  describe the **compressed** payload, which is what the checks are given, and the inflate comes
  after them (`HistoryBlob`). Confirmed from the reference's own upload half
  (`Utils/messages-media.js:309-383`, `encryptedStream`: `fileSha256` over the plaintext,
  `fileEncSha256` over `ciphertext || mac`, `fileLength` over the plaintext) and its download
  half (`:416-520`, which never feeds the ten-byte MAC to the cipher).
- **The protobuf field numbers of everything the media and history paths read** were compared
  one by one against `WAProto/WAProto.proto` (`Message`, `ImageMessage`, `VideoMessage`,
  `AudioMessage`, `DocumentMessage`, `ProtocolMessage`, `HistorySync`, `Conversation`,
  `HistorySyncMsg`, `WebMessageInfo`, `MessageKey`, `CallLogRecord`, `HistorySyncNotification`).
  Every number the app uses is the reference's, `initialHistBootstrapInlinePayload` = 11
  included.
- **The two `Zlib` shapes** (`0x78` header skipped, trailer ignored) are the two the reference
  produces (`zlib.deflateSync` for the bootstrap: `Utils/history.js:120-126`), and the header
  test only ever runs on those two.

## Finding 1 (fixed): the sync's messages never reached the conversations

**Symptom.** After a pairing the chat list filled (the previous plan's fix) and **every
conversation opened empty**, with the page waiting up to `HistoryWaitMaxMilliseconds = 20000`
for a burst that never closed and then logging `history arrived empty for <chat>`.

**Why.** `NativeConnection.ApplySync` raised `ChatArrived` per conversation and nothing else,
and the only consumer of a conversation's messages was the chat row's preview
(`DataService.cs:370`, `row.Messages[last].Text`). So `DataService` never saw a history message:
nothing went through its `IsHistory` branch (`AddHistoryMessage`, which is also what fills
`_historyArrived` and so what raises `HistoryCompleted` per chat). The app's own contract says
otherwise in three places: `README.md:419` ("a chat shows what the last sync carried"),
`ChatPage`'s wait-and-scroll machinery, and `maintain-the-app`'s "the rows go through the same
path a live message does, marked `IsHistory`".

It is also a media-path defect: a photo in the history can never be fetched, because the bubble
whose tap asks for its bytes does not exist.

**Fix.** The messages travel, and only the ones this client can draw:
`NativeChats.ApplyHistory` answers the messages it actually added (dedupe applied) with
`IsHistory` set, and `NativeConnection.ApplySync` raises `MessageArrived` for each of them after
the rows and before the burst closes. `DataService.OnNativeMessage` copies the flag. A message
whose payload is a sticker, a reaction or anything else this client does not draw is kept in the
row but not handed over (`NativeChats.Shows`), which is the same decision the live path makes in
`NativeConnection.ReadMediaAsync`.

## Finding 2 (fixed): a history sync from anybody was applied

**Symptom.** None on a healthy account. Reachable by any contact who sends a message carrying a
`protocolMessage.historySyncNotification`: the app fetched, decoded and applied it - rows, call
log, status feed - because the branch ran before `IsOurEcho` and asked nothing about the sender.

**Why it matters.** `historySyncNotification` is a *self-only* protocol message. The reference
marks it `fromMe` and drops it otherwise: *"an attacker could otherwise spoof any of these to
manipulate local state"* (`Utils/process-message.js:207-237`, `SELF_ONLY_TYPES`, mirroring
`whatsmeow`'s `handleProtocolMessage`). The shape is documented in the same checkout
(`Utils/decode-wa-message.js:129-133`: "Peer-routed self stanzas (history sync, app-state sync,
etc.) arrive with `from` set to our own device"), which is also what makes the guard safe: a
legitimate sync is always one of this account's own JIDs.

**Fix.** `ReadMessageAsync` applies a sync only when `IncomingMessage.IsMine(incoming.From,
account)` answers - the same check that decides `IsOurEcho`, now public and documented - and
names the refusal in the log (`native: a history sync from ... is not this account's, so it is
left alone`).

## What pins them

- `ProtocolTests/NativeChatsTests.cs`: the chunk answers the messages the app draws, each marked
  `IsHistory`; a chunk delivered twice hands nothing over the second time; a message with nothing
  to draw stays in the row and out of the answer.
- `ProtocolTests/IncomingMessageTests.cs`: `IsMine` for the number, the LID, another device of
  the account, and a contact (245 protocol tests).
- `tools/check-history-sync.js` (+ its four cases in `tools/test/check-history-sync.test.js`):
  the three app-side cables no protocol test can see - the answer of `ApplyHistory` is kept, it
  is raised as `MessageArrived`, `DataService` copies `incoming.IsHistory`, and the sync is
  applied only behind `IncomingMessage.IsMine`.

## Not fixed, named

- `ChatPage.SendAttachmentAsync` sends **any** audio attachment as a voice note (`kind ==
  "audio"` → `ptt`) and passes `seconds = 0`: a picked track arrives at the receiver as a voice
  note with no duration. The reference sets `ptt` only for a recorded note and takes the seconds
  from the file.
- A bubble read back from `MessageCache` has no `Media` (the keys are deliberately not a
  `[DataMember]`), so a tap on a cached video answers "arrived without keys" instead of
  downloading it again - which is what the cache's own comment promises.
- The history's messages are drawn, but a chat is still not fetched on demand: opening one shows
  the sync plus what has arrived since (`README.md:419`).

## What is not verified here

The device run. The two fixes are visible only on a phone: the conversations must show the
sync's messages within a minute of a pairing, the log must carry `native: N message(s) of the
sync are in the chats`, and opening a chat during the burst must bind it without the twenty
seconds. `test-the-app`'s on-device checklist item 13 is the one to read it against.
