# WhatsApp WP8.1 XAP Visual Parity Implementation Plan

> **For agentic workers:** Execute inline in this session. Keep each screen-family change independently testable and do not use an emulator or device.

**Goal:** Bring every existing, implemented app screen into faithful visual alignment with WhatsApp 2.17.52.0 for Windows Phone 8.1, using the supplied reference images and local XAP/source as the authority.

**Architecture:** Retain the current Silverlight pages, shared controls, and behavior. Correct the shared palette/chrome first, then align each existing screen family with the XAP's WP8 typography, spacing, shapes, colors, and image assets. Do not add screens, actions, data, or protocol behavior merely to imitate features the app does not implement.

**Tech Stack:** Windows Phone 8.1 Silverlight, C# 5, XAML, existing Node source-contract tests, ProtocolTests, MSBuild 12.

**Spec:** Approved in chat on 2026-11-11: the app should remain 1:1 with the original WhatsApp Windows Phone design. References: `docs/superpowers/specs/2026-10-19-official-xap-look-design.md` for original XAP evidence, user-provided WP screenshots, `C:\Users\Vincenzo\Documents\WhatsApp-2.17.52.0-CSharp`, and `C:\Users\Vincenzo\Documents\WhatsApp-2.17.52.0-xap`.

## Global Constraints

- Keep the three existing projects, Windows Phone 8.1 Silverlight, and C# 5.
- The released WhatsApp WP8.1 XAP and user screenshots outrank generic Silverlight examples; use only techniques compatible with this project.
- Preserve all existing supported interactions and data; do not add unsupported WhatsApp features or fake states.
- Match the phone's own theme/accent behavior instead of freezing a modern WhatsApp green or a guessed cyan.
- Keep UI copy localized in English and Italian; comments in code remain English.
- Do not test, install, update, or launch on a simulator or device; `de` remains untouched. Use local test gates and x86/Any CPU builds only.
- Preserve unrelated work. Commit and push only verified, self-contained changes; never force-push.

---

## File Map and Scope

- `WhatsappApp/App.xaml`, `WhatsappApp/Services/Accent.cs`: shared theme colors and app-owned accent brushes.
- `WhatsappApp/Pages/HomePage.xaml`, `WhatsappApp/Controls/TabHeader.xaml`, `StatusPane.xaml`, `CallsPane.xaml`: home/Pivot, section header, lists, and bottom application-bar visuals.
- `WhatsappApp/Pages/ChatPage.xaml`: conversation title, bubbles, wallpaper, composer, attachment panel, and contextual surfaces.
- `WhatsappApp/Pages/CallPage.xaml`: ringing, active, and ended call states.
- `WhatsappApp/Pages/ContactInfoPage.xaml`, `ConnectionPage.xaml`, `PairPage.xaml`, `SettingsPage.xaml`, `DiagnosticsPage.xaml`, `StatusViewerPage.xaml`: existing secondary screens.
- `tools/test/*reference*.test.js`, `tools/test/*page*.test.js`: existing source-contract coverage; add a narrowly scoped test only where no contract currently catches a visual invariant.
- `WhatsappApp/WhatsApp.csproj`: register an asset only if an exact source-XAP asset is needed and not already present.

## Acceptance Criteria

1. Every existing screen and shared chrome file is reviewed against the reference set; no screen is claimed as visually verified on hardware.
2. Shared accents, backgrounds, text, separators, bubble colors, sizes, typography, and button shapes follow the WP8 XAP/theme rather than the recent ad hoc palette.
3. Existing screens retain their current supported controls and actions; no new user-visible strings are needed unless a verified parity correction requires one (then add both locales).
4. No emulator/device action is performed. All feasible static tests, protocol tests, repository guards, and Windows Phone x86 and Any CPU rebuilds pass.
5. Working tree is clean after commit and `master` is pushed without force.

---

### Task 1: Establish a verified visual baseline and extract reusable XAP rules

**Files:** `WhatsappApp/App.xaml`, all existing page/control XAML (read-only inventory), XAP/source reference files (read-only), existing parity tests.

- [ ] Record page-to-reference correspondence and inspect each existing XAML for colors, font families/sizes, margins, image assets, and geometric primitives; classify each difference as supported parity gap, intentional app behavior, or unsupported reference feature.
- [ ] Read source-XAP implementations for the theme/accent, tab header, chat list row, composer, message bubble, settings, contact/connection, status, and call screen. Read the relevant WP7/WP8 sample Pivot/style/template examples only to validate syntax/API patterns.
- [ ] Save no imported third-party sample source and add no new XAP-derived asset until its exact on-disk source and intended control are confirmed.
- [ ] Record baseline verification results in the plan execution section, including the existing 764 protocol tests, 505 Node tests, C#5/icon/resource guards, and current x86/Any CPU rebuild outcomes.

### Task 2: Restore the shared Windows Phone theme and palette contract

**Files:** `WhatsappApp/App.xaml`, `WhatsappApp/Services/Accent.cs`, focused palette test if required.

- [ ] Add a failing regression assertion that an app-local fixed `PhoneAccentColor`/`PhoneAccentBrush` cannot shadow the platform accent used by the XAP, and that `Accent.Apply` paints app-owned brushes from the platform resource with the documented fallback only when unavailable.
- [ ] Run the focused test and verify RED against current App.xaml's hard-coded `#00A8C7` theme override.
- [ ] Remove only the conflicting local overrides and correct app-owned palette resources to the actual WP8 XAP contract; retain runtime accent application, fallback behavior, and keys still consumed by XAML/code.
- [ ] Ensure incoming/outgoing bubble and bubble metadata brushes follow the XAP formulas/asset behavior for light and dark phone accents; do not force all pages to one modern green.
- [ ] Run focused test, `node tools/check-resw.js --strict`, `node tools/check-csharp5.js`, existing accent guard and x86 rebuild.

### Task 3: Align Home, Pivot, chat rows, Status, and Calls list chrome

**Files:** `WhatsappApp/Pages/HomePage.xaml`, `WhatsappApp/Controls/TabHeader.xaml`, `WhatsappApp/Controls/StatusPane.xaml`, `WhatsappApp/Controls/CallsPane.xaml`, relevant page/control tests.

- [ ] Compare the current element tree with the XAP's tab/title hierarchy, selected/unselected labels, badge, row dimensions, avatar shape, row spacing, separators, and supported native/custom application-bar actions.
- [ ] Add or update focused source-contract tests before changing the XAML for any unprotected mismatch; run them RED.
- [ ] Apply only reference-backed layout/style changes. Preserve active rows, pinned/unread indicators, supported actions, and navigation; do not introduce fake favorites/archive/status data.
- [ ] Verify fixed-size WP8 buttons still set `MinWidth="0" MinHeight="0"`, icons remain inline or use already-packaged assets according to repository rules, and all names/handlers remain coherent.
- [ ] Run Home/status/call visual-contract tests, icon/actions/XAML-name/project-file/localization guards, and x86 rebuild.

### Task 4: Align the Chat page and its existing interaction surfaces

**Files:** `WhatsappApp/Pages/ChatPage.xaml`, existing chat layout/reference-icon/status-icon tests.

- [ ] Compare the XAP chat title, wallpaper, bubble tails/corners/colors, body/meta typography, grouped-message spacing, media blocks, composer, attachment tile panel, and bottom/context bars with current XAML.
- [ ] Add/adjust focused regressions for proven deviations and verify RED before implementation.
- [ ] Correct XAML to exact supported reference measurements, colors, and assets without changing send, attachment, recording, emoji availability, scroll, receipt, or selection behavior.
- [ ] Keep existing limitations truthful: no fake emoji panel, invented media metadata, or inaccessible actions.
- [ ] Run all chat layout/icon/status/action tests, C#5/icons/actions/XAML names/project files/resw guards, protocol tests, and x86 rebuild.

### Task 5: Align CallPage and StatusViewerPage

**Files:** `WhatsappApp/Pages/CallPage.xaml`, `WhatsappApp/Pages/StatusViewerPage.xaml`, existing call/status tests.

- [ ] Compare each visible call state and status viewer state against the corresponding XAP surfaces and user reference images, including system bars, image cropping, type scale, control sizes, and dismissal controls.
- [ ] Add focused regressions for proven style/layout gaps, run RED, and make small XAML-only adjustments where behavior is already correct.
- [ ] Keep call operations and status read/receipt semantics unchanged; do not add missing XAP functions unsupported by the client.
- [ ] Run call/status tests and all applicable app guards; rebuild x86.

### Task 6: Align the existing secondary pages

**Files:** `WhatsappApp/Pages/ContactInfoPage.xaml`, `ConnectionPage.xaml`, `PairPage.xaml`, `SettingsPage.xaml`, `DiagnosticsPage.xaml`, relevant tests.

- [ ] Compare page title and subtitle scale, margins, section headers, toggles, profile image geometry, account state, QR placement, and diagnostics text surface against exact XAP examples and supplied screenshots.
- [ ] Add focused source tests only for reference rules not already captured; run each new test RED before changing its XAML.
- [ ] Apply reference-backed adjustments while preserving back navigation, QR visibility rules, account state, existing theme choices, alert controls, and diagnostic readability.
- [ ] Keep English/Italian UI values unchanged unless the reference-backed correction requires a corresponding localized string in both resources.
- [ ] Run pair/settings/contact/connection tests and all app guards; rebuild x86 and Any CPU.

### Task 7: Full parity audit and publication

**Files:** all in-scope files changed above and this plan's execution section.

- [ ] Re-read every existing `WhatsappApp/**/*.xaml` and compare against the XAP/source inventory; list no unexamined existing screen in the final report.
- [ ] Run `node tools/test-protocol.js`, `node --test "tools/test/**/*.test.js"`, all available repository guards from the current testing skill, x86 and Any CPU rebuilds.
- [ ] Inspect the final diff, verify no generated build artifacts or unrelated edits are included, and run comment/resource/project-file checks.
- [ ] Append `## What execution changed about this plan` with actual differences and unavailable validations.
- [ ] Commit a standalone verified tranche and push `origin master` without force; confirm the remote commit and clean working tree.

## What execution changed about this plan

- Initial baseline: `node tools/test-protocol.js` passed 764 tests; `node --test "tools/test/**/*.test.js"` passed 505 tests; `check-csharp5`, `check-icons`, and strict `check-resw` passed. The Windows Phone x86 and Any CPU baseline rebuilds are still to be run.
- A complete HTML comparison initially timed out in the preview browser; a smaller, self-contained schematic rendered successfully. It is a direction check, not device evidence or a pixel-perfect acceptance artifact.
- The previously read attachment offered as snippets is a diagnostic log, not code snippets; no implementation code was taken from it.
- Second pass: restored the inline emoji key (the empty grid was a regression), made the chat action bar its own page row so the attachment sheet leaves it visible, restyled the sheet to the reference bottom panel (15 px caps heading, 140 px accent tiles), added the 24 px leading margin to the chat list, bound the conversation to the row under the title bar again, and added the WHATSAPP CALL header. `Call_Header` and `ChatPage_TapForContactInfo` exist in both resources.
- The pairing regression in `check-client-state.test.js` no longer depends on a CRLF checkout.
- This Linux workspace has no MSBuild or WP8.1 SDK: the x86/Any CPU rebuilds and the protocol suite could not run here. The Node suite (516 tests) and every static guard pass.
