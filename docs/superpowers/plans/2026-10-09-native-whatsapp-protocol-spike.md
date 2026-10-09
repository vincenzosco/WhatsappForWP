# Native WhatsApp protocol inside the app — Milestone 1: the core and the handshake spike

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove, on the Lumia and with no server of any kind, that this app can speak
the WhatsApp Web protocol by itself: open the WebSocket to `web.whatsapp.com`,
complete the Noise handshake, and read the server's `<stream:features>` node in the
app's own code.

**Architecture:** The WhatsApp Web protocol is re-implemented in **pure C# 5** under
`WhatsappApp/Protocol/`. The core references **no WinRT API and no
`System.Security.Cryptography`**: every primitive (SHA-256, HMAC, HKDF, AES-CBC,
AES-GCM, Curve25519) is managed code, so the exact same source files compile both
inside the WP8.1 app and inside a plain .NET 4.5 console runner on this machine
(`ProtocolTests/`, built by MSBuild 12). That runner is the only place the protocol
is unit-tested; the phone build is the integration gate. The on-device transport is
one small file that implements `ITransport` over WinRT `MessageWebSocket`.

Baileys is **not** portable here (Node.js runtime, `node:crypto`, libsignal, modern
TypeScript) and is not used: it is the behavioural reference, and the byte-level
vectors below are taken from it.

**Tech Stack:** C# 5 (`.cs` compiled by the WP8.1 toolchain and by MSBuild 12),
MSBuild 12 / `csc` from Visual Studio 2013, Node.js 24 (the test launcher and the
reference-vector generator), WinRT `MessageWebSocket` (device only).

## Scope, and what it costs

Milestone 1 is a **spike**: WABinary + the crypto primitives + the Noise handshake +
the WebSocket transport, ending when the phone prints the server's features. It does
**not** log in and does **not** send or receive a message. The road after it, so the
size is not a surprise:

| Milestone | Content | Ends when |
| --- | --- | --- |
| **M1 (this plan)** | WABinary, managed crypto, Noise handshake, WebSocket transport | the phone reads `<stream:features>` |
| M2 | `WAProto` protobuf, `ClientPayload`, device identity (ADV), login (QR + pairing code), session store | the phone shows a QR WhatsApp accepts |
| M3 | Signal (X3DH + double ratchet), prekeys, `message`/`receipt`/`presence`, media keys | the phone exchanges a real text message |
| M4 | Wire the protocol into `DataService`/`CommunicationService`, drop the adapter from the app path | the app runs with no server |

## Global Constraints

- **C# 5 only.** `node tools/check-csharp5.js` must stay green. No `?.`, no `$""`,
  no expression-bodied members, no auto-property initializers, no `nameof`, no
  inline `out`, and **no `await` inside a `catch`** (CS1985).
- **The protocol core is portable C#.** `WhatsappApp/Protocol/**` must reference
  neither `Windows.*` nor `System.Security.Cryptography`: the phone is not the only
  place it compiles. Any WinRT call lives in a device-only file (`Protocol/Transport/`),
  which the desktop runner excludes.
- **No new NuGet package and no new dependency.** The app has none today; the
  protocol code is written in this repository. The adapter keeps zero runtime deps.
- **Every `.cs` under `WhatsappApp` is in `WhatsappApp.csproj`** and
  `node tools/check-project-files.js` must pass. After any build on this machine,
  `git checkout -- WhatsappApp/Package.appxmanifest` (the toolchain rewrites it).
- **The desktop runner is not part of `WhatsappApp.sln`.** The solution build gate
  (`msbuild WhatsappApp.sln /t:Rebuild ... /p:Platform=x86`) must keep working, and a
  net45 console project with no ARM/x86 platform would poison it.
- **Runtime text is English**, source comments are English. The device probe's
  user-visible text goes through `Loc.Get` with a key in **both** `.resw` files.
- **Memory is the budget of a 512 MB phone.** No structure here keeps a whole
  session in memory: nodes and frame buffers are transient and bounded.
- **Byte vectors are taken from Baileys, not invented.** Every primitive and every
  token table entry is checked against a value the reference produces.

## File Structure

| File | Responsibility | Change |
| --- | --- | --- |
| `tools/test-protocol.js` | **Create.** Build `ProtocolTests` with MSBuild 12 and run it; exit code is the verdict. | new |
| `ProtocolTests/ProtocolTests.csproj` | **Create.** net45 console project; `<Compile Include="..\WhatsappApp\Protocol\**\*.cs" />` — the shared core is compiled, never copied. Not in the `.sln`. | new |
| `ProtocolTests/TestRunner.cs` | **Create.** Reflection over `[Test]` methods, one line each, non-zero exit on failure. | new |
| `ProtocolTests/Assert.cs` | **Create.** `AreEqual` / `IsTrue` / `Throws` with the failing value in the message. | new |
| `ProtocolTests/WABinaryTests.cs` | **Create.** Token tables, node round-trips, packed strings, JIDs, binary payloads. | new |
| `WhatsappApp/Protocol/WABinary/Tags.cs` | **Create.** The `TAGS` constants. | new |
| `WhatsappApp/Protocol/WABinary/Tokens.cs` | **Create.** `SingleByteTokens` and `DoubleByteTokens` — the exact tables of `WABinary/constants.ts`. | new |
| `WhatsappApp/Protocol/WABinary/BinaryNode.cs` | **Create.** The node type (`Tag`, ordered attrs, content). | new |
| `WhatsappApp/Protocol/WABinary/JidUtils.cs` | **Create.** `JidDecode` / `JidEncode` — the JID forms the wire uses. | new |
| `WhatsappApp/Protocol/WABinary/NodeWriter.cs` | **Create.** `Encode(BinaryNode)` — the byte-for-byte port of `encode.ts`. | new |
| `WhatsappApp/Protocol/WABinary/NodeReader.cs` | **Create.** `Decode(byte[])` — the port of `decodeDecompressedBinaryNode`. | new |
| `WhatsappApp/Protocol/Diagnostics.cs` | **Create.** A tiny logger interface so `Diag` is not needed inside the core. | new |
| `docs/superpowers/plans/2026-10-09-native-whatsapp-protocol-spike.md` | This plan. | new |

Files added by later tasks of this milestone (listed here so the layout is fixed
once): `Protocol/Crypto/{Sha256,Hmac,Hkdf,Aes,AesGcm,Curve25519}.cs`,
`Protocol/Noise/NoiseHandshake.cs`, `Protocol/Transport/ITransport.cs`,
`Protocol/Transport/WebSocketTransport.cs`, `Protocol/Client/WhatsAppSocket.cs`,
`tools/noise-reference.js`.

**Task order:** 1 → 2 is strict (the runner is how Task 2 is verified). Tasks 3, 4, 5
are ordered by dependency (crypto → noise → transport) but each is independently
verifiable; Task 6 needs 2 and 5 on the device; Task 7 closes the milestone.

---

### Task 1: The desktop protocol-test harness

**Files:**
- Create: `tools/test-protocol.js`
- Create: `ProtocolTests/ProtocolTests.csproj`
- Create: `ProtocolTests/TestRunner.cs`
- Create: `ProtocolTests/Assert.cs`
- Create: `WhatsappApp/Protocol/Diagnostics.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `node tools/test-protocol.js` → builds and runs the runner; exit `0` when every
    test passes, `1` otherwise, and prints `OK: N protocol test(s)` or one line per
    failure. Every later task uses this as its test command.
  - `WhatsappApp.Protocol.ILog { void Info(string); void Failure(string, Exception); }`
    and `WhatsappApp.Protocol.SilentLog` (a no-op `ILog`), so the core never calls
    `Diag` and stays compilable off the phone.
  - `WhatsappApp.Protocol.ProtocolTests` convention: a public static method marked
    `[Test]` with no parameters.

- [ ] **Step 1: Write the runner skeleton and a first failing test**

`ProtocolTests/TestRunner.cs` reflects over the assembly's types, finds public static
methods carrying `TestAttribute`, invokes them, and counts. `Assert.cs` throws
`AssertionException` with the two values in the message. `ProtocolTests.csproj` is a
`v4.5` console `Exe` whose `Compile` items are its own files **plus**
`..\WhatsappApp\Protocol\**\*.cs`; it excludes `Protocol\Transport\**\*.cs` (device
only, WinRT).

First test (in `ProtocolTests/HarnessTests.cs`, which stays afterwards as the
harness's own smoke test):

```csharp
[Test]
public static void The_runner_reports_a_failure_with_both_values()
{
    Assert.Throws<AssertionException>(delegate { Assert.AreEqual(1, 2); });
}
```

- [ ] **Step 2: Write `tools/test-protocol.js`**

It locates MSBuild 12 (`C:\Program Files (x86)\MSBuild\12.0\bin\MSBuild.exe`), runs
it on `ProtocolTests/ProtocolTests.csproj` with `/p:Configuration=Debug
/p:Platform=AnyCPU /v:minimal`, fails with the build output if the build fails, then
runs `ProtocolTests/bin/Debug/ProtocolTests.exe` and forwards its exit code.

- [ ] **Step 3: Run it and watch the build fail**

Run: `node tools/test-protocol.js`
Expected: FAIL — the build cannot find the shared core yet (`Protocol/Diagnostics.cs`
does not exist), or the runner reports `0 test(s)`.

- [ ] **Step 4: Add `Protocol/Diagnostics.cs` and make it pass**

```csharp
namespace WhatsappApp.Protocol
{
    /// <summary>Where the core writes what it survives. The app passes Diag in;
    /// the test runner passes SilentLog, so the core has no WinRT dependency.</summary>
    public interface ILog
    {
        void Info(string line);
        void Failure(string site, System.Exception error);
    }

    public sealed class SilentLog : ILog
    {
        public void Info(string line) { }
        public void Failure(string site, System.Exception error) { }
    }
}
```

Run: `node tools/test-protocol.js`
Expected: PASS — `OK: 2 protocol test(s)`.

- [ ] **Step 5: Commit**

```bash
git add tools/test-protocol.js ProtocolTests WhatsappApp/Protocol
git commit -m "test: run the protocol core on the desktop, not only on the phone"
```

---

### Task 2: `WABinary` — the node codec

**Files:**
- Create: `WhatsappApp/Protocol/WABinary/Tags.cs`
- Create: `WhatsappApp/Protocol/WABinary/Tokens.cs`
- Create: `WhatsappApp/Protocol/WABinary/BinaryNode.cs`
- Create: `WhatsappApp/Protocol/WABinary/JidUtils.cs`
- Create: `WhatsappApp/Protocol/WABinary/NodeWriter.cs`
- Create: `WhatsappApp/Protocol/WABinary/NodeReader.cs`
- Create: `ProtocolTests/WABinaryTests.cs`
- Add the six files to `WhatsappApp/WhatsappApp.csproj`

**Interfaces:**
- Consumes: `ILog` (Task 1).
- Produces:
  - `Tags.ListEmpty = 0`, `Dictionary0 = 236`, `Dictionary3 = 239`, `InteropJid = 245`,
    `FbJid = 246`, `AdJid = 247`, `List8 = 248`, `List16 = 249`, `JidPair = 250`,
    `Hex8 = 251`, `Binary8 = 252`, `Binary20 = 253`, `Binary32 = 254`, `Nibble8 = 255`,
    `PackedMax = 127`.
  - `string[] SingleByteTokens` (256 entries) and `string[][] DoubleByteTokens` (4
    dictionaries), exactly as `WABinary/constants.ts`.
  - `class BinaryNode { string Tag; IDictionary<string,string> Attrs; object Content; }`
    where `Content` is `null`, a `string`, a `byte[]` or `List<BinaryNode>`.
  - `static byte[] NodeWriter.Encode(BinaryNode node)`
  - `static BinaryNode NodeReader.Decode(byte[] buffer)` (no decompression: the
    caller inflates first — see Task 5).
  - `static string JidUtils.JidEncode(string user, string server, int? device)`,
    `static JidUtils.DecodedJid JidUtils.JidDecode(string jid)`.

- [ ] **Step 1: Write the failing tests**

`ProtocolTests/WABinaryTests.cs`, following `encode.ts` / `decode.ts` exactly:

```csharp
[Test]
public static void The_token_tables_match_the_reference()
{
    Assert.AreEqual(256, Tokens.SingleByteTokens.Length);
    Assert.AreEqual("s.whatsapp.net", Tokens.SingleByteTokens[3]);
    Assert.AreEqual("type", Tokens.SingleByteTokens[4]);
    Assert.AreEqual("read-self", Tokens.DoubleByteTokens[0][0]);
    Assert.AreEqual("mute_v2", Tokens.DoubleByteTokens[0][223]);
    Assert.AreEqual(4, Tokens.DoubleByteTokens.Length);
}

[Test]
public static void A_node_round_trips()
{
    var node = new BinaryNode("iq");
    node.Attrs.Add("type", "set");
    node.Attrs.Add("to", "s.whatsapp.net");
    node.Content = "ping";

    BinaryNode back = NodeReader.Decode(NodeWriter.Encode(node));

    Assert.AreEqual("iq", back.Tag);
    Assert.AreEqual("set", back.Attrs["type"]);
    Assert.AreEqual("ping", back.Content);
}

[Test]
public static void A_children_list_round_trips()
{
    var parent = new BinaryNode("ib");
    var child = new BinaryNode("item");
    child.Attrs.Add("v", "1");
    parent.Content = new List<BinaryNode> { child };

    BinaryNode back = NodeReader.Decode(NodeWriter.Encode(parent));
    var list = (List<BinaryNode>)back.Content;
    Assert.AreEqual(1, list.Count);
    Assert.AreEqual("item", list[0].Tag);
}

[Test]
public static void A_binary_payload_round_trips_unchanged()
{
    byte[] payload = new byte[] { 0x00, 0xff, 0x10, 0x00, 0x7f };
    var node = new BinaryNode("message");
    node.Content = payload;

    BinaryNode back = NodeReader.Decode(NodeWriter.Encode(node));
    Assert.ByteArraysEqual(payload, (byte[])back.Content);
}

[Test]
public static void A_jid_is_packed_as_a_pair_and_read_back()
{
    var node = new BinaryNode("to");
    node.Attrs.Add("jid", "393401234567@s.whatsapp.net");

    byte[] bytes = NodeWriter.Encode(node);
    BinaryNode back = NodeReader.Decode(bytes);

    Assert.AreEqual("393401234567@s.whatsapp.net", back.Attrs["jid"]);
    // the user is all digits, so writeString packs it as nibbles, not raw
    Assert.IsTrue(Contains(bytes, (byte)Tags.Nibble8));
}

[Test]
public static void A_device_jid_keeps_its_device()
{
    var node = new BinaryNode("to");
    node.Attrs.Add("jid", "393401234567:12@s.whatsapp.net");
    BinaryNode back = NodeReader.Decode(NodeWriter.Encode(node));
    Assert.AreEqual("393401234567:12@s.whatsapp.net", back.Attrs["jid"]);
}

[Test]
public static void A_long_string_uses_the_20_bit_length()
{
    string text = new string('a', 300);
    var node = new BinaryNode("body");
    node.Content = text;
    byte[] bytes = NodeWriter.Encode(node);
    Assert.IsTrue(Contains(bytes, (byte)Tags.Binary20));
    Assert.AreEqual(text, NodeReader.Decode(bytes).Content);
}

[Test]
public static void A_truncated_buffer_is_refused_not_read()
{
    byte[] bytes = NodeWriter.Encode(new BinaryNode("iq"));
    Assert.Throws<ProtocolException>(delegate { NodeReader.Decode(Truncate(bytes, 3)); });
}
```

(`Contains` and `Truncate` are small local helpers in the test file.)

- [ ] **Step 2: Run the tests and watch them fail**

Run: `node tools/test-protocol.js`
Expected: FAIL to build — `Tokens`, `BinaryNode`, `NodeWriter`, `NodeReader`,
`Tags`, `ProtocolException` do not exist. The build is the first failing gate.

- [ ] **Step 3: Implement the codec**

`Tags.cs` and `Tokens.cs` are transcriptions of `WABinary/constants.ts` (the token
tables are not to be reordered or "cleaned": an index is the wire format).
`BinaryNode.cs` keeps attributes in a `List<KeyValuePair<string,string>>` under a
`Dictionary` view, because **attribute order is part of the encoding** — the reference
writes them in `Object.keys` order.

`NodeWriter.Encode` mirrors `encodeBinaryNodeInner`: the leading `0x00`, `WriteListStart`,
`WriteString` with the token lookup first, then nibble packing (digits/`-`/`.`), then
hex packing (`0-9A-F`, **uppercase only**), then `JidDecode` → `JID_PAIR`, else
`WriteStringRaw`. `AdsJid` (`0xF7`) is written for a device JID only in the ADV path,
which M1 does not use; a `:device` in a plain JID must go out as the `user` of a
`JID_PAIR` (that is what the reference does for every non-ADV node).

`NodeReader.Decode` mirrors `decodeDecompressedBinaryNode` including the
`end of stream` guard, the `(listSize - 1) >> 1` attribute count and the
`listSize % 2 == 0` content rule; every failure becomes a `ProtocolException` with a
site string, never a silent default.

- [ ] **Step 4: Run the tests until they pass**

Run: `node tools/test-protocol.js`
Expected: PASS — `OK: 10 protocol test(s)` (the two Task 1 smoke tests included).

- [ ] **Step 5: Run the fast gate and the app build**

Run: `node tools/check-csharp5.js && node tools/check-project-files.js && node tools/check-docs.js`
Expected: all exit `0`.
Run: `MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/bin/MSBuild.exe" WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86`
Expected: `0 Error(s)`. Then `git checkout -- WhatsappApp/Package.appxmanifest`.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Protocol WhatsappApp/WhatsappApp.csproj ProtocolTests/WABinaryTests.cs
git commit -m "feat: encode and decode WhatsApp binary nodes in the app's own C#"
```

---

### Task 3: Managed crypto primitives

**Files:**
- Create: `WhatsappApp/Protocol/Crypto/Sha256.cs`, `Hmac.cs`, `Hkdf.cs`, `Aes.cs`,
  `AesGcm.cs`, `Curve25519.cs`
- Create: `ProtocolTests/CryptoTests.cs`
- Add the files to `WhatsappApp.csproj`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `Sha256.Hash(byte[]) → byte[]`
  - `Hmac.Sha256(byte[] key, byte[] data) → byte[]`
  - `Hkdf.Derive(byte[] key, byte[] salt, byte[] info, int length) → byte[]`
  - `Aes.CbcEncrypt(byte[] key, byte[] iv, byte[] plain) / CbcDecrypt(...) → byte[]`
    (PKCS#7)
  - `AesGcm.Seal(byte[] key, byte[] nonce, byte[] plain, byte[] aad) → byte[] ciphertext+16-byte tag`,
    `AesGcm.Open(byte[] key, byte[] nonce, byte[] sealedBytes, byte[] aad) → byte[]`
  - `Curve25519.KeyPair(byte[] privateKey) → byte[] publicKey`,
    `Curve25519.SharedSecret(byte[] privateKey, byte[] publicKey) → byte[]`

- [ ] **Step 1: Write the failing tests with published vectors**

Use the standard, citable vectors — SHA-256 of `"abc"`, HMAC-SHA256 RFC 4231 case 1,
HKDF RFC 5869 test case 1, AES-128/256 FIPS-197 / NIST CBC vectors, NIST GCM test
case 2, RFC 7748 §6.1 X25519 (`a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4`
→ `e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c`), plus the
round-trip of each. Each test names the vector it uses so a failure can be looked up.

- [ ] **Step 2: Run and watch them fail to build**

Run: `node tools/test-protocol.js`
Expected: FAIL — the crypto types do not exist.

- [ ] **Step 3: Implement, in this order (one file is one concern)**

`Sha256` (FIPS 180-4, 64-byte block, message schedule), `Hmac` (RFC 2104 over it),
`Hkdf` (extract-then-expand), `Aes` (FIPS-197, 128/192/256, key schedule + CBC with
PKCS#7), `AesGcm` (GCM over the existing `Aes` CTR mode: GHASH with the 128-bit
multiply, 96-bit-nonce J0, tag = E(K,J0) XOR S), `Curve25519` (RFC 7748 ladder over
2^255-19, plus the `crypto_sign` key conversion only when M2 needs it).

**None of them may call `System.Security.Cryptography` or WinRT**: the phone has no
GCM and no Curve25519, and the desktop runner must compile the identical source. This
is the reason the file is written by hand rather than wrapped.

- [ ] **Step 4: Run until green**

Run: `node tools/test-protocol.js`
Expected: PASS, one line per vector.

- [ ] **Step 5: Cross-check against the reference implementation**

Add `tools/crypto-reference.js` (Node's own `crypto`, no dependency): it prints the
hex of each vector and a few extra random ones, so a disagreement is found against
the platform the phone will talk to rather than against a textbook. Run it and paste
its `X25519` line into the test as an eye check; the published vectors stay the
assertions.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Protocol/Crypto ProtocolTests/CryptoTests.cs tools/crypto-reference.js
git commit -m "feat: managed SHA-256, HMAC, HKDF, AES and Curve25519 for the phone"
```

---

### Task 4: The Noise handshake

**Files:**
- Create: `WhatsappApp/Protocol/Noise/NoiseHandshake.cs`
- Create: `ProtocolTests/NoiseTests.cs`
- Create: `tools/noise-reference.js`
- Add the file to `WhatsappApp.csproj`

**Interfaces:**
- Consumes: Task 3's primitives and Task 2's `NodeWriter`/`NodeReader`.
- Produces:
  - `NoiseHandshake.Prologue = "WA\\x06\\x03"` (the 6-byte `WHATSAPP` prologue pair
    as the reference writes it; the value is fixed once in the file),
  - `NoiseHandshake.Start(string header)` → the first `ClientHello`,
  - `byte[] ProcessHandshake(byte[] serverHello) → byte[] finish`,
  - `byte[] Encrypt(byte[] plain)` / `byte[] Decrypt(byte[] cipher)` after the split,
  - `Hash`/`Salt` state carried per the noise spec, with the handshake hash kept for
    the `success` verification of M2.

- [ ] **Step 1: Write the reference generator and the failing test**

`tools/noise-reference.js` implements `Noise_XX_25519_AESGCM_SHA256` over Node's
`crypto` (a known-good, dependency-free transcription) and writes a fixed transcript
to `ProtocolTests/Vectors/noise-transcript.txt`: a fixed client static key, a fixed
server static key, a fixed ephemeral, the client's two handshake messages, and the
1 000-byte payload the client encrypts after the split with its counters. Determinism
comes from the fixed keys, so the file is a committed vector, regenerated never.

`ProtocolTests/NoiseTests.cs` drives `NoiseHandshake` with the same fixed keys,
asserts `ProcessHandshake` produces the transcript's second message byte for byte,
and asserts `Encrypt(payload)` equals the transcript's ciphertext. **This is the
milestone's core assertion**: if the handshake matches a reference transcript, the
phone can talk to WhatsApp.

- [ ] **Step 2: Run and watch it fail**

Run: `node tools/test-protocol.js`
Expected: FAIL — `NoiseHandshake` does not exist (and the vector file is absent).

- [ ] **Step 3: Implement `NoiseHandshake`**

`Noise_XX` in the order Baileys uses: `-> e`, `<- e, ee, s, es`, `-> s, se`, then
`Split`. `MixHash`/`MixKey` per the spec, AES-GCM under the handshake key with the
nonce counter, the prologue mixed first, and the 4-byte length prefix (no length
prefix inside the handshake, only on the post-split records) respected.

- [ ] **Step 4: Run until green**

Run: `node tools/test-protocol.js`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Protocol/Noise ProtocolTests/NoiseTests.cs ProtocolTests/Vectors tools/noise-reference.js
git commit -m "feat: complete the WhatsApp Noise handshake off the phone"
```

---

### Task 5: The transport and the socket driver

**Files:**
- Create: `WhatsappApp/Protocol/Transport/ITransport.cs`
- Create: `WhatsappApp/Protocol/Transport/WebSocketTransport.cs` (device only, WinRT)
- Create: `WhatsappApp/Protocol/Client/WhatsAppSocket.cs`
- Create: `ProtocolTests/WhatsAppSocketTests.cs`
- Add the files to `WhatsappApp.csproj`

**Interfaces:**
- Consumes: Tasks 2–4.
- Produces:
  - `interface ITransport { Task ConnectAsync(string url); Task SendAsync(byte[] frame); Task<byte[]> ReceiveAsync(); void Close(); }`
  - `WebSocketTransport : ITransport` over `Windows.Networking.Sockets.MessageWebSocket`
    with `MessageType = Binary`, `SetRequestHeader("Origin", "https://web.whatsapp.com")`
    and `ConnectAsync(new Uri("wss://web.whatsapp.com/ws/chat"))`.
  - `WhatsAppSocket(ITransport transport, ILog log)` with
    `Task<string> HandshakeAsync()` → the `tag` of the first server node
    (`<stream:features>`), stopping there; and `Task<BinaryNode> ReadNodeAsync()`.

- [ ] **Step 1: Write the failing test with a fake transport**

`ProtocolTests/WhatsAppSocketTests.cs` uses a scripted `ITransport` that feeds the
transcript's server bytes; the test asserts the client sends the expected hello and
that `HandshakeAsync` returns `stream:features`.

- [ ] **Step 2: Run and watch it fail**

Run: `node tools/test-protocol.js`
Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement**

`WhatsAppSocket` drives: connect → send `ClientHello` → receive `ServerHello` →
`NoiseHandshake.ProcessHandshake` → send finish → read the first node → return its
tag. Decompression (`decode.ts`'s `0x02` prefix) is applied here, in the driver, so
the reader stays a pure byte reader. `WebSocketTransport` wraps each frame in the
4-byte big-endian length prefix the protocol uses and adds the device-only WinRT call.

- [ ] **Step 4: Run until green, then build the app**

Run: `node tools/test-protocol.js`
Expected: PASS.
Run the x86 solution build (as in Task 2, Step 5).
Expected: `0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Protocol/Transport WhatsappApp/Protocol/Client ProtocolTests/WhatsAppSocketTests.cs
git commit -m "feat: drive the WhatsApp socket through a transport the phone can supply"
```

---

### Task 6: The probe on the Lumia

**Files:**
- Modify: `WhatsappApp/Pages/DiagnosticsPage.xaml(.cs)` — one button and its label
- Modify: `WhatsappApp/Strings/en-US/Resources.resw` and `Strings/it-IT/Resources.resw`
  — one key, `DiagnosticsPage_ProtocolProbe`
- Modify: `tools/check-actions.js` if the button needs its own icon name declared

**Interfaces:**
- Consumes: Task 5's `WhatsAppSocket` + `WebSocketTransport`.
- Produces: a `DIAG` line per step — `probe: connecting`, `probe: handshake ok,
  tag=stream:features`, or `probe: failed <site> <hresult>` — and the same lines on
  screen, so a phone with no debugger attached can be read.

- [ ] **Step 1: Write the button and the handler**

The handler runs on a background task, logs through `Diag`, and appends the result to
the page's log. No `async void` without a guard: it goes through the same
`Guarded.RunGuardedAsync` the rest of the app uses, because a fire-and-forget
`async Task` that throws loses the exception and leaves no `DIAG` line.

- [ ] **Step 2: Run the fast gate**

Run: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-actions.js && node tools/check-project-files.js && node tools/check-docs.js`
Expected: all exit `0`.

- [ ] **Step 3: Build ARM, install and run on the Lumia**

Run the ARM bundle build, `Install`, then `Launch` on the device, as the
`test-the-app` skill describes.
Expected: `DIAG ok: probe: handshake ok, tag=stream:features`.

- [ ] **Step 4: Read the log back and record it**

Export `diag.log` with `ISETool.exe ts` and paste the probe lines into the plan's
"Run on the device" section below.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Pages/DiagnosticsPage.xaml WhatsappApp/Pages/DiagnosticsPage.xaml.cs WhatsappApp/Strings
git commit -m "feat: prove the handshake on the phone, with no server"
```

---

### Task 7: Documentation and the close of the milestone

**Files:**
- Modify: `README.md` and `README.it.md` — a paragraph under the app's features
  saying the protocol core is being ported natively and what M1 proved
- Modify: `README.md` / `README.it.md` and `WhatsappBridge/README.md` /
  `WhatsappBridge/README.it.md` only if a documented claim changes (the adapter is
  still the app's transport, so most claims do not)
- Modify: `.agents/skills/maintain-the-app/SKILL.md` — the new `Protocol/` tree and
  its one rule (portable C#, no WinRT/BCL crypto outside `Transport/`)
- Modify: `.agents/skills/test-the-app/SKILL.md` — the new gate
  `node tools/test-protocol.js`
- Append: `## What execution changed about this plan` here

- [ ] **Step 1: Update the docs in pairs**

Run: `node tools/check-docs.js`
Expected: exit `0`.

- [ ] **Step 2: Run the whole fast gate and the adapter suite**

Run the gate list from `maintain-the-app`, plus
`node --test "tools/test/**/*.test.js"` and `cd WhatsappBridge && npm test`.
Expected: green (the four `zip ENOENT` failures of the tools suite are the known
Windows-without-`zip` cases and are not caused by this work).

- [ ] **Step 3: Append the divergence section and commit**

```bash
git add -A
git commit -m "docs: record the native protocol spike and its one verified result"
```

---

## Run on the device

_(filled by Task 6, Step 4: the probe lines from `diag.log` and the phone model)_

## Review Focus

- **The token tables are data, not code.** A single misplaced entry shifts every
  later index and every later node decodes to nonsense. The only protection is the
  test that pins `SingleByteTokens[3]`, `SingleByteTokens[4]`, `DoubleByteTokens[0][0]`,
  `DoubleByteTokens[0][223]` — and it is deliberately spot-checked rather than
  exhaustive, so a reviewer should diff the tables against `constants.ts`.
- **Attribute order.** `BinaryNode` must preserve insertion order; a `Dictionary`
  iteration order would make the encoding non-deterministic in a way no round-trip
  test catches.
- **The core compiles in two toolchains.** A `System.Security.Cryptography` using,
  a C# 6 construct, or a `Windows.*` using in the shared tree breaks the phone build
  only. The guards catch the syntax; nothing catches a BCL crypto call except review.
- **Endianness and lengths at the edges.** `Binary20` is three bytes with the top
  nibble first, `LIST_16` is big-endian, CBC/GCM nonces are counters — the vectors
  cover them, a hand-rolled "obvious" implementation does not.
- **The handshake is verified against a transcript, not against itself.** A test that
  only proves the client agrees with the client would pass while the phone cannot
  connect; the transcript in `ProtocolTests/Vectors/` is the external truth and the
  reason Task 4 Step 1 writes the generator before the implementation.
