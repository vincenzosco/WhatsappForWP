# Conversation History And Tile Background Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a conversation that has messages on the server show them on the phone, make the phone able to report itself to the PC without a cable, and give the tile and the app icon a white background so the green mark can be told apart.

**Architecture:** The adapter is not the problem and this plan does not touch how it reads messages. The evidence is direct: on 2026-10-04 at 08:16:33 the container logged `history: 50 message(s) for 393273153236-1489263010@g.us`, and a probe run against the live GOWA from inside the container returned the same chat's messages with `chat_jid` identical to the requested JID for every row - so the frames leave the server correct and the app is where they stop. Four things follow. First, the phone's `Diag` history has to reach the PC without a cable, because the only copy of the app's side of the story is on the phone and the copy-and-paste route does not exist on WP8.1: a `diag` control frame ships it to the adapter, which writes every line to the container log, so the next report is `docker logs` instead of a screenshot. Second, the app writes the three lines that tell the three failure points apart - was the history even requested, did the burst arrive, was the list ever bound. Third, the two code paths whose silence would explain an empty conversation are removed: the list is always bound, and a burst that closes with an empty conversation is asked for once more. Fourth, the background is the one thing that can be decided without evidence: the mark is green, the closed decisions - white square logos, a white manifest background - are what make it readable, and they are specified exactly. The splash keeps its green gradient; it blends into the first page and nobody reported it.

**Tech Stack:** C# 5 / WinRT XAML on Windows Phone 8.1; the `.resw` pairs; Node.js 18.13+ for the adapter and its `node:test` suite; the guards in `tools/`; plain Node (no ImageMagick) for the brand PNGs.

**Spec:** the user's report of 2026-10-04 - "le chat dentro una persona/gruppo non caricano, inoltre cambia il background color per il tile e app icon, che non e' tanto bello/non si riesce a distinguere" - with the decisions taken with them in the same session: one plan for both jobs, and `#FFFFFF` as the background. Evidence: the adapter log line above, and the GOWA probe that compared the chat-list JID with the messages' `chat_jid`.

## Global Constraints

- C# 5: no `await` inside a `catch` or `finally` (CS1985); no `?.`, `$"..."`, `nameof`, pattern matching, auto-property initializers, `out var`.
- Every file LF, no BOM.
- No hardcoded user-visible string: XAML uses `x:Uid` with the property that matches the element; C# uses `Loc.Get("Key", "fallback")`. Both `.resw` files get the same key.
- A new `.cs` or `.xaml` under `WhatsappApp` needs its `<Compile Include>` / `<Page>` in `WhatsappApp.csproj`, or `check-project-files.js` fails.
- Runtime text (adapter logs, `Diag` lines) is **English**; the app UI is localized in the `.resw` pair; source comments are English.
- The docs are pairs; `node tools/check-docs.js` must pass. A new adapter control command is additive, is ignored gracefully by an older app, and is listed in **both** adapter READMEs in the same commit.
- Gate before every commit: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node tools/check-fire-and-forget.js && node tools/check-project-files.js && node tools/check-chat-list-source.js`, then `node --test "tools/test/**/*.test.js"` and `cd WhatsappBridge && npm test`.
- Build only ARM (phone). After a build, `git checkout -- WhatsappApp/Package.appxmanifest` and **never** the `.csproj`.
- Push is the rule: every task ends with `git push origin master`. No apostrophes in commit messages; no emoji in documents except U+26A0.
- The tunnel and the NAS deployment are **not** touched by this plan.

## Review Focus

- **A conversation opened before the socket is up**: it must still get its history once the socket arrives, instead of staying empty until it is closed and opened again. Task 3 owns it.
- **A burst that closes with nothing in it**: the conversation must ask once more rather than stay empty for the session. Task 3 owns it.
- **A page that is left before its burst closes**: the list must never be left with no items source, which renders as an empty conversation even when the messages are in memory. Task 3 owns it.
- **A report that must survive the phone leaving**: the `diag` frame must write every line the screen shows, so the last line before an exit is in the container log. Task 1 owns it.
- **A tile on a phone set to a dark theme**: the white background and `ForegroundText` must agree, or the tile text is white on white. Task 4 owns it.

---

### Task 1: The phone's report travels to the server

**Files:**
- Modify: `WhatsappBridge/server.js` (the `handleCommand` switch, after `case 'media.get'`)
- Test: `WhatsappBridge/test/server.test.js`
- Modify: `WhatsappApp/Pages/DiagnosticsPage.xaml`
- Modify: `WhatsappApp/Pages/DiagnosticsPage.xaml.cs`
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw`
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`

**Interfaces:**
- Consumes: `createBridge({ config, gowa, log, debug })` and `bridge.handleControl(msg)` - the harness the neighbouring tests in `server.test.js` already use; `CommunicationService.Instance.SendControlAsync(string command, string payload)` on the app side, which puts `payload` in `Text`.
- Produces: the control command `diag`; `Text` is the report, one line per `\n`. The adapter writes each line as `[DIAG] <line>` at level `INFO`. Sending it from an app to an adapter that does not know it hits the existing `default: dbg(...)` branch, so it is ignored without closing the socket.

- [ ] **Step 1: Write the failing test**

Add to `WhatsappBridge/test/server.test.js`, next to the other command tests:

```js
test('the diag command writes every line of the phone report to the log', async () => {
  const lines = [];
  const bridge = createBridge({
    config: { bridge: { port: 8585 }, chats: {}, calls: {}, messages: {} },
    gowa: fakeGowa(),
    log: (level, message) => lines.push(level + ' ' + message),
    debug: () => {}
  });

  await bridge.handleControl({
    Type: 3,
    Command: 'diag',
    Text: 'connected: true\nok: opened chat a@s.whatsapp.net\nok: history arrived for a@s.whatsapp.net'
  });

  assert.ok(lines.some((line) => line.includes('[DIAG] connected: true')), 'the header line');
  assert.ok(lines.some((line) => line.includes('[DIAG] ok: opened chat a@s.whatsapp.net')), 'a middle line');
  assert.ok(lines.some((line) => line.includes('[DIAG] ok: history arrived for a@s.whatsapp.net')), 'the last line');
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd WhatsappBridge && node --test --test-name-pattern "diag command" test/server.test.js`
Expected: FAIL - no line contains `[DIAG]`, because `diag` falls into the `default:` branch.

- [ ] **Step 3: Implement `case 'diag'` in `WhatsappBridge/server.js`**

Add this case to the `handleCommand` switch, after `case 'media.get'` and before `case 'logout'`. Split the report so one container log line is one `Diag` line; a phone can send up to the 200 lines its buffer keeps, and the ceiling stops a crafted frame from filling the log.

```js
      case 'diag':
        // The phone's own history, on its way to the machine that can read it.
        // The app's copy is the only one there is - Debug.WriteLine needs a
        // debugger and WP8.1 has no clipboard worth the name - so it is sent
        // here and written to the container log, one line per line.
        for (const line of String(msg.Text || '').split('\n').slice(0, 300)) {
          if (line.trim()) logger('INFO', `[DIAG] ${line}`);
        }
        break;
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd WhatsappBridge && node --test --test-name-pattern "diag command" test/server.test.js`
Expected: PASS.

- [ ] **Step 5: Add the button to the diagnostics page**

The inner `Grid` of `WhatsappApp/Pages/DiagnosticsPage.xaml` today has three rows: the hint (`Auto`), `DiagnosticsText` (`*`) and `ClearButton` (`Auto`, `HorizontalAlignment="Left"`). Add a fourth row for the status line, and replace the single `ClearButton` with a horizontal `StackPanel` holding both buttons. The two buttons keep the shape `check-actions.js` checks: a name, a `Click` handler in the code-behind, `Height="44"` and `MinWidth="120"`.

```xml
                <RowDefinition Height="Auto"/>
```

```xml
            <StackPanel Grid.Row="2" Orientation="Horizontal" Margin="0,8,0,0">
                <Button x:Name="ClearButton" x:Uid="DiagnosticsPage_Clear"
                        Content="Clear"
                        Background="#FFE0E0E0" Foreground="#FF075E54" FontSize="16"
                        Height="44" MinWidth="120" BorderThickness="0"
                        Click="ClearButton_Click"/>
                <Button x:Name="SendButton" x:Uid="DiagnosticsPage_Send"
                        Content="Send to server"
                        Background="#FFE0E0E0" Foreground="#FF075E54" FontSize="16"
                        Height="44" MinWidth="120" BorderThickness="0" Margin="8,0,0,0"
                        Click="SendButton_Click"/>
            </StackPanel>

            <TextBlock x:Name="SendStatusText" Grid.Row="3"
                       Foreground="#FF808080" FontSize="12" TextWrapping="Wrap"
                       Margin="0,8,0,0"/>
```

Extend the hint in the same file so the new button is not folklore - it is the `DiagnosticsPage_Hint.Text` value, which must be changed in **both** `.resw` files: "...Long press the text to select and copy it, or send the report to the server to read it in the container log."

- [ ] **Step 6: Implement `SendButton_Click`**

In `WhatsappApp/Pages/DiagnosticsPage.xaml.cs`, send the exact string the screen shows, so the log and the screenshot cannot disagree. `Refresh()` already builds it; keep that string in a field.

```csharp
        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CommunicationService.Instance.IsConnected)
            {
                SendStatusText.Text = Loc.Get("DiagnosticsPage_SendOffline",
                    "Not connected: the report could not be sent.");
                return;
            }

            string report = DiagnosticsText.Text ?? "";
#pragma warning disable 4014
            Guarded.RunGuardedAsync("DiagnosticsPage/send",
                CommunicationService.Instance.SendControlAsync("diag", report));
#pragma warning restore 4014
            SendStatusText.Text = Loc.Get("DiagnosticsPage_Sent", "Report sent to the server.");
        }
```

Add the keys `DiagnosticsPage_Send`, `DiagnosticsPage_Sent`, `DiagnosticsPage_SendOffline` to **both** `.resw` files, with the same keys and the translated values (`Invia al server`, `Report inviato al server.`, `Non connesso: il report non e' stato inviato.`).

- [ ] **Step 7: List the command in both adapter READMEs**

In `WhatsappBridge/README.md` and `WhatsappBridge/README.it.md`, add `diag` to the control-command table with the same meaning: the phone sends its own diagnostics, `Text` carries the report one line per `\n`, and the adapter writes each line to the log as `[DIAG] ...`. The headings must stay identical between the two files.

- [ ] **Step 8: Run the guards, the tool tests and the bridge suite**

Run the gate command from Global Constraints, then `node --test "tools/test/**/*.test.js"` and `cd WhatsappBridge && npm test`.
Expected: the gate prints its `OK:` lines, the tools tests are `90 tests / 85 pass / 4 fail` (the four are the pre-existing `zip`/download ones), and the bridge is `230` tests with `0` failures plus the new one.

- [ ] **Step 9: Build and commit**

Run the ARM build from Global Constraints, then `git checkout -- WhatsappApp/Package.appxmanifest`.

```bash
git add WhatsappBridge/server.js WhatsappBridge/test/server.test.js WhatsappBridge/README.md WhatsappBridge/README.it.md WhatsappApp/Pages/DiagnosticsPage.xaml WhatsappApp/Pages/DiagnosticsPage.xaml.cs WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw
git commit -m "Send the phone report to the server so the next run reports itself"
git push origin master
```

---

### Task 2: The conversation says where its history stops

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (`OnNavigatedTo`, `BindAfterCacheAsync`)
- Modify: `WhatsappApp/Services/DataService.cs` (`OnControlMessageReceived`, the `history.done` case)

**Interfaces:**
- Consumes: `Diag.Ok(string)`, `DataService.Instance.GetMessages(string)`, `DataService.Instance.ActiveChatId` - all already present.
- Produces: three `Diag` lines with fixed prefixes, which Task 1 carries to the container log: `history requested for <jid>`, `history done for <jid>: <n> message(s)`, `conversation bind skipped for <jid>`. The three together say which of the three failure points it is: the request, the burst, or the bind.

- [ ] **Step 1: Log the request, both ways**

In `ChatPage.xaml.cs`, in `OnNavigatedTo`, inside the `if (historyRequested)` block that sends `SendControlAsync("messages", contact.Id)`, add the line before the send. And after the `if`, log the refusal, so a chat that asked for nothing is not silent:

```csharp
                if (historyRequested)
                {
                    Diag.Ok("history requested for " + contact.Id);
                    // ... the existing send stays exactly as it is
                }
                else
                {
                    Diag.Ok("history not requested for " + contact.Id
                        + (CommunicationService.Instance.IsConnected ? " (already asked)" : " (offline)"));
                }
```

- [ ] **Step 2: Log what the burst delivered**

In `DataService.cs`, `OnControlMessageReceived`, replace the `history.done` case so the count of what the conversation holds at the end of the burst is recorded:

```csharp
                case "history.done":
                    Diag.Ok("history done for " + message.ChatId + ": "
                        + GetMessages(message.ChatId).Count + " message(s)");
                    RaiseHistoryCompleted(message.ChatId);
                    break;
```

- [ ] **Step 3: Log a bind that was skipped**

In `ChatPage.xaml.cs`, `BindAfterCacheAsync`, replace the bare early return with one that says why:

```csharp
            if (DataService.Instance.ActiveChatId != chatId)
            {
                Diag.Ok("conversation bind skipped for " + chatId);
                return;
            }
```

- [ ] **Step 4: Run the guards**

Run the gate command from Global Constraints.
Expected: every `OK:` line, including `check-csharp5.js` still reporting 50 C# files with the new statements C# 5 compatible.

- [ ] **Step 5: Build and commit**

Run the ARM build, then `git checkout -- WhatsappApp/Package.appxmanifest`.

```bash
git add WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/Services/DataService.cs
git commit -m "Say in the log where a conversation history stops"
git push origin master
```

---

### Task 3: A conversation with messages shows them

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (`OnNavigatedTo`, `BindAfterCacheAsync`)
- Modify: `WhatsappApp/Services/DataService.cs` (a per-chat retry flag beside `_historyRequested`)

**Interfaces:**
- Consumes: `DataService.Instance.HistoryCompleted` (already raised with the chat id), `CommunicationService.Instance.ConnectionEstablished` (already raised when the socket is ready), `DataService.Instance.ActiveChatId`.
- Produces: `DataService.Instance.MarkHistoryRetried(string chatId) : bool`, true the first time it is called for a chat in a session - the same shape as the existing `MarkHistoryRequested`, so a retry cannot become a loop.

- [ ] **Step 1: Add the retry marker to `DataService`**

Beside `_historyRequested` (line 35) add `private readonly HashSet<string> _historyRetried = new HashSet<string>();`, clear it wherever `_historyRequested.Remove(chatId)` is already done (lines 999 and 1079), and add the method below `MarkHistoryRequested`:

```csharp
        /// <summary>
        /// Whether this chat may ask for its history one more time. It is the
        /// second and last answer for a chat whose burst came back empty: the
        /// request was made, the adapter answered, and the conversation is still
        /// empty - so the first request was too early, and exactly one more is
        /// allowed per chat per session.
        /// </summary>
        public bool MarkHistoryRetried(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return false;
            return _historyRetried.Add(chatId);
        }
```

- [ ] **Step 2: Bind the list even when the page is no longer the active chat**

In `ChatPage.xaml.cs`, `BindAfterCacheAsync`, the early return from Task 2 leaves a live page with `ItemsSource == null`, which is an empty conversation even when the messages are in the collection. Bind before returning:

```csharp
            if (DataService.Instance.ActiveChatId != chatId)
            {
                // The page is not the one in front any more, but its list is
                // still this page's: leaving it unbound is an empty conversation
                // the next time this instance is shown.
                Diag.Ok("conversation bind skipped for " + chatId);
                _view.Bind(_messages);
                return;
            }
```

- [ ] **Step 3: Ask once more when the burst closed empty**

In `ChatPage.xaml.cs`, `BindAfterCacheAsync`, after the bind, add the retry. It runs only when the app asked for a burst (so a chat with no history on the server is not asked for twice) and only when the conversation is still empty:

```csharp
            if (historyRequested && _messages.Count == 0
                && CommunicationService.Instance.IsConnected
                && DataService.Instance.MarkHistoryRetried(chatId))
            {
                Diag.Ok("history retried for " + chatId);
#pragma warning disable 4014
                Guarded.RunGuardedAsync("ChatPage/messages retry",
                    CommunicationService.Instance.SendControlAsync("messages", chatId));
#pragma warning restore 4014
            }
```

- [ ] **Step 4: Ask when the socket arrives while the chat is open**

A chat opened before the app is connected asks for nothing (`historyRequested` is false) and, without this, stays empty for as long as it is open. Declare a field beside `_messages` (line 24):

```csharp
        private EventHandler _connectionEstablished;
```

Subscribe in `OnNavigatedTo`, right after the existing `if (historyRequested)` block, so a page opened while offline hears the socket arrive:

```csharp
                _connectionEstablished = OnConnectionEstablished;
                CommunicationService.Instance.ConnectionEstablished += _connectionEstablished;
```

Add the handler beside the other page handlers:

```csharp
        /// <summary>
        /// The socket became ready while this conversation is the one in front.
        /// A chat opened before the connection asked for nothing, and this is
        /// what gives it its history instead of an empty list for as long as it
        /// stays open. MarkHistoryRequested is the once-per-chat-per-session
        /// gate, so a chat that was already served asks for nothing here.
        /// </summary>
        private void OnConnectionEstablished(object sender, EventArgs e)
        {
            if (_contact == null) return;
            if (DataService.Instance.ActiveChatId != _contact.Id) return;
            if (!DataService.Instance.MarkHistoryRequested(_contact.Id)) return;

            Diag.Ok("history requested on connect for " + _contact.Id);
#pragma warning disable 4014
            Guarded.RunGuardedAsync("ChatPage/messages on connect",
                CommunicationService.Instance.SendControlAsync("messages", _contact.Id));
#pragma warning restore 4014
        }
```

In `OnNavigatedFrom`, unsubscribe with the other handlers and set `_connectionEstablished = null`.

- [ ] **Step 5: Run the guards**

Run the gate command from Global Constraints.
Expected: every `OK:` line; `check-fire-and-forget.js` reports that every call fired without await is observed, because the two new sends go through `Guarded.RunGuardedAsync`.

- [ ] **Step 6: Build and commit**

Run the ARM build, then `git checkout -- WhatsappApp/Package.appxmanifest`.

```bash
git add WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/Services/DataService.cs
git commit -m "Make a conversation show the history that arrived"
git push origin master
```

---

### Task 4: A white background for the tile and the app icon

**Files:**
- Modify: `tools/make-brand-assets.js`
- Regenerated: `WhatsappApp/Assets/Logo.scale-240.png`, `SmallLogo.scale-240.png`, `Square71x71Logo.scale-240.png`, `StoreLogo.scale-240.png`, `WideLogo.scale-240.png`
- Modify: `WhatsappApp/Package.appxmanifest`
- Modify: `README.md`, `README.it.md`
- Modify: `.agents/skills/release-the-app/SKILL.md`

**Interfaces:**
- Consumes: `placeMark(mark, w, h, inner, background, yOffset)` and `gradient(w, h, topHex, bottomHex)` in `make-brand-assets.js`.
- Produces: `solid(w, h, hex) : { width, height, data }`, the same shape `gradient` returns, used as the background of the four square logos and the wide logo.

- [ ] **Step 1: Add the white solid and use it for the square logos**

In `tools/make-brand-assets.js`, add the colour beside the others and a `solid` beside `gradient`:

```js
const WHITE = '#FFFFFF'; // sfondo di icone e tile: il marchio e' verde
```

```js
/** Un riempimento pieno, per uno sfondo che non e' un gradiente. */
function solid(w, h, hex) {
  const c = hexToRgb(hex);
  const img = blank(w, h);
  for (let p = 0; p < w * h; p++) {
    const o = p * 4;
    img.data[o] = c[0]; img.data[o + 1] = c[1]; img.data[o + 2] = c[2]; img.data[o + 3] = 255;
  }
  return img;
}
```

Then replace the background of the four square logos and of the wide logo: `gradient(size, size, GREEN_LIGHT, GREEN_MID)` becomes `solid(size, size, WHITE)` (twice, once per loop), and `gradient(744, 360, GREEN_LIGHT, GREEN_MID)` becomes `solid(744, 360, WHITE)`. The two `TileIcon` PNGs stay transparent, because the tile background is painted by the manifest, and the splash keeps `gradient(1152, 1920, GREEN_MID, GREEN_DARK)`.

- [ ] **Step 2: Regenerate the PNGs and look at them**

Run: `node tools/make-brand-assets.js --preview`
Expected: the eight `bytes` lines, then the three ASCII previews; the `Logo` preview is a green mark on a light field rather than on the gradient, and the tile preview is unchanged.

- [ ] **Step 3: Set the manifest background and text**

In `WhatsappApp/Package.appxmanifest`, on `m3:VisualElements`, change `BackgroundColor="transparent"` to `BackgroundColor="#FFFFFF"` and `ForegroundText="light"` to `ForegroundText="dark"`. The two go together: a white tile with light text is white on white. Update the comment above the line, which still says transparent is what shows the Start background.

- [ ] **Step 4: Run the guards**

Run the gate command from Global Constraints.
Expected: every `OK:` line, including `check-tile.js` reporting `ms-appx:///Assets/TileIcon.png (200x200, ...)`, and `tools/check-icons.js`'s count unchanged.

- [ ] **Step 5: Update the two READMEs and the skill**

In `README.md` and `README.it.md`, in the icons section, the paragraph that says the manifest sets `BackgroundColor="transparent"` and that this is how WP8.1 shows the Start background now says the tile and the square logos are white (`#FFFFFF`) with `ForegroundText="dark"`, and that a solid background is what keeps the green mark readable. In `.agents/skills/release-the-app/SKILL.md`, the manifest table row for `BackgroundColor` changes from `transparent` to `#FFFFFF`.

- [ ] **Step 6: Build, uninstall, deploy**

Run the ARM build from Global Constraints, then `git checkout -- WhatsappApp/Package.appxmanifest`.

WP8.1 caches the tile: uninstall before installing, or Start keeps the old one.

```
AppDeployCmd.exe /uninstall 7ccc5b77-3cf2-4020-92a7-9542b250bb49 /targetdevice:0
AppDeployCmd.exe /install <path to WhatsappApp_1.0.0.0_arm_Debug.appxbundle> /targetdevice:0
```

- [ ] **Step 7: Commit**

```bash
git add tools/make-brand-assets.js WhatsappApp/Assets WhatsappApp/Package.appxmanifest README.md README.it.md .agents/skills/release-the-app/SKILL.md
git commit -m "Put the green mark on a white tile and app icon"
git push origin master
```

---

## On-device checklist

Run this after Task 3 and again after Task 4, with the app installed on the Lumia and the tunnel up.

- [ ] Open a person with old messages: the conversation shows them.
- [ ] Open a group with old messages: the conversation shows them.
- [ ] Open Diagnostics, press Send, and run `docker logs whatsapp-for-wp8 | grep DIAG` on the NAS: every line of the screen is there, in order.
- [ ] Read the three lines for the chat that was opened: `history requested`, `history done ... N message(s)`, and either `conversation bound N message(s)` or `conversation bind skipped`.
- [ ] If the conversation was still empty, check `history retried for <jid>` and whether the second burst arrived.
- [ ] Start screen: the tile and the app list show the green mark on white, readable on both a dark and a light theme.
