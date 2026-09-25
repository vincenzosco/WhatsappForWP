# WP8.1 icon inline geometry (runtime XamlParseException) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the app crashing on launch, which it did with `Windows.UI.Xaml.Markup.XamlParseException: Failed to assign to property '%0'. [Line: 31 Position: 23]` while creating `WhatsappApp.Pages.ConnectionPage`.

**Architecture:** The previous change made the nine icon geometries *compile* by writing them in element form, but left them as `PathGeometry` resources in `App.xaml` consumed with `Data="{StaticResource IconX}"`. That is a documented WinRT defect: a `Geometry` is not shareable through a `StaticResource`, so the assignment to `Path.Data` throws at runtime (microsoft-ui-xaml issues #1909 and #5780 — same exception text, and the inline `<Path.Data><RectangleGeometry/></Path.Data>` form is the reported working one). This plan inlines each geometry on its consuming `Path`, removes the now-unused resources from `App.xaml`, and rewrites `tools/check-icons.js` to fail if the resource form or `Figures="M..."` ever comes back.

**Tech Stack:** WinRT XAML for Windows Phone 8.1 (`Windows.UI.Xaml`), Node.js 18+ for the repo guards, ImageMagick 7 (`magick`) only for the optional ASCII icon preview.

## Global Constraints

- **C# 5 only.** No `$"..."`, `?.`, expression-bodied members, auto-property initializers, `out int x`, `is Type name`, `nameof`, `_ = ...`. Gate: `node tools/check-csharp5.js` → `OK: 21 C# file(s) are C# 5 compatible.` (no C# changed by this plan).
- **`Data="{StaticResource IconX}"` is forbidden.** It compiles and then throws at runtime. A `PathGeometry` must be the inline value of a `<Path.Data>`, never a `ResourceDictionary` entry.
- **`Figures="M..."` is forbidden.** WP8.1's `PathFigureCollection` type converter has no string form; that attribute is a build error.
- **No icon font.** WP8.1 predates `Segoe MDL2 Assets`; a glyph-based icon button renders blank.
- **Each icon keeps its exact coordinates.** The before/after `--preview` ASCII renders must be identical (order-independent `diff` clean: `OK: 13 inline icon Path(s), 9 distinct icon(s).`).
- **No `WhatsappBridge/**` change.** `cd WhatsappBridge && npm test` stays `pass 29`, `fail 0`.
- **Authoritative gate:** the user's Windows machine, `msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86` → `0 Error(s)`, then uninstall the app on the phone and deploy+launch. Nothing on macOS compiles or runs the app.
- Commit style: `type: why`, imperative.

---

## Diagnostics that fixed the target

| Evidence | Meaning |
| --- | --- |
| `Pages/ConnectionPage.xaml:31` col 23 = `Data="{StaticResource IconBack}"` | the reported line/position is the consuming `Path`, i.e. the failing assignment is `Path.Data` |
| `obj/Debug/App.xaml` and `App.xbf` contain no `Figures` | the icon resources do compile; the failure is at runtime, when the resource value is assigned |
| microsoft-ui-xaml#1909: `Failed to assign to property 'Windows.UI.Xaml.Shapes.Path.Data'` when loading a `Geometry` from a `ResourceDictionary`; #5780: `Data="{StaticResource ...}"` fails while inline `<Path.Data><RectangleGeometry/></Path.Data>` works | root cause: a `Geometry` is not shareable through a `StaticResource` in WinRT |

---

### Task 1: Inline every geometry on its `Path`

**Files:**

- Modify: `WhatsappApp/Pages/ConnectionPage.xaml`, `WhatsappApp/Pages/StatusPage.xaml`,
  `WhatsappApp/Pages/CallsPage.xaml`, `WhatsappApp/Pages/ChatsPage.xaml`,
  `WhatsappApp/Pages/ChatPage.xaml`, `WhatsappApp/Controls/SectionNav.xaml` (13 `Path` sites).

- [x] Replace each `Data="{StaticResource IconX}"` attribute with the geometry inlined as
      `<Path.Data><PathGeometry><PathGeometry.Figures>...` plus an `<!-- IconX -->` comment
      above it naming the icon.

Drop the `Data=` attribute, keep the open tag, and append the comment + `Path.Data` before `</Path>`:

```xml
<Path Stroke="White" StrokeThickness="2.4"
      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
      Width="24" Height="24">
    <!-- IconBack -->
    <Path.Data>
        <PathGeometry>
            <PathGeometry.Figures>
                <PathFigure StartPoint="15.5,4">
                    <PathFigure.Segments>
                        <PolyLineSegment Points="7.5,12 15.5,20"/>
                    </PathFigure.Segments>
                </PathFigure>
            </PathGeometry.Figures>
        </PathGeometry>
    </Path.Data>
</Path>
```

- [x] Expected: `grep -rn 'StaticResource Icon' WhatsappApp --include=*.xaml` finds nothing in the
      source tree (only stale `obj/` copies), and there are 13 `<PathGeometry>` blocks.

### Task 2: Remove the icon resources from `App.xaml`

**Files:** Modify: `WhatsappApp/App.xaml`

- [x] Delete the nine `<PathGeometry x:Key="Icon...">` resources and replace the explanatory
      comment with one that states the runtime rule and points at the guard.

### Task 3: Make the guard enforce the inline form

**Files:** Modify: `tools/check-icons.js`

- [x] Replace the `App.xaml`-resource parser with a scanner over every `.xaml` under
      `WhatsappApp/`: XML comments are blanked for pattern checks (newlines kept for line
      numbers) but read for the `<!-- IconX -->` names.
- [x] Fail on: `Segoe MDL2 Assets`; `Data="{StaticResource IconX}"`; `Figures=`; a
      `<PathGeometry>` that is not the inline value of a `<Path.Data>`; a `<Path>` with no
      inline geometry or no name comment; two copies of the same icon name whose geometry differs.
- [x] Keep `--preview`: render each distinct icon through ImageMagick, `Fill`-only when every
      use sets `Fill` and none sets `Stroke`.

- [x] Self-test by temporarily appending to `ConnectionPage.xaml`:

      <Path Data="{StaticResource IconBack}" Stroke="White"/>
      <PathGeometry Figures="M0,0 L10,10"/>

      Expected: exit 1 with all four problems, then revert.

### Task 4: Verify

- [x] `node tools/check-icons.js` → `OK: 13 inline icon Path(s), 9 distinct icon(s).`
- [x] `node tools/check-icons.js --preview` → per-icon ASCII blocks identical to the previous
      render (`diff <(grep sections | sort)` clean).
- [x] `node tools/check-csharp5.js`, `node tools/check-resw.js --strict`, `xmllint --noout` on
      every XAML, `cd WhatsappBridge && npm test` → all green.
- [x] Update `README.md`, `.agents/skills/{update-the-app,maintain-the-app,test-the-app}/SKILL.md`,
      `.agents/skills/README.md`.

### Task 5: Hand back to the Windows machine

- [x] Build verified on the real toolchain (Visual Studio 2013 / MSBuild 12.0 +
      WP8.1 SDK, inside a Windows 11 ARM64 Parallels VM):
      `msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86`
      → `0 Error(s)`, `WhatsappApp.exe` + `WhatsappApp_1.0.1.0_x86_Debug.appxbundle`
      + `WhatsappServer.exe`, with only the two pre-existing CS0618/CS4014 warnings.
- [x] The same command **fails** with
      `WMC9999: The given key was not present in the dictionary` when run from the
      Parallels shared folder (`C:\Mac\Home\...`). Reproduced with the previous
      XAML too (`git checkout ee4f78a -- WhatsappApp/...`), on a cleaned `obj/`,
      for x86 and AnyCPU, through both `msbuild` and `devenv.com`: the shared
      filesystem is the trigger, not this change. Documented in `README.md`,
      `test-the-app`, `release-the-app`.
- [x] `WhatsappServer` could never compile (`.NET 4.5.1` + `static async Task
      Main` → CS0028 + CS5001, an error `check-csharp5.js` did not catch); it now
      has a C# 5 `static void Main` that waits on `MainAsync`.
- [ ] **Deploy + launch: impossible on this host.** The WP8.1 emulator images are
      x86 and need Hyper-V; the guest is Windows 11 ARM64 (`vmms`/`vmcompute`
      absent, `PROCESSOR_ARCHITECTURE=ARM64`). Needs an x86/x64 Windows machine or
      a USB-attached WP8.1 device.
