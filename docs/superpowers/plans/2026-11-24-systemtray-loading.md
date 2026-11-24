# SystemTray Loading Indicator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show the WP8.1 SystemTray progress indicator for overlapping foreground data/content loads, chat opening, and finite VoIP setup without showing it for ringing waits or established calls.

**Architecture:** A UI-independent, reference-counted activity lease lives in `WhatsappApp/Services` and is compiled into the shared core. A foreground UI adapter attaches one indeterminate `ProgressIndicator` to the current page after navigation. Data, page, and VoIP entry points acquire/release leases in `try/finally`; existing detailed local bars remain.

**Tech Stack:** C# 5, Windows Phone 8.1 Silverlight, `Microsoft.Phone.Shell.ProgressIndicator`, existing .resw localization, ProtocolTests/.NET 4.5, Node.js static regression tests, MSBuild ARM.

**Spec:** `docs/superpowers/specs/2026-11-24-systemtray-loading-design.md` and `.it.md`.

## Global Constraints

- Keep the app compatible with Windows Phone 8.1 Silverlight and C# 5.
- Do not change protocol payloads, cache formats, call signaling, or media contents.
- Keep English and Italian resource keys in sync; visible app strings must use localization.
- Never install on the phone; prepare the versioned XAP and update script for the operator.
- Bump `1.3.12.85` to `1.3.12.86` in Package.appxmanifest, WMAppManifest.xml, and AssemblyInfo.cs.
- Run the repository gates, Node tests, Release ARM build, then commit and push; read the phone log before claiming on-device behavior verified.

---

### Task 1: Add a UI-independent loading activity counter

**Files:**
- Create: `WhatsappApp/Services/LoadingActivity.cs`
- Modify: `ProtocolTests/ProtocolTests.csproj`
- Create: `ProtocolTests/LoadingActivityTests.cs`
- Create: `tools/test/loading-activity.test.js`

**Interfaces:**
- Produces `LoadingActivity.Begin(string name) : IDisposable`, `LoadingActivity.IsActive`, and `LoadingActivity.Changed : event EventHandler`.
- Each returned lease releases exactly once; disposing twice is harmless. The counter publishes only active/inactive transitions, not every count mutation.

- [x] **Step 1: Add failing C# tests** for initial inactive state, one lease activates, two leases remain active after the first release, final release deactivates, and double-dispose does not underflow or emit a duplicate transition.
- [x] **Step 2: Include `LoadingActivity.cs` in the ProtocolTests project and run** `node tools/test-protocol.js`; confirm the new tests fail before implementation.
- [x] **Step 3: Implement the smallest thread-safe counter** with a private lease type using `Interlocked.Exchange` for idempotent release and an event raised outside the lock.
- [x] **Step 4: Run** `node tools/test-protocol.js` and verify the lease tests pass.
- [x] **Step 5: Add a Node regression test** that guards the interface and idempotent release contract; run `node --test tools/test/loading-activity.test.js`.

### Task 2: Attach a localized SystemTray indicator to every active page

**Files:**
- Create: `WhatsappApp/Services/LoadingIndicator.cs`
- Modify: `WhatsappApp/App.xaml.cs`
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`
- Modify: `WhatsappApp/Strings/it-IT/Resources.resw`
- Modify: `tools/test/loading-activity.test.js`

**Interfaces:**
- `LoadingIndicator.Start(PhoneApplicationFrame frame)` subscribes once to frame navigation and `LoadingActivity.Changed`, attaches the indicator to the current `PhoneApplicationPage`, and reflects current activity on every page.
- `LoadingIndicator.Refresh()` updates `IsVisible` on the UI dispatcher; a missing dispatcher/page is a no-op.
- Resource key: `LoadingIndicator_Loading` in both languages.

- [x] **Step 1: Add a failing source regression** for startup wiring, navigation reattachment, localized text, and active-state reflection.
- [x] **Step 2: Run** `node --test tools/test/loading-activity.test.js`; confirm it fails on missing adapter/wiring/key.
- [x] **Step 3: Implement the adapter** with one `ProgressIndicator { IsIndeterminate = true, Text = Loc.Get(...), IsVisible = ... }`; on each frame `Navigated`, attach it with `SystemTray.SetProgressIndicator(page, indicator)` and update visibility. Subscribe once from `App.StartServicesOnce` while on the UI thread.
- [x] **Step 4: Run** `node tools/check-resw.js --strict`, `node tools/check-csharp5.js`, the focused Node test, and `node tools/check-project-files.js`.

### Task 3: Cover chat opening and app-owned data/cache loads

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs`
- Modify: `WhatsappApp/Services/DataService.cs`
- Modify: `WhatsappApp/Services/AvatarEnricher.cs`
- Modify: `WhatsappApp/Services/ContactDirectory.cs` only if its phone-contact retrieval is part of the foreground load; this file already exists.
- Modify: `tools/test/loading-activity.test.js`

**Interfaces:**
- Use `using (LoadingActivity.Begin("..."))` around finite awaited loads.
- Chat-opening lease covers cache load, initial bind/scroll, and the existing bounded per-chat history wait; it releases on all exits and does not access UI after `_left`.

- [x] **Step 1: Add failing regression assertions** for `BindAfterCacheAsync` scope, cache/status restoration, sync completion, list refresh/scan entry points, avatar enrichment, and guaranteed release in `finally`/`using` scopes.
- [x] **Step 2: Run** the focused test and confirm the uncovered call paths fail.
- [x] **Step 3: Instrument the chat open** from before `LoadCachedMessagesAsync` through bind/scroll and the existing history wait; ensure the lease is disposed if the page is left or the wait throws.
- [x] **Step 4: Instrument actual async data retrievals** in `DataService` and existing cache/avatar/name/contact services: cached chats/statuses, chat/status history sync, manual resync, list refresh reads, calls/status scans, and avatar retrieval/decode. Instrument phone-contact lookup only when it runs in the foreground; do not instrument `SaveSnapshotAsync`, routine socket reads, or idle polling.
- [x] **Step 5: Run** `node --test tools/test/loading-activity.test.js`, `node tools/check-fire-and-forget.js`, `node tools/check-csharp5.js`, and `node tools/test-protocol.js`.

### Task 4: Cover user-visible media, status, and page retrievals

**Files:**
- Modify: `WhatsappApp/Services/DataService.cs`
- Modify: `WhatsappApp/Pages/StatusViewerPage.xaml.cs`
- Modify: `WhatsappApp/Pages/ContactInfoPage.xaml.cs`
- Modify: `WhatsappApp/Pages/CallPage.xaml.cs` only for avatar/data decode started by the page.
- Modify: `tools/test/loading-activity.test.js`

**Interfaces:**
- Media and status download/decode activities use the same coordinator as data/cache reads.
- Existing `IsMediaLoading`, status loading panel, and history percentage remain unchanged as detailed local progress.

- [x] **Step 1: Add failing checks** for media download, status media download/view, contact avatar request/decode and call avatar decode leases.
- [x] **Step 2: Run** the focused test to establish the red result.
- [x] **Step 3: Add balanced leases** around message media and status media requests (including storage/decrypt/apply), status image retrieval/decode, contact picture lookup/decode, and call avatar decode. Reuse existing try/catch logging and ensure every early return releases.
- [x] **Step 4: Run** focused tests, `node tools/check-memory.js`, `node tools/check-status-viewer.js`, `node tools/check-fire-and-forget.js`, and localization checks.

### Task 5: Track finite VoIP call setup, not ringing or active conversation

**Files:**
- Modify: `WhatsappApp/Services/DataService.cs`
- Modify: `WhatsappApp/Services/NativeConnection.cs`
- Modify: `WhatsappApp/Protocol/Transport/CallMediaSocket.cs`
- Modify: `WhatsappApp/Pages/CallPage.xaml.cs` only if page-side setup retrieval requires a lease.
- Modify: `tools/test/loading-activity.test.js`

**Interfaces:**
- Outbound `StartCallAsync` holds a lease only while the offer is being created/sent; it releases after signaling returns, not while waiting for the callee.
- Incoming key unwrap and `AcceptCallAsync` hold leases for their finite async work.
- Media transport emits/bridges state transitions so the coordinator holds a lease only for an attached `CallTransportState.Handshaking` transport; release on `Ready`, `Failed`, detach, termination, or replacement. `Waiting` is not active loading.
- Call state must not be tracked by a page lifetime; navigation to chat does not orphan the lease.

- [x] **Step 1: Add failing source checks** proving offer/accept/key paths are leased, transport handshake state is observed, and `Ringing`/`Active`/waiting nomination do not independently hold the indicator.
- [x] **Step 2: Run** the focused test and establish failure on current call paths.
- [x] **Step 3: Instrument finite offer, key unwrap, incoming acceptance, and media handshake transitions** with idempotent leases and release on all terminal outcomes.
- [x] **Step 4: Preserve call signaling and UI behavior**; do not lease the 45-second unanswered ringing period, call duration, Opus/audio pump, or transport-ready state.
- [x] **Step 5: Run** focused checks, `node --test tools/test/call-ticker-stops-on-leave.test.js`, C# 5 guard, and protocol tests.

### Task 6: Full verification, version bump, build, and delivery artifacts

**Files:**
- Modify: `WhatsappApp/Package.appxmanifest`
- Modify: `WhatsappApp/Properties/WMAppManifest.xml`
- Modify: `WhatsappApp/Properties/AssemblyInfo.cs`
- Update: `docs/superpowers/plans/2026-11-24-systemtray-loading.md` with execution deviations.
- Create only the requested build artifacts: versioned desktop XAP and `Aggiorna-WhatsApp.cmd` beside it.

- [x] **Step 1: Run all project gates** listed in `.agents/skills/test-the-app/SKILL.md` and `node --test "tools/test/**/*.test.js"`; resolve failures before bumping the version.
- [x] **Step 2: Bump each of the three version entries** from `1.3.12.85` to `1.3.12.86` and check they match.
- [x] **Step 3: Build Release ARM** with `MSYS_NO_PATHCONV=1 "C:/Program Files (x86)/MSBuild/14.0/Bin/MSBuild.exe" WhatsappApp/WhatsApp.csproj /p:Configuration=Release /p:Platform=ARM /v:minimal /nologo`.
- [x] **Step 4: Copy the built XAP to** `C:\Users\Vincenzo\Desktop\WhatsApp-1.3.12.86-ARM-Release.xap` and place `Aggiorna-WhatsApp.cmd` beside it; never run the update script or install to the phone.
- [x] **Step 5: Review** `git diff --check`, version consistency, generated XAP existence, and clean deliverables; append only actual deviations to the plan.
- [x] **Step 6: Commit** the completed implementation and plan with a short imperative, no AI attribution; push `origin master` as required by repository workflow.
- [x] **Step 7: Report** test/build evidence and clearly state that phone verification still awaits the operator's update and a fresh device log.

## What execution changed about this plan

- `LoadingIndicator.cs` is app-only UI code, so the shared `WhatsappApp.Core.csproj` service glob explicitly excludes it. The first ARM build exposed a duplicate-type warning; a source regression test was observed failing before the exclusion was added, and the next ARM build completed without that warning.
- The full test and gate suite was repeated after the exclusion. Device verification remains pending; no install command was run.
- The independent review found that late history progress could reacquire the tray lease after an account reset. A failing source regression was observed first; progress is now gated by account lifecycle, and downloaded history from a replaced connection is discarded before it mutates account data.
- Final verification on the pushed `master`: 786 protocol tests passed; all 830 Node tests passed with no skips; every `tools/check-*.js` guard passed. A fresh Release ARM build succeeded with the existing `CS0168` warning in `OpusEncoder.cs` and packaging warning `APPX4001`. The Desktop XAP was recopied from that build and has SHA-256 `809da88d55a2755ec1a22f158d66bdab3e65474da826cffa88ebe4f3d92870da`; `Aggiorna-WhatsApp.cmd` is beside it. The script was not run and the phone has not been updated; device verification still requires the operator's update and a fresh log.
