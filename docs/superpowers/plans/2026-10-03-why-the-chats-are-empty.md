# Why The Chats Are Empty Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the phone itself say why the chat list is empty and why the app leaves, and fix the most likely cause of the first, so the next run produces evidence instead of another pasted assembly list.

**Architecture:** The adapter is healthy - it read 21 conversations and answered them - and the app never reached it: across the whole container log there is not one `handshake from`, not one `invalid frame from the app`, and the only connections are internet noise on the public tunnel. The pasted app log has no `DIAG` line at all, so the app's side of the story does not exist anywhere: a debugger window is the only place `Diag` writes, and the user has no debugger. Two things follow. First, `Diag` gains an in-memory ring buffer and a screen that shows it on the phone, so a run reports itself without a PC. Second, the app records the two things nobody can see today - which address it is about to dial and whether the endpoint file answered - because "the chats never load" with a server that is fine is almost always the address. The screen is the deliverable: every later fix reads it.

**Tech Stack:** C# 5 / WinRT XAML on Windows Phone 8.1; `Debug.WriteLine`; the `.resw` pairs; the Node.js guards in `tools/`.

**Spec:** the user's report of 2026-10-03 - "le chat sembrano ancora che non caricano con lo stesso result di prima, l'app crasha, se vuoi aggiungi linee debug per vedere il motivo" - with the pasted debug output that ends at `SYSTEM.IO.NI.DLL` and `The program '[3580] WhatsappApp.exe' has exited with code 0 (0x0)`, and the adapter's log on the NAS for the same window: `Chats: 21 conversation(s) from 25`, and zero handshakes.

## Global Constraints

- C# 5: no `await` inside a `catch` or `finally` (CS1985); no `?.`, `$"..."`, `nameof`, pattern matching, auto-property initializers, `out var`.
- Every file LF, no BOM.
- No hardcoded user-visible string: XAML uses `x:Uid` with the property that matches the element; C# uses `Loc.Get("Key", "fallback")`. Both `.resw` files get the same key.
- A new `.cs` or `.xaml` under `WhatsappApp` needs its `<Compile Include>` / `<Page>` in `WhatsappApp.csproj`, or `check-project-files.js` fails.
- Runtime text (adapter logs, `Diag` lines) is **English**; the app UI is localized in the `.resw` pair; source comments are English.
- The docs are pairs; `node tools/check-docs.js` must pass.
- Gate before every commit: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node tools/check-fire-and-forget.js && node tools/check-project-files.js && node tools/check-chat-list-source.js`, then `node --test "tools/test/**/*.test.js"` and `cd WhatsappBridge && npm test`.
- Build only ARM (phone) or x86 (emulator). After a build, `git checkout -- WhatsappApp/Package.appxmanifest` and **never** the `.csproj`.
- `WhatsappBridge/` is **not** touched by this plan: no Docker mirror sync is needed.
- Push is the rule: every task ends with `git push origin master`. No apostrophes in commit messages; no emoji in documents except U+26A0.

## Review Focus

- **A run where the app never connects**: the screen must say which address was dialled and what the endpoint file answered, or the next report is as blind as this one. Task 2 owns it.
- **A run where the app connects and the list is still empty**: the screen must show the adapter's own words - the `error` frame text and the `chats.done` count - so "empty" and "refused" are told apart. Task 1 owns the lines, Task 2 owns the count.
- **The app leaves with no exception**: the last lines before the exit must survive in the buffer, so the screen shows what was happening rather than nothing. Task 1 owns it.
- **A `Diag` line that repeats every two seconds**: the buffer must not fill with one line and hide everything else. Task 1 owns it.
- **A string shown on screen**: it must exist in both `.resw` files with the same key, or the page is English on an Italian phone. Task 2 owns it.

---

### Task 1: Diag keeps a history, not just a debugger line

**Files:**
- Modify: `WhatsappApp/Services/Diag.cs`
- Test: none in C#; the guards and the ARM build are the test

**Interfaces:**
- Consumes: nothing.
- Produces: `Diag.History` (an `IList<string>` snapshot, oldest first, at most 200 lines) and `Diag.HistoryText()` (the same lines joined by `"\n"`). Both are safe to read from the UI thread; `Write` is the only writer.

- [ ] **Step 1: Add the buffer to `Diag`**

`Diag` currently keeps a `List<string> Seen` for deduplication and writes one `Debug.WriteLine`. Add a second list, `History`, capped: when it is full the oldest line is removed before the new one is added, so the buffer always holds the most recent 200 lines including the last line written before a crash. The dedupe stays exactly as it is - the `Seen` list is what stops the discovery beacon's per-two-second failure from filling the log, and the buffer must keep that property too.

```csharp
        /// <summary>
        /// How many lines the history keeps. A phone run has no debugger: this
        /// list is the only copy of what the app did, so it holds the most recent
        /// lines and the last line before a crash survives.
        /// </summary>
        private const int HistoryLimit = 200;

        private static readonly List<string> History = new List<string>();

        /// <summary>The lines written so far, oldest first. A copy: the caller may hold it.</summary>
        public static IList<string> HistoryLines()
        {
            lock (Gate)
            {
                return new List<string>(History);
            }
        }

        /// <summary>The whole history as one string, for a TextBlock.</summary>
        public static string HistoryText()
        {
            lock (Gate)
            {
                var text = new StringBuilder();
                for (int i = 0; i < History.Count; i++)
                {
                    text.Append(History[i]);
                    text.Append('\n');
                }
                return text.ToString();
            }
        }
```

In `Write`, inside the existing lock, after `Seen.Add(line)`, add the line to `History` with the cap:

```csharp
            lock (Gate)
            {
                if (Seen.Contains(line)) return;
                Seen.Add(line);

                if (History.Count >= HistoryLimit) History.RemoveAt(0);
                History.Add(line);
            }
            Debug.WriteLine("DIAG " + line);
```

Add `using System.Text;` for the `StringBuilder`.

- [ ] **Step 2: Record the address a connection is about to dial**

An empty chat list with a healthy server is nearly always the wrong address, and today nothing in the app says which one it used. In `WhatsappApp/Services/AutoConnector.cs`, at the top of `TryConnectAsync`, after the two early returns, add one line:

```csharp
            Diag.Ok("connecting: public=" + SettingsService.UsePublicServer
                + " saved=" + SettingsService.ServerAddress + ":" + SettingsService.ServerPort);
```

And in `WhatsappApp/Services/EndpointService.cs`, in `ResolveAsync`, after `fetched` is resolved, record what the endpoint file answered (or that it did not):

```csharp
            if (fetched != null)
            {
                Diag.Ok("endpoint " + fetched.Address + ":" + fetched.Port);
                return fetched;
            }
```

`Diag.Ok` dedupes, so a retry loop adds one line, not one per attempt.

- [ ] **Step 3: Run the gate and the ARM build**

Run the gate from Global Constraints, then the ARM build. Expected: every guard `OK`, the same pass counts as before, and a build ending with `Your package has been successfully created.`

- [ ] **Step 4: Commit and push**

```bash
git add WhatsappApp/Services/Diag.cs WhatsappApp/Services/AutoConnector.cs WhatsappApp/Services/EndpointService.cs
git commit -m "Keep a history of the diagnostics and record the address dialled"
git push origin master
```

---

### Task 2: The phone shows the diagnostics

**Files:**
- Create: `WhatsappApp/Pages/DiagnosticsPage.xaml`, `WhatsappApp/Pages/DiagnosticsPage.xaml.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj` (a `Page` and a `Compile`)
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml` (a button under the status panel)
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml.cs` (its click handler)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw` (same keys)
- Modify: `WhatsappApp/Services/DataService.cs` (`ChatListCompleted` event and a count)
- Modify: `WhatsappApp/Pages/ChatsPage.xaml.cs` (a line when the list completes)
- Test: none in C#; the guards and the ARM build are the test

**Interfaces:**
- Consumes: `Diag.HistoryText()` from Task 1.
- Produces: `DiagnosticsPage`, reached by a button on the settings page, showing the `Diag` history and the connection's own fields (`IsConnected`, `WhatsAppState`, `AccountJid`, `ServerAddress`, `SettingsService.DeviceId`), with a copy button that puts the text on the clipboard. `DataService.ChatListCompleted` is raised with the number of rows in the last list, so the page can say `chats.done: N row(s)`.

- [ ] **Step 1: Raise the row count when a chat list completes**

In `WhatsappApp/Services/DataService.cs`, next to the other events, add:

```csharp
        /// <summary>
        /// A chat list finished arriving, with how many rows it carried. The
        /// adapter sends `chats.done` after the last `chat` frame, so this is the
        /// only place that can say the list was answered and was empty.
        /// </summary>
        public event EventHandler<int> ChatListCompleted;

        private void RaiseChatListCompleted(int rows)
        {
            var handler = ChatListCompleted;
            if (handler != null) handler(this, rows);
        }
```

In the `chats.done` case of `OnControlMessageReceived`, before `RememberChatList()`, record the count of rows in this batch and raise it. The batch is `_freshChatRows`, which `BeginChatList` cleared at the start of the list:

```csharp
                case "chats.done":
                    // The count before the batch is consumed: RememberChatList
                    // clears it, and the number of rows the server answered is
                    // exactly what tells an empty list from a refused one.
                    int rows = _freshChatRows.Count;
                    RememberChatList();
                    Diag.Ok("chats.done: " + rows + " row(s)");
                    RaiseChatListCompleted(rows);
                    break;
```

In `WhatsappApp/Pages/ChatsPage.xaml.cs`, subscribe and unsubscribe in `OnNavigatedTo`/`OnNavigatedFrom` like the other events, and write the count where the empty state is decided:

```csharp
        private void OnChatListCompleted(object sender, int rows)
        {
            Diag.Ok("chat list: " + rows + " row(s), list shows "
                + DataService.Instance.Contacts.Count);
        }
```

- [ ] **Step 2: Create the page**

Create `WhatsappApp/Pages/DiagnosticsPage.xaml`: the same header shape as `ConnectionPage` (a 48 px back button with the `IconBack` `Path` inlined, a title), then a `ScrollViewer` holding a `TextBlock` bound to nothing (filled in code) with a small monospace-ish font, `TextWrapping="Wrap"`, and two buttons: `CopyButton` and `ClearButton`. Every visible string comes from `x:Uid`.

Create `WhatsappApp/Pages/DiagnosticsPage.xaml.cs`:

```csharp
    /// <summary>
    /// What the app did, on the phone.
    ///
    /// Why it exists: Diag writes to Debug.WriteLine, which needs a PC with a
    /// debugger attached - and the run being diagnosed is the one on the phone.
    /// This page shows the same history, plus the connection fields that decide
    /// whether the adapter is reachable at all, so a report is a screenshot
    /// instead of a pasted assembly list.
    /// </summary>
    public sealed partial class DiagnosticsPage : Page
    {
        public DiagnosticsPage()
        {
            this.InitializeComponent();
            ToolTipService.SetToolTip(BackButton, Loc.Get("ChatPage_BackTooltip", "Back"));
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Refresh();
        }

        private void Refresh()
        {
            var comm = CommunicationService.Instance;
            var text = new StringBuilder();

            text.Append("connected: ").Append(comm.IsConnected).Append('\n');
            text.Append("whatsapp: ").Append(comm.WhatsAppState).Append('\n');
            text.Append("account: ").Append(comm.AccountJid).Append('\n');
            text.Append("server: ").Append(comm.ServerAddress).Append('\n');
            text.Append("device: ").Append(SettingsService.DeviceId).Append('\n');
            text.Append("chats: ").Append(DataService.Instance.Contacts.Count).Append('\n');
            text.Append("---\n");
            text.Append(Diag.HistoryText());

            DiagnosticsText.Text = text.ToString();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            var package = new DataPackage();
            package.SetText(DiagnosticsText.Text ?? "");
            Clipboard.SetContent(package);
            Diag.Ok("diagnostics copied");
            Refresh();
        }
    }
```

`Refresh` is called on navigation and after a copy, not on a timer: the page is opened when something is already wrong.

- [ ] **Step 3: Register the page and open it from the settings**

In `WhatsappApp/WhatsappApp.csproj`, add both entries, in the same alphabetical position as the other pages:

```xml
    <Compile Include="Pages\DiagnosticsPage.xaml.cs">
      <DependentUpon>DiagnosticsPage.xaml</DependentUpon>
    </Compile>
```

and

```xml
    <Page Include="Pages\DiagnosticsPage.xaml">
      <Generator>MSBuild:Compile</Generator>
      <SubType>Designer</SubType>
    </Page>
```

In `WhatsappApp/Pages/ConnectionPage.xaml`, under the `StatusPanel` `Border`, add a button that is always visible (the settings page is the one screen a stuck user can reach):

```xml
                <Button x:Name="DiagnosticsButton" x:Uid="ConnectionPage_Diagnostics"
                        Content="Show diagnostics"
                        Background="#FFE0E0E0" Foreground="#FF075E54" FontSize="14"
                        Height="40" BorderThickness="0" Margin="0,8,0,0"
                        Click="DiagnosticsButton_Click"/>
```

In `WhatsappApp/Pages/ConnectionPage.xaml.cs`:

```csharp
        private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(DiagnosticsPage));
        }
```

- [ ] **Step 4: Add the strings to both languages**

Add to `WhatsappApp/Strings/en-US/Resources.resw` and `WhatsappApp/Strings/it-IT/Resources.resw`, with the same keys:

- `ConnectionPage_Diagnostics.Content` - "Show diagnostics" / "Mostra diagnostica"
- `DiagnosticsPage_Title.Text` - "Diagnostics" / "Diagnostica"
- `DiagnosticsPage_Copy.Content` - "Copy" / "Copia"
- `DiagnosticsPage_Clear.Content` - "Clear" / "Pulisci"
- `DiagnosticsPage_Hint.Text` - "What the app did, and where it tried to connect." / "Cosa ha fatto l app, e dove ha provato a collegarsi."

`ClearButton` calls a new `Diag.Clear()` that empties the history and the `Seen` set and returns to the same page (add `Clear()` to `Diag` in this task, next to `HistoryLines`).

- [ ] **Step 5: Run the gate and the ARM build**

Run the gate and the ARM build. Expected: every guard `OK` (`check-resw.js --strict` proves both `.resw` files carry every new key, `check-project-files.js` proves the two new files are in the `.csproj`), and a build ending with `Your package has been successfully created.`

- [ ] **Step 6: Commit and push**

```bash
git add WhatsappApp/Pages/DiagnosticsPage.xaml WhatsappApp/Pages/DiagnosticsPage.xaml.cs WhatsappApp/WhatsappApp.csproj WhatsappApp/Pages/ConnectionPage.xaml WhatsappApp/Pages/ConnectionPage.xaml.cs WhatsappApp/Services/DataService.cs WhatsappApp/Pages/ChatsPage.xaml.cs WhatsappApp/Services/Diag.cs WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw
git commit -m "Show the diagnostics on the phone instead of only in a debugger"
git push origin master
```

---

## Self-Review

**1. Spec coverage.** "Le chat non caricano con lo stesso result di prima" is answered by making the app say what it tried and what came back: Task 1 records the address and the endpoint answer, Task 2 shows the history, the connection fields and the row count. "L'app crasha" is answered by the same screen: the last lines before the exit stay in the buffer, so the crash has a name on the phone. "Aggiungi linee debug per vedere il motivo" is the whole plan. The adapter needed no change - its log already proves it is healthy, and that is itself the finding.

**2. Step scan.** Each step is one action with one checkable result: add the buffer, add two log lines, gate; raise the count, create the page, register it, add the strings, gate. No step carries a second decision.

**3. Type consistency.** `Diag.HistoryText()` returns `string` and is what the page assigns to `TextBlock.Text`. `DataService.ChatListCompleted` is `EventHandler<int>`, matching `RaiseChatListCompleted(int rows)`; the handler signature in `ChatsPage` is `(object sender, int rows)`. `DiagnosticsPage` uses `Frame.GoBack()`/`Frame.Navigate` the way `ConnectionPage` already does.

**4. Review Focus.** Each line names its owner: the never-connected run and the empty-but-answered run are Task 1's address and endpoint lines plus Task 2's row count; the silent exit is Task 1's capped buffer; the repeating line is Task 1's dedupe and cap; the localized string is Task 2 Step 4 with `check-resw.js --strict` behind it.

**5. Proportion.** The plan adds one small page, one buffer and three log lines, and prints only the fragments whose exact text is the change. It deliberately fixes no guessed bug: with a healthy adapter and a silent app, a guess is what the last three changes were, and the screen is what turns the next report into evidence.

## Why the obvious fixes were not taken

- **The 29 connections are not the phone.** They are 60 seconds apart, and none of them handshakes: that is a port scanner or a monitor hitting the public bore port, not a client. `bore.pub:41417` is reachable from the internet by design, so this is expected noise and not a symptom.
- **`GOWA not reachable` is gone from the new startup log**, and the adapter binds `24f43937`, the `logged_in` device, with `WhatsApp connected as 393892672185@s.whatsapp.net`. The startup race fix is in force on the NAS.
- **The endpoint file is correct**: `https://raw.githubusercontent.com/vincenzosco/whatsappforwp-endpoint/main/endpoint.json` answers `host: bore.pub`, `port: 41417`, matching the tunnel. So the address the app reads is right, which is why the app must be the one that reports what it did with it.
- **The process exiting with code 0 and no exception** is the signature of an activation that quits, not of a thrown fault: `App.OnUnhandled` would have written a `DIAG` line first, and the pasted log has none. That is exactly why the buffer has to be readable on the phone.

## What execution changed about this plan

- **The clipboard is not on Windows Phone 8.1.** The plan asked for a `CopyButton` using `Windows.ApplicationModel.DataTransfer.DataPackage` and `Clipboard.SetContent`, the way a UWP app copies text. That namespace is not part of the WP8.1 Runtime surface - it belongs to the Silverlight `System.Windows` world, which this project does not reference - and the ARM build failed with `CS0103: The name 'Clipboard' does not exist in the current context`. The button was dropped and the `DiagnosticsText` is now a read-only `TextBox` with `AcceptsReturn`, which gets the platform's own select-and-copy menu on long press. The `DiagnosticsPage_Copy.Content` key was removed from both `.resw` files with it, and the hint string now tells the user to long press.
- **A `TextBlock` title with `Text=""` is invisible to `check-resw.js --strict`.** The guard only counts an `x:Uid` as used when the element also carries a non-empty literal for the same property, so the page title was first reported as a never-used key. The title now carries the English literal like `CallsPage_Title` does, and the guard resolves it.
- **The `DiagnosticsButton` sits outside the `StatusPanel`.** The plan said "under the `StatusPanel`"; the panel is `Collapsed` until something goes wrong, and the point of the button is to be reachable in exactly that state, so it is a sibling of the panel, not a child.
- **Everything else matched the plan.** The gate passed with 50 C# files, 159 resource keys in both languages and 27 buttons; the ARM build ended with `Your package has been successfully created.` and `Package.appxmanifest` was restored afterwards.

## What the follow-ups added

Three follow-ups were requested after the page shipped, and two of them are code:

- **The empty state opens the diagnostics itself.** `ChatsPage` now carries a `DiagnosticsButton` (`ChatsPage_Diagnostics.Content`) inside `EmptyStatePanel`, wired to `DiagnosticsButton_Click`. The plan had put the only entrance on the settings page, which is one screen away from the exact moment the answer is wanted.
- **Every control frame is logged, both directions.** `Diag.Frame(direction, command, detail)` writes `in`/`out` lines in arrival order and **does not dedupe** - the point of a frame log is the repetition the `Seen` set exists to hide, so `Frame` appends to `History` directly. The cap still holds, and `media.chunk` is dropped because one transfer is a hundred identical lines. The payload is clipped at 48 characters, because a login QR and a media blob both travel in `Text` and neither belongs in the buffer whole. `DispatchMessage` logs the inbound system frames, and the single `SendMessageAsync` path logs the outbound ones, so a new control frame cannot be added without being seen.
- **The third follow-up - read the diagnostics the user pastes and name the fault - cannot run until the text arrives.** The build is on the phone, so the next message with the copied text is the one that turns the buffer into a diagnosis.
