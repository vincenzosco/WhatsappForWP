# FrameCodec out of CommunicationService

## Goal

`CommunicationService.cs` was one file doing four jobs: the socket, the connection
attempt state machine, the write queue, and the frame contract. The frame contract -
the 4-byte length prefix, its little-endian byte order, the 8 MiB ceiling and the
whole-frame read - lived among them, and the guard and the invariant both had to name
that file by path to say where framing was. Move the contract into one file,
`WhatsappApp/Services/FrameCodec.cs`, without changing the wire format.

This is architecture candidate 4 from the review (`report-architecture`), scheduled
and run through the agent team on a real prompt.

## What moved

Into `WhatsappApp/Services/FrameCodec.cs` (new, `public static class FrameCodec`):

- `public const uint MaxFrameLength = 8 * 1024 * 1024;`
- `public static DataReader CreateFrameReader(IInputStream)` - pins
  `InputStreamOptions.Partial` + `ByteOrder.LittleEndian`.
- `public static DataWriter CreateFrameWriter(IOutputStream)` - pins the writer.
- `public static async Task WriteFrameAsync(DataWriter, byte[])`.
- `public static async Task<byte[]> ReadFrameAsync(DataReader)`.
- `private static async Task<bool> LoadAtLeastAsync(DataReader, uint)`.

What stayed in `CommunicationService.cs`:

- `CryptoHelper.Encrypt` and the decrypt side - cipher, not framing.
- `_writes` / `SerialQueue` - ordering, not framing. `SendFrameAsync` stays and now
  calls `FrameCodec.WriteFrameAsync`.
- `DecryptToMessage` - cipher + JSON.

Five call sites updated to `FrameCodec.*`:
`CommunicationService.cs:281`, `:378-379`, `:601`, `:866`, `:899`.

## The guard followed the code

`tools/check-framing.js` used to scan `WhatsappApp/Services/CommunicationService.cs`.
It now scans `WhatsappApp/Services/FrameCodec.cs` (rule A), and its "no
DataReader/DataWriter found" message already warned this would be needed ("did the
framing move? then this guard must move too"). Rules B (adapter little-endian helpers)
and C (one ceiling on both sides) are unchanged.

The guard's rule A is scoped to the codec file on purpose: the app has other
`DataReader`/`DataWriter` uses - files, images, incoming media - where the byte order
means nothing. A first attempt that scanned every `.cs` failed the fast gate on
`ImageHelper`, `IncomingMediaStore` and the file pickers, which is the guard doing its
job against an over-broad rule.

## Invariants and docs

- `.agents/skills/maintain-the-app/SKILL.md`: the framing invariant now names
  `FrameCodec.cs` as the one file the contract lives in.
- `README.md` / `README.it.md`, `WhatsappBridge/README.md` / `.it.md`,
  `WhatsappBridge/server.js`: stale `CommunicationService.MaxFrameLength` /
  unnamed-file references updated to `FrameCodec.MaxFrameLength` /
  `WhatsappApp/Services/FrameCodec.cs`.

## Not touched

- The wire format: `check-framing.js` rules B and C pin it, zero change.
- `WhatsappServer/Program.cs:26` holds a third copy of the ceiling
  (`System.Net.Sockets`) - real, but a separate project: out of scope.

## Verification

- `node tools/check-csharp5.js && ... && node tools/check-chat-list-source.js` - 11
  guards `OK`.
- `node --test "tools/test/**/*.test.js"` - tool tests green, including the new
  `tools/test/check-framing.test.js` (5 tests).
- `cd WhatsappBridge && npm test` - 204 adapter tests.
- ARM Rebuild in the Parallels VM, Debug - `.appxbundle` produced.
- Mirror Docker: `WhatsappBridge/server.js` changed, so run
  `node tools/sync.js` and `node tools/sync.js --check`.
- On the phone: a real connect + a real conversation, which is the only place the
  frame read is exercised against the adapter.
