# Windows Phone 8.1 Silverlight Migration Plan

> **Goal:** Convert the existing WhatsApp solution in place to Windows Phone 8.1 Silverlight, keep the same solution folder, `WhatsApp.sln`, and three project entries, preserve app data and protocol behavior, and bring the UI close to the supplied original-client references.
>
> **Design:** `docs/superpowers/specs/2026-11-06-wp81-silverlight-migration-design.md`.
>
> **Repository contract:** keep all work in the existing three `.csproj` files; no new project, no source-tree move, C# 5, and commit each independently buildable migration milestone with the project’s one-line/no-attribution rule.

## Acceptance Criteria

- The existing solution builds as WP8.1 Silverlight for x86 and ARM and emits a Silverlight XAP, not a WinRT app-container package.
- The project list, solution path, and source layout remain unchanged; `WhatsApp`, `WhatsappApp.Core`, and `WhatsappApp.Tasks` are all Silverlight-targeted.
- Pairing, account identity, Signal sessions/pre-keys, chat/history, settings, call history, Status, attachments, and protocol behavior survive conversion without requiring a new pairing.
- The app's screens follow the supplied official Windows Phone WhatsApp references: chats/pivots, chat bubbles and composer, attachment chooser, group/contact information, settings, Status, and call screens. Other existing screens follow the same design system.
- The existing protocol tests and project guards pass, updated where a guard encoded a WinRT-only assumption.
- The migration is installed over data only after a non-primary test proves that APPX-to-XAP/update or the chosen data-migration path preserves the account and encrypted session records.
- A physical WP8.1 smoke test proves navigation, pairing/session restore, chat send/receive, attachments, Status, background limits, and calls where the backend path is implemented. Android/iOS call compatibility is reported only after actual two-device tests.

## Phase 0: Baseline and compatibility gate

- [x] Record the clean base commit and baseline gates before project retargeting (745 protocol tests, 473 tools tests, fast guards, and WP8.1 x86/ARM rebuild passed with 0 errors; existing CS4014 warnings remain).
- [x] Map the existing `Windows.*`, `Windows.UI.Xaml`, storage, sockets, media, share/contacts, notifications, and background references by subsystem and source group in the design's Platform Boundary Map; verify exact type availability as each migration phase is built.
- [x] Verify the WP8.1 Silverlight WebSocket API surface against the installed SDK and a Core compile probe.

The Silverlight compile probe confirms that the WP8.1 Silverlight profile lacks `Windows.UI.Xaml`, `ResourceLoader`, `Windows.System.Profile`, `MessageWebSocket`/`IWebSocket`, and `System.IO.Compression`; `Windows.Storage`, `Windows.Media.Capture`, `DatagramSocket`, `StreamSocket`, and `StreamSocket.ConnectAsync(..., SocketProtectionLevel.Tls12)` are present. A temporary Silverlight-profile build also compiles the custom `StreamSocket` WebSocket transport. A raw TLS 1.2 handshake to `wss://web.whatsapp.com/ws/chat` returned HTTP 101 with the expected `Sec-WebSocket-Accept`. The transport implementation is now manual RFC 6455 over `StreamSocket`; a full Core/XAP build and phone runtime test are still required.
- [x] Inspect the installed VS2013 WP8.1 Silverlight application, class-library, and ScheduledTaskAgent templates; map their required properties/imports and manifest entries onto the existing projects only.
- [ ] Prove the current APPX-to-Silverlight XAP identity/data update path on a non-primary emulator or restorable test copy. Preserve ProductId/Publisher mappings and compare only file inventory and non-secret diagnostic state; never print or commit session/pre-key contents.
- [ ] If the update path cannot preserve app data, stop before replacing the primary phone installation and design a tested in-place export/import path without adding a project.

## Phase 1: Retarget the three existing projects

- [ ] Convert `WhatsappApp/WhatsApp.csproj` from the WinRT app-container project type to the WP8.1 Silverlight application type, retaining the same project GUID/name/path and XAML/code files. Emit the existing package identity as XAP with the required Silverlight manifests.
- [ ] Convert `WhatsappApp.Core/WhatsappApp.Core.csproj` to a WP8.1 Silverlight class library with the same project identity and linked source layout.
- [ ] Convert `WhatsappApp.Tasks/WhatsappApp.Tasks.csproj` from WinRT component/`winmdobj` to the WP8.1 Silverlight ScheduledTaskAgent assembly, retaining the same project identity.
- [ ] Update existing project references, build configurations, manifest declarations, capabilities, and `WhatsappApp.sln` project configuration only; do not add or remove project entries.
- [ ] Verify the smallest Silverlight retargeted shell builds to XAP before porting app behavior. If the shell fails, fix project metadata before API or UI changes.

## Phase 2: Port platform services without changing protocol behavior

- [ ] Keep `WhatsappApp/Protocol/**` independent of UI and preserve its existing test coverage; only adapt APIs that the Silverlight target actually lacks.
- [ ] Port `App.xaml.cs`, application lifecycle, navigation, dispatcher access, and crash/diagnostic initialization to Silverlight (`Application`, `PhoneApplicationFrame`, `Deployment.Current.Dispatcher`, and Silverlight lifecycle events as applicable).
- [ ] Migrate the two `Resources.resw` sets, `x:Uid` values, and `Loc.Get` lookups to Silverlight-compatible localized resource files and bindings; keep the English/Italian key sets synchronized.
- [ ] Port file storage and settings while preserving the exact session/pre-key and cache filenames and serialized formats. Verify read/write compatibility against a backed-up LocalState before any device update.
- [ ] Port WebSocket and UDP transports to APIs supported by the WP8.1 Silverlight target. Keep the Noise/WebSocket framing and STUN/media datagram contracts unchanged and add focused transport tests for any adapter replacement.
- [ ] Port media capture/playback, Opus native DLL loading, picker/share operations, contacts, notifications/toasts, tiles, and memory/diagnostic reporting through Silverlight-supported APIs. Keep observable refusal paths and never silently drop a feature.
- [ ] Replace the WinRT background task registration and `IBackgroundTask` entry point with the existing project's Silverlight ScheduledTaskAgent model; verify its actual cadence, CPU quota, cancellation, and `NotifyComplete` behavior on device.
- [ ] After each service group, run protocol tests, applicable guards, and a Silverlight x86/ARM build; commit a passing group before continuing.

## Phase 3: Rebuild the original-client UI in Silverlight XAML

- [ ] Port the app shell and navigation to Silverlight. Use the original pivot categories and native Windows Phone `Pivot`, system tray, and application bar where those match the screenshots.
- [ ] Port the chat list and row templates: original typography, avatar size/cropping, preview/unread/timestamp layout, pin/mute marks, and swipe/pivot behavior.
- [ ] Port the conversation page: header/avatar, incoming/outgoing bubble colors and tails, sender labels, timestamps/status marks, media/voice-note bubbles, typing state, keyboard/composer, and overflow actions.
- [ ] Port attachment selection, photo/video capture and picker, location/contact share, group/contact information, settings, call screen, call log, Status feed/viewer, pairing/connection, and diagnostics pages. Keep the Status and optional screens consistent with the supplied design references even where no exact screenshot was supplied.
- [ ] Map each existing page to its Silverlight page and retain route/state semantics; do not replace working pages with placeholders.
- [ ] Compare the rendered Silverlight pages with the supplied screenshots at supported device sizes and refine spacing, typography, colors, icon geometry, and system chrome.

## Phase 4: Data migration and end-to-end verification

- [ ] On a non-primary emulator or restorable test copy, install the current APPX, populate synthetic app data, update to the Silverlight XAP using the real identity path, and verify the file tree plus app/session readback. Do not expose secret contents.
- [ ] Take a user-approved ISETool snapshot before any primary-device update. Verify that the app still loads the paired account and the sessions it needs without asking for a new pairing.
- [ ] Run the full fast gate from `.agents/skills/test-the-app/SKILL.md`, `node tools/test-protocol.js`, tooling tests, C# 5/API checks updated for Silverlight, project-file checks, comment/doc checks, and a clean WP8.1 Silverlight x86/ARM rebuild.
- [ ] On a WP8.1 device, exercise pairing/session restore, chat list and history, text send/receive, photos/voice notes, picker/share cancellation, notifications, Status, background behavior, mute/hangup, and incoming/outgoing calls.
- [ ] Verify calls against official Android and iOS clients in both directions. Exercise direct and relay paths only when the Silverlight app's actual negotiated route is observable; no self-round-trip counts as interoperability.
- [ ] Keep unsupported or unverified video/background/relay behaviors labeled honestly. Do not claim Meta endorsement; report only observed protocol compatibility.

## Known Risks and Decision Gates

- Microsoft's documented preservation path is for supported Windows Phone 8-to-8.1 upgrades; this repository currently ships a Windows Phone 8.1 APPX. The specific APPX-to-Silverlight-XAP update path is not yet established. This is a hard gate before touching the main phone.
- **Secure WebSocket migration risk:** the Silverlight profile omits `MessageWebSocket` and `ClientWebSocket`; the replacement uses `StreamSocket` TLS 1.2 plus a manual RFC 6455 frame/handshake implementation. The codec and handshake have protocol tests, the transport compiles in an isolated Silverlight-profile probe, and a raw TLS 1.2 handshake to the official endpoint returned HTTP 101. None of this proves `StreamSocket` runtime behavior or the complete transport on a Lumia; do not install over the primary phone until a reversible device test is prepared.
- Silverlight 8.1 supports some Windows Runtime APIs, but not the entire current WinRT surface. Each actual API use must be confirmed against the installed WP8.1 Silverlight SDK or device; compile-time metadata alone is not runtime proof.
- Silverlight 8.1's scheduled-agent model has stricter lifetime/CPU limits than the current WinRT timer task. Background sync behavior may have to degrade within platform limits, but it must remain observable and must not promise push-like behavior.
- Store distribution for new WP8.x XAP submissions has ended. Local development deployment/sideload is the verification path unless the user supplies another supported distribution route.
- The live call-key log exposed an incoming `pkmsg` session gap; the responder-session fix is in the preceding commit and still needs another real Android/iOS call test. Keygen-v2 `raw_e2e`, participant key vectors, authenticated WARP relay bind, and media routing remain separate VoIP plan gates.

## What execution changed about this plan

- The Silverlight package manifests are `WhatsappApp/Properties/WMAppManifest.xml` and
  `Properties/AppManifest.xml` (`AGHost.exe` with a XAML page entry point), not the WinRT
  `Package.appxmanifest` shape; the product id and version are unchanged.
- Localization is embedded resx in `WhatsappApp.Core/Strings` read by `Loc` through a
  `ResourceManager`; the `.resw` pair stays as the key inventory and `check-resw` validates
  the resx pair and their `EmbeddedResource` registration now.
- The tile notification queue lives on `Windows.UI.Notifications`, which Silverlight 8.1
  does supply: `NotificationService` composes every turn before it pushes, the payload sets
  `src` on each image binding, and `TileTheme.EnableNotificationQueue` switches the queue on.
- The scheduled agent listens for cancellation through `ScheduledTaskAgent.OnCancel()`
  (parameterless); `check-diagnostics` accepts it beside the WinRT `taskInstance.Canceled +=`.
- Guards that pinned a WinRT call shape were widened to the Silverlight equivalent with the
  rule intact: `check-accent` (the `Contains` ask beside `ContainsKey`), `check-app-state`
  (the `RunGuarded(name, delegate)` fire-and-forget shape), `check-tile` (URI-wrapped
  `card == null ? AppIcon : ... card.Small`, `Refuse` beside `RefuseAsync`).
- The default language is `it-IT`, the language the app's strings are in; that is also what
  removes the MakePRI 0xdef00522 warning about resources outside the default language.
- P/Invoke is not supported by the WP8.1 Silverlight runtime: `NativeInterop` probes that
  once and every waaudio/waopus path falls back without throwing. Call audio and Opus voice
  notes are degraded until a native Windows Runtime component can bridge them, which needs
  a project beyond the three this plan allows.
