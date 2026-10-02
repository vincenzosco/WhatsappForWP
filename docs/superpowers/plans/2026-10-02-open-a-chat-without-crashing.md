# Opening a Conversation Without Crashing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a chat show its old messages and stop the crash that follows, by naming the fatal exception instead of guessing at it, and by binding the conversation list once, after the history burst is over.

**Architecture:** Three changes on the phone and one on the wire. First the app reports its own unhandled exception, because today a UI-thread crash has no handler and dies before writing a `DIAG` line, which is why the log stops at the assembly list. Then the conversation says what it bound and how many history rows it inserted, so the next run tells the two failures apart. Then the adapter marks the end of a history burst with one `history.done` frame and the page binds the list when that arrives, instead of binding it while fifty one-by-one inserts are still hitting a list that is already watching the collection.

**Tech Stack:** C# 5 / WinRT XAML on Windows Phone 8.1; Node.js 18+ with `node:test` in `WhatsappBridge`.

**Spec:** the phone run of 2026-10-02 - "adesso si entra nella chat, ma non compaiono le vecchie chat e l'app crasha, e dopo un pò pure windows" - and the log the user pasted. The log ends at `SYSTEM.RUNTIME.EXTENSIONS.NI.DLL` with no exception and no `DIAG` line: the crash is *not in it*, which is the first thing this plan fixes. The previous run's evidence (`first chance System.Exception` in `SYSTEM.NI.DLL`, no `DIAG`) is the same shape.

## Global Constraints

- C# 5: no `await` inside a `catch` or `finally` (CS1985).
- Every file LF, no BOM; normalize with `perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' <files>`.
- Gate before every commit (eleven guards): `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node tools/check-fire-and-forget.js && node tools/check-project-files.js && node tools/check-chat-list-source.js`, then `node --test "tools/test/**/*.test.js"`, then `cd WhatsappBridge && npm test`.
- There is no C# test harness: a C# task's tests are the guards, the ARM build, and the phone run. The adapter has real tests, in `WhatsappBridge/test`.
- Build only ARM (phone) or x86 (emulator), never Any CPU. After a build, `git checkout -- WhatsappApp/Package.appxmanifest` and **never** the `.csproj`.
- A commit that touches `WhatsappBridge/` is not finished until the Docker mirror is synced (`node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP`, `--check` until `OK: server/ matches the adapter (28 file(s))`, `cd server && npm test`, commit, `git push origin main`).
- Push is the rule, not a question: every task ends with `git push origin master`. No apostrophes in commit messages; no emoji in documents except U+26A0.
- `Diag.Failed` and `Diag.Ok` are for a condition the app survives; a line that names a site is what the next phone run has to read.

## Review Focus

- A fatal exception on the UI thread while a chat renders: the log names it as `DIAG App/unhandled: <type> 0x<code> <message>` instead of stopping at the assembly list. Task 1 owns it.
- The same crash in release, where the handler must not keep a half-dead UI alive: the handler survives the exception only under `#if DEBUG`. Task 1 Step 2 owns it.
- A history burst of one frame per message, which is up to `MESSAGES_LIMIT` (default 50, capped at 60 by the adapter's config test): the list is bound once, after the burst, not once per insert. Task 3 owns it.
- An adapter that predates `history.done`: the page still binds, on its own timeout, so an old server does not leave the chat empty forever. Task 3 Step 5 owns it.
- A chat whose history arrives after the saved copy is already on screen: no second bind, no duplicated rows. Task 3 Step 6 owns it.

---

### Task 1: The app reports its own unhandled exception

**Files:**
- Modify: `WhatsappApp/App.xaml.cs` (the constructor and a new pair of handlers)
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (a new invariant)
- Test: none in C#; the guards, the ARM build and the phone run are the test

**Interfaces:**
- Consumes: `Diag.Failed(string, Exception)`.
- Produces: `App.OnUnhandled(object, UnhandledExceptionEventArgs)` and `App.OnUnobservedTask(object, UnobservedTaskExceptionEventArgs)`, both subscribed in the constructor.

- [ ] **Step 1: Subscribe in the constructor**

In `WhatsappApp/App.xaml.cs`, after `this.Resuming += this.OnResuming;` add:

```csharp
            // A fatal exception on the UI thread used to kill the process before
            // any Diag line was written: the log stopped at the assembly list and
            // the crash had no name. These two handlers put the type, the HRESULT
            // and the message in the log before that happens.
            this.UnhandledException += this.OnUnhandled;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += this.OnUnobservedTask;
```

- [ ] **Step 2: Add the two handlers**

Add them next to `OnSuspending`:

```csharp
        /// <summary>
        /// An exception nobody caught. It is written down first; a debug build
        /// then survives it, because the run that is being diagnosed is worth more
        /// alive than dead. A release build still goes down: an app that lost its
        /// UI is worse than an app that closes.
        /// </summary>
        private void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            Diag.Failed("App/unhandled", e.Exception);
#if DEBUG
            e.Handled = true;
#endif
        }

        /// <summary>
        /// A Task whose fault nobody read. The phone used to die with no line at
        /// all, which is the same blind spot one task lower.
        /// </summary>
        private void OnUnobservedTask(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            Diag.Failed("App/unobserved-task", e.Exception);
            e.SetObserved();
        }
```

- [ ] **Step 3: Record the invariant**

In `.agents/skills/maintain-the-app/SKILL.md`, add one bullet: the app logs its own unhandled exception, because a UI-thread exception with no handler dies before any `Diag` line and a log that stops at the assembly list names nothing.

- [ ] **Step 4: Gate and build**

Run the gate and the ARM build from Global Constraints. Expected: every guard `OK`, 85 tool tests, `# pass 204` in the adapter, and a build ending with `Your package has been successfully created.` plus `WhatsappServer.exe`.

- [ ] **Step 5: Commit and push**

```bash
git add WhatsappApp/App.xaml.cs .agents/skills/maintain-the-app/SKILL.md
git commit -m "Write the unhandled exception down before the app dies"
git push origin master
```

---

### Task 2: The conversation says what it bound and what it inserted

**Files:**
- Modify: `WhatsappApp/Services/ConversationView.cs` (`Bind`)
- Modify: `WhatsappApp/Services/DataService.cs` (`AddHistoryMessage`, `LoadCachedMessagesAsync`)
- Test: none in C#; the guards and the phone run are the test

**Interfaces:**
- Consumes: `Diag.Ok(string)`.
- Produces: the log lines `ok: conversation bound N message(s)`, `ok: cache restored N message(s)` and `ok: history arrived for <chatId>`, which separate "the history never arrived" from "the history arrived and the list did not show it".

- [ ] **Step 1: Log the bind**

In `ConversationView.Bind`, after `_list.ItemsSource = messages;` add:

```csharp
            Diag.Ok("conversation bound " + (messages == null ? 0 : messages.Count) + " message(s)");
```

- [ ] **Step 2: Log the cache fill**

In `DataService.LoadCachedMessagesAsync`, after the loop, add:

```csharp
            Diag.Ok("cache restored " + cached.Count + " message(s)");
```

- [ ] **Step 3: Log the arrival of a history burst once per chat**

`AddHistoryMessage` runs once per row and is also the cache path, so the line belongs one level up, in the `IsHistory` branch of `OnNetworkMessageReceived`. Add a field next to `_historyRequested`: `private readonly HashSet<string> _historyArrived = new HashSet<string>();`, and in that branch, before `AddHistoryMessage(message)`, log `Diag.Ok("history arrived for " + message.ChatId)` only when `_historyArrived.Add(message.ChatId)` is true. One line per chat is enough to tell the two failures apart; the total comes with `history.done` in Task 3.

- [ ] **Step 4: Gate and build**

Run the gate and the ARM build. Expected: every guard `OK` and the same pass counts.

- [ ] **Step 5: Commit and push**

```bash
git add WhatsappApp/Services/ConversationView.cs WhatsappApp/Services/DataService.cs
git commit -m "Say in the log what the conversation bound and inserted"
git push origin master
```

---

### Task 3: One history burst, one bind

**Files:**
- Modify: `WhatsappBridge/server.js` (`sendMessages`, around line 306)
- Modify: `WhatsappBridge/test/server.test.js` (the test at line 512)
- Modify: `WhatsappApp/Services/DataService.cs` (`OnControlMessageReceived`, a new event)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (`OnNavigatedTo`, `BindAfterCacheAsync`)
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (the `A cache is a photograph` invariant)
- Test: `WhatsappBridge/test/server.test.js`

**Interfaces:**
- Produces (adapter): one extra control frame `{ command: 'history.done', chatId }`, sent after the last history frame of that chat.
- Produces (phone): `DataService.HistoryCompleted : event EventHandler<string>`, raised on that command with the chat id; `ChatPage.BindWhenHistorySettlesAsync(string chatId) -> Task`, replacing the direct `_view.Bind` inside `BindAfterCacheAsync`.
- Consumes: `ConversationView.Bind`, `DataService.MarkHistoryRequested(string) -> bool`.

- [ ] **Step 1: Write the failing adapter test**

In `WhatsappBridge/test/server.test.js`, in the test at line 512, change the two assertions that count frames and add the last frame:

```js
  assert.strictEqual(frames.length, 3);
```

and after the last existing assertion:

```js
  assert.strictEqual(frames[2].Command, 'history.done');
  assert.strictEqual(frames[2].Text, 'a@s.whatsapp.net');
  assert.strictEqual(frames[2].IsHistory, undefined);
```

- [ ] **Step 2: Run it to watch it fail**

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: FAIL, `3 !== 2` on the frame count.

- [ ] **Step 3: Send the closing frame**

In `WhatsappBridge/server.js`, in `sendMessages`, after the `for` loop and before the `logger` line, add:

```js
      // The burst has an end on the wire, so the app can bind the list once,
      // after it, instead of once per frame: a bound ListView re-lays out on
      // every insert, and fifty inserts inside one burst is what left the phone
      // unresponsive. Text carries the chat, like the other control frames.
      sendControl(session, { command: 'history.done', chatId });
```

- [ ] **Step 4: Run the adapter tests**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 204 tests (the assertion count changed, not the test count), 0 fail. Run the gate too.

- [ ] **Step 5: Raise the event on the phone**

In `DataService.OnControlMessageReceived`, add the case:

```csharp
                case "history.done":
                    RaiseHistoryCompleted(message.Text);
                    break;
```

with `public event EventHandler<string> HistoryCompleted;` next to the other events and `RaiseHistoryCompleted` following the pattern of `RaiseTypingChanged` (copy the handler to a local, then invoke).

- [ ] **Step 6: Bind once, when the burst is over**

In `ChatPage.OnNavigatedTo`, remember whether a history request went out for this chat:

```csharp
                bool historyRequested = CommunicationService.Instance.IsConnected
                    && DataService.Instance.MarkHistoryRequested(contact.Id);
```

Pass it to the load, and keep the request block as it is but reuse the flag:

```csharp
                Guarded.RunGuardedAsync("ChatPage/cached messages",
                    BindAfterCacheAsync(contact.Id, historyRequested));
```

In `BindAfterCacheAsync`, bind immediately when `historyRequested` is false, and otherwise wait for `HistoryCompleted` for this chat **or** 2000 ms, whichever comes first, then bind once. Unsubscribe both the event and the timeout in a `finally`, and ignore an event for another chat. A burst that never closes (an adapter without the frame) therefore still binds, two seconds late, and the chat is never left empty.

- [ ] **Step 7: Gate, tests and build**

Run the full gate, the tool tests and the adapter tests (204), then the ARM build. Expected: every guard `OK` and a package created.

- [ ] **Step 8: Sync the mirror**

Run the mirror sequence from Global Constraints: sync, `--check` until `OK: server/ matches the adapter (28 file(s))`, `cd server && npm test` (204 tests).

- [ ] **Step 9: Record the rule and commit both repositories**

In `.agents/skills/maintain-the-app/SKILL.md`, in the `A cache is a photograph, not a truth` bullet, add: the history arrives as a burst whose end is the adapter's `history.done`, and the page binds the list once, after it (or after its own timeout), because a bind while the burst is still inserting re-lays out the list on every row.

```bash
git add WhatsappBridge/server.js WhatsappBridge/test/server.test.js WhatsappApp/Services/DataService.cs WhatsappApp/Pages/ChatPage.xaml.cs .agents/skills/maintain-the-app/SKILL.md
git commit -m "Bind the conversation once, after the history burst"
git push origin master
```

Then, in the mirror, commit the synced `server/` files and `git push origin main`.

---

## Self-Review

**1. Spec coverage.** The report has three facts. The crash is Task 1: the log cannot name it because `App` never handled an unhandled exception. "The old messages do not appear" is Task 2 (a line that says whether the history arrived) plus Task 3 (the list binds when the burst closes, so what arrived is on screen). "And after a while even Windows" is the memory cost of fifty inserts into a list that is already watching the collection, which Task 3 removes.

**2. Step scan.** Each step is one action with one checkable result: subscribe, add two handlers, add a `Diag` line, change one assertion, run it, send one frame, add a case, add a wait with its timeout. The largest step (Task 3 Step 6) is one method's new shape, and its interface is fixed by the Interfaces block.

**3. Type consistency.** `App.OnUnhandled(object, UnhandledExceptionEventArgs)` and `App.OnUnobservedTask(object, UnobservedTaskExceptionEventArgs)` match the two event signatures. `HistoryCompleted` is `EventHandler<string>` and is raised with `message.Text`, the same field the `messages` request puts the chat id in. `BindAfterCacheAsync(string, bool) -> Task` is called once, from `OnNavigatedTo`.

**4. Review Focus.** Each line names its owner step: the named crash (Task 1 Step 2), the release behaviour (Task 1 Step 2), the burst size (Task 3 Step 3), the adapter without the frame (Task 3 Step 6), and the copy already on screen (Task 3 Step 6).

**5. Proportion.** The plan is shorter than the code it changes and prints only the fragments whose exact text is the fix. Task 1 and Task 2 exist because the evidence is missing, and they are the cheapest way to get it.

## What the phone run should now show

1. If it still crashes, the log carries one `DIAG App/unhandled: <type> 0x<code> <message>` line, and that line names the site.
2. The conversation logs `ok: conversation bound N message(s)` and `ok: history inserted N message(s)`; if the second line is absent while the first is there, the history never arrived and the fault is on the wire, not in the list.
3. Opening a chat does not make the phone unresponsive: the list is laid out once, on a burst that has ended.
