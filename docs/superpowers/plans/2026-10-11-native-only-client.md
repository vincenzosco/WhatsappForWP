# The app runs only on its own client Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** every feature this app has today - text, chat list, receipts, typing, media,
avatars, groups, the call log, Status - works over the phone's own WhatsApp protocol
implementation, and `WhatsappBridge`/GOWA and every trace of it (services, settings keys,
the account screen's server rows, the docs) is gone.

**Architecture:** `WhatsappApp/Protocol/**` already pairs, logs in, holds the account, has
Signal sessions on disk, and encodes and decodes a text message end to end. This plan grows
it in one direction only: the protocol core keeps every *rule* (pure, compiled twice, tested
by `ProtocolTests`), the app keeps every *wiring* (`Services/**`, WinRT, the pages), and the
adapter's thirteen `SendControlAsync`/frame commands are replaced one by one by the node
that means the same thing. The adapter is deleted last, in one commit, once nothing calls it.

**Tech Stack:** C# 5 on Windows Phone 8.1 (app), plain .NET 4.5 for the desktop test host
`ProtocolTests` (run by `node tools/test-protocol.js`), Node guards under `tools/check-*.js`,
MSBuild 12 for the app and the solution.

**Spec:** there is no single design document; the request is the operator's, in their words:
*"continua ad implementare tutto baileys, esegui test e dopo avere finito esegui la skill
bug-hunter linea per linea e se compare qualcosa risolvi tutti i bug, RIMUOVI LA POSSIBILITÀ
SERVER, L'APP DEVE FUNZIONARE SOLO CON BAILIYS INTERNO"*, and they answered *"all of them,
construct them in a single plan and execute it"* when asked which features must keep working
natively (media, avatars, groups, calls log and Status were all four named). The design
authority for the protocol work is `docs/superpowers/plans/2026-10-10-native-whatsapp-client.md`
(M1-M6), for the Signal layer `docs/superpowers/plans/2026-10-10-signal-x3dh-and-the-double-ratchet.md`
(Task 6 is open and this plan executes it as its Task 9), and for the removal
`docs/superpowers/plans/2026-10-10-retire-the-server.md` (Tasks 6 and 7, executed here as
Tasks 11 and 12). The two references are checked out on this machine and every node shape in
this plan is read from them, never remembered:
`/tmp/m4ref/node_modules/@whiskeysockets/baileys/lib` (TypeScript) and `/tmp/ref/**` plus
`/tmp/ref/whatsmeow-main` (Go).

## Global Constraints

- **C# 5 only.** Under `WhatsappApp/Protocol/**`: no `System.Security.Cryptography`, no
  `Windows.*` type, no LINQ extension method without `using System.Linq;`.
  `node tools/check-csharp5.js` fails the build on any of the three and is never weakened.
- **The core compiles twice**: inside the app and inside `ProtocolTests` (net45, MSBuild 12).
  Anything the core cannot own is injected, never reached for: `IRandomSource`, `ILog`, and
  - new in this plan - `IMediaFetcher` for HTTP.
- **Every new `.cs`/`.xaml` under `WhatsappApp` is listed in `WhatsappApp/WhatsApp.csproj`**
  (`node tools/check-project-files.js`), and the app build is not optional for a protocol
  task: a green `test-protocol` is not a green app build.
- **Every user-visible string** is an `x:Uid`/`Loc.Get` key present in **both**
  `Strings/en-US/Resources.resw` and `Strings/it-IT/Resources.resw`
  (`node tools/check-resw.js --strict`).
- **A call fired without `await` goes through `Guarded.RunGuardedAsync`**
  (`node tools/check-fire-and-forget.js`).
- **The version announced to the server stays `2.3000.1049558099`**; a companion older than
  the floor answers `<failure reason="405" location="frc">`.
- **Nothing of the user's leaves over an unverified channel**: the certificate chain is
  verified before any payload, including the media upload (HTTPS to `mmg.whatsapp.net`, whose
  URL is signed into the message by the server).
- **Docs come in pairs** (`README.md`/`README.it.md`, and the pairs `tools/check-docs.js`
  lists): same headings, same order, no emoji, `## Disclosure` last where marked. Plans under
  `docs/superpowers/plans/` are records and are not rewritten.
- **The gates are** `node tools/test-protocol.js`, `node tools/check-csharp5.js`, the
  `tools/check-*.js` guards (fourteen today; fifteen once Task 12 adds `check-no-server.js`),
  `node --test "tools/test/**/*.test.js"`, and
  `MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/Bin/MSBuild.exe" WhatsappApp.sln -t:Rebuild -p:Configuration=Debug -p:Platform=x86 -nologo -v:minimal`
  reporting `0 Error(s)` (and the same for `Platform=ARM`).
- **Do not commit unrelated working-tree changes**: `WhatsappApp/Services/ConversationView.cs`,
  `WhatsappApp/Pages/ChatPage.xaml.cs`, `README.md`, `README.it.md` and
  `.agents/skills/test-the-app/SKILL.md` carry the earlier scroll work; it is committed in
  Task 1 or left alone, and never mixed into a task that is about the protocol.
- **The phone is the authority.** A step that cannot be run on the Lumia is written as not
  run, never as passed.

## Review Focus

Five inputs this plan will meet that no task constructs on purpose. Each line names the task
whose tests pin it.

1. **An install upgraded over the server build**: it still has `ServerAddress`,
   `ServerPort` and `UsePublicServer` in `ApplicationData`, a `ChatCache` written by the
   adapter, and a user who never sees a QR. The app must start, ignore the stale keys, and
   show the cached chats until the native client answers. Task 5, Task 6, Task 12.
2. **A stored account WhatsApp has already revoked** (unlinked from another phone): the app
   must clear it, set `SessionState.Expired`, and land on the QR with the expired sentence -
   never on a chat list that can never fill. Task 2.
3. **A media transfer that dies half-way** (the phone loses Wi-Fi during a 4 MB video): the
   bubble stops spinning with a named sentence and no half-decrypted file is ever shown.
   Task 7, Task 8.
4. **A message for a group we have left, or a group member whose sender key never arrived**:
   the send fails by name and the bubble goes to `MessageStatus.Failed`; it is never
   silently swallowed and never shown as sent. Task 9.
5. **A status/broadcast message from someone not  in the chat list**, and a group whose
   subject changes while the app is open: neither may create a chat row or a duplicate
   contact. Task 9, Task 11.

## File Structure

    WhatsappApp/Protocol/Client/            the protocol core: rules, portable, tested by ProtocolTests
      ConnectionPolicy.cs                   backoff, the account-is-gone rule, the pre-key floor, staleness
      InboundRouter.cs                      a node -> what it is (one classifier, every tag)
      NativeChats.cs                        history/incoming -> ordered, de-duplicated chat input
      ConversationModel.cs                  the plain conversation/message shape the app reads
      HistorySync.cs                        the decrypted history payload -> conversations
      MediaConn.cs                          the <iq xmlns="w:m"> media_conn query and its answer
      GroupDirectory.cs                     group metadata query, parse, and the per-device fan-out
      ProfilePictures.cs                    the w:profile:picture query, its answer, and setting one
      Calls.cs                              the call log from history, and a call rejection
      StatusFeed.cs                         status@broadcast: the list query, the view receipt
    WhatsappApp/Protocol/WaProto/
      HistorySync.cs  HistorySyncNotification.cs
      Message.cs                            gains the four media sub-messages (Task 7)
    WhatsappApp/Protocol/Media/             new directory: encrypted media, both directions
      MediaKeys.cs                          mediaKey -> iv/cipherKey/macKey (HKDF)
      MediaDownload.cs                      fetch, decrypt, verify fileSha256/fileEncSha256
      MediaUpload.cs                        encrypt, upload, and the fields the receiver needs
    WhatsappApp/Protocol/Signal/
      SenderKeyRecord.cs  SenderKeys.cs      the group path libsignal calls sender keys
    WhatsappApp/Protocol/Transport/IMediaFetcher.cs   HTTP, injected like ITransport
    WhatsappApp/Services/
      NativeConnection.cs                   the account, the loop, and every send
      DataService.cs                        the same page surface, fed by the client above
      WinRtMediaFetcher.cs                  the app's IMediaFetcher
      AvatarCache.cs  IncomingMediaStore.cs  same disk format, native source
    ProtocolTests/                          one test file per core file above, plus Vectors/
      Vectors/HistoryFixture.cs             the reference's own bytes for a history bootstrap
      ConnectionPolicyTests.cs InboundRouterTests.cs HistorySyncTests.cs NativeChatsTests.cs
      SessionSendTests.cs MediaKeysTests.cs MediaDownloadTests.cs MediaConnTests.cs
      MediaUploadTests.cs SenderKeyTests.cs GroupDirectoryTests.cs ProfilePictureTests.cs
      CallsTests.cs StatusFeedTests.cs
    tools/check-no-server.js  tools/test/check-no-server.test.js   the guard that keeps the server out

Deleted by Task 12, not before: `CommunicationService.cs`, `DataService`'s control-frame
handlers, `FrameCodec.cs`, `CryptoHelper.cs`, `EndpointService.cs`, `DiscoveryService.cs`,
`ServerPinger.cs`, `AutoConnector.cs`, `ConnectionWatchdog.cs`, `SelfCheck.cs`,
`Models/DiscoveredServer.cs`, the `Server.` keys in `SettingsService`, and the adapter
instructions in the README pair and `.agents/skills/**`.

---

### Task 1: The tree this plan starts from

The working tree carries the in-flight native work: `WhatsappApp/Services/NativeConnection.cs`,
`WhatsappApp/Protocol/Client/NativeSession.cs`, `ProtocolTests/NativeSessionTests.cs`, the
rewritten `Pages/ConnectionPage.xaml(.cs)` and `Pages/PairPage.xaml(.cs)`, the keep-alive and
ping-answer changes in `Protocol/Client/SessionKeepAlive.cs` / `PairingSession.cs`, the
framing fixes in `Protocol/Transport/WebSocketTransport.cs` and `Protocol/WABinary/NodeReader.cs`,
`Protocol/Signal/SessionRecord.cs`, `Services/FileSessionStorage.cs`,
`WhatsappApp/Package.appxmanifest`, both `.resw` files, `WhatsappApp/WhatsApp.csproj` and the
two plan documents. Every later task builds on it, so it is committed first, as it is.

**Files:**
- Modify: `ProtocolTests/SessionKeepAliveTests.cs`, `ProtocolTests/SignalSessionTests.cs`,
  `ProtocolTests/WABinaryTests.cs`, `docs/superpowers/plans/2026-10-10-signal-x3dh-and-the-double-ratchet.md`
- Add: `WhatsappApp/Protocol/Client/NativeSession.cs`, `WhatsappApp/Services/NativeConnection.cs`,
  `ProtocolTests/NativeSessionTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: a clean tree, and the one member this plan names as its own starting point:
  `NativeConnection.Instance` with `void Start()`, `bool IsConnected`, `string Status`, and
  `NativeSession` with `Task<BinaryNode> LoginAsync(Credentials, string)`,
  `Task<BinaryNode> ReadNodeAsync()`, `Task SendAsync(BinaryNode)`, `Task PingAsync()`,
  `Task AnswerProbeAsync(BinaryNode)`, `Task GoAvailableAsync(string)`, `void Close()`,
  `bool IsOpen`, `static string Refusal(BinaryNode)`.

- [ ] **Step 1: Run the gates the in-flight work has to pass anyway**

Run: `node tools/test-protocol.js`, `node tools/check-csharp5.js`,
`node --test "tools/test/**/*.test.js"`, then the x86 `MSBuild` line from the Global
Constraints. Expected: `OK:`/`exit 0` from each and `0 Error(s)`.

- [ ] **Step 2: Commit the native work, and nothing else**

```bash
git add WhatsappApp/Protocol/Client/NativeSession.cs WhatsappApp/Services/NativeConnection.cs \
        ProtocolTests/NativeSessionTests.cs ProtocolTests/SessionKeepAliveTests.cs \
        ProtocolTests/SignalSessionTests.cs ProtocolTests/WABinaryTests.cs \
        WhatsappApp/Protocol WhatsappApp/Pages/ConnectionPage.xaml WhatsappApp/Pages/ConnectionPage.xaml.cs \
        WhatsappApp/Pages/PairPage.xaml WhatsappApp/Pages/PairPage.xaml.cs \
        WhatsappApp/Services/FileSessionStorage.cs WhatsappApp/Package.appxmanifest \
        WhatsappApp/Strings WhatsappApp/WhatsApp.csproj \
        docs/superpowers/plans/2026-10-10-signal-x3dh-and-the-double-ratchet.md
git commit -m "feat: the account is opened again after a pairing, and the connection is kept"
```

Leave `ConversationView.cs`, `Pages/ChatPage.xaml.cs`, `README.md`, `README.it.md` and
`.agents/skills/test-the-app/SKILL.md` unstaged: they are the earlier scroll work.

---

### Task 2: Every decision the session loop makes, as a rule with a test

`Services/NativeConnection.cs` decides five things inline today: how long to wait before
reconnecting, what ends the account, when to publish more one-time pre-keys, whether a node
is the liveness probe, and whether a silence means the connection is dead. A decision inside
a `while (true)` that only a phone can exercise is a decision nobody tests.

**Files:**
- Create: `WhatsappApp/Protocol/Client/ConnectionPolicy.cs`
- Create: `ProtocolTests/ConnectionPolicyTests.cs`
- Modify: `WhatsappApp/Services/NativeConnection.cs`, `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `NativeSession.Refusal`, `SessionKeepAlive.IsPing`, `SessionKeepAlive.IsStale`,
  `PreKeyUpload.NeedsUpload`, `PreKeyUpload.LowWaterMark`, `PreKeyStore.Remaining`.
- Produces: `static class ConnectionPolicy` with
  `static int ReconnectDelaySeconds(int attempt)` (1, 2, 4, 8, 15, 30, then 30),
  `static bool AccountIsGone(BinaryNode node)` (true for `failure`, and for `stream:error`
  with code `401`/`409`; a `stream:error` with any other code is a reconnect),
  `static bool NeedsPreKeys(int remaining)`, and
  `static bool ConnectionIsStale(DateTime lastNodeUtc, DateTime nowUtc)`.

- [ ] **Step 1: Read the reference for the code that ends an account**

Run: `grep -rn "401\|409\|ConnectFailure\|stream:error" /tmp/ref/whatsmeow-main/*.go /tmp/ref/whatsmeow-main/**/*.go | head -40`
and `grep -rn "DisconnectReason\|loggedOut\|connectionClosed" /tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Types/*.js | head -30`.
Record in the test file's comment which codes the two references treat as "the account is
gone" versus "reconnect", with the file and line each came from.

- [ ] **Step 2 (RED): the tests**

`ProtocolTests/ConnectionPolicyTests.cs`:
- `TheBackoffGrowsAndThenStops` - attempts 1..8 give `1, 2, 4, 8, 15, 30, 30, 30`, and a
  negative attempt is 1 rather than an exception.
- `ALoggedOutFailureEndsTheAccount` - `failure reason=401` is `AccountIsGone`; `stream:error
  code=515` is not; `success` is not.
- `ThePreKeyFloorIsTheConstant` - `NeedsPreKeys(LowWaterMark + 1)` false, `LowWaterMark` false,
  `LowWaterMark - 1` true.
- `ASilencePastTheIntervalIsStale` - reuses `SessionKeepAlive.IsStale`'s boundaries, asserted
  through `ConnectionIsStale` so the loop and the rule cannot disagree.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile, `ConnectionPolicy` not found.

- [ ] **Step 3 (GREEN): implement it, and put the loop on it**

`Services/NativeConnection.cs` becomes: one `while` that reads a node, `ConnectionPolicy`
answers what it is, and each branch is one call into `NativeSession`. The reconnect wraps the
loop instead of ending it, with `SessionState.Expired` set and `_store.Clear()` called *only*
when `AccountIsGone` said so.

- [ ] **Step 4: The tests pass, and the app builds**

Run: `node tools/test-protocol.js` (four new `pass` lines), `node tools/check-csharp5.js`,
then the x86 `MSBuild` line. Expected: exit 0 each, `0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Protocol/Client/ConnectionPolicy.cs ProtocolTests/ConnectionPolicyTests.cs \
        WhatsappApp/Services/NativeConnection.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: the session loop's decisions are rules, and the account survives a reconnect"
```

---

### Task 3: The node decides what it is - one classifier, tested shape by shape

Today the loop asks `SessionKeepAlive.IsPing` and `NativeSession.Refusal` and then ignores
everything else, which is why the log of the first device run ends at the login. WhatsApp's
traffic arrives as one stream of nodes with thirteen meanings.

**Files:**
- Create: `WhatsappApp/Protocol/Client/InboundRouter.cs`
- Create: `ProtocolTests/InboundRouterTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `BinaryNode`, `SessionKeepAlive.IsPing`, `NativeSession.Refusal`.
- Produces: `enum InboundKind { Probe, Refusal, Message, Receipt, ChatState, Presence,
  HistorySync, Call, MediaRetry, ServerSync, Ignored }` and
  `static class InboundRouter` with `static InboundKind Classify(BinaryNode node)` and
  `static string NotificationType(BinaryNode node)` (the `type` of an `ib`/`notification`
  child, or null).

- [ ] **Step 1: Read the shapes out of the reference**

Run: `grep -n "case '\|node.tag ===\|tag: '" /tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Utils/decode-wa-message.js /tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Socket/messages-recv.js | head -50`
Expected: the tags this classifier must know, with their file and line. Write them into the
test file as a comment; the classifier's rules come from there and not from memory.

- [ ] **Step 2 (RED): the tests**

`ProtocolTests/InboundRouterTests.cs`, one test per shape, each node built with
`NodeWriter`-free `BinaryNode` literals so it reads as the wire does:
- `EachTagBecomesItsOwnKind` - `message` -> `Message`, `receipt` -> `Receipt`,
  `chatstate` -> `ChatState`, `presence` -> `Presence`, `call` -> `Call`, `success` ->
  `ServerSync`, an `ib` with a `historySyncNotification` child -> `HistorySync`,
  an `ib` with a `media_retry` child -> `MediaRetry`,
  an empty `iq` on `w:p` -> `Probe`, `failure` -> `Refusal`, `iq` -> `Ignored`.
- `AnIbCarriesItsNotificationType` - `NotificationType` reads `encrypt`, `server_sync` and
  `picture` from the child; a node without one answers null.
- `AMessageForAnotherDeviceIsStillAMessage` - `to` is a device JID (`x:2@s.whatsapp.net`)
  and the kind is unchanged: the classifier answers what the node is, not who it is for.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile.

- [ ] **Step 3 (GREEN): implement `InboundRouter`**

A `switch` over `node.Tag` first, then over the notification child's `type`; unknown tags are
`Ignored` and named in `Diag` by the caller, not here.

- [ ] **Step 4: Run the tests**

Run: `node tools/test-protocol.js` - Expected: `OK: <n> protocol test(s)`, exit 0.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Protocol/Client/InboundRouter.cs ProtocolTests/InboundRouterTests.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: one classifier for the nodes the server sends"
```

---

### Task 4: History sync - the chat list and the backlog, natively

The chat list arrived as the adapter's `chats`/`chat`/`chats.done` frames, and a chat's
backlog as `messages`/`history.done`. WhatsApp sends the same thing itself: an `ib` with a
`historySyncNotification` whose payload is the `HistorySync` protobuf.

**Files:**
- Create: `WhatsappApp/Protocol/WaProto/HistorySync.cs`,
  `WhatsappApp/Protocol/WaProto/HistorySyncNotification.cs`
- Create: `ProtocolTests/Vectors/HistoryFixture.cs`, `ProtocolTests/HistorySyncTests.cs`
- Create: `WhatsappApp/Protocol/Client/ConversationModel.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `ProtoReader`/`ProtoWriter`, `Message.Decode` for the message blobs.
- Produces:
  - `HistorySyncNotification` with `byte[] MediaKey`, `string DirectPath`, `long FileLength`,
    `byte[] FileSha256`, `byte[] FileEncSha256`; `static HistorySyncNotification Decode(byte[])`.
  - `HistorySync` with `int SyncType`, `bool IsComplete`, `bool IsRecent`,
    `IList<HistoryConversation> Conversations`; `static HistorySync Decode(byte[])`.
  - `HistoryConversation` with `string Jid`, `string Name`, `long Timestamp`, `int UnreadCount`,
    `bool IsGroup`, `IList<HistoryMessage> Messages`, `bool Archived`.
  - `HistoryMessage` with `string Jid`, `string Id`, `string Text`, `bool IsFromMe`,
    `long Timestamp`, `byte[] Encoded` (the untouched `Message` blob, for the media of
    Task 7).
  - `static class HistorySyncReader` with `static HistorySync From(byte[] decryptedPayload)` -
    the fetch and the decryption of that payload are not here: they are the caller's, one
    line over Task 7's `MediaDownload` (Task 5's wiring).

- [ ] **Step 1: Print the reference's own bytes for a bootstrap payload**

Outside the repository, in `/tmp/m4ref`:

```bash
node -e "const p=require('@whiskeysockets/baileys').proto; \
 const s=p.HistorySync.encode({syncType:2, conversations:[{id:'393000000000@s.whatsapp.net',\
 name:'Marco',unreadCount:2,messages:[{message:{conversation:'ci sei?'},messageTimestamp:1791600000}]}]}); \
 console.log(Buffer.from(s.finish()).toString('hex'))"
```

Record the command and the hex in `ProtocolTests/Vectors/HistoryFixture.cs`, with the field
numbers read from `node_modules/@whiskeysockets/baileys/WAProto/index.d.ts`
(`HistorySync`, `Conversation`, `HistorySyncMsg`) quoted as a comment.

- [ ] **Step 2 (RED): the tests**

`ProtocolTests/HistorySyncTests.cs`:
- `TheReferenceBootstrapBecomesOneConversation` - the fixture decodes to one
  `HistoryConversation` with the JID, name, unread count and one message, oldest first.
- `TheNotificationCarriesTheMediaItIsHiddenBehind` - a `HistorySyncNotification` fixture
  decodes to the same mediaKey, directPath, fileLength and both hashes the reference
  encoded.
- `TwoConversationsKeepTheirOrder` - a fixture with two conversations keeps the order the
  reference encoded, which is the order the list shows.
- `AMessageWithNoTextIsStillAMessage` - a conversation whose message carries only an
  `extendedTextMessage` yields its body; one with neither is kept with `Text` null rather
  than dropped (Task 8 fills it from `Encoded`).
- `AGroupConversationIsMarkedAsOne` - a `@g.us` JID sets `IsGroup`.
- `AnUnknownFieldCostsNothing` - the fixture with a hand-appended field 99 still decodes.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile.

- [ ] **Step 3 (GREEN): implement the two protobufs and the reader**

`HistorySyncNotification.Decode` first (the `ib` child's payload is itself a protobuf), then
`HistorySync.Decode` over `ProtoReader`, then `ConversationModel` as the plain shape
`DataService` will read (Task 5). A message whose only payload is an image keeps its bytes in
`Encoded` and its `Text` null; Task 7 is what turns those bytes into a picture.

- [ ] **Step 4: Run the tests**

Run: `node tools/test-protocol.js` - Expected: exit 0, six new `pass` lines.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Protocol/WaProto/HistorySync.cs WhatsappApp/Protocol/WaProto/HistorySyncNotification.cs \
        WhatsappApp/Protocol/Client/ConversationModel.cs ProtocolTests/HistorySyncTests.cs \
        ProtocolTests/Vectors/HistoryFixture.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: the chat list and the backlog, read from WhatsApp's own history payload"
```

---

### Task 5: The store the pages already bind to

The pages bind `DataService.Contacts`, `GetMessages`, `AddMessage`, `BeginChatList` and
`SetMessageStatus`; that surface is not rewritten. What changes is where its input comes
from: `DataService` subscribes to the native client instead of `CommunicationService`, and
the adapter's control frames (`chat`, `chats.done`, `contact`, `messages`, `history.done`,
`revoked`, `edited`, `error`) lose their handlers.

**Files:**
- Modify: `WhatsappApp/Services/DataService.cs` (the input side; the list, unread, pin, mute,
  cache and preference behaviour is untouched)
- Modify: `WhatsappApp/Services/NativeConnection.cs` (the events it exposes)
- Create: `WhatsappApp/Protocol/Client/NativeChats.cs` (the pure rolling of an
  `InboundKind.Message`/`HistorySync` into ordered, de-duplicated chat input)
- Create: `ProtocolTests/NativeChatsTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `HistorySyncReader.From`, `HistorySyncNotification.Decode`,
  `MediaDownload.FetchAsync` (Task 7), `IncomingMessage.Read`, `HistoryMessage`,
  `HistoryConversation`, `DataService.ApplyChat` (its existing row path).
- Produces:
  - `NativeChats` with `void ApplyHistory(HistorySync sync)`, `bool AddIncoming(HistoryMessage)`
    (false when the id was already seen), `bool AddOutgoing(string jid, string id, string text,
    long timestamp)`, `IList<HistoryConversation> Rows` and `bool IsSyncComplete`.
  - `NativeConnection` events the app subscribes to:
    `event EventHandler<HistoryConversation> ChatArrived`,
    `event EventHandler<HistoryMessage> MessageArrived`,
    `event EventHandler<HistorySync> HistoryFinished`,
    `event EventHandler<string> ConnectionStatusChanged`.
  - `NativeConnection`'s history wiring: an `ib` whose notification is a history sync is
    fetched with `MediaDownload.FetchAsync` over the notification's mediaKey/directPath and
    handed to `HistorySyncReader.From`, which is the two lines that make the download and the
    decode the same moment.

- [ ] **Step 1 (RED): the tests**

`ProtocolTests/NativeChatsTests.cs`:
- `AHistorySyncBecomesRowsInItsOwnOrder` - two conversations in, `Rows` in the same order,
  each with its messages oldest first.
- `TheSameIdTwiceBecomesOneMessage` - the same `HistoryMessage` twice: the second
  `AddIncoming` answers false and `Rows` still holds one (Review Focus 5).
- `AnIncomingMessageForAnUnknownChatCreatesTheRow` - `AddIncoming` for a JID not in the
  history creates a `HistoryConversation` with `UnreadCount` 1.
- `AnOutgoingMessageDoesNotCountAsUnread` - `AddOutgoing` adds the row and leaves the count
  at whatever it was.
- `ASecondSyncDoesNotDuplicateTheRows` - applying the same fixture twice leaves `Rows` the
  size it was.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile.

- [ ] **Step 2 (GREEN): implement `NativeChats`, then point `DataService` at it**

`DataService.Start()` subscribes to `NativeConnection`'s four events; `OnNetworkMessageReceived`
keeps its body but is reached with a `ChatMessage` built from a `HistoryMessage` (one small
private `ToChatMessage`), and every adapter control `case` that has no native meaning is
deleted rather than left dormant. `ChatCache`/`MessageCache`/`ChatPreferences` are untouched:
the cache written by the adapter install is read exactly as before (Review Focus 1).

- [ ] **Step 3: The tests pass, the guards agree, the app builds**

Run: `node tools/test-protocol.js`, `node tools/check-fire-and-forget.js`,
`node tools/check-chat-list-source.js`, `node tools/check-memory.js`,
`node tools/check-project-files.js`, then the x86 `MSBuild` line.
Expected: exit 0 each, `0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Services/DataService.cs WhatsappApp/Services/NativeConnection.cs \
        WhatsappApp/Protocol/Client/NativeChats.cs ProtocolTests/NativeChatsTests.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: the chat list is filled by this phone's own client"
```

---

### Task 6: Sending, receipts, typing and read state, on the native path

`ChatPage` sends with `CommunicationService.Instance.SendMessageAsync`, marks read with
`SendControlAsync("read")`, types with `SendControlAsync("typing")`; `ChatsPage` asks for
`status`. Each has a WhatsApp node: `message`, `receipt`, `chatstate`, `presence`.

**Files:**
- Modify: `WhatsappApp/Services/NativeConnection.cs` (the four send members)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs`, `WhatsappApp/Pages/ChatsPage.xaml.cs`,
  `WhatsappApp/Services/DataService.cs` (the read/typing/status call sites only)
- Create: `ProtocolTests/SessionSendTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `OutgoingMessage.Node`, `IncomingMessage.Ack`/`Receipt`, `DeviceDirectory.Query`/
  `Bundles`/`Session`, `SessionKeepAlive.Presence`, the chatstate node shape from the
  reference.
- Produces: `NativeConnection` with `Task<string> SendTextAsync(string jid, string body)`
  (the message id), `Task MarkReadAsync(string jid, IList<string> ids)`,
  `Task SendTypingAsync(string jid, bool typing)`, `Task SendPresenceAsync(string state)`,
  and `event EventHandler<MessageStatusUpdate> MessageStatusChanged`, raised for a `receipt`
  node - `MessageStatusUpdate` is `string Jid`, `string Id`, `bool IsRead`, which is what
  `DataService.SetMessageStatus` needs and no more.

- [ ] **Step 1: Read the four node shapes out of the reference**

Run: `grep -n "chatstate\|type: 'chatstate'\|composing\|paused" /tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Socket/messages-recv.js /tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Socket/messages-send.js | head -20`
and the same file's `readMessages` / `sendReceipt` for the receipt's attribute set. Record
the shapes in `ProtocolTests/SessionSendTests.cs` as a comment with file and line.

- [ ] **Step 2 (RED): the tests**

`ProtocolTests/SessionSendTests.cs`:
- `TheTypingNodeIsTheOneTheReferenceBuilds` - tag `chatstate`, `to` the chat JID, and the
  composing/paused child the reference uses.
- `TheReadReceiptNamesEveryId` - tag `receipt`, `type` `read`, `to` the chat, and one `id`
  child per id in the order given.
- `AMessageNodeCarriesTheDevicesOfItsTarget` - for a group JID the node's `to` is the group
  and it carries one `enc` per participant device; for a person, one per device of that
  person (this is the shape Task 9's fan-out reuses).
- `AnAckIsBuiltForTheIdThatArrived` - `IncomingMessage.Ack(jid, id)` names both.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile.

- [ ] **Step 3 (GREEN): implement the four sends and re-point the pages**

`ChatPage.SendMessageAsync` (its own method, line ~1373) calls `SendTextAsync`; the "read"
call becomes `MarkReadAsync`; the typing control becomes `SendTypingAsync`; `ChatsPage`'s
`status` call becomes `SendPresenceAsync`. Each call site that was `SendControlAsync` for a
reason that no longer exists is deleted, not wrapped.

- [ ] **Step 4: The gate, and the phone**

Run: `node tools/test-protocol.js`, `node tools/check-fire-and-forget.js`, `node tools/check-resw.js --strict`,
then deploy the x86 package to the Lumia (`AppDeployCmd.exe /install` + `/launch`, see
`.agents/skills/release-the-app/SKILL.md`) and check, with the phone: a text arrives on the
contact's phone, `Sent` turns into `Delivered`, then `Read` when they open it, and their
reply appears in the chat with the typing indicator before it.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Services/NativeConnection.cs WhatsappApp/Pages/ChatPage.xaml.cs \
        WhatsappApp/Pages/ChatsPage.xaml.cs WhatsappApp/Services/DataService.cs \
        ProtocolTests/SessionSendTests.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: send, read, type and say we are here on the native path"
```

---

### Task 7: Media that arrives - download, decrypt, show

An incoming photo or voice note is an `ImageMessage`/`AudioMessage`/`VideoMessage`/
`DocumentMessage` whose bytes live behind `directPath` and a `mediaKey`. Today
`RequestMediaAsync` asks the adapter for pieces.

**Files:**
- Create: `WhatsappApp/Protocol/Media/MediaKeys.cs`, `WhatsappApp/Protocol/Media/MediaDownload.cs`
- Create: `WhatsappApp/Protocol/Transport/IMediaFetcher.cs`
- Create: `ProtocolTests/MediaKeysTests.cs`, `ProtocolTests/MediaDownloadTests.cs`
- Create: `WhatsappApp/Services/WinRtMediaFetcher.cs`
- Modify: `WhatsappApp/Protocol/WaProto/Message.cs` (the four media sub-messages),
  `WhatsappApp/Services/IncomingMediaStore.cs`, `WhatsappApp/Services/NativeConnection.cs`,
  `WhatsappApp/Pages/ChatPage.xaml.cs` (the `RequestMediaAsync` call), `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `Message.Decode` (the media fields, kept encoded by Task 8 of M4),
  `IMediaFetcher` (new), `Hkdf`, `AesGcm`, `Sha256`.
- Produces:
  - `interface IMediaFetcher { Task<byte[]> GetAsync(string url); Task<byte[]> PostAsync(string url,
    byte[] body, IList<KeyValuePair<string, string>> headers); }` - injected like `ITransport`.
  - `static class MediaKeys` with `static byte[] Expanded(byte[] mediaKey, string mediaType)`
    (HKDF, info `<type>`), `byte[] Iv`, `byte[] CipherKey`, `byte[] MacKey` off it.
  - `static class MediaDownload` with `Task<byte[]> FetchAsync(IMediaFetcher fetcher,
    byte[] mediaKey, string directPath, string url, byte[] expectedSha256, long fileLength)` -
    decrypts the AES-CBC stream, verifies `fileSha256` and `fileEncSha256`, and throws a
    `ProtocolException` naming which check failed.
  - `MediaMessage` (in `WaProto/Message.cs`): `decode`/`encode` for
    `ImageMessage`, `AudioMessage`, `VideoMessage`, `DocumentMessage`, exposing `MediaKey`,
    `DirectPath`, `Url`, `Mimetype`, `FileName`, `FileLength`, `FileSha256`, `FileEncSha256`,
    `MediaKeyTimestamp`, `JpegThumbnail`, `Seconds`, `Ptt`.

- [ ] **Step 1: Print the reference's own bytes for one media message and one key expansion**

In `/tmp/m4ref`:
```bash
node -e "const p=require('@whiskeysockets/baileys').proto; \
 console.log(Buffer.from(p.Message.encode({imageMessage:{url:'https://mmg.whatsapp.net/x',\
 mimetype:'image/jpeg',fileSha256:Buffer.alloc(32,1),fileEncSha256:Buffer.alloc(32,2),\
 mediaKey:Buffer.alloc(32,3),fileLength:1234,directPath:'/v/t62/x',mediaKeyTimestamp:1791600000}}).finish()).toString('hex'))"
```
and read the key schedule from
`/tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Utils/messages-media.js` (`hkdfInfoKey`,
`getMediaKeys`, the iv/cipherKey/macKey split) - quoted into `MediaKeysTests.cs` with file and
line. Record both in `ProtocolTests/Vectors/` as one fixture file.

- [ ] **Step 2 (RED): the tests**

`ProtocolTests/MediaKeysTests.cs`: `TheThreeKeysAreTheReferenceOnes` - a fixed mediaKey of
32 bytes gives the same iv/cipherKey/macKey as the reference printed (byte for byte), for
`image` and for `audio`.
`ProtocolTests/MediaDownloadTests.cs`:
- `AReferenceBlobDecryptsToItsPlaintext` - a small blob encrypted with the reference's own
  scheme comes back whole.
- `AMovedCiphertextByteIsANamedFailure` - the MAC fails, and the message says so.
- `AWrongSha256IsRefusedAfterDecrypt` - `fileSha256` mismatched throws, and the text names
  `fileSha256`.
- `AShortBodyIsRefusedBeforeDecrypt` - fewer bytes than `fileLength` throws without touching
  the decryptor (Review Focus 3).

Run: `node tools/test-protocol.js` - Expected: FAIL to compile.

- [ ] **Step 3 (GREEN): implement the keys, the download and the media protobufs**

`WinRtMediaFetcher` is the app's `IMediaFetcher` over
`Windows.Web.Http.HttpClient` (`GetAsync`/`PostAsync`), and `IncomingMediaStore` takes
`byte[]` from it instead of from the adapter's base64 pieces.

- [ ] **Step 4: The tests, the guards and the app build**

Run: `node tools/test-protocol.js`, `node tools/check-csharp5.js`, `node tools/check-attachment-file.js`,
then the x86 and ARM `MSBuild` lines. Expected: exit 0 each, `0 Error(s)` both platforms.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Protocol/Media WhatsappApp/Protocol/Transport/IMediaFetcher.cs \
        WhatsappApp/Services/WinRtMediaFetcher.cs WhatsappApp/Services/IncomingMediaStore.cs \
        WhatsappApp/Protocol/WaProto/Message.cs ProtocolTests/Vectors ProtocolTests/MediaKeysTests.cs \
        ProtocolTests/MediaDownloadTests.cs WhatsappApp/Services/NativeConnection.cs \
        WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: photos and voice notes arrive and are decrypted here"
```

---

### Task 8: Media that leaves - the upload the server accepts

Sending a photo needs the upload host and its token (`<iq xmlns="w:m">` with `media_conn`),
the encrypted POST, and the message fields the receiver needs to fetch it back.

**Files:**
- Create: `WhatsappApp/Protocol/Client/MediaConn.cs`, `WhatsappApp/Protocol/Media/MediaUpload.cs`
- Create: `ProtocolTests/MediaConnTests.cs`, `ProtocolTests/MediaUploadTests.cs`
- Modify: `WhatsappApp/Services/NativeConnection.cs`, `WhatsappApp/Pages/ChatPage.xaml.cs`
  (`SendMediaBeginAsync`/`Chunk`/`End` become one call), `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `IMediaFetcher`, `MediaKeys`, `Message`, `IRandomSource`, `Sha256`, `Hmac`.
- Produces:
  - `static class MediaConn` with `static BinaryNode Query()` (the `iq`), and
    `static MediaConnInfo Read(BinaryNode answer)` holding `string Auth`, `long MaxContentLength`,
    `IList<string> Hosts`.
  - `static class MediaUpload` with `Task<UploadedMedia> SendAsync(IMediaFetcher fetcher,
    MediaConnInfo conn, byte[] plaintext, string mediaType, string mimetype, IRandomSource random)`
    returning `UploadedMedia` with `byte[] MediaKey`, `string DirectPath`, `string Url`,
    `byte[] FileSha256`, `byte[] FileEncSha256`, `long FileLength`, `long MediaKeyTimestamp`,
    `byte[] JpegThumbnail`; and `static Message Image(UploadedMedia media, string caption)`
    (and `Audio`/`Video`/`Document`).

- [ ] **Step 1: Read the upload out of the reference, and record it**

Run: `sed -n '1,80p' /tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Utils/messages-media.js | grep -n "media_conn\|host\|auth" ; grep -n "media_conn\|w:m\|post\|/mms/" /tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Utils/messages-media.js /tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Socket/messages-send.js | head -30`
Expected: the `iq`'s namespace and child, the `POST /mms/<type>/<token>` path, the headers
(`Origin: https://web.whatsapp.com`, `Authorization: Bearer <auth>`), and the multipart body
shape. Quote each with file and line into `ProtocolTests/MediaConnTests.cs`.

- [ ] **Step 2 (RED): the tests**

`ProtocolTests/MediaConnTests.cs`:
- `TheQueryIsTheIqTheServerAnswers` - tag `iq`, `type` `set`, `xmlns` `w:m`, one `media_conn` child.
- `TheAnswerCarriesHostAuthAndLimit` - a fixture answer yields the host list, the token and
  the max size, and a missing `auth` throws a named `ProtocolException`.
`ProtocolTests/MediaUploadTests.cs`:
- `TheUploadedFieldsRoundTripThroughTheMessage` - `Image(uploaded, "ciao")` encodes, and the
  decoded `ImageMessage` carries the same `mediaKey`, `directPath`, `fileSha256`, `fileLength`
  and `mediaKeyTimestamp`, with the caption in `caption`.
- `TheEncryptedBodyDecryptsBackToTheFile` - the body a scripted `IMediaFetcher` received
  decrypts with `MediaDownload` to the original bytes.
- `AnOversizeFileIsRefusedBeforeUpload` - a body larger than `MaxContentLength` throws by name.
- `TheHashesAreTheOnesTheReceiverChecks` - `fileSha256` is over the plaintext,
  `fileEncSha256` over the ciphertext (both recomputed in the test from the reference's own
  definition).

Run: `node tools/test-protocol.js` - Expected: FAIL to compile.

- [ ] **Step 3 (GREEN): implement the connector and the upload**

`ChatPage`'s three-piece send (`SendMediaBeginAsync`, `SendMediaChunkAsync`,
`SendMediaEndAsync` and the picker path) becomes one `NativeConnection.SendMediaAsync(jid,
file, mime, caption)`; the adapter's chunking code and
`CommunicationService`'s transfer ids leave with it.

- [ ] **Step 4: The tests, the guards and the app build**

Run: `node tools/test-protocol.js`, `node tools/check-csharp5.js`, `node tools/check-fire-and-forget.js`,
`node tools/check-attachment-file.js`, then the x86 `MSBuild` line. Expected: exit 0, `0 Error(s)`.

- [ ] **Step 5: The phone**

Send a photo, a voice note and a PDF from the Lumia to a real contact; each arrives, and a
photo the contact sends back shows in the chat with its caption.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Protocol/Client/MediaConn.cs WhatsappApp/Protocol/Media/MediaUpload.cs \
        ProtocolTests/MediaConnTests.cs ProtocolTests/MediaUploadTests.cs \
        WhatsappApp/Services/NativeConnection.cs WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: a photo or a voice note leaves the phone through WhatsApp's own uploader"
```

---

### Task 9: Groups - sender keys, membership, and the fan-out

Groups are the one part of Signal this client still lacks: `libsignal`'s sender keys.
`WhatsappApp/Protocol/Signal/**` has no `SenderKey` type today (`grep -rn "SenderKey"
WhatsappApp/Protocol/Signal` is empty), and M3's Task 6 is open because of it. This task is
that task plus what a group chat needs around it: membership, the subject, and the send path.

**Files:**
- Create: `WhatsappApp/Protocol/Signal/SenderKeyRecord.cs`, `WhatsappApp/Protocol/Signal/SenderKeys.cs`
- Create: `WhatsappApp/Protocol/Client/GroupDirectory.cs`
- Create: `ProtocolTests/SenderKeyTests.cs`, `ProtocolTests/GroupDirectoryTests.cs`
- Modify: `WhatsappApp/Protocol/Signal/SessionStore.cs` (sender-key state on disk),
  `WhatsappApp/Protocol/Client/OutgoingMessage.cs` (the group send),
  `WhatsappApp/Protocol/Client/IncomingMessage.cs` (the `skmsg` shape),
  `WhatsappApp/Services/DataService.cs` (`IsGroup` rows, participant names), `WhatsappApp/WhatsApp.csproj`
- Modify: `docs/superpowers/plans/2026-10-10-signal-x3dh-and-the-double-ratchet.md`
  (Task 6's checkboxes: it ran here)

**Interfaces:**
- Consumes: `X3dh`, `Hkdf`, `Hmac`, `Cipher`, and the reference's own group primitives -
  `/tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Signal/Group/sender-key-record.js`,
  `sender-key-message.js`, `sender-chain-key.js`, `keyhelper.js` - which are read, not
  remembered.
- Produces:
  - `SenderKeyRecord` with `bool HasKey`, `SenderKeyDistributionMessage Distribution(string groupId)`,
    `byte[] Encrypt(string groupId, byte[] plaintext)`, `byte[] Decrypt(SenderKeyMessage message)`;
    `static SenderKeyRecord Create(string groupId, IRandomSource random)`,
    `static SenderKeyRecord StartAsReceiver(string groupId, byte[] distribution)`.
  - `static class SenderKeys` with `static byte[] DistributionMessage(string groupId,
    SenderKeyRecord record)` and `static byte[] Ciphertext(string groupId, SenderKeyRecord record,
    byte[] plaintext)` - the two `Message` fields (2 and 2's wrapper) the group path sets.
  - `static class GroupDirectory` with `static BinaryNode Query(string groupJid)`,
    `static GroupInfo Read(BinaryNode answer)` (`string Jid`, `string Subject`,
    `IList<string> Participants`, `string OwnerJid`), and
    `static IList<KeyValuePair<string, SessionRecord>> FanOut(GroupInfo group, SessionStore store,
    Credentials credentials)`.

- [ ] **Step 1: Read the sender-key scheme out of both references**

Run: `sed -n '1,120p' /tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Signal/Group/sender-key-record.js`
and `sed -n '1,60p' .../Group/sender-key-message.js`, `.../Group/keyhelper.js`. Record in
`SenderKeyTests.cs` as a comment: the record's serialized layout, the ciphertext message's
`version || protobuf(signature, iv, ciphertext)`, the signing key's derivation, and the info
strings (the M3 notes say the info strings are not in `libsignal-protocol`, so they come from
here or from `/tmp/ref/whatsmeow-main`).

- [ ] **Step 2 (RED): the tests**

`ProtocolTests/SenderKeyTests.cs`:
- `TwoMembersOfAGroupReadEachOther` - a record each, one `Distribution`, `StartAsReceiver` on
  the other side, then `Encrypt`/`Decrypt` gives the plaintext back.
- `AMovedCiphertextByteIsRefused` - the signature check fails by name.
- `ADistributionFromAnotherGroupIsRefused` (Review Focus 4) - a distribution minted for
  `g1` used on `g2` throws, naming both ids.
- `TheRecordSurvivesADiskRoundTrip` - serialize/parse keeps the chain.
`ProtocolTests/GroupDirectoryTests.cs`:
- `TheMetadataAnswerBecomesAParticipantList` - a fixture answer (built with `NodeWriter`, as
  `PeerBundleFixture` does) yields the subject, the owner and every participant.
- `TheFanOutIsOneSessionPerParticipantDevice` - two participants with one device each give
  two records, each named by its device JID.
- `AGroupWithAMemberWeCannotReachStillSends` (Review Focus 4) - a participant whose bundle
  has no one-time pre-key still gets an entry; the send is not abandoned because of one
  member.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile.

- [ ] **Step 3 (GREEN): implement the sender keys, the directory and the group send**

`OutgoingMessage.Node` gains its group branch: the wrapped message carries the
`senderKeyDistributionMessage`, one `skmsg` per participant device for members whose sender
key we hold, and one `enc` per device for the ones we do not.

- [ ] **Step 4: The receive side**

`IncomingMessage` learns the `skmsg` shape: `SenderKeyMessage` from a `senderKeyDistributionMessage`
in the same stanza, and the group text into `DataService` with its participant as `SenderName`.

- [ ] **Step 5: The tests, the guards and the app build**

Run: `node tools/test-protocol.js`, `node tools/check-csharp5.js`, `node tools/check-resw.js --strict`,
then the x86 `MSBuild` line. Expected: exit 0, `0 Error(s)`.

- [ ] **Step 6: The phone**

In a real group: the group appears with its subject and participants, a message sent from
the Lumia arrives in the group, and a message written by someone else shows with their name.

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Protocol/Signal/SenderKeyRecord.cs WhatsappApp/Protocol/Signal/SenderKeys.cs \
        WhatsappApp/Protocol/Signal/SessionStore.cs WhatsappApp/Protocol/Client/GroupDirectory.cs \
        WhatsappApp/Protocol/Client/OutgoingMessage.cs WhatsappApp/Protocol/Client/IncomingMessage.cs \
        WhatsappApp/Services/DataService.cs ProtocolTests/SenderKeyTests.cs \
        ProtocolTests/GroupDirectoryTests.cs docs/superpowers/plans/2026-10-10-signal-x3dh-and-the-double-ratchet.md \
        WhatsappApp/WhatsApp.csproj
git commit -m "feat: groups, over their own sender keys"
```

---

### Task 10: Avatars and profile pictures, from WhatsApp

`AvatarCache` is filled by the adapter today; `ContactInfoPage` asks for `contact.info`. The
picture is an `iq` on `w:profile:picture` (with a `type` of `image` or `preview`), and a
preview is small enough to keep for every row.

**Files:**
- Create: `WhatsappApp/Protocol/Client/ProfilePictures.cs`
- Create: `ProtocolTests/ProfilePictureTests.cs`
- Modify: `WhatsappApp/Services/AvatarCache.cs`, `WhatsappApp/Services/NativeConnection.cs`,
  `WhatsappApp/Pages/ContactInfoPage.xaml.cs`, `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `BinaryNode`, `JidUtils`.
- Produces: `static class ProfilePictures` with `static BinaryNode Query(string jid, string type)`
  (type `preview` for the list, `image` for the profile page),
  `static BinaryNode Encode(string jid, byte[] jpeg)` (setting our own), and
  `static byte[] Read(BinaryNode answer)` (the `picture` child's bytes, null when the server
  answers the empty `picture` node that means "no picture").

- [ ] **Step 1: Read the query out of the reference**

Run: `grep -n "w:profile:picture\|profilePictureUrl\|buildProfilePictureQueryContent" /tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Socket/chats.js | head -20`
and read the ten lines around it. Quote them, with file and line, into
`ProtocolTests/ProfilePictureTests.cs`.

- [ ] **Step 2 (RED): the tests**

`ProtocolTests/ProfilePictureTests.cs`:
- `TheQueryIsThePictureIq` - tag `iq`, `type` `get`, the xmlns, `to` the JID, one `picture`
  child with the type asked for.
- `AnEmptyPictureIsNotAPicture` - the server's "no picture" answer gives null, not an empty
  array and not an exception.
- `TheAnswerYieldsTheBytes` - a fixture answer with a `picture` child of three bytes gives
  those three bytes.
- `ASettingNodeCarriesTheImage` - `Encode(jid, bytes)` has the picture child with those bytes.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile.

- [ ] **Step 3 (GREEN): implement it, and fill `AvatarCache` from it**

`AvatarCache` keeps its disk format; its source becomes `NativeConnection.RequestAvatarAsync(jid)`,
which asks for a `preview` for list rows and an `image` on `ContactInfoPage`. `ContactInfoPage`'s
`contact.info` control call is deleted and replaced with that request.

- [ ] **Step 4: The tests, the guards and the app build**

Run: `node tools/test-protocol.js`, `node tools/check-memory.js`, `node tools/check-resw.js --strict`,
then the x86 `MSBuild` line. Expected: exit 0, `0 Error(s)`.

- [ ] **Step 5: The phone**

The chat list shows contact pictures without the adapter, and the contact page shows the
larger one; a contact with no picture shows initials, not an empty square.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Protocol/Client/ProfilePictures.cs ProtocolTests/ProfilePictureTests.cs \
        WhatsappApp/Services/AvatarCache.cs WhatsappApp/Services/NativeConnection.cs \
        WhatsappApp/Pages/ContactInfoPage.xaml.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: contact pictures are asked of WhatsApp itself"
```

---

### Task 11: The call log, and Status

`CallsPage` gets its rows from the adapter's `calls` scan and `call` control frame;
`StatusPage` shows the status feed the adapter builds. Both come from WhatsApp, and both are
read here.

**Files:**
- Create: `WhatsappApp/Protocol/Client/Calls.cs`, `WhatsappApp/Protocol/Client/StatusFeed.cs`
- Create: `ProtocolTests/CallsTests.cs`, `ProtocolTests/StatusFeedTests.cs`
- Modify: `WhatsappApp/Services/DataService.cs` (the `Calls` collection and its scan event),
  `WhatsappApp/Pages/CallsPage.xaml.cs`, `WhatsappApp/Pages/StatusPage.xaml.cs`,
  `WhatsappApp/Services/NativeConnection.cs`, `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `HistorySync` (`callLogRecords`), `IncomingMessage`, `Message.Decode`,
  `BroadcastList`/`JidUtils`.
- Produces:
  - `Calls` with `static IList<CallLogEntryModel> FromHistory(HistorySync sync)` and
    `static BinaryNode Reject(string callId, string jid)`; `CallLogEntryModel` with
    `string Id`, `string Jid`, `bool IsVideo`, `bool IsMissed`, `long Timestamp`, `int Seconds`.
  - `static class StatusFeed` with `static BinaryNode ListQuery(IList<string> jids)` (the
    `status@broadcast` list), `static bool IsStatus(string jid)`, and
    `static BinaryNode Viewed(string jid, string id)` (the view receipt).

- [ ] **Step 1: Read both shapes out of the reference**

Run: `grep -n "callLogRecords\|call_log\|w:call\|rejectCall" /tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Socket/messages-send.js /tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Types/Call.js /tmp/m4ref/node_modules/@whiskeysockets/baileys/WAProto/index.d.ts | head -30`
and `grep -rn "status@broadcast\|Broadcast" /tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Socket/chats.js | head -20`.
Quote what it says, with file and line, into the two test files; where a shape is genuinely
not in either reference, the plan says so and the test asserts the weakest true thing.

- [ ] **Step 2 (RED): the tests**

`ProtocolTests/CallsTests.cs`:
- `TheHistoryCallLogBecomesRows` - a fixture with two call log records gives two entries,
  newest first, with their `IsVideo`/`IsMissed` as the reference encodes them.
- `AMissedCallWithNoDurationSaysZero` - a missing duration is 0, not a crash and not a random.
- `AnEmptyCallLogIsAnEmptyList` - no records, no rows, no exception.
`ProtocolTests/StatusFeedTests.cs`:
- `AStatusJidIsRecognised` - `status@broadcast` (and a device JID of it) is a status;
  an ordinary JID is not (Review Focus 5).
- `TheListQueryAsksForEveryJid` - one `usync`/`list` entry per JID, in the order given.
- `AViewedReceiptNamesTheStatus` - the node it builds carries the id.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile.

- [ ] **Step 3 (GREEN): implement both, and point the two pages at them**

`CallsPage`'s `SendControlAsync("calls")` and `CallsScanCompleted` path becomes
`NativeConnection.ScanCallsAsync()`; `CallsPage`'s reject action becomes `Calls.Reject`.
`StatusPage` reads status messages through `StatusFeed` into its existing row model, and
`DataService` must not create a chat row for a `status@broadcast` message (Review Focus 5).

- [ ] **Step 4: The tests, the guards and the app build**

Run: `node tools/test-protocol.js`, `node tools/check-resw.js --strict`, `node tools/check-memory.js`,
then the x86 `MSBuild` line. Expected: exit 0, `0 Error(s)`.

- [ ] **Step 5: The phone**

The call log lists real calls (including ones made while the phone was off), and Status shows
a status a contact posted, playable, with its view receipt arriving on their phone.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Protocol/Client/Calls.cs WhatsappApp/Protocol/Client/StatusFeed.cs \
        ProtocolTests/CallsTests.cs ProtocolTests/StatusFeedTests.cs WhatsappApp/Services/DataService.cs \
        WhatsappApp/Services/NativeConnection.cs WhatsappApp/Pages/CallsPage.xaml.cs \
        WhatsappApp/Pages/StatusPage.xaml.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: the call log and Status come from WhatsApp itself"
```

---

### Task 12: The switch - the adapter leaves, and a guard keeps it out

Nothing calls the adapter any more after Tasks 5-11. This is `retire-the-server` Tasks 6 and
7 in one commit: the services, the settings keys, the account screen's server rows, the
guard, the READMEs and the skills.

**Files:**
- Delete: `WhatsappApp/Services/CommunicationService.cs`, `FrameCodec.cs`, `CryptoHelper.cs`,
  `EndpointService.cs`, `DiscoveryService.cs`, `ServerPinger.cs`, `AutoConnector.cs`,
  `ConnectionWatchdog.cs`, `SelfCheck.cs`, `Models/DiscoveredServer.cs` (ten files)
- Modify: `WhatsappApp/Services/SettingsService.cs` (every `Server.`-shaped key, the address
  and port defaults, `UsePublicServer`, `HasSavedSettings`), `WhatsappApp/App.xaml.cs`,
  `WhatsappApp/Pages/DiagnosticsPage.xaml.cs`, `WhatsappApp/Services/CrashReport.cs`,
  `WhatsappApp/Services/MemoryWatcher.cs`, `WhatsappApp/Services/SessionService.cs` (its XML
  comment says contacts and messages come from the adapter; the type itself is this app's own
  last-section memory and stays), `WhatsappApp/WhatsApp.csproj`, both `.resw` files
- Create: `tools/check-no-server.js`, `tools/test/check-no-server.test.js`
- Modify: `README.md`, `README.it.md`, `.agents/skills/maintain-the-app/SKILL.md`,
  `.agents/skills/test-the-app/SKILL.md`, `.agents/skills/update-the-app/SKILL.md`,
  `.agents/skills/release-the-app/SKILL.md`
- Delete: `.agents/skills/run-the-login-server/`

**Interfaces:**
- Consumes: everything above; `StartDecision`; `CredentialStore`.
- Produces: `node tools/check-no-server.js` - exit 0 with `OK: the repository has no server`
  when none of the eight service files, a `Server.` settings key, a `WhatsappBridge/`,
  `WhatsappServer/` or `registry/` directory, or an adapter mention in the README pair or
  `.agents/skills/**` is present; a list of what it found otherwise.

- [ ] **Step 1 (RED): the guard's own test**

`tools/test/check-no-server.test.js`, in the shape of
`tools/test/check-project-files.test.js`: a fixture tree with `WhatsappBridge/server.js`
reports it; a fixture tree with only the app and the protocol core reports nothing.
Run: `node --test tools/test/check-no-server.test.js` - Expected: FAIL, the module does not exist.

- [ ] **Step 2 (GREEN): the guard**

Implement it with the same helpers the other guards use (`tools/csharp.js`), then run the test
again - Expected: pass.

- [ ] **Step 3: Delete the adapter and its call sites**

`git rm` the ten files, remove their `<Compile>` lines, and delete every remaining call
site: `App.xaml.cs`'s `Prewarm`, `SendWatchingAsync`, `AutoConnector.TryConnectAsync` and the
watchdog; `DiagnosticsPage`'s status/`diag` calls; `CrashReport`'s `SendControlAsync("diag")`;`MemoryWatcher` keeps `TrimForMemory` (it trims this app's own caches and has nothing to do
  with the adapter; only a comment changes); `SettingsService`'s server keys go, and so do the
  `.resw` keys that named them.

- [ ] **Step 4: The account screen, without a service behind it**

`ConnectionPage` (already rewritten once) loses its `CommunicationService` fallback and its
`login.qr`/`state` handling: it shows the stored JID, the `NativeConnection.Status` line, and
the way to link a new device (which is `PairPage`); `StartDecision` and `App.OnLaunched` call
`NativeConnection.Instance.Start()` and nothing else. Run `node tools/check-resw.js --strict`.

- [ ] **Step 5: The docs and the skills**

The README pair: what the app is, that it speaks WhatsApp's protocol from the phone, how to
build and install it, that the first launch shows a QR, and (for the people who used the old
service) that the image repository and the endpoint file are private now and the app reads
neither (Review Focus 1's documentation half). `run-the-login-server` is deleted; every skill
that names the adapter loses the reference. Run: `node tools/check-docs.js`.

- [ ] **Step 6: The whole gate set**

```bash
node tools/test-protocol.js && node tools/check-csharp5.js && node tools/check-icons.js \
  && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-tile.js \
  && node tools/check-memory.js && node tools/check-actions.js && node tools/check-fire-and-forget.js \
  && node tools/check-project-files.js && node tools/check-chat-list-source.js \
  && node tools/check-chat-scroll.js && node tools/check-diagnostics.js \
  && node tools/check-xaml-names.js && node tools/check-attachment-file.js \
  && node tools/check-no-server.js && node --test "tools/test/**/*.test.js"
```
Expected: exit 0 and an `OK:` line from every guard. Then the x86 and ARM `MSBuild -t:Rebuild`
lines: `0 Error(s)`, package created.

- [ ] **Step 7: The phone, on an install that still has the server settings**

Deploy over the version that talked to the adapter, **without clearing its settings**. Expected:
the app opens on the account (it has a session), the stale `ServerAddress`/`UsePublicServer`
keys are ignored, the cached chats show while the client connects, and a text crosses both
ways with no server running anywhere on the network.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: the app has no server, and a guard keeps it that way"
```

---

### Task 13: The bug-hunter pass, line by line, and the fixes it finds

The operator asked for the `bug-hunter` skill to be run over this work and for everything it
reports to be fixed. It runs here, after the code is complete and green, and its findings are
fixed the same way every other change in this plan is: a failing test first, then the fix.

**Files:**
- Read, line by line: every file created or modified by Tasks 2-12 (the list is the union of
  those tasks' `Files:` blocks)
- Create: whatever the findings need, test first
- Modify: `docs/superpowers/plans/2026-10-11-native-only-client.md` (this plan's Execution notes)

**Interfaces:**
- Consumes: the whole tree.
- Produces: a fixed tree and an Execution notes section that names every finding, every fix
  and every finding that was deliberately not fixed, with the reason.

- [ ] **Step 1: Run the skill in its line-by-line mode**

Load `bug-hunter` and run its `local-sequential` mode over this plan's files (the skill's own
instructions decide the report's shape). A session that cannot load the skill runs the same
pass by hand over the union of the `Files:` blocks, in this order: the protocol core first
(every file under `WhatsappApp/Protocol/**` touched here), then the services, then the pages,
then the guards and the docs.

- [ ] **Step 2: Write every finding down before fixing any of it**

One line each, in the Execution notes: file, line, what the code does, what it should do, and
whether a person using the app would meet it. A finding with no user-visible effect is still
written down; it is not dropped for being small.

- [ ] **Step 3: Fix them, test first**

A finding that is a rule becomes a test in `ProtocolTests` before the fix; a finding in
app-side wiring gets its check where the other checks live (a guard, or the `[Test]` style if
it can be made pure). No fix is allowed to weaken a guard, skip a test or add a suppression.

- [ ] **Step 4: Re-run everything the fixes could have broken**

The whole gate block from Task 12 Step 6, plus the x86 and ARM rebuilds. Expected: exit 0
each, `0 Error(s)`.

- [ ] **Step 5: The device checklist, end to end**

On the Lumia, with no server: pair (fresh install), text both ways, photo and voice note both
ways, a group message, a contact picture, the call log, a status. Record what happened in the
Execution notes, failures included.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "fix: what the line-by-line pass found, with a test for each"
```

---

## Ordering

Tasks 2-11 are one chain and each ends green offline: the loop's rules, the classifier, the
history, the store, sending, media in, media out, groups, pictures, calls and status. The
adapter stays alive underneath until Task 12, which is the single switch - the same shape the
previous plans used, and the reason the app never spends a commit between "the old path is
gone" and "the new path works".

Within the chain, Task 4 must precede Task 5 (the store reads the history reader) and Task 6
must precede Task 9 (the group send reuses the per-device `enc` shape Task 6 pins). Task 7 is
the one exception to the numbering: the history payload an `ib` carries is itself an encrypted
media container, so Task 4 decodes the notification and the payload and Task 5 fetches and
decrypts it with Task 7's `MediaDownload`. Executing Task 7 before Task 5 - which is what the
implementer should do, since the download is the only thing Task 5's wiring needs from it -
leaves Task 4 exactly as written either way.

Task 13 is last by construction: it reviews code that exists.

## Execution notes

Appended as the tasks run: what the reference printed, what the phone did, and every ruling in
the form `Ruling: <what> - <why> - <cost if wrong>`.

### What the tree was before Task 1 (2026-10-11)

- The in-flight work is the login after a pairing (`Protocol/Client/NativeSession.cs`,
  `Services/NativeConnection.cs`) and the device run that found the two bugs this plan's
  Task 2 exists to prevent: the server's ping with no `id` (`SessionKeepAlive.PingAnswer`) and
  a 30-second silence (`SessionKeepAlive.IsStale`).
- `git status` at the start: 24 modified files, 3 untracked. Of those, the scroll work
  (`ConversationView.cs`, `Pages/ChatPage.xaml.cs`, both READMEs, `test-the-app/SKILL.md`) is
  from an earlier session and is not this plan's.
- The references on this machine are `/tmp/m4ref` (Baileys 7.x, TypeScript) and `/tmp/ref`
  (whatsmeow, Go: `client.go`, `message.go`, `pair.go`, `internals.go`, and
  `/tmp/ref/whatsmeow-main`).

### What has run (2026-10-11, inline execution)

| Task | Commit | State |
| --- | --- | --- |
| 1 | `d59ed53` | done: the in-flight login work gated (135 protocol tests, 101 node tests, 14 guards, x86 package) and committed |
| 2 | `9597c09` | done: `ConnectionPolicy` + the reconnect loop + pre-key publish. 141 protocol tests |
| 3 | `f44d36b` | done: `InboundRouter` + the harness's missing `AreEqual(..., because)`. 147 |
| 4 | `9ed5280` | done: `HistorySync`, `HistorySyncNotification`, `Zlib`. 155 |
| 5 | `d948e19`, `1f34e32` | done: `NativeChats`, then the wiring - an inbound stanza is acked, opened, and becomes history, a chat row or a message; `DataService` reads `NativeConnection` and no longer the adapter. 163 |
| 6 | `12b610e` | done: `Conversation` (typing + receipts), the send path with its waiter table, receipts into `SetMessageStatus`, `ChatPage` send/typing and `DataService`'s read off the adapter. 169 |
| 7 | (this commit) | done: `MediaKeys`, `MediaDownload`, `IMediaFetcher`, `WinRtMediaFetcher`, the four media protobufs in `Message`, the row + the bytes in `NativeConnection`, the bytes into `IncomingMediaStore.StoreAsync` and `DataService`, the tap into `NativeConnection.DownloadMediaAsync`. 178 protocol tests |
| 8 | (this commit) | done: `MediaConn` + `MediaUpload`, `IMediaFetcher.PostAsync` (and its WinRT half), `NativeConnection.SendMediaAsync` (device list, media connection, upload, message), `ChatPage`'s attachment path, `OutgoingMessage`'s media stanza. 190 protocol tests |
| 9 | (this commit) | done, less two named gaps: the sender key (record, message, distribution), `GroupDirectory`, `OutgoingMessage.Group` (one `skmsg` plus the `participants` node), `NativeConnection.SendGroupAsync`/`SenderKeyAsync`/`MemberDevicesAsync`, the `skmsg` receive path in `IncomingMessage`, `DeviceDirectory`'s multi-JID query. 219 protocol tests, incl. 6 that drive a whole group exchange |
| 10 | (this commit) | **partly done**: `ProfilePictures` (query, setter, reader) with 5 tests, `NativeConnection.RequestAvatarAsync`, and `ContactInfoPage` asking WhatsApp for the full picture. The chat list's `preview` prefetch is not wired |
| 11-13 | - | not started |

What Task 7 changed about the tree the tasks before it left:

- The media key schedule, the ten-byte MAC, the two hashes and the four field tables were
  printed from the reference (`/tmp/m4ref`, one field at a time) and are pinned by
  `ProtocolTests/Vectors/MediaFixture.cs`; the field numbers really do differ per type
  (`fileSha256` is 4 in a photo and 3 in a voice note, `directPath` 11 against 9), which is
  why `MediaMessages` has four tables and not one.
- **One deviation from the plan's file list**: the tests live in `ProtocolTests/MediaTests.cs`
  instead of `MediaKeysTests.cs` + `MediaDownloadTests.cs`. The four cases the plan names are
  all there, in one file, because they share the fixture the reference printed.
- **`Windows.Web.Http.HttpClient` has no `Timeout` property** (the desktop one does, and the
  plan's Step 3 assumed it): `WinRtMediaFetcher` passes a `CancellationTokenSource` to
  `AsTask` instead. A download that never answered would otherwise park the stanza reader -
  the same loop every other message arrives on - forever.
- The message whose payload is media is now **a row and then bytes**, not a stanza that is
  "acknowledged and left alone": `ReadMessageAsync` adds the row (with the encoded `Message`
  on it) and emits `MessageArrived`, then fetches, decrypts and emits `MediaArrived`. A
  download that fails leaves the bubble without its picture and a tap asks again.
- The history chunks that arrive **behind a media download** are still named in the log and
  not fetched (`NativeConnection.ApplyHistory`); that half of the sentence above still stands.

Every platform rebuilt: x86 and ARM `MSBuild -t:Rebuild` both ended with `Your package has
been successfully created.`

What Task 10 changed, and the one place the reference contradicted the plan:

- **The answer is a URL, not the JPEG.** The plan wrote `Read(BinaryNode)` as "the picture
  child's bytes" and its test as `TheAnswerYieldsTheBytes`; both references ask with
  `query="url"` and read `picture.attrs.url` (`Socket/chats.js:572-582`, `user.go:548-600`),
  and that is what this does. The bytes come from a second call - `RequestAvatarAsync` asks,
  fetches and returns them - and the test is `TheAnswerYieldsTheUrl`. Why: a query shaped
  around the plan's wording would ask for a form of the answer neither reference asks for,
  and a field that stopped being sent would read here as "no picture".
- **The JID asked about is the `target` attribute**, not the `to`: `to` is
  `s.whatsapp.net` and `target` is the contact. Both references agree, and it is the reverse
  of every other query in this client.
- **`ContactInfoPage` asks for the picture natively and still asks the adapter for the
  rest.** The page's `contact.info` frame carries the name, number, about and business hours
  as well, and there is no native equivalent of that frame yet; deleting the call now would
  empty the page. Task 12 removes it with the adapter.
- **Not done**: the chat list's `preview` prefetch for rows without a cached avatar. The
  query and the reader are here; who asks for previews, and how many, is open (it is the
  kind of loop `check-memory.js` is about).

What Task 9 added in its wiring step, and the two gaps it leaves:

- **The send is one ciphertext, not twenty**: `OutgoingMessage.Group` writes the sender key
  for each device that has not had it (in a `participants` node, `to[jid]` + `enc`), then one
  `enc type="skmsg"` for everybody. The plaintext is the message with **no device-sent
  wrapper** (`marshalMessage`: only a 1:1 send has one).
- **`addressing_mode` is echoed** when the group's metadata names one, and nothing is sent when
  it does not: a guessed mode is a message the group cannot attribute.
- **The key is handed out once per member device per run** (`_senderKeysSent`, in memory).
  `Ruling:` in memory and not on disk. Why: after a restart every device gets the key once
  more, which costs one small encrypted node per device and can never leave a member unable
  to read; a file would be a second copy of the server's membership that can disagree with it.
  Cost if wrong: a longer-lived duplicate than the reference needs.
- **The receive path reads the key before the ciphertext**, always: the `to[device]` node's
  plaintext is a message whose only field is `senderKeyDistributionMessage`, and the `skmsg`
  cannot be opened without the key it carries. **The reference's two shapes are both read** -
  `to[jid]` straight under the stanza (Baileys) and inside a `participants` node (whatsmeow) -
  and the first test written here caught a reader that knew only one of them.
- **Two gaps, both named rather than half-done**: a group **read receipt** goes to the group
  without the `participant` attribute (`whatsmeow` sets it: the receipt then names the message's
  author), because `DataService.MarkReadAsync` only has the chat and the ids; and a group row
  created by a *live* message shows the member's number and not their push name, because the
  names arrive with the history sync and not on the message. Neither loses a message; both are
  visible on the phone the moment a real group is used.
- `GroupDirectory.FanOut` as the plan listed it ("one session per participant device") is not a
  function any more: the fan-out is `NativeConnection.MemberDevicesAsync` + the per-device
  bundles in `SendGroupAsync`, because the device lists and the bundles are two requests that
  have to be made in the order the reference makes them.

What Task 9 has and has not, as of this commit:

- **Done and pinned by the reference**: the whole sender-key schedule - `HMAC(chainKey, 0x01)` for
  the message key seed, `HMAC(chainKey, 0x02)` for the next chain key, `HKDF(seed, salt = 32
  zero bytes, info = "WhisperGroup", 48 bytes)` split into iv 0..16 and cipher key 16..48 - the
  two serialized forms (distribution and `SenderKeyMessage`, version byte `0x33`, 64-byte
  signature at the end), the XEdDSA signature check *before* the decrypt, skipped iterations
  for out-of-order messages, the record's own disk framing, and the group metadata query and
  its reader. `ProtocolTests/SenderKeyTests.cs` opens the reference's own ciphertext (two
  iterations of it) and `ProtocolTests/GroupDirectoryTests.cs` reads an answer in the shape
  `groups.js` parses.
- **Two things the reference corrected here, and both were wrong from memory**: the iv is the
  first sixteen bytes of the HKDF **expansion** (not of the extract), and the first message off
  a chain uses `HMAC(chainKey, 0x01)` and not the chain key itself. Each was caught by the
  fixture and by nothing else.
- **Not done, and the feature does not work without it**: `OutgoingMessage`'s group branch
  (stanza `type="media"`/`text` with `participants` holding one `to[device]` per member that
  has no sender key, plus the one `enc type="skmsg"`), `IncomingMessage`'s `skmsg` shape, the
  distribution a member processes on arrival, `GroupDirectory.FanOut` (which needs the
  per-participant device lists), and `DataService`'s group rows and participant names. The
  plan's Task 9 steps 3 and 4 are exactly those, and they are untouched.
- The record keeps **one** state where the reference keeps five: WhatsApp rotates a sender key
  by starting a new `keyId`, so a client with the current state treats a retired key's messages
  as unreadable - which is what the reference does too once it has rotated five times.
  `Ruling:` one state, and a rotation to follow in the wiring step. Cost if wrong: a member who
  rotated four times without this client noticing can be read by the reference and not here,
  and the log would show a `keyId` mismatch rather than a decrypt failure.

What Task 8 changed about the tree, and the one deliberate deviation:

- The upload is `Prepare` (the key, the body, both hashes) plus `SendAsync` (the POST), split
  because the encryption half is the half that has to be right and it is testable without a
  host. The body is checked by handing it straight to `MediaDownload.Open` in the test, which
  is the receiver's own check.
- **`MediaConn.Query(id)` takes the id** where the plan wrote `Query()`: the answer comes back
  on the same socket as the messages and the id is the only thing that ties it to the wait.
- **`SendMediaAsync` carries more than the plan's four arguments** - `(jid, file, mediaType,
  mimeType, fileName, caption, seconds, voiceNote)`: a voice note needs its seconds and its
  `ptt` flag, and the media type cannot be guessed from the MIME type alone (`audio/ogg` is a
  voice note here and an audio file there). The four-argument shape would have forced the app
  to send a voice note the receiver shows as a music file.
- **`OutgoingMessage.Node` gained the media stanza** - `type="media"` and `mediatype` on every
  `enc` (`messages-send.js`, `getMessageType` at 885 and `getMediaType` at 906) - and the text
  node now delegates to it, so there is one place a stanza is framed.
- **The bubble takes the wire id.** Both sends now assign the id the client minted to the
  message the page put on screen. Without it a receipt names a message the list does not have,
  and the ticks of a message this phone sent never turn blue - which is what Task 6's receipt
  path was for.
- **One flag is a ruling, not a measurement**: an attachment whose MIME type is `audio/*` is
  sent as a voice note (`ptt`), and its seconds are 0. Why: the picker cannot produce an audio
  file this app recorded, the recorder always can, and a receiving phone draws a music file
  for a `ptt` it should have shown as a note - the reverse mistake is worse. Cost if wrong: a
  picked MP3 arrives as a voice note, and the note's bar shows 0:00 until it plays.

Every task above ended with all fourteen guards exit 0, `node --test tools/test/*.test.js`
101 pass / 0 fail, `check-csharp5` exit 0, and the x86 `MSBuild -t:Rebuild` producing
`Your package has been successfully created.` **The ARM rebuild has not been run in this
session.** Nothing has been deployed to the Lumia: no step of this plan that needs the phone
has run, so every device claim in it is still unproven.

What the reference corrected in this plan, mid-flight:

- **The history bootstrap is not an `ib` child.** It arrives as a
  `protocolMessage.historySyncNotification` inside a **message** (`Utils/history.js:131`,
  `getHistoryMsg`), so Task 3's `HistorySync` kind was dropped from the classifier and the
  envelope is a `Message`. Task 4 says so now.
- **That notification is field 6 of `ProtocolMessage`**, not field 1: encoding one that
  carries only a notification starts with `32 04`, and `0x32 >> 3` is 6. The first cut had
  1, and the fixture is what said otherwise.
- **Two Task 4 files were dropped as redundant**: `ConversationModel.cs`
  (`HistoryConversation`/`HistoryMessage` *are* the plain shapes the app reads) and
  `HistorySyncReader.From` (`HistorySync.Decode` is the reader). `Zlib.cs` was added
  instead, because the inline payload carries the two-byte zlib header that a raw
  `DeflateStream` reads as a block.
- **The harness was missing `Assert.AreEqual(expected, actual, because)`** - the M4 notes
  record three call sites learning to do without it. It has the overload now.
- **`Assert.AreEqual` boxes its arguments**: `AreEqual(2, someByte)` fails with
  `expected <2> but was <2>`. Task 4's fixture caught it, in the same place the M4 notes did.

### Rulings

- **Task 5 keeps `DataService` instead of growing a second store.** `Ruling:` the native path
  feeds the `ObservableCollection<Contact>` and `ChatMessage` model the pages already bind,
  through one private converter and one `NativeChats` roll-up, rather than introducing
  `NativeChatStore` beside it. Why: `DataService` is where unread counts, pin/mute, deletion,
  the caches and the avatar restore already live, and every page reads them; a second store
  means two of each of those, and the first divergence would be an unread count that disagrees
  with itself. Cost if wrong: `NativeChats` is the only new glue, and a later move to a
  separate store would delete it rather than untangle it. (This differs from
  `2026-10-10-messages-on-the-native-client.md`'s File Structure, which named
  `NativeChatStore`; that plan's Task 6 was never executed, and this ruling supersedes it.)
- **The adapter is deleted in one task, after parity, not per feature.** `Ruling:` Tasks 2-11
  add the native path beside the adapter and Task 12 removes the adapter in one commit. Why:
  a phone between two commits would have media but no text, or text but no chat list. Cost if
  wrong: the adapter's dead code stays in the tree for the length of the plan, and a plan step
  that lands half-done leaves an app with two paths - which is what `check-no-server` is for.

### Task 11 (2026-10-11, this commit)

| Task | Commit | State |
| --- | --- | --- |
| 11 | (this commit) | done: `Calls` + `CallLogEntryModel`, `StatusFeed` + `StatusPost`, the call log from the history sync, and the **Status feed**: `HistorySync.statusV3Messages`, `NativeConnection` into `_statuses`, `DataService.Statuses`, `StatusPage`'s list, the view receipt on tap and the photo fetched on the same tap. 234 protocol tests |
| 12-13 | - | not started |

- **The Status page is no longer a stub.** The plan's Step 3 said it "reads status messages ... into its existing row model"; there was no row model - the page was an empty state and the adapter never fed it. A feed was built instead, on the rows every other page uses (`DataService.Statuses`, a `ChatMessage` each), so the initials square, the picture and the time are the same three as a chat's.
- **The reference corrected the plan twice, and both were read, not remembered**: the call log's field 12 is `callCreatorJID` (not a "peer") and there are `groupJID` 13 and `participants` 14; and a status sync carries its statuses in `statusV3Messages` field 3 (`syncType == InitialStatusV3`), not in a conversation. Both are now read from `/tmp/ref/whatsmeow-main/proto`.
- **Still not run on the phone.** No step of this plan that needs the Lumia has run: the Status feed, the call log and the group paths are pinned offline and by nothing else.

### Task 13: the line-by-line pass (2026-10-11, this commit)

`bug-hunter`, local-sequential, over the whole tree (triage: 211 files, 179 scannable, FILE_BUDGET 60, large-codebase). **Coverage is partial and is reported as partial**: the CRITICAL/HIGH app source was read (protocol core, the media and Signal layers, the services and the pages touched by Tasks 5-11); `tools/**` and `ProtocolTests/**` were treated as context, and `bin/`, `obj/` and `skills/` were not read. Five real bugs, all fixed with a test each; one recorded for manual review.

| # | Severity | Where | What it was |
| --- | --- | --- | --- |
| 1 | Critical | `CredentialStore.Load` | `PushName` is optional (`RequiredKeys`) but was read with `ReadText`, which throws for an absent key - and `HasSession` loads inside its own try, so a whole valid account came back as "no session" and the app showed the QR over it. This is the `ProtocolException: no Session.PushName is stored` the device log named |
| 2 | High | `IncomingMessage` | the chat was keyed on the stanza's raw `from`, which keeps the sender's device - a contact writing from a linked phone got a chat row named `15551230000:5`, and one row per device. `JidUtils.Normalize` (the reference's `ToNonAD`/`jidNormalizedUser`) and a separate `IncomingMessage.Chat` now split the two |
| 3 | High | `HistorySync` / `Calls` | `CallLogRecord` field 12 is `callCreatorJID`, not a peer: it is this account on every call we made, so outgoing calls were filed under our own number, and a group call was never filed under its group |
| 4 | Medium | `HistorySync` | `statusV3Messages` (field 3) was skipped entirely, so the feed was empty on a first launch no matter how many statuses the phone had |
| 5 | Low | `StatusFeed` / `NativeConnection` | the feed's author kept the device suffix (bug 2 on the status path) |

`Ruling:` BUG-6 (`DeviceDirectory.Bundles` dereferences a missing `registration`/`skey` child as an NRE rather than a named refusal) is left in the report and not fixed. Why: the negative answer is an `error` child and it *is* handled, and both references treat those nodes as always present - fixing it would mean inventing a sentence for an answer that does not occur. Cost if wrong: an NRE with no name in the log instead of a sentence, and the device log is where that would show up.

`Ruling:` the thread the socket reader runs on is left alone too. `DataService` mutates its `ObservableCollection`s from the reader loop, which is a UWP hazard - but it is pre-existing and everywhere (every native message already did it), and a fix is a change to the threading model of every event in `NativeConnection`, not a line in a bug fix. Cost if wrong: a cross-thread exception on a busy chat list, which no offline test can show.

Every gate green for this commit: 234 protocol tests, 101 node tests, all fourteen `tools/check-*.js` exit 0, and **both** the x86 and the ARM `MSBuild -t:Rebuild` ending with `Your package has been successfully created.`
