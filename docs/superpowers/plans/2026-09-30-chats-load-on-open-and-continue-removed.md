# Chats on Open, No Continue Button Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The app opens on the chats, connects on its own, and fills the list as soon as the server says WhatsApp is connected - with no Continue button and no trip through the settings page.

**Architecture:** Three app-side causes, one per task, plus the button removal. (1) `SettingsService.HasSavedSettings` is "a server address was typed", so a phone configured for the *public* server counts as a first run and `App.OnLaunched` opens `ConnectionPage` instead of `ChatsPage` on every launch. (2) `ChatsPage.RequestChats` waits for `WhatsAppState == "connected"` but nothing asks the adapter for the state once the page is in front, so the list waits for a frame that may have already gone by. (3) the rows of the cached snapshot are added to the batch that decides the cache, so the order (and the place of a new conversation) is the previous session's. The `Continue` button existed only because (1) and (2) left the user on a screen that could not fill itself.

**Tech Stack:** Windows Phone 8.1 WinRT/XAML, C# 5; Node.js check scripts under `tools/`; Parallels VM with MSBuild 12.

## Global Constraints

- C# 5 only: no `?.`, no string interpolation, no `nameof`, no tuples, no `async` lambdas. `node tools/check-csharp5.js` is the gate.
- Every file is LF with no BOM. After editing: `perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' <files>`.
- A button named `X` is wired only to `X_Click`, and a button that declares `Width` must declare `MinWidth="0" MinHeight="0"`. Gate: `node tools/check-actions.js`.
- `.resw` keys must exist, and match, in `en-US` and `it-IT`. Gate: `node tools/check-resw.js --strict`.
- No emoji in any `.md` except U+26A0. Gate: `node tools/check-docs.js`.
- Commit messages must contain no apostrophe (they are passed through a bash heredoc).
- **Fast gate** (run after every task, from the repository root):
  `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"`
- **Build gate** (Parallels VM named `Windows 11`), three commands, all three must be clean:
  1. `prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"`
  2. `prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"` -> `COPIA=0`
  3. `prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m /p:WarningLevel=4 & echo FINE=%errorlevel%"` -> `FINE=0`, no warnings
- The phone counts as verified only by the manual steps in "Full verification on the device". The automated gates cannot see the runtime behaviour.

**Context the implementer needs:** the app's saved settings live in `ApplicationData.Current.LocalSettings`, and a debug deploy that reinstalls the app starts from an empty configuration. That is why the pasted debug log shows `DIAG ChatPreferences.Load: FileNotFoundException` and no `ConnectToServerAsync` line at all: nothing was configured, so `App.OnLaunched` opened the settings page and no connection was ever attempted. The adapter side was already fixed and deployed (webhooks are routed by the account JID as well as the device UUID), so this plan is app-only.

---

### Task 1: A public server is a configured server

**Files:**
- Modify: `WhatsappApp/Services/SettingsService.cs:44` (the `EnsureLoaded` comment), `WhatsappApp/Services/SettingsService.cs:50` (the default), `WhatsappApp/Services/SettingsService.cs:121-128` (`HasSavedSettings`)

**Interfaces:**
- Consumes: nothing new.
- Produces: `SettingsService.HasSavedSettings` returns `true` when `UsePublicServer` is on, and the fresh-install default of `UsePublicServer` becomes `true`. `App.OnLaunched` and `ConnectionPage.OnNavigatedTo` read `HasSavedSettings` unchanged.

- [ ] **Step 1: Make the public server count as configured**

In `WhatsappApp/Services/SettingsService.cs`, replace the `HasSavedSettings` property:

```csharp
        /// <summary>
        /// True when the app has enough to connect on its own: a typed address, or
        /// the public service, which needs no address at all because it reads one
        /// from GitHub. Without the second half, a phone set up for the public
        /// server counted as a first run, so every launch opened the settings page
        /// and the chats screen never asked for its list.
        /// </summary>
        public static bool HasSavedSettings
        {
            get { return UsePublicServer || !string.IsNullOrEmpty(ServerAddress); }
        }
```

- [ ] **Step 2: Make the public server the default**

In the same file, in `EnsureLoaded`, change the `_usePublicServer` read:

```csharp
            // The service this app is built around is the shared one: it needs no
            // address, so a phone that has never been configured can go straight to
            // the chats and find it. A private server is one switch away, in the
            // settings page, which the gear of the chats screen opens.
            _usePublicServer = ReadBool(KeyUsePublicServer, true);
```

Also update the stale comment above `_serverAddress` so it is not a lie:

```csharp
            // The address stays empty until the user saves one, or until the public
            // service is connected to at least once: HasSavedSettings tells "first
            // run" from "already configured".
            _serverAddress = ReadString(KeyServerAddress, "");
```

- [ ] **Step 3: Run the fast gate**

Run:
```bash
cd /Users/vincenzo/Documents/WhatsappForWP && node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"
```
Expected: every line starts with `OK:` and the test run ends `pass 58`, `fail 0`.

- [ ] **Step 4: Build on the VM**

Run the three build-gate commands from Global Constraints.
Expected: `COPIA=0`, then `FINE=0` with no warning lines.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Services/SettingsService.cs
git commit -m "Treat the public server as a configured server

A phone set up for the public service has no typed address, so HasSavedSettings
was false and every launch opened the settings page instead of the chats. The
public switch now makes the app configured, and it is the default, so a fresh
install connects on its own and the settings page is reached only on purpose."
```

---

### Task 2: The chats page asks for the state, so the list loads by itself

**Files:**
- Modify: `WhatsappApp/Pages/ChatsPage.xaml.cs:288-294` (`OnNavigatedTo` tail), `WhatsappApp/Pages/ChatsPage.xaml.cs:317-320` (`OnServerAvailable`)

**Interfaces:**
- Consumes: `CommunicationService.Instance.SendControlAsync(string, string = null)`, `CommunicationService.Instance.IsConnected`, `CommunicationService.Instance.ConnectionEstablished`, `.ControlMessageReceived`, `.WhatsAppState`; `CommunicationService.Instance` already exposes all of them.
- Produces: a `state` frame is requested whenever the socket is up, which routes into the existing `case "state"` and `RequestChats()`.

- [ ] **Step 1: Ask for the state on entry when the socket is already up**

In `WhatsappApp/Pages/ChatsPage.xaml.cs`, in `OnNavigatedTo`, replace the final line `RequestChats();` with:

```csharp
            // The adapter says nothing on its own until something asks: if the
            // socket came up before this page existed, the state frame has already
            // gone by and the list would wait for another one forever. Asking here
            // makes the answer arrive now, and OnControlMessageReceived turns it
            // into the list request.
            if (CommunicationService.Instance.IsConnected)
            {
#pragma warning disable 4014
                CommunicationService.Instance.SendControlAsync("status");
#pragma warning restore 4014
            }

            RequestChats();
```

- [ ] **Step 2: Ask for the state when the connection comes up**

In the same file, replace the body of `OnServerAvailable`:

```csharp
        private void OnServerAvailable(object sender, EventArgs e)
        {
            ServerUnavailableBar.Visibility = Visibility.Collapsed;

            // The socket is up. Whether WhatsApp is linked is a separate answer,
            // and the list is asked for only once it is connected: this is the
            // request that produces it.
#pragma warning disable 4014
            CommunicationService.Instance.SendControlAsync("status");
#pragma warning restore 4014
        }
```

- [ ] **Step 3: Run the fast gate**

Run the fast gate from Global Constraints.
Expected: all `OK:`, `pass 58`, `fail 0`.

- [ ] **Step 4: Build on the VM**

Run the three build-gate commands.
Expected: `COPIA=0`, `FINE=0`, no warnings.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Pages/ChatsPage.xaml.cs
git commit -m "Ask the adapter for the state so the chat list loads on its own

The list is requested only when the adapter says WhatsApp is connected, and
nothing asked it after the page was in front: a state frame that arrived while
the socket was coming up was the only one there was, so the list stayed with the
cached rows. The page now asks for the state on entry and on every connection."
```

---

### Task 3: The server order is the order of the list

**Files:**
- Modify: `WhatsappApp/Services/DataService.cs:418-431` (`RememberChatList`), and add `BeginChatList()` next to it
- Modify: `WhatsappApp/Pages/ChatsPage.xaml.cs:337-344` (`RequestChats`)

**Interfaces:**
- Consumes: `DataService.Instance` (existing), `DataService.FindContact(string)` (existing private), `DataService.ResortContacts()` (existing private), `DataService._freshChatRows` (existing private `List<ChatMessage>`), `DataService._contacts` (existing private `ObservableCollection<Contact>`).
- Produces: `public void DataService.BeginChatList()`, called by `ChatsPage.RequestChats` before it sends `chats`.

- [ ] **Step 1: Let the batch be only what the server sent**

In `WhatsappApp/Services/DataService.cs`, immediately before `private void RememberChatList()`, add:

```csharp
        /// <summary>
        /// A chat list is about to arrive. The rows of the cached snapshot went
        /// through ApplyChat at startup, and each of them registered itself in the
        /// batch: without this the batch - and so the file written back, and the
        /// order taken from it - is the previous session, not the server one.
        /// </summary>
        public void BeginChatList()
        {
            _freshChatRows.Clear();
        }
```

- [ ] **Step 2: Move the rows where the server put them**

In the same file, replace the body of `RememberChatList`:

```csharp
        private void RememberChatList()
        {
            if (_freshChatRows.Count == 0) return;

            // The server sends the conversations most recent first, and that is the
            // order the list has to show. A conversation that wrote while the phone
            // was off would otherwise keep the position it had in the cached
            // snapshot, and a brand new one would sit at the bottom, off screen.
            for (int i = 0; i < _freshChatRows.Count; i++)
            {
                if (i >= _contacts.Count) break;
                Contact contact = FindContact(_freshChatRows[i].ChatId);
                if (contact == null) continue;
                int at = _contacts.IndexOf(contact);
                if (at < 0 || at == i) continue;
                _contacts.Move(at, i);
            }
            ResortContacts();

            _chatRows.Clear();
            _chatRows.AddRange(_freshChatRows);
            _freshChatRows.Clear();

#pragma warning disable 4014
            ChatCache.SaveAsync(_chatRows);
#pragma warning restore 4014
        }
```

- [ ] **Step 3: Start the batch where the list is requested**

In `WhatsappApp/Pages/ChatsPage.xaml.cs`, replace the body of `RequestChats`:

```csharp
        private void RequestChats()
        {
            if (!CommunicationService.Instance.IsConnected) return;
            if (CommunicationService.Instance.WhatsAppState != "connected") return;

            // The rows that come back are the server order, and this is where that
            // batch starts: everything ApplyChat records from here on belongs to it.
            DataService.Instance.BeginChatList();

#pragma warning disable 4014
            CommunicationService.Instance.SendControlAsync("chats");
#pragma warning restore 4014
        }
```

- [ ] **Step 4: Run the fast gate**

Run the fast gate from Global Constraints.
Expected: all `OK:`, `pass 58`, `fail 0`.

- [ ] **Step 5: Build on the VM**

Run the three build-gate commands.
Expected: `COPIA=0`, `FINE=0`, no warnings.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Services/DataService.cs WhatsappApp/Pages/ChatsPage.xaml.cs
git commit -m "Put the chat rows in the order the server sent them

The batch that decides the position of a row also held the rows of the cached
snapshot, loaded through ApplyChat at startup, so a conversation that wrote while
the phone was off kept its old place and a new one was appended at the bottom.
The batch now starts when the list is requested, and the list is moved into the
server order when it ends."
```

---

### Task 4: Remove the Continue button

**Files:**
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml:197-203`
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml.cs:389`, `:398`, `:411`, `:622-625`
- Modify: `WhatsappApp/Strings/en-US/Resources.resw:244-246`
- Modify: `WhatsappApp/Strings/it-IT/Resources.resw:244-246`
- Modify: `.agents/skills/maintain-the-app/SKILL.md:440-446` (the note that still describes the button)

**Interfaces:**
- Consumes: nothing new.
- Produces: `ConnectionPage` keeps `ContinueToChatsPage()` (still used by `BackButton_Click`), drops `ContinueButton` and `ContinueButton_Click`. `UpdateLoginUi` still drives every other control in the `WhatsAppPanel`.

- [ ] **Step 1: Remove the control from the XAML**

In `WhatsappApp/Pages/ConnectionPage.xaml`, delete this block, which is the last child of the `StackPanel` inside the `Border` of `WhatsAppPanel`:

```xml
                            <Button x:Name="ContinueButton" x:Uid="ConnectionPage_Continue"
                                    Content="Continue"
                                    Background="{StaticResource WhatsAppAccentBrush}"
                                    Foreground="White" FontSize="18" FontWeight="SemiBold"
                                    Height="48" BorderThickness="0" Margin="0,14,0,0"
                                    IsEnabled="False"
                                    Click="ContinueButton_Click"/>
```

- [ ] **Step 2: Remove it from the code-behind**

In `WhatsappApp/Pages/ConnectionPage.xaml.cs`:

Delete the handler:

```csharp
        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            ContinueToChatsPage();
        }
```

In `UpdateLoginUi`, delete the three `ContinueButton.IsEnabled = ...;` lines, one in each `case` (`connected` had `= true;`, `waiting` and `default` had `= false;`). Leave every other statement in those cases untouched.

- [ ] **Step 3: Remove the two resource entries**

In both `WhatsappApp/Strings/en-US/Resources.resw` and `WhatsappApp/Strings/it-IT/Resources.resw`, delete:

```xml
  <data name="ConnectionPage_Continue.Content" xml:space="preserve">
    <value>Continue</value>
  </data>
```

The `it-IT` file has `<value>Continua</value>` instead; both blocks go.

- [ ] **Step 4: Update the note that describes the old behaviour**

In `.agents/skills/maintain-the-app/SKILL.md`, in the bullet that begins "**A screen that is shown before the socket is up cannot ask for its data.**", replace the sentence that ends the explanation of the old bug:

Old: ``...so the list stayed empty until the user walked through the settings page, which is what "I have to press Continue every time" was.``

New:

```
`ChatsPage.OnNavigatedTo` used to request the list only when
  `IsConnected && Contacts.Count == 0`; on a cold start neither is true, so the list
  stayed empty until the user walked through the settings page. The request now happens
  in `RequestChats()` - called on navigation, on every `state` frame, and after the page
  asks the adapter for the state (`status`) - and it waits for
  `WhatsAppState == "connected"`, because the adapter answers "not connected" until the
  WhatsApp login is done. There is no Continue button any more: a configured app opens
  on the chats and never on the settings.
```

- [ ] **Step 5: Run the fast gate**

Run the fast gate from Global Constraints.
Expected: `OK: 20 button(s) and 1 Button style(s), name/handler/icon/size agree.`, `OK: 147 key(s) in en-US and it-IT, every x:Uid and Loc.Get lookup resolved.`, and `pass 58`, `fail 0`.

- [ ] **Step 6: Build on the VM**

Run the three build-gate commands.
Expected: `COPIA=0`, `FINE=0`, no warnings.

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Pages/ConnectionPage.xaml WhatsappApp/Pages/ConnectionPage.xaml.cs WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw .agents/skills/maintain-the-app/SKILL.md
git commit -m "Remove the Continue button from the settings page

It only existed to carry the user away from a page that could not fill the list
by itself. With the app opening on the chats and the list loading as soon as the
adapter says WhatsApp is connected, the button has no job left."
```

---

## Full verification on the device

Nothing above proves the phone behaviour: the checks and the build see source, not a running app. Run this once all four tasks are in.

- [ ] **Step 1: Deploy the package**

The VM build writes `C:\Temp\wp81\WhatsappApp\AppPackages\WhatsappApp_1.0.0.0_Debug_Test\WhatsappApp_1.0.0.0_arm_Debug.appxbundle` (the ARM one; the phone is ARM). Install it on the phone the way the previous builds were installed.

- [ ] **Step 2: Confirm the app opens on the chats**

Unplug the debugging session first: with the debugger attached the app is started by Visual Studio and the settings page may be what is on screen. Launch the app from the phone's app list.
Expected: the chats screen, never the settings page, with no Continue button anywhere.

- [ ] **Step 3: Confirm the list fills by itself**

Stay on the chats screen and wait. The adapter prints, on the NAS:
`docker logs --tail 30 whatsapp-for-wp8 | grep -E 'Chats:|reading up to'`
Expected: `reading up to 25 conversation(s)...` and `Chats: N conversation(s) from M` appear without touching the phone.

- [ ] **Step 4: Confirm the current conversations are in it**

Expected: the conversations of the linked account are in the list, most recent first, each with the preview of its last message. Rows show a picture when WhatsApp has one, initials when it does not: a chat with no profile photo is not a bug.

- [ ] **Step 5: Confirm a new message moves the row**

Send yourself a WhatsApp message from another phone while the chats screen is open.
Expected: the row jumps to the top with the new preview, and the unread count goes up.
