# Chat Open Xaml Name Duplicate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make opening a chat stop closing the app on the Lumia 625: remove the duplicate `x:Name` that makes the conversation's `DataTemplate` throw `XamlParseException 0x802B000A` when its first row is realized, and add the guard that would have caught it before the phone.

**Architecture:** The defect is named, not guessed. A VS2013 *Attach to Process* onto the app already running on the phone - the environment where it closes, unlike F5, which republishes and masks it - printed the exception immediately after the bind it had been blamed on. Position 69 of line 429 of the generated `obj/ARM/Debug/Pages/ChatPage.xaml` is the `x:` of the second `x:Name="PlayAudioButton"`; the first is at line 234 of the source. Both sit inside the single `DataTemplate` of `WhatsappApp/Pages/ChatPage.xaml` (lines 158-554), and one namescope holds one name: a template registers its names when an item is realized, which is the layout pass right after `_list.ItemsSource = messages`, so the app dies with `ok: bound 31 rows` already on disk and looks like it dies in the bind. The fix gives the two audio transports two names and two handlers that share one body. The guard - `tools/check-xaml-names.js`, the fourteenth - makes the duplicate impossible to ship again, and lands in the same commit as the fix, because a guard that reddens the chain cannot be committed on its own and a fix with no guard is the same defect waiting to come back.

**Tech Stack:** C# 5 / WinRT XAML on Windows Phone 8.1; Node.js guards in `tools/` with `node:test`; `WhatsappBridge/` is not touched by this plan.

**Spec:** The evidence of 2026-10-08, from the debugger attached to the app already installed on the phone (never from F5 - the debugger there republishes and the defect does not reproduce):

```
DIAG ok: opened chat 393336165354@s.whatsapp.net, memory 24 MB
DIAG ok: history requested for 393336165354@s.whatsapp.net
DIAG ok: cache restored 0 message(s)
DIAG ok: history arrived for 393336165354@s.whatsapp.net
DIAG ok: history done for 393336165354@s.whatsapp.net: 31 message(s), 10159 bytes
DIAG ok: conversation bound 31 message(s)
DIAG ok: bound 31 rows, memory 25 MB
'WhatsappApp.exe' (CoreCLR: .): Loaded 'C:\windows\system32\System.Runtime.WindowsRuntime.UI.Xaml.NI.DLL'.
DIAG App/unhandled: XamlParseException 0x802B000A The text associated with this error code could not be found.
E_UNKNOWN_ERROR [Line: 429 Position: 69]
```

The XAML runtime module arrives last, the bind has already reported success, and the line and position land on the duplicate name. The same read of the source (`grep -o 'x:Name="[^"]*"' WhatsappApp/Pages/ChatPage.xaml | sort | uniq -d`) returns exactly one name - `PlayAudioButton` - and it is the only duplicate `x:Name` in the whole app. The two are at line 234, inside the bubble bound to `IsIncoming` (line 165), and line 429, inside the bubble bound to `IsOutgoing` (line 360).

## Global Constraints

- C# 5: no `await` inside a `catch` or `finally` (CS1985); no `?.`, `$"..."`, `nameof`, pattern matching, auto-property initializers, `out var`.
- **Line endings**: `core.autocrlf=true`, and the index stores LF (`git ls-files --eol <file>` answers `i/lf w/crlf`). Write the file with the normal editor and do not hand-convert anything; after a change, confirm the file still answers `i/lf w/crlf` and that `git diff --stat` shows the lines you expected - a whole-file rewrite in the diff means the endings changed.
- **The fast gate is fourteen guards and it must exit 0**, run from `tools/`: `node check-csharp5.js && node check-icons.js && node check-resw.js --strict && node check-docs.js && node check-framing.js && node check-tile.js && node check-memory.js && node check-actions.js && node check-fire-and-forget.js && node check-project-files.js && node check-chat-list-source.js && node check-diagnostics.js && node check-handshake-answer.js && node check-xaml-names.js`, then `node --test "tools/test/**/*.test.js"` and `cd WhatsappBridge && npm test`.
- **There is no C# test host**: a C# task's tests are the guards, the ARM build and the phone run. The guard added in Task 2 has real tests, in `tools/test/check-xaml-names.test.js`.
- **The test baseline before this plan** is 107 tests in `node --test "tools/test/**/*.test.js"`: 102 pass, **4 fail**, 1 skip. The four failures are `tools/test/download.test.js` (`extractLargestEntry`, `ensureArchive`) because this host has no `zip`; they are the baseline and are reported as failures, never as passes and never "fixed" by weakening the assertion. After Task 2 expect six more tests, all passing.
- Build only ARM (phone) or x86 (emulator), never Any CPU. After a build, `git checkout -- WhatsappApp/Package.appxmanifest` and **never** the `.csproj`; delete `.build.log`.
- No new `.cs` file in this plan, so no `<Compile Include>` entry is needed; if one is added, `check-project-files.js` fails without it.
- **Push is the rule**: every task ends with `git push origin master`, filtered with `grep -v "repository moved"`. No apostrophes in commit messages; no emoji anywhere except the warning sign U+26A0.
- This machine has no git identity configured, so every commit is prefixed with the identity: `env GIT_AUTHOR_NAME=vincenzosco GIT_AUTHOR_EMAIL=131459463+vincenzosco@users.noreply.github.com GIT_COMMITTER_NAME=vincenzosco GIT_COMMITTER_EMAIL=131459463+vincenzosco@users.noreply.github.com git commit ...`.
- **`WhatsappBridge/` is not touched**, so no Docker mirror sync is needed.
- A diagnostic or guard label is never spelled as a `Loc.Get("...")` call: `check-resw.js` reads every such occurrence in C#, comments included, as a resource key.

## Review Focus

- **A `x:Name` inside a comment.** `ChatPage.xaml` and its neighbours carry prose comments, and a commented-out element left in place is a normal thing to find; if the guard counted one, it would cry wolf on a file nobody broke and would be turned off. Task 2 owns it with the `commented-name.xaml` fixture.
- **The same name in two different templates of one file.** Legal on the phone - each template instantiates into its own namescope - so flagging it would be the guard's first false positive on a perfectly valid page. Task 2 owns it with the `same-name-in-two-templates.xaml` fixture.
- **A template inside a template.** A `ControlTemplate` inside a `DataTemplate` (or a `DataTemplate` inside an `ItemsPanelTemplate`) has its own scope; a name may repeat across the two. Task 2 owns it with the `nested-templates.xaml` fixture.
- **The generated XAML under `obj/` and `bin/`.** Four stale copies of `ChatPage.xaml` exist there right now, one for each platform, with `x:ConnectionId` injected and shifting every column. Reading them would fail the gate on a build leftover and report a line that is not in the source. Task 2's walk skips both directories, exactly as `check-actions.js` does, and the guard's run on the real tree is what proves it.
- **The next media template, added by copy and paste.** The audio bubble is cut twice in one file, so the next one will be too. Task 2's guard is the only thing between that copy and a phone round trip, and Task 3's device run is where it would otherwise land.

---

### Task 1: The work standing in the tree lands first

The tree is dirty with four reviewed, green changes from the investigation that named this defect. They land first and separately: `ChatPage.xaml.cs` and `ChatPage.xaml` are the files Task 2 edits, and a fix committed on top of uncommitted behaviour cannot be reverted apart from it.

**Files:**
- Commit: `docs/superpowers/plans/2026-10-08-chat-open-xaml-name-duplicate.md` (this plan)
- Commit: `WhatsappApp/Pages/ChatPage.xaml.cs` (the `IsHistory` early return in `OnMessageReceived`, and `opened chat ... , memory ... MB` through `Diag.OkNow`)
- Commit: `WhatsappApp/Pages/ChatsPage.xaml.cs` (`OnChatListCompleted` reports rows and memory through `Diag.OkNow`)
- Commit: `WhatsappApp/Services/SelfCheck.cs` (`CheckScreenRequest` logs the truth: the request is asked for and given back)
- Commit: `WhatsappApp/Services/CommunicationService.cs` (`DispatchOnUiThread`'s no-dispatcher path writes `step no dispatcher, running inline` before running inline)
- Commit: `.agents/skills/maintain-the-app/SKILL.md` (the standing note that the token re-issue is not gated by `AUTH_REGISTER`)

**Interfaces:**
- Consumes: nothing.
- Produces: a clean tree at a green commit; `WhatsappApp/Pages/ChatPage.xaml` and `ChatPage.xaml.cs` at their committed state for Task 2.

- [ ] **Step 1: Commit the plan**

```bash
git add docs/superpowers/plans/2026-10-08-chat-open-xaml-name-duplicate.md
env GIT_AUTHOR_NAME=vincenzosco GIT_AUTHOR_EMAIL=131459463+vincenzosco@users.noreply.github.com GIT_COMMITTER_NAME=vincenzosco GIT_COMMITTER_EMAIL=131459463+vincenzosco@users.noreply.github.com \
  git commit -m "docs: plan the chat open fix the attached debugger named"
```

- [ ] **Step 2: Run the gate on the tree as it stands**

Run, from `tools/`, the Global Constraints list without its last entry (all of it but `check-xaml-names.js`, which does not exist yet), then `node --test "tools/test/**/*.test.js"`.

Expected: thirteen `OK` lines and exit 0; 107 tests with the four `download.test.js` failures of the baseline.

- [ ] **Step 3: Commit the standing work**

```bash
git add WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/Pages/ChatsPage.xaml.cs \
  WhatsappApp/Services/CommunicationService.cs WhatsappApp/Services/SelfCheck.cs \
  .agents/skills/maintain-the-app/SKILL.md
env GIT_AUTHOR_NAME=vincenzosco GIT_AUTHOR_EMAIL=131459463+vincenzosco@users.noreply.github.com GIT_COMMITTER_NAME=vincenzosco GIT_COMMITTER_EMAIL=131459463+vincenzosco@users.noreply.github.com \
  git commit -m "fix: say where the log stops and stop the screen request from lying"
```

- [ ] **Step 4: Push**

```bash
git push origin master 2>&1 | grep -v "repository moved"
```

Expected: `master -> master`. `git status --short` is then empty.

---

### Task 2: A name cannot be used twice in one namescope, and the two audio buttons stop sharing one

**Files:**
- Create: `tools/check-xaml-names.js`
- Create: `tools/test/check-xaml-names.test.js`
- Create: `tools/xaml-names-fixtures/unique-names.xaml`
- Create: `tools/xaml-names-fixtures/duplicate-in-template.xaml`
- Create: `tools/xaml-names-fixtures/same-name-in-two-templates.xaml`
- Create: `tools/xaml-names-fixtures/nested-templates.xaml`
- Create: `tools/xaml-names-fixtures/commented-name.xaml`
- Modify: `WhatsappApp/Pages/ChatPage.xaml:234` and `WhatsappApp/Pages/ChatPage.xaml:429` (the two `x:Name` attributes and their `Click`)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs:740` (`PlayAudioButton_Click` and the doc comment above it)
- Modify: `README.md` and `README.it.md` (the fast-gate block)
- Modify: `.agents/skills/test-the-app/SKILL.md` and `.agents/skills/maintain-the-app/SKILL.md` (the gate list and the guard table)

**Interfaces:**
- Consumes: `WhatsappApp/Pages/ChatPage.xaml` and `ChatPage.xaml.cs` at the commit Task 1 leaves.
- Produces:
  - `tools/check-xaml-names.js` exporting `nameProblems(xaml, file) -> string[]`, a pure function of the two arguments (it reads no file of its own), and running as a guard when required as a script.
  - `ChatPage.xaml` with `x:Name="IncomingPlayAudioButton"` at line 234 wired to `Click="IncomingPlayAudioButton_Click"`, and `x:Name="OutgoingPlayAudioButton"` at line 429 wired to `Click="OutgoingPlayAudioButton_Click"`. No `PlayAudioButton` name anywhere.
  - `ChatPage.xaml.cs` with `private void OnPlayAudioClicked(object sender)` holding the body that was in `PlayAudioButton_Click`, and the two event handlers above as one-line delegations to it.

- [ ] **Step 1: Write the five fixtures**

`tools/xaml-names-fixtures/unique-names.xaml` - the legitimate shape: every name once.

```xml
<Page x:Name="Root">
    <Grid x:Name="Body">
        <DataTemplate>
            <Border x:Name="Bubble">
                <TextBlock x:Name="MessageText"/>
            </Border>
        </DataTemplate>
    </Grid>
</Page>
```

`tools/xaml-names-fixtures/duplicate-in-template.xaml` - the defect, at lines 4 and 5.

```xml
<Page x:Name="Root">
    <DataTemplate>
        <Grid>
            <Button x:Name="PlayAudioButton"/>
            <Button x:Name="PlayAudioButton"/>
        </Grid>
    </DataTemplate>
</Page>
```

`tools/xaml-names-fixtures/same-name-in-two-templates.xaml` - legal: two scopes, one name each.

```xml
<Page x:Name="Root">
    <DataTemplate>
        <Button x:Name="PlayAudioButton"/>
    </DataTemplate>
    <DataTemplate>
        <Button x:Name="PlayAudioButton"/>
    </DataTemplate>
</Page>
```

`tools/xaml-names-fixtures/nested-templates.xaml` - legal: the inner `ControlTemplate` is its own scope.

```xml
<Page x:Name="Root">
    <DataTemplate>
        <Button x:Name="Bubble">
            <Button.Template>
                <ControlTemplate>
                    <Border x:Name="Bubble"/>
                </ControlTemplate>
            </Button.Template>
        </Button>
    </DataTemplate>
</Page>
```

`tools/xaml-names-fixtures/commented-name.xaml` - legal: the commented one is not there.

```xml
<Page x:Name="Root">
    <DataTemplate>
        <!--
        <Button x:Name="PlayAudioButton"/>
        -->
        <Button x:Name="PlayAudioButton"/>
    </DataTemplate>
</Page>
```

- [ ] **Step 2: Write the failing test**

`tools/test/check-xaml-names.test.js`, with the fixture loader of `tools/test/check-handshake-answer.test.js`:

```js
const { nameProblems } = require('../check-xaml-names.js');
const fixture = (name) => fs.readFileSync(
  path.join(__dirname, '..', 'xaml-names-fixtures', name), 'utf8');
```

Six tests, each asserting on `nameProblems(xaml, file).join('\n')`:

1. `nomi unici: nessun problema` - `deepStrictEqual(nameProblems(fixture('unique-names.xaml'), 'unique-names.xaml'), [])`.
2. `lo stesso nome in due DataTemplate diversi e legittimo` - `deepStrictEqual(..., [])` on `same-name-in-two-templates.xaml`.
3. `un ControlTemplate dentro un DataTemplate ha il suo namescope` - `deepStrictEqual(..., [])` on `nested-templates.xaml`.
4. `un nome dentro un commento non conta` - `deepStrictEqual(..., [])` on `commented-name.xaml`.
5. `due volte lo stesso nome in un DataTemplate e un problema` - the problems of `duplicate-in-template.xaml` match `/PlayAudioButton/` and `/4 and 5/`.
6. `due volte lo stesso nome nella radice della pagina e un problema` - on the literal string `'<Page>\n<Button x:Name="A"/>\n<Button x:Name="A"/>\n</Page>'` with file `'inline.xaml'`, the problems match `/A/` and `/2 and 3/`.

- [ ] **Step 3: Run the test and watch it fail**

Run: `node --test tools/test/check-xaml-names.test.js`

Expected: FAIL - `Cannot find module '../check-xaml-names.js'`.

- [ ] **Step 4: Implement `tools/check-xaml-names.js`**

`nameProblems(xaml, file)`: one pass over the text with

```js
const TOKEN = /<!--[\s\S]*?-->|<(\/?)(DataTemplate|ControlTemplate|ItemsPanelTemplate)\b([^>]*)>|x:Name="([^"]*)"/g;
```

A comment match is skipped and does not touch the depth. A template tag pushes onto the scope stack, or pops on a closing tag; a self-closing tag (a `/` at the end of group 3) pushes and pops at once. An `x:Name` is recorded against the current stack path, with its line from a `lineAt(offset)` helper built once from the newline offsets of the text. A name appearing a second time at the same path is a problem; each later occurrence is reported against the first, with this exact wording:

```
${file}: x:Name="${name}" is used twice in one namescope (lines ${first} and ${line}): a template registers its names when an item is realized, so the second one throws XamlParseException 0x802B000A at run time and the screen that holds it closes instead of opening
```

`main()`: walk `WhatsappApp` skipping `obj` and `bin` - copy `check-actions.js`'s `walk` - read every `.xaml`, total the names, and either print the problems followed by `\n${n} name problem(s).` and exit 1, or print

```
OK: ${names} name(s) in ${files} XAML file(s), no name repeats inside one namescope.
```

and exit 0. Export `{ nameProblems }`; call `main()` behind `if (require.main === module)`.

- [ ] **Step 5: Run the test and watch it pass**

Run: `node --test tools/test/check-xaml-names.test.js`

Expected: six tests, all passing.

- [ ] **Step 6: Run the guard on the app and reproduce the crash on the PC**

Run: `node tools/check-xaml-names.js`

Expected: FAIL, exit 1, naming `WhatsappApp/Pages/ChatPage.xaml` and `x:Name="PlayAudioButton"` twice with lines **234** and **429** - the defect of the phone log, reproduced here in one second without a phone. Confirm the file count it walked excludes `obj/`: nine files, not thirteen.

- [ ] **Step 7: Give the two buttons two names in `WhatsappApp/Pages/ChatPage.xaml`**

At line 234, inside the bubble bound to `IsIncoming` (line 165):

```xml
<Button x:Name="IncomingPlayAudioButton" Grid.Column="0"
```

and its `Click` on the next lines becomes `Click="IncomingPlayAudioButton_Click"`.

At line 429, inside the bubble bound to `IsOutgoing` (line 360):

```xml
<Button x:Name="OutgoingPlayAudioButton" Grid.Column="0"
```

and its `Click` becomes `Click="OutgoingPlayAudioButton_Click"`.

`check-actions.js` requires a button named `X` to be wired to `X_Click`, which is why each gets its own handler rather than sharing the old one; the two handlers share the body instead.

- [ ] **Step 8: Give the handler the two names its buttons now carry**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, the doc comment above `PlayAudioButton_Click` (line 730) stays where it is and keeps saying what the tap does; add why there are two of them. Rename the body method and add the two handlers:

```csharp
        private void IncomingPlayAudioButton_Click(object sender, RoutedEventArgs e)
        {
            OnPlayAudioClicked(sender);
        }

        private void OutgoingPlayAudioButton_Click(object sender, RoutedEventArgs e)
        {
            OnPlayAudioClicked(sender);
        }

        private void OnPlayAudioClicked(object sender)
        {
            // the body that was in PlayAudioButton_Click, unchanged
        }
```

- [ ] **Step 9: Run the guard again**

Run: `node tools/check-xaml-names.js`

Expected: `OK: 120 name(s) in 9 XAML file(s), no name repeats inside one namescope.`

- [ ] **Step 10: Put the guard in the lists that claim to be the gate**

`README.md` and `README.it.md`: the `3. Make the change, then run the fast gate:` block lists eleven guards and stops before `check-diagnostics.js` and `check-handshake-answer.js`. Add all three, in the same order and with the same continuation lines in both languages, so the block matches the Global Constraints list. `check-docs.js` compares headings and not code blocks, so the pair stays in step; make the two edits in the same commit anyway.

`.agents/skills/test-the-app/SKILL.md`: add `node tools/check-xaml-names.js  # a x:Name is used once in one namescope` to the command block, add a row to the guard table naming what it catches (the duplicate that threw `XamlParseException 0x802B000A` when the first row of the conversation was realized), and set the `# the tools' own tests (104)` comment to the number of **passing** tests the next step prints - the comment there counts what passes, and the four `download.test.js` failures are not part of it.

`.agents/skills/maintain-the-app/SKILL.md`: add `&& node tools/check-xaml-names.js` to the fast-gate command in step 5.

- [ ] **Step 11: Run the full gate**

Run, from `tools/`, the fourteen guards of the Global Constraints list, then `node --test "tools/test/**/*.test.js"`, then `cd WhatsappBridge && npm test`.

Expected: fourteen `OK` lines and exit 0; 113 tests with 108 pass, the same 4 `download.test.js` failures, 1 skip; the adapter suite with its usual `pass`/`fail 0`.

- [ ] **Step 12: Build ARM and check it is clean**

```bash
(MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/Bin/MSBuild.exe" WhatsappApp.sln /t:Rebuild \
  /p:Configuration=Debug /p:Platform=ARM /v:m > .build.log 2>&1 &)
sleep 135
grep -cE "warning CS|error CS" .build.log   # must print 0
grep -c "successfully created" .build.log   # must print 1
git checkout -- WhatsappApp/Package.appxmanifest
rm .build.log
```

- [ ] **Step 13: Commit and push**

```bash
git add tools/check-xaml-names.js tools/test/check-xaml-names.test.js tools/xaml-names-fixtures \
  WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Pages/ChatPage.xaml.cs README.md README.it.md \
  .agents/skills/test-the-app/SKILL.md .agents/skills/maintain-the-app/SKILL.md
env GIT_AUTHOR_NAME=vincenzosco GIT_AUTHOR_EMAIL=131459463+vincenzosco@users.noreply.github.com GIT_COMMITTER_NAME=vincenzosco GIT_COMMITTER_EMAIL=131459463+vincenzosco@users.noreply.github.com \
  git commit -m "fix: the two audio buttons stop sharing one name, and a guard holds the line"
git push origin master 2>&1 | grep -v "repository moved"
```

---

### Task 3: On the phone, the chat opens

The fix is only proven where it failed. The artifact is the log of a run on the Lumia with no debugger attached, showing the conversation bound and no unhandled exception.

**Files:**
- Evidence: `.tools/phone-open/IsolatedStore/diag.log` (a snapshot, gitignored)
- Record: `.superpowers/sdd/2026-10-08-chat-open-xaml-name-duplicate/progress.md` (the ledger, gitignored)

**Interfaces:**
- Consumes: the ARM package Task 2 built, at `WhatsappApp/bin/ARM/Debug/WhatsappApp_1.0.0.0_Bundle/WhatsappApp_1.0.0.0_ARM_Debug.appx`.
- Produces: a phone log whose last lines are the conversation bound and nothing after it, and a ledger entry naming the run.

- [ ] **Step 1: Install and launch, without a debugger**

Ask the operator to unlock the phone screen first: `AppDeployCmd.exe` refuses `/launch` on a locked screen. Then, from `/c/Program Files (x86)/Microsoft SDKs/Windows Phone/v8.1/Tools/AppDeploy`:

```bash
env MSYS_NO_PATHCONV=1 ./AppDeployCmd.exe /install '<abs path to the .appx>' /targetdevice:de
env MSYS_NO_PATHCONV=1 ./AppDeployCmd.exe /launch 7ccc5b77-3cf2-4020-92a7-9542b250bb49 /targetdevice:de
```

Read the output: the tool exits 0 even when it prints `Errore:`. `/install` wipes the isolated storage, so the token and the caches are rebuilt from the hardware id on this launch.

- [ ] **Step 2: Open a chat and let it sit**

Ask the operator to open a conversation that has messages in it, wait for the messages to appear, then bring the app to the app list. Nothing is judged from the screen alone: the log decides.

- [ ] **Step 3: Pull the run and read it**

With the app closed, from `.../IsolatedStorageExplorerTool`:

```bash
rm -rf /c/Users/Vincenzo/Documenti/WhatsAppForWP/.tools/phone-open
env MSYS_NO_PATHCONV=1 ./ISETool.exe ts de 7ccc5b77-3cf2-4020-92a7-9542b250bb49 \
  'C:\Users\Vincenzo\Documenti\WhatsAppForWP\.tools\phone-open'
```

Take the snapshot at least a few minutes after the run: a snapshot taken while the log is being written catches a `diag.log.~tmp` instead.

- [ ] **Step 4: Assert on the log**

Expected, in `.tools/phone-open/IsolatedStore/diag.log`:

- `ok: opened chat <chat id>, memory <N> MB`, `ok: history done for <chat id>: <N> message(s)`, `ok: conversation bound <N> message(s)` and `ok: bound <N> rows, memory <N> MB` - the open completed;
- **no `App/unhandled` line, and no `XamlParseException`**, anywhere;
- a run whose end marker is present, or absent only because the app is still alive.

If it still closes, the last line of the log is the next site and names it: fix forward from there, and the loop of Task 2 Step 11 to Task 3 Step 4 repeats.

- [ ] **Step 5: Record the run in the ledger**

Append to `.superpowers/sdd/2026-10-08-chat-open-xaml-name-duplicate/progress.md`: the date and time of the run, the package path, the four `DIAG` lines of Step 4, the absence of `App/unhandled`, and the snapshot directory. No commit: the ledger and the snapshot are gitignored, and the fix is already pushed.
