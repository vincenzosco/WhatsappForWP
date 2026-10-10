# Native WhatsApp client on the phone — Milestones 2 to 5: authentication, Signal, messages, media

**Status:** M2 in progress. M1 closed with `f83151f` (the Noise handshake decrypts
WhatsApp's real `serverHello`; see
`docs/superpowers/plans/2026-10-09-native-whatsapp-protocol-spike.md`).

## What this plan is

The phone itself speaks WhatsApp's protocol, so the app needs no `WhatsappBridge`,
no GOWA, no `node`, and no machine in the middle. M1 built the parts that need no
account: the node codec, the managed crypto, the transport and the Noise handshake.

These milestones build the parts that need one:

- **M2 — authentication and pairing.** XEdDSA, verified certificates, the
  `ClientPayload`, stored credentials, linking as a companion device by QR,
  `pair-success` + ADV signatures, pre-keys on the server, `<success>`, keepalive.
- **M3 — Signal.** X3DH, the Double Ratchet, pre-key and session records, sender keys
  for groups. This is what makes messages unreadable to everyone but the peers.
- **M4 — messages.** Sending and receiving text, receipts and acks, presence,
  history sync, and the app's own chat list fed from the socket.
- **M5 — media.** WhatsApp's media encryption, upload and download, images and voice
  notes, and the removal of the adapter from the app.

Each milestone leaves the app working with the adapter still in place. The adapter is
removed in M5, last, so a half-finished native path is never the only path.

## Global Constraints

- **C# 5 only**, no `System.Security.Cryptography` and no `Windows.*` types under
  `WhatsappApp/Protocol/**`: the WP8.1 projection has neither the BCL crypto nor the
  same `Windows.*` shape. `tools/check-csharp5.js` fails the build on both, and it is
  not to be weakened to get past a line.
- **Every primitive has a published vector** and every wire format has a captured
  real-server fixture. A signature we cannot verify against WhatsApp's own certificate
  is not "probably right"; it is unverified.
- **The desktop host runs the same sources**: `ProtocolTests/ProtocolTests.csproj`
  compiles `WhatsappApp/Protocol/**` with the same C# 5 compiler, so each step is
  checked in seconds with `node tools/test-protocol.js`.
- **Anything that only the server can answer is a probe step**: run with
  `node tools/test-protocol.js --connect`, and the output is quoted in the plan's
  execution notes rather than summarized.
- **Nothing of the user's is sent over an unverified channel**: the certificate
  signatures are verified before the `ClientPayload` is sent. Until then `--connect`
  stops at the serverHello, which is what M1 does today.
- **Do not commit** unrelated working-tree changes (`ChatPage.xaml.cs`,
  `ConversationView.cs`, `README*.md`, `SKILL.md` carry in-progress scroll work from
  the previous session).

## File Structure

    WhatsappApp/Protocol/
      Crypto/  Sha512.cs Ed25519.cs XEdDsa.cs          (M2, task 1-2)
      WaProto/ ClientPayload.cs DeviceProps.cs AdvIdentity.cs  (M2, task 3)
      Client/  Credentials.cs CredentialStore.cs PairingQr.cs  (M2, task 4-6)
      Signal/  Session.cs Ratchet.cs SenderKey.cs MessageCrypto.cs (M3)
      Media/   MediaKeys.cs MediaUpload.cs MediaDownload.cs   (M5)
    ProtocolTests/
      Vectors/ Ed25519Vectors.cs CertFixture.cs ClientPayloadFixture.cs
      *Tests.cs

## Milestone table

| Milestone | Proves | Server needed |
| --- | --- | --- |
| M2 task 1-2 | Ed25519 and XEdDSA verify WhatsApp's real certificate chain | no (fixture), then yes |
| M2 task 3-4 | the `ClientPayload` and the credential set a device needs | no |
| M2 task 5-6 | the server accepts our finish and hands us a QR pair request | yes |
| M2 task 7 | a real phone scan links this device (the milestone's real test) | yes + phone |
| M3 | sessions, ratchets and sender keys match published test vectors | no |
| M4 | the phone sends and receives a text end to end | yes + phone |
| M5 | images and voice notes through the native path; adapter deleted | yes + phone |

---

### Task 1: SHA-512 and Ed25519

Ed25519 is written by hand here - no `System.Security.Cryptography`, and the WP8.1
projection has no `Ed25519` - and it is the primitive the certificate verification,
the signed pre-key and the ADV device signature all stand on.

**Files:** `WhatsappApp/Protocol/Crypto/Sha512.cs`,
`WhatsappApp/Protocol/Crypto/Field25519.cs`,
`WhatsappApp/Protocol/Crypto/Edwards25519.cs`,
`WhatsappApp/Protocol/Crypto/Scalar25519.cs`,
`ProtocolTests/Vectors/Ed25519Vectors.cs`, `ProtocolTests/Ed25519Tests.cs`

**Step 1 (RED):** write `Ed25519Vectors` with the RFC 8032 section 7.1 vectors -
TEST 1, TEST 2, TEST 3 and the 1023-byte TEST 1024 - plus the SHA-512 vectors from
FIPS 180-4. Then `Ed25519Tests` asserting `Ed25519.Verify(publicKey, message, signature)`
for each, and `Sha512.Hash` for the SHA-512 vectors.

**Step 2:** `node tools/test-protocol.js` → **Expected:** every new test FAILS with
"not implemented" (the types do not exist yet), 36 old tests still pass.

**Step 3 (GREEN):** implement.

- `Sha512`: 64-bit words, the same shape as the existing `Sha256`.
- `Field25519`: a field element as eight 32-bit little-endian limbs and the arithmetic
  that is easy to review - `Add`, `Sub`, `Mul` (reduce `2^256` by folding
  `19 * high`), `Square`, `Invert` (exponentiation by `2^255 - 21`), `Pow2523`,
  `FromBytes` (masking the top bit), `ToBytes`, `IsNegative`, `IsZero`, `Cmov`.
  Deliberately **not** a ref10 limb-for-limb port: this is read by people, and a
  ref10 transcription that is subtly wrong is the failure mode this avoids.
- `Edwards25519`: extended coordinates `(X:Y:Z:T)`, `Add`, `Double`, `ScalarMultiply`,
  `Encode`, `Decode`, the standard base point, and `MontgomeryToEdwards`
  (`y = (u - 1) / (u + 1)`).
- `Scalar25519`: `l = 2^252 + 27742317777372353535851937790883648493`, and
  `Reduce512(bytes)` / `MultiplyAdd(a, b, c)` implemented as schoolbook
  multiplication and bitwise modular reduction rather than the reference's 21-bit
  limbs - same reason as the field: it is slower by microseconds and much easier to
  see.

**Step 4:** `node tools/test-protocol.js` → **Expected:** the RFC 8032 vectors pass,
36 + new tests, `OK: <n> protocol test(s)`, exit 0.

**Step 5:** `node tools/check-csharp5.js`, `node tools/check-project-files.js`,
`node tools/check-docs.js` → **Expected:** exit 0 each.

**Step 6:** commit `feat: managed SHA-512 and Ed25519 for the phone`.

### Task 2: XEdDSA, and a certificate chain that is actually verified

WhatsApp signs with XEdDSA: the private key is a Curve25519 scalar, the public key is
its Montgomery `u`, and the signature is an Ed25519 signature whose sign bit is carried
in the top bit of `S`. Verifying needs the birational map; signing needs the explicit
nonce derived with the `0xFE 0xFF*31` prefix. Both are transcribed from
`libsignal-protocol-c`'s `curve_sigs.c` / `sign_modified.c`, which is what the
reference clients link.

**Files:** `WhatsappApp/Protocol/Crypto/XEdDsa.cs`,
`WhatsappApp/Protocol/WaProto/CertificateVerifier.cs`,
`ProtocolTests/Vectors/CertFixture.cs`, `ProtocolTests/XEdDsaTests.cs`,
`WhatsappApp/Protocol/Noise/NoiseHandshake.cs`

**Step 1 (RED):** write the fixture: the leaf and intermediate certificate of the
serverHello captured on 2026-10-10 (details and signature, both hex), and the root
public key `142375574d0a587166aae71ebe516437c4a28b73e3695c6ce1f7f9545da8ee6b`.
Assertions: the intermediate verifies against the root key, the leaf verifies against
the intermediate's key from its own details, and a single flipped byte in either
signature makes verification fail. Then the XEdDSA round trip: sign with an arbitrary
32-byte private key, verify with its Curve25519 public key, over four message lengths.

**Step 2:** `node tools/test-protocol.js` → **Expected:** the new tests fail
(`XEdDsa` does not exist), the old ones pass.

**Step 3 (GREEN):** implement `XEdDsa.Verify(publicKey, message, signature)` and
`XEdDsa.Sign(privateKey, message)` - the latter with 64 zero bytes as the nonce noise,
which is the deterministic variant: a different `random` only changes `R` and `S`
together, and verification does not look at it. Then `CertificateVerifier`, which
checks, in this order: the leaf's key equals the handshake's static key, the leaf's
issuer serial is the intermediate's serial, the intermediate's issuer serial is the
root's (`0`), the leaf's window contains now, the intermediate's window contains now,
and both signatures verify. It throws `ProtocolException` with which check failed.

**Step 4:** `node tools/test-protocol.js` → **Expected:** all pass.

**Step 5:** `node tools/test-protocol.js --connect` → **Expected:** the probe prints
`chain VERIFIED` instead of `signatures NOT verified`, and the run still ends in
`PROBE OK`.

**Step 6:** commit `feat: verify WhatsApp's certificate chain with managed XEdDSA`.

### Task 3: the `ClientPayload` and the device payload

The first frame after the handshake is the `ClientPayload`: what the device is, and -
for a companion - the pairing data that asks the server for a QR or a pairing code.
`proto` field numbers come from the reference's `validate-connection.ts` and
`waCompanionReg`; the sub-messages are written by hand on the existing `ProtoWriter`.

**Files:** `WhatsappApp/Protocol/WaProto/ClientPayload.cs`,
`WhatsappApp/Protocol/WaProto/DeviceProps.cs`, `ProtocolTests/ClientPayloadTests.cs`

**Step 1 (RED):** a test that encodes a registration `ClientPayload` and decodes it
back with `ProtoReader`, asserting: `passive = false`, `pull = false`, `username`
absent, `devicePairingData` present with `eRegid` (4 bytes big-endian),
`eKeyType` (`0x05`), `eIdent` (the identity public key), `eSkeyId` (3 bytes big-endian),
`eSkeyVal`, `eSkeySig`, and that the `deviceProps` blob decodes with
`platformType = CHROME` and `os = "Windows"`.

**Step 2:** run → **Expected:** fails, `ClientPayload` does not exist.

**Step 3 (GREEN):** implement `DeviceProps` (nested `HistorySyncConfig` included) and
`ClientPayload` with `UserAgent`, `WebInfo` and `DevicePairingData`, plus
`ClientPayload.Registration(...)` and `ClientPayload.Login(jid, device)`.

**Step 4:** run → **Expected:** pass.

**Step 5:** commit `feat: build the ClientPayload the server expects`.

### Task 4: credentials, and where they live on the phone

**Files:** `WhatsappApp/Protocol/Client/Credentials.cs`,
`WhatsappApp/Protocol/Client/CredentialStore.cs`, `ProtocolTests/CredentialsTests.cs`

**Step 1 (RED):** tests for what must survive a restart: the noise key pair, the
identity key pair, the registration id, the ADV secret, the signed pre-key, the
account (`jid`, `lid`, `platform`), the pre-keys and the sessions. `Credentials.Create()`
draws a fresh set; `CredentialStore` round-trips it through a `string`-based key/value
map (the phone's `ApplicationData.Current.LocalSettings` is a map of strings, so the
encoding is visible to a test rather than hidden in a file).

**Step 2-4:** as above: RED, implement, GREEN.

**Step 5:** commit `feat: keep the device credentials where the phone can restore them`.

### Task 5: send the finish, and ask the server to pair

**Files:** `WhatsappApp/Protocol/Client/WhatsAppSocket.cs`,
`WhatsappApp/Protocol/Client/PairingQr.cs`, `ProtocolTests/HandshakeTests.cs`,
`ProtocolTests/LiveProbe.cs`

**Step 1 (RED):** offline tests for the pairing QR string
(`https://wa.me/settings/linked_devices#<ref>,<noise pub>,<identity pub>,<adv secret>,<client type>`,
each base64) and for reading a `<pair-device>` node's `ref` children.

**Step 2:** run → RED.

**Step 3 (GREEN):** implement the login half of the driver: after the handshake, send
`HandshakeMessage{clientFinish{static, payload}}` with the encrypted `ClientPayload`,
call `FinishInit`, then read nodes through the transport. `PairingQr.FromRefs(refs, credentials)`
builds the strings.

**Step 4:** run → GREEN.

**Step 5:** `node tools/test-protocol.js --connect --pair` → **Expected:** the probe
prints the QR payload it received from the server (the terminal QR helper
`tools/qr-term.js` already exists for the adapter's flow and is reused here).

**Step 6:** commit `feat: finish the handshake and ask the server to pair this device`.

### Task 6: `pair-success`, the ADV signature, and the account we keep

**Step 1 (RED):** offline tests with a captured `pair-success` node shape: the HMAC
over the device details must equal `HMAC-SHA256(advSecret, prefix || details)`, a
wrong secret must fail, the account signature must verify against
`accountSignatureKey`, and our device signature must verify as an XEdDSA signature
over `0x06 0x01 || details || identityPublic || accountSignatureKey`.

**Step 2-4:** RED, implement (`WaProto/AdvIdentity.cs`, the driver's `HandlePairSuccess`,
the `pair-device-sign` reply), GREEN.

**Step 5:** `--connect --pair`, scan the QR with a real phone, then re-run with the
stored credentials to log in → **Expected:** the server sends `<success>` and the
socket stays up. This is the milestone's real test and needs the phone in hand.

**Step 6:** commit.

### Task 7: pre-keys, keepalive, and the docs

Upload the pre-key bundle (`<iq xmlns="encrypt">`, `<digest>`), answer the server's
keepalive (`<iq xmlns="w:p">`), and record in both READMEs what the app now does, with
the QR run and its output. Commit.

### M3, M4, M5

Written out in the same shape when M2 closes; their files and checkpoints are the table
above. M3's tests are the published Signal vectors, M4's checkpoint is a text message
arriving on the phone, M5's is an image and a voice note and the adapter's removal.

## Review Focus

- **The field arithmetic and the scalar reduction**, which are the two places a
  plausible-looking implementation still produces signatures that verify nowhere. The
  RFC 8032 vectors and the frozen certificate are the evidence, and a probe run is the
  check that no vector capture is itself wrong.
- **The sign-bit convention**: `S`'s top bit is not part of the signature. A verifier
  that forgets to clear it, or a signer that forgets to set it, passes every
  round-trip test and fails against WhatsApp.
- **What is stored and where**: the identity private key and the ADV secret are the
  account. Anything that writes them into a world-readable place, a log line, or a
  crash dump is the bug this milestone can ship.
- **The URL and the QR string**: a malformed ref or a wrong client type is a pairing
  that hangs instead of failing.

## What execution changed about this plan

**Task 1 and Task 2 are done.** `Sha512`, `Field25519`, `Edwards25519`, `Scalar25519`,
`Ed25519` and `XEdDsa` are in `WhatsappApp/Protocol/Crypto`, `CertificateVerifier` is in
`WaProto`, and the offline suite is **53 tests**: FIPS 180-4's SHA-512 vectors, RFC 8032's
Ed25519 vectors (1, 2 and 3), the group and field properties, the refusal cases, and both
of WhatsApp's own certificates. The live probe ends in
`PROBE OK: WhatsApp's certificate chain VERIFIED` - so the handshake is authenticated now,
not just encrypted, and it says so.

### Ruling: the field arithmetic is readable, not a ref10 transcription

The reference's `fe_*` code keeps unreduced 25.5-bit limbs and is trusted by its authors'
reasoning rather than by reading. A line-for-line port that is subtly wrong produces keys
and signatures that verify nowhere while passing every round-trip test. What is here is
smaller and slower: eight 32-bit limbs, every operation fully reduced, the inverse by
Fermat, the scalar reduction by bitwise division. The vectors are the evidence.

*Cost if wrong:* a signature takes a few milliseconds more on a phone that does one per
pre-key upload. The correctness is what the RFC vectors and the frozen certificates check.

### Ruling: `out` parameters and `.Skip(` are avoided for the guard's sake, not weakened

`tools/check-csharp5.js` reads `out int wireType` in a parameter list as C# 6's inline
declaration, and `reader.Skip(wireType)` as a LINQ call with no `using System.Linq;`.
Both are false positives, and both were fixed by changing the code rather than the guard -
`ProtoReader.ReadField()` returns a `ProtoField` struct and the method is `SkipField`.
This was found by checking the guard's exit code, which an earlier piped run had hidden:
the M1 commit had shipped with the guard failing.

*Cost if wrong:* none; the API is clearer than the one it replaces.

### Ruling: a mangled constant is a bug the fixture cannot catch

The intermediate certificate's signature was transcribed into
`ProtocolTests/XEdDsaTests.cs` with two bytes mangled, and the resulting failure looked
exactly like a broken signature implementation. Node's own Ed25519 verifier settled it:
the bytes were wrong, not the code. Both directions of verification now agree with Node,
and the fixture is written in 16-byte groups so a future transcription can be compared
where it was copied from.

*Cost if wrong:* none, and the check is repeatable without the phone.

### Ruling: the app project was renamed outside this work, and the guards follow it

Mid-session the working tree had `WhatsappApp/WhatsappApp.csproj` deleted and
`WhatsappApp/WhatsApp.csproj` added, with `WhatsappApp.sln` renamed to match and
downgraded to the Visual Studio 2013 format. Nothing in this plan did that. The new
protocol files were added to the file that exists, and `tools/check-project-files.js`,
`tools/check-resw.js` and their test now name it; leaving them pointing at a file that is
gone would have left the gates broken for the next change.

*Cost if wrong:* if the rename is reverted, the three guards have to name the old file
again, and they fail loudly rather than silently checking nothing.
