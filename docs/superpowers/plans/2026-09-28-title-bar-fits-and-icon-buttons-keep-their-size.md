# The title bar that fits, and icon buttons that keep the size they declare

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The chat list shows the whole word "WhatsApp" in its header, and every icon button in the app occupies exactly the `Width`/`Height` its markup declares.

**Architecture:** The WP8.1 theme's default `Button` style imposes `MinWidth = PhoneButtonMinWidth = 109` and `MinHeight = PhoneButtonMinHeight = 57.5`, and a minimum beats a declared size in XAML layout: every icon button drawn as `Width="48" Height="48"` is really 109 x 57.5. Three of them in the chat list's header (4 columns: dots, `*` title, new chat, settings) take 343 of the 480 logical px, so the title column is 133 px and the title is clipped on the right. The fix is the one thing that makes `Width`/`Height` mean anything again: `MinWidth="0" MinHeight="0"` on the buttons that declare a fixed size, plus a guard in `tools/check-actions.js` so a fourth button cannot bring the clipping back. Nothing is resized or shrunk: the title keeps the 24 px the WP header uses and gets the 320 px column the layout was drawn for.

**Tech Stack:** WP8.1 XAML (WinRT `Windows.UI.Xaml`, C# 5), Node.js guards under `tools/` with `node:test`, the Parallels Windows 11 MSBuild gate.

## Global Constraints

- **C# 5 only**, exactly as the WP8.1 compiler accepts it. No C# 6+ syntax anywhere.
- **No `Segoe MDL2 Assets`**, no icon fonts. Icons are inline `Path` elements with the geometry inlined in `<Path.Data>` and the name in an `<!-- IconX -->` comment. `tools/check-icons.js` enforces it; no icon changes are part of this plan.
- **Every user string lives in both `.resw`** (`WhatsappApp/Strings/en-US/Resources.resw` and `it-IT/Resources.resw`). `node tools/check-resw.js --strict` must stay green with **125 keys**. This plan adds no string: the title is the brand name, hardcoded as it is today.
- **LF line endings, no BOM.** An edit tool may rewrite a file as CRLF+BOM; repair with `perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' <file>` before committing.
- **`tools/check-actions.js`**: a button named `X` must be wired to `X_Click`, and the three title-bar buttons must draw the icon declared in `TITLE_BAR` (`MoreButton: IconOverflow`, `NewChatButton: IconNewChat`, `SettingsButton: IconSettings`).
- **The WP8.1 phone theme is the authority for layout numbers**: `Button` = `MinWidth` 109, `MinHeight` 57.5, font `Segoe WP`; the chat list header is 480 grid units wide and 56 high, its title `FontSize="24" FontWeight="SemiBold"`.
- **The chat list title must stay 24 px.** It is the WP header size; making the text smaller to fit is the wrong fix, and a `Viewbox` that shrinks it is the same wrong fix with more machinery.
- **Everything is verified twice**: the fast gate (below) and the real WP8.1 build on the Parallels VM. A task is not done until both are green.
- **Fast gate** (from the repo root, each line must print `OK: ...`):
  ```bash
  node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict \
    && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js \
    && node tools/check-memory.js && node tools/check-actions.js \
    && node --test "tools/test/**/*.test.js"
  ```
  Expected counts as of the first commit: 36 C# files, 125 keys in each `.resw`, 23 inline icon Paths (14 distinct), 20 buttons, 41 tests in `tools/test` (**43** after Task 2).
- **Build gate** on the Parallels VM `Windows 11`, in this order (retry the identical MSBuild call once if it exits 255 with `PrlJob_GetRetCode: Invalid argument`):
  ```bash
  prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"
  prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"
  prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m /p:WarningLevel=4"
  ```
  Expected: `COPIA=0`, then `Avvisi: 0`, `Errori: 0` and `Your package has been successfully created.`
- **`robocopy` always copies the whole tree**: a file as its third argument fails with `ERRORE: parametro non valido #3`.
- **Commits**: one per task, explicit paths only (`git add <file> ...`, never `-A`; `WhatsappApp/Package.appxmanifest` has a deliberate uncommitted diff and `.DS_Store` files are untracked noise - leave both alone). Commit messages carry **no apostrophes**. Commit footer: a `Generated with Codebuff` line prefixed with the robot emoji, then `Co-Authored-By: Codebuff <noreply@codebuff.com>`. The commands below write the footer without the emoji, because `tools/check-docs.js` rejects any emoji in a markdown file; add it when the commit is actually made.
- **No emoji in any `.md`**, except the warning sign U+26A0 (`tools/check-docs.js`).
- **Push once**, at the end of Task 3. Nothing under `WhatsappBridge/` changes, so the Docker mirror is untouched by this plan.

---

## The bug, in numbers

The phone kit's `C:\Program Files (x86)\Windows Phone Kits\8.1\Include\abi\Xaml\Design\generic.xaml` (the default `Button` style) sets:

```xml
<Setter Property="MinHeight" Value="{ThemeResource PhoneButtonMinHeight}" />
<Setter Property="MinWidth" Value="{ThemeResource PhoneButtonMinWidth}" />
```

and `themeresources.xaml` gives `PhoneButtonMinWidth = 109`, `PhoneButtonMinHeight = 57.5`.

| | declared | really drawn | column it needs |
| --- | --- | --- | --- |
| `MoreButton` | 48 x 48 | 109 x 57.5 | 113 (109 + margin 4) |
| title `WhatsApp` | 24 px text | clipped | the rest: 480 - 343 = 137, minus its 4 px margin = **133** |
| `NewChatButton` | 48 x 48 | 109 x 57.5 | 117 (109 + margin 8) |
| `SettingsButton` | 48 x 48 | 109 x 57.5 | 113 (109 + margin 4) |

Before the three-dots button existed the header had two buttons (218 px) and 246 px for the title, which is why the clip appeared only now. After the fix the three buttons take 160 px and the title column is **320 px**: the same header, at the size it was drawn for.

The same 109 px clamp is why the chat page's input row (`Auto / * / Auto`: attach, text box, send) loses 122 px of text box, and why the back arrows of the chat and connection pages sit in the middle of a 109 px box instead of at the left edge. `ClearImageButton` (44), `VideoCloseButton` (48), `AttachButton` (48) and `SendButton` (48) are the other fixed-size buttons in the same situation. Buttons that declare only a `Height` (the connection page's Connect, Disconnect, login buttons) are also stretched to 57.5, but they are full width and nothing next to them is squeezed, so they are deliberately out of scope here.

## File Structure

| File | Responsibility after the change |
| --- | --- |
| `WhatsappApp/Pages/ChatsPage.xaml` | The header's three icon buttons declare `MinWidth="0" MinHeight="0"`, so the `*` title column gets its 320 px and the title fits. |
| `tools/check-actions.js` | Keeps the name/handler/icon agreement, and refuses a button that declares a size the theme minimums would override. |
| `tools/test/check-actions.test.js` | The guard's own tests. |
| `WhatsappApp/Pages/ChatPage.xaml` | The five fixed-size icon buttons (back, clear image, attach, send, close video) keep their declared size, so the message box is 376 px wide and the icons sit at the edges. |
| `WhatsappApp/Pages/ConnectionPage.xaml` | The back button keeps its 48 px, so the page title starts at x=56 instead of x=117. |
| `WhatsappApp/Controls/SectionNav.xaml` | `NavIconButtonStyle` keeps the 48 px height it sets, instead of overflowing its 48 px bar by 4.75 px top and bottom. |
| `.agents/skills/maintain-the-app/SKILL.md` | The gotcha: a WP8.1 theme minimum overrides a declared size. |
| `.agents/skills/test-the-app/SKILL.md` | The guard's row in the table, the refreshed test count, and the on-device checks for the header and the input row. |

No C# file, no `.resw`, no `csproj` and nothing under `WhatsappBridge/` is touched.

---

### Task 1: the chat list title gets its room back

**Files:**
- Modify: `WhatsappApp/Pages/ChatsPage.xaml:47-141` (the three title-bar buttons)
- Modify: `tools/check-actions.js` (`titleBarProblems`, and the comment above `TITLE_BAR`)
- Test: `tools/test/check-actions.test.js`

**Interfaces:**
- Consumes: `TITLE_BAR` (unchanged: `MoreButton: IconOverflow`, `NewChatButton: IconNewChat`, `SettingsButton: IconSettings`), `attribute(tag, name)`.
- Produces: `titleBarProblems(xaml, file) -> string[]`, still reporting a title-bar button that draws the wrong icon, now also reporting one that does not declare `MinWidth="0"` and `MinHeight="0"`. `buttonProblems` is unchanged by this task.

- [x] **Step 1: Write the failing test**

In `tools/test/check-actions.test.js`, the existing test `l'icona scambiata sulla barra del titolo si segnala` asserts that a title-bar button with the wrong icon yields exactly one problem. Its fixture declares neither minimum, so from now on it would yield three; add them to the fixture:

```js
test("l'icona scambiata sulla barra del titolo si segnala", () => {
  const xaml = '<Button x:Name="NewChatButton" MinWidth="0" MinHeight="0" ' +
    'Click="NewChatButton_Click">\n' +
    '  <!-- IconSettings -->\n</Button>';
  const problems = actions.titleBarProblems(xaml, FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /IconNewChat/);
});
```

Then append the new test:

```js
test('un pulsante della barra del titolo senza minimi si segnala', () => {
  const xaml = '<Button x:Name="MoreButton" MinWidth="0" MinHeight="0" Click="MoreButton_Click">\n' +
    '  <!-- IconOverflow -->\n</Button>\n' +
    '<Button x:Name="NewChatButton" Click="NewChatButton_Click">\n' +
    '  <!-- IconNewChat -->\n</Button>';
  const problems = actions.titleBarProblems(xaml, FILE);
  assert.strictEqual(problems.length, 2);
  assert.ok(problems.every((problem) => problem.includes('NewChatButton')));
});
```

- [x] **Step 2: Run it and watch it fail**

Run: `node --test tools/test/check-actions.test.js`
Expected: FAIL on the new test - `expected 0 to equal 2`, because nothing checks the minimums yet.

- [x] **Step 3: Write the rule**

In `tools/check-actions.js`, inside `titleBarProblems`, after the icon comparison and inside the same loop, add:

```js
    // Il tema di WP8.1 da' a ogni Button un MinWidth di 109 e un MinHeight di
    // 57.5, e un minimo scavalca la misura dichiarata: con Width="48" e senza
    // MinWidth="0" ogni pulsante occupa 109 px, la colonna del titolo si
    // stringe e "WhatsApp" arriva sul telefono tagliato a destra.
    if (attribute(m[1], 'MinWidth') !== '0') {
      problems.push(`${file}: ${name} does not set MinWidth="0", so the WP8.1 theme ` +
        'minimum (109) overrides its Width and the button takes the room of the title');
    }
    if (attribute(m[1], 'MinHeight') !== '0') {
      problems.push(`${file}: ${name} does not set MinHeight="0", so the WP8.1 theme ` +
        'minimum (57.5) overrides its Height and the button is taller than the bar it sits in');
    }
```

- [x] **Step 4: Run the test and the real guard**

Run: `node --test tools/test/check-actions.test.js`
Expected: PASS, 5 tests.

Run: `node tools/check-actions.js`
Expected: FAIL with 6 problems, all for `Pages/ChatsPage.xaml` (`MoreButton`, `NewChatButton`, `SettingsButton`, two lines each) and `6 action problem(s).` - the guard has just caught the bug on the device's behalf.

- [x] **Step 5: Give the three buttons their declared size**

In `WhatsappApp/Pages/ChatsPage.xaml`, replace the button declarations (keep the `Path` bodies untouched). First, above `MoreButton`, then on all three:

```xml
            <!-- Il minimo del tema (Button: 109 x 57.5) scavalca Width/Height:
                 senza MinWidth/MinHeight a 0 ognuno di questi tre pulsanti
                 occupa 109 px, la colonna del titolo si stringe e "WhatsApp"
                 finisce tagliato a destra. tools/check-actions.js lo impedisce. -->
            <Button x:Name="MoreButton" Grid.Column="0"
                    Background="Transparent" BorderThickness="0" Padding="0"
                    MinWidth="0" MinHeight="0"
                    Width="48" Height="48" Margin="4,0,0,0" Click="MoreButton_Click">
```

```xml
            <Button x:Name="NewChatButton" Grid.Column="2"
                    Background="Transparent" BorderThickness="0" Padding="0"
                    MinWidth="0" MinHeight="0"
                    Width="48" Height="48" Margin="0,0,8,0" Click="NewChatButton_Click">
```

```xml
            <Button x:Name="SettingsButton" Grid.Column="3"
                    Background="Transparent" BorderThickness="0" Padding="0"
                    MinWidth="0" MinHeight="0"
                    Width="48" Height="48" Margin="0,0,4,0" Click="SettingsButton_Click">
```

- [x] **Step 6: Run the guard again**

Run: `node tools/check-actions.js`
Expected: `OK: 20 button(s), name/handler/icon agree.`
Also run: `node tools/check-icons.js` - Expected: the same `OK: ... 23 inline icon Path(s) ...` as before, because the `Path` elements did not move.

- [x] **Step 7: Run the whole fast gate**

Run the nine commands of the Global Constraints.
Expected: every line `OK: ...`, the numbers unchanged except `43 tests` only if Task 2 is already done - at this point `41 tests in tools/test`, and `node tools/check-resw.js --strict` still says 125 keys.

- [x] **Step 8: Build it for the phone**

Run the three commands of the build gate, the last one with `/p:WarningLevel=4`.
Expected: `COPIA=0`, then `Avvisi: 0`, `Errori: 0`, `Your package has been successfully created.`

- [x] **Step 9: Commit**

```bash
git add WhatsappApp/Pages/ChatsPage.xaml tools/check-actions.js tools/test/check-actions.test.js
git commit -m "$(cat <<'EOF'
fix: the chat list title is no longer clipped by the theme button minimum

WP8.1 gives every Button a MinWidth of 109 and a MinHeight of 57.5, and a
minimum beats the declared size: the three 48 px icon buttons of the header
were really 109 px each, eating 343 of the 480 px and leaving the title with
133 px, which cut it on the right. With MinWidth/MinHeight at 0 the header is
160 px of buttons and a 320 px title column.

tools/check-actions.js now refuses a title-bar button that does not declare
the two minimums, so the fourth button cannot bring the clip back.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
```

---

### Task 2: the same defect outside the title bar

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml:47-53`, `:389-392`, `:425-429`, `:472-478`, `:518-523`
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml:26-31`
- Modify: `WhatsappApp/Controls/SectionNav.xaml:6-12`
- Modify: `tools/check-actions.js` (`buttonProblems` gains the rule, `titleBarProblems` loses it)
- Test: `tools/test/check-actions.test.js`

**Interfaces:**
- Consumes: `attribute(tag, name)`, `TITLE_BAR`.
- Produces: `buttonProblems(xaml, file) -> string[]` reporting, for every `<Button>` with an `x:Name` and a `Click`, the name/handler mismatch as before **and** a declared `Width` without `MinWidth="0"`/`MinHeight="0"`. `titleBarProblems(xaml, file) -> string[]` goes back to checking only the icon, because the size rule is no longer a title-bar rule.

- [x] **Step 1: Replace the narrow test with the general ones**

In `tools/test/check-actions.test.js`, delete the test added in Task 1 (`un pulsante della barra del titolo senza minimi si segnala`) - the general rule below covers it - and append:

```js
test('un pulsante a misura fissa senza minimi si segnala', () => {
  const xaml = '<Button x:Name="SendButton" Width="48" Height="48" Click="SendButton_Click">\n' +
    '  <!-- IconSend -->\n</Button>';
  const problems = actions.buttonProblems(xaml, FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /SendButton/);
  assert.match(problems[0], /MinWidth/);
});

test('un pulsante a misura fissa con i minimi a zero passa', () => {
  const xaml = '<Button x:Name="SendButton" Width="48" Height="48" MinWidth="0" MinHeight="0" ' +
    'Click="SendButton_Click">\n  <!-- IconSend -->\n</Button>';
  assert.deepStrictEqual(actions.buttonProblems(xaml, FILE), []);
});

test('un pulsante che dichiara solo Height resta fuori dalla regola', () => {
  const xaml = '<Button x:Name="ActionButton" Height="52" Click="ActionButton_Click">\n</Button>';
  assert.deepStrictEqual(actions.buttonProblems(xaml, FILE), []);
});
```

- [x] **Step 2: Run them and watch the first one fail**

Run: `node --test tools/test/check-actions.test.js`
Expected: FAIL on `un pulsante a misura fissa senza minimi si segnala` - `expected 0 to equal 1`. The other two pass, because there is no rule yet.

- [x] **Step 3: Move the rule into `buttonProblems`**

In `tools/check-actions.js`, remove the two `MinWidth`/`MinHeight` checks (and their comment) that Task 1 added to `titleBarProblems`, leave that function checking the icon only, and add to `buttonProblems`, inside the loop after the handler check:

```js
    // Il tema di WP8.1 impone a ogni Button un MinWidth di 109 e un MinHeight
    // di 57.5, e un minimo scavalca la misura dichiarata. Un pulsante a misura
    // fissa - uno che dichiara Width - deve quindi azzerare i minimi, o la sua
    // colonna si allarga oltre il disegnato e stringe quello che sta accanto:
    // era il titolo "WhatsApp" della lista chat, ed era la riga di scrittura di
    // una chat, larga 122 px in meno del previsto.
    const width = attribute(m[1], 'Width');
    if (width && (attribute(m[1], 'MinWidth') !== '0' || attribute(m[1], 'MinHeight') !== '0')) {
      problems.push(`${file}: ${name} declares Width="${width}" without MinWidth="0" and ` +
        'MinHeight="0": the WP8.1 theme minimums (109 x 57.5) override the declared size, ' +
        'the column grows and the text beside it is squeezed');
    }
```

- [x] **Step 4: Run the tests and the guard**

Run: `node --test tools/test/check-actions.test.js`
Expected: PASS, 7 tests.

Run: `node tools/check-actions.js`
Expected: FAIL with 6 problems, one each for `Pages/ChatPage.xaml` (`BackButton`, `ClearImageButton`, `AttachButton`, `SendButton`, `VideoCloseButton`) and `Pages/ConnectionPage.xaml` (`BackButton`).

- [x] **Step 5: Give the other fixed-size buttons their declared size**

In `WhatsappApp/Pages/ChatPage.xaml`, add `MinWidth="0" MinHeight="0"` to these five buttons, keeping everything else identical:

```xml
            <Button x:Name="BackButton" Grid.Column="0"
                    Background="Transparent"
                    MinWidth="0" MinHeight="0"
                    Width="48" Height="48" Margin="0,4,0,0" Padding="0"
                    Click="BackButton_Click"
                    BorderThickness="0">
```

```xml
                <Button x:Name="ClearImageButton" Grid.Column="2"
                        Background="Transparent"
                        MinWidth="0" MinHeight="0"
                        Width="44" Height="44" BorderThickness="0" Padding="0"
                        Click="ClearImageButton_Click">
```

```xml
                <Button x:Name="AttachButton" Grid.Column="0"
                        Background="Transparent"
                        MinWidth="0" MinHeight="0"
                        Width="48" Height="48" BorderThickness="0" Padding="0"
                        Margin="0,4,0,4"
                        Click="AttachButton_Click">
```

```xml
                <Button x:Name="SendButton" Grid.Column="2"
                        Background="{StaticResource WhatsAppAccentBrush}"
                        Foreground="White"
                        MinWidth="0" MinHeight="0"
                        Width="48" Height="48"
                        Margin="0,4,8,4"
                        BorderThickness="0" Padding="0"
                        Click="SendButton_Click">
```

```xml
            <Button x:Name="VideoCloseButton"
                    Background="Transparent"
                    MinWidth="0" MinHeight="0"
                    Width="48" Height="48"
                    Margin="0,8,8,0"
                    HorizontalAlignment="Right" VerticalAlignment="Top"
                    BorderThickness="0" Padding="0"
                    Click="VideoCloseButton_Click">
```

In `WhatsappApp/Pages/ConnectionPage.xaml`:

```xml
            <Button x:Name="BackButton" Grid.Column="0"
                    Background="Transparent"
                    MinWidth="0" MinHeight="0"
                    Width="48" Height="48" Margin="0,4,0,0" Padding="0"
                    Click="BackButton_Click"
                    BorderThickness="0">
```

In `WhatsappApp/Controls/SectionNav.xaml`, the three nav buttons get their size from a style, so the style is where the minimum goes:

```xml
        <Style x:Key="NavIconButtonStyle" TargetType="Button">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Padding" Value="0"/>
            <Setter Property="MinWidth" Value="0"/>
            <Setter Property="MinHeight" Value="0"/>
            <Setter Property="Height" Value="48"/>
        </Style>
```

- [x] **Step 6: Run the guard and the icon guard**

Run: `node tools/check-actions.js`
Expected: `OK: 20 button(s), name/handler/icon agree.`
Run: `node tools/check-icons.js`
Expected: unchanged `OK: ...` line - the `Path` elements and the `<!-- IconX -->` comments did not move, only attributes of the buttons around them.

- [x] **Step 7: Run the whole fast gate**

Run the nine commands of the Global Constraints.
Expected: every line `OK: ...`, with `43 tests in tools/test`.

- [x] **Step 8: Build it for the phone**

Run the three commands of the build gate, the last one with `/p:WarningLevel=4`.
Expected: `COPIA=0`, `Avvisi: 0`, `Errori: 0`, `Your package has been successfully created.`

- [x] **Step 9: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Pages/ConnectionPage.xaml WhatsappApp/Controls/SectionNav.xaml tools/check-actions.js tools/test/check-actions.test.js
git commit -m "$(cat <<'EOF'
fix: every fixed-size icon button keeps the size it declares

The same WP8.1 theme minimums (109 x 57.5) were also overriding the chat page
back/clear/attach/send/close buttons, the connection page back button and the
three navigation buttons: the message box was 122 px narrower than drawn and
the back arrows sat in the middle of a 109 px box.

The rule moves out of the title bar and into buttonProblems, so it now covers
any button that declares a Width, and the title-bar-only test is replaced by
the general ones.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
```

---

### Task 3: write down what the phone taught the project

**Files:**
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (the `## Known gotchas` list, after the tile gotchas)
- Modify: `.agents/skills/test-the-app/SKILL.md` (the counts line, the guard table row, the on-device checklist)
- Add: `docs/superpowers/plans/2026-09-28-title-bar-fits-and-icon-buttons-keep-their-size.md`

**Interfaces:**
- Consumes: everything the two previous tasks produced.
- Produces: nothing executable; the docs the next agent reads before touching a header.

- [x] **Step 1: The gotcha**

In `.agents/skills/maintain-the-app/SKILL.md`, in `## Known gotchas`, add:

```markdown
- **On WP8.1 a theme minimum overrides the size you declare.** The default
  `Button` style sets `MinWidth = PhoneButtonMinWidth = 109` and
  `MinHeight = PhoneButtonMinHeight = 57.5` (phone kit's `generic.xaml` and
  `themeresources.xaml`), and a minimum beats `Width`/`Height`: an icon button
  written `Width="48" Height="48"` is drawn 109 x 57.5. A fixed-size button must
  therefore also declare `MinWidth="0" MinHeight="0"`, or the `Auto` column
  around it grows past the drawing and squeezes its neighbour - that is how the
  chat list's "WhatsApp" title lost its last letters when the three-dots button
  was added. `tools/check-actions.js` refuses a button that declares a `Width`
  without the two minimums. Buttons that declare only a `Height` (the connection
  page's Connect/Disconnect/login buttons) are outside that rule on purpose: they
  are full width and nothing sits next to them.
```

- [x] **Step 2: The guard's contract and the counts**

In `.agents/skills/test-the-app/SKILL.md`:

Change the counts line to `... 20 buttons, 133 adapter tests, 43 tests in tools/test.` (the exact number `node --test "tools/test/**/*.test.js"` prints at the end of Task 2).

Extend the `check-actions.js` row of the "What they catch" table, after `draws its icon`:

```markdown
  its declared size, which the WP8.1 theme minimums (109 x 57.5) would override - the reason the chat list title was clipped. |
```

- [x] **Step 3: The on-device checks**

In the same file, in the on-device checklist, add after item 32 (the title-bar item):

```markdown
32b. Title bar: the whole word `WhatsApp` is on screen, with no letters missing on
    the right, the three dots sit at the left edge and the two icons at the right.
    If the title is cut, the build on the phone is not the one in this tree:
    `node tools/check-actions.js` says what the source does.
```

and in the same list, extend the item about writing a message, or add after it:

```markdown
    and the input row is full width: the photo button sits at the left edge, the
    send button at the right edge, and the text box between them is about 376 px
    wide, not 254.
```

- [x] **Step 4: Tick this plan and describe where it changed**

In `docs/superpowers/plans/2026-09-28-title-bar-fits-and-icon-buttons-keep-their-size.md`, tick every executed step, and append at the end:

```markdown
## What execution changed about this plan

(the tasks as they ran, the test count the suite really printed, and anything the
plan got wrong about the theme or the layout)
```

- [x] **Step 5: Run the whole fast gate**

Run the nine commands of the Global Constraints, plus `cd WhatsappBridge && npm test` for the 133 adapter tests.
Expected: every line `OK: ...`; `node tools/check-docs.js` may now count one more `.md` file (the plan itself), which is fine as long as its `OK:` line appears and the docs pair count stays 2.

- [x] **Step 6: Commit and push**

```bash
git add .agents/skills/maintain-the-app/SKILL.md .agents/skills/test-the-app/SKILL.md docs/superpowers/plans/2026-09-28-title-bar-fits-and-icon-buttons-keep-their-size.md
git commit -m "$(cat <<'EOF'
docs: the theme minimum that hid the title, and the guard that stops it

The gotcha explains why Width/Height were ignored on WP8.1 and which buttons
must zero the minimums; the test skill gains the guard row, the refreshed test
count and two on-device checks for the header and the input row.

Generated with Codebuff
Co-Authored-By: Codebuff <noreply@codebuff.com>
EOF
)"
git push origin master
```

---

## Self-review

**Spec coverage.** The request was "the WhatsApp title is not fully visible, make it fit the size", and the answer is Task 1: the header's three buttons stop occupying 109 px each, the title column goes from 133 px to 320 px, the title stays at the WP header's 24 px. Task 2 covers the same defect where it squeezes something else (the chat page's message box and back arrow, the connection page's title, the navigation buttons) - a reviewer can reject Task 2 and keep Task 1. Task 3 is the documentation and the plan file, which this repo requires with every change.

**Deliberate exclusions, so nobody reads them as oversights.** The title keeps `FontSize="24"`; no `TextTrimming`, no `Viewbox`, no smaller font - the words are meant to be readable, not shrunk. Buttons that declare only a `Height` are left as they are, with the reason written in the gotcha. No `.resw` key is added because the title is the brand name.

**Placeholder scan.** Every step carries the exact XAML, the exact JavaScript, the exact commands and the expected output. The only bracketed text is the one in Step 4 of Task 3, which is the body of the execution note the agent writes with what it learned.

**Type consistency.** `titleBarProblems(xaml, file) -> string[]` and `buttonProblems(xaml, file) -> string[]` keep their names and shapes; the rule moves between them in Task 2, which is stated in that task's `Interfaces` block. `attribute(tag, name)` is used, never re-implemented. `TITLE_BAR` is not changed by any task.

**The one thing the plan cannot test here.** How the header looks is a device fact: the macOS machine has no WP8.1 renderer. The numbers above come from the phone kit's own theme files, and the build gate proves the XAML compiles, but the last look is on the phone (Step 3 of Task 3 records what to look for).

## Execution Handoff

Repo rule: **inline execution in this same session, task by task, to the final push.** The plan is executed in order, each task ending with its commit, and the execution note in Step 4 of Task 3 records every place where reality differed from this document.

## What execution changed about this plan

Executed in one session on 2026-09-28, in this order: `701d68b` (Task 1), `55c523b` (Task 2), then this documentation commit.

**The diagnosis held exactly.** The guard found 6 problems in Task 1 (the three title-bar buttons, `MinWidth` and `MinHeight` each) and 6 in Task 2 (`ChatPage`'s `BackButton`, `ClearImageButton`, `AttachButton`, `SendButton`, `VideoCloseButton` and `ConnectionPage`'s `BackButton`), which is what this document predicted. Both builds on the VM ended with `Your package has been successfully created.`, `Avvisi: 0`, `Errori: 0`.

**The test counts came out as planned:** 40 tests before, 41 after Task 1, 43 after Task 2. The rest of the fast gate is unchanged: 36 C# files, 125 keys, 23 inline icon Paths (14 distinct), 20 buttons, 133 adapter tests, 2 doc pairs. `check-docs.js` now counts 38 `.md` files instead of 37, because this plan is one of them.

**Five things the plan got wrong or did not know, all fixed in place:**

1. The plan's Task 1 did not mention that the existing test `l'icona scambiata sulla barra del titolo si segnala` asserts *one* problem for a title-bar button. Its fixture declares no minimums, so the new rule would have made it three. The fixture gained `MinWidth="0" MinHeight="0"` before the new test was written.
2. The commit-message blocks were written with the robot emoji in them, and `tools/check-docs.js` rejects any emoji in a markdown file. The plan was corrected to the convention the earlier plans use: the commands write the footer without the emoji, and it is added when the commit is made.
3. Task 3 said to extend the `check-actions.js` row of the "What they catch" table. That table has rows only for `check-csharp5.js`, `check-icons.js`, `check-resw.js`, `check-docs.js` and `check-framing.js`: there was no `check-actions.js` row to extend, so one was added, after `check-framing.js`.
4. The plan said to insert the on-device checks as `32b` in the middle of the checklist. The list ends at 50 and the project appends new checks at the end (that is how 47-50 arrived), so they became **51** and **52**.
5. `/v:m` on this MSBuild 12 prints no `Avvisi`/`Errori` summary at all here, so the build gate's command would have shown only `Your package has been successfully created.` and no warning count. The count is visible with `/v:n`; that is the verbosity the runs used. The step's expected output was right, its command was not.

**One thing a reader of the log will notice.** From `701d68b` on, the commits carry a `Generated with Codebuff` footer; the older commits in this repository do not. That is this session's commit rule, not a project rule.

**What was not verified, and cannot be from here.** How the header actually looks. The numbers come from the phone kit's own theme files and the layout markup, and the build proves the XAML compiles into a package, but there is no WP8.1 renderer on macOS: the last look is check 51 and 52 on the phone.
