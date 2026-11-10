# WP8.1 Native Chrome and Reference Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the existing WhatsApp WP8.1 Silverlight screens match the installed 2.17.52.0 XAP reference, use native Windows Phone ApplicationBar where that client uses it, ensure pairing lands on Home without relaunch, and ensure the chat list contains all synced chats in correct activity order.

**Architecture:** Keep the current three projects, Silverlight 8.1 and C# 5. Use `PhoneApplicationPage.ApplicationBar` for the current Home sections and Chat actions, swapping bar variants as the selected section/chat-selection state changes; retain the conversation composer as page content, styled with the XAP assets, since the reference itself puts it in the page rather than the ApplicationBar. Use the already-read/disassembled XAML and matching packaged image resources as the reference. Diagnose pairing via root-frame navigation events, and treat history row ordering as a separate data-pipeline concern with a failing regression before edits.

**Tech Stack:** C# 5, Windows Phone 8.1 Silverlight, XAML, `Microsoft.Phone.Shell.ApplicationBar`, existing Node source-contract tests, ProtocolTests/MSBuild.

**Spec:** `docs/superpowers/specs/2026-11-06-wp81-silverlight-migration-design.md` (approved source visual contract); detailed reference inventory: `C:\Users\Vincenzo\Documents\WhatsApp-2.17.52.0-CSharp` (read-only external reference).

## Global Constraints

- Retain exactly `WhatsappApp`, `WhatsappApp.Core`, and `WhatsappApp.Tasks`; do not add a project.
- Keep C# 5 and Windows Phone 8.1 Silverlight APIs.
- Preserve user changes and prior uncommitted edits; do not reset or overwrite unrelated work.
- Do not update or uninstall the physical device `de`; use emulator 7 only.
- Do not fabricate history, names, timestamps, media, or server state.
- Localize UI strings in English/Italian resources and keep code comments in English.
- Do not claim every one of the 149 decompressed reference pages is implemented: port only features represented by this app or explicitly scoped screens. Treat the 149-page inventory as evidence for screen patterns and reusable assets.
- Reuse official images/icons from `WhatsApp-2.17.52.0-xap/Images` where licensed/available in the local XAP extraction; add each shipped resource to the existing app project and verify packaging. No new project.
- Build x86 and Any CPU, run protocol/tool tests and relevant guards after each task. Use emulator 7 only after each clean x86 rebuild.

---

## Reference Inventory / Scope Map

**Present app screens:** `HomePage`, `ChatPage`, `ContactInfoPage`, `CallPage`, `StatusViewerPage`, `SettingsPage`, `ConnectionPage`, `PairPage`, `DiagnosticsPage`; shared `TabHeader`, `StatusPane`, and `CallsPane` controls.

**Reference XAP counterparts read:** `ContactsPage` + `TabHeaderControl` + chat list controls; `ChatPage` + `ChatInputBar` + `AttachPanel`; `ContactInfoPage`; `GroupInfoPage`; `CallScreenPage`; `MediaPickerPage` + `MediaPickerAlbumPage`; `MediaGalleryPage`; `SettingsPage` + `pages/settings/*`; `WebSessionsPage`; `WaStatusViewPage`/`MyStatusPage`/`OwnStatusesPage`; relevant app bar resource declarations and image assets.

**Important reference behavior:** Home uses a native WP `Pivot` and swaps `ApplicationBar` by section: main chats, status, favorites, calls, and selected-chats/calls bars. Main XAML contains real search/new-conversation/new-group icon buttons and a real overflow menu. `ChatPage` composer is a page `UserControl`, with its own official bitmap icons and circular record/send transitions; native `ApplicationBar` is for context menu and multi-select commands, not for replacing composer. The original source defaults to a title-panel more menu on chat for that build configuration, and has a native selection ApplicationBar with Forward/Copy/Delete. Image picker uses native bars for select/done.

---

## Acceptance Criteria

1. Home exposes a clickable system-native WP8 `ApplicationBar` that follows the active Chats/Status/Calls section; all actions have correct handlers and actual reference asset icons. Selection bars replace the normal bars when selection mode is active.
2. Chat composer and header actions are visible, hit-testable, and placed in rows declared by the root Grid; official inputbar assets are used in the same semantic positions (attach, text/emoji/keyboard, mic-or-send). Chat contextual actions and selection actions use native ApplicationBar/menu behavior.
3. Pair success log records `pair-success` accepted → credentials saved → login restarted → frame navigation accepted → **frame actually lands on HomePage**. Do not clear or hide a failure merely because `Navigate()` returned true.
4. Chat list contains the conversations in the current history sync (excluding only hidden/non-conversation classes), including people and groups, sorted by valid last activity timestamp, pinned first, stable for equal timestamps. Chats with no timestamp do not mask valid-timestamp order. New unseen chats are placed by activity, not appended at the bottom.
5. App UI assets and page-specific chrome match the XAP for in-scope screens, without invented unavailable backend features. If a design surface cannot be implemented accurately within Silverlight or the three-project rule, identify the technical limitation rather than approximating silently.
6. Tests cover root grid rows, native bar wiring and state changes, toolbar asset registration, pairing frame landing, and chat order across missing/stale/recent timestamps. Full x86/AnyCPU builds, 762 protocol tests, tools tests, comment/resw/icon/actions/project guards pass.
7. Emulator 7 starts with a fresh x86 build. Pairing after QR scan reaches Home and a subsequent clean restart opens Home from the saved credentials. Do not touch device `de`.

---

## Task 1: Correct the chat page layout and determine why its action controls disappear

**Files:** `WhatsappApp/Pages/ChatPage.xaml`, `WhatsappApp/Pages/ChatPage.xaml.cs`, `tools/test/chat-page-layout.test.js` (new).

- [ ] Write a failing source-contract test that parses root `Grid.RowDefinitions` and verifies every direct child `Grid.Row` assignment is within the declared rows. Assert the input composer, message list, typing region and header have distinct intended rows; include that bottom action controls exist and are not behind an overlay at rest.
- [ ] Run `node --test tools/test/chat-page-layout.test.js` and confirm RED because composer is assigned root row 3 while the root declares only four rows? First calculate actual rows and direct child spans; test should specifically discover current overlap/placement rather than assert guessed structure. If it already has four root rows, inspect inner Grid separately: it has three rows but its ring toolbar is assigned row 2 correctly. Record the actual layout defect only after reproducing it on emulator 7 or a real visual capture.
- [ ] Capture emulator 7 screenshot of the newly rebuilt app, navigate to one chat, and confirm whether composer/action row is off-screen or covered. Do not substitute an emulator log for a visual observation.
- [ ] Compare the full original `chatinputbar.xaml`/view model behavior against current ChatPage controls. Create a mapping table in test comments/source tests for attach, text, emoji/keyboard, mic/send toggle; no API or behavior removed.
- [ ] Apply the smallest markup fix if a parent row/clipping/visibility defect is observed. Preserve functional click handlers and `MinWidth="0" MinHeight="0"` for sized buttons.
- [ ] Run the targeted test, `node tools/check-actions.js`, `node tools/check-xaml-names.js`, then x86 rebuild.

## Task 2: Package and use reference image assets; correct search and composer icons

**Files:** `WhatsappApp/Assets/Reference/*` (or existing image folder), `WhatsappApp/WhatsApp.csproj`, `WhatsappApp/Pages/HomePage.xaml`, `WhatsappApp/Pages/ChatPage.xaml`, `WhatsappApp/Pages/ChatPage.xaml.cs`, `tools/test/reference-assets.test.js` (new).

- [ ] Inventory exact XAP source resources for `search.png`, `newgroup.png`, `camera-icon.png`, `assets/notheme/add.png`, `assets/dark/inputbar-attach.png`, `inputbar-emoji.png`, `inputbar-send.png`, `assets/light/*`, `assets/dark/*`, and attachment tile assets. Use image dimensions/alpha channels, do not infer icon names from the file list alone.
- [ ] Add only images needed by in-scope controls to the existing project as content with stable XAP-relative URIs; add project entries explicitly.
- [ ] Add tests that assert image assets exist, project content registration matches, and the intended source URI is used by search/composer elements. Verify fallback for unavailable light/dark variant.
- [ ] Replace the hand-built magnifier with the exact `Images/search.png` bitmap used by the XAP native app bar. Keep automation name localized.
- [ ] Replace the fake ring/send/attach/smiley composer glyphs with the official `inputbar-*` assets. Match the reference state: left attach, center text field, emoji/keyboard toggle, right microphone while empty and send while populated; preserve the existing voice-record gesture/cancel state, emoji input scope and send reliability.
- [ ] Keep contact call action in the chat header only if the original/current call feature has it; do not place it among composer actions unless the matching screenshot/reference confirms it.
- [ ] Run image/project/action/C# 5/XAML guards and x86 build.

## Task 3: Implement the Home native ApplicationBar and per-section variants

**Files:** `WhatsappApp/Pages/HomePage.xaml`, `WhatsappApp/Pages/HomePage.xaml.cs`, `WhatsappApp.Core/Strings/Resources.resx`, `Resources.it-IT.resx`, `tools/test/home-native-appbar.test.js` (new).

- [ ] Add failing tests for `HomePage.ApplicationBar` setup, exact native button/menu action wiring for Chats/Status/Calls, and a selection-mode application bar.
- [ ] Read the original `contactspage.xaml` resource bars, `ContactsPage.MainPivot_SelectionChanged`, `GetStatusAppBar`, `GetCallsAppBar`, and selected chat bar behavior; record precisely which command is enabled on each relevant tab.
- [ ] Implement XAML `ApplicationBar` resources using `ApplicationBarIconButton` and `ApplicationBarMenuItem`, referenced image URIs and existing semantic actions. The Chats bar mirrors Search, New Conversation, Create Group; overflow includes supported Archived, Broadcast, Starred, Web, Settings actions only if those destination screens/actions exist in this app. Unsupported entries are omitted, never dummy actions.
- [ ] Implement status bar with the supported add-status/camera action only if `StatusPane` can actually create a status; otherwise use the native menu commands the app supports and explicitly do not pretend status creation works.
- [ ] Implement calls bar with initiate voice call via existing contact picker; no video-call button while unsupported/unverified. Include native call-log clear/menu only when implementation exists.
- [ ] Bind native bar to active pivot state using `Sections_SelectionChanged`; replace it with native archive/delete action bar during multi-select, restore correct tab bar after exiting selection. Keep all `ApplicationBar` clicks separately named and localized.
- [ ] Remove obsolete custom Home bottom rows only after native actions are wired and tests assert feature parity. Keep pinned/read-all actions in the appropriate WP native overflow menu.
- [ ] Run targets, static guard, locale guard, x86 build, then verify clicks on emulator 7.

## Task 4: Native ApplicationBar and native overflow for ChatPage

**Files:** `WhatsappApp/Pages/ChatPage.xaml`, `WhatsappApp/Pages/ChatPage.xaml.cs`, localization resources, `tools/test/chat-native-appbar.test.js` (new).

- [ ] Write failing tests for native `ApplicationBar` declaration/state, its menu commands, and multi-select replacement bar. Preserve current more/mention/delete behavior.
- [ ] Inspect the decompiled `ChatPage.ResetPageMenu`, `InitDefaultAppBar`, `ResetDefaultAppBar`, `TryInitSelectModeAppBar`, and select-mode callbacks. The reference has a native menu when title-panel overflow is not selected and a native selection bar with Forward/Copy/Delete. Port action availability from current models; disable/hide unsupported/invalid actions, never wire inert buttons.
- [ ] Set up a chat default native ApplicationBar with the contextual actions available for contact vs group and supported by current pages (info, add contact when applicable, wallpaper when supported, select messages). Use `ApplicationBarMenuItem` entries for overflow when native WP menu is the selected reference behavior.
- [ ] When message selection starts, set native selection ApplicationBar. Enable Forward only for 1..10 eligible messages, Copy only for text-only selected messages, Delete for non-empty selection, exactly matching reference eligibility after reconciling supported current actions. Restore the contextual bar on exit.
- [ ] Ensure opening native menu actions navigates to `ContactInfoPage`, `GroupInfoPage` implementation if available (currently group information shares `ContactInfoPage` model/presentation), or supported wallpaper/settings routes without losing selected chat state.
- [ ] Run native bar tests, XAML/actions/localization/project guards, x86 build and emulator click-path verification.

## Task 5: Fix pairing handoff and prove frame landing

**Files:** `WhatsappApp/Pages/PairPage.xaml.cs`, `WhatsappApp/App.xaml.cs`, `tools/test/pair-page-qr.test.js`, and new focused test/helper only if the existing test architecture supports it.

- [ ] Reproduce with the supplied device log: it proves `pair-success`, `Navigate=True`, but lacks `App/navigation landed on /Pages/HomePage.xaml` after it. Add/extend instrumentation for `OnNavigatedTo`, `OnNavigatedFrom`, `frame.CurrentSource`, mapper input/output, `NavigationStopped` if API exists, frame `Navigated` and `NavigationFailed`, all without credentials or QR data.
- [ ] Write failing test(s) distinguishing `Navigate accepted` from actual `HomePage landed`; ensure it fails if only acceptance is logged.
- [ ] Verify Silverlight WP8.1 navigation APIs and runtime behavior in installed reference assemblies. Trace mapper behavior specifically after `_initialNavigationPending` becomes false and for URI mapping `/Pages/HomePage.xaml` to avoid stale map/re-entry or navigation suppression.
- [ ] Navigate via the root `PhoneApplicationFrame` on dispatcher and verify actual `CurrentSource`/`Content` is `HomePage`. Do not report success until emulator 7 log after a real QR scan includes `HomePage is now visible`.
- [ ] Keep `_left` guard to avoid navigating for an off-screen page; if the pairing page was already left, surface a durable app route/recovery that doesn't pull user away from a different page.
- [ ] Test failure (`Navigate=false`/NavigationFailed), success (frame landed), and QR timeout paths. Run C# 5/resx/protocol checks and x86 build.

## Task 6: Reproduce and repair chat list sync/order from live source evidence

**Files:** `WhatsappApp/Protocol/Client/NativeChats.cs`, `WhatsappApp/Services/DataService.cs`, `WhatsappApp/Protocol/WaProto/HistorySync.cs` only if decoding is implicated, `ProtocolTests/NativeChatsTests.cs`, `tools/test/chat-list-order.test.js` (new).

- [ ] On emulator 7 only, start the rebuilt XAP with a paired emulator test account (never touch `de`). Copy the non-secret diagnostic log to `.tools` and capture for each list row: server timestamps (`conversationTimestamp`, `lastMessageTimestamp`, newest history message timestamp), derived `Contact.LastMessageAt`, chat kind and resulting index. No JIDs/names/session data in committed artifacts.
- [ ] Reproduce the reported screenshot/log on the active build. Confirm whether “old chats first / groups together / new chats at bottom” comes from stale cache, invalid timestamps, settings/pins, `HistoryConversation` timestamps, or `LongListSelector` visual order. Current code already stable-sorts pinned-first and activity descending; do not change that contract absent evidence.
- [ ] Add failing protocol tests using real-shaped `HistorySync` conversation chunks: valid conversation timestamp, last-message timestamp only, zero timestamps but messages with valid timestamps, out-of-order group/person rows, duplicate/canonical LID rows, stale cache rows replaced by newer sync rows. Assert protocol rows sorted by most recent trustworthy activity.
- [ ] If sync lacks a list timestamp but messages carry the actual latest time, derive row’s last activity from max valid timestamp among chunk messages without changing unread/order semantics. If `ApplyChat` fails to reflect it, fix there. If cached row deserialization loses timestamp, fix cache row explicitly and test round-trip.
- [ ] Assert merged/upserted rows always use one comparator and are resorted after each new valid activity; tie stability preserved and pinned order preserved.
- [ ] Run focused protocol test, 762-suite protocol tests, chat-list cache/order guards, full tool suite, x86/AnyCPU builds. Verify emulator list against sample ordering.

## Task 7: Fix root-frame launch and post-pair routing

**Files:** `WhatsappApp/App.xaml.cs`, `WhatsappApp/Pages/PairPage.xaml.cs`, `WhatsappApp/Pages/HomePage.xaml.cs`, `tools/test/pair-page-qr.test.js`.

- [ ] Inspect the actual WP8.1 `WMAppManifest` default route and confirm default launch page. The supplied log says the frame lands HomePage before PairPage launches, then post-scan `Navigate=True` with no further `Navigated` event.
- [ ] Add regression coverage for URI mapper sequences: initial `/Pages/HomePage.xaml`, mapped initial `PairPage`, post-pair `/Pages/HomePage.xaml` after `_initialNavigationPending` is false, and duplicate launch URI. Ensure mapper preserves explicitly requested route except while true initial routing decision is pending.
- [ ] Align implementation to that rule: initial default route may select PairPage or HomePage using account state; after startup, explicit navigation must pass untouched. Log the mapped input and final frame source. If `Navigate()` accepted but no landing, wait for exactly one frame event with a bounded route timeout and show actionable error/reopen instruction only after timeout.
- [ ] Run the new regression before/after and reproduce on emulator 7 through a real QR scan; verify app remains on HomePage while sync continues.

## Task 8: Apply UI reference parity to remaining existing screens

**Files:** Existing `WhatsappApp/Pages/*.xaml`, controls, selected code-behind, `WhatsappApp/WhatsApp.csproj`, both resource files, and focused tests only.

- [ ] Compare original and current side by side by feature: pairing/connection, chat list, chat (header/bubbles/composer), contact info, settings, call screen, status viewer, attachment picker/gallery. Use `docs/superpowers/specs/2026-10-19-official-xap-look-design.md` and the 149 XAML inventory. Define measurable image/layout asset deviations.
- [ ] Implement the original XAP `AttachPanel` 3×2 tile behavior/assets supported by this app, with a supported `FileOpenPicker` behind each tile. No fake album dates or thumbnails. Check WP8.1 SDK support before using any XNA/MediaLibrary APIs; keep OS picker fallback truthful if full gallery isn't available.
- [ ] Replace UI-specific guessed icons with matching XAP image assets (search, composer, app bar, attachment tiles). Keep app resources localized and include paths in project.
- [ ] Apply reference typography, margins, colors, avatar/bubble geometry and matching native system bars to relevant screens. Preserve user-facing copy/features and accessibility automation names.
- [ ] Do not create out-of-scope reference-only screens such as Broadcast lists or archived chats unless the repo already has a backing model/capability and the task can include complete behavior; add them as a separate approved scope otherwise.
- [ ] Run XAML checks, all guards, tool and protocol suites, x86/AnyCPU builds and emulator screen/navigation tests at 480-WVGA and one higher-resolution emulator if available.

## Task 9: Full parity and release evidence

- [ ] Review the full diff for project-count invariant, correctness, layout, asset registration, UI localization, and only real reference parity claims.
- [ ] Confirm each accepted screen and action has explicit test evidence or an honest manual verification limitation.
- [ ] Run `node tools/test-protocol.js`, `node --test "tools/test/**/*.test.js"`, project fast gate, `node tools/check-comments.js`, full x86 and Any CPU Rebuild.
- [ ] Install clean x86 build on emulator 7; verify Home bars click, Chat composer icons/buttons visible/clickable, chat selection native bar, pair→Home arrival and chat list order. Use no operation on physical device `de`.
- [ ] Update any stale test expectations/report text affected by the change. Commit one self-contained passing change per repo policy. Do not claim 1:1 for screens not actually implemented or tested.

---

## Current evidence / open items

- Latest provided diagnostic has `pair-success` then `frame.Navigate(HomePage)` returned `True`, but no subsequent `App/navigation landed on /Pages/HomePage.xaml`; this is unresolved.
- Same device log had a previous history sync of 174 rows and 481 messages, so history was received. `Contact.LastMessageAt` is populated using `max(lastMessageTimestamp, conversationTimestamp)`; however current run log lacks `order after the sync`, so the user's latest ordering symptom is unverified against the latest code. Prior archived logs show cached rows with `01/01 01:00` sentinel dates and later correct chats at the top; diagnose current ISE snapshot before changing sort policy.
- Current `ChatPage.xaml` root already declares four rows (Auto, *, Auto, Auto); an early rough assessment that root only declared three was incorrect. Its nested composer declares three rows and toolbar is nested row 2. Confirm visual clipping on emulator rather than changing row counts by assumption.
- `HomePage` currently uses custom bottom grids, not WP8 native ApplicationBar. Chat `More` uses a custom Popup. Reference XAP's Home `ContactsPage` has native app bars and swaps them per Pivot tab/selection state; reference ChatPage composer uses `ChatInputBar` in page content, and selected-message mode has native Forward/Copy/Delete ApplicationBar.
- Reference repository currently has no `applicationBar` declarations in our page XAML/code. All changes must respect existing .csproj item registration.
