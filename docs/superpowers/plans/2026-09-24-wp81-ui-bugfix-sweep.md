# WP8.1 App Bugfix Sweep Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove every bug found in a full read of the WP8.1 app: icons that never render because they use a font the platform does not have, a floating button placed in the wrong grid row, duplicated chat messages, timestamps shown in UTC, chat images that never decode, and several smaller correctness/UX defects.

**Architecture:** Two root causes dominate. (1) Every icon button in `MainPage.xaml`, `ChatPage.xaml` and `ConnectionPage.xaml` renders a glyph with `FontFamily="Segoe MDL2 Assets"` — that font shipped with **Windows 10**, not with Windows Phone 8.1 (which has *Segoe WP* and *Segoe UI Symbol*), so the glyphs fall back and are invisible. The fix replaces every glyph with a **vector `Path`** whose `PathGeometry` is defined once in `App.xaml`, colour-and-scale-agnostic and dependency-free. (2) Chat messages are inserted into the same `ObservableCollection` twice — once by the page and once by `DataService` — producing duplicate bubbles. The fix gives `DataService` sole ownership of insertion. Chat images move from a synchronous `IValueConverter` (which disposes the stream before `BitmapImage` decodes it) to an awaited decode that exposes a ready `BitmapImage` on the model.

**Tech Stack:** C# 5 / XAML for Windows Phone 8.1 (WinRT XAML, `{StaticResource}` + `PathGeometry`), Node.js 18+ for the repo guard scripts, ImageMagick 7 only for the optional ASCII icon preview.

## Global Constraints

- The app compiles with the **C# 5 compiler** and the **WP8.1 WinRT API subset**: no C# 6/7 syntax, no Windows 8.1/10-only API. `node tools/check-csharp5.js` must stay green after every task, and XAML must not reference `Segoe MDL2 Assets`.
- Icons are **vector paths** with a 24x24 coordinate space, defined exactly once in `WhatsappApp/App.xaml` as `<PathGeometry x:Key="IconX" Figures="…"/>` and consumed as `Data="{StaticResource IconX}"`. The `Figures` strings below were rendered with ImageMagick and visually verified (magnifier, vertical dots, sliders, chat bubble, person, clock, handset, camcorder, photo, paper plane, X, +, chevron) — do not "improve" them by hand.
- Each `Path` gets `Width`/`Height` (24 in header/bars, 48 in the empty state) and an explicit `Stroke`/`StrokeThickness` (`StrokeThickness="2"`, except `IconCalls` = 3 and the filled `IconMore`/`IconSend` which use `Fill` and no stroke). Colours: `White` on the teal header, `#FF128C7E` for the active bar item, `#FF808080` for inactive ones.
- Controls whose feature does not exist yet (main tabs, bottom bar, voice/video call) get `IsEnabled="False"`: a dimmed control is honest, a control that silently does nothing is a bug.
- No behaviour change beyond the listed fixes; UI strings stay Italian; the wire protocol, the AES passphrase and `WhatsappBridge/**` are untouched (29/29 `node --test` tests must stay green).
- The project file lists sources explicitly: **no new `.xaml`/`.cs` files are added to `WhatsappApp`**, so `WhatsappApp.csproj` needs no edit. (`tools/check-icons.js` lives outside the solution.)
- Authoritative gate: the user's `msbuild WhatsappApp.sln /p:Configuration=Debug /p:Platform=x86` on Windows.

## File Structure

| File | Responsibility | Change |
| --- | --- | --- |
| `tools/check-icons.js` | **Create.** Guard: no `Segoe MDL2 Assets` in XAML; every `{StaticResource IconX}` used is defined in `App.xaml`; optional ASCII preview of each geometry. | new |
| `WhatsappApp/App.xaml` | App-level resources: colours, styles, **icon geometries**. | +13 `PathGeometry` |
| `WhatsappApp/MainPage.xaml` | Chat list, header, tabs, bottom bar, FAB, empty state. | icons, FAB row, empty state, disabled dead controls |
| `WhatsappApp/MainPage.xaml.cs` | Chat list code-behind. | empty-state toggling |
| `WhatsappApp/Pages/ChatPage.xaml` | Chat view + input bar. | icons, image binding → `MediaImage` |
| `WhatsappApp/Pages/ChatPage.xaml.cs` | Chat code-behind. | no double insert, decode sent image, `Failed` status when offline |
| `WhatsappApp/Pages/ConnectionPage.xaml` | Setup/login UI. | back icon |
| `WhatsappApp/Pages/ConnectionPage.xaml.cs` | Setup/login code-behind. | clear the pairing code once connected |
| `WhatsappApp/Models/ChatMessage.cs` | Message DTO + bindable state. | local-time formatting, `MediaImage` + `LoadMediaImageAsync()` |
| `WhatsappApp/Services/DataService.cs` | Contacts/messages store. | await the incoming image decode |
| `WhatsappApp/Services/CommunicationService.cs` | Encrypted TCP transport. | dispatcher must not run an action twice |
| `WhatsappApp/Converters/Converters.cs` | XAML value converters. | delete the broken base64 converter, fix the colour hash |

Tasks 2 → 5 change only XAML/markup and can be reviewed together as "the icon work"; 6 → 12 are one defect each.

---

### Task 1: Icon/`Segoe MDL2` guard script

**Files:**
- Create: `tools/check-icons.js`

**Interfaces:**
- Consumes: `WhatsappApp/App.xaml` (resource keys), every `WhatsappApp/**/*.xaml` file (references).
- Produces: `node tools/check-icons.js` → exit `0` printing `OK: N icon(s) defined, M reference(s) resolved.`; exit `1` listing `<file>:<line>: <problem>` for: `Segoe MDL2 Assets` anywhere, a `{StaticResource IconX}` that is not defined, or a defined `IconX` that is never used. `--preview` additionally renders each geometry through ImageMagick (`magick`) and prints ASCII art; without ImageMagick the preview is skipped with a notice, never an error.

- [ ] **Step 1: Write the guard script**

```js
#!/usr/bin/env node
/**
 * tools/check-icons.js
 *
 * Guard for the WP8.1 app icons. "Segoe MDL2 Assets" is a Windows 10 font and
 * is NOT present on Windows Phone 8.1, so an icon button using it renders
 * nothing. All icons live in App.xaml as PathGeometry resources and are
 * consumed as Data="{StaticResource IconX}".
 *
 * Usage:
 *   node tools/check-icons.js            # references + font check
 *   node tools/check-icons.js --preview  # + ASCII preview (needs ImageMagick)
 */
'use strict';

const { execFileSync, spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');
const APP_XAML = path.join(APP, 'App.xaml');
const PREVIEW = process.argv.includes('--preview');

function walk(dir, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'obj' || entry.name === 'bin') continue;
      walk(path.join(dir, entry.name), out);
    } else if (entry.name.endsWith('.xaml')) {
      out.push(path.join(dir, entry.name));
    }
  }
  return out;
}

const appXaml = fs.readFileSync(APP_XAML, 'utf8');
const defined = new Map();
for (const m of appXaml.matchAll(/<PathGeometry\s+x:Key="([^"]+)"\s+Figures="([^"]+)"/g)) {
  defined.set(m[1], m[2]);
}

const files = walk(APP, []).filter((f) => f !== APP_XAML);
const problems = [];
const used = new Set();

for (const file of files) {
  const rel = path.relative(ROOT, file);
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
  lines.forEach((line, i) => {
    if (/Segoe MDL2 Assets/.test(line)) {
      problems.push(`${rel}:${i + 1}: uses "Segoe MDL2 Assets", which WP8.1 does not have -> ${line.trim()}`);
    }
    for (const m of line.matchAll(/\{StaticResource (Icon[A-Za-z]+)\}/g)) {
      used.add(m[1]);
      if (!defined.has(m[1])) problems.push(`${rel}:${i + 1}: {StaticResource ${m[1]}} is not defined in App.xaml`);
    }
  });
}

for (const key of defined.keys()) {
  if (!used.has(key)) problems.push(`WhatsappApp/App.xaml: ${key} is defined but never used`);
}

if (problems.length) {
  console.log(problems.join('\n'));
  console.log(`\n${problems.length} icon problem(s).`);
  process.exit(1);
}
console.log(`OK: ${defined.size} icon(s) defined, ${used.size} reference(s) resolved.`);

if (PREVIEW) {
  const hasMagick = spawnSync('magick', ['-version'], { stdio: 'ignore' }).status === 0;
  if (!hasMagick) {
    console.log('preview skipped: ImageMagick (magick) not available');
  } else {
    for (const [name, figures] of defined) {
      const filled = /Z M/.test(figures) && !/L \d/.test(figures.split('M').pop());
      const args = ['-size', '24x24', 'xc:black'];
      if (filled) args.push('-fill', 'white', '-stroke', 'none');
      else args.push('-fill', 'none', '-stroke', 'white', '-strokewidth', '2');
      args.push('-draw', `stroke-linecap round stroke-linejoin round path '${figures}'`,
        '-depth', '8', `/tmp/${name}.png`);
      execFileSync('magick', args);
      const txt = execFileSync('magick', [`/tmp/${name}.png`, '-resize', '30x30!', 'txt:-'],
        { encoding: 'utf8' });
      const grid = Array.from({ length: 30 }, () => Array(30).fill('.'));
      for (const line of txt.split('\n')) {
        const px = line.match(/^(\d+),(\d+): \(([\d.]+)(?:,[\d.]+)*\)/);
        if (px) grid[+px[2]][+px[1]] = Number(px[3]) > 100 ? '#' : '.';
      }
      console.log(`\n== ${name} ==`);
      for (const row of grid) console.log('  ' + row.join(''));
    }
  }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/check-icons.js`
Expected: exit 1, listing 15 `uses "Segoe MDL2 Assets"` problems (3 in `Pages/ConnectionPage.xaml`, 6 in `Pages/ChatPage.xaml`, 10 in `MainPage.xaml` — 19 lines total because some buttons only differ by column) and a footer `<n> icon problem(s).`

- [ ] **Step 3: Commit**

```bash
git add tools/check-icons.js
git commit -m "test: add the WP8.1 icon guard"
```

---

### Task 2: Icon geometries in `App.xaml`

**Files:**
- Modify: `WhatsappApp/App.xaml` (inside the existing `ResourceDictionary`)

**Interfaces:**
- Consumes: nothing.
- Produces: 13 `PathGeometry` resources for later tasks — `IconAdd`, `IconBack`, `IconCalls`, `IconChats`, `IconClose`, `IconContacts`, `IconMore`, `IconPhoto`, `IconSearch`, `IconSend`, `IconSettings`, `IconStatus`, `IconVideo`. `IconMore` and `IconSend` are **filled** shapes (consume with `Fill`, no `Stroke`); the other 11 are **stroked** outlines (consume with `Stroke` + `StrokeThickness`).

- [ ] **Step 1: Add the geometries**

Insert immediately after the `WhatsAppBlueBrush` line in `WhatsappApp/App.xaml`:

```xml
            <!-- Icon geometries (24x24). Vector paths instead of an icon font:
                 WP8.1 has no "Segoe MDL2 Assets", so glyph-based buttons were blank. -->
            <PathGeometry x:Key="IconAdd" Figures="M12,4 L12,20 M4,12 L20,12"/>
            <PathGeometry x:Key="IconBack" Figures="M15.5,4 L7.5,12 L15.5,20"/>
            <PathGeometry x:Key="IconCalls" Figures="M16.6,16.6 A6.5,6.5 0 0 1 7.4,7.4"/>
            <PathGeometry x:Key="IconChats" Figures="M4,5 L20,5 L20,15.5 L10.5,15.5 L5.5,20 L5.5,15.5 L4,15.5 Z"/>
            <PathGeometry x:Key="IconClose" Figures="M6,6 L18,18 M18,6 L6,18"/>
            <PathGeometry x:Key="IconContacts" Figures="M12,4.2 A3.6,3.6 0 1 1 12,11.4 A3.6,3.6 0 1 1 12,4.2 Z M5.5,20.8 A6.5,5.8 0 0 1 18.5,20.8"/>
            <PathGeometry x:Key="IconMore" Figures="M12,3.5 A1.5,1.5 0 1 1 12,6.5 A1.5,1.5 0 1 1 12,3.5 Z M12,10.5 A1.5,1.5 0 1 1 12,13.5 A1.5,1.5 0 1 1 12,10.5 Z M12,17.5 A1.5,1.5 0 1 1 12,20.5 A1.5,1.5 0 1 1 12,17.5 Z"/>
            <PathGeometry x:Key="IconPhoto" Figures="M3.5,5.5 L20.5,5.5 L20.5,18.5 L3.5,18.5 Z M3.5,15 L8.5,10 L12.5,14 L15.5,11.5 L20.5,15.5 M7.5,8 A1.3,1.3 0 1 1 7.5,10.6 A1.3,1.3 0 1 1 7.5,8"/>
            <PathGeometry x:Key="IconSearch" Figures="M10.5,4 A6.5,6.5 0 1 1 10.5,17 A6.5,6.5 0 1 1 10.5,4 M15.4,15.4 L20.5,20.5"/>
            <PathGeometry x:Key="IconSend" Figures="M2.5,20.5 L21.5,12 L2.5,3.5 L2.5,9.8 L15,12 L2.5,14.2 Z"/>
            <PathGeometry x:Key="IconSettings" Figures="M4,6.5 L20,6.5 M4,12 L20,12 M4,17.5 L20,17.5 M9,4.5 A2,2 0 1 1 9,8.5 A2,2 0 1 1 9,4.5 M15,10 A2,2 0 1 1 15,14 A2,2 0 1 1 15,10 M9,15.5 A2,2 0 1 1 9,19.5 A2,2 0 1 1 9,15.5"/>
            <PathGeometry x:Key="IconStatus" Figures="M12,4 A8,8 0 1 1 12,20 A8,8 0 1 1 12,4 Z M12,7.5 L12,12.3 L16,14.5"/>
            <PathGeometry x:Key="IconVideo" Figures="M3.5,7 L14,7 L14,17 L3.5,17 Z M14,11 L20.5,7.5 L20.5,16.5 L14,13 Z"/>
```

- [ ] **Step 2: Run the guard to check the geometries parse and reach the preview**

Run: `node tools/check-icons.js --preview`
Expected: exit 1 with only the `Segoe MDL2 Assets` problems left (the icons are now defined), and 13 `== IconX ==` ASCII blocks. Confirm each block reads as its name: a magnifier (`IconSearch`), three vertical dots (`IconMore`), three sliders with knobs (`IconSettings`), a bubble with a tail (`IconChats`), a person (`IconContacts`), a clock (`IconStatus`), a handset arc (`IconCalls`), a camcorder (`IconVideo`), a framed photo (`IconPhoto`), a right-pointing plane (`IconSend`), an X (`IconClose`), a plus (`IconAdd`), a left chevron (`IconBack`).

- [ ] **Step 3: Commit**

```bash
git add WhatsappApp/App.xaml
git commit -m "feat: define the app icons as vector geometries"
```

---

### Task 3: `MainPage.xaml` — icons, FAB row, empty state

**Files:**
- Modify: `WhatsappApp/MainPage.xaml`

**Interfaces:**
- Consumes: the `Icon*` geometries from Task 2; `DataService.Instance.Contacts` (Task 6 toggles `EmptyStatePanel`).
- Produces: unchanged `x:Name` set plus one new name, `EmptyStatePanel`; unchanged click handlers (`SearchButton_Click`, `MoreButton_Click`, `ConnectionButton_Click`, `NewChatButton_Click`, `ChatListView_SelectionChanged`).

- [ ] **Step 1: Replace the three header buttons**

Old:

```xml
            <Button Grid.Column="1" Content="&#xE717;" FontFamily="Segoe MDL2 Assets"
                    Foreground="White" Background="Transparent"
                    Width="44" Height="44" BorderThickness="0"
                    Click="SearchButton_Click"/>

            <Button Grid.Column="2" Content="&#xE10F;" FontFamily="Segoe MDL2 Assets"
                    Foreground="White" Background="Transparent"
                    Width="44" Height="44" BorderThickness="0"
                    Click="MoreButton_Click"/>

            <Button Grid.Column="3" Content="&#xE700;" FontFamily="Segoe MDL2 Assets"
                    Foreground="White" Background="Transparent"
                    Width="44" Height="44" BorderThickness="0"
                    Click="ConnectionButton_Click"/>
```

New:

```xml
            <Button Grid.Column="1" Foreground="White" Background="Transparent"
                    Width="44" Height="44" BorderThickness="0" IsEnabled="False"
                    Click="SearchButton_Click">
                <Path Data="{StaticResource IconSearch}" Stroke="White" StrokeThickness="2"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24"/>
            </Button>

            <Button Grid.Column="2" Foreground="White" Background="Transparent"
                    Width="44" Height="44" BorderThickness="0" IsEnabled="False"
                    Click="MoreButton_Click">
                <Path Data="{StaticResource IconMore}" Fill="White" Width="24" Height="24"/>
            </Button>

            <Button Grid.Column="3" Foreground="White" Background="Transparent"
                    Width="44" Height="44" BorderThickness="0"
                    Click="ConnectionButton_Click">
                <Path Data="{StaticResource IconSettings}" Stroke="White" StrokeThickness="2"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24"/>
            </Button>
```

(`SearchButton_Click` and `MoreButton_Click` stay wired to their existing empty handlers; their buttons are disabled because search and the overflow menu are not implemented — the settings button remains active and navigates to `ConnectionPage`.)

- [ ] **Step 2: Disable the three not-implemented tab buttons**

Replace each of the three tab `Button` elements (the `CHAT` one is the first) so they keep their text but can no longer be tapped into nothing. Old (all three, in order):

```xml
            <Button Grid.Column="0" Content="CHAT" Foreground="White"
                    Background="Transparent" FontSize="14" FontWeight="SemiBold"
                    BorderThickness="0" Height="48"/>
            <Button Grid.Column="1" Content="STATO" Foreground="#B0FFFFFF"
                    Background="Transparent" FontSize="14" FontWeight="Normal"
                    BorderThickness="0" Height="48"/>
            <Button Grid.Column="2" Content="CHIAMATE" Foreground="#B0FFFFFF"
                    Background="Transparent" FontSize="14" FontWeight="Normal"
                    BorderThickness="0" Height="48"/>
```

New:

```xml
            <Button Grid.Column="0" Content="CHAT" Foreground="White"
                    Background="Transparent" FontSize="14" FontWeight="SemiBold"
                    BorderThickness="0" Height="48" IsEnabled="False"/>
            <Button Grid.Column="1" Content="STATO" Foreground="#B0FFFFFF"
                    Background="Transparent" FontSize="14" FontWeight="Normal"
                    BorderThickness="0" Height="48" IsEnabled="False"/>
            <Button Grid.Column="2" Content="CHIAMATE" Foreground="#B0FFFFFF"
                    Background="Transparent" FontSize="14" FontWeight="Normal"
                    BorderThickness="0" Height="48" IsEnabled="False"/>
```

- [ ] **Step 3: Add the empty state panel right after the `ListView`**

Insert between the closing `</ListView>` and the `<!-- Bottom Bar -->` comment:

```xml
        <!-- Empty state: shown by UpdateEmptyState() when there are no contacts -->
        <StackPanel x:Name="EmptyStatePanel" Grid.Row="2" Visibility="Collapsed"
                    VerticalAlignment="Center" HorizontalAlignment="Center">
            <Path Data="{StaticResource IconChats}" Stroke="#FFBDBDBD" StrokeThickness="2"
                  StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                  Width="48" Height="48" Stretch="Uniform" HorizontalAlignment="Center"/>
            <TextBlock Text="Nessuna chat" Foreground="#FF9E9E9E" FontSize="16"
                       HorizontalAlignment="Center" Margin="0,10,0,0"/>
            <TextBlock Text="Tocca + per iniziare" Foreground="#FFBDBDBD" FontSize="13"
                       HorizontalAlignment="Center" Margin="0,2,0,0"/>
        </StackPanel>
```

- [ ] **Step 4: Replace the four bottom-bar buttons**

Old:

```xml
            <Button Grid.Column="0" Content="&#xE80F;" FontFamily="Segoe MDL2 Assets"
                    Foreground="#FF128C7E" FontSize="20"
                    Background="Transparent" BorderThickness="0"/>
            <Button Grid.Column="1" Content="&#xE140;" FontFamily="Segoe MDL2 Assets"
                    Foreground="#FF808080" FontSize="20"
                    Background="Transparent" BorderThickness="0"/>
            <Button Grid.Column="2" Content="&#xE15D;" FontFamily="Segoe MDL2 Assets"
                    Foreground="#FF808080" FontSize="20"
                    Background="Transparent" BorderThickness="0"/>
            <Button Grid.Column="3" Content="&#xE16F;" FontFamily="Segoe MDL2 Assets"
                    Foreground="#FF808080" FontSize="20"
                    Background="Transparent" BorderThickness="0"/>
```

New:

```xml
            <Button Grid.Column="0" Background="Transparent" BorderThickness="0" IsEnabled="False">
                <Path Data="{StaticResource IconChats}" Stroke="#FF128C7E" StrokeThickness="2"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24"/>
            </Button>
            <Button Grid.Column="1" Background="Transparent" BorderThickness="0" IsEnabled="False">
                <Path Data="{StaticResource IconContacts}" Stroke="#FF808080" StrokeThickness="2"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24"/>
            </Button>
            <Button Grid.Column="2" Background="Transparent" BorderThickness="0" IsEnabled="False">
                <Path Data="{StaticResource IconStatus}" Stroke="#FF808080" StrokeThickness="2"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24"/>
            </Button>
            <Button Grid.Column="3" Background="Transparent" BorderThickness="0" IsEnabled="False">
                <Path Data="{StaticResource IconCalls}" Stroke="#FF808080" StrokeThickness="3"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24"/>
            </Button>
```

- [ ] **Step 5: Fix the floating "+" button — wrong grid row**

The button currently has no `Grid.Row`, so it is laid out inside row 0 (the 56 px header) and paints over the header instead of floating above the bottom bar. Old:

```xml
        <!-- Floating Action Button (New Chat) - square button since CornerRadius not supported on WP8.1 -->
        <Button x:Name="NewChatButton"
                Content="&#xE109;" FontFamily="Segoe MDL2 Assets"
                Width="56" Height="56"
                Background="{StaticResource WhatsAppAccentBrush}"
                Foreground="White" FontSize="24"
                BorderThickness="0"
                HorizontalAlignment="Right" VerticalAlignment="Bottom"
                Margin="0,0,16,64"
                Click="NewChatButton_Click"/>
```

New:

```xml
        <!-- Floating Action Button (New Chat): row 2 (the chat list) so it floats
             above the bottom bar instead of overlapping the header. -->
        <Button x:Name="NewChatButton" Grid.Row="2"
                Width="56" Height="56"
                Background="{StaticResource WhatsAppAccentBrush}"
                Foreground="White"
                BorderThickness="0" Padding="0"
                HorizontalAlignment="Right" VerticalAlignment="Bottom"
                Margin="0,0,16,16"
                Click="NewChatButton_Click">
            <Path Data="{StaticResource IconAdd}" Stroke="White" StrokeThickness="2.2"
                  StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                  Width="24" Height="24"/>
        </Button>
```

- [ ] **Step 6: Run the guard**

Run: `node tools/check-icons.js`
Expected: exit 0, `OK: 13 icon(s) defined, N reference(s) resolved.`, with no `Segoe MDL2 Assets` and no undefined icon.

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/MainPage.xaml
git commit -m "fix: vector icons, correct FAB row and empty state on the chat list"
```

---

### Task 4: `ChatPage.xaml` — icons and the image binding

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml`

**Interfaces:**
- Consumes: the `Icon*` geometries; `ChatMessage.MediaImage` (Task 7) for both message images.
- Produces: unchanged `x:Name` set (`BackButton`, `ContactNameText`, `OnlineStatusText`, `MessagesListView`, `ImagePreviewBar`, `SelectedImagePreview`, `MessageTextBox`, `AttachButton`, `SendButton`) and handlers; the `Base64ToImage` resource and its two bindings are gone.

- [ ] **Step 1: Drop the base64 converter resource**Old (`Page.Resources`, lines 9-15):

```xml
        <conv:BoolToVisibilityConverter x:Key="BoolToVisibility"/>
        <conv:MessageStatusToStringConverter x:Key="MsgStatusToString"/>
        <conv:MessageStatusToColorConverter x:Key="MsgStatusToColor"/>
        <conv:Base64ToImageSourceConverter x:Key="Base64ToImage"/>
        <conv:MessageTypeToImageVisibilityConverter x:Key="MsgTypeToImageVis"/>
        <conv:MessageTypeToTextVisibilityConverter x:Key="MsgTypeToTextVis"/>
```

New — the third line is simply gone:

```xml
        <conv:BoolToVisibilityConverter x:Key="BoolToVisibility"/>
        <conv:MessageStatusToStringConverter x:Key="MsgStatusToString"/>
        <conv:MessageStatusToColorConverter x:Key="MsgStatusToColor"/>
        <conv:MessageTypeToImageVisibilityConverter x:Key="MsgTypeToImageVis"/>
        <conv:MessageTypeToTextVisibilityConverter x:Key="MsgTypeToTextVis"/>
```

- [ ] **Step 2: Replace the back button glyph**

Old:

```xml
            <Button x:Name="BackButton" Grid.Column="0"
                    Content="&#xE0C4;" FontFamily="Segoe MDL2 Assets"
                    Foreground="White" Background="Transparent"
                    Width="48" Height="48" Margin="0,4,0,0"
                    Click="BackButton_Click"
                    BorderThickness="0"/>
```

New:

```xml
            <Button x:Name="BackButton" Grid.Column="0"
                    Foreground="White" Background="Transparent"
                    Width="48" Height="48" Margin="0,4,0,0"
                    Click="BackButton_Click"
                    BorderThickness="0" Padding="0">
                <Path Data="{StaticResource IconBack}" Stroke="White" StrokeThickness="2.4"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24"/>
            </Button>
```

- [ ] **Step 3: Replace the two call buttons**

Old:

```xml
                <Button Content="&#xE717;" FontFamily="Segoe MDL2 Assets"
                        Foreground="White" Background="Transparent"
                        Width="44" Height="44" BorderThickness="0"/>
                <Button Content="&#xE13F;" FontFamily="Segoe MDL2 Assets"
                        Foreground="White" Background="Transparent"
                        Width="44" Height="44" BorderThickness="0"/>
```

New:

```xml
                <Button Foreground="White" Background="Transparent"
                        Width="44" Height="44" BorderThickness="0" IsEnabled="False" Padding="0">
                    <Path Data="{StaticResource IconCalls}" Stroke="White" StrokeThickness="3"
                          StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                          Width="24" Height="24"/>
                </Button>
                <Button Foreground="White" Background="Transparent"
                        Width="44" Height="44" BorderThickness="0" IsEnabled="False" Padding="0">
                    <Path Data="{StaticResource IconVideo}" Stroke="White" StrokeThickness="2"
                          StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                          Width="24" Height="24"/>
                </Button>
```

- [ ] **Step 4: Point both message images at the decoded bitmap**

There are two identical image blocks (incoming and outgoing). Replace **both** occurrences of:

```xml
                                    <Image Source="{Binding MediaData, Converter={StaticResource Base64ToImage}}"
                                           Stretch="UniformToFill"
                                           HorizontalAlignment="Center"/>
```

with:

```xml
                                    <Image Source="{Binding MediaImage}"
                                           Stretch="UniformToFill"
                                           HorizontalAlignment="Center"/>
```

- [ ] **Step 5: Replace the preview-bar close button**

Old:

```xml
                <Button Grid.Column="2" Content="&#xE10A;" FontFamily="Segoe MDL2 Assets"
                        Foreground="#FF808080" Background="Transparent"
                        Width="44" Height="44" BorderThickness="0"
                        Click="ClearImageButton_Click"/>
```

New:

```xml
                <Button Grid.Column="2" Foreground="#FF808080" Background="Transparent"
                        Width="44" Height="44" BorderThickness="0" Padding="0"
                        Click="ClearImageButton_Click">
                    <Path Data="{StaticResource IconClose}" Stroke="#FF808080" StrokeThickness="2.4"
                          StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                          Width="24" Height="24"/>
                </Button>
```

- [ ] **Step 6: Replace the attach and send buttons**

Old:

```xml
                <Button x:Name="AttachButton" Grid.Column="0"
                        Content="&#xE11B;" FontFamily="Segoe MDL2 Assets"
                        Foreground="#FF808080" Background="Transparent"
                        Width="48" Height="48" BorderThickness="0"
                        Margin="0,4,0,4"
                        Click="AttachButton_Click"/>
```

New:

```xml
                <Button x:Name="AttachButton" Grid.Column="0"
                        Foreground="#FF808080" Background="Transparent"
                        Width="48" Height="48" BorderThickness="0" Padding="0"
                        Margin="0,4,0,4"
                        Click="AttachButton_Click">
                    <Path Data="{StaticResource IconPhoto}" Stroke="#FF808080" StrokeThickness="2"
                          StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                          Width="24" Height="24"/>
                </Button>
```

Old:

```xml
                <Button x:Name="SendButton" Grid.Column="2"
                        Content="&#xE13F;" FontFamily="Segoe MDL2 Assets"
                        Background="{StaticResource WhatsAppAccentBrush}"
                        Foreground="White"
                        Width="48" Height="48"
                        Margin="0,4,8,4"
                        BorderThickness="0"
                        Click="SendButton_Click"/>
```

New:

```xml
                <Button x:Name="SendButton" Grid.Column="2"
                        Background="{StaticResource WhatsAppAccentBrush}"
                        Foreground="White"
                        Width="48" Height="48"
                        Margin="0,4,8,4"
                        BorderThickness="0" Padding="0"
                        Click="SendButton_Click">
                    <Path Data="{StaticResource IconSend}" Fill="White" Width="22" Height="22"/>
                </Button>
```

- [ ] **Step 7: Run the guard**

Run: `node tools/check-icons.js`
Expected: exit 0; in particular `IconClose`, `IconPhoto`, `IconSend`, `IconVideo` and `IconCalls` are now reported as used.

- [ ] **Step 8: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml
git commit -m "fix: vector icons and bitmap binding in the chat page"
```

---

### Task 5: `ConnectionPage.xaml` — back icon

**Files:**
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml`

**Interfaces:**
- Consumes: `IconBack`.
- Produces: unchanged names/handlers (`BackButton`, `PageTitleText`, `ActionButton`, `DisconnectButton`, `StatusPanel`, `StatusText`, `WhatsAppPanel`, `WhatsAppStateText`, `LoginQrButton`, `QrImage`, `QrInfoText`, `PhoneBox`, `LoginCodeButton`, `PairCodeText`, `ContinueButton`).

- [ ] **Step 1: Replace the back button glyph**

Old:

```xml
            <Button x:Name="BackButton" Grid.Column="0"
                    Content="&#xE0C4;" FontFamily="Segoe MDL2 Assets"
                    Foreground="White" Background="Transparent"
                    Width="48" Height="48" Margin="0,4,0,0"
                    Click="BackButton_Click"
                    BorderThickness="0"/>
```

New:

```xml
            <Button x:Name="BackButton" Grid.Column="0"
                    Foreground="White" Background="Transparent"
                    Width="48" Height="48" Margin="0,4,0,0"
                    Click="BackButton_Click"
                    BorderThickness="0" Padding="0">
                <Path Data="{StaticResource IconBack}" Stroke="White" StrokeThickness="2.4"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24"/>
            </Button>
```

- [ ] **Step 2: Run the guard**

Run: `node tools/check-icons.js`
Expected: exit 0, `OK: 13 icon(s) defined, 16 reference(s) resolved.` (12 in `MainPage.xaml`, 8 in `ChatPage.xaml`… the exact reference count printed must simply be `> 0` and there must be no problem line).

- [ ] **Step 3: Commit**

```bash
git add WhatsappApp/Pages/ConnectionPage.xaml
git commit -m "fix: vector back icon on the connection page"
```

---

### Task 6: `MainPage.xaml.cs` — empty state

**Files:**
- Modify: `WhatsappApp/MainPage.xaml.cs`

**Interfaces:**
- Consumes: `EmptyStatePanel` (Task 3), `DataService.Instance.Contacts` (`ObservableCollection<Contact>`, raising `CollectionChanged`).
- Produces: `private void Contacts_CollectionChanged(object, NotifyCollectionChangedEventArgs)` and `private void UpdateEmptyState()`; `OnNavigatedTo`/`OnNavigatedFrom` keep/hook the listener idempotently.

- [ ] **Step 1: Subscribe and update in `OnNavigatedTo`**

Old:

```csharp
            base.OnNavigatedTo(e);
            ChatListView.ItemsSource = DataService.Instance.Contacts;

            // OnNavigatedTo is not async: fire the contacts request and ignore the task
            if (CommunicationService.Instance.IsConnected)
                CommunicationService.Instance.SendControlAsync("contacts");
```

New:

```csharp
            base.OnNavigatedTo(e);
            ChatListView.ItemsSource = DataService.Instance.Contacts;

            // Keep the empty state in sync with the contact list
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
            DataService.Instance.Contacts.CollectionChanged += Contacts_CollectionChanged;
            UpdateEmptyState();

            // OnNavigatedTo is not async: fire the contacts request and ignore the task
            if (CommunicationService.Instance.IsConnected)
                CommunicationService.Instance.SendControlAsync("contacts");
```

- [ ] **Step 2: Unsubscribe in `OnNavigatedFrom`**

Old:

```csharp
            base.OnNavigatedFrom(e);
            HardwareButtons.BackPressed -= HardwareButtons_BackPressed;
```

New:

```csharp
            base.OnNavigatedFrom(e);
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
            HardwareButtons.BackPressed -= HardwareButtons_BackPressed;
```

- [ ] **Step 3: Add the handler and the helper**

Insert immediately after `OnNavigatedFrom`:

```csharp
        private void Contacts_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            bool empty = DataService.Instance.Contacts.Count == 0;
            EmptyStatePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }
```

- [ ] **Step 4: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-icons.js`
Expected: `OK: 15 C# file(s) are C# 5 compatible.` and `OK: 13 icon(s) …` (exit 0 both).

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/MainPage.xaml.cs
git commit -m "fix: show an empty state on the chat list"
```

---

### Task 7: `ChatMessage.cs` — local times and decoded media

**Files:**
- Modify: `WhatsappApp/Models/ChatMessage.cs`

**Interfaces:**
- Consumes: `Windows.Storage.Streams.InMemoryRandomAccessStream`, `DataWriter`, `Windows.UI.Xaml.Media.Imaging.BitmapImage`.
- Produces: private field `_mediaImage`; `public BitmapImage MediaImage { get; set; }` (bindable, **not** a `[DataMember]`, so it never reaches the wire); `public async Task LoadMediaImageAsync()`; `FormatTime` now converts UTC to local.

- [ ] **Step 1: Add the usings**

Old:

```csharp
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;
using System.Text;
```

New:

```csharp
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;
```

- [ ] **Step 2: Declare the backing field**

Old:

```csharp
        private string _accountJid;     // WhatsApp JID of the logged-in account
```

New:

```csharp
        private string _accountJid;     // WhatsApp JID of the logged-in account
        private BitmapImage _mediaImage; // decoded MediaData, for the XAML image binding
```

- [ ] **Step 3: Add the bindable property and the decoder**

Insert immediately before the line `public string FormattedTime`:

```csharp
        /// <summary>
        /// Immagine decodificata da MediaData. Non è un [DataMember]: resta
        /// solo lato client e serve al binding XAML della bolla.
        /// </summary>
        public BitmapImage MediaImage
        {
            get { return _mediaImage; }
            set { _mediaImage = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Decodifica MediaData (base64) in <see cref="MediaImage"/>.
        /// Deve essere atteso sul thread UI: il flusso resta aperto finché
        /// SetSourceAsync non ha finito di decodificare.
        /// </summary>
        public async Task LoadMediaImageAsync()
        {
            if (Type != MessageType.Image || string.IsNullOrEmpty(MediaData)) return;

            try
            {
                byte[] bytes = Convert.FromBase64String(MediaData);
                using (var stream = new InMemoryRandomAccessStream())
                {
                    using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(bytes);
                        await writer.StoreAsync();
                    }
                    var bitmap = new BitmapImage();
                    stream.Seek(0);
                    await bitmap.SetSourceAsync(stream);
                    MediaImage = bitmap;
                }
            }
            catch
            {
                MediaImage = null;
            }
        }

```

- [ ] **Step 4: Convert UTC timestamps to local time**

Old:

```csharp
        private static string FormatTime(DateTime dt)
        {
            var now = DateTime.Now;
```

New:

```csharp
        private static string FormatTime(DateTime dt)
        {
            // Il serializer legge /Date(ms)/ come UTC: senza questa conversione
            // l'orario mostrato è sfasato rispetto a quello del telefono.
            if (dt.Kind == DateTimeKind.Utc)
                dt = dt.ToLocalTime();

            var now = DateTime.Now;
```

- [ ] **Step 5: Run the guards**

Run: `node tools/check-csharp5.js`
Expected: `OK: 15 C# file(s) are C# 5 compatible.` (the new code is C# 5: no `?.`, no interpolation).

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Models/ChatMessage.cs
git commit -m "fix: show local timestamps and decode chat images into a bitmap"
```

---

### Task 8: `DataService.cs` — decode incoming images

**Files:**
- Modify: `WhatsappApp/Services/DataService.cs`

**Interfaces:**
- Consumes: `ChatMessage.LoadMediaImageAsync()` (Task 7).
- Produces: `OnNetworkMessageReceived` becomes `async void` and awaits the decode for image messages; insertion stays exactly once per message.

- [ ] **Step 1: Make the handler async and decode the image**

Old:

```csharp
        private void OnNetworkMessageReceived(object sender, ChatMessage message)
        {
            // Ignore system/handshake messages
            if (message.Type == MessageType.System) return;
```

New:

```csharp
        private async void OnNetworkMessageReceived(object sender, ChatMessage message)
        {
            // Ignore system/handshake messages
            if (message.Type == MessageType.System) return;
```

- [ ] **Step 2: Await the decode after the message has been added**

Old (the end of the same method):

```csharp
                var idx = _contacts.IndexOf(contact);
                if (idx > 0)
                    _contacts.Move(idx, 0);
            }
        }
```

New:

```csharp
                var idx = _contacts.IndexOf(contact);
                if (idx > 0)
                    _contacts.Move(idx, 0);
            }

            // Decodifica asincrona dell'immagine: il binding XAML segue MediaImage
            if (message.Type == MessageType.Image)
                await message.LoadMediaImageAsync();
        }
```

- [ ] **Step 3: Run the guards**

Run: `node tools/check-csharp5.js`
Expected: `OK: 15 C# file(s) are C# 5 compatible.`

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Services/DataService.cs
git commit -m "fix: decode received images so they show in the chat"
```

---

### Task 9: `ChatPage.xaml.cs` — no duplicate messages

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs`

**Interfaces:**
- Consumes: `DataService.Instance.AddMessage(string, ChatMessage)` (the only inserter), `CommunicationService.Instance.IsConnected`.
- Produces: unchanged handlers; message lists contain exactly one instance per message; sent-image messages carry a decoded `MediaImage`.

- [ ] **Step 1: Stop double-adding incoming messages**

`DataService.OnNetworkMessageReceived` already appends the message to the very same `ObservableCollection` that `MessagesListView` is bound to (`ChatPage._messages` comes from `DataService.GetMessages`). The page must only scroll.

Old:

```csharp
        private void OnMessageReceived(object sender, ChatMessage message)
        {
            if (message.ChatId == _contact.Id)
            {
                _messages.Add(message);
                MessagesListView.UpdateLayout();
                MessagesListView.ScrollIntoView(message);
            }
        }
```

New:

```csharp
        private void OnMessageReceived(object sender, ChatMessage message)
        {
            // DataService ha già inserito il messaggio nella stessa collezione:
            // qui si scorre soltanto, altrimenti la bolla comparirebbe due volte.
            if (message.ChatId == _contact.Id)
            {
                MessagesListView.UpdateLayout();
                MessagesListView.ScrollIntoView(message);
            }
        }
```

- [ ] **Step 2: Stop double-adding the outgoing message and report a real status**

Old:

```csharp
        private async void AddAndSendMessage(ChatMessage message)
        {
            // Add message locally
            _messages.Add(message);
            DataService.Instance.AddMessage(_contact.Id, message);
            MessageTextBox.Text = "";

            // Auto-scroll
            MessagesListView.UpdateLayout();
            MessagesListView.ScrollIntoView(message);

            // Send via network if connected
            if (_isConnectedMode)
            {
                message.Status = MessageStatus.Sending;
                await CommunicationService.Instance.SendMessageAsync(message);
                message.Status = MessageStatus.Sent;
            }
        }
```

New:

```csharp
        private async void AddAndSendMessage(ChatMessage message)
        {
            // DataService è l'unico punto di inserimento: _messages è la stessa
            // ObservableCollection che il ListView osserva.
            DataService.Instance.AddMessage(_contact.Id, message);
            MessageTextBox.Text = "";

            // Auto-scroll
            MessagesListView.UpdateLayout();
            MessagesListView.ScrollIntoView(message);

            // Send via network if connected
            if (_isConnectedMode)
            {
                message.Status = MessageStatus.Sending;
                await CommunicationService.Instance.SendMessageAsync(message);
                message.Status = CommunicationService.Instance.IsConnected
                    ? MessageStatus.Sent
                    : MessageStatus.Failed;
            }
        }
```

- [ ] **Step 3: Decode the image we are about to send**

Old:

```csharp
            AddAndSendMessage(message);

            // Clear image preview
            ClearSelectedImage();
```

New:

```csharp
            // Decodifica locale: il mittente deve vedere la propria immagine
            await message.LoadMediaImageAsync();

            AddAndSendMessage(message);

            // Clear image preview
            ClearSelectedImage();
```

- [ ] **Step 4: Run the guards**

Run: `node tools/check-csharp5.js`
Expected: `OK: 15 C# file(s) are C# 5 compatible.`

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml.cs
git commit -m "fix: insert each chat message exactly once"
```

---

### Task 10: `Converters.cs` — delete the broken converter, fix the colour hash

**Files:**
- Modify: `WhatsappApp/Converters/Converters.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: the nine remaining converters (`BoolToVisibilityConverter`, `MessageStatusToStringConverter`, `MessageStatusToColorConverter`, `InitialToColorConverter`, `UnreadCountToVisibilityConverter`, `OnlineStatusConverter`, `OnlineToDotColorConverter`, `MessageTypeToImageVisibilityConverter`, `MessageTypeToTextVisibilityConverter`). `Base64ToImageSourceConverter` is **gone** — Task 4 removed its only binding, and its synchronous `BitmapImage.SetSource` over a disposed stream could never render.

- [ ] **Step 1: Delete the converter class**

Delete the whole `Base64ToImageSourceConverter` block, i.e. from

```csharp
    /// <summary>
    /// Converts a base64-encoded image string to a BitmapImageSource
    /// </summary>
    public class Base64ToImageSourceConverter : IValueConverter
```

through its closing

```csharp
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

```

leaving the following `/// <summary>` / `public class MessageTypeToImageVisibilityConverter` block untouched.

- [ ] **Step 2: Drop the now-unused usings**

Old:

```csharp
using System;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
```

New:

```csharp
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;
```

(Safe: the only users of `Windows.Storage.Streams` and `Windows.UI.Xaml.Media.Imaging` in this file were inside the deleted class; the remaining converters use `Visibility`, `IValueConverter` and `SolidColorBrush`.)

- [ ] **Step 3: Fix the overflow in the avatar colour hash**

`Math.Abs(int.MinValue)` throws `OverflowException`, which on a XAML binding surfaces as a broken avatar (no brush at all).

Old:

```csharp
            string initials = value as string ?? "?";
            int hash = initials.GetHashCode();
            int index = Math.Abs(hash) % Colors.Length;
```

New:

```csharp
            string initials = value as string ?? "?";
            int hash = initials == null ? 0 : initials.GetHashCode();
            // Maschera il bit di segno: Math.Abs(int.MinValue) va in overflow
            int index = (hash & 0x7FFFFFFF) % Colors.Length;
```

- [ ] **Step 4: Confirm the removal is complete**

Run:

```bash
grep -rn 'Base64ToImage\|MediaData, Converter' WhatsappApp --include=*.cs --include=*.xaml | grep -v obj/ || echo "no base64 converter references left"
```

Expected: `no base64 converter references left`

- [ ] **Step 5: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-icons.js`
Expected: both exit 0.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Converters/Converters.cs
git commit -m "fix: remove the broken base64 image converter and guard the avatar hash"
```

---

### Task 11: `CommunicationService.cs` — dispatch once

**Files:**
- Modify: `WhatsappApp/Services/CommunicationService.cs`

**Interfaces:**
- Consumes: `CoreDispatcher.RunAsync`.
- Produces: unchanged public surface; `DispatchOnUiThread` runs the action **exactly once**, falling back to a direct call only when the dispatcher itself refuses to run it.

- [ ] **Step 1: Guard the fallback**

The current code wraps both the dispatch *and* the action in one `try`, so an exception thrown by a subscriber makes the action run a second time — for example inserting every contact twice.

Old:

```csharp
        private async void DispatchOnUiThread(Action action)
        {
            var dispatcher = GetUiDispatcher();
            if (dispatcher != null)
            {
                try
                {
                    await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
                }
                catch
                {
                    action();
                }
            }
            else
            {
                action();
            }
        }
```

New:

```csharp
        private async void DispatchOnUiThread(Action action)
        {
            var dispatcher = GetUiDispatcher();
            if (dispatcher == null)
            {
                action();
                return;
            }

            // Il flag distingue "il dispatcher non ha eseguito nulla" (si
            // riprova in linea) da "l'azione è partita ma è esplosa" (non va
            // rieseguita, altrimenti gli handler ricevono l'evento due volte).
            bool dispatched = false;
            try
            {
                await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    dispatched = true;
                    action();
                });
            }
            catch
            {
                if (!dispatched) action();
            }
        }
```

- [ ] **Step 2: Run the guard**

Run: `node tools/check-csharp5.js`
Expected: `OK: 15 C# file(s) are C# 5 compatible.`

- [ ] **Step 3: Commit**

```bash
git add WhatsappApp/Services/CommunicationService.cs
git commit -m "fix: never dispatch a UI event twice"
```

---

### Task 12: `ConnectionPage.xaml.cs` — clear the pairing code when connected

**Files:**
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml.cs`

**Interfaces:**
- Consumes: `UpdateLoginUi(string state, string accountJid)`.
- Produces: unchanged behaviour except that the pairing code disappears once the account is linked.

- [ ] **Step 1: Reset the code in the connected branch**

Old:

```csharp
                case "connected":
                    WhatsAppStateText.Text = string.IsNullOrEmpty(accountJid)
                        ? "WhatsApp connesso!"
                        : "Connesso come " + accountJid.Split('@')[0];
                    LoginQrButton.Visibility = Visibility.Collapsed;
                    QrImage.Visibility = Visibility.Collapsed;
                    PhoneBox.Visibility = Visibility.Collapsed;
                    LoginCodeButton.Visibility = Visibility.Collapsed;
                    QrInfoText.Text = "";
                    ContinueButton.IsEnabled = true;
                    break;
```

New:

```csharp
                case "connected":
                    WhatsAppStateText.Text = string.IsNullOrEmpty(accountJid)
                        ? "WhatsApp connesso!"
                        : "Connesso come " + accountJid.Split('@')[0];
                    LoginQrButton.Visibility = Visibility.Collapsed;
                    QrImage.Visibility = Visibility.Collapsed;
                    PhoneBox.Visibility = Visibility.Collapsed;
                    LoginCodeButton.Visibility = Visibility.Collapsed;
                    QrInfoText.Text = "";
                    PairCodeText.Text = "";
                    ContinueButton.IsEnabled = true;
                    break;
```

- [ ] **Step 2: Run the guard**

Run: `node tools/check-csharp5.js`
Expected: `OK: 15 C# file(s) are C# 5 compatible.`

- [ ] **Step 3: Commit**

```bash
git add WhatsappApp/Pages/ConnectionPage.xaml.cs
git commit -m "fix: hide the pairing code once the account is linked"
```

---

### Task 13: Whole-app verification

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: all previous tasks.
- Produces: a repo state where both guards pass, the adapter suite is green, and no XAML file references an icon font.

- [ ] **Step 1: Run both guards**

Run: `node tools/check-csharp5.js && node tools/check-icons.js`
Expected: `OK: 15 C# file(s) are C# 5 compatible.` then `OK: 13 icon(s) defined, N reference(s) resolved.` (exit 0)

- [ ] **Step 2: Prove the legacy icon font is gone**

Run:

```bash
grep -rn 'Segoe MDL2 Assets&#124;&#124;FontFamily=' WhatsappApp --include=*.xaml | grep -v obj/ || echo "no FontFamily left in XAML"
```

Expected: `no FontFamily left in XAML`

- [ ] **Step 3: Check every `x:Name` used in code-behind still exists in XAML**

Run:

```bash
for n in $(grep -rhoE '\b[A-Z][A-Za-z]+(\.[A-Za-z]+)? =' WhatsappApp/*.cs WhatsappApp/Pages/*.cs | sed 's/ =$//' | cut -d. -f1 | sort -u); do grep -rq "x:Name=\"$n\"" WhatsappApp --include=*.xaml || true; done; grep -rhoE 'x:Name="[A-Za-z]+"' WhatsappApp --include=*.xaml | sort -u | wc -l
```

Expected: a count of unique `x:Name` declarations (22); no error output. Cross-check by hand that `EmptyStatePanel` is among them.

- [ ] **Step 4: Run the adapter regression suite**

Run: `cd WhatsappBridge && npm test; cd ..`
Expected: `ℹ pass 29` / `ℹ fail 0`.

- [ ] **Step 5: Document the icon system in the README**

Add to `README.md`, directly under the "Icone, tile e splash screen" section:

```markdown
Le icone dell'interfaccia (ricerca, impostazioni, tab, allegati, invio…)
**non** usano un font di icone: Windows Phone 8.1 non ha `Segoe MDL2 Assets`
(è arrivato con Windows 10), quindi i pulsanti restavano vuoti. Sono `Path`
vettoriali definiti una sola volta in `WhatsappApp/App.xaml`
(`PathGeometry x:Key="Icon…"`) e consumati con
`Data="{StaticResource Icon…}"`. Per controllare che nessun riferimento sia
rotto o inutilizzato:

```bash
node tools/check-icons.js            # riferimenti + font vietati
node tools/check-icons.js --preview  # + anteprima ASCII (richiede ImageMagick)
```
```

- [ ] **Step 6: Commit and push**

```bash
git add README.md
git commit -m "docs: document the vector icon system and its guard"
git push origin master
```

- [ ] **Step 7: Rebuild on Windows**

```bat
msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86
```

Expected: `0 Error(s)`. Then check on the device/emulator: every header/bottom-bar/send/attach button shows its icon; the green "+" floats above the bottom bar (not over the header); sending a message shows one bubble; a received image appears inside the bubble; times match the phone clock; the chat list shows "Nessuna chat" until contacts arrive.

---

## Self-Review

**Spec coverage:** the user's three complaints map to tasks — *icons missing*: Task 1 (guard), Task 2 (geometries), Tasks 3-5 (every glyph replaced); *buggy UI*: Task 3 (FAB in row 0, empty state, dead controls), Task 12 (stale pairing code); *bugs found by reading all the code*: Task 7 (UTC times, undecodable images), Task 8 + Task 9 (duplicate messages, both directions; `Failed` status instead of a fake `Sent`), Task 10 (broken converter, `Math.Abs` overflow), Task 11 (double event dispatch). Every `.cs`/`.xaml` file of the app was read; files with no defect (`App.xaml.cs`, `SettingsService.cs`, `CryptoHelper.cs`, `Models/Contact.cs`, `Models/ServerConfig.cs`, `Package.appxmanifest`) are deliberately untouched, and `Contact.AvatarUri` (never set, never bound) is left alone as dead-but-harmless.

**Placeholder scan:** no `TBD`/`TODO`/"handle edge cases"; every markup and code step shows the exact old and new text; the new guard script is given in full; every verification step names a command and its expected output.

**Type consistency:** `MediaImage` is a `BitmapImage` produced by `LoadMediaImageAsync()` (Task 7), awaited by `DataService` (Task 8) and `ChatPage.SendImageMessage` (Task 9) and bound as `{Binding MediaImage}` (Task 4) — one name, one type, three sites. `EmptyStatePanel` is declared in Task 3 and only referenced by `UpdateEmptyState()` in Task 6. Icon keys are spelled identically in `App.xaml` (Task 2) and in the three XAML files (Tasks 3-5), and Task 1's guard fails the build if that ever drifts.
