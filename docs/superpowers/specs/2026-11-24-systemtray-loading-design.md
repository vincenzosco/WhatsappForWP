# SystemTray loading indicator

Date: 24 November 2026

## Goal

Show the Windows Phone `SystemTray.ProgressIndicator` whenever the foreground app is loading data or content, including automatic loads and VoIP call setup. A load started on one page must keep the indicator visible across navigation, and overlapping loads must not hide one another.

This change is UI/activity tracking only. It does not alter WhatsApp protocol payloads, call behavior, cache formats, or media content.

## Current state

The app has page-local progress UI: pairing uses `BusyBar`, history sync has a percentage bar on `HomePage`, media rows and the status viewer have their own indicators. `ChatPage` loads cached messages and may wait for history before binding. `DataService.Start` restores cached chats and statuses; other services load preferences, names, avatars and call records. `DataService` also starts chat/status/call scans and fetches message media, status media and avatars. These independent operations do not share a foreground activity count.

`SystemTray.ProgressIndicator` is a Windows Phone 8.1 Silverlight page-attached property. The visible page changes during work, so setting it once on a page does not cover later navigation. `CallSession` distinguishes ringing, connecting, active and ended calls; media transport separately reports waiting, handshake, ready and failed.

## Design

### Shared activity coordinator

Add a small C# 5-compatible loading coordinator. Callers acquire an idempotent disposable activity lease before an actual data/content load and dispose it in `finally` (or equivalent `using` scope). The coordinator tracks concurrent leases rather than a single Boolean, so one completion cannot hide an indicator while another load remains active. Its state and event contract should be independently testable without Windows Phone UI types.

The UI adapter owns one indeterminate `Microsoft.Phone.Shell.ProgressIndicator`, with localized generic loading text. It subscribes to the coordinator, marshals updates through the UI dispatcher, and attaches the indicator to the current `PhoneApplicationPage` after each root-frame navigation. A newly active page immediately reflects the current activity count. With no foreground window/dispatcher, updates are harmless no-ops; a background task cannot display a tray indicator.

Do not track saves, outgoing sends/uploads, typing, audio playback, or routine socket keep-alives as loads. Track data/content reads and fetches: cache restore; chat/message history and sync; status and call-log scans; chat-list refresh; contact/name/avatar fetches and decode; message/status media downloads and decode; status viewing; and other app-owned data retrievals reached from pages or shared services. Automatic operations use the same leases as user-triggered loads. A continuous history sync remains active from its explicit start until its completion/failure signal, not during later idle time.

### Chat opening

Opening a conversation acquires an activity before reading its cached message tail. It remains active until the cache read and list binding/initial scroll complete. If the page waits for the corresponding history burst, that wait is also a load and remains covered until the burst, bounded timeout, or requested on-demand history operation ends. All exits, including navigation away and failures, release their leases; late completion must not update a page that has left.

### VoIP

Track outbound offer submission and incoming acceptance/key/media setup as finite asynchronous activities. An unanswered call's ordinary ringing/wait for the other person is not a load. A pre-accept/setup transition is a load until the call becomes active or ends. Track an attached media transport only while it is in the actual DTLS handshake; waiting for a peer nomination is not an active load, and ready, failed, detached, or ended call state releases the activity. Keep this activity in the shared coordinator, not in `CallPage`, so navigating to the chat during a call cannot strand or hide it. Do not show the indicator for the duration of an established conversation.

### Existing page-local indicators

Keep local progress UI that conveys information the system tray cannot, including history percentage and the current media/status spinner. The SystemTray indicator is the app-wide activity signal, not a replacement for those details. Pairing's local bar may remain as its page-specific feedback while its network/cache steps are also leased.

### Localization and lifecycle

Add the generic loading label to both English and Italian `.resw` files with matching keys. The adapter must attach to the active page after navigation, re-attach on app activation as needed, and never retain a departed page. When the last lease ends, hide the progress indicator even if an operation failed; failures remain logged through existing `Diag` paths.

## Verification

- Unit-test lease count transitions, overlapping leases, idempotent disposal, and the event state transitions.
- Add focused static/regression checks for page attachment on navigation, chat-open lease scope, tracked data-load entry points, VoIP setup versus ringing/active boundaries, and balanced release on failure.
- Run the repository's protocol suite, JavaScript tests, C# 5/XAML/localization/project inclusion/static gates, and Release ARM build.
- Build and prepare the versioned XAP and update script; do not install it on the phone. Device verification requires the operator to apply the update and provide the phone log.

## Risks and limits

- A missed acquisition means one load has no tray signal; an unbalanced lease leaves it on. Explicit scope boundaries plus source regression checks mitigate both.
- A legacy Windows Phone tray property is page-scoped. Navigation reattachment is required and must be exercised by a device run; offline source tests cannot prove shell rendering.
- Background sync runs in a process without a window and cannot display this UI. It is still instrumented only where it shares foreground code, but no indicator is promised while the UI process is absent.
- The SystemTray indicator is indeterminate and generic; exact percentages remain in the existing local sync UI.
