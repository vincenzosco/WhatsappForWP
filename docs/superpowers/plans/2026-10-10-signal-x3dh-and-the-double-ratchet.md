# M3 — Signal on the phone: X3DH, the ratchet, sessions and sender keys

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** the app can turn a pre-key bundle into a session and a session into
messages, with the arithmetic of Signal rather than of its own invention: X3DH for
the first agreement, the Double Ratchet for everything after, a session that
survives a restart, and the sender keys groups need.

**Architecture:** `WhatsappApp/Protocol/Signal` is a new folder of portable C# 5,
built out of the primitives that are already in `Protocol/Crypto` (X25519, XEdDSA,
HKDF, HMAC, SHA-256/512, AES-CBC) and the protobuf reader/writer in
`Protocol/WaProto`. It has no WinRT type and no socket: it is a library of
arithmetic with frozen fixtures, compiled by `ProtocolTests` beside the phone. The
app's session (`NativeSession`, the retire plan's Task 6) is the only caller, and
it arrives in M4.

**Tech Stack:** C# 5 for Windows Phone 8.1, compiled twice — into the app and into
the .NET 4.5 desktop host (`ProtocolTests`, run by `node tools/test-protocol.js`).
Protobuf by hand (`ProtoWriter`/`ProtoReader`), AES-256-CBC + HMAC-SHA256 for the
message AEAD, HKDF-SHA256 for the key schedule.

**Spec:** `docs/superpowers/plans/2026-10-10-native-whatsapp-client.md`, the line
*"M3 — Signal. X3DH, the Double Ratchet, pre-key and session records, sender
keys"* and its milestone table (`M3 | sessions, ratchets and sender keys match
published test vectors | no [phone]`). The protocol itself is the published
specifications — <https://signal.org/docs/specifications/x3dh/> and
<https://signal.org/docs/specifications/doubleratchet/> — and the wire format is
the reference client's, which is why Task 1 captures it rather than transcribes
it.

## Global Constraints

- **C# 5 only.** `tools/check-csharp5.js` fails on interpolated strings,
  `?.`, expression-bodied members, `nameof`, inline `out` declarations, an
  `await` in a `catch`, and a LINQ call without `using System.Linq;`.
- **Under `WhatsappApp/Protocol/**` there is no `System.Security.Cryptography` and
  no `Windows.*`**: every file this plan adds must compile in the desktop host.
- **Every new file appears in `WhatsappApp/WhatsApp.csproj`** as a
  `<Compile Include>` and in `ProtocolTests/ProtocolTests.csproj` through
  `..\WhatsappApp\Protocol\**\*.cs`; `tools/check-project-files.js` is what says so.
- **Fixtures are printed, never typed.** A constant or a ciphertext copied by eye
  is a bug the fixture cannot catch (see the client plan's ruling on the mangled
  certificate constant). Every frozen vector carries the command that regenerates
  it in the file's header.
- **The wire format is the reference's, the on-disk record is ours.** What leaves
  the phone must be byte-identical to what the reference sends; what it stores
  for itself only has to parse.
- **The gates are** `node tools/test-protocol.js`, `tools/check-csharp5.js`, every
  `tools/check-*.js`, `node --test tools/test/*.test.js`, and the x86 + ARM
  rebuild reporting `0 Error(s)`.
- **No repository is made public or private, and nothing is pushed, without
  asking** — the rules from the retire plan still hold.

## Review Focus

Five ways this milestone is likely to bite a person using the phone, and the task
whose tests pin each one.

1. **A message that arrives out of order, or twice.** WhatsApp retries and
   reorders; a ratchet that only moves forward refuses both. The skipped message
   keys have to be kept, and a replayed message must decrypt to the same bytes
   rather than throw. Task 3.
2. **A session that does not survive the app being closed.** The record is
   written and read on every run; a half-written one must read as "no session"
   and start again rather than fail inside a decryption. Task 5.
3. **An initial message built from a bundle whose one-time pre-key is already
   gone.** The server may hand out a bundle whose OPK another device consumed:
   the X3DH then has four DH outputs instead of three, and the receiver must be
   able to agree from what it has. Task 2.
4. **The version byte and the truncated MAC.** A MAC compared in full, or a
   version byte written as `0x03` instead of the ASCII `3`, produces messages
   that look right locally and are refused by WhatsApp. Task 4.
5. **A group message from a member whose sender key arrived later.** The sender
   key distribution message is itself encrypted and can be missed; the group path
   must say "no sender key for this member" instead of decrypting garbage.
   Task 6.

---

### Task 1: The oracle — the reference's constants and a conversation to check against

Nothing in this milestone can be verified by reasoning: the ratchet's constants
are the reference's choice, not a standard's, and a plausible-looking
implementation still produces ciphertexts no WhatsApp client accepts. This task
builds the oracle first, so every later task has something frozen to fail against.

**Files:**
- Create: `WhatsappApp/Protocol/Signal/SignalConstants.cs`
- Create: `ProtocolTests/Vectors/SignalFixture.cs`
- Create: `ProtocolTests/SignalFixtureTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: nothing.
- Produces: `SignalConstants.InfoText`, `SignalConstants.InfoRatchet`,
  `SignalConstants.InfoMessageKeys`, `SignalConstants.InfoSenderKey`,
  `SignalConstants.InfoSenderKeyDistribution`, `SignalConstants.AgreementPrefix`
  (32 bytes, `0xFF`),  `SignalConstants.MessageVersion` (the ASCII version byte),
  `SignalConstants.MacLength`; and, in the fixture, `SignalFixture.X3dh` (the
  reference's own Alice/Bob key material, and the two root keys those keys agree
  on), `SignalFixture.Conversation` (the reference's own transcript in both
  directions: Alice's three ciphertexts, Bob's two, and what each decrypts to),
  `SignalFixture.BobSide` (the receiver's private halves, which are what make the
  other direction testable), each as byte arrays and strings. There is no group
  fixture yet: Task 6 captures one when it is written.

- [x] **Step 1: Capture the constants from the reference**

Print the reference's own constants from a temporary directory outside this
repository — the same method Task 4 of the retire plan used for the QR tables:

```
mkdir -p /tmp/signalref && cd /tmp/signalref && npm init -y
npm install libsignal-protocol

grep -rn "deriveSecrets" node_modules/libsignal-protocol/src   # every info string
grep -rn "CURRENT_VERSION" node_modules/libsignal-protocol/src  # the version byte
```

**The values are what those commands print.** Write them into
`SignalConstants.cs` with the two commands in the file header and, on each
constant, the reference file the grep found it in. This plan deliberately does
not state them: a constant copied out of a plan is a constant nobody checked,
and a wrong one makes every message below decode locally and be refused by
WhatsApp.

- [x] **Step 2 (RED): the fixture's own test**

`ProtocolTests/SignalFixtureTests.cs` — one test that fails while the fixture is
absent or empty, and that pins the shape rather than the arithmetic:

```csharp
[Test]
public static void TheFixturesArePopulated()
{
    Assert.AreEqual(32, SignalConstants.AgreementPrefix.Length);
    Assert.AreEqual(0xFF, SignalConstants.AgreementPrefix[0]);
    Assert.AreEqual(32, SignalFixture.X3dh.ExpectedSharedSecret.Length);
    Assert.IsTrue(SignalFixture.Conversation.Ciphertexts.Count == 2);
    Assert.AreEqual("the reference's first plaintext", SignalFixture.Conversation.Plaintexts[0]);
}
```

Run: `node tools/test-protocol.js` — Expected: fail, `SignalFixture` does not
exist.

- [x] **Step 3 (GREEN): print the fixtures**

A Node script in `/tmp/signalref` that, with the reference library, (a) runs the
specification's X3DH test vector, (b) builds a session between two reference
parties with fixed keys and emits two ciphertexts with their keys, and (c) builds
a sender key chain and one `skmsg`. Its output is written into
`ProtocolTests/Vectors/SignalFixture.cs` in 16-byte groups, the way
`XEdDsaTests.cs` writes its certificates, with the script's command in the file
header. Run the test again — Expected: pass.

- [x] **Step 4: The second oracle, for the arithmetic** — **done, differently.** The published specifications carry no such vectors: X3DH revision 1 and the Double Ratchet revision 4 were read end to end for this step, and neither has an appendix of test vectors. What the primitives *do* have is RFC 7748 for the curve and RFC 5869 for HKDF, both already pinned in `CryptoTests`, so what was missing was the composition - and that is `SignalFixture.X3dh.RootKey` and `RootKeyWithoutPreKey`, computed from the fixture's own keys by `signal-root-key.js` over OpenSSL's X25519 and HKDF, and compared by `X3dhTests` against both sides of the agreement. See the execution notes for how the two values were produced.

- [x] **Step 5: Commit**

```bash
git add WhatsappApp/Protocol/Signal/SignalConstants.cs ProtocolTests/Vectors/SignalFixture.cs \
        ProtocolTests/SignalFixtureTests.cs WhatsappApp/WhatsApp.csproj
git commit -m "test: freeze the reference's Signal constants and a transcript to check against"
```

---

### Task 2: X3DH, and the two sides that have to agree — **done**

**Files:**
- Create: `WhatsappApp/Protocol/Signal/X3dh.cs`
- Create: `ProtocolTests/X3dhTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `Curve25519.ScalarMult`/`PublicKey`, `Hkdf`, `SignalConstants`.
- Produces: `X3dh.AliceSecret(byte[] aliceIdentityPrivate, byte[] aliceEphemeralPrivate, byte[] bobIdentityPublic, byte[] bobSignedPreKeyPublic, byte[] bobOneTimePreKeyPublic)`
  and `X3dh.BobSecret(byte[] bobIdentityPrivate, byte[] bobSignedPreKeyPrivate, byte[] bobOneTimePreKeyPrivate, byte[] aliceIdentityPublic, byte[] aliceEphemeralPublic)`,
  both returning 32 bytes; a null or empty `bobOneTimePreKeyPublic` means the
  three-DH form. Throws `ProtocolException` on a key that is not 32 or 33 bytes,
  and on a 33-byte key whose first byte is not `0x05`. Also produces the three
  helpers the rest of the milestone uses: `X3dh.Bare(byte[])`, `X3dh.Typed(byte[])`
  and `X3dh.TypedPublicKey(byte[])`, plus `X3dh.Text(string)` for the info strings.

- [x] **Step 1: Write the failing tests**

`ProtocolTests/X3dhTests.cs`. The first test is not the specification's vector —
it publishes none — but `SignalFixture.X3dh.RootKey`, computed from the fixture's
own keys by `signal-root-key.js` over OpenSSL's X25519 and HKDF (see Step 4 of
Task 1 and the execution notes):

- `TheSenderAgreesOnTheRootKeyTheReferenceKeysProduce` — `AliceSecret` equals
  `RootKey`, byte for byte.
- `TheReceiverAgreesOnTheSameRootKey` — `BobSecret` equals it too, which is the
  half the first ciphertext cannot check on its own.
- `WithoutAOneTimePreKeyBothSidesStillAgree` — the same two tests against
  `RootKeyWithoutPreKey`, which is the ordinary case once a device has handed out
  every pre-key it published (review focus 3).
- `TheTwoFormsAreTwoKeys` — the fourth DH changes the root; if it did not, both
  sides would agree with each other and with nobody else.
- `AKeyOfTheWrongLengthIsRefused` — every argument of both sides, one at a time;
  the optional pre-key is the one argument null is allowed to be.

Run: `node tools/test-protocol.js` — Expected: fail, `X3dh` does not exist.

- [x] **Step 2: Implement the agreement**

DH order per the specification: `DH1 = DH(IKA, SPKB)`, `DH2 = DH(EKA, IKB)`,
`DH3 = DH(EKA, SPKB)`, `DH4 = DH(EKA, OPKB)`; the secret is
`HKDF(AgreementPrefix || DH1 || DH2 || DH3 || DH4, salt = 32 zero bytes,
info = SignalConstants.InfoText)` truncated to 32 bytes. `BobSecret` computes the
same four in the receiver's order - `DH1 = DH(SPKB, IKA)` and so on - with one
shared `Derive` writing the buffer, so there is exactly one place where the order
is written down.

- [x] **Step 3: Run the tests**

Run: `node tools/test-protocol.js` — Expected: pass. It does: 89 test(s).

- [x] **Step 4: Commit** — `88dbf9b`, together with Tasks 3 and 4 and the session
  that ties them together, because the conversation is what makes any of it
  credible: the agreement alone cannot show that its output is the reference's.

---

### Task 3: The ratchet — **done, and not as two files**

**Files:**
- Create: `WhatsappApp/Protocol/Signal/Keys.cs`
- Create: `WhatsappApp/Protocol/Signal/SessionRecord.cs` (with Task 4)
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `Hmac`, `Hkdf`, `Curve25519`, `SignalConstants`, `IRandomSource`.
- Produces:
  - `RootAndChain Keys.Root(byte[] dhOutput, byte[] currentRootKey)` — `RootKey`
    and `ChainKey` as two fields of a `RootAndChain` struct, 32 bytes each.
  - `byte[] Keys.NextChainKey(byte[] chainKey)`, `byte[] Keys.MessageKey(byte[] chainKey)`,
    and `MessageKeys Keys.MessageKeys(byte[] messageKey)` (cipher key, MAC key, IV).
  - `SessionRecord.StartAsInitiator(...)` / `StartAsResponder(...)`,
    `byte[] Encrypt(byte[])`, `byte[] Decrypt(byte[] wire, bool preKey)`,
    `bool HasSession`, `bool PreKeyPending`, `byte[] TheirIdentityPublic`,
    `byte[] OurIdentityPublic`.

- [x] **Step 1: Write the failing tests** — the chain-key half of the
specification's appendix does not exist either (see Task 1, Step 4), so the oracle
is the reference's conversation, and the tests live in `SignalSessionTests`:

- `TheRuleBookChainKeysAgree` — **not written, and deliberately**: `MessageKey` and
  `NextChainKey` are `HMAC-SHA256(chainKey, 0x01)` and `(chainKey, 0x02)`, and both
  seeds are pinned by every ciphertext the conversation test compares. A vector
  written by us would agree with us.
- `TheRootAdvancesWithBothKeys` — the same, and for the same reason: the third
  ciphertext is only reproducible if the step's root and chain are the reference's,
  and Bob's replies are only readable if his step agrees with Alice's.
- `ASkippedMessageKeyComesBack` (review focus 1) —
  `AMessageThatArrivesEarlyIsStillReadable`: first, third, then second.
- `AReplayedMessageDecryptsToTheSameBytes` — **the specification's behaviour is
  not the reference's**: the reference deletes the message key it used, so a
  duplicate is refused with an error rather than decrypted twice, and
  `AReplayedMessageIsRefused` pins that. A test asserting the specification's
  reading would pin behaviour no WhatsApp client has.

- [x] **Step 2: Implement `Keys` and the ratchet**

`NextChainKey` and `MessageKey` are the two HMACs of `KDF_CK`; `Root` is
`HKDF(dhOutput, salt = currentRootKey, info = "WhisperRatchet", 64 bytes)` split in
half; `MessageKeys` is `HKDF(messageKey, salt = 32 zeros, info =
"WhisperMessageKeys", 80 bytes)` split into cipher key, MAC key and IV.

The ratchet itself is `SessionRecord` rather than a `Ratchet.cs`: the step is not
stateless - it archives a chain, records a counter, keeps skipped keys and draws a
new key pair - and splitting those from the record that owns them would have meant
two objects holding one session's state, with the bugs that come with it. The
chains are kept as a list rather than the reference's map, because a session has a
handful of them and a list of five beats a dictionary on a phone.

- [x] **Step 3: Run the tests** — `node tools/test-protocol.js`: 89 test(s), then
  90 with the fixture's own keys (see Task 1's `X3dhTests`).

- [x] **Step 4: Commit** — `88dbf9b`, with Tasks 2 and 4.

---

### Task 4: The message, as the wire wants it — **done**

**Files:**
- Create: `WhatsappApp/Protocol/Signal/Cipher.cs`, `WhatsappApp/Protocol/Signal/WhisperMessage.cs`
- Create: `ProtocolTests/SignalSessionTests.cs` (rather than `WhisperMessageTests.cs`: the
  message's own tests are the conversation's, and a second file would have tested the
  framing against a fixture that only has the whole messages)
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `Aes` (CBC), `Hmac`, `ProtoWriter`/`ProtoReader`, `SignalConstants`.
- Produces: `Cipher.Encrypt(byte[] cipherKey, byte[] iv, byte[] plaintext)`,
  `Cipher.Decrypt(...)`, `Cipher.Mac(byte[] macKey, byte[] senderIdentity,
  byte[] receiverIdentity, byte[] message)` and `Cipher.VerifyMac(...)` (the
  reference's 8-byte truncation); `SignalMessage.Encode/Decode`,
  `SignalMessage.Seal(...)`, `SignalEnvelope.Seal/Open` for the sealed message and
  `SignalEnvelope.Wrap/Unwrap` for the pre-key wrapper, which carries no MAC;
  `PreKeySignalMessage.Encode/Decode` with the pre-key id, the base key, the
  sender's identity and registration id, and the signed pre-key id.

- [x] **Step 1: Write the failing tests**

- `TheReferenceCiphertextIsReproduced` — Task 1's transcript: the same keys and
  plaintext produce the reference's own bytes, `Assert.ByteArraysEqual`. This is
  the test the whole milestone hangs on (review focus 4).
- `TheVersionByteIsWritten` — the first byte is `SignalConstants.MessageVersion`.
- `TheMacIsTruncatedToTheReferenceLength` — a payload with one byte changed is
  refused; the MAC length is `SignalConstants.MacLength` (review focus 4).
- `APreKeyMessageCarriesItsPreKeyId` — round trip through encode/decode.

Run: `node tools/test-protocol.js` — Expected: fail.

- [x] **Step 2: Implement `Cipher` and the two message shapes**

AES-256-CBC with the reference's padding, HMAC-SHA256 over
`senderIdentity || receiverIdentity || versionByte || protobuf` truncated to
`MacLength` — the exact input order is the reference's, and the fixture is what
says it is right. Two shapes, and the asymmetry between them is the thing the
plan got wrong and the fixture caught: the sealed message carries the MAC, the
pre-key wrapper around it does not, and the wrapper's registration id is the
*sender's*.

- [x] **Step 3: Run the tests** — Expected: pass, and it does: 84 test(s), then 90
  with `X3dhTests` and the fixture's own-key check.

- [x] **Step 4: Commit** — `88dbf9b` (with Tasks 2 and 3).

The tests that carry this task, in `ProtocolTests/SignalSessionTests.cs`:

- `WhatTheSenderProducesIsTheReferenceSCiphertext` — the whole chain at once
  (agreement, first step, both HMACs, the message keys' HKDF, AES-CBC, the
  protobuf, the version byte, the MAC), against the reference's own bytes.
- `WhatTheReceiverOpensIsTheReferenceSText` and
  `WhatTheReceiverSendsBackIsTheReferenceSCiphertext` — the same keys, the other
  direction, which is the half a session can get wrong while encrypting perfectly.
- `WhatTheSenderProducesAfterTheStepIsTheReferenceSCiphertext` — the third message,
  which pins the *receiving* step: it is the reference's bytes because the fixture
  now carries the private ratchet key its step generated.
- `ThePreKeyMessageCarriesWhatTheReferenceCarries` — the wrapper's fields, read off
  the reference's bytes rather than ours.
- `ATamperedMessageIsRefused`, `AReplayedMessageIsRefused`,
  `AMessageThatArrivesEarlyIsStillReadable`, `TheConversationContinuesAfterTheStep`.

---

### Task 5: Sessions that survive the app being closed — **done**, `91ae8d4`

**Files:**
- Create: `WhatsappApp/Protocol/Signal/SessionStore.cs` (the record's own `Encode`/
  `Decode` are methods on `SessionRecord` rather than a `Session.cs`: the file that
  owns the state is the file that knows how to write it)
- Create: `WhatsappApp/Services/FileSessionStorage.cs` (the phone's half)
- Create: `ProtocolTests/SessionStoreTests.cs`
- Create: `ProtocolTests/SignalSessions.cs` (the builders the session tests and the
  store tests share, so neither builds the other's)
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `ProtoWriter`/`ProtoReader`, `Sha256`, `Hmac`, `IRandomSource`.
- Produces: `SessionRecord` with `public byte[] Encrypt(byte[] plaintext)`,
  `public byte[] Decrypt(byte[] wire, bool preKey)`, `public bool HasSession`,
  `public bool PreKeyPending`, `public byte[] Encode()` and
  `public static SessionRecord Decode(byte[] bytes, IRandomSource random)` (the
  last one returns null for anything it cannot parse whole);
  `interface ISessionStorage { bool TryRead(string key, out byte[] value); void Write(string key, byte[] value); void Remove(string key); }`;
  `SessionStore(ISessionStorage storage, IRandomSource random)` with
  `public SessionRecord Load(string peer)`, `public void Save(string peer, SessionRecord session)`
  and `public void Remove(string peer)`; `PreKeyStore(ISessionStorage storage)` with
  `public byte[] Take(uint id)` (null when the id is spent or unknown),
  `public void Publish(uint id, byte[] preKey)`, `public int Remaining`,
  `public void SetSignedPreKey(uint id, byte[] privateKey, byte[] signature)` and
  the three getters for it.

  Two deviations from this plan's first draft, both recorded as they were made: the
  storage's `out` parameter is `byte[] value` rather than `record`, because the
  pre-key store writes through the same interface and a pre-key is not a record;
  and `Count` is gone, because there is no way to ask a file-per-peer store how many
  peers it has without enumerating it, and nothing needed the answer.

- [x] **Step 1: Write the failing tests**

- `ARecordRoundTrips` — a session built, moved (one message sent, so the record that
  is saved has a counter and a pending pre-key in it), encoded, decoded and compared
  byte for byte, then continued in both directions. Comparing the re-encoded bytes
  rather than a field is the point: the state is a ratchet, and a field-by-field
  test would pass while the one that moved was wrong.
- `AHalfWrittenRecordIsNoSession` (review focus 2) — truncated at six places *and*
  byte by byte through all of it, plus a version byte this build does not know, plus
  the two trivial cases (null, empty), and through the store, which is where a file
  actually arrives.
- `TheStoreKeepsOneSessionPerPeer` — two peers, two records, no crossing; overwriting
  one leaves the other; removing one removes only that one; removing an absent one is
  not an error.
- `ThePreKeyStoreNeverHandsTheSameOneTimeKeyTwice` — the second draw of a consumed id
  fails, and the signed pre-key keeps its id, its private half and its signature
  across a restart.

Run: `node tools/test-protocol.js` — Expected: fail.

- [x] **Step 2: Implement the record and the pre-key store**

The record is ours, not the reference's: `ProtoWriter` fields for the identity keys,
 the root, the ratchet keys, the counters, the pending pre-key and the base key, with
 the two chains nested. The framing is a version byte, the body's length, the body,and the SHA-256 of the body - one more field than this plan asked for, and for a
reason it says: the length catches a write that was cut short, and the digest catches
a bit flipped inside a 32-byte key, which leaves every length intact and would
 otherwise parse into a session that cannot be read. A record whose digest does not
 match, whose length does not add up, or whose version is not this build's is null:
 `Decode` never throws, because what reaches it is a file.

The phone's `ISessionStorage` is held in memory and written through, not one file per
peer read on demand, and the deviation is worth the paragraph: the core's interface is
synchronous (an encrypt cannot await a file), the platform's file API is asynchronous,
and blocking on it with `.Wait()` deadlocks the moment a caller on the UI thread asks
for a session. So `OpenAsync` reads the folder once and the writes go behind the
caller, one at a time, through `SerialQueue` - the same shape `ChatPreferences` uses.
A write goes to `.writing` first and is renamed over the real name, and the cleanup
is gated on the rename having failed, because after a rename the same file object
names the record itself.

- [x] **Step 3: Run the tests** — Expected: pass. It does: 94 test(s).

- [x] **Step 4: Commit** — `91ae8d4`.

---

### Task 6: Sender keys, for the groups

**Files:**
- Create: `WhatsappApp/Protocol/Signal/SenderKey.cs`
- Create: `ProtocolTests/SenderKeyTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `Keys`, `Cipher`, `WhisperMessage`, `SignalConstants`.
- Produces: `SenderKeyMessage.Create(byte[] chainKey, int index, byte[] cipherKey, byte[] plaintext)`
  and `SenderKeyMessage.Open(byte[] chainKey, SenderKeyMessage message)`;
  `SenderKeyDistributionMessage.Encode(byte[] chainKey, int index, byte[] signatureKey, byte[] signingPrivate)`
  and `Decode(byte[] bytes)`; `SenderKeyStore` with `Add(string group, string participant, ...)`
  and `TryGet(...)` returning false when the member's distribution message has not
  arrived — which the caller reports, not decrypts (review focus 5).

- [ ] **Step 1: Write the failing tests**

- `TheReferenceSenderKeyCiphertextIsReproduced` — Task 1's group fixture.
- `TheChainAdvancesPerMessage` — two messages from one chain use two indices, and
  the second's key is not the first's.
- `AMemberWithNoSenderKeySaysSoInsteadOfDecrypting` (review focus 5) —
  `TryGet` false, and the caller's path is a named failure.
- `TheDistributionMessageIsSigned` — a distribution message with a moved byte
  fails its signature check.

Run: `node tools/test-protocol.js` — Expected: fail.

- [ ] **Step 2: Implement the sender key path**

The group AEAD is the same `Cipher`, with `SignalConstants.InfoSenderKey` in the
key schedule and a signature over the distribution message; the exact info
strings and the signature's input are the reference's, read in Task 1.

- [ ] **Step 3: Run the tests** — Expected: pass.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Protocol/Signal/SenderKey.cs ProtocolTests/SenderKeyTests.cs \
        WhatsappApp/WhatsApp.csproj
git commit -m "feat: sender keys, and a group message from a member we have no key for"
```

---

### Task 7: The gates, and what M4 inherits

**Files:**
- Modify: `docs/superpowers/plans/2026-10-10-native-whatsapp-client.md` (its M3
  row), `docs/superpowers/plans/2026-10-10-retire-the-server.md` (the open item
  that names M3 as Task 6's gate)

**Interfaces:**
- Consumes: everything above.
- Produces: the fact that M4 can start, and the list M4's plan needs: the exact
  signatures of `SessionRecord.Encrypt`/`Decrypt`, `SessionStore.Load`/`Save` and
  `SenderKeyStore.TryGet`.

- [ ] **Step 1: The full gate set**

Run: `node tools/test-protocol.js`, `node tools/check-csharp5.js`, every
`node tools/check-*.js`, `node --test tools/test/*.test.js`, and the x86 and ARM
rebuild. Expected: exit 0 each, `0 Error(s)`.

- [ ] **Step 2: Say in the two plans what is true**

The client plan's M3 row becomes done with the test count; the retire plan's open
item now names M4 as Task 6's remaining gate. Neither claims more than the tests
show: **M3 decrypts the reference's messages, not WhatsApp's** — that is M4's
checkpoint, on the phone.

- [ ] **Step 3: Commit**

```bash
git add docs/
git commit -m "docs: M3 closed against the reference, and M4 is what Task 6 waits for"
```

---

## Ordering, and what is not in this plan

Tasks 1 to 6 are one chain and each ends green offline; none of them needs the
phone, which is why they come first. Task 7 closes the milestone.

**M4 — messages is a separate plan**, written when this one closes, in the same
shape: the pre-key bundle, `NativeSession` where the eight adapter services are
today, sending, receipts and acks. It is the last gate of
`docs/superpowers/plans/2026-10-10-retire-the-server.md`'s Task 6, which cannot
delete the adapter until a text crosses the wire in both directions.

## Execution notes

(filled in as the tasks run:
### Task 1 - the oracle, captured: what the reference actually says

The reference is `libsignal-protocol` 1.3.15 from npm, installed outside the
repository. Everything in `WhatsappApp/Protocol/Signal/SignalConstants.cs` and
`ProtocolTests/Vectors/SignalFixture.cs` was printed from it, and the commands are
in the files' own headers.

What the capture settled, in the reference's words:

- **The agreement**: `SessionBuilder.js:141` fills a `32 + 32 * 4` buffer with
  `0xff`, then writes the four DH outputs at offsets 32, 64, 96 and 128;
  `SessionBuilder.js:166` derives with `HKDF(sharedSecret, zeros(32),
  "WhisperText")`. `SignalConstants.AgreementPrefix` is that 32-byte fill.
- **The ratchet**: `HKDF(dhOutput, currentRootKey, "WhisperRatchet")`, read as
  `masterKey[0]` = new root, `masterKey[1]` = chain key (`SessionBuilder.js:209`,
  `SessionCipher.js:342`).
- **The message keys**: `HKDF(messageKey, zeros(32), "WhisperMessageKeys")`, where
  `keys[0]` is the cipher key, `keys[1]` the MAC key and `keys[2]` the IV used
  `slice(0, 16)` (`SessionCipher.js:57-64`).
- **The chain**: `fillMessageKeys` sets a one-byte input to `1` for the message key
  and then to `2` for the next chain key, each through HMAC-SHA256.
- **The HKDF is RFC 5869** with `HMAC(salt, input)` as extract (`crypto.js`,
  `HKDF`) - which is what `Protocol/Crypto/Hkdf.cs` already does, so the existing
  primitive needs no change.
- **The wire**: the version byte `(3 << 4) | 3` = `0x33` at the front of the
  message *and* inside the MAC's input; the MAC's input is `senderIdentity(33) ||
  receiverIdentity(33) || versionByte || message`; the MAC is truncated to **8**
  bytes.

`Ruling:` the fixture is the reference's own frozen conversation rather than one
printed by a script driving its session API. Why: `test/testvectors.js` is what the
reference's own suite runs against - Alice's base, ephemeral and identity private
keys, Bob's pre-key bundle with its signature, the expected ciphertext of two
messages, the two replies with the text they decrypt to - so the fixture is
published material and not a transcript of our own run. Cost if wrong: none, and
the generator below re-runs against any later version.

`Ruling:` the sender-key info strings are **not** in this reference (1.3.15 has no
`GroupCipher`; only a protobuf mentions `WhisperGroup`). Why: Task 6 must capture
them from the Go or TypeScript implementation instead of assuming this one. Cost if
wrong: Task 6's first step becomes a capture, which is where it belongs anyway.

`Ruling:` the *arithmetic's* oracle is a script, not a specification vector,
because the specifications publish none. Why: X3DH revision 1 and the Double
Ratchet revision 4 were read end to end for this (there is no appendix of test
vectors in either), so what is frozen for the arithmetic is
`SignalFixture.X3dh.RootKey` and `RootKeyWithoutPreKey`, computed from the
fixture's own keys over OpenSSL's X25519 and HKDF by `signal-root-key.js`. The
primitives keep their own published vectors - RFC 7748 and RFC 5869, both already
in `CryptoTests`. Cost if wrong: the composition is checked against a second
implementation rather than against the one the wire format comes from; the wire
still has the reference's own bytes, and `88dbf9b` is what shows the two agree.

### The generator, as it ran

Two scripts, both in a directory outside this repository (`/tmp/signalref`, with
`npm install libsignal-protocol` in it). They are here because the fixture's whole
value is that nothing in it was typed, and a generator that lives only on one
machine's disk is a generator a reviewer cannot check.

The first reads the reference's own vectors file and writes
`ProtocolTests/Vectors/SignalFixture.cs`. What follows is its head, its reads and
the decisions it makes; the string assembly around them is marked where it is
elided, because it is the part that cannot be wrong without the output being
visibly malformed:

```javascript
// Prints ProtocolTests/Vectors/SignalFixture.cs from the reference's own frozen
// vectors: libsignal-protocol's test/testvectors.js, the file its own suite runs.
//
//   mkdir -p /tmp/signalref && cd /tmp/signalref && npm init -y
//   npm install libsignal-protocol
//   node emit-signal-fixture.js        # with REPO pointing at the repository root
//
// Nothing in the output is typed by hand: a constant copied by eye is a bug the
// fixture cannot catch.
'use strict';

const fs = require('fs');
const vm = require('vm');
const path = require('path');

const ROOT = process.env.REPO || '.';
const VECTORS = 'node_modules/libsignal-protocol/test/testvectors.js';

const code = fs.readFileSync(VECTORS, 'utf8');
const sandbox = { console: console, hexToArrayBuffer: (hex) => hex };
vm.createContext(sandbox);
vm.runInContext(code.replace('var TestVectors =', 'this.TestVectors ='), sandbox);

const tests = sandbox.TestVectors;
const alice = tests.filter((t) => t.name === 'Standard Signal Protocol Test Vectors as Alice')[0];
const bob = tests.filter((t) => t.name === 'Standard Signal Protocol Test Vectors as Bob')[0];
if (!alice) throw new Error('no Alice test in ' + VECTORS);
if (!bob) throw new Error('no Bob test in ' + VECTORS);

const aliceSends = alice.vectors.filter((v) => v[0] === 'sendMessage');
const aliceRecvs = alice.vectors.filter((v) => v[0] === 'receiveMessage' && v[1].expectedSmsText);
const bobRecvs = bob.vectors.filter((v) => v[0] === 'receiveMessage' && v[1].expectedSmsText);
const bobSends = bob.vectors.filter((v) => v[0] === 'sendMessage');

const bundleEntry = aliceSends[0][1];
const bundle = bundleEntry.getKeys.devices[0];
const bobFirst = bobRecvs[0][1];

// ... the rest is string building: the three private halves, the bundle, both
// transcripts, and the private ratchet key each side's first step generated (the
// vectors call it `newEphemeralKey`, and it is what makes the third ciphertext and
// the receiver's replies comparable).
```

The second computes the two values the vectors do not carry - the root key with
and without the one-time pre-key - over OpenSSL's X25519 and HKDF:

```javascript
// Computes the session's initial root key from the fixture's own keys: an oracle
// independent of both our code and of libsignal's vectors file, which does not
// carry the root key.
//
//   node signal-root-key.js
//
// The X25519 private keys here are already clamped, and the scalar is imported as
// PKCS#8 - OpenSSL does not clamp an imported scalar, so an unclamped one would
// silently disagree with the reference. (It cost an hour here: two Diffie-Hellmans
// that must be equal were not, because the *test* keys were raw random bytes.)
'use strict';

const crypto = require('crypto');
const PKCS8 = '302e020100300506032b656e04220420';
const SPKI = '302a300506032b656e032100';
const H = (value) => Buffer.from(value, 'hex');

const privateKey = (raw) => crypto.createPrivateKey({
  key: Buffer.concat([H(PKCS8), H(raw)]), format: 'der', type: 'pkcs8'
});
const publicKey = (hex) => crypto.createPublicKey({
  key: Buffer.concat([H(SPKI), H(hex.slice(-64))]), format: 'der', type: 'spki'
});
const sharedSecret = (privateRaw, publicHex) => crypto.diffieHellman({
  privateKey: privateKey(privateRaw), publicKey: publicKey(publicHex)
});
// Bob's half is computed against Alice's *public* keys, not against her private
// ones wearing a type byte: the first version of this script did the latter and
// "found" a disagreement in a Diffie-Hellman that is symmetric by construction.
const publicOf = (privateRaw) =>
  crypto.createPublicKey(privateKey(privateRaw)).export({ format: 'der', type: 'spki' }).slice(-32).toString('hex');
const hkdf = (input, length) => Buffer.from(
  crypto.hkdfSync('sha256', input, Buffer.alloc(32), Buffer.from('WhisperText'), length));

// ... the fixture's keys verbatim, Alice's four DHs and Bob's four, a check that
// the eight agree pairwise, then the two HKDFs and their hex.
```

`signal-root-key.js` compares the two sides' four DHs before it derives anything,
because a DH that is symmetric by construction is the one thing a script like this
cannot "check" — it was written wrong once, and the check is what said so.

### Task 1's result

- `node tools/test-protocol.js` -> **OK: 74 protocol test(s)** (71 before; the three
  new ones check the constants and the fixture's shape, not any arithmetic).
- `check-project-files`, `check-csharp5`, `check-docs` -> exit 0.
- Two mistakes the tests themselves caught, worth knowing: the generator first read
  Bob's identity key off the device entry instead of `getKeys`, printing
  `Hex("undefined")`; and `Assert.AreEqual` compares `object`s, so `(int)255` and
  `(byte)255` are not equal - the failure reads `expected <255> but was <255>`. Both
  are fixed, and the second is commented in the test so nobody spends an afternoon
  on it.

### Tasks 2, 3, 4 - the session, and the three things the fixture caught

The three tasks landed together (`88dbf9b`), because the conversation is what
makes any of them credible: the agreement alone cannot show its output is the
reference's, and the message format alone cannot show which key it is sealing for.
What the fixture caught, in the order it was written:

- **A pre-key message is not sealed twice.** The plan assumed a wrapper around the
  sealed message, with a second MAC of its own. The reference writes a bare version
  byte in front of the `PreKeyWhisperMessage` protobuf (`SessionCipher.js`, the
  pre-key branch's `String.fromCharCode`) and nothing else: no tag. The first
  comparison failed on the last eight bytes, which is exactly the second MAC's size -
  and the eight bytes the reference writes there turned out to be the registration id
  and the signed pre-key id, because both messages' trailing bytes were *identical*.
- **The wrapper's registration id is the sender's.** `preKeyMsg.registrationId =
  myRegistrationId` (`SessionCipher.js:101`), not the receiver's: the field says
  which device the message came from. The session now carries our own id, which is
  why `StartAsInitiator` takes it as an argument.
- **The step's ratchet key is not the first one.** `newEphemeralKey` in the vectors
  is the *private* half of the key a session's ratchet step generates - verified by
  computing its public half, which is the ratchet key of the very message that step
  produces. That is what made the third ciphertext comparable, and with it the
  receiving half of the ratchet: before it, Alice's step drew her own first key
  again and the two sessions disagreed about a key neither had changed.

Two test-side traps, for whoever writes the next one. A fixed random source that
returns the same key for every step makes a session step onto the key it already
has, and the failure it produces (`the message key for 0 was already used`) has
nothing to do with the code under test - the conversation test now uses a counter
source and says why. And a message's *type* travels with it: the sender's session
says `PreKeyPending`, the stanza says `type="pkmsg"`, and the receiver is told by
it which path to take, because a wrapped message and a plain one cannot be told
apart from the bytes alone.

Counts: `node tools/test-protocol.js` -> **OK: 90 protocol test(s)** (84 after the
three tasks, 89 with `X3dhTests`, 90 with the fixture's own-key check). The
settled pieces of the plan above are the ones marked done; nothing in this section
is an argument for a claim the tests do not make.
