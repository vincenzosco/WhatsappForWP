# WhatsApp 1:1 Voice Calling Interoperability Plan

> **For agentic workers:** execute this plan inline in this thread, in order. Keep the Investigator, Coder, and Publisher roles separate as required by `.agents/skills/agent-coordinator/SKILL.md`.

**Goal:** Make one-to-one WhatsApp voice calls carry bidirectional audio over the actual call signaling and media path, rather than a self-consistent transport that only works against its own tests.

**Architecture:** Keep the phone as the client and the existing `Protocol/Client` and `Protocol/Media` separation. Correct the wire model and participant-scoped media keys first, then implement the WhatsApp relay binding and WARP media envelope, and only then connect the audio source/sink to that path. Do not import the Node worker pool or proprietary WhatsApp Web WASM from `call-WhiskeySockets`; it is a reference implementation for behavior, not a WP8.1 runtime dependency.

**Spec:** `docs/superpowers/specs/2026-11-02-voice-and-video-calls-design.md`; `docs/superpowers/specs/2026-11-04-calls-audio-feasibility.md`; Baileys issue #40 (`https://github.com/WhiskeySockets/Baileys/issues/40`); `call-WhiskeySockets` (`https://github.com/billantony/call-WhiskeySockets`); WACRG signaling and relay reconstructions (`https://wacrg.org/spec/signalling/`, `https://wacrg.org/spec/relay/`), including the specifically consulted relay candidates, relay-latency, SRTP key schedule, E2E SRTP, hop-by-hop SRTP, WARP RTP, RTP framing, and SSRC pages. Keep their statuses attached to each claim: CRY-05 is review, CRY-03 is draft.

## Global Constraints

- The app is a direct Windows Phone 8.1 client; no server, Node runtime, or new account-side service is added.
- The WP8.1 app remains C# 5 and may use only APIs present in its target SDK.
- Treat `call-WhiskeySockets` as a browser/WASM implementation reference; do not copy or package WhatsApp's proprietary WASM asset.
- Keep protocol code portable and testable in `ProtocolTests`; keep WinRT socket/capture integration in the existing app service boundary.
- Protocol facts from WACRG marked `draft`, `review`, or `probable` remain qualified until corroborated by a pinned fixture or live call; never label a self-round-trip as peer interoperability.
- No unsupported video, group-call, call-link, or push behavior may be presented as working.
- Every failure the app survives is reported through `Diag`; no `await` in C# 5 `catch`/`finally` blocks.

## Review Focus

- A relay `te2` address is packed binary (6-byte IPv4 or 18-byte IPv6), not a textual `host:port`; test both and malformed lengths.
- Relay and auth tokens are independent zero-based tables. A missing endpoint index defaults to 0 per WACRG; an absent table slot, out-of-range index, or duplicate table ID must remain unresolved rather than silently selecting another token.
- The current relay probe is a standard unauthenticated Binding request; a real relay bind must use the WhatsApp STUN dialect, authentication, and transaction matching.
- A selected relay must become the actual media destination and use WARP plus hop-by-hop protection; sending only `relaylatency`/`relayelection` while continuing to send direct RTP is not relay support.
- E2E key derivation and SSRC identity depend on the participant LID and stream slot; PN JIDs, caller/callee reversal, and multiple calls must not alias key material.

---

### Task 1: Parse relay blocks without inventing endpoint formats

**Files:**
- Modify: `WhatsappApp/Protocol/Client/CallSignaling.cs`
- Modify: `ProtocolTests/CallSignalingTests.cs`

**Interfaces:**
- Produce `CallRelayBlock.Read(BinaryNode) -> CallRelayBlock`, with block fields `Uuid`, `RelayKeyText`, decoded `RelayKey`, `HopByHopKey`, `WarpMiTagLength`, indexed `Tokens`, and indexed `AuthTokens`.
- Extend `CallRelay` as one endpoint referencing its parent `CallRelayBlock`, with `Address` (`StunEndpoint`), `RelayName`, `RelayId`, `TokenId`, `AuthTokenId`, `Protocol`, `IsFallbackOnly`, optional reported RTT, and resolved `Token`/`AuthToken`. Retain `Host`/`Port` only as values derived from `Address` for existing socket callers.
- `CallSignal.Relays` remains `List<CallRelay>`, with one item per valid `te2`, so consumers receive typed parsed data and do not reinterpret raw nodes.

- [x] Add failing fixtures for a relay with 6-byte IPv4 `te2`, 18-byte IPv6 `te2`, token/auth-token ID lookup, FNA endpoint, and malformed/truncated endpoint/token references.
- [x] Run `node tools/test-protocol.js`; confirm the new parser contract fails before implementation.
- [x] Implement the typed parser; preserve unknown child nodes and skip malformed endpoint entries instead of guessing.
- [x] Re-run the targeted protocol suite and confirm all fixtures pass (741 protocol tests).

### Task 2: Make call signaling carry the relay and media identities it names

**Files:**
- Modify: `WhatsappApp/Protocol/Client/CallSignaling.cs`
- Modify: `WhatsappApp/Protocol/Client/Calls.cs`
- Modify: `WhatsappApp/Services/NativeConnection.cs`
- Modify: `ProtocolTests/CallSignalingTests.cs`

**Interfaces:**
- `CallSignal.CallCreator` preserves the action's `call-creator` attribute.
- `NativeConnection` keeps a bounded call-id-to-creator map, seeded from inbound call actions and the local account JID for outgoing offers.
- Production call builders take an explicit creator JID; native accept/reject/terminate/relay-latency actions resolve the stored creator instead of inferring it from `from` or `to`.
- `CallSignaling.RelayLatency(string callId, string creatorJid, string peerJid, CallRelayLatency sample, IList<string> targetDeviceJids) -> BinaryNode` emits one `te`, encodes `latency = 0x02000000 + rttMs`, carries `relay_name` and the raw packed endpoint, optionally adds destination device JIDs, and omits the outer stanza id.
- `CallRelayLatency.Latency` remains the decoded RTT in milliseconds; `RelayName` and `Data` identify the relay and packed address.

- [x] Add golden-shape tests for creator propagation through offer/accept/reject/terminate, plus relay-latency's no-id wrapper, one `te`, offset latency, packed address, and optional device destinations.
- [x] Add parser tests that recover the creator, decoded RTT, relay name and endpoint bytes.
- [x] Update `NativeConnection` to preserve the creator for each active call, pass it to every production call action, and send one report per measured outbound relay; skip FNA/inbound-only endpoints.
- [x] Implement builders/readers in `CallSignaling` and keep `Calls` as a forwarding facade; do not send a `relayelection` message as a substitute for the selected `transport` until its exact role is verified.
- [x] Run the protocol suite (743 tests), `node tools/check-csharp5.js`, and `node tools/check-comments.js`.

### Task 2a: Open incoming call-key pre-key messages safely

**Files:**
- Modify: `WhatsappApp/Protocol/Client/CallKeyExchange.cs`
- Modify: `WhatsappApp/Protocol/Client/IncomingMessage.cs`
- Modify: `WhatsappApp/Services/NativeConnection.cs`
- Modify: `ProtocolTests/CallKeyTests.cs`

**Observed failure:** An official Android offer reached WP8.1, but the incoming `pkmsg` call key could not open against five pre-existing sessions. The last refusal was an identity-key/session-peer mismatch. WP8.1 still sent `accept`, and the media path did not start. The same run showed the incoming relay probe occurring before the media socket existed.

- [x] Reuse the ordinary incoming-message responder-session rule for call-key `pkmsg`: use the sender JID in the call stanza; start or replace a responder session for an unseen base key.
- [x] Persist the session and consume a one-time pre-key only after the decrypted padded message contains a valid call key.
- [x] Wait for a usable call key before sending `accept`; refuse an undecryptable key instead of claiming the call is connected.
- [x] Defer inbound relay reachability probes until after the accepted call binds its media socket.
- [x] Test a fresh responder, stale-session replacement, and invalid plaintext rollback; protocol suite passes (745 tests).

### Task 2a: Open incoming call-key pre-key messages safely

**Files:**
- Modify: `WhatsappApp/Protocol/Client/CallKeyExchange.cs`
- Modify: `WhatsappApp/Protocol/Client/IncomingMessage.cs`
- Modify: `WhatsappApp/Services/NativeConnection.cs`
- Modify: `ProtocolTests/CallKeyTests.cs`

**Observed failure:** An official Android offer reached WP8.1, but its incoming `pkmsg` call key failed against all five attempted sessions; the last refusal was an identity-key/session-peer mismatch. WP8.1 still sent `accept`, although the media could not start. The same trace showed relay probing before the local media socket existed.

- [x] Reuse the ordinary incoming-message responder-session rule for call-key `pkmsg`: start or replace the responder session for an unseen base key using the sender JID from the call stanza.
- [x] Persist the session and consume its one-time pre-key only after the decrypted padded message contains a valid call key.
- [x] Wait for an opened call key before sending `accept`; send an undecryptable-key rejection instead of accepting without media keying.
- [x] Defer inbound relay reachability probes until the accepted call binds its media socket.
- [x] Test a fresh responder, stale-session replacement, and invalid-plaintext rollback; protocol suite passes (745 tests).

### Task 3: Replace the self-consistent key schedule and RTP profile with participant-scoped media keys

**Files:**
- Modify: `WhatsappApp/Protocol/Media/CallKeys.cs`
- Modify: `WhatsappApp/Protocol/Media/CallMediaSetup.cs`
- Modify: `WhatsappApp/Protocol/Crypto/Srtp.cs`
- Modify: `WhatsappApp/Protocol/Media/RtpPacket.cs`
- Modify: `WhatsappApp/Protocol/Client/CallKeyExchange.cs` only if the confirmed keygen-v2 plaintext requires preserving `raw_e2e` in addition to `callKey`.
- Modify: `ProtocolTests/CallKeysTests.cs`, `ProtocolTests/CallMediaSetupTests.cs`, `ProtocolTests/RtpTests.cs`, and `ProtocolTests/CallEndToEndTests.cs`

**Interfaces:**
- Derive per-participant E2E master material from the confirmed call secret and normalized participant LID; expose the 16-byte master key and 14-byte master salt and discard the remaining 16 bytes of the 46-byte expansion.
- Derive per-participant/per-slot SSRCs from call ID, LID, and little-endian slot word.
- Implement the WhatsApp RTP profile (PT 120/121, `0xDEBE` header extension, WARP MI tag) separately from generic RFC RTP/SRTP code so voice-note/RTP behavior cannot drift together.

- [ ] Add failing fixed vectors for two participant LIDs, caller/callee direction, SSRC slots, 16+14 key/salt split, WARP auth key and RTP PT/header fields. Generate expected vectors with an independent implementation and preserve the cited source/revision beside the tests.
- [ ] Demonstrate that existing `CallEndToEndTests` do not prove peer interoperability: pin at least one fixture that is independent of `CallKeys`/`RtpPacket` serialization.
- [ ] Implement the minimum typed key derivation and WARP RTP packet layer; retain current generic SRTP tests and behavior for any other caller.
- [ ] Run the focused protocol tests, then the full `node tools/test-protocol.js` suite.

### Task 4: Bind to the selected WhatsApp relay and route media through WARP

**Files:**
- Create: `WhatsappApp/Protocol/Media/WarpRelay.cs`
- Create: `ProtocolTests/WarpRelayTests.cs`
- Modify: `WhatsappApp/Protocol/Transport/CallMediaSocket.cs`
- Modify: `WhatsappApp/Services/NativeConnection.cs`
- Modify: `WhatsappApp/WhatsApp.csproj` and `ProtocolTests/ProtocolTests.csproj` only for the new source file.

**Interfaces:**
- `WarpRelay.BuildAllocateRequest(CallRelayBlock relay, CallRelayEndpoint endpoint, byte[] transactionId, CallRelaySubscriptions subscriptions) -> byte[]` constructs an authenticated WhatsApp relay STUN request from the parsed relay key, auth token, relay token, and stream subscriptions.
- `WarpRelay.ReadAllocateResponse(byte[] datagram, CallRelayBlock relay, byte[] transactionId) -> WarpRelayBindingResult` validates transaction ID, integrity, fingerprint, mapped/relayed endpoint, and error response without treating a malformed or refused response as a successful probe.
- `CallMediaSocket` owns direct and relay destinations; a selected relay becomes the path used for packets, while direct ICE remains a fallback only when the protocol actually negotiated it.
- Media sent via relay is wrapped with the hop-by-hop protection and WARP framing from verified source facts; receive processing authenticates/unpacks the relay envelope before E2E SRTP and Opus.

- [ ] Add fixed request/response byte fixtures from an independent implementation or captured protocol sample. If the relay-token protobuf schema or bind response cannot be pinned from the supplied sources, stop this task before writing an invented serializer and report that exact missing evidence.
- [ ] Test wrong transaction IDs, bad HMAC/FINGERPRINT, STUN error responses, timeout, duplicate endpoint/token IDs, IPv4/IPv6, and relay close during an in-flight probe.
- [ ] Implement the verified allocate/bind fields and media envelope, with bounded packet size, timeout, and per-call cancellation.
- [ ] Add a route-selection test proving media packets go to the elected relay after bind and direct packets remain available only for a negotiated fallback.
- [ ] Run the full protocol suite and `node tools/check-fire-and-forget.js`.

### Task 5: Integrate and verify the live 1:1 audio path

**Files:**
- Modify: `WhatsappApp/Services/CallAudioPipe.cs`
- Modify: `WhatsappApp/Services/CallMicrophone.cs`
- Modify: `WhatsappApp/Services/CallPlayback.cs`
- Modify: `WhatsappApp/Pages/CallPage.xaml.cs` only if a truthful audio/media status is needed.
- Modify: tests/guards only where a portable invariant can be checked.

- [ ] Add tests for capture start/stop races, a call ending while the stream opens, muted silence, partial PCM reads, WAV header variation, and microphone denial; keep WinRT-specific behavior behind an injectable/testable seam where feasible.
- [ ] Verify the microphone path does not start on a call that has not completed the trusted media handshake and that stop closes the exact capture for the call that started it.
- [ ] Wire the media engine's frame format to the actual 16 kHz source/sink and relay path; do not claim success from a compile or self-loop alone.
- [ ] Run the project's complete fast gate, protocol suite, tool tests, and Windows Phone 8.1 rebuild.
- [ ] Record the remaining on-device test: two paired devices, incoming and outgoing 1:1 calls, NAT/direct and relay paths, mute, hangup during setup, and a network transition.

### Task 6: Video capability gate

**Files:**
- Read: `WhatsappApp/Pages/CallPage.xaml`, `WhatsappApp/Pages/CallPage.xaml.cs`, `WhatsappApp/Protocol/Media/`, and the WP8.1 SDK surface used by the project.
- Modify only if the existing supported APIs can supply realtime H.264 frames and render received frames within the current 512 MB budget.

- [ ] Establish with SDK metadata and a device-capable API path whether WP8.1 can capture, encode, transport, decode, and render realtime video frames; file recording or a static frame is not sufficient.
- [ ] If all operations are supported, add independent video packetization tests and implement the end-to-end path.
- [ ] If the API path is absent, do not fake video support: keep the video option disabled/explicitly unsupported and record the exact SDK/device evidence in a paired English/Italian spec update.

### Task 7: Final review and publish

**Files:**
- Update the two README files only if their user-visible feature claims change.
- Append `## What execution changed about this plan` to this plan with every divergence.

- [ ] Re-read the complete diff; run the full gate in `.agents/skills/test-the-app/SKILL.md` and the ARM package build.
- [ ] Run the standards/spec review from `.agents/skills/code-review/SKILL.md` using the originating local spec and public protocol references.
- [ ] Commit each independently passing decision with one-line, no-attribution messages.
- [ ] Push `origin/master`, then verify the remote-tracking ref and clean worktree.
- [ ] Report the implemented scope and remaining live-device evidence; do not label 1:1 calling interoperable without the two-device run.

## What execution changed about this plan

- The relay signaling slice corrects relay block parsing and latency reports, but candidate probing still sends an unauthenticated STUN Binding request. It does not allocate a relay, elect a route, or move media; Task 4 remains necessary.
- WACRG's relay-candidate reconstruction specifies omitted `token_id` and `auth_token_id` as index 0. Invalid index text, duplicate table IDs, unpopulated slots, and out-of-range references remain unresolved instead of falling back to another token.
- The ISETool run proved that an official Android offer and its relay block reach WP8.1. The call-key `pkmsg` failed before media negotiation because the production path only tried existing sessions; the app nevertheless sent `accept`. The same run exposed an inbound relay-probe/socket ordering race. Task 2a now reuses the ordinary responder-session rule, gates accept on a valid key, and defers relay probes until a socket exists. A new live call is still required to verify this fix on the phone.
- Task 3 has not started. The independent fixed-vector gate remains unmet; an attempt to generate synthetic key-derived values was blocked by the environment, so no participant-key or WARP RTP implementation was added. Obtain pinned external fixtures or explicit authorization for that local vector-generation step before proceeding.
