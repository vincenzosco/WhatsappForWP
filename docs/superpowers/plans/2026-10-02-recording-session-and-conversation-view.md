# Recording Session and Conversation View Implementation Plan

**Goal:** Give the two pieces of `ChatPage` state that keep failing one home each: the voice-note state machine and the conversation's view state (bind, scroll, viewer). Both were spread across loose fields on a 1500-line page, and both produced a bug whose log line named the method instead of the cause.

**Architecture:** Two modules in `WhatsappApp/Services/`, each an instance the page owns. `RecordingSession` holds the transitions idle -> starting -> recording and hides `AudioRecorder`; `ConversationView` holds the list, the scroll queue and the viewer. The page keeps what is only drawing: the bar, the buttons, the clock timer.

**Tech Stack:** C# 5, WinRT XAML, Windows Phone 8.1.

**Spec:** the review of `efb6410..HEAD` (candidate 3 of the architecture report) and the phone log of 2026-10-02, where `E_UNEXPECTED` had moved from `ChatPage/cached messages` to `ChatPage/ScrollToMessage`. There is no separate spec document.

## Decisions taken while grilling

- **Interface: async methods, not an observable state.** `StartAsync` returns `bool`, `StopAsync` returns the file name or null, `CancelAsync` returns nothing. The caller renders the result. An enum plus a `StateChanged` event was rejected: it would make the page observe transitions and keep the bar in step with them, which is more coupling to XAML, not less.
- **The clock timer stays on the page.** It only draws. `RecordingSession` exposes `StartedAt` and the page reads it, the same way it reads any other value it renders.
- **One instance per page**, held in a `readonly` field, not a static. Two `ChatPage` instances can be cached by the frame, and a shared session would be one microphone between two chats.
- **Both extractions in one change**, because they are two halves of the same state on the same page and splitting them would mean touching the same fields twice.

## What moved, and what did not

Moved into `RecordingSession`: `_recording`, `_startingRecording`, `_recordStarted`, the single-flight guard, the call-site `try`/`catch`, and the `AudioRecorder` calls.

Moved into `ConversationView`: `_pendingScroll`, `_scrollQueued`, `_messagesViewer`, `_viewChangedHooked`, the dispatcher queue, the viewer lookup, the `UpdateLayout` fallback and `BottomFollowMargin`.

Stayed on the page: the `DispatcherTimer`, the bar and button visibility, `_markReadPending` (a decision about telling the server, not about the view), and every string.

## What was fixed after the first pass

`RecordingSession.CancelAsync` first forgot a start that had not answered without cancelling a capture it might still create - the page's old behaviour, kept to make the extraction behaviour-preserving. It now always asks `AudioRecorder.CancelAsync`, because that is the only call that reaches a capture created after the leave, and it is harmless when there is nothing to cancel. A start already past its own `try` can still win the race; the window is noted, not closed, and closing it needs a cancellation the platform does not offer here.

## What the phone run should now show

1. A voice note records, stops and sends as before.
2. Tapping the microphone twice quickly still starts one recording, and a start that fails still shows the sentence with a `DIAG RecordingSession.StartAsync` line.
3. Opening a chat shows its messages, and no `DIAG ChatPage/ScrollToMessage` line appears. If one does, it now names the call: `ConversationView/scrollIntoView`, `/findViewer` or `/changeView`.

## What execution changed about this plan

Nothing yet beyond the guard: `tools/check-fire-and-forget.js` matched `Dispatcher.RunAsync` case-sensitively, and the module reaches the dispatcher through a field (`_dispatcher.RunAsync`). The pattern is now case-insensitive, so the module is recognised as a dispatcher call and its lambda's own `catch` is what the guard reads. The page's `StartRecordingAsync` is fired through `Guarded.RunGuardedAsync`, because its `catch` moved into the module with the code it guarded.