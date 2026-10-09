# Open a chat at its newest message, and jump back to it Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A conversation opens showing its newest message even when the history burst arrives after the list is bound, and a bottom-left arrow like WhatsApp's brings the reader back to the newest message from anywhere in the conversation.

**Architecture:** Two independent defects, one per request. The first is a missing scroll: the page relies on one scroll queued at bind time and on the comment that says the history burst would produce the next one, but the burst's own handler returns early for every `IsHistory` row, so when the burst lands after the bind nothing moves the view and the reader is left at the oldest cached row. That is fixed by listening to `DataService.HistoryCompleted`, the frame that already closes a burst, and asking the view for its bottom there. The second is a new control: one icon-only button over the conversation, whose visibility is driven by the view's own position, which means `ConversationView` has to report the position in both directions instead of only telling the page when the bottom is reached.

**Tech Stack:** C# 5 / WP8.1 XAML (`Windows.UI.Xaml`) on the app side; dependency-free Node.js (CommonJS) with `node:test` for the guards in `tools/`.

**Spec:** the user's report of 2026-10-09, verbatim, is the authority — there is no separate design document:

> alcune chat, non vanno direttamente quando le apro ai nuovi messaggi, ma quelli di una volta quando ancora riuscivo ad aprire la chat e l'app non crashava, inoltre metti una freccia in basso a sinistra come whatsapp, in modo che si possa cliccare e ritornare ai messaggi recenti

Read as two requests:

1. **Open at the newest.** Some chats open on the messages of an earlier time instead of the newest ones.
2. **A bottom-left arrow** that is tapped to come back to the recent messages.

## The facts this plan is built on

Every one of these was read out of the tree on 2026-10-09; each names the file and line, and each is what a task changes or leans on.

**The one scroll a chat gets.** `ChatPage.BindAfterCacheAsync` (`WhatsappApp/Pages/ChatPage.xaml.cs:492-505`) binds the list and then asks once for the newest row:

```csharp
_view.Bind(_messages);
Diag.OkNow("bound " + _messages.Count + " rows, memory " + ... );
if (isActiveChat && _messages.Count > 0)
    _view.ScrollTo(_messages[_messages.Count - 1]);
```

**The scroll that was supposed to follow, and never does.** `ChatPage.OnMessageReceived` (`:262`) drops every history row:

```csharp
// History is not news. ...
if (message.IsHistory) return;
```

so the burst produces no scroll at all. `ConversationView.QueueScroll` (`WhatsappApp/Services/ConversationView.cs:139-181`) leans on that scroll existing — its early return for a list that is not in the tree is commented *"The next scroll - the one the history burst produces - runs when the list is up"* — and there is no next scroll. A chat therefore stays wherever the list put it when it was bound, which is its first row: the oldest one loaded.

**When the burst lands after the bind.** `BindAfterCacheAsync` awaits the burst only when this page asked for it (`WaitForHistoryAsync`, at most `HistoryWaitMaxMilliseconds = 20000`). The burst still lands after the bind in three cases that the log shows as `history not requested for ... (already asked)` or as a request made later: the chat opened while the socket was down (the request goes out from `OnConnectionEstablished`, `:574`), the chat opened in the same session before (`MarkHistoryRequested` is once per chat per session), and the wait timing out against the adapter's cold read of the account. In all three the rows are inserted into a list that is already bound, and nothing moves it.

**Where the burst closes.** `DataService` raises `HistoryCompleted` with the chat id on the adapter's `history.done` frame — `RaiseHistoryCompleted(message.ChatId)` (`WhatsappApp/Services/DataService.cs:362`), the same event `WaitForHistoryAsync` subscribes to for its own one-shot wait (`ChatPage.xaml.cs:548`). The page has no permanent subscription, so the event that says "the burst is in" reaches nobody.

**"Those of an earlier time" is not the message cache.** Every pulled store on this phone reads `ok: cache restored 0 message(s)` (19 occurrences across the four `diag.log` files of 2026-10-08/09) and no `messages_*.json` file exists in any of them, so the rows the reader is looking at are the ones the session already had, not a `MessageCache` copy. The position is the defect; the messages are present.

**The position is the whole of request 1.** The read path works: the same logs show `history done for 393492556507@s.whatsapp.net: 50 message(s)` and `bound 50 rows`. What is missing is the move to the bottom.

**What reports the position today.** `ConversationView.WatchBottom(Action onBottom)` (`ConversationView.cs:100`) calls its action only while the reader *is* at the bottom, and only from the moment the page asks for it (lazily, from `ChatPage.WatchBottomReached`, `ChatPage.xaml.cs:349`). There is no way to be told that the newest message has left the screen, which is exactly the state the arrow has to be shown for.

**The margin that decides "at the bottom".** `BottomFollowMargin = 80` (`ConversationView.cs:29`) and `AtBottom()` (`:88`) answer `true` for a list with no viewer or nothing to scroll (`ScrollableHeight <= 0`), so a conversation shorter than its viewport is already at the bottom and must not show the arrow.

**The button rules this page already follows.** An icon-only button gets its label from a tooltip set in the constructor (`ToolTipService.SetToolTip(BackButton, Loc.Get("ChatPage_BackTooltip", "Back"))`, `ChatPage.xaml.cs:73`), which is a `Loc.Get` key that must exist in both `.resw` files (`WhatsappApp/Strings/en-US/Resources.resw` and `WhatsappApp/Strings/it-IT/Resources.resw`, 172 keys each today). A fixed-size button must zero the WP8.1 theme minimums, an icon is an inline `<PathGeometry>` under a `<!-- IconX -->` comment, and a button named `X` is wired only to `X_Click` (`tools/check-actions.js`, `tools/check-icons.js`).

**The root grid has the row to put it in.** `WhatsappApp/Pages/ChatPage.xaml:63-72` defines four rows — header `Auto`, conversation `*`, typing `Auto`, composer `Auto` — and the `ListView x:Name="MessagesListView"` is `Grid.Row="1"` (`:148-556`). A child declared after it in row 1 draws over the conversation, which is how the full-screen viewers already sit over it.

## Global Constraints

These hold for every task; they come from `.agents/skills/maintain-the-app/SKILL.md`.

- **C# 5 only.** No `$"..."`, no `?.`, no `get => x`, no `nameof`, no `out var`, no `await` inside a `catch` body. Gate: `node tools/check-csharp5.js`.
- **No hardcoded user-visible strings.** UI text goes in both `.resw` files with the same key, then `x:Uid` / `Loc.Get`. Gate: `node tools/check-resw.js --strict` (it also refuses a key no `Loc.Get` uses).
- **Runtime text outside the app UI is English** — every `Diag` line.
- **Icons are inline `<PathGeometry>`** in a `<Path.Data>`, never a `StaticResource`, never `Figures="M..."`, each named by a `<!-- IconX -->` comment. Gate: `node tools/check-icons.js`.
- **A silent `catch` is a bug.** Every survived failure goes through `Diag.Failed("<call site>", ex)`.
- **A fixed-size button zeroes the theme minimums** (`MinWidth="0" MinHeight="0"`), or the 109x57.5 minimum of WP8.1 overrides the declared size. Gate: `node tools/check-actions.js`.
- **An `x:Name` is used once in one namescope.** Gate: `node tools/check-xaml-names.js`.
- **`MessagesListView.ItemsSource` has exactly one writer**, `ConversationView.Bind`. Gate: `node tools/check-chat-list-source.js`.
- **A new source file is a project file too** — `<Compile Include>` / `<Page>` in `WhatsappApp.csproj`. Gate: `node tools/check-project-files.js`. (This plan adds no app file: both tasks edit files that are already listed.)
- **Docs are written in pairs**, `README.md` + `README.it.md`, with the same headings in the same order and no emoji. Gate: `node tools/check-docs.js`.
- **The counts in `.agents/skills/test-the-app/SKILL.md` are stale** (`145 keys in each .resw` against 172 on disk). Take every number from the guards' own output, never from that line.

## Review Focus

The spec is a report of two symptoms: it says what the app must do, not everything the change will meet. These are the five failure modes the report implies and no task's tests exercise, most likely first. Each is pinned in the task that owns the code, as its device step.

1. **A reader scrolled up inside the chat when the burst closes is moved to the bottom.** The report asks for exactly this move, and it is one tap to undo with the new arrow; the alternative (never moving) is the bug being fixed. Task 1 owns it.
2. **A burst that closes for a chat that is not on screen moves another page's list.** `ActiveChatId` is the page's own answer to "is this the chat in front", and the handler must ask it. Task 1 owns it.
3. **The arrow steals the tap of the bubble under it.** It is 44x44 over the conversation, bottom left, and a bubble that reaches there must still open its own action. Task 2 owns it.
4. **A conversation shorter than the screen shows the arrow.** `AtBottom()` is `true` with nothing to scroll, so the visibility must come from the position callback and from nowhere else. Task 2 owns it.
5. **The unread row stops clearing when the reader reaches the bottom.** The mark-read path is wired through the subscription this plan replaces (`WatchBottom` -> `WatchPosition`), so a message that arrived while reading somewhere else must still be told to the server when the reader gets to it. Task 2 owns it.

---

### Task 1: the page follows the burst that closes after the bind

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (subscribe in `OnNavigatedTo`, unsubscribe in `OnNavigatedFrom`, new handler next to `WatchBottomReached`)
- Create: `tools/check-chat-scroll.js`
- Create: `tools/test/check-chat-scroll.test.js`

**Interfaces:**
- Consumes: `DataService.Instance.HistoryCompleted` (`EventHandler<string>`, raised with the chat id at `Services/DataService.cs:362`), `DataService.Instance.ActiveChatId`, `ConversationView.ScrollToBottom()` (`Services/ConversationView.cs:76`), `Diag.Ok(string)`.
- Produces: `ChatPage.OnHistoryCompleted(object sender, string chatId)` (private); `chatScrollProblems(files) -> string[]` from `tools/check-chat-scroll.js`, where `files = { page, xaml }` are the two file contents. The array is empty when both rules hold, and each entry is a sentence naming the failure.

The guard has two rules. Rule 1 is this task's, and it fails today:

- **R1 — the burst that closes moves the conversation to its newest message.** The page must subscribe to `HistoryCompleted` and must unsubscribe from it, its handler must ask `ActiveChatId` before it acts, and it must call `ScrollToBottom()`.

- [ ] **Step 1: Write the failing test**

`tools/test/check-chat-scroll.test.js`, in the shape of `tools/test/check-attachment-file.test.js` (which reads the real files and mutates a copy):

```javascript
'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const { chatScrollProblems } = require('../check-chat-scroll');

const ROOT = path.join(__dirname, '..', '..');
const PAGE = 'WhatsappApp/Pages/ChatPage.xaml.cs';
const XAML = 'WhatsappApp/Pages/ChatPage.xaml';
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
const files = () => ({ page: read(PAGE), xaml: read(XAML) });

test('il salto all ultimo messaggio e la freccia sono a posto', () => {
  assert.deepStrictEqual(chatScrollProblems(files()), []);
});

test('una pagina che non segue il burst non arriva ai messaggi nuovi', () => {
  const broken = files();
  broken.page = broken.page
    .replace(/DataService\.Instance\.HistoryCompleted \+= OnHistoryCompleted;\r?\n/, '');
  assert.match(chatScrollProblems(broken).join('\n'), /HistoryCompleted/);
});

test('un burst senza la chat in primo piano non deve muovere un altra lista', () => {
  const broken = files();
  broken.page = broken.page
    .replace(/if \(DataService\.Instance\.ActiveChatId != _contact\.Id\) return;\r?\n/,
      '');
  assert.match(chatScrollProblems(broken).join('\n'), /ActiveChatId/);
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `node --test tools/test/check-chat-scroll.test.js`
Expected: FAIL — `Cannot find module '../check-chat-scroll'`.

- [ ] **Step 3: Write `tools/check-chat-scroll.js`**

Same header shape as the neighbours (`tools/check-attachment-file.js`): the reason the guard exists, the rules as a list, `--help`-less `main()` guarded by `require.main === module`, `module.exports = { chatScrollProblems }`, an `OK: ...` line and exit 1 with the problems joined. The module walks the two paths it is given — it does not reach into the tree itself — so a test can hand it a mutated copy. R1's three assertions, each with its own sentence:

- `DataService.Instance.HistoryCompleted += OnHistoryCompleted;` is present in `page`, and `DataService.Instance.HistoryCompleted -= OnHistoryCompleted;` is too: *"the burst that closes is what puts the newest messages in, and a page that is not told about it stays at its oldest row"*.
- the body of `OnHistoryCompleted` contains `DataService.Instance.ActiveChatId`: *"a burst for a chat that is no longer in front would move another page's list"*.
- the same body contains `_view.ScrollToBottom()`: *"the conversation is bound before some bursts arrive, so nothing else moves it to its newest message"*.

- [ ] **Step 4: Run test to verify R1 fails on the real page**

Run: `node --test tools/test/check-chat-scroll.test.js`
Expected: 2 tests PASS (the two mutations) and the first FAIL with the `HistoryCompleted` sentence — the page has no such subscription: this is the RED for the app change.

- [ ] **Step 5: Implement the page change**

In `ChatPage.xaml.cs`:

- in `OnNavigatedTo`, next to `DataService.Instance.AudioArrived += OnAudioArrived;`: `DataService.Instance.HistoryCompleted += OnHistoryCompleted;`
- in `OnNavigatedFrom`, next to the unsubscribe of the same block: `DataService.Instance.HistoryCompleted -= OnHistoryCompleted;`
- a new handler beside `WatchBottomReached` (`:349`):

```csharp
/// <summary>
/// The burst of old messages has closed for this chat. Its rows went in below
/// whatever the list was showing, and nothing else moves the view: a chat that
/// was bound before its burst was requested - opened while the socket was down,
/// or opened a second time in the same session - stayed at its oldest row.
/// </summary>
private void OnHistoryCompleted(object sender, string chatId)
{
    if (_contact == null || chatId != _contact.Id) return;
    if (DataService.Instance.ActiveChatId != _contact.Id) return;

    Diag.Ok("burst closed, moved to the newest for " + chatId);
    _view.ScrollToBottom();
}
```

Nothing else in `OnMessageReceived` changes: its early return for `IsHistory` rows stays, because acting on each row of a burst is what killed the run of 2026-10-08.

- [ ] **Step 6: Run test to verify it passes**

Run: `node --test tools/test/check-chat-scroll.test.js`
Expected: 3 tests PASS.

- [ ] **Step 7: Run the gate this task touches**

Run: `node tools/check-chat-scroll.js` — Expected: `OK: ...`, exit 0.
Run: `node tools/check-csharp5.js`, `node tools/check-chat-list-source.js`, `node tools/check-fire-and-forget.js` — Expected: exit 0 each.

- [ ] **Step 8: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml.cs tools/check-chat-scroll.js tools/test/check-chat-scroll.test.js
git commit -m "fix: a chat follows the history burst that closes after the bind"
```

---

### Task 2: the arrow over the conversation

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml` (a `Button` in row 1, declared after the `ListView` that closes at `:556`)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (`OnViewPositionChanged`, `RecentMessagesButton_Click`, the tooltip in the constructor, the subscription in `OnNavigatedTo` / `OnNavigatedFrom`)
- Modify: `WhatsappApp/Services/ConversationView.cs` (`WatchPosition` replaces `WatchBottom`)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw` (the tooltip key)
- Modify: `tools/check-chat-scroll.js`, `tools/test/check-chat-scroll.test.js` (rule R2 and its tests)

**Interfaces:**
- Consumes: `ConversationView.AtBottom()` (`ConversationView.cs:88`), `ConversationView.ScrollToBottom()` (`:76`), `ConversationView.Reset()` (`:127`, already called from `OnNavigatedFrom`), the root grid's row 1 (`ChatPage.xaml:63-72`).
- Produces: `ConversationView.WatchPosition(Action<bool> atBottom)` — called once when it is put on and again on every change of "at the bottom", replacing `WatchBottom(Action)`, which is deleted along with `ChatPage.WatchBottomReached`; `ChatPage.OnViewPositionChanged(bool atBottom)`; `ChatPage.RecentMessagesButton_Click(object sender, RoutedEventArgs e)`; the resw key `ChatPage_JumpToRecentTooltip`; rule R2 in `chatScrollProblems`.

- **R2 — the jump button is named, wired to its own handler, over the conversation, hidden to start, and its handler is the view's own scroll.** The XAML must carry a `Button x:Name="RecentMessagesButton"` with `Click="RecentMessagesButton_Click"`, `Grid.Row="1"`, `Visibility="Collapsed"` and an `<!-- IconArrowDown -->` geometry; the page must have `private void RecentMessagesButton_Click` whose body calls `_view.ScrollToBottom()`; and the page must contain no `ScrollableHeight` — the position comes from the view, never from the page.

- [ ] **Step 1: Write the failing test**

Append to `tools/test/check-chat-scroll.test.js`:

```javascript
test('una freccia senza il suo gestore non riporta ai messaggi recenti', () => {
  const broken = files();
  broken.xaml = broken.xaml.replace(/Click="RecentMessagesButton_Click"/, 'Click="RecentMessagesButtonClick"');
  assert.match(chatScrollProblems(broken).join('\n'), /RecentMessagesButton_Click/);
});

test('una pagina che decide da sola il fondo e un problema', () => {
  const broken = files();
  broken.page = broken.page + '\r\n// ScrollableHeight\r\n';
  assert.match(chatScrollProblems(broken).join('\n'), /ScrollableHeight/);
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `node --test tools/test/check-chat-scroll.test.js`
Expected: the two new tests FAIL — R2 is not written yet, so `chatScrollProblems` reports nothing for a broken xaml. (If Step 1 is taken before Task 1's guard exists, the whole file fails on the missing module; that is the same RED.)

- [ ] **Step 3: Add R2 to `tools/check-chat-scroll.js`**

Five assertions with their own sentences, the last one being *"the page must take the position from ConversationView: a second opinion about the bottom is the one that goes stale"*.

- [ ] **Step 4: Run test to verify R2 fails on the real files**

Run: `node --test tools/test/check-chat-scroll.test.js`
Expected: R1's tests PASS, the "everything in place" test FAILS with the `RecentMessagesButton` sentence, the xaml mutation test now PASSES. This is the RED for the app change.

- [ ] **Step 5: Implement `ConversationView.WatchPosition`**

Replace `WatchBottom(Action onBottom)` with:

```csharp
public void WatchPosition(Action<bool> atBottom)
{
    _onPosition = atBottom;
    _lastAtBottom = null;
    AttachViewChanged();
}
```

Two decisions this signature carries, and both go in the code's comments:

- the callback is called **once when it is put on**, so the arrow starts in the right state instead of waiting for the first scroll;
- `AttachViewChanged()` is also called from `Bind`, because the page asks for the position during `OnNavigatedTo`, before the list is in the tree and before `Viewer()` can find its `ScrollViewer`. A watch put on with no viewer is therefore kept and attached when the list is bound, which is the case `WatchBottom` had no answer for (`if (viewer == null) return;`).

`OnViewChanged` compares `AtBottom()` with `private bool? _lastAtBottom;` and calls the callback only on a change (a `ViewChanged` fires on every frame of a scroll); `Reset()` detaches and clears both the callback and `_lastAtBottom`, exactly as it already clears the viewer.

- [ ] **Step 6: Add the button to the markup**

In `ChatPage.xaml`, after the `ListView`'s closing tag and before the typing bar, in row 1: a `Button x:Name="RecentMessagesButton"` with `Grid.Row="1"`, `HorizontalAlignment="Left"`, `VerticalAlignment="Bottom"`, `Margin="12,0,0,12"`, `Width="44" Height="44"`, `MinWidth="0" MinHeight="0"`, `BorderThickness="0"`, `Padding="0"`, `Background="Transparent"`, `Visibility="Collapsed"`, `Click="RecentMessagesButton_Click"`, whose content is a `Grid` holding an `Ellipse Fill="#FF455A64"` and the arrow `Path` centred in it — the down chevron `M5,8 L12,15 L19,8` drawn as two `LineSegment`s on one `PathFigure`, under `<!-- IconArrowDown -->`, stroked like `IconClose` (`Stroke="White"`, `StrokeThickness="2.4"`, `StrokeStartLineCap="Round"`, `StrokeEndLineCap="Round"`, `Width="18" Height="18"`) and not filled: a chevron drawn with `Fill` alone is a blob, because its outline is not closed.

Run: `node tools/check-icons.js` — Expected: `OK: ...`, one more Path and one more distinct icon.

- [ ] **Step 7: Add the tooltip string**

In the constructor, beside the other icon-only buttons: `ToolTipService.SetToolTip(RecentMessagesButton, Loc.Get("ChatPage_JumpToRecentTooltip", "Jump to the newest messages"));`

Add `ChatPage_JumpToRecentTooltip` to both `.resw` files, same key: EN `Jump to the newest messages`, IT `Vai ai messaggi recenti`.

Run: `node tools/check-resw.js --strict` — Expected: `OK: ...`, one more key in both languages.

- [ ] **Step 8: Wire the page**

In `ChatPage.xaml.cs`:

```csharp
/// <summary>
/// The newest message is off screen, or it is back on it. One subscription and
/// two jobs: WhatsApp's arrow is shown only while there is something below to
/// come back to, and a message left below is told to the server when the reader
/// gets to it (the work WatchBottom did on its own).
/// </summary>
private void OnViewPositionChanged(bool atBottom)
{
    RecentMessagesButton.Visibility = atBottom ? Visibility.Collapsed : Visibility.Visible;
    if (atBottom) MarkReadWhenAtBottom();
}

private void RecentMessagesButton_Click(object sender, RoutedEventArgs e)
{
    Diag.Ok("jump to the newest message");
    _view.ScrollToBottom();
}
```

`OnNavigatedTo` gains `_view.WatchPosition(OnViewPositionChanged);` (after `_messages` is set), `OnNavigatedFrom` keeps `_view.UnwatchBottom()`'s replacement — `_view.Reset()` already detaches — and `WatchBottomReached()`/`WatchBottom` disappear, with `OnMessageReceived`'s `WatchBottomReached();` call removed (the watch is now permanent, so `_markReadPending` is settled by the callback instead).

- [ ] **Step 9: Run the tests and the guards**

Run: `node --test tools/test/check-chat-scroll.test.js` — Expected: 5 tests PASS.
Run: `node tools/check-chat-scroll.js`, `node tools/check-actions.js`, `node tools/check-xaml-names.js`, `node tools/check-csharp5.js` — Expected: exit 0 each.

- [ ] **Step 10: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/Services/ConversationView.cs WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw tools/check-chat-scroll.js tools/test/check-chat-scroll.test.js
git commit -m "feat: an arrow over the conversation jumps back to the newest message"
```

---

### Task 3: the gate, the docs, and the run on the phone

**Files:**
- Modify: `.agents/skills/test-the-app/SKILL.md` (the command block, the guard table, and the counts)
- Modify: `README.md`, `README.it.md` (the behaviour list: opening a chat, and the arrow)
- Modify: `docs/superpowers/plans/2026-10-09-open-at-newest-and-jump-to-recent.md` (the run, appended below, never rewritten)

**Interfaces:**
- Consumes: everything Tasks 1 and 2 produce; the guards' own `OK:` lines for the counts.
- Produces: the gate entry for `check-chat-scroll.js`, the two README bullets, and the device evidence.

- [ ] **Step 1: Read the counts off the guards**

Run every guard in `.agents/skills/test-the-app/SKILL.md`'s block, one at a time, and write the numbers each one prints into that file's first paragraph (files, resw keys, icon Paths and distinct icons, buttons, tests in `tools/test`) in place of the stale ones. Do not carry over a number from the old line: that line said `145 keys` against 172 on disk.

- [ ] **Step 2: Add the guard to the gate**

In the command block, beside `check-attachment-file.js`:

```bash
node tools/check-chat-scroll.js  # a chat follows its history burst, and the arrow back to the newest message is wired
```

and a row in the table below it, the same shape as its neighbours: which regression it refuses.

- [ ] **Step 3: The two README bullets**

In the "Features"/"Funzionalita'" list, next to the existing bullet about a chat painting from its cache, `README.md` and `README.it.md` each gain the same sentence, in their own language and with the same headings: opening a chat shows its newest message, and a chat whose history arrives after the list is already on screen is moved to the newest when the burst closes; an arrow over the conversation, bottom left, appears while the newest message is off screen and returns to it when tapped. Then:

Run: `node tools/check-docs.js` — Expected: `OK: ...`, exit 0.

- [ ] **Step 4: Build and install**

```bash
MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/Bin/MSBuild.exe" WhatsappApp.sln /t:Rebuild \
  /p:Configuration=Debug /p:Platform=ARM /v:m
git checkout -- WhatsappApp/Package.appxmanifest
cd "/c/Program Files (x86)/Microsoft SDKs/Windows Phone/v8.1/Tools/AppDeploy"
env MSYS_NO_PATHCONV=1 ./AppDeployCmd.exe /install '<abs path to ..._arm_Debug.appxbundle>' /targetdevice:de
env MSYS_NO_PATHCONV=1 ./AppDeployCmd.exe /launch 7ccc5b77-3cf2-4020-92a7-9542b250bb49 /targetdevice:de
```

Expected: `Build succeeded` with 0 `warning CS` / 0 `error CS`, the manifest left clean, and `Completato.` twice.

- [ ] **Step 5: The device checklist**

Ask the operator, in this order, with the app left open between them, and pull `diag.log` with `ISETool.exe ts de 7ccc5b77-3cf2-4020-92a7-9542b250bb49 <dir>` afterwards:

1. **Open at the newest (R1, Review Focus 1 and 2).** Open a chat that has messages older than this session and let its history arrive. Expected: it shows the newest message without scrolling, and the log carries `history done for <jid>` then `burst closed, moved to the newest for <jid>`. This is the reported defect, and it is the one step that decides whether the plan did what it was written for.
2. **The arrow appears (R2, Review Focus 4).** In a long chat, scroll up. Expected: the arrow appears at the bottom left. In a chat shorter than the screen, it never appears.
3. **The arrow brings the reader back (R2).** Tap it. Expected: the conversation is at its newest message and the arrow is gone.
4. **The bubble under the arrow still answers (Review Focus 3).** Tap a message bubble in the bottom-left corner beside the arrow. Expected: the bubble's own action, not a jump.
5. **The unread row still clears (Review Focus 5).** Scroll up, let a message arrive, then scroll to the bottom. Expected: the view does not move on arrival, the arrow is there, and reaching the bottom clears the unread row on the chat list.

- [ ] **Step 6: Record the run**

Append the task-by-task ledger entry to `.superpowers/sdd/2026-10-09-open-at-newest-and-jump-to-recent/progress.md` (gitignored), and append to this plan, below everything above, a section titled `## The run of 2026-10-09` carrying the commits, the commands, their output, and any divergence from this document. Do not rewrite the tasks.

- [ ] **Step 7: Commit**

```bash
git add .agents/skills/test-the-app/SKILL.md README.md README.it.md docs/superpowers/plans/2026-10-09-open-at-newest-and-jump-to-recent.md
git commit -m "docs: the gate, the readme and the run for the newest-message scroll"
```
