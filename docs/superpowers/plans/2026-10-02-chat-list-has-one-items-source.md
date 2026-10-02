# The Conversation List Has One Items Source Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a chat show its messages again by leaving `MessagesListView.ItemsSource` to the code that binds the saved copy, and keep a second writer from coming back.

**Architecture:** Three independent changes on the phone. The first removes the `ItemsSource="{Binding}"` the conversation list still declares in XAML: `ConversationView.Bind` is the single writer, and a XAML binding on the same property is resolved against `DataContext` (a `Contact`, not a collection), which fights the code-set source and leaves the list empty. The second freezes that rule in a guard. The third stops logging the absent first-open copy as a failure, so the next phone log says what actually broke instead of naming the one expected condition.

**Tech Stack:** C# 5 / WinRT XAML on Windows Phone 8.1; Node.js 18+ with `node:test` in `tools/`.

**Spec:** the phone log of 2026-10-02 (opening a chat shows it empty; the phone no longer restarts; `DIAG MessageCache.Load: FileNotFoundException 0x80070002`; about a dozen first chance `System.Exception` in `SYSTEM.NI.DLL`; no `DIAG ChatPage/...` line at all). There is no separate spec document; the evidence is that log, `WhatsappApp/Pages/ChatPage.xaml` and `WhatsappApp/Services/ConversationView.cs`. The earlier plan `2026-10-02-chat-opens-with-its-copy-and-no-status-row.md` is what claimed `MessagesListView.ItemsSource` was assigned "there and nowhere else" - the XAML binding was missed.

## Global Constraints

- C# 5: no `await` inside a `catch` or `finally` (CS1985).
- Every file LF, no BOM; normalize with `perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' <files>`. `Package.appxmanifest` is CRLF+BOM and is left alone.
- There is no C# test harness in this repository. The tests of a C# task are the guard scripts, the ARM build, and the phone run. The guards have real tests, in `tools/test`, run by `node --test "tools/test/**/*.test.js"`.
- Gate before every commit (eleven guards after this plan): `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node tools/check-fire-and-forget.js && node tools/check-project-files.js && node tools/check-chat-list-source.js`, then `node --test "tools/test/**/*.test.js"`, then `cd WhatsappBridge && npm test`.
- Every failure the app decides to survive logs through `Diag.Failed(site, ex)`; a condition we expect is not a failure and does not.
- Build only ARM (phone) or x86 (emulator), never Any CPU. After a build, `git checkout -- WhatsappApp/Package.appxmanifest` and **never** the `.csproj` (the build runs on a copy on the VM, so the checked-out `.csproj` would drop the `Compile Include` entries).
- Push is the rule, not a question: every task ends with `git push origin master`. A commit that touches `WhatsappBridge/` is not finished until the Docker mirror is synced and `git push origin main` there too (this plan touches none).
- No apostrophes in commit messages; no emoji in documents except the warning sign U+26A0.

## Review Focus

- Opening a chat whose messages are already in memory: they are on screen, and the debugger prints no run of first chance `System.Exception` in `SYSTEM.NI.DLL`. Task 1 Step 3 owns it.
- Opening a chat with a saved copy on the phone: the copy is on screen. Task 1 Step 3 owns it.
- Opening a chat with neither a copy nor history: an empty list, no phantom row, no error line. Task 1 Step 3 owns it.
- The header of an open chat: the name and the avatar still come from `DataContext` (a `Contact`), even though the list no longer binds to it. Task 1 Step 4 owns it.
- The first open of a chat whose file is not on disk: the log carries no `MessageCache.Load` line, because an absent copy is not a failure. Task 3 Step 2 owns it.

---

### Task 1: The conversation list has one items source

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml` (the `MessagesListView` element, around line 156)
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (the `The conversation's view state is one module` invariant)
- Test: none in C#; the guards, the ARM build and the phone run are the test (see Global Constraints)

**Interfaces:**
- Consumes: `ConversationView.Bind(ObservableCollection<ChatMessage>)` (the only code writer), called from `ChatPage.BindAfterCacheAsync`.
- Produces: `MessagesListView` no longer declares `ItemsSource` in XAML, so the item source has exactly one writer.

- [ ] **Step 1: Read the two writers back**

Open `WhatsappApp/Pages/ChatPage.xaml` and `WhatsappApp/Services/ConversationView.cs`. Confirm (a) `MessagesListView` still declares `ItemsSource="{Binding}"` in its start tag, and (b) the only code assignment to `ItemsSource` is `_list.ItemsSource = messages;` inside `ConversationView.Bind`. If the XAML attribute is already gone, stop and report instead of changing the plan.

- [ ] **Step 2: Remove the XAML writer**

In `WhatsappApp/Pages/ChatPage.xaml`, in the `MessagesListView` start tag, delete this one line:

```xml
                  ItemsSource="{Binding}"
```

The tag keeps everything else unchanged, starting `Grid.Row="1"` and ending `SelectionMode="None">`.

- [ ] **Step 3: Check the three ways into the list by hand**

Reason through, in a comment or in your report, because no test covers them:

1. A chat with messages in memory: `BindAfterCacheAsync` calls `ConversationView.Bind(_messages)` with no XAML binding left to re-apply, so the rows appear and no binding to a `Contact` is ever used as an items source.
2. A chat with a saved copy: the copy is inserted into `_messages` before `Bind`, then the list binds to it.
3. A chat with neither: `_messages` is empty, the list binds empty, and no row appears.

- [ ] **Step 4: Confirm the header still binds to the contact**

In the same file, confirm the header still uses `{Binding Initials}`, `{Binding Avatar}` and `{Binding HasAvatar}` against `DataContext = contact` (set in `ChatPage.OnNavigatedTo`). Removing the list's `{Binding}` must not have touched them.

- [ ] **Step 5: Record the rule as an invariant**

In `.agents/skills/maintain-the-app/SKILL.md`, at the end of the `The conversation's view state is one module` bullet, add one sentence: the conversation `ListView` declares no `ItemsSource` in XAML - a binding there is resolved against `DataContext` (a `Contact`, not a collection) and fights the one writer, `ConversationView.Bind`, which is why the list went empty with the same `SYSTEM.NI.DLL` exception run and no `DIAG` line.

- [ ] **Step 6: Gate and build**

Run the gate from Global Constraints (with `check-chat-list-source.js` not yet existing, run it **without** that last clause for this task) and the ARM build. Expected: every guard `OK`, `node --test` pass count unchanged, `# pass 204` in the adapter, and a build ending with `Your package has been successfully created.` plus `WhatsappServer.exe`.

- [ ] **Step 7: Commit and push**

```bash
git add WhatsappApp/Pages/ChatPage.xaml .agents/skills/maintain-the-app/SKILL.md
git commit -m "Leave the chat list one source to bind from"
git push origin master
```

---

### Task 2: A guard keeps the list to one source

**Files:**
- Create: `tools/check-chat-list-source.js`
- Create: `tools/test/check-chat-list-source.test.js`
- Modify: `README.md` and `README.it.md` (the fast gate block, around lines 231 and 237)
- Modify: `.agents/skills/test-the-app/SKILL.md` (the guard table and the tool-test count on line 26)
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (the gate command in the workflow section, around line 195)
- Test: `tools/test/check-chat-list-source.test.js`

**Interfaces:**
- Produces: `problemsFor(xamlText) -> string[]` exported from `tools/check-chat-list-source.js`; an empty array means the conversation list has no XAML item source. Run as a script, it reads `WhatsappApp/Pages/ChatPage.xaml` and exits 1 on any problem.

- [ ] **Step 1: Write the failing test**

Create `tools/test/check-chat-list-source.test.js`:

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');
const fs = require('node:fs');

const guard = require('../check-chat-list-source');

test('un ItemsSource sul ListView della conversazione e un problema', () => {
  const xaml = '<ListView x:Name="MessagesListView" Grid.Row="1"\n'
    + '          ItemsSource="{Binding}"\n'
    + '          SelectionMode="None">';
  const problems = guard.problemsFor(xaml);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /MessagesListView/);
});

test('lo stesso ListView senza ItemsSource non e un problema', () => {
  const xaml = '<ListView x:Name="MessagesListView" Grid.Row="1"\n'
    + '          SelectionMode="None">';
  assert.deepStrictEqual(guard.problemsFor(xaml), []);
});

test('un altro ListView con ItemsSource non e un problema', () => {
  const xaml = '<ListView x:Name="OtherList" ItemsSource="{Binding}">';
  assert.deepStrictEqual(guard.problemsFor(xaml), []);
});

test('il ChatPage.xaml del progetto ha una sola sorgente', () => {
  const file = path.join(__dirname, '..', '..', 'WhatsappApp', 'Pages', 'ChatPage.xaml');
  assert.deepStrictEqual(guard.problemsFor(fs.readFileSync(file, 'utf8')), []);
});
```

- [ ] **Step 2: Run it to watch it fail**

Run: `node --test tools/test/check-chat-list-source.test.js`
Expected: FAIL, because `../check-chat-list-source` does not exist.

- [ ] **Step 3: Write the guard**

Create `tools/check-chat-list-source.js`, modelled on `tools/check-project-files.js` (header comment, `main`, and `module.exports`). It must export:

```js
/** I problemi di un testo XAML: vuoto quando la conversazione ha una sola sorgente. */
function problemsFor(xaml) {
  const problems = [];
  const tag = xaml.match(/<ListView\b[^>]*x:Name="MessagesListView"[^>]*>/);
  if (tag && /ItemsSource\s*=/.test(tag[0])) {
    problems.push('WhatsappApp/Pages/ChatPage.xaml: MessagesListView sets ItemsSource in XAML, '
      + 'but ConversationView.Bind is the one writer: a binding there is resolved against '
      + 'DataContext (a Contact, not a collection) and fights the code-set source, so the '
      + 'conversation opens empty with a run of SYSTEM.NI.DLL exceptions and no DIAG line');
  }
  return problems;
}
```

`main()` reads `WhatsappApp/Pages/ChatPage.xaml`, prints the problems and exits 1 if any, else prints `OK: the conversation list is bound from code only.`.

- [ ] **Step 4: Run it to watch it pass**

Run: `node --test tools/test/check-chat-list-source.test.js`
Expected: PASS, 4 tests, 0 fail.

- [ ] **Step 5: Put it in the gate and the docs**

Add `&& node tools/check-chat-list-source.js` after `check-project-files.js` in the fast gate block of `README.md` and `README.it.md` (both, same commit), in the workflow gate of `.agents/skills/maintain-the-app/SKILL.md`, and as a new `node tools/check-chat-list-source.js  # ...` line plus a table row in `.agents/skills/test-the-app/SKILL.md`. Update the tool-test count in the same skill's comment (line 26) from `(81)` to the new total.

- [ ] **Step 6: Gate**

Run the full eleven-guard gate and the tool tests. Expected: every guard `OK` including `check-chat-list-source.js`, and the new pass count.

- [ ] **Step 7: Commit and push**

```bash
git add tools/check-chat-list-source.js tools/test/check-chat-list-source.test.js README.md README.it.md .agents/skills/test-the-app/SKILL.md .agents/skills/maintain-the-app/SKILL.md
git commit -m "Guard the conversation list against a second items source"
git push origin master
```

---

### Task 3: The absent copy is not a failure

**Files:**
- Modify: `WhatsappApp/Services/MessageCache.cs` (`LoadAsync`)
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (the `A cache is a photograph, not a truth` invariant)
- Test: none in C#; the guards and the phone log are the test

**Interfaces:**
- Consumes: `Diag.Failed(string, Exception)` and nothing else new.
- Produces: `MessageCache.LoadAsync` logs no failure when the chat has no file yet.

- [ ] **Step 1: Split the two catches**

In `WhatsappApp/Services/MessageCache.cs`, in `LoadAsync`, the single `catch (Exception ex)` around the read currently catches both "there is no file yet" and "the file is broken". Replace it with two, in this order:

```csharp
            catch (FileNotFoundException)
            {
                // First open: there is no copy of this chat on the phone. That is
                // the normal state and not a failure, so it is not logged: a
                // FileNotFoundException line in the log hid the real fault.
            }
            catch (Exception ex)
            {
                // A cache written by a different version.
                Diag.Failed("MessageCache.Load", ex);
            }
```

Keep the method's `return empty;` after the try/catch unchanged.

- [ ] **Step 2: Check the log expectation by hand**

Read the new `LoadAsync` and confirm: `GetFileAsync` on a missing file throws `FileNotFoundException` (HRESULT 0x80070002), which now returns `empty` with no `DIAG` line; a malformed file still logs `MessageCache.Load`.

- [ ] **Step 3: Record the rule**

In `.agents/skills/maintain-the-app/SKILL.md`, in the `A cache is a photograph, not a truth` bullet, add one clause: the first open has no file and that is not a failure, so `MessageCache.LoadAsync` catches `FileNotFoundException` without a `Diag` line and logs only a cache it could not parse.

- [ ] **Step 4: Gate and build**

Run the full gate and the ARM build (see Global Constraints). Expected: every guard `OK`, the adapter at `# pass 204`, and the build ending with the package created plus `WhatsappServer.exe`.

- [ ] **Step 5: Commit and push**

```bash
git add WhatsappApp/Services/MessageCache.cs .agents/skills/maintain-the-app/SKILL.md
git commit -m "Stop logging the absent first-open copy as a failure"
git push origin master
```

---

## Self-Review

**1. Spec coverage.** The log has three facts. The empty chat is Task 1: a XAML `ItemsSource` binding resolved against `DataContext` (a `Contact`, not a collection) fights the one code writer, which explains both the empty list and the run of first chance `System.Exception` in `SYSTEM.NI.DLL` that has no `DIAG` line because it is thrown inside WinRT. The silent regression risk is Task 2. The `MessageCache.Load` line that made the log look like a cache failure is Task 3.

**2. Step scan.** Each step is one action with one checkable result: read two writers back, delete one attribute, reason through three paths, confirm the header, add one sentence, gate, commit. The only code block is the exact deletion and the two-way catch, because their text is the fix.

**3. Type consistency.** `ConversationView.Bind(ObservableCollection<ChatMessage>)` is the one writer and is already called from `ChatPage.BindAfterCacheAsync(string) -> Task`; `MessagesListView` keeps its name, so `ConversationView`'s constructor argument and `check-chat-list-source.js` agree on it. `problemsFor(string) -> string[]` is the only new symbol, used by the test and by `main()`.

**4. Review Focus.** Each of the five lines names its owner step: the in-memory chat, the saved copy and the empty chat (Task 1 Step 3), the header (Task 1 Step 4), and the first-open log (Task 3 Step 2).

**5. Proportion.** The plan is shorter than the change it describes. Task 1 is one deletion, Task 3 is one split `catch`; only Task 2 has real code, because a guard is code.

## What the phone run should now show

1. Open a chat that has messages, a saved copy, or neither: the messages are there in the first two cases, and the list is empty without an error in the third. The debugger prints no run of first chance `System.Exception` in `SYSTEM.NI.DLL`.
2. The first open of a chat never seen before prints no `DIAG MessageCache.Load` line.
