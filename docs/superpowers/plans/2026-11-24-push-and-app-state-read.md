# Push and App-State Read Implementation Plan

> **For agentic workers:** Execute this plan inline in the current session, task by task. Keep all work on `master`.

**Goal:** Open WP8.1 push channels in the supported order and read app-state snapshots returned as encrypted external blobs.

**Architecture:** Keep channel creation and event subscription in `PushChannel`, but open a newly-created channel before binding it to shell notifications. Decode app-state `snapshot` content as the reference client's `ExternalBlobReference`, download the blob through `IMediaFetcher`, verify/decrypt it with the `md-app-state` media keys, then decode and apply the embedded `SyncdSnapshot`.

**Tech Stack:** C# 5, Windows Phone 8.1 Silverlight, protocol core tests on .NET 4.5, Node static tests.

**Spec:** Fresh-device diagnostics in `C:/Users/Vincenzo/AppData/Local/Temp/phone-log-read-20261124/IsolatedStore/diag.log`; reference behavior in `.tools/wa.il` (`MpnsForeground`) and `.tools/ref/package/lib/Utils/chat-utils.js` (`extractSyncdPatches`, `downloadExternalBlob`).

## Global Constraints

- Work directly on `master`; commit and push to `origin/master`.
- Preserve WP8.1 Silverlight and C# 5 compatibility.
- Write regression tests first and observe them fail before implementation.
- Do not install the app or run `Aggiorna-WhatsApp.cmd`.
- A delivered app change increments all three version files in a separate commit after the fix/build gates.

---

### Task 1: Decode an app-state snapshot external blob

**Files:**
- Modify: `WhatsappApp/Protocol/WaProto/AppStateMessages.cs`
- Modify: `WhatsappApp/Protocol/Client/AppStateSync.cs`
- Modify: `WhatsappApp/Protocol/Media/MediaKeys.cs`
- Modify: `ProtocolTests/AppStateTests.cs`

- [x] Add an `ExternalBlobReference` model/decoder using the reference field numbers: mediaKey 1, directPath 2, handle 3, fileSizeBytes 4, fileSha256 5, fileEncSha256 6.
- [x] Add an app-state snapshot fetch/open path using `MediaKeys.AppState` (`WhatsApp App State Keys`), media MAC/hash/length verification, and the existing `IMediaFetcher`; decode the downloaded plaintext as `SyncdSnapshot`.
- [x] Decode the reference's external representation explicitly; do not heuristically treat its media key as a protobuf snapshot.
- [x] Let `SyncAppStateAsync` asynchronously resolve external snapshots before applying collections, preserving per-collection error isolation.
- [x] Add reference-shaped fixture coverage proving the external reference is parsed and the downloaded, decrypted snapshot yields both saved contacts.
- [x] Run `node tools/test-protocol.js`; the external-snapshot regression was observed failing before implementation and passes afterward.

### Task 2: Open push channel before shell binding

**Files:**
- Modify: `WhatsappApp/Services/PushChannel.cs`
- Modify: `tools/test/push-config-iq.test.js`

- [x] Strengthen the source regression to require a new channel's `Open()` before shell toast/tile binding and to retain URI event observation.
- [x] Run the focused test to verify it fails on the current ordering.
- [x] Move channel opening ahead of binding for new channels; adopt existing channels and use the URI already available, while observing URI updates for the initial registration.
- [x] Run the push regression and full Node test suite.

### Task 3: Verify, bump version, commit, and push

**Files:**
- Modify version: `WhatsappApp/Package.appxmanifest`, `WhatsappApp/Properties/WMAppManifest.xml`, `WhatsappApp/Properties/AssemblyInfo.cs`

- [x] Run protocol tests (787), all repository Node tests (832), repository static gates, `git diff --check`, and the WP8.1 Release ARM build at version 1.3.12.87.
- [x] Bump patch version to 1.3.12.88 in the three version files in a separate `Version 1.3.12.88` commit, then rebuild Release ARM to produce the delivered package.
- [x] Review the diff and working tree, create the fix commit and version commit, then push both to `origin master`.
- [x] Do not deploy; on-device behavior still needs verification.

## What execution changed about this plan

- The isolation test's valid collection now uses an external-reference fixture and resolves the blob before applying it; preserving an inline snapshot there contradicted the reference format confirmed during implementation.
- The first full protocol run after the decoding change exposed that outdated test assumption (1 failure); updating the test to the external format restored all 787 protocol tests.
- Static repository checks passed (all 42 `tools/check-*.js` scripts), all 832 Node tests passed, and all 787 protocol tests passed. The WP8.1 Release ARM rebuild passed at version 1.3.12.88 with only existing Opus warnings.
- The final XAP was copied to `C:/Users/Vincenzo/Desktop/WhatsApp-1.3.12.88-ARM-Release.xap`; its SHA-256 is `0c2d31ae63963693613385b84d55266ce7892b96676a937b5ba908a3710a4103`. The packaged `AppxManifest.xml` and `WMAppManifest.xml` both report version 1.3.12.88. `Aggiorna-WhatsApp.cmd` is beside it and was not run.
- Commits `345d6f7` (`Read external app-state snapshots`) and `fbf6c00` (`Version 1.3.12.88`) were pushed to `origin/master`; the local branch was confirmed equal to the remote with a clean tree. On-device behavior remains unverified.
