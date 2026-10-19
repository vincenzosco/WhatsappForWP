# The official WP8.1 look, rebuilt in the WinRT app Design

**Date:** 2026-10-19
**Status:** approved in chat (approach and accent colour); this document is the written spec.

**Goal:** the app on screen is the released WhatsApp 2.17.52 WP8.1 package, rebuilt on the
project we already have. Not an inspired-by restyle: the same tab header, the same rows, the
same chat chrome, the same colours, the same artwork. No Silverlight port — the app stays
WinRT and the `Pivot` is emulated.

**Acceptance criteria (what "done" means):**

1. The app opens on one `HomePage` with three sections (Chat / Stato / Chiamate), switched by
   tapping the header and by swiping left/right, and Back from Home exits the app.
2. The tab header matches the numbers below within 1 px; the selected tab and its 2 px
   underline use the phone's accent colour, and the unread badge is a 24 px circle.
3. The chat list, the chat screen and the message bubbles match the numbers below.
4. Changing the phone accent changes the app bar, the selected tab, the underline,
   the badge fill and the sent bubble on the next start (fallback `#25D366` when the
   platform does not answer).
5. `node tools/check-*.js`, `node tools/test-protocol.js` and
   `msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86` are green,
   and the ARM bundle installs and launches on the connected phone.

## Evidence (why these numbers)

Everything here was read out of `C:\Users\Vincenzo\Documents\WhatsApp v2.17.52.0
[file.windowsviet.com].xap`, not guessed:

- the package is WP8.1 Silverlight; 149 XAML files are plain text inside
  `WhatsApp.g.resources` and were extracted to `.tools/xap-resources/`;
- `WhatsApp.dll` was disassembled with `ildasm` into `.tools/wa.il`. The home screen is
  `WhatsApp.ContactsPage` (route `/PageSelect`): fields `chatsPivotItem`, `statusPivotItem`,
  `callsPivotItem`, `favsPivotItem`, `TabHeader`, `mainAppBar`, `callsAppBar`, `statusAppBar`,
  `ChatListControl`, `WaStatusList`, `CallHistoryList`;
- `TabHeaderControl` builds the tabs: label `FontSize 32`, selected label brush
  `PhoneAccentBrush`, idle `PhoneForegroundBrush`, selected indicator a 2 px `Rectangle` on
  the accent, badge an `Ellipse 24x24` with a 16 px number and `Margin 8,3,0,0`;
- `ChatItemControl` builds the row: name `FontSize 20`, preview `20` at reduced opacity,
  unread pill `25x18`, avatar column 62; `ChatListControl` puts each row at `Margin 0,24,24,0`
  and the list footer at `24,24,24,88` (88 = the app bar);
- `ChatPageTitlePanel`: header `Height 60`, white text, accent background;
- `ChatPage.xaml` keeps the wallpaper in row 0 behind the list, the input bar in row 3;
- `themes/generic.xaml`/`app.xaml` reference `PhoneAccentBrush`, `PhoneForegroundBrush`,
  `PhoneBackgroundBrush`: the official look follows the phone theme, it is not a fixed green;
- `Windows.winmd` 8.1 contains `Hub` and no `Pivot`/`PivotItem`, so a native Pivot is not
  available to this project — hence the emulation below.

## Architecture

```
Pages/HomePage.xaml(.cs)         the only section host: header + FlipView + app bar
Controls/TabHeader.xaml(.cs)     3 tabs, 32 px labels, accent underline, badges
Controls/ChatsPane.xaml(.cs)     the chat list and its actions (from ChatsPage)
Controls/StatusPane.xaml(.cs)    the status feed (from StatusPage)
Controls/CallsPane.xaml(.cs)     the call log (from CallsPage)
Controls/AppBar.cs               the WP8 app bar: 72 px, accent, per-tab actions
Services/Accent.cs               UISettings accent -> brushes, fallback #25D366
```

`ChatsPage`, `StatusPage`, `CallsPage` and `SectionNav` are removed from the tree and from
`WhatsappApp.csproj`; their bodies move into the three panes, which keeps each unit short and
lets the panes keep their existing code-behind (bindings, holding menus, refresh).

`HomePage` owns the `FlipView` (a WinRT control, present in 8.1) whose items are the three
panes. The header is drawn by `HomePage`, not by the panes: `TabHeader` raises
`Selected(index)` and `HomePage` sets `FlipView.SelectedIndex`; `FlipView.SelectionChanged`
moves the underline. The panes are created once and kept, so list state and scroll position
survive a swipe. Back at Home exits (the app's existing `BackPressed` wiring already does
this for the section roots).

`AppBar` is a `Grid` 72 px tall at the bottom, accent background, 48x48 icon buttons with a
12 px label under each and an optional overflow count. It is not the WinRT CommandBar: the
official app uses the WP8 app bar and this is the closest drawing of it. Only actions that
exist are shown (new chat, refresh calls, settings, diagnostics); the official camera button
on the Status tab is omitted because this client cannot post a status, and a dead button is
worse than one button less.

## Screens

**Home / tab header.** White page background. Tab labels 32 px at `Margin 0,0,0,0`,
selected accent + 2 px accent rectangle beneath its text (width of the label), idle
`#333333`. Badge: accent circle 24 px, white 16 px number, margin `8,3,0,0`. Header total
height ≈ 60 px including the underline. The list starts under it with the official margins.

**Chat list.** Row 24 px from the left edge, avatar 62 px circle (initials fallback,
picture via `Border CornerRadius=31`), 12 px gap, name 20 px semibold `#333333`,
preview 20 px `#939393` single line, time 20 px right `#939393`, unread pill 25x18 with an
11 px white number on the accent, pin/mute marks 12 px. Separator 1 px `#14000000` inset
to the text column. Row height 76 (62 avatar + 7 top/bottom).

**Status and calls.** The same 24 px left margin and 20 px typography; status rows keep the
44 px avatar and 3-line preview, calls rows keep time/detail. They are the current item
templates restyled, not new data.

**Chat screen.** Header 60 px, accent background: back chevron 48x48, avatar 40 px,
name 23 px white, presence 13 px white at 70%. Wallpaper `Assets/ChatBackground.jpg` as the
page background (already done). Received bubble `#EFEFEF`, sent bubble the accent colour,
radius and tail measured from `MessageBubble` in `.tools/wa.il` during implementation
(the colour is the fixed point; the geometry is not guessed here), text 16 px `#333333`,
time 11 px `#939393` inside the bubble, own time white at 70% on the accent. Media inside the bubble radius 3. Input bar 56 px white, separated by a 1 px
`#14000000` line: attach, emoji/keyboard, mic and send drawn with the official
`inputbar-*.png` (light/dark variant per theme), send on the accent. The typing bubble uses
the received bubble colour.

**Secondary pages** (contact info, connection, pairing, diagnostics): `PageTitlePanel` look —
title 54 px SemiLight, subtitle 22 px subtle, margin `-2,-7,0,28`; lists on white with 24 px
leading margin; the current teal headers are removed (they were not in the official app).

## Colour and theme

`Accent` reads `UISettings.GetColorValue(UIColorType.Accent)` once per start (and on resume),
exposes `AccentBrush`, `AccentDarkBrush` (multiplied 0.8 for pressed states) and
`ContrastBrush` (white on the accent when its luminance is low, `#333333` otherwise).
Fallback accent `#25D366` if the call throws. `App.xaml` keeps the fixed palette for
everything that is not accent-driven: background `#FFFFFFFF`, primary text `#333333`,
secondary `#939393`, separator `#14000000`, received bubble `#EFEFEF`, header text white.

Dark theme is out of scope for this change: the official app follows it through
`PhoneBackgroundBrush`, but every page here is light and mixing the two would produce
unreadable combinations. The hardcoded colours stay and the spec for a dark pass is a
separate task.

## Testing

- static guards: `check-actions` (every fixed-size Button still needs `MinWidth=0
  MinHeight=0`), `check-icons` (the new app bar icons are `Path` with an `IconX` comment;
  PNG transport icons are images and exempt), `check-xaml-names` (three panes = three
  namescopes), `check-resw` (the existing `Nav_*` keys, reworded to the official tab
  labels, stay in both languages), `check-project-files`
  (the removed pages and new files in the csproj), `check-csharp5` (`UISettings` and
  `FlipView` are in the WP8.1 projection — the guard proves it);
- protocol suite unchanged: 267 tests, because no wire code is touched;
- `msbuild` x86 Rebuild and the ARM bundle; install on the connected device with
  `AppDeployCmd /updatelaunch`, then walk Home → status → calls → chat → back and read
  `diag.log` for the start-up lines (accent resolved, sections bound).

## Risks

- **Memory:** three panes alive at once. Lists already virtualize and avatars come from the
  capped cache; `MemoryWatcher` stays the authority and the panes are not bound when empty.
- **FlipView feel:** the swipe animation is not the Silverlight Pivot's. Accepted in the
  approach choice; the layout and the header are what read as "the official app".
- **Accent API:** `UISettings` exists on 8.1, but only the phone can prove it answers; the
  fallback is exercised by a deliberate throw in a unit-style probe (`Accent.FromColor`).
- **Removing pages:** `App.OnLaunched`, the back-stack trimming and several guards name
  `ChatsPage`/`StatusPage`/`CallsPage`; every reference is updated in the same change, and
  `check-project-files` plus a full Rebuild catch a missed one.
