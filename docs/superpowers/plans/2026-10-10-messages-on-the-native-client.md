# Messages on the native client Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A text message this phone sends arrives on the other phone, a text that
arrives shows in the chat, and none of it needs the adapter.

**Architecture:** The core already pairs, holds the account (`Credentials`,
`CredentialStore`) and has Signal sessions on disk (`SessionRecord`, `SessionStore`,
`PreKeyStore`). M4 adds what surrounds a message: the `Message` protobuf, the
pre-keys this device publishes so peers can start a session with it, the peer's
device list and the session a first send builds from it, the outgoing `message`
node with one `<enc>` per device, the incoming path with its acks and receipts, and
`historySync` for the chats. Then the app's pages stop calling the eight adapter
services (`CommunicationService`, `DataService`, ...) and call a `NativeSession`
built on the socket instead - which is `retire-the-server`'s Task 6, whose frozen
surface this plan consumes rather than re-decides.

**Tech Stack:** C# 5 (WP8.1 projection), the project's own protocol core under
`WhatsappApp/Protocol/**`, the desktop test host `ProtocolTests` (net45, MSBuild 12),
the live probe `node tools/test-protocol.js --connect`.

**Spec:** `docs/superpowers/plans/2026-10-10-native-whatsapp-client.md` (its M4 row
and *Milestone table*), `docs/superpowers/plans/2026-10-10-signal-x3dh-and-the-double-ratchet.md`
(Task 7's hand-off list), `docs/superpowers/plans/2026-10-10-retire-the-server.md`
(Task 6 and Task 7, which this plan's Tasks 6 and 7 execute).

## Global Constraints

- **C# 5 only.** `node tools/check-csharp5.js` is the gate: no `System.Security.Cryptography`,
  no `Windows.*` type inside `WhatsappApp/Protocol/**`, no LINQ extension method in a file
  without `using System.Linq;`.
- **The core compiles twice**: inside the app and inside `ProtocolTests` (net45, the same
  compiler). Anything the core cannot own is injected - `IRandomSource`, `ILog` - never
  reached for from the platform.
- **Every new `.cs`/`.xaml` under `WhatsappApp` is listed in `WhatsappApp.csproj`**
  (`node tools/check-project-files.js`): MSBuild never sees an unlisted file, and the
  guards all pass while the type "does not exist" only on the VM build.
- **Every user-visible string** is a `Loc.Get`/`x:Uid` key present in **both**
  `Strings/en-US/Resources.resw` and `Strings/it-IT/Resources.resw`
  (`node tools/check-resw.js --strict`).
- **A call fired without `await` goes through `Guarded.RunGuardedAsync`**
  (`node tools/check-fire-and-forget.js`).
- **The version announced to the server is `2.3000.1049558099`.** A companion older
  than the floor answers `<failure reason="405" location="frc">`.
- **Nothing of the user's is sent over an unverified channel**: the certificate
  signatures are verified before the `ClientPayload` goes out.
- **Do not commit unrelated working-tree changes.** `ChatPage.xaml.cs`,
  `ConversationView.cs`, `README.md`, `README.it.md` and
  `.agents/skills/test-the-app/SKILL.md` carry in-progress work from earlier sessions.
- **Tests** are `[Test]`-attributed public static parameterless methods in
  `ProtocolTests/*.cs` (the project compiles `*.cs` and `Vectors\*.cs`); they run with
  `node tools/test-protocol.js`, which builds the host with MSBuild 12 and forwards its
  exit code. A step that needs the real server is `node tools/test-protocol.js --connect`.

## Review Focus

Five inputs this plan will meet that no test of the protocol work itself covers. Each
line names the test that pins it, in the task that owns the code.

1. **A peer we have never talked to.** The first text to a contact with no session stored
   and no bundle fetched must fetch the bundle and build the session, not send nothing and
   not throw. Pinned by `DeviceDirectoryTests.AFirstSendBuildsTheSessionItNeeds` (Task 3).
2. **The same message delivered twice.** A message the server re-delivers after a
   reconnect (same `id`) is one row in the chat; the second copy reaches a named line, not
   a second row. Pinned by `IncomingMessageTests.TheSameIdTwiceIsOneMessage` (Task 5).
3. **A peer with no one-time pre-key left.** The server answers with no `key` in the
   `list` (`PreKeyBundle.HasPreKey` false); the session is built from the signed pre-key
   alone. Pinned by `DeviceDirectoryTests.APeerWithNoOneTimePreKeyStillGetsASession` (Task 3).
4. **The first message from a peer is a pre-key message.** The receive path builds the
   session from the embedded bundle (`PreKeySignalMessage`, `SessionRecord.StartAsResponder`)
   before it decrypts; the message is not dropped as "no session". Pinned by
   `IncomingMessageTests.TheFirstMessageFromANewPeerStartsTheSession` (Task 5).
5. **An ack or receipt for a message id we do not have**, and an outgoing message echoed
   back by another device of ours: neither may throw, the unknown id is a named
   `Diag.Failed` line, and our own echo is not shown as an incoming message. Pinned by
   `IncomingMessageTests.AnUnknownReceiptIdIsNamedAndHarmless` and
   `IncomingMessageTests.OurOwnEchoIsNotAnIncomingMessage` (Task 5).

## File Structure

    WhatsappApp/Protocol/WaProto/
      Message.cs              the Message protobuf: text, extended text, device-sent, protocol
    WhatsappApp/Protocol/Client/
      PreKeyUpload.cs         the iq that publishes this device's pre-keys
      DeviceDirectory.cs      a peer's devices and the pre-key bundle each answers
      OutgoingMessage.cs      plaintext -> one <enc> per device -> the message node
      IncomingMessage.cs      a message node -> decrypted text, ack, receipt
      SessionKeepAlive.cs     the ping, the presence node, and when a connection is stale
      NativeSession.cs        the socket loop, history sync, and the pages' surface
    WhatsappApp/Services/
      NativeChatStore.cs      conversations and messages the pages bind to
    ProtocolTests/
      MessageTests.cs DeviceDirectoryTests.cs OutgoingMessageTests.cs
      IncomingMessageTests.cs NativeSessionTests.cs
      Vectors/MessageFixture.cs PeerBundleFixture.cs

Deleted by Task 7, not before: `CommunicationService.cs`, `DataService.cs`,
`SessionService.cs`, `AutoConnector.cs`, `ConnectionWatchdog.cs`, `EndpointService.cs`,
`DiscoveryService.cs`, `ServerPinger.cs`, `FrameCodec.cs`, `CryptoHelper.cs`,
`SelfCheck.cs`, and the adapter-facing halves of `SettingsService`, `ChatsPage`,
`ChatPage`, `ContactInfoPage`, `CallsPage`, `StatusPage`, `DiagnosticsPage`.

## Preconditions, and what this plan does not cover

- **M3 must be closed first**: `docs/superpowers/plans/2026-10-10-signal-x3dh-and-the-double-ratchet.md`
  Tasks 6 (sender keys) and 7 (the gates) are the last two open steps there. Task 1 below
  starts from that plan's Task 7 hand-off list - the signatures it prints are this plan's
  `Consumes` blocks.
- **Groups are not here.** Sender keys are M3 Task 6; a group message is M5's material
  together with media. M4 is one-to-one text.
- **Media is not here** (M5: `Protocol/Media/**`, upload and download through the native
  path, adapter deleted).
- **The phone-user features that need the adapter today** (quoted reply, forward, message
  actions, in-chat search, group management) are a separate plan: after Task 7 they are
  written against `NativeSession`, and half of them need protocol work this plan does not
  have.

---

### Task 1: The `Message` protobuf, and the fixture that fixes its field numbers

**Files:**
- Create: `WhatsappApp/Protocol/WaProto/Message.cs`
- Create: `ProtocolTests/MessageTests.cs`
- Create: `ProtocolTests/Vectors/MessageFixture.cs`
- Modify: `WhatsappApp/WhatsApp.csproj` (the new `Compile` entry)

**Interfaces:**
- Consumes: `ProtoWriter` (`Bytes(int, byte[])`, `Varint(int, ulong)`, `Text(int, string)`,
  `ToArray()`), `ProtoReader` (`ReadField()`, `ReadBytes()`, `ReadVarint()`, `SkipField(int)`),
  `ProtoField.Number`/`WireType`.
- Produces: `Message` with `string Conversation`, `byte[] ExtendedTextMessage` (the
  encoded `ExtendedText`), `byte[] SenderKeyDistributionMessage`, `byte[] DeviceSentMessage`,
  `byte[] ProtocolMessage`; `static Message Text(string body)`, `byte[] Encode()`,
  `static Message Decode(byte[] bytes)`, and the four the tasks below need when they have
  a wrapper in hand rather than a decoded message: `static Message DeviceSent(string destinationJid, Message inner)`,
  `static string Destination(byte[] deviceSentPayload)`, `static Message Inner(byte[] deviceSentPayload)`,
  `string Body()` (the words, from `Conversation` or from an `ExtendedText`).

- [x] **Step 1: Print the reference's own bytes, outside the repository**

```bash
mkdir -p /tmp/m4ref && cd /tmp/m4ref && npm i @whiskeysockets/baileys
node -e "const p=require('@whiskeysockets/baileys').proto; \
  console.log(Buffer.from(p.Message.encode({conversation:'Hello, WhatsApp!'}).finish()).toString('hex')); \
  console.log(Object.entries(p.Message.decode(Buffer.alloc(0)).constructor.fields || {}).length || 'see proto');"
```

Record in `ProtocolTests/Vectors/MessageFixture.cs`, as a comment, the command above and
the hex it printed. The fixture's two constants are that hex (`TextEncoded`) and the body
it came from (`TextBody`).

- [x] **Step 2: Write the failing tests**

`ProtocolTests/MessageTests.cs` - as written, five tests rather than three: the field
numbers got a test of their own, and the device-sent wrapper another.

- `TheReferenceTextMessageIsReproducedByteForByte` - `Message.Text(MessageFixture.TextBody).Encode()`
  equals `MessageFixture.TextEncoded`.
- `EachFieldDecodesFromTheReferencesOwnBytes` - one decode per fixture, so the field
  numbers are pinned by the reference's own bytes: `conversation` 1,
  `senderKeyDistributionMessage` 2, `extendedTextMessage` 6, `protocolMessage` 12,
  `deviceSentMessage` 31 (the varint 250).
- `TheDeviceSentFormCarriesTheDestinationAndTheText` - `Message.DeviceSent(...)`
  reproduces the fixture byte for byte, and `Destination`/`Inner` read the `destinationJid`
  and the wrapped text out of the encoded field. The test passes the field's own payload
  (`Message.Decode(bytes).DeviceSentMessage`), which is the shape the receive path has.
- `ARoundTripKeepsEveryField` - a message built with all five fields set survives
  `Encode` then `Decode` byte for byte.
- `AnUnknownFieldIsSkippedNotFatal` - `Decode` of the reference bytes with a
  hand-appended field 100 ignores it and still reads the text.

Run: `node tools/test-protocol.js` - Expected: FAIL, `MessageTests.*` failing to compile
(`The type or namespace name 'Message' could not be found`).

- [x] **Step 3: Implement `Message`**

Hand-written on `ProtoWriter`/`ProtoReader` like the other `WaProto` types: a `Decode`
that walks fields with `ReadField()`/`SkipField()` and copies the four sub-messages'
bytes verbatim, so an unknown or a newer field costs nothing.

- [x] **Step 4: Run the tests**

Run: `node tools/test-protocol.js` - Expected: exit 0, five `pass` lines, and
`OK: 101 protocol test(s)`. It passed: `OK: 101 protocol test(s)`, exit 0.

Run the app build too, and not as a formality:

```bash
MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/Bin/MSBuild.exe" WhatsappApp.sln \
  -t:Rebuild -p:Configuration=Debug -p:Platform=x86 -nologo -v:minimal
```

Expected: `0 Error(s)`. The first run of it answered
`Message.cs(90,20): error CS1501: no overload of GetString takes 1 argument`: the WP8.1
projection has `Encoding.UTF8.GetString(byte[], int, int)` and not the one-argument
overload the desktop test host compiles happily. The code calls the three-argument one
now (as `NodeReader` and `PairingQr` already did), and the lesson belongs in this step:
**a protocol task is not done until the app build has seen the file.**

- [x] **Step 5: Commit** - `cd21d8b` (amended once: the first add swept the whole
  `WhatsApp.csproj` because the working copy's CRLF endings were normalized; the commit
  holds the one added `<Compile>` line and nothing else of that file).

```bash
git add WhatsappApp/Protocol/WaProto/Message.cs ProtocolTests/MessageTests.cs \
        ProtocolTests/Vectors/MessageFixture.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: the Message protobuf, from the reference's own bytes"
```

---

### Task 2: The pre-keys this device publishes

**Files:**
- Create: `WhatsappApp/Protocol/Client/PreKeyUpload.cs`
- Create: `ProtocolTests/PreKeyUploadTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `Credentials` (`RegistrationId`, `IdentityPublicKey`, `SignedPreKeyId`,
  `SignedPreKeyPublic`, `SignedPreKeySignature`, `SignedPreKeyToSign(byte[])`),
  `Credentials.Create(IRandomSource)` for the signed pre-key, `IRandomSource.Bytes(int)`
  for each one-time pair, `X3dh.TypedPublicKey(byte[] privateKey)` for the public half,
  `PreKeyStore` (`Remaining`, `Publish(uint, byte[])`, `SetSignedPreKey(uint, byte[], byte[])`,
  `SignedPreKeyId`), `BinaryNode`, `NodeWriter.Encode`.
- Produces: `PreKeyUpload.Node(Credentials credentials, PreKeyStore store, IRandomSource random, int count)`
  returning the `BinaryNode`, and `PreKeyUpload.NeedsUpload(PreKeyStore store, int floor)`
  returning `bool`.

- [ ] **Step 1: Write the failing tests**

`ProtocolTests/PreKeyUploadTests.cs`:

- `TheNodeIsTheOneTheServerExpects` - tag `iq`, `type` `set`, `xmlns` `encrypt`, and
  children `registration`, `type`, `identity`, `skey` (with `id`, `value`, `signature`)
  and `list` holding one `key` per published pre-key.
- `ItPublishesExactlyTheCountItWasAsked` - after `Node(..., 20)`, `store.Remaining` is 20,
  and every `key`'s `id` is distinct.
- `ABundleAboveTheFloorIsNotPublishedAgain` - `NeedsUpload(store, 5)` is false with 20
  remaining and true with 2.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile, `PreKeyUpload` not found.

- [ ] **Step 2: Implement `PreKeyUpload`**

`Node` draws `count` pairs with `random.Bytes(32)`, takes each public half from
`X3dh.TypedPublicKey`, stores the private half with `PreKeyStore.Publish`, signs and
stores a signed pre-key (`Credentials.Create(random)` / `SetSignedPreKey`) when
`store.SignedPreKeyId` is 0, and builds the node. Version and device attributes come from
`Credentials` as `PairingSession` already builds them.

- [ ] **Step 3: Run the tests** - Expected: exit 0, three `pass` lines, `OK: <previous + 3>`.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Protocol/Client/PreKeyUpload.cs ProtocolTests/PreKeyUploadTests.cs \
        WhatsappApp/WhatsApp.csproj
git commit -m "feat: publish this device's pre-keys, so a peer can start a session"
```

---

### Task 3: A peer's devices, and the session a first send builds

**Files:**
- Create: `WhatsappApp/Protocol/Client/DeviceDirectory.cs`
- Create: `ProtocolTests/DeviceDirectoryTests.cs`
- Create: `ProtocolTests/Vectors/PeerBundleFixture.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `PreKeyBundle` (its fields and `Check()`), `X3dh.AliceSecret(...)`,
  `SessionRecord.StartAsInitiator(byte[] ourIdentityPrivate, byte[] ourEphemeralPrivate,
  byte[] ourRatchetPrivate, uint ourRegistrationId, PreKeyBundle bundle, IRandomSource random)`,
  `SessionStore.Load`/`Save`, the `Credentials` this device holds, `BinaryNode`.
- Produces: `DeviceDirectory.Query(string jid)` returning the `BinaryNode` to send;
  `DeviceDirectory.Bundles(BinaryNode answer)` returning `IList<PreKeyBundle>`;
  `DeviceDirectory.Session(string jid, PreKeyBundle bundle, Credentials credentials, SessionStore store)`
  returning the `SessionRecord` to encrypt with (existing one when it is already stored or
  still valid, a new one otherwise).

- [ ] **Step 1: Freeze the answer's shape**

Put the server's answer for one contact in `ProtocolTests/Vectors/PeerBundleFixture.cs`:
the node built with `NodeWriter.Encode` in the test, and a comment recording where the
field names come from (`registration`, `identity`, `skey.id`/`value`/`signature`,
`key.id`/`value`, one `user` per JID with `devices`). No live capture is committed: the
fixture's job is the shape the parser reads, and the live run in Task 7 is what proves it.

- [ ] **Step 2: Write the failing tests**

`ProtocolTests/DeviceDirectoryTests.cs`:

- `AFixtureAnswerBecomesOneBundlePerDevice` - two `device` entries, two bundles, each
  with its own registration id.
- `ASignedPreKeyWhoseSignatureDoesNotVerifyIsRefused` - a bundle with one flipped byte in
  `skey.signature` fails `Check()` and the failure names the JID.
- `APeerWithNoOneTimePreKeyStillGetsASession` (review focus 3) - an answer with no `key`
  in it gives `HasPreKey` false, and `Session` still returns a record whose `HasSession`
  is true.
- `AFirstSendBuildsTheSessionItNeeds` (review focus 1) - `Session` on an empty
  `SessionStore` returns a record and `Save` puts it where the next call reads it; a second
  call returns the loaded record rather than a second session.
- `AnEstablishedSessionIsNotReplaced` - a stored record whose `HasSession` is true comes
  back unchanged.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile, `DeviceDirectory` not found.

- [ ] **Step 3: Implement `DeviceDirectory`**

`Bundles` walks the answer's `user`/`device`/`key` nodes into `PreKeyBundle`s and calls
`Check()` on each; a failed check is a `ProtocolException` naming the JID and the device.
`Session` returns the stored record when it exists, otherwise starts one with
`SessionRecord.StartAsInitiator` over `X3dh.AliceSecret`, and saves it.

- [ ] **Step 4: Run the tests** - Expected: exit 0, five `pass` lines, `OK: <previous + 5>`.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Protocol/Client/DeviceDirectory.cs ProtocolTests/DeviceDirectoryTests.cs \
        ProtocolTests/Vectors/PeerBundleFixture.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: a peer's device list, and the session a first send builds from it"
```

---

### Task 4: The text that leaves the phone

**Files:**
- Create: `WhatsappApp/Protocol/Client/OutgoingMessage.cs`
- Create: `ProtocolTests/OutgoingMessageTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `Message.Text`, `SessionRecord.Encrypt(byte[] plaintext)`,
  `SignalEnvelope.Seal(byte[] message, byte[] macKey, byte[] senderIdentity, byte[] receiverIdentity)`
  and its `Wrap(byte[])` for the pre-key form, `BinaryNode`, `NodeWriter.Encode`,
  `IRandomSource.Bytes(int)` (the message id).
- Produces: `OutgoingMessage.Id(IRandomSource)` returning the upper-case hex id;
  `OutgoingMessage.Node(string to, string id, string body, IList<KeyValuePair<string, SessionRecord>> devices, Credentials credentials, uint timestamp)`
  returning the `message` `BinaryNode`.

- [ ] **Step 1: Write the failing tests**

`ProtocolTests/OutgoingMessageTests.cs`:

- `TheNodeCarriesOneEncPerDevice` - node tag `message`, attrs `to`, `type` `text`, `id`,
  `t`, and one `enc` child per device with that device's `v` attribute, in the order given.
- `EachEncDecryptsBackToTheText` - two responder `SessionRecord`s built from the same
  initiator (Task 3's path), one decryption per `enc`, both equal to the body.
- `TheIdIsSixteenUpperHexAndUnique` - two draws with different random bytes differ, and
  neither matches `[^0-9A-F]`.
- `ThePlaintextIsTheDeviceSentForm` - the decrypted bytes decode as a `Message` whose
  `DeviceSentMessage` carries the text and the `to` JID, so a second device of ours reads
  the same words.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile.

- [ ] **Step 2: Implement `OutgoingMessage`**

One `enc` per device: `SessionRecord.Encrypt` over the `deviceSentMessage` form, sealed
into a `SignalMessage`/`PreKeySignalMessage` (pre-key form while the record says
`PreKeyPending`), and the node built with the message id, the `to` JID and the timestamp.

- [ ] **Step 3: Run the tests** - Expected: exit 0, four `pass` lines, `OK: <previous + 4>`.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Protocol/Client/OutgoingMessage.cs ProtocolTests/OutgoingMessageTests.cs \
        WhatsappApp/WhatsApp.csproj
git commit -m "feat: a text leaves the phone as one enc per device"
```

---

### Task 5: The text that arrives, and what it answers

**Files:**
- Create: `WhatsappApp/Protocol/Client/IncomingMessage.cs`
- Create: `ProtocolTests/IncomingMessageTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `PreKeySignalMessage.Decode`, `SignalMessage.Decode`, `SignalEnvelope.Open(...)`,
  `SessionRecord.Decrypt(byte[] wire, bool preKey)`,
  `SessionRecord.StartAsResponder(byte[] ourIdentityPrivate, byte[] ourSignedPreKeyPrivate,
  byte[] ourOneTimePreKeyPrivate, uint ourRegistrationId, PreKeySignalMessage message, IRandomSource random)`,
  `SessionStore`, `PreKeyStore.Take(uint)`, `Message.Decode`, `BinaryNode`.
- Produces: `IncomingMessage` with `string Text`, `string From`, `string Id`, `uint Timestamp`;
  `static IncomingMessage Read(BinaryNode message, Credentials credentials, SessionStore sessions, PreKeyStore preKeys)`,
  which throws a `ProtocolException` naming the node id rather than an `InvalidOperationException`;
  `static BinaryNode Ack(string to, string id)` and
  `static BinaryNode Receipt(string to, IList<string> ids, string type)`.

- [ ] **Step 1: Write the failing tests**

`ProtocolTests/IncomingMessageTests.cs`:

- `ATextFromAPeerIsReadBack` - a node built through Task 4's path (a second device's
  session) reads back the same body, JID and id.
- `TheFirstMessageFromANewPeerStartsTheSession` (review focus 4) - an empty
  `SessionStore`, a pre-key node: `Read` returns the text, the store has the peer's session
  afterwards, and the consumed one-time pre-key is gone (`PreKeyStore.Take`).
- `TheSameIdTwiceIsOneMessage` (review focus 2) - the second `Read` of the same node
  answers `Diag`-visible "already seen", and `ChatStore`-facing callers get the same id
  once.
- `AMovedCiphertextByteIsANamedFailure` - a flipped byte in `enc` throws a
  `ProtocolException` naming the node id, not an `InvalidOperationException`.
- `AnUnknownReceiptIdIsNamedAndHarmless` (review focus 5) - a receipt for an id we never
  sent does not throw, and the failure line names the id.
- `OurOwnEchoIsNotAnIncomingMessage` (review focus 5) - a node whose `from` is our own JID
  and whose payload is a `deviceSentMessage` is not returned as an incoming message.
- `TheAckNamesTheNodeItAnswers` - `Ack(id)` has tag `ack`, `class` `message`, the id, and
  the `to` JID.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile.

- [ ] **Step 2: Implement `IncomingMessage`**

Decode the `enc` child, take the pre-key or the whisper form, decrypt with the stored
session (or start one from the embedded bundle when there is none), decode the
`Message`, and hand back the text with its ids. The seen-id set is bounded and lives here,
not in the page.

- [ ] **Step 3: Run the tests** - Expected: exit 0, seven `pass` lines, `OK: <previous + 7>`.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Protocol/Client/IncomingMessage.cs ProtocolTests/IncomingMessageTests.cs \
        WhatsappApp/WhatsApp.csproj
git commit -m "feat: read a message, answer it, and leave it out when it is our own echo"
```

---

### Task 5b: Pre-keys the server hands out stay ahead of the demand

**Files:**
- Modify: `WhatsappApp/Protocol/Client/PreKeyUpload.cs` (the floor)
- Create: `ProtocolTests/PreKeyFloorTests.cs`

**Interfaces:**
- Consumes: Task 2's `NeedsUpload`, `PreKeyStore.Remaining`.
- Produces: `PreKeyUpload.LowWaterMark` (the constant the loop uses) and
  `PreKeyUpload.RefillCount`.

- [ ] **Step 1: Write the failing tests**

- `TheFloorIsTheConstantAndNotARepeatedNumber` - `LowWaterMark` is 5 and `RefillCount`
  is 20, and `NeedsUpload(store, PreKeyUpload.LowWaterMark)` answers false at exactly 5.
- `ATakeBelowTheFloorAsksForMore` - after `Remaining` drops to 4, `NeedsUpload` is true.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile.

- [ ] **Step 2: Implement the two constants and the call the session makes**

`NativeSession` checks the floor on every reconnect and after every pre-key message read.

- [ ] **Step 3: Run the tests** - Expected: exit 0, two `pass` lines, `OK: <previous + 2>`.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Protocol/Client/PreKeyUpload.cs ProtocolTests/PreKeyFloorTests.cs
git commit -m "feat: refill the one-time pre-keys before the server runs out"
```

---

### Task 5c: The connection stays alive, and the phone says it is there

**Files:**
- Create: `WhatsappApp/Protocol/Client/SessionKeepAlive.cs`
- Create: `ProtocolTests/SessionKeepAliveTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `WhatsAppSocket.SendAsync`, `BinaryNode`, `Diag`.
- Produces: `SessionKeepAlive` with `const int PingSeconds` (30), a constant
  `PingNamespace` (`w:p`), `static BinaryNode Ping()`, `static BinaryNode Presence(IList<string> categories)`,
  and `static bool IsStale(DateTime lastNode, DateTime now)`.

- [ ] **Step 1: Write the failing tests**

`ProtocolTests/SessionKeepAliveTests.cs`:

- `ThePingIsTheEmptyIqOnThePingNamespace` - tag `iq`, `type` `get`, `xmlns` `w:p`, `to`
  `s.whatsapp.net`, and no children.
- `ThePresenceNodeIsAvailableAndCarriesEachCategory` - tag `presence`, `type` `available`,
  one `category` child per name given, in the order given.
- `ASilentConnectionIsStaleAfterTheInterval` - `IsStale` is false at 29 s and true at 31 s.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile, `SessionKeepAlive` not found.

- [ ] **Step 2: Implement it, and call it from the session**

`NativeSession` (Task 6) sends `Presence` once after the handshake, pings on
`PingSeconds`, and turns a `failure` or `stream:error` node into a named failure and a
reconnect - never into a message.

- [ ] **Step 3: Run the tests** - Expected: exit 0, three `pass` lines, `OK: <previous + 3>`.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Protocol/Client/SessionKeepAlive.cs ProtocolTests/SessionKeepAliveTests.cs \
        WhatsappApp/WhatsApp.csproj
git commit -m "feat: keep the connection alive, and say this phone is there"
```

---

### Task 6: `NativeSession`, and the chats it fills

**Files:**
- Create: `WhatsappApp/Protocol/Client/NativeSession.cs`
- Create: `WhatsappApp/Services/NativeChatStore.cs`
- Create: `ProtocolTests/NativeSessionTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`
- Modify: `WhatsappApp/Pages/ChatsPage.xaml.cs`, `WhatsappApp/Pages/ChatPage.xaml.cs`
  (read from `NativeSession`/`NativeChatStore` instead of `DataService`)

**Interfaces:**
- Consumes: everything above, `WhatsAppSocket` (`HandshakeAsync`, `ConnectAsync`,
  `ReadNodeAsync`, `SendAsync`, `Close`), `Credentials`/`CredentialStore`,
  `WebSocketTransport`, `Guarded.RunGuardedAsync`, `Loc.Get`, `Diag`.
- Produces: `NativeSession.Instance` with `Task StartAsync()`,
  `Task<string> SendTextAsync(string jid, string body)` (the message id, or a
  `ProtocolException` that says why), `event EventHandler<IncomingMessage> MessageReceived`,
  `event EventHandler ChatsChanged`, `string Status`; and `NativeChatStore.Instance` with
  `Contacts` (the same `ObservableCollection<Contact>` the pages bind today),
  `MessagesFor(string jid)`, `MarkRead(string jid)`.

- [ ] **Step 1: Freeze the surface this must provide**

```bash
grep -rn "DataService\.\|CommunicationService\." --include=*.cs WhatsappApp/Pages WhatsappApp/Services \
  | grep -v "/obj/" | sed 's/.*\(DataService\.[A-Za-z]*\|CommunicationService\.[A-Za-z]*\).*/\1/' | sort -u
```

Every name that comes out is a member `NativeSession`/`NativeChatStore` must offer, or a
call the page loses. Paste the list into this task's step as the checklist to satisfy,
and delete a call rather than re-implement it when the adapter's reason for it is gone
(`EndpointService`, discovery, `ServerPinger`).

- [ ] **Step 2: Write the failing tests**

`ProtocolTests/NativeSessionTests.cs` (the socket is `ProtocolTests`' own
`DesktopTransport`, as `WhatsAppSocketTests` uses it):

- `AHistorySyncNotificationBecomesConversationsAndMessages` - a `historySync` notification
  node reads into the store: one contact per conversation, its messages in order, the
  oldest first.
- `AMessageForAChatNotInTheStoreCreatesIt` - an incoming text from an unknown JID lands in
  `Contacts` with an unread count of one.
- `SendingToAnUnknownPeerFetchesAndStores` - `SendTextAsync` on a JID with no session
  writes the `query` for the bundle, then the `message` node, in that order, on the
  transport's recorded writes.
- `AReconnectDoesNotLoseTheSession` - the store's conversations survive a second
  `StartAsync` over the same storage.

Run: `node tools/test-protocol.js` - Expected: FAIL to compile.

- [ ] **Step 3: Implement `NativeSession` and `NativeChatStore`**

The socket loop follows `PairingSession`'s shape: one reader, one writer, `Guarded` at
every fire-and-forget, `Diag` on every named failure. `NativeChatStore` is backed by the
same storage pattern `ChatCache`/`MessageCache` already use, so a restart shows the last
conversations before the socket answers.

- [ ] **Step 4: Run the tests, and the gate that matters for this task**

Run: `node tools/test-protocol.js` - Expected: exit 0, four `pass` lines, `OK: <previous + 4>`.
Run: `node tools/check-fire-and-forget.js`, `node tools/check-memory.js`,
`node tools/check-project-files.js`, `node tools/check-resw.js --strict` - Expected: exit 0 each.
Run: `node tools/test-protocol.js --connect --pair` - Expected: the live pairing still prints
its codes (`PROBE OK: the server asked for a pairing`), then `Ctrl-C`.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Protocol/Client/NativeSession.cs WhatsappApp/Services/NativeChatStore.cs \
        ProtocolTests/NativeSessionTests.cs WhatsappApp/Pages/ChatsPage.xaml.cs \
        WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: a native session the pages read chats and messages from"
```

---

### Task 7: The app stops talking to the adapter, and the text crosses the wire twice

**Files:**
- Modify: `WhatsappApp/App.xaml.cs` (start `NativeSession`, drop the discovery path)
- Delete: the adapter-facing services listed in *File Structure*
- Modify: `WhatsappApp/WhatsApp.csproj` (the deletions)
- Create: `tools/check-no-server.js` (or execute `retire-the-server` Task 7 if it is
  already written by then - it decides the guard's exact rules)
- Modify: `README.md`, `README.it.md`, `.agents/skills/test-the-app/SKILL.md`

**Interfaces:**
- Consumes: `NativeSession`/`NativeChatStore` from Task 6, `StartDecision`,
  `CredentialStore`.
- Produces: an app whose build and tests contain no reference to the adapter; the
  milestone's own check is the phone.

- [ ] **Step 1: Execute `retire-the-server` Task 6**

That task has the file-by-file list and the ruling about which service dies with which;
its Step 1 is the same grep as Task 6's Step 1 above. Task 6 has already switched the
reads, so what the grep still reports is call sites - each one is deleted, not kept
behind a wrapper.

- [ ] **Step 2: Execute `retire-the-server` Task 7**

The guard (`tools/check-no-server.js`) and the README claims, in its words, not a second
copy of them here.

- [ ] **Step 3: The full gate**

```bash
node tools/test-protocol.js && node tools/check-csharp5.js && node tools/check-icons.js \
  && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-tile.js \
  && node tools/check-memory.js && node tools/check-actions.js && node tools/check-fire-and-forget.js \
  && node tools/check-project-files.js && node tools/check-chat-list-source.js \
  && node tools/check-chat-scroll.js && node tools/check-diagnostics.js \
  && node tools/check-xaml-names.js && node tools/check-attachment-file.js \
  && node tools/check-no-server.js && node --test "tools/test/**/*.test.js"
```

Expected: exit 0 each, `OK: ...` from every guard. Then:

```bash
MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/Bin/MSBuild.exe" WhatsappApp.sln \
  -t:Rebuild -p:Configuration=Debug -p:Platform=x86 -nologo -v:minimal
```

Expected: `0 Error(s)`, `Your package has been successfully created.`

- [ ] **Step 4: The phone, which is the only authority for this milestone**

Deploy the x86 or ARM package to the Lumia, then:

1. Pair with the QR on a fresh install (the code from `PairPage`).
2. Send a text from the phone to a real contact: it arrives on that contact's phone.
3. Have that phone answer: the text appears in the chat, with the ack and the read receipt.
4. Turn the phone's Wi-Fi off and on: the session comes back without a new pairing, and a
   message sent while offline arrives once (not twice) when it returns.

Record what happened in the plan's *Execution notes*, failures included. A step that
cannot be done on the phone is written as not run, never as passed.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: the app talks to WhatsApp directly, and the adapter is gone"
```

---

## Ordering

Tasks 1-5c are one chain, each ending green offline: the protobuf, the pre-keys, the
peer's devices, the outgoing text, the incoming text, the pre-key floor, the keepalive.
Task 6 is the first that needs the socket loop and the pages together, and Task 7 is the
deletion plus the phone.

Tasks 1-5c change nothing the app runs: the app still talks to the adapter until Task 7.
That is deliberate - the milestone has one switch, and it is thrown once.

## Execution notes

Append here as the plan runs: what the reference printed, what the live probe answered,
what the phone did. The M3 plan's own notes are the shape to copy.

### Task 1 - the protobuf, and what the app build caught (`cd21d8b`)

- The reference printed, with the command in `ProtocolTests/Vectors/MessageFixture.cs`:
  `text 0a1048656c6c6f2c20576861747341707021`, `extended 32040a024869`,
  `skdm 12080a01671203010203`, `protocol 621a0a16...1000`,
  `deviceSent fa01260a107240732e...`.
- RED first: the new tests answered `CS0103: the name 'Message' does not exist`, exit 1.
- Then GREEN: `OK: 101 protocol test(s)`, and the app build answered `CS1501` for the
  single-argument `Encoding.UTF8.GetString`. Only the WP8.1 projection refuses it; the
  three call sites use the three-argument overload now.
- One test was wrong and the code was right: `Destination`/`Inner` read the field's own
  payload, and the test handed them the wrapping message. The test now decodes the
  wrapper first, which is what the receive path will do.
- The desktop test host compiles the same sources as the app, but **not the same API
  surface**: a green `test-protocol` is not a green app build, and this task is the proof.
- After the fix: all fourteen guards exit 0, `node --test tools/test/**/*.test.js` green,
  app rebuild `0 Error(s)`.
