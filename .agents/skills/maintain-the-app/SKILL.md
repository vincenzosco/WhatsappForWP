---
name: maintain-the-app
description: Constraints and safe workflow for changing the WhatsApp for Windows Phone 8.1 codebase (C# 5 only, vector icons, resw localization, GOWA adapter). Use before editing any file under WhatsappApp/ or WhatsappBridge/, when a build fails, or when deciding where a change belongs.
---

# Maintaining the app

## What this is

An unofficial WhatsApp client for **Windows Phone 8.1**, built in Visual Studio
2015 with the WP8.1 SDK, that talks to a self-hosted **GOWA**
(go-whatsapp-web-multidevice) server through a thin Node.js adapter.

```
WhatsappApp/            WP8.1 XAML app (C# 5)
  App.xaml(.cs)         icon geometries, start page, resource loader warm-up
  Controls/SectionNav   the shared bottom navigation bar
  Pages/                ChatsPage, StatusPage, CallsPage, ChatPage, ConnectionPage
  Converters/           IValueConverter implementations used by the XAML
  Models/               Contact, ChatMessage, ServerConfig
  Services/             CommunicationService (socket), DataService (state),
                        CryptoHelper (AES-GCM), Loc (strings), ImageHelper,
                        SettingsService, SessionService (last section)
  Strings/<lang>/       Resources.resw - every user-visible string
  Assets/               generated PNGs (tiles, logos, splash)
WhatsappBridge/         Node.js adapter: GOWA HTTP + webhook -> encrypted TCP frames
WhatsappServer/         legacy .NET console project (not part of the app flow)
tools/                  static guards - run them, they are the real gate
.agents/skills/         this directory
```

## Hard constraints

1. **C# 5.** The WP8.1 toolchain compiler rejects C# 6/7 syntax:
   `$"..."`, `?.`, `get => x`, `X Y { get; set; } = v;`, `out int x`,
   `is Contact c`, `nameof(...)`, `_ = ...`.
   Gate: `node tools/check-csharp5.js` (it also flags WP8.1-missing WinRT APIs
   such as `CryptographicBuffer.CreateFromByteArray(byte[], uint, uint)` and
   `ContentDialog.CloseButtonText`).
2. **No icon font.** WP8.1 predates `Segoe MDL2 Assets`; an icon button using it
   renders blank. Icons are `PathGeometry` resources in `App.xaml` consumed as
   `Data="{StaticResource IconX}"`. Every geometry must be both defined and
   used. Geometries are written in element form (`PathFigure` + segments), never
   with `Figures="M..."`: WP8.1's `PathFigureCollection` converter has no string
   form, so that attribute costs 18 build errors. Gate:
   `node tools/check-icons.js` (add `--preview` for an ASCII render).
3. **No hardcoded user-visible strings.** XAML uses `x:Uid` with the property
   that matches the element (`TextBlock`→`.Text`, `Button`→`.Content`,
   `TextBox`→`.PlaceholderText`); C# uses `Loc.Get("Key", "fallback")`. Icon-only
   buttons get their label from `ToolTipService.SetToolTip(button, Loc.Get(...))`
   in the constructor - an `x:Uid` on them would overwrite the `Path`.
   Gate: `node tools/check-resw.js --strict`.
4. **Never create a `ResourceLoader` off the UI thread.** Strings arrive from the
   socket on a background thread; `Loc.Prewarm()` runs on the UI thread at
   startup and `Loc.Get` falls back to its literal instead of throwing.
5. **Back navigation.** Do not subscribe to `HardwareButtons.BackPressed` to
   reimplement Back; the system pops the frame back stack and exits at the root.
   `SectionNav` trims the section page it leaves, so Back exits from any section.
6. **The adapter is a separate, dependency-free Node.js program** (`package.json`
   has zero runtime dependencies). Its tests must keep passing.
7. **One page per section.** A new screen means a new file under `Pages/`, not
   another block inside an existing page, and it must be registered in the
   `.csproj` (a page that is not listed does not exist at build time).

## Workflow for any change

1. `git status --short` - start from a clean tree.
2. Read the file you are about to change **completely**; this codebase keeps
   per-file invariants in comments.
3. Make the change.
4. Run all three guards (and `cd WhatsappBridge && npm test` if you touched the
   adapter).
5. If the change is user-visible, say which page and which string key changed.
6. Commit with a message that says *why* (the repo history is the changelog).

## Where a change belongs

| Change | File |
| --- | --- |
| New section of the app | new `Pages/XxxPage.xaml(.cs)` + a case in `SectionNav` |
| New icon | `PathGeometry` in `App.xaml`, then reference it |
| New string | both `.resw` files (same key), then `x:Uid`/`Loc.Get` |
| Socket/protocol behaviour | `Services/CommunicationService.cs` (+ adapter + its tests) |
| Contacts/messages state | `Services/DataService.cs` (keep `_contactIndex` in sync) |
| State kept across suspend/termination | `Services/SessionService.cs` |
| Image handling | `Services/ImageHelper.cs` |
| A new GOWA call | `WhatsappBridge/gowa-client.js`, a control command in `server.js`, and the app side in `ConnectionPage`/`CommunicationService` |

## Known gotchas

- The app cannot be built on macOS: there is no WP8.1 toolchain. The build gate
  runs on the Windows/Parallels machine.
- `x:Uid` on a `Button` overwrites `Content`; do not combine it with a `Path`.
- A key and the same key with a `.Property` suffix cannot coexist in a `.resw`
  (duplicate resource identifier) - the guard enforces this.
- WP8.1 caches the tile name and icons: after changing them, uninstall the app on
  the device before redeploying.
- `Frame.BackStack` is mutable and used on purpose in `SectionNav`.
- `DataService.Contacts` is a public collection: if something adds a contact
  without `AddContact`, `FindContact` rebuilds its index, so keep inserts going
  through `AddContact` when you can.
- `ChatMessage.LoadMediaImageAsync` is a no-op once `MediaImage` is set; do not
  "fix" that by forcing a re-decode.
