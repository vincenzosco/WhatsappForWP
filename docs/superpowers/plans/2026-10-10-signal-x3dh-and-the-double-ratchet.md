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
  (32 bytes, `0xFF`), `SignalConstants.MessageVersion` (the ASCII version byte),
  `SignalConstants.MacLength`; and, in the fixture, `SignalFixture.X3dh` (the
  specification's Alice/Bob key material and the expected `SK`),
  `SignalFixture.Conversation` (the reference's own Alice-to-Bob transcript:
  root key, their ratchet public key, and the ciphertext of two messages with the
  plaintexts), `SignalFixture.Group` (a sender key chain and one `skmsg`
  ciphertext), each as byte arrays and strings.

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

- [ ] **Step 4: The second oracle, for the arithmetic** — **open.** The X3DH half is Task 2's first step and the chain-key half is Task 3's, which is what the execution notes record: the reference conversation pins the wire, and the specifications pin the arithmetic.

The reference's transcript proves the wire format; the published specifications
prove the arithmetic. Freeze the X3DH specification's appendix test vector (its
`DH1..DH3`, `F || KM` and the resulting `SK`) and the Double Ratchet
specification's appendix chain-key vectors into the same fixture, each labelled
with the section it came from.

- [x] **Step 5: Commit**

```bash
git add WhatsappApp/Protocol/Signal/SignalConstants.cs ProtocolTests/Vectors/SignalFixture.cs \
        ProtocolTests/SignalFixtureTests.cs WhatsappApp/WhatsApp.csproj
git commit -m "test: freeze the reference's Signal constants and a transcript to check against"
```

---

### Task 2: X3DH, and the two sides that have to agree

**Files:**
- Create: `WhatsappApp/Protocol/Signal/X3dh.cs`
- Create: `ProtocolTests/X3dhTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `Curve25519.ScalarMult`/`PublicKey`, `Hkdf`, `SignalConstants`.
- Produces: `X3dh.AliceSecret(byte[] aliceIdentityPrivate, byte[] aliceEphemeralPrivate, byte[] bobIdentityPublic, byte[] bobSignedPreKeyPublic, byte[] bobOneTimePreKeyPublic)`
  and `X3dh.BobSecret(byte[] bobIdentityPrivate, byte[] bobSignedPreKeyPrivate, byte[] bobOneTimePreKeyPrivate, byte[] aliceIdentityPublic, byte[] aliceEphemeralPublic)`,
  both returning 32 bytes; a null `bobOneTimePreKeyPublic` means the three-DH
  form. Throws `ProtocolException` on a key of the wrong length.

- [ ] **Step 1: Write the failing tests**

`ProtocolTests/X3dhTests.cs`:

- `TheRuleBookVectorAgrees` — the specification's appendix values, through both
  `AliceSecret` and `BobSecret`, equal the vector's `SK`.
- `BothSidesAgree` — a fresh pair of key sets (fixed bytes, not random): Alice's
  and Bob's halves are equal, with and without a one-time pre-key.
- `TheThreeDhFormAgreesWithItself` — the four-DH value and the three-DH value are
  different, and each agrees with its own receiver (review focus 3: a bundle whose
  OPK is gone).
- `AKeyOfTheWrongLengthIsRefused` — `Assert.Throws<ProtocolException>`.

Run: `node tools/test-protocol.js` — Expected: fail, `X3dh` does not exist.

- [ ] **Step 2: Implement the agreement**

DH order per the specification: `DH1 = DH(IKA, SPKB)`, `DH2 = DH(EKA, IKB)`,
`DH3 = DH(EKA, SPKB)`, `DH4 = DH(EKA, OPKB)`; the secret is
`HKDF(AgreementPrefix || DH1 || DH2 || DH3 || DH4, salt = 32 zero bytes,
info = SignalConstants.InfoText)` truncated to 32 bytes. `BobSecret` computes the
same four in the receiver's order.

- [ ] **Step 3: Run the tests**

Run: `node tools/test-protocol.js` — Expected: pass.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Protocol/Signal/X3dh.cs ProtocolTests/X3dhTests.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: the X3DH agreement, against the specification's own vector"
```

---

### Task 3: The ratchet

**Files:**
- Create: `WhatsappApp/Protocol/Signal/Keys.cs`, `WhatsappApp/Protocol/Signal/Ratchet.cs`
- Create: `ProtocolTests/RatchetTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `Hmac`, `Hkdf`, `SignalConstants`.
- Produces:
  - `RootAndChain Keys.Root(byte[] rootKey, byte[] dhOutput)` — `root` and `chain`
    as two fields of a `RootAndChain` struct, 32 bytes each.
  - `byte[] Keys.NextChainKey(byte[] chainKey)`, `byte[] Keys.MessageKey(byte[] chainKey)`.
  - `Ratchet.Step(byte[] rootKey, byte[] ourRatchetPrivate, byte[] theirRatchetPublic)`
    returning a `RatchetStep` with `RootKey`, `OurRatchetPrivate`,
    `OurRatchetPublic` and `TheirChainKey`.
  - `Ratchet.SendingChain(byte[] chainKey, int index)` / `Ratchet.ReceivingChain(...)`,
    which advance a chain to an index and keep the skipped message keys in a
    `SkippedKeys` list (review focus 1).

- [ ] **Step 1: Write the failing tests**

- `TheRuleBookChainKeysAgree` — the Double Ratchet specification's appendix
  vectors for `KDF_CK` (`0x01` for the message key, `0x02` for the next chain key).
- `TheRootAdvancesWithBothKeys` — the same DH on both sides gives the same root
  and chain.
- `ASkippedMessageKeyComesBack` — send messages 1, 2, 3, receive 1 and 3: message 3
  decrypts, and message 2's key is still there afterwards.
- `AReplayedMessageDecryptsToTheSameBytes` (review focus 1) — the same ciphertext
  twice gives the same plaintext, not an exception.

Run: `node tools/test-protocol.js` — Expected: fail.

- [ ] **Step 2: Implement `Keys` and `Ratchet`**

`NextChainKey` and `MessageKey` are the two HMACs of the specification's
`KDF_CK`; `Root` is `HKDF(rootKey, dhOutput, info = SignalConstants.InfoRatchet,
64 bytes)` split in half.

- [ ] **Step 3: Run the tests** — `node tools/test-protocol.js`, Expected: pass.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Protocol/Signal/Keys.cs WhatsappApp/Protocol/Signal/Ratchet.cs \
        ProtocolTests/RatchetTests.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: the Double Ratchet's key schedule, with skipped keys kept"
```

---

### Task 4: The message, as the wire wants it

**Files:**
- Create: `WhatsappApp/Protocol/Signal/Cipher.cs`, `WhatsappApp/Protocol/Signal/WhisperMessage.cs`
- Create: `ProtocolTests/WhisperMessageTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `Aes` (CBC), `Hmac`, `ProtoWriter`/`ProtoReader`, `SignalConstants`.
- Produces: `Cipher.Encrypt(byte[] cipherKey, byte[] iv, byte[] macKey, byte[] plaintext)`
  and `Cipher.Decrypt(...)`, both with the reference's truncated MAC;
  `WhisperMessage.Encode(byte[] ratchetKey, int counter, int previousCounter, byte[] ciphertext,
  byte[] macKey, byte[] senderIdentityPrivate, byte[] receiverIdentityPublic)` and
  `WhisperMessage.Decode(byte[] payload)` returning a `WhisperContent` with those
  fields; and `PreKeyWhisperMessage` with the registration id, the pre-key id and
  the base key.

- [ ] **Step 1: Write the failing tests**

- `TheReferenceCiphertextIsReproduced` — Task 1's transcript: the same keys and
  plaintext produce the reference's own bytes, `Assert.ByteArraysEqual`. This is
  the test the whole milestone hangs on (review focus 4).
- `TheVersionByteIsWritten` — the first byte is `SignalConstants.MessageVersion`.
- `TheMacIsTruncatedToTheReferenceLength` — a payload with one byte changed is
  refused; the MAC length is `SignalConstants.MacLength` (review focus 4).
- `APreKeyMessageCarriesItsPreKeyId` — round trip through encode/decode.

Run: `node tools/test-protocol.js` — Expected: fail.

- [ ] **Step 2: Implement `Cipher` and the two message shapes**

AES-256-CBC with the reference's padding, HMAC-SHA256 over
`version || senderIdentity || receiverIdentity || payload` truncated to
`MacLength` — the exact input order is the reference's, and the fixture is what
says it is right.

- [ ] **Step 3: Run the tests** — Expected: pass.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Protocol/Signal/Cipher.cs WhatsappApp/Protocol/Signal/WhisperMessage.cs \
        ProtocolTests/WhisperMessageTests.cs WhatsappApp/WhatsApp.csproj
git commit -m "feat: the whisper message format, byte for byte against the reference"
```

---

### Task 5: Sessions that survive the app being closed

**Files:**
- Create: `WhatsappApp/Protocol/Signal/Session.cs`, `WhatsappApp/Protocol/Signal/SessionStore.cs`
- Create: `WhatsappApp/Services/FileSessionStorage.cs` (the phone's half)
- Create: `ProtocolTests/SessionStoreTests.cs`
- Modify: `WhatsappApp/WhatsApp.csproj`

**Interfaces:**
- Consumes: `Ratchet`, `WhisperMessage`, `ProtoWriter`/`ProtoReader`.
- Produces: `SessionRecord` with `public byte[] Encrypt(byte[] plaintext)`,
  `public byte[] Decrypt(byte[] payload)`, `public bool HasSession`,
  `public byte[] Encode()` and `public static SessionRecord Decode(byte[] bytes)`
  (the last one returns null for anything it cannot parse whole);
  `interface ISessionStorage { bool TryRead(string key, out byte[] record); void Write(string key, byte[] record); void Remove(string key); }`;
  `SessionStore(ISessionStorage storage)` with `public SessionRecord Load(string key)`,
  `public void Save(string key, SessionRecord record)` and `public int Count`;
  `PreKeyStore(ISessionStorage storage)` with
  `public byte[] Take(uint id)` (null when the id is spent or unknown),
  `public void Publish(uint id, byte[] preKey)` and `public int Remaining`.

- [ ] **Step 1: Write the failing tests**

- `ARecordRoundTrips` — a session built and encoded, decoded, encrypts and
  decrypts after the round trip (the same message key material).
- `AHalfWrittenRecordIsNoSession` (review focus 2) — a truncated record and a
  record with one byte corrupted both answer "no session" rather than throwing
  out of a decryption.
- `TheStoreKeepsOneSessionPerPeer` — two peers, two records, no crossing.
- `ThePreKeyStoreNeverHandsTheSameOneTimeKeyTwice` — the pre-key store the
  bundle upload (M4) draws from: the second draw of a consumed id fails, and the
  signed pre-key keeps its id, its private half and its signature across a
  restart.

Run: `node tools/test-protocol.js` — Expected: fail.

- [ ] **Step 2: Implement the record and the pre-key store**

The record is ours, not the reference's: `ProtoWriter` fields for the version, the
current root, the two chains and the skipped keys, with a length-prefixed wrapper
so a truncated file is detected instead of parsed. The phone's
`ISessionStorage` is one file per peer under
`ApplicationData.Current.LocalFolder`, written through a temporary file and
renamed, so the half-written state is the one on disk for as little time as
possible. `PreKeyStore` keeps the one-time pre-keys this device has published but
not spent, over the same storage interface, keyed by id — it is what M4 uploads
and what `X3dh.BobSecret` consumes.

- [ ] **Step 3: Run the tests** — Expected: pass.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Protocol/Signal/Session.cs WhatsappApp/Protocol/Signal/SessionStore.cs \
        WhatsappApp/Services/FileSessionStorage.cs ProtocolTests/SessionStoreTests.cs \
        WhatsappApp/WhatsApp.csproj
git commit -m "feat: sessions on disk, and a half-written one is not a session"
```

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

Open for Task 2: the X3DH specification's appendix vector is not in the fixture
yet. The reference conversation is the stronger oracle for the *wire*; the
specification is the oracle for the *arithmetic*, and Task 2 adds it as
`SignalFixture.X3dh.RuleBook`.

### The generator, as it ran

```javascript
// Prints ProtocolTests/Vectors/SignalFixture.cs from the reference's own frozen
// conversation (libsignal-protocol's test/testvectors.js).
const fs = require('fs'), vm = require('vm'), path = require('path');

const code = fs.readFileSync('node_modules/libsignal-protocol/test/testvectors.js', 'utf8');
const sandbox = { console: console, hexToArrayBuffer: (hex) => hex };
vm.createContext(sandbox);
vm.runInContext(code.replace('var TestVectors =', 'this.TestVectors ='), sandbox);

const tests = sandbox.TestVectors;
console.error('tests: ' + tests.map(t => t.name + '(' + t.vectors.length + ')').join(', '));

const alice = tests.filter(t => /alice/i.test(t.name))[0];
if (!alice) throw new Error('no Alice test');
const vectors = alice.vectors;

const sends = vectors.filter(v => v[0] === 'sendMessage').slice(0, 2);
const recvs = vectors.filter(v => v[0] === 'receiveMessage' && v[1].expectedSmsText).slice(0, 2);
const first = sends[0][1], keys = first.getKeys, bundle = keys.devices[0];

const hex = (value) => '0x' + value.toString(16).toUpperCase();

let out = '';
out += 'using System;\n\n';
out += 'namespace ProtocolTests\n';
out += '{\n';
out += '    /// <summary>\n';
out += '    /// The reference conversation, frozen. Printed by a script in a temporary\n';
out += '    /// directory outside this repository, from libsignal-protocol\'s own\n';
out += '    /// test/testvectors.js - the same file its own test suite runs against.\n';
out += '    ///\n';
out += '    /// The command that regenerates it, from a directory outside this repository:\n';
out += '    ///\n';
out += '    ///   mkdir -p /tmp/signalref && cd /tmp/signalref && npm init -y\n';
out += '    ///   npm install libsignal-protocol\n';
out += '    ///   node emit-fixture.js                 # the script is in the plan\'s notes\n';
out += '    ///\n';
out += '    /// Nothing in it was typed: a constant copied by eye is a bug the fixture cannot\n';
out += '    /// catch - which is how a mangled certificate constant once cost an afternoon.\n';
out += '    /// </summary>\n';
out += '    public static class SignalFixture\n';
out += '    {\n';
out += '        public static class X3dh\n';
out += '        {\n';
out += '            /// <summary>Alice\'s private halves, in the clear, because a fixture is not a phone.</summary>\n';
out += '            public static readonly byte[] AliceIdentityPrivate = Hex("' + first.ourIdentityKey + '");\n';
out += '            public static readonly byte[] AliceBasePrivate = Hex("' + first.ourBaseKey + '");\n';
out += '            public static readonly byte[] AliceEphemeralPrivate = Hex("' + first.ourEphemeralKey + '");\n';
out += '            public static readonly uint AliceRegistrationId = ' + first.registrationId + ';\n\n';
out += '            /// <summary>Bob\'s half, as the server hands it out in a pre-key bundle.</summary>\n';
out += '            public static readonly byte[] BobIdentityPublic = Hex("' + keys.identityKey + '");\n';
out += '            public static readonly uint BobRegistrationId = ' + hex(bundle.registrationId) + ';\n';
out += '            public static readonly uint BobPreKeyId = ' + bundle.preKey.keyId + ';\n';
out += '            public static readonly byte[] BobPreKeyPublic = Hex("' + bundle.preKey.publicKey + '");\n';
out += '            public static readonly uint BobSignedPreKeyId = ' + bundle.signedPreKey.keyId + ';\n';
out += '            public static readonly byte[] BobSignedPreKeyPublic = Hex("' + bundle.signedPreKey.publicKey + '");\n';
out += '            public static readonly byte[] BobSignedPreKeySignature = Hex("' + bundle.signedPreKey.signature + '");\n';
out += '        }\n\n';
out += '        /// <summary>The conversation: what Alice sent, and what came back.</summary>\n';
out += '        public static class Conversation\n';
out += '        {\n';
out += '            public static readonly string[] Plaintexts = new string[] { ' +
       sends.map(s => '"' + s[1].smsText + '"').join(', ') + ' };\n';
out += '            public static readonly string[] Ciphertexts = new string[]\n';
out += '            {\n';
out += sends.map(s => '                "' + s[1].expectedCiphertext + '"').join(',\n') + '\n';
out += '            };\n\n';
out += '            /// <summary>Bob\'s replies: the first arrives with a new ratchet key.</summary>\n';
out += '            public static readonly string[] Received = new string[]\n';
out += '            {\n';
out += recvs.map(r => '                "' + r[1].message + '"').join(',\n') + '\n';
out += '            };\n';
out += '            public static readonly string[] ReceivedTexts = new string[] { ' +
       recvs.map(r => '"' + r[1].expectedSmsText + '"').join(', ') + ' };\n';
out += '            public static readonly string[] ReceivedEphemeralKeys = new string[]\n';
out += '            {\n';
out += recvs.map(r => '                "' + r[1].newEphemeralKey + '"').join(',\n') + '\n';
out += '            };\n';
out += '        }\n\n';
out += '        /// <summary>Hex, the way the vectors write it.</summary>\n';
out += '        public static byte[] Hex(string hex)\n';
out += '        {\n';
out += '            var bytes = new byte[hex.Length / 2];\n';
out += '            for (int i = 0; i < bytes.Length; i++)\n';
out += '            {\n';
out += '                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);\n';
out += '            }\n\n';
out += '            return bytes;\n';
out += '        }\n';
out += '    }\n';
out += '}\n';

fs.writeFileSync(path.resolve(process.env.REPO, 'ProtocolTests/Vectors/SignalFixture.cs'), out);
console.error('wrote ' + out.length + ' bytes');
```

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
