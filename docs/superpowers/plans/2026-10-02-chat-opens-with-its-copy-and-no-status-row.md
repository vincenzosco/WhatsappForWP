# A Chat Opens With Its Copy, and the Status Row Goes Away Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the saved copy of a conversation actually appear when the chat is opened, and keep the WhatsApp status broadcast out of the conversation list.

**Architecture:** Two independent fixes at the two ends of the same wire. On the phone, the ListView is bound *after* the saved copy has been inserted, because inserting into a collection the ListView is already watching, in the middle of a navigation, is what answered `E_UNEXPECTED` and left the conversation empty. In the adapter, the status broadcast is skipped exactly like a channel already is, and the phone refuses the same row a second time so a copy already on disk does not survive.

**Tech Stack:** C# 5 / WinRT XAML on Windows Phone 8.1; Node.js 18+ with `node:test` in `WhatsappBridge`.

**Spec:** the bug report of 2026-10-02 (opening a chat shows it empty, and the debugger prints `DIAG ChatPage/cached messages: Exception 0x8000FFFF Catastrophic failure`; the conversation list carries a row named Status). There is no separate spec document; the evidence is that log line and `WhatsappBridge/chats.js`.

## Global Constraints

- C# 5: no `await` inside a `catch` or `finally` (CS1985).
- `OnNavigatedTo` stays non-async: it is the convention of this codebase and the page binds through a fire-and-forget task it hands to `Guarded.RunGuardedAsync`.
- Every file LF, no BOM; normalize with `perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' <files>`.
- Every failure the app decides to survive logs through `Diag.Failed(site, ex)`.
- There is no C# test harness in this repository. The tests of a C# task are the guard scripts and the ARM build; the last check is on the phone. The adapter has real tests, in `WhatsappBridge/test`, run by `npm test`.
- Gate before every commit: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node tools/check-fire-and-forget.js`, then `node --test "tools/test/**/*.test.js"`, then `cd WhatsappBridge && npm test`.
- Build only ARM (phone) or x86 (emulator), never Any CPU. After a build, `git checkout -- WhatsappApp/Package.appxmanifest` (the `.csproj` is not rewritten: the build runs on a copy on the VM).
- `WhatsappBridge/chats.js` lives in two repositories: after changing it, `cd /Users/vincenzo/Documents/docker-whatsappforwp && node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP`, then `node tools/sync.js --check --from ...` until it prints `OK: server/ matches the adapter (28 file(s))`, then `cd server && npm test`, commit and push `main`.
- No apostrophes in commit messages.

## Review Focus

- Opening a chat that has a saved copy: the copy is on screen, and no `DIAG` line is printed. Task 1 Step 3 owns it.
- Opening a chat that has neither a copy nor history: an empty list, no error, no phantom row. Task 1 Step 3 owns it.
- Coming back to a chat whose messages are already in memory: the list binds and scrolls to the newest bubble. Task 1 Step 4 owns it.
- The account's `status@broadcast` arrives in the server list: no row named Status, and no second HTTP request spent on it. Task 2 Step 2 owns it.
- A row named Status already sitting in `ChatCache` on the phone: it is dropped the first time `ApplyChat` sees it, without waiting for the adapter. Task 2 Step 4 owns it.

---

### Task 1: The saved copy is in the list before the list binds

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (`OnNavigatedTo`, and `LoadCachedMessagesAsync` at the file's cache region)
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (the `A cache is a photograph` invariant)
- Test: none in C#; the guard scripts and the ARM build are the test (see Global Constraints)

**Interfaces:**
- Consumes: `DataService.Instance.LoadCachedMessagesAsync(string) -> Task`, `ChatPage.ScrollToMessage(ChatMessage)`, `MessagesListView`, `Guarded.RunGuardedAsync(string, Task)`.
- Produces: `ChatPage.BindAfterCacheAsync(string) -> Task`, replacing `ChatPage.LoadCachedMessagesAsync`; `MessagesListView.ItemsSource` is now assigned there and nowhere else on this page.

- [ ] **Step 1: Read the two call sites back**

Open `WhatsappApp/Pages/ChatPage.xaml.cs`. Confirm that `LoadCachedMessagesAsync` is called from exactly one place (`OnNavigatedTo`) and that `MessagesListView.ItemsSource` is assigned exactly once, in that same method. The fix moves both into one method; if either has a second call site, stop and report it instead of changing the plan.

- [ ] **Step 2: Bind the list after the copy is in, not before**

In `OnNavigatedTo`, replace the four lines that bind and start the load:

```csharp
                _messages = DataService.Instance.GetMessages(contact.Id);
                MarkRead();
                MessagesListView.ItemsSource = _messages;
#pragma warning disable 4014
                Guarded.RunGuardedAsync("ChatPage/cached messages", LoadCachedMessagesAsync(contact.Id));
#pragma warning restore 4014

                // Auto-scroll to bottom
                if (_messages.Count > 0)
                    ScrollToMessage(_messages[_messages.Count - 1]);
```

with:

```csharp
                // Load messages: first the ones on the phone, so the conversation
                // shows right away, then the real history. The list is bound after
                // that copy is in, and not here: inserting into a collection the
                // ListView is already watching, in the middle of a navigation,
                // answered E_UNEXPECTED and the conversation stayed empty.
                _messages = DataService.Instance.GetMessages(contact.Id);
                MarkRead();
#pragma warning disable 4014
                Guarded.RunGuardedAsync("ChatPage/cached messages", BindAfterCacheAsync(contact.Id));
#pragma warning restore 4014
```

- [ ] **Step 3: Give the load the binding it now owns**

Replace the whole of `LoadCachedMessagesAsync` with:

```csharp
        /// <summary>
        /// Fills the conversation with the copy on the phone, then binds the list
        /// and scrolls to the newest bubble. It must be awaited on the UI thread:
        /// the collection is the one bound to the list.
        /// </summary>
        private async System.Threading.Tasks.Task BindAfterCacheAsync(string chatId)
        {
            await DataService.Instance.LoadCachedMessagesAsync(chatId);

            MessagesListView.ItemsSource = _messages;
            if (_messages.Count > 0) ScrollToMessage(_messages[_messages.Count - 1]);
        }
```

- [ ] **Step 4: Check the three ways into this method by hand**

Read the new `OnNavigatedTo` and `BindAfterCacheAsync` and answer, in a comment or in your report, these three, because no test covers them:

1. A chat with a saved copy: `DataService.LoadCachedMessagesAsync` fills `_messages`, then the list binds — the copy is on screen.
2. A chat with no copy and no history: the call returns with nothing, the list binds empty, no row appears.
3. Coming back to a chat whose messages are already in memory: `DataService.LoadCachedMessagesAsync` returns immediately (its own `GetMessages(chatId).Count > 0` guard), then the list binds and scrolls.

- [ ] **Step 5: Record the order as an invariant**

In `.agents/skills/maintain-the-app/SKILL.md`, in the `A cache is a photograph, not a truth` bullet, add one sentence at the end of the paragraph: the chat page binds its list only after the copy is in, because an insert into a collection the ListView is already watching, during a navigation, answers `E_UNEXPECTED` and the conversation stays empty.

- [ ] **Step 6: Gate and build**

Run the gate and the ARM build from Global Constraints. Expected: every guard `OK`, `# pass 77` in `tools/test`, `# pass 202` in the adapter, and a build that ends with `Your package has been successfully created.` plus `WhatsappServer.exe`.

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml.cs .agents/skills/maintain-the-app/SKILL.md
git commit -m "Bind the chat list after its saved copy is in"
```

---

### Task 2: The status broadcast is not a conversation

**Files:**
- Modify: `WhatsappBridge/chats.js` (`isChannelJid`, `collectChats`, `module.exports`)
- Modify: `WhatsappBridge/message-format.js` (the comment that names `isChannelJid`)
- Modify: `WhatsappBridge/test/chats.test.js`
- Modify: `WhatsappApp/Services/DataService.cs` (`ApplyChat`, `ApplyContact`)
- Modify: `.agents/skills/maintain-the-app/SKILL.md`
- Test: `WhatsappBridge/test/chats.test.js`

**Interfaces:**
- Produces: `isNotAConversation(jid) -> boolean` in `WhatsappBridge/chats.js`, replacing the exported `isChannelJid`; `DataService.IsNotAConversation(string) -> bool`, private.
- Consumes: `ChatPreferences.IsHidden(string) -> bool` (the existing guard these two methods already open with).

- [ ] **Step 1: Write the failing adapter test**

In `WhatsappBridge/test/chats.test.js`, change the import to `isNotAConversation` and add:

```js
test('il broadcast degli stati si riconosce dal suo jid', () => {
  assert.strictEqual(isNotAConversation('status@broadcast'), true);
  assert.strictEqual(isNotAConversation('123456@newsletter'), true);
  assert.strictEqual(isNotAConversation('393401234567@s.whatsapp.net'), false);
  assert.strictEqual(isNotAConversation('123@g.us'), false);
  assert.strictEqual(isNotAConversation(null), false);
});

test('il broadcast degli stati non compare fra le conversazioni', async () => {
  const gowa = {
    chats: async () => [
      { jid: 'a@s.whatsapp.net', name: 'Mario' },
      { jid: 'status@broadcast', name: 'Status' }
    ],
    chatMessages: async () => []
  };
  const rows = await collectChats({ gowa, limit: 25, log: () => {} });
  assert.deepStrictEqual(rows.map((r) => r.chatId), ['a@s.whatsapp.net']);
});
```

- [ ] **Step 2: Run it to watch it fail**

Run: `cd WhatsappBridge && node --test test/chats.test.js`
Expected: FAIL, because `isNotAConversation` is not exported.

- [ ] **Step 3: Skip the status broadcast where the channel is already skipped**

In `WhatsappBridge/chats.js`, replace `isChannelJid` with:

```js
// A channel and the status broadcast are not conversations: neither can be
// answered, and in the chat list each takes the place of a person. GOWA lists
// both, so both are skipped here. The status is the same JID message-format.js
// refuses as a message, and the app draws it in its own Status section.
function isNotAConversation(jid) {
  return typeof jid === 'string'
    && (jid === 'status@broadcast' || jid.endsWith('@newsletter'));
}
```

Use it in the loop, in place of the old call:

```js
    if (isNotAConversation(chat.jid)) continue;
```

and update the export:

```js
module.exports = { previewForMessage, collectChats, isNotAConversation };
```

In `WhatsappBridge/message-format.js`, in the comment above the `status@broadcast` check, change the reference from `chats.js, isChannelJid` to `chats.js, isNotAConversation`.

- [ ] **Step 4: Refuse the same row on the phone**

In `WhatsappApp/Services/DataService.cs`, add near `ApplyChat`:

```csharp
        /// <summary>
        /// The status broadcast is not a conversation: WhatsApp keeps its updates
        /// in the Status section of the app, and GOWA lists the same JID among the
        /// chats. Drawing it as a row put a conversation called "Status" next to
        /// the people. The adapter skips it too; this is here because the row can
        /// also come from the copy already on the phone.
        /// </summary>
        private static bool IsNotAConversation(string chatId)
        {
            return string.Equals(chatId, "status@broadcast", StringComparison.OrdinalIgnoreCase);
        }
```

and make it the first line of both row builders, before the `ChatPreferences.IsHidden` check in each:

```csharp
            if (IsNotAConversation(message.ChatId)) return;
```

- [ ] **Step 5: Run the adapter tests and the gate**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 204 tests (202 + 2), 0 fail.

Run the full gate from Global Constraints, then the ARM build.

- [ ] **Step 6: Sync the mirror**

Run the mirror sequence from Global Constraints: sync, `--check` until it prints `OK: server/ matches the adapter (28 file(s))`, then `cd server && npm test` (204 tests).

- [ ] **Step 7: Record the rule**

In `.agents/skills/maintain-the-app/SKILL.md`, extend the invariant that describes what the adapter lists as a conversation with: the status broadcast is skipped beside the channels, on both ends, because the row can also arrive from the copy already on the phone.

- [ ] **Step 8: Commit and push both repositories**

```bash
git add WhatsappBridge/chats.js WhatsappBridge/message-format.js WhatsappBridge/test/chats.test.js WhatsappApp/Services/DataService.cs .agents/skills/maintain-the-app/SKILL.md
git commit -m "Keep the status broadcast out of the conversation list"
git push origin master
```

Then, in the mirror, commit the synced `server/` files and `git push origin main`.

---

## Self-Review

**1. Spec coverage.** The report has two findings. Finding one ("the chat opens empty", with the `E_UNEXPECTED` line) is Task 1: the exception was always thrown but nobody observed it until `Guarded.RunGuardedAsync` was added, and it aborted the load before the first insert. Finding two (a row named Status) is Task 2, on both ends of the wire.

**2. Step scan.** Each step is one action with one checkable result: read two call sites back, replace a block, replace a method, answer three questions about paths no test covers, run the gate, commit. The two rewritten blocks appear in full because their order is the fix — a signature does not determine that the binding moves.

**3. Type consistency.** `BindAfterCacheAsync(string) -> Task` is called once, from `OnNavigatedTo`, through `Guarded.RunGuardedAsync`, whose second parameter is a `Task`. `LoadCachedMessagesAsync` no longer exists on `ChatPage`, and nothing else calls it. `isNotAConversation` is exported from `chats.js`, imported by the test, and referenced by the `message-format.js` comment. `DataService.IsNotAConversation(string)` is private and takes the `message.ChatId` both callers already have.

**4. Review Focus.** Each of the five lines names the step that covers it: the copy on screen and the empty chat (Task 1 Step 4), the re-opened chat (Task 1 Step 4), the server-list row (Task 2 Step 3), and the row already on disk (Task 2 Step 4).

**5. Proportion.** The plan is shorter than the change it describes and does not re-print the code it leaves alone. Both fixes are one decision each: where the binding happens, and which JIDs are conversations.

## What the phone run should now show

1. Open a chat that was opened before: the old messages are on screen immediately, and the debugger prints no `ChatPage/cached messages` line.
2. Open the chat list: no row named Status. If one is still there, it came from the copy on the phone and goes away at the next `ApplyChat` for that JID (Task 2 Step 4) or at the next list refresh.
