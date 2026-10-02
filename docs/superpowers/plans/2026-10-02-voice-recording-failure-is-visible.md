# Voice Recording Failure Is Visible Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a failing voice-note start show the error sentence and a `DIAG` line instead of doing nothing, and stop a doubled or stuck `MediaCapture` call from wedging the capture engine.

**Architecture:** Two defensive layers, no behaviour change on the happy path. `ChatPage` wraps every recorder call so an exception thrown at the call site — a type or method the phone cannot load, which used to vanish into an unobserved `Task` — is caught, logged and shown, and a start that is already in flight is not started a second time. `AudioRecorder` puts a ceiling on the two `MediaCapture` operations so a hang becomes a `false`, the same `false` every other failure already produces.

**Tech Stack:** C# 5, WinRT `Windows.Media.Capture` / `Windows.Foundation`, Windows Phone 8.1, XAML.

**Spec:** the bug report of 2026-10-02 (tapping the microphone produces nothing on a real phone, and the debugger shows only first-chance exceptions, no `DIAG` line). There is no separate spec document; this plan supersedes the error path of `docs/superpowers/plans/2026-10-01-voice-note-recording.md`, whose happy path it keeps.

## Global Constraints

- C# 5: no `await` inside a `catch` or `finally` (CS1985). A failure is noted, and awaited afterwards outside the block.
- Every file LF, no BOM.
- Every failure the app decides to survive logs through `Diag.Failed(site, ex)`, which prints `DIAG <site>: <Type> 0x<HRESULT> <message>`.
- There is no C# test harness in this repository. The tests of a task are the guard scripts and the ARM build; the last check is on the phone.
- Gate before every commit: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js`, then `node --test "tools/test/**/*.test.js"`, then `cd WhatsappBridge && npm test`.
- Build only ARM (phone) or x86 (emulator), never Any CPU: the manifest declares `AppxBundlePlatforms=arm`.
- `<DeviceCapability Name="microphone" />` stays in `WhatsappApp/Package.appxmanifest`; Visual Studio rewrites that file on every build, so `git checkout -- WhatsappApp/Package.appxmanifest WhatsappApp/WhatsappApp.csproj` after a build.
- No apostrophes in commit messages.

## Review Focus

- A tap that reaches the handler while the recorder throws before returning anything: the user must see the error sentence and a `DIAG` line, never silence. This is the reported bug, and Task 1 Step 3 owns it.
- A `MediaCapture` call that never completes: the same sentence must appear within the ceiling, not a permanent freeze. Task 2 Step 2 owns it.
- A second tap while the first start is still in flight: no second `MediaCapture`, no second dialog. Task 1 Step 2 owns it.
- A start that succeeds: the bar, the timer, the stop button and the send path are unchanged. Task 1 Step 4 owns it.
- Leaving the page mid-recording: still cancelled, no capture left behind, and now also no start left behind. Task 1 Step 5 owns it.

---

### Task 1: The chat page never loses a recording failure

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (the recording region, `RecordButton_Click` through `CancelRecording`)

**Interfaces:**
- Consumes: `AudioRecorder.StartAsync() -> Task<bool>`, `AudioRecorder.StopAsync() -> Task<string>`, `AudioRecorder.CancelAsync() -> Task` (unchanged signatures), `Diag.Failed(string, Exception)`, `Loc.Get(string, string)`.
- Produces: `ChatPage.StartRecordingAsync`, `StopRecordingAsync`, `CancelRecording` that never throw and never lose a fault; the private field `_startingRecording`; the private method `ShowRecordErrorAsync`; the private method `RunGuardedAsync(string where, System.Threading.Tasks.Task work)`.

- [ ] **Step 1: Make the error sentence its own method**

Replace the inline `new MessageDialog(...).ShowAsync()` in `StartRecordingAsync` with a guarded method, so a failing dialog is logged instead of becoming another silent fault:

```csharp
/// <summary>
/// The sentence of a recording that did not start. It is guarded: a dialog
/// that cannot be shown is logged, not thrown into a Task nobody observes.
/// </summary>
private async System.Threading.Tasks.Task ShowRecordErrorAsync()
{
    try
    {
        await new MessageDialog(
            Loc.Get("ChatPage_RecordError", "Could not start the recording. " +
                "Check that this app may use the microphone.")).ShowAsync();
    }
    catch (Exception ex)
    {
        Diag.Failed("ChatPage/RecordError", ex);
    }
}
```

- [ ] **Step 2: A start in flight is a start already running**

Add the field next to `_recording`:

```csharp
// A start that has been asked for and has not answered yet. The microphone
// button is not disabled, so a second tap would otherwise create a second
// MediaCapture: two captures at once wedge the engine on this platform, and
// that is how a tap that did nothing freezes the phone.
private bool _startingRecording;
```

- [ ] **Step 3: Catch what AudioRecorder could not**

Rewrite `StartRecordingAsync` so the recorder call is inside a `try`, the guard wraps the whole start, and every exit is a visible one:

```csharp
private async System.Threading.Tasks.Task StartRecordingAsync()
{
    if (_recording || _startingRecording) return;
    _startingRecording = true;

    bool started;
    try
    {
        started = await AudioRecorder.StartAsync();
    }
    catch (Exception ex)
    {
        // An exception thrown before AudioRecorder's own try - a type the
        // phone refuses to load, a method it does not have - is thrown at the
        // call site, and it lands here instead of in an unobserved Task. This
        // is the case where the old code did nothing at all, silently.
        Diag.Failed("ChatPage.StartRecordingAsync", ex);
        started = false;
    }
    finally
    {
        _startingRecording = false;
    }

    if (!started)
    {
        await ShowRecordErrorAsync();
        return;
    }

    _recording = true;
    _recordStarted = DateTime.Now;
    RecordTimerText.Text = "0:00";
    RecordingBar.Visibility = Visibility.Visible;
    RecordButton.Visibility = Visibility.Collapsed;
    StopRecordButton.Visibility = Visibility.Visible;
    StartRecordTimer();
}
```

- [ ] **Step 4: The stop and the cancel lose nothing either**

Give the two other recorder calls the same treatment. `StopRecordingAsync` keeps its shape but logs and shows instead of dropping a fault:

```csharp
private async System.Threading.Tasks.Task StopRecordingAsync()
{
    string fileName = null;
    try
    {
        fileName = await AudioRecorder.StopAsync();
    }
    catch (Exception ex)
    {
        Diag.Failed("ChatPage.StopRecordingAsync", ex);
    }

    EndRecordingState();

    if (string.IsNullOrEmpty(fileName)) return;

    try
    {
        StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(fileName);
        await AttachmentInbox.PutAsync(file, null);
    }
    catch (Exception ex)
    {
        Diag.Failed("ChatPage.StopRecordingAsync/slot", ex);
    }
}
```

`CancelRecording` awaits the cancel through the same guard:

```csharp
private void CancelRecording()
{
    _startingRecording = false;
    if (!_recording) return;
    EndRecordingState();
#pragma warning disable 4014
    RunGuardedAsync("ChatPage.CancelRecording", AudioRecorder.CancelAsync());
#pragma warning restore 4014
}
```

Add the one helper both fire-and-forget paths use:

```csharp
/// <summary>
/// Awaits a task this page cannot wait for, and logs the fault it would
/// otherwise have dropped. C# 5 has no way to await inside a catch, so the
/// fault is caught here, in its own method.
/// </summary>
private static async System.Threading.Tasks.Task RunGuardedAsync(
    string where, System.Threading.Tasks.Task work)
{
    try
    {
        await work;
    }
    catch (Exception ex)
    {
        Diag.Failed(where, ex);
    }
}
```

- [ ] **Step 5: Leaving during a start is leaving too**

`OnNavigatedFrom` already calls `CancelRecording()`, which now clears `_startingRecording` and returns without touching a capture that was never created. Confirm by reading the call site — no new call is added.

- [ ] **Step 6: Gate and build**

Run the full gate (Global Constraints). Run the ARM build on the VM with the established command, then `iconv -c -f CP1252 -t UTF-8 build-vm.log | tail -30`, delete `build-vm.log`, and `git checkout -- WhatsappApp/Package.appxmanifest WhatsappApp/WhatsappApp.csproj`.

Expected: `GATE=OK` on all eight guards, the tools and adapter tests green, and the build ends with `Your package has been successfully created.` plus `WhatsappServer.exe`.

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml.cs
git commit -m "Show a voice note that fails to start, instead of nothing"
```

---

### Task 2: A MediaCapture call has a ceiling

**Files:**
- Modify: `WhatsappApp/Services/AudioRecorder.cs`

**Interfaces:**
- Consumes: `Windows.Foundation.IAsyncAction`, `WindowsRuntimeSystemExtensions.AsTask` (extension method, reaches `IAsyncAction` with `using System;` already present).
- Produces: `AudioRecorder.StartAsync` still `Task<bool>`; the private const `CaptureTimeoutMs`; the private method `InTimeAsync(Windows.Foundation.IAsyncAction) -> Task<bool>`; the private method `Fail(string, string)`.

- [ ] **Step 1: Add the ceiling and the helper**

Add `using Windows.Foundation;` to the file's usings, then, beside the existing fields:

```csharp
/// <summary>
/// How long one MediaCapture call may take before it counts as failed. Some
/// phones leave InitializeAsync hanging after a failed or doubled start; with
/// no ceiling the tap does nothing forever, which is the bug this answers.
/// Ten seconds is far longer than a healthy start and short enough that
/// nobody waits on it twice.
/// </summary>
private const int CaptureTimeoutMs = 10000;

/// <summary>
/// Awaits one capture operation, or gives up. false means it did not finish
/// in time and has been cancelled. A fault is not swallowed: awaiting the
/// operation surfaces it to the caller, which logs it like every other one.
/// </summary>
private static async Task<bool> InTimeAsync(IAsyncAction action)
{
    Task task = action.AsTask();
    Task finished = await Task.WhenAny(task, Task.Delay(CaptureTimeoutMs));
    if (finished != task)
    {
        try
        {
            action.Cancel();
        }
        catch (Exception ex)
        {
            Diag.Failed("AudioRecorder/cancel", ex);
        }
        return false;
    }

    await task;
    return true;
}

/// <summary>One sentence for a call that ran out of time.</summary>
private static void Fail(string where, string call)
{
    Diag.Failed(where, new InvalidOperationException(
        call + " did not finish in " + CaptureTimeoutMs + " ms"));
}
```

- [ ] **Step 2: Put the ceiling on the two calls, and release on every failure**

Rewrite `StartAsync` so the local capture is declared outside the `try` (a failure must be able to release it) and both operations go through `InTimeAsync`:

```csharp
public static async Task<bool> StartAsync()
{
    if (_capture != null) return false;

    MediaCapture capture = null;
    try
    {
        var settings = new MediaCaptureInitializationSettings();
        settings.StreamingCaptureMode = StreamingCaptureMode.Audio;

        capture = new MediaCapture();
        if (!await InTimeAsync(capture.InitializeAsync(settings)))
        {
            Fail("AudioRecorder.StartAsync/initialize", "MediaCapture.InitializeAsync");
            Release(capture);
            return false;
        }

        StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
            FileName, CreationCollisionOption.ReplaceExisting);

        if (!await InTimeAsync(capture.StartRecordToStorageFileAsync(
                MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Auto), file)))
        {
            Fail("AudioRecorder.StartAsync/record", "StartRecordToStorageFileAsync");
            Release(capture);
            return false;
        }

        _capture = capture;
        return true;
    }
    catch (Exception ex)
    {
        Diag.Failed("AudioRecorder.StartAsync", ex);
        Release(capture);
        return false;
    }
}
```

`Release` already tolerates a null argument? It does not: change its first line to `_capture = null;` and add `if (capture == null) return;` before the try, so the null case is a no-op.

- [ ] **Step 3: Gate and build**

Same gate and the same ARM build as Task 1 Step 6.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Services/AudioRecorder.cs
git commit -m "Give a stuck MediaCapture call ten seconds, then give up"
```

---

### Task 3: The invariant, the record, and the push

**Files:**
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (the invariants list)
- Modify: `docs/superpowers/plans/2026-10-01-voice-note-recording.md` (`## What execution changed about this plan`)

**Interfaces:** none — documentation.

- [ ] **Step 1: Write the two invariants**

In the invariant list of `.agents/skills/maintain-the-app/SKILL.md`, beside the existing voice-note entries, add:

```markdown
- **A recorder failure must be visible.** A fire-and-forget `async Task` that
  throws loses the exception: the tap does nothing and no `DIAG` line appears.
  Every recorder call on `ChatPage` is awaited inside a `try`, and the start is
  single-flight through `_startingRecording`, because two `MediaCapture`
  initializations at once wedge the engine on this platform.
- **A capture call has a ceiling.** `MediaCapture.InitializeAsync` and
  `StartRecordToStorageFileAsync` run through `AudioRecorder.InTimeAsync`
  (ten seconds): a call that does not answer becomes a `false`, the same
  failure every other path already produces, instead of a freeze.
```

- [ ] **Step 2: Record what execution changed**

Append to the `## What execution changed about this plan` section of `docs/superpowers/plans/2026-10-01-voice-note-recording.md` a short paragraph: on a real phone the microphone tap did nothing and left no `DIAG` line, because the failure was thrown at the call site into an unobserved `Task` and because a second tap started a second `MediaCapture`; the follow-up is `2026-10-02-voice-recording-failure-is-visible.md`.

- [ ] **Step 3: Gate, self-review, commit and push**

Run the gate (the documentation guards are part of it). Then commit and push both repositories per the project rule: `git push origin master` here, and the Docker mirror only if a file it carries changed (this plan changes no adapter file, so the mirror takes the `--check` and stops).

```bash
git add .agents/skills/maintain-the-app/SKILL.md docs/superpowers/plans
git commit -m "Record that a recorder failure must be visible"
git push origin master
```

---

## What the phone run should now show

1. Tap the microphone. If the start fails for any reason, the error sentence appears **and** the debugger prints one line: `DIAG ChatPage.StartRecordingAsync: <Type> 0x<HRESULT> <message>`. That line carries the real cause, which the old code hid.
2. Tap the microphone twice quickly. The second tap is ignored while the first is in flight: no second `MediaCapture`, no wedged engine.
3. If a call simply never answers, ten seconds later the same sentence appears and the recorder is released.

## Self-Review

**1. Spec coverage.** The report has two findings that no plan yet answers: the silent tap, and the freeze that follows repeated taps. Task 1 answers both — Step 3 makes the failure visible, Step 2 makes the start single-flight. Task 2 answers the third: a call with no answer. Task 3 records both as invariants so the next change cannot reintroduce them.

**2. Step scan.** Every step is one action: add a helper, add a field, rewrite one method, run the gate, commit. The two rewritten methods are given in full because their control flow is the fix — the `try`/`finally` shape and the position of the guard are the decision, not a body the signature determines.

**3. Type consistency.** `AudioRecorder.StartAsync` stays `Task<bool>` and `StopAsync` stays `Task<string>`; Task 1 consumes exactly those. `InTimeAsync` takes an `IAsyncAction` and returns `Task<bool>`, and is called with `InitializeAsync` and `StartRecordToStorageFileAsync`, both of which return `IAsyncAction`. `RunGuardedAsync` takes the `Task` of a call whose value is unused (`CancelAsync`). `ShowRecordErrorAsync` returns `Task` and is awaited only inside `StartRecordingAsync`.

**4. Review Focus.** Each of the five lines names the task step whose code and check cover it: two taps (Task 1 Step 2), the silent throw (Task 1 Step 3), the hang (Task 2 Step 2), the happy path (Task 1 Step 4), leaving mid-recording (Task 1 Step 5).

**5. Proportion.** The plan is shorter than the code it changes, and it argues from a one-paragraph report rather than transcribing the recorder: the two methods whose shape is the decision appear in full, the rest are signatures and one-line changes.
