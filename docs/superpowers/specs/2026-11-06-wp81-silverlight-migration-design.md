# Windows Phone 8.1 Silverlight In-Place Migration Design

## Goal

Convert the existing WhatsApp Windows Phone solution from the Windows Phone Store / WinRT app model to Windows Phone 8.1 Silverlight, without adding a project or moving the solution. Preserve the current protocol and app behavior, preserve the account and local data, and make the interface closely match the supplied original WhatsApp Windows Phone screenshots, including Status and the other applicable screens.

This is a native app-model migration, not a visual mockup and not a backend rewrite. The target is a Silverlight 8.1 companion client that communicates with official WhatsApp Android and iOS clients. That is a technical compatibility target, not a claim of Meta endorsement or official support.

## Approved Scope and Constraints

- Keep `WhatsappApp.sln` at its current path and retain the existing three project entries and project GUIDs: `WhatsApp`, `WhatsappApp.Core`, and `WhatsappApp.Tasks`.
- Do not add a `.csproj`, move the source tree, introduce a server, or package community-client runtimes or proprietary WASM.
- Retarget the app, Core library, and background component in place to the Windows Phone 8.1 Silverlight app model.
- Preserve the working protocol, Signal identity/session/pre-key state, chats, settings, call history, and other local data through the app-model transition. Keep the current app/product identity mappings; prove the actual APPX-to-XAP update path before touching the user's installed app.
- Keep C# 5 and Windows Phone 8.1 SDK compatibility. Retain protocol code as portable .NET code where possible; platform-specific replacements stay behind existing service boundaries.
- Treat the supplied screenshots as the visual authority for the screens they show. Build the remaining existing and optional screens in the same original WhatsApp Windows Phone design language rather than inventing a new UI.
- Keep the UI strings localized in Italian and English.
- Do not advertise video or call media as functional unless the corresponding existing protocol and device gates pass.

## Approaches Considered

### 1. Convert the existing solution in place (recommended)

Retarget the three existing projects, retain the source layout and project identities, convert the UI to Windows Phone Silverlight controls, and port only the platform code that is unavailable to Silverlight. This follows the explicit no-new-project constraint and keeps the tested protocol code close to its existing consumers.

### 2. Keep a WinRT backend behind a Silverlight shell

This could reduce the UI rewrite, but a Silverlight application cannot simply reference the existing Windows Phone Store application model and WinRT background component as if they were Silverlight assemblies. It would also leave the requested conversion incomplete and create two incompatible app lifecycles. Rejected as the target architecture.

### 3. Recreate only the screenshots in the current WinRT app

This has the lowest cost but does not change the app model. It fails the request to convert the full solution to Silverlight. Rejected.

## Target Project Architecture

### `WhatsApp` project

Retarget the existing project to a Windows Phone 8.1 Silverlight XAP. Replace the WinRT application root and page types with `Application`, `PhoneApplicationFrame`, and `PhoneApplicationPage`. Use native Silverlight `Pivot`, `ApplicationBar`, and `SystemTray` behavior where it matches the screenshots. Retain the existing project path, GUID, assets, and localized resources; add or adapt the Silverlight manifests required by the SDK without creating another project.

### `WhatsappApp.Core` project

Retarget the existing library to the Windows Phone 8.1 Silverlight class-library profile. Keep the existing protocol, Signal, crypto, message, and model code in the same source tree. Audit every `Windows.*` dependency and preserve it only when it is present and supported in the Silverlight 8.1 profile; otherwise replace it with a Silverlight implementation behind the current service boundary.

### `WhatsappApp.Tasks` project

Retarget the existing project to the Silverlight scheduled-task-agent assembly required by Windows Phone 8.1 Silverlight. Keep the same project name and GUID. Replace the WinRT `IBackgroundTask` activation path with `ScheduledTaskAgent`/`PeriodicTask` registration and its Silverlight manifest declarations. Preserve the task's bounded work and logging; respect the platform's short execution window and periodic-task limits.

## Platform Boundary Map

The conversion must inventory and port concrete uses rather than apply namespace substitutions blindly:

- **XAML/UI:** `Windows.UI.Xaml` pages, `Frame`, `FlipView`, `ListView`, dialogs, resource dictionaries, navigation, and dispatcher calls become Silverlight page/navigation/control/resource patterns. Lists and chat bubbles must preserve virtualization and low-memory behavior.
- **Sockets:** preserve the WebSocket framing/TLS contract and the UDP call socket contract. Select only socket APIs actually available in the WP8.1 Silverlight target (including the platform's supported `System.Net.Sockets` or WinRT-compatible surface); verify on device or emulator, not only by compiling.
- **Storage:** preserve the existing file names, contents, and account identity. Determine whether the Silverlight app can continue using the Windows Phone 8.1 `ApplicationData` local folder or needs an `IsolatedStorage` adapter. No store reset, re-pair, or data deletion is an acceptable migration shortcut.
- **Background work:** map the current timer task to a Silverlight scheduled agent, accounting for agent lifetime, network availability, rescheduling, and the distinction between foreground reconnection and background refresh.
- **Media/audio:** keep the current native Opus/audio DLLs only if they load from the Silverlight XAP on WP8.1. Verify microphone capture, output playback, socket delivery, and consent/capability declarations. Silverlight 8.1 supports some Windows Runtime capture APIs, but each API used by this app still needs a target-SDK check and device evidence.
- **Pickers/share/contacts/notifications/tiles:** map each current WinRT contract to its Silverlight 8.1 task/manifest equivalent; preserve cancellation, file ownership, and local-data behavior.
- **Localization and diagnostics:** replace WinRT `.resw`/`x:Uid`/`ResourceLoader` lookups with Silverlight localized resources while retaining every Italian and English string. Preserve the existing diagnostic format and add equivalent instrumentation around migrated platform calls.

## Visual Design Contract

- Match the supplied historical WhatsApp Windows Phone references: dark/black surfaces, original cyan/teal message and accent colors, large/light pivot headings, compact top status/header areas, native Windows Phone typography, chat bubble direction/tails, timestamps and status marks, and the original compact icon/application bars.
- Port the existing chat list, chat page, attachment chooser, group/contact information, settings, call screen, Status feed/viewer, pairing/connection, and diagnostics pages. Status and other original-client pages not shown in every reference remain part of the scope when the current app exposes them.
- Prefer native Silverlight phone controls for system chrome and interaction states. Draw a custom control only where the supplied reference requires a shape not provided by the native control.
- Verify screen layouts at WVGA, WXGA, and 720p phone dimensions and against the supplied screenshots; visual resemblance is not established by compilation alone.

## Data Preservation and Distribution Gate

The current app is a Windows Phone Store / WinRT-style APPX. Microsoft documentation describes in-place data preservation for supported Windows Phone 8 to Windows Phone 8.1 Silverlight/Store upgrade paths and requires matching PhoneProductId/PhonePublisherId mappings. It does not yet establish this repository's exact transition from the present Windows Phone 8.1 APPX to a Silverlight XAP. Therefore:

1. Keep the current `Identity`, `PhoneProductId`, `PhonePublisherId`, WMAppManifest ProductId/PublisherId, and storage keys aligned.
2. Prove the update/migration path on a non-primary emulator or a restorable test copy before deploying over the user's active app.
3. Use ISETool to snapshot and compare the required account/session/data files without displaying or committing secret contents.
4. If the platform cannot preserve the store during APPX-to-XAP replacement, stop before deployment and design an explicit, verified export/import path that keeps the account session intact. Do not silently clear state or require re-pairing.
5. Windows Phone 8.1 Silverlight XAP distribution is a device/sideload target; Microsoft Store stopped accepting new WP8.x XAP submissions. This migration does not imply Meta or Microsoft endorsement.

## Verification and Acceptance

- All three existing projects build as Silverlight 8.1 from the unchanged solution; no WinRT app-container or WinRT component target remains.
- The XAP installs/updates on a WP8.1 emulator/device without losing the paired account, Signal sessions/pre-keys, messages, chat ordering, settings, call log, or Status data.
- Existing protocol and tools suites pass; update static guards so they verify Silverlight APIs/project targets rather than the old WinRT target.
- Functional smoke test: login/resume; one-to-one messages and media; attachments and share/pickers; contacts; notifications; live tile/background behavior within Silverlight limits; call signaling/audio behavior; Status; settings and navigation.
- Visual comparison covers the supplied references and every existing app page at supported WP8.1 resolutions.
- State separately what was not verified on a physical device. Do not claim real Android/iOS call interoperability without a live cross-device test in both directions.
