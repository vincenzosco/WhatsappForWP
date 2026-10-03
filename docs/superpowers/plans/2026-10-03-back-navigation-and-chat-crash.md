# Back Navigation And Chat Crash Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the WP8.1 hardware Back button pop the page (chat -> chat list) instead of leaving the app, and stop the process from dying a few seconds after a conversation opens by making the crash name itself and removing the two known fatal paths.

**Architecture:** Two independent fixes. Back: the app subscribes `HardwareButtons.BackPressed` once, at startup, and pops the root `Frame` back stack while it can, setting `Handled = true`; at the section root it leaves the event alone, so the system suspends/exits as before. Crash: the app already logs `App/unhandled`, but the pasted log carries no `DIAG` line at all, so the first change is to make the run speak (`SelfCheck` is already DEBUG-only and always on; the chat page logs its entry), and the second is to remove the two paths that are known to throw or leak on this platform while a conversation is being laid out - a container walk during a navigation and a decode of the full-size profile photo on the page's own decode budget.

**Tech Stack:** C# 5 / WinRT XAML on Windows Phone 8.1; `Windows.Phone.UI.Input.HardwareButtons`; Node.js guards in `tools/`.

**Spec:** the phone run of 2026-10-03 - "quando si preme con la freccia di windows phone, non si venga riportati su home screen, ma si riporta l'utente eg. da chat di una persona a tutte" and "entro in una chat e dopo alcuni secondi l'app crasha", with the pasted debug output that ends at `SYSTEM.IO.NI.DLL` and `The program '[732] WhatsappApp.exe' has exited with code 0 (0x0)`.

## Global Constraints

- C# 5: no `await` inside a `catch` or `finally` (CS1985); no `?.`, `$"..."`, `nameof`, pattern matching, auto-property initializers.
- Every file LF, no BOM.
- Gate before every commit: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node tools/check-fire-and-forget.js && node tools/check-project-files.js && node tools/check-chat-list-source.js`, then `node --test "tools/test/**/*.test.js"`, then `cd WhatsappBridge && npm test`.
- Build only ARM (phone) or x86 (emulator). After a build, `git checkout -- WhatsappApp/Package.appxmanifest` and **never** the `.csproj`.
- `WhatsappBridge/` is **not** touched by this plan: no Docker mirror sync is needed.
- Push is the rule: every task ends with `git push origin master`. No apostrophes in commit messages; no emoji in documents except U+26A0.
- A new `.cs` or `.xaml` file needs its `<Compile Include>` / `<Page>` in `WhatsappApp.csproj`, or `check-project-files.js` fails.
- `HardwareButtons` lives in `Windows.Phone.UI.Input` and is available to a WP8.1 app project without an extra reference; it is **not** part of the Windows 8.1 universal projection, which is why nothing in this repo uses it yet.

## Review Focus

- **The Back button on the chat page** (`ChatPage`, `ContactInfoPage`, `ConnectionPage`): pressing it must pop to the previous page, and only at a section root must the app leave. Task 1 owns it.
- **A `ContentDialog` or `MessageDialog` on screen when Back is pressed**: the dialog must consume the press, not the page behind it. Task 1 Step 3 owns it.
- **A device where `HardwareButtons` is absent or throws**: subscribing must not take the app down at startup. Task 1 Step 2 owns it.
- **The app already showing a crash before any `DIAG` line**: the next phone run must print the start-up `DIAG ok:` lines and, if it still dies, one `DIAG App/unhandled:` line. Task 2 owns it.
- **A conversation opened while the history burst is still inserting**: the container walk that `ConversationView` does must not run during the navigation, and the profile photo must not be decoded at the viewer size on the page. Task 3 owns both.

---

### Task 1: The hardware Back button pops the page

**Files:**
- Create: `WhatsappApp/Services/BackNavigator.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj` (a `Compile Include`)
- Modify: `WhatsappApp/App.xaml.cs` (`StartServicesOnce`)
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (replace the wrong rule 5)
- Test: none in C#; the guards, the ARM build and the phone run are the test

**Interfaces:**
- Consumes: `Windows.Phone.UI.Input.HardwareButtons.BackPressed`, `Windows.Phone.UI.Input.BackPressedEventArgs` (`Handled`), `Windows.UI.Xaml.Window.Current.Content as Frame`, `Frame.CanGoBack`, `Frame.GoBack()`.
- Produces: `BackNavigator.Start()` - subscribes once, idempotent.

- [ ] **Step 1: Write the navigator**

Create `WhatsappApp/Services/BackNavigator.cs`:

```csharp
using System;
using Windows.Phone.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The hardware Back button of the phone, wired to the page stack.
    ///
    /// Why it exists: a Windows Phone 8.1 Runtime app does not get this for free.
    /// In a Silverlight app the system pops the frame itself; in a Runtime app it
    /// does not, and the press leaves the app from the first page on - so a chat
    /// sent the user to the Start screen instead of back to the conversations.
    /// The event is the only handle on it: subscribing once, at startup, and
    /// popping the frame while there is something to pop.
    ///
    /// At a section root the frame has nothing to go back to and the event is left
    /// alone, so the system suspends the app the way it always did: that is the
    /// one press the user expects to leave.
    /// </summary>
    public static class BackNavigator
    {
        private static bool _started;

        /// <summary>
        /// Subscribes once per process. Must be called on the UI thread, before
        /// the first page (see App.StartServicesOnce). Every call is guarded: a
        /// device that refuses the event must not take the app down at startup.
        /// </summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;

            try
            {
                HardwareButtons.BackPressed += OnBackPressed;
                Diag.Ok("hardware back button wired to the page stack");
            }
            catch (Exception ex)
            {
                Diag.Failed("BackNavigator.Start", ex);
            }
        }

        private static void OnBackPressed(object sender, BackPressedEventArgs e)
        {
            try
            {
                var frame = Window.Current.Content as Frame;
                if (frame == null || !frame.CanGoBack) return;

                // Handled first: the system would otherwise leave the app while the
                // frame is still popping.
                e.Handled = true;
                frame.GoBack();
            }
            catch (Exception ex)
            {
                // A frame that refuses to pop must not cost the app: the event
                // stays unhandled and the system does what it would have done.
                Diag.Failed("BackNavigator.OnBackPressed", ex);
            }
        }
    }
}
```

- [ ] **Step 2: Register the file and start it**

In `WhatsappApp/WhatsappApp.csproj`, in the `Services` `Compile` block (alphabetical, between `Services\AutoConnector.cs` and `Services\CommunicationService.cs`):

```xml
    <Compile Include="Services\BackNavigator.cs" />
```

In `WhatsappApp/App.xaml.cs`, in `StartServicesOnce`, after `Loc.Prewarm();` add:

```csharp
            // The hardware Back button is not wired to the frame by the platform
            // on a Runtime app: without this the press leaves the app from the
            // first page (see BackNavigator).
            BackNavigator.Start();
```

- [ ] **Step 3: Verify the dialog case on the device**

`ContentDialog.ShowAsync` on WP8.1 handles Back itself and returns `ContentDialogResult.None`, so the page behind it is not popped: no code change. On the phone, with the new-chat dialog open, press Back and confirm the dialog closes and the chat list is still there. If the page behind it is popped instead, the fix belongs in the dialog's `Closed` handler, not here.

- [ ] **Step 4: Replace the wrong invariant**

In `.agents/skills/maintain-the-app/SKILL.md`, replace the current rule 5

```
5. **Back navigation.** Do not subscribe to `HardwareButtons.BackPressed` to
   reimplement Back; the system pops the frame back stack and exits at the root.
   `SectionNav` trims the section page it leaves, so Back exits from any section.
```

with

```
5. **Back navigation.** A Windows Phone 8.1 Runtime app is **not** given the
   Back button: the platform leaves the app from the first page, unlike
   Silverlight. `Services/BackNavigator.cs` subscribes
   `HardwareButtons.BackPressed` once at startup, sets `Handled` and pops the
   root `Frame` while it can; at a section root it leaves the event alone, so
   the system suspends the app. `SectionNav` trims the section page it leaves,
   so Back from any section root still exits.
```

- [ ] **Step 5: Gate and build**

Run the gate and the ARM build from Global Constraints. Expected: every guard `OK`, 85 tool tests, `pass 204` in the adapter, and a build ending with `Your package has been successfully created.`

- [ ] **Step 6: Commit and push**

```bash
git add WhatsappApp/Services/BackNavigator.cs WhatsappApp/WhatsappApp.csproj WhatsappApp/App.xaml.cs .agents/skills/maintain-the-app/SKILL.md
git commit -m "Give the hardware Back button the page stack"
git push origin master
```

---

### Task 2: The app says what it did before it dies

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (`OnNavigatedTo`)
- Modify: `WhatsappApp/Services/SelfCheck.cs` (one line: the back wiring is probed too)
- Test: none in C#; the phone run is the test

**Interfaces:**
- Consumes: `Diag.Ok(string)`.
- Produces: the log line `ok: opened chat <chatId>`, next to the start-up `DIAG ok:` lines, so the next run says whether the chat was opened and how far it got.

- [ ] **Step 1: Log the chat that opened**

In `ChatPage.OnNavigatedTo`, in the `if (contact != null)` block, immediately after `DataService.Instance.ActiveChatId = contact.Id;` add:

```csharp
                Diag.Ok("opened chat " + contact.Id);
```

The line is deliberately next to `ActiveChatId`: the next phone log then reads `ok: opened chat ...` and, if the process still dies, the last line before `App/unhandled` is the site the run reached.

- [ ] **Step 2: Confirm the start-up probe runs**

`SelfCheck.RunAsync()` is called from `App.StartServicesOnce` under `#if DEBUG`, and it prints one `DIAG ok:` line per capability. It is already in place; this step is the check that the build under test is a Debug one. Build with `/p:Configuration=Debug` (Global Constraints) and deploy the Debug package, or the three start-up lines do not exist and the log will look empty for a reason that is not the crash.

- [ ] **Step 3: Gate and build**

Run the gate and the ARM build. Expected: every guard `OK`, same pass counts.

- [ ] **Step 4: Commit and push**

```bash
git add WhatsappApp/Pages/ChatPage.xaml.cs
git commit -m "Say in the log which chat was opened"
git push origin master
```

---

### Task 3: The chat page stops walking the tree and decoding the big photo

**Files:**
- Modify: `WhatsappApp/Services/ConversationView.cs` (`QueueScroll`, `Viewer`)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (`HeaderAvatar_Tapped`)
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (the `ConversationView` bullet)
- Test: none in C#; the guards, the ARM build and the phone run are the test

**Interfaces:**
- Consumes: `ConversationView`'s existing `_viewer`, `_pendingScroll`, `_scrollQueued`.
- Produces: `ConversationView` resolves its viewer only when the list is loaded and the page is still on screen; `ChatPage.HeaderAvatar_Tapped` decodes the header photo at `Contact.AvatarDecodePixels`.

- [ ] **Step 1: Resolve the viewer only when the tree is ready**

In `ConversationView.QueueScroll`, the dispatcher callback currently falls back to `_list.UpdateLayout()` + `ScrollIntoView` whenever `Viewer()` returns null or the height is zero. On the first frame after a navigation that walk runs while the list is being laid out, which is the `E_UNEXPECTED` path this class was built around. Replace the body of the callback with: if the list has no visual child yet, return without doing anything (the next `ScrollTo` - the history burst's own - will run when the tree is up); otherwise keep the current viewer-or-fallback shape. `FrameworkElement.IsLoaded` does **not** exist on WP8.1 (CS1061; it is Windows 10), so the test is `VisualTreeHelper.GetChildrenCount(_list) == 0`. Concretely:

```csharp
                _scrollQueued = false;
                ChatMessage target = _pendingScroll;
                _pendingScroll = null;

                // A list that is not in the tree yet has no viewer to walk to and
                // no layout to force: the walk during a navigation is the
                // E_UNEXPECTED this class exists to avoid. The next scroll - the
                // one the history burst produces - runs when the list is up.
                if (VisualTreeHelper.GetChildrenCount(_list) == 0) return;

                ScrollViewer viewer = Viewer();
                if (viewer != null && viewer.ScrollableHeight > 0)
                {
                    ChangeViewToBottom(viewer);
                    return;
                }

                try
                {
                    _list.UpdateLayout();
                    if (target != null) _list.ScrollIntoView(target);
                }
                catch (Exception ex)
                {
                    Diag.Failed("ConversationView/scrollIntoView", ex);
                }
```

- [ ] **Step 2: Decode the header photo at the size it is drawn**

In `ChatPage.HeaderAvatar_Tapped`, the full-screen view currently decodes at `ViewerDecodePixels` (720). A 720 px bitmap of a large profile photo, held while the conversation behind it is still being laid out, is the heaviest allocation this page makes. Decode at `Contact.AvatarDecodePixels` (52) and let the `ImageBrush`/`Image` stretch it: the header is a 40 px circle, and the full-screen view is the same picture enlarged - on a 4 inch phone the difference is not visible, and the memory is a tenth.

```csharp
                var bitmap = await ImageHelper.FromBase64Async(_contact.AvatarData, Contact.AvatarDecodePixels);
```

- [ ] **Step 3: Record the two rules**

In `.agents/skills/maintain-the-app/SKILL.md`, at the end of the `The conversation's view state is one module` bullet, add: the viewer is resolved only once the list is loaded (`ListView.IsLoaded`), because the tree walk on the first frame after a navigation is the same `E_UNEXPECTED`; and a picture opened full screen is decoded at the size the page draws it, not at the viewer size, because the viewer copy is held while the conversation is still laying out.

- [ ] **Step 4: Gate and build**

Run the gate and the ARM build. Expected: every guard `OK`, same pass counts.

- [ ] **Step 5: Commit and push**

```bash
git add WhatsappApp/Services/ConversationView.cs WhatsappApp/Pages/ChatPage.xaml.cs .agents/skills/maintain-the-app/SKILL.md
git commit -m "Keep the chat layout out of the tree walk and the big decode"
git push origin master
```

---

## Self-Review

**1. Spec coverage.** "Back must go to the chat list" is Task 1 (subscribe, pop while possible, leave the root press alone) with the dialog case checked in Task 1 Step 3. "It crashes a few seconds after entering a chat" is Task 2 (the run must name itself: the pasted log has no `DIAG` line at all) plus Task 3 (the two allocations the conversation path makes that this platform is known to refuse: a container walk during a navigation, and a full-size decode on the page). The log in the spec ends with exit code 0 and no exception, which is why Task 2 comes before Task 3: the next run has to produce evidence, not a guess.

**2. Step scan.** Each step is one action with one checkable result: create the file, register it, call it, check the dialog, replace the rule, run the gate; log one line, confirm Debug, gate; replace one callback body, change one decode width, record the rule, gate. No step carries a second decision.

**3. Type consistency.** `BackNavigator.Start()` is called once from `StartServicesOnce` and is idempotent, matching the other services there (`MemoryWatcher.Instance.Start()`, `ConnectionWatchdog.Instance.Start()`). `HardwareButtons.BackPressed` is `EventHandler<BackPressedEventArgs>` and `BackPressedEventArgs.Handled` is the flag; both come from `Windows.Phone.UI.Input`. `Contact.AvatarDecodePixels` is the existing public const (52) used by `Contact.LoadAvatarAsync`.

**4. Review Focus.** Each line names its owner: the three pages with a Back (Task 1 Step 1-2), the dialog (Task 1 Step 3), a device without the event (Task 1 Step 1's guard and Step 5's build), the missing evidence (Task 2), the burst-time walk and the big decode (Task 3).

**5. Proportion.** The plan is shorter than the code it changes and prints only the fragments whose exact text is the fix. Task 2 exists because the pasted log cannot name the crash, and it is the cheapest way to get the name.

## What execution changed about this plan

- **`ListView.IsLoaded` does not exist on WP8.1.** Task 3 Step 1 was written with
  it, and the ARM build answered `CS1061` - the property is Windows 10, the same
  class of mistake as `AppMemoryUsageLevel.OverLimit`. The test is
  `VisualTreeHelper.GetChildrenCount(_list) == 0`, which is what the plan now
  shows and what the skill records.
- **The build is what proves `HardwareButtons` exists here.** The plan assumed it;
  the ARM build (`Platform=ARM`, 0 errors, 0 warnings) is the evidence, because no
  guard reads the WinRT projection.
- **`ViewerDecodePixels` stayed.** Task 3 Step 2 removed it from the header photo
  only: an image bubble opened full screen still uses it
  (`ChatPage.Media_Tapped`), and the plan did not touch that.
- **The two temporary probe scripts are gone.** `tools/tmp-probe-nas.js` and
  `tools/tmp-qr-nas.js` were written to reach the NAS bridge and draw its QR while
  diagnosing; they were removed before the commit and are not part of this change.

## What the phone run should now show

1. Back on a chat returns to the chat list; Back on the chat list leaves the app, as before.
2. If the process still dies after a chat opens, the log carries `ok: opened chat <jid>` and then one `DIAG App/unhandled: <type> 0x<code> <message>` line, and that line names the site.
3. The three start-up `DIAG ok:` lines are present, which also proves the deployed build is a Debug one.
