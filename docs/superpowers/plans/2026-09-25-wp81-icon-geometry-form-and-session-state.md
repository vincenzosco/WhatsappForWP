# WP8.1 icon geometry form + session state Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `WhatsappApp/App.xaml` compile on Windows Phone 8.1 by writing the nine icon geometries in element form, teach `tools/check-icons.js` to enforce that form, and implement the two template `TODO`s in `App.xaml.cs` so the app resumes on the section it was in.

**Architecture:** The nine `PathGeometry` resources lose their `Figures="M..."` attribute (WP8.1's XAML type converter for `PathFigureCollection` refuses a string) and gain explicit `<PathGeometry.Figures>` → `<PathFigure StartPoint>` → `<PathFigure.Segments>` → `<LineSegment>`/`<PolyLineSegment>`/`<ArcSegment>` elements, which is the shape the Windows Phone Silverlight XAML vocabulary documents. `tools/check-icons.js` is rewritten to parse that form, fail loudly on a `Figures=` attribute, and regenerate the equivalent path mini-language so the ImageMagick ASCII preview keeps working. A new `Services/SessionService.cs` persists the current section through `ApplicationData.Current.LocalSettings`, which `App.OnLaunched` reads back after `ApplicationExecutionState.Terminated` and `App.OnSuspending` writes.

**Tech Stack:** C# 5 / WinRT XAML for Windows Phone 8.1, `Windows.Storage.ApplicationData`, Node.js 18+ for the repo guards, ImageMagick 7 (`magick`) only for the optional ASCII icon preview.

## Global Constraints

- **C# 5 only.** No `$"..."`, `?.`, `get => x`, expression-bodied members, auto-property initializers, `out int x`, `is Type name`, `nameof(...)`, `_ = ...`. Gate: `node tools/check-csharp5.js` → `OK: 20 C# file(s) are C# 5 compatible.` (21 after Task 3).
- **No icon font.** WP8.1 predates `Segoe MDL2 Assets`; an icon button using it renders blank.
- **The icon form is not a preference.** `PathGeometry.Figures="M..."` is a hard build error on this toolchain (18 errors: `The TypeConverter for "PathFigureCollection" does not support converting from a string.` ×9 and `Cannot assign text value '...' into property 'Figures' of type 'PathFigureCollection'` ×9). The element form below is the only form allowed in this repo after this plan.
- **Each icon keeps its exact coordinates.** The generated mini-language must be byte-identical to today's `Figures` strings; the before/after `--preview` ASCII renders must be identical (`diff` clean).
- **No `WhatsappBridge/**` change.** `cd WhatsappBridge && npm test` must stay `pass 29`, `fail 0`.
- **No new XAML file.** `WhatsappApp.csproj` lists sources explicitly; only `Services\SessionService.cs` is added to it.
- **Authoritative gate:** the user's Windows machine, `msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86` → `0 Error(s)`. Nothing on macOS compiles the app.
- Commit style: `type: why`, imperative, no `Co-Authored-By` other than the repo's own footer.

---

## File Structure

| File | Responsibility | Change |
| --- | --- | --- |
| `tools/check-icons.js` | Guard: icon geometry form, `Segoe MDL2 Assets`, defined ↔ referenced, optional ASCII preview. | rewrite the parser |
| `WhatsappApp/App.xaml` | App resources: brushes + the nine 24×24 icon geometries. | 9 geometries to element form |
| `WhatsappApp/Services/SessionService.cs` | Persists the current section across suspend/termination. | new |
| `WhatsappApp/App.xaml.cs` | Start page choice, suspend handler. | read/write the section |
| `WhatsappApp/Controls/SectionNav.xaml.cs` | Section ↔ page mapping. | `PageFor` becomes public |
| `WhatsappApp/WhatsappApp.csproj` | Explicit source list + `PRIResource` + `DefaultLanguage`. | +`SessionService.cs` |
| `.agents/skills/{update-the-app,maintain-the-app,test-the-app}/SKILL.md`, `README.md` | How to keep the app working. | element-form rule |

---

### Task 1: Make the icon guard enforce the WP8.1 form

**Files:**
- Modify: `tools/check-icons.js` (whole file)

**Interfaces:**
- Consumes: `WhatsappApp/App.xaml` (resource keys), every `WhatsappApp/**/*.xaml` (references).
- Produces: `node tools/check-icons.js` → exit `0` printing `OK: N icon(s) defined, M reference(s) resolved.`; exit `1` listing one line per problem, including `WhatsappApp/App.xaml:<line>: Figures="..." does not compile on WP8.1 ...`. `--preview` additionally renders each geometry through ImageMagick and prints ASCII art.

- [ ] **Step 1: Capture the current preview as the reference render**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-icons.js --preview > /tmp/icons-before.txt
```

Expected: exit `0`, `OK: 9 icon(s) defined, 9 reference(s) resolved.`

- [ ] **Step 2: Write the failing guard**

Replace the whole content of `tools/check-icons.js` with:

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
 * The geometries are written in *element* form (PathFigure + LineSegment /
 * PolyLineSegment / ArcSegment). The WP8.1 XAML compiler rejects the path
 * mini-language in PathGeometry.Figures with
 *
 *   The TypeConverter for "PathFigureCollection" does not support converting
 *   from a string.
 *
 * so a Figures="M..." attribute is a build error, not a style preference, and
 * this guard fails on it by name. The element form is the one the Windows
 * Phone Silverlight XAML vocabulary documents.
 *
 * Usage:
 *   node tools/check-icons.js            # form + references + font check
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

const POINT = /^-?\d+(\.\d+)?,-?\d+(\.\d+)?$/;
const POINTS = /^-?\d+(\.\d+)?,-?\d+(\.\d+)?(\s+-?\d+(\.\d+)?,-?\d+(\.\d+)?)*$/;
const SEGMENT = /<(LineSegment|PolyLineSegment|ArcSegment)(?=[\s/>])([^>]*?)\/?>/g;

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

function attribute(tag, name) {
  const m = tag.match(new RegExp(`\\b${name}="([^"]*)"`));
  return m ? m[1] : null;
}

const appXaml = fs.readFileSync(APP_XAML, 'utf8');
const problems = [];

// The form WP8.1 rejects, by name: one error per attribute.
appXaml.split(/\r?\n/).forEach((line, i) => {
  if (/\bFigures\s*=/.test(line)) {
    problems.push('WhatsappApp/App.xaml:' + (i + 1) +
      ': Figures="..." does not compile on WP8.1 (PathFigureCollection has no' +
      ' string converter) -> write PathFigure + LineSegment elements instead');
  }
});

/**
 * Parses the element form and rebuilds the equivalent path mini-language (used
 * only by --preview, so the ASCII render keeps working unchanged).
 */
const defined = new Map();
for (const geometry of appXaml.matchAll(/<PathGeometry(?=[\s>])([^>]*)>([\s\S]*?)<\/PathGeometry>/g)) {
  const key = attribute(geometry[1], 'x:Key');
  if (!key) continue;
  const where = 'WhatsappApp/App.xaml: ' + key;
  if (defined.has(key)) problems.push(where + ': duplicate resource key');

  const figures = [];
  for (const figure of geometry[2].matchAll(/<PathFigure(?=[\s>])([^>]*)>([\s\S]*?)<\/PathFigure>/g)) {
    const start = attribute(figure[1], 'StartPoint');
    if (!start || !POINT.test(start)) {
      problems.push(where + ': PathFigure needs StartPoint="x,y"');
      continue;
    }

    let mini = 'M' + start;
    let segments = 0;
    for (const segment of figure[2].matchAll(SEGMENT)) {
      const kind = segment[1];
      const tag = segment[2];
      if (kind === 'LineSegment') {
        const point = attribute(tag, 'Point');
        if (!point || !POINT.test(point)) {
          problems.push(where + ': LineSegment needs Point="x,y"');
          continue;
        }
        mini += ' L' + point;
      } else if (kind === 'PolyLineSegment') {
        const points = (attribute(tag, 'Points') || '').trim();
        if (!POINTS.test(points)) {
          problems.push(where + ': PolyLineSegment needs Points="x,y x,y ..."');
          continue;
        }
        mini += ' L' + points.split(/\s+/).join(' L');
      } else {
        const size = attribute(tag, 'Size');
        const point = attribute(tag, 'Point');
        if (!size || !POINT.test(size) || !point || !POINT.test(point)) {
          problems.push(where + ': ArcSegment needs Size="rx,ry" and Point="x,y"');
          continue;
        }
        const rotation = attribute(tag, 'RotationAngle') || '0';
        const large = attribute(tag, 'IsLargeArc') === 'True' ? 1 : 0;
        const sweep = attribute(tag, 'SweepDirection') === 'Clockwise' ? 1 : 0;
        mini += ' A' + size + ' ' + rotation + ' ' + large + ' ' + sweep + ' ' + point;
      }
      segments++;
    }

    if (segments === 0) problems.push(where + ': PathFigure has no segment');
    if (attribute(figure[1], 'IsClosed') === 'True') mini += ' Z';
    figures.push(mini);
  }

  if (figures.length === 0) problems.push(where + ': PathGeometry has no PathFigure');
  defined.set(key, figures.join(' '));
}

const files = walk(APP, []).filter((f) => f !== APP_XAML);
const used = new Set();
const withFill = new Set();
const withStroke = new Set();

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
      if (/\bFill\s*=/.test(line)) withFill.add(m[1]);
      if (/\bStroke\s*=/.test(line)) withStroke.add(m[1]);
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
      // Filled only when every use is Fill and no use is Stroke: an open
      // outline drawn with the even-odd fill rule is a blob, not an icon.
      const filled = withFill.has(name) && !withStroke.has(name);
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

- [ ] **Step 3: Run the guard to verify it fails on today's App.xaml**

```bash
node tools/check-icons.js
```

Expected: exit `1`, nine lines of `WhatsappApp/App.xaml:21..29: Figures="..." does not compile on WP8.1 ...`, then `9 icon problem(s).` This is the reproduction of the user's build error, in a form that runs on macOS.

- [ ] **Step 4: Commit**

```bash
git add tools/check-icons.js
git commit -m "test: make check-icons reject the PathGeometry.Figures string form"
```

---

### Task 2: Write the nine icon geometries in element form

**Files:**
- Modify: `WhatsappApp/App.xaml:17-29`

**Interfaces:**
- Consumes: nothing.
- Produces: the same nine resource keys (`IconBack`, `IconCalls`, `IconChats`, `IconClose`, `IconNewChat`, `IconPhoto`, `IconSend`, `IconSettings`, `IconStatus`), still `Geometry` values, still consumed as `Data="{StaticResource IconX}"`. Coordinates unchanged.

- [ ] **Step 1: Replace the geometry block**

Replace `WhatsappApp/App.xaml` lines 17-29 (the comment plus the nine one-line `PathGeometry` resources) with:

```xml
            <!-- Icon geometries (24x24). Vector paths instead of an icon font:
                 WP8.1 has no "Segoe MDL2 Assets", so glyph-based buttons were
                 blank. Every geometry has to be referenced by a page, so the
                 ones whose last control was removed are gone too.

                 They are written in element form on purpose. WP8.1's XAML type
                 converter for PathFigureCollection refuses a string, so
                 <PathGeometry x:Key="IconX" Figures="M..."/> does not compile
                 ("The TypeConverter for \"PathFigureCollection\" does not
                 support converting from a string."). PathGeometry.Figures ->
                 PathFigure -> PathFigure.Segments -> PathSegment is the form
                 the Windows Phone Silverlight XAML vocabulary documents. -->
            <PathGeometry x:Key="IconBack">
                <PathGeometry.Figures>
                    <PathFigure StartPoint="15.5,4">
                        <PathFigure.Segments>
                            <PolyLineSegment Points="7.5,12 15.5,20"/>
                        </PathFigure.Segments>
                    </PathFigure>
                </PathGeometry.Figures>
            </PathGeometry>

            <PathGeometry x:Key="IconCalls">
                <PathGeometry.Figures>
                    <PathFigure StartPoint="16.6,16.6">
                        <PathFigure.Segments>
                            <ArcSegment Size="6.5,6.5" RotationAngle="0" IsLargeArc="False"
                                        SweepDirection="Clockwise" Point="7.4,7.4"/>
                        </PathFigure.Segments>
                    </PathFigure>
                </PathGeometry.Figures>
            </PathGeometry>

            <PathGeometry x:Key="IconChats">
                <PathGeometry.Figures>
                    <PathFigure StartPoint="4,5" IsClosed="True">
                        <PathFigure.Segments>
                            <PolyLineSegment Points="20,5 20,15.5 10.5,15.5 5.5,20 5.5,15.5 4,15.5"/>
                        </PathFigure.Segments>
                    </PathFigure>
                </PathGeometry.Figures>
            </PathGeometry>

            <PathGeometry x:Key="IconClose">
                <PathGeometry.Figures>
                    <PathFigure StartPoint="6,6">
                        <PathFigure.Segments>
                            <LineSegment Point="18,18"/>
                        </PathFigure.Segments>
                    </PathFigure>
                    <PathFigure StartPoint="18,6">
                        <PathFigure.Segments>
                            <LineSegment Point="6,18"/>
                        </PathFigure.Segments>
                    </PathFigure>
                </PathGeometry.Figures>
            </PathGeometry>

            <PathGeometry x:Key="IconNewChat">
                <PathGeometry.Figures>
                    <PathFigure StartPoint="4,5" IsClosed="True">
                        <PathFigure.Segments>
                            <PolyLineSegment Points="20,5 20,15.5 10.5,15.5 5.5,20 5.5,15.5 4,15.5"/>
                        </PathFigure.Segments>
                    </PathFigure>
                    <PathFigure StartPoint="12,7.6">
                        <PathFigure.Segments>
                            <LineSegment Point="12,13"/>
                        </PathFigure.Segments>
                    </PathFigure>
                    <PathFigure StartPoint="9.3,10.3">
                        <PathFigure.Segments>
                            <LineSegment Point="14.7,10.3"/>
                        </PathFigure.Segments>
                    </PathFigure>
                </PathGeometry.Figures>
            </PathGeometry>

            <PathGeometry x:Key="IconPhoto">
                <PathGeometry.Figures>
                    <PathFigure StartPoint="3.5,5.5" IsClosed="True">
                        <PathFigure.Segments>
                            <PolyLineSegment Points="20.5,5.5 20.5,18.5 3.5,18.5"/>
                        </PathFigure.Segments>
                    </PathFigure>
                    <PathFigure StartPoint="3.5,15">
                        <PathFigure.Segments>
                            <PolyLineSegment Points="8.5,10 12.5,14 15.5,11.5 20.5,15.5"/>
                        </PathFigure.Segments>
                    </PathFigure>
                    <PathFigure StartPoint="7.5,8">
                        <PathFigure.Segments>
                            <ArcSegment Size="1.3,1.3" RotationAngle="0" IsLargeArc="True"
                                        SweepDirection="Clockwise" Point="7.5,10.6"/>
                            <ArcSegment Size="1.3,1.3" RotationAngle="0" IsLargeArc="True"
                                        SweepDirection="Clockwise" Point="7.5,8"/>
                        </PathFigure.Segments>
                    </PathFigure>
                </PathGeometry.Figures>
            </PathGeometry>

            <PathGeometry x:Key="IconSend">
                <PathGeometry.Figures>
                    <PathFigure StartPoint="2.5,20.5" IsClosed="True">
                        <PathFigure.Segments>
                            <PolyLineSegment Points="21.5,12 2.5,3.5 2.5,9.8 15,12 2.5,14.2"/>
                        </PathFigure.Segments>
                    </PathFigure>
                </PathGeometry.Figures>
            </PathGeometry>

            <PathGeometry x:Key="IconSettings">
                <PathGeometry.Figures>
                    <PathFigure StartPoint="4,6.5">
                        <PathFigure.Segments>
                            <LineSegment Point="20,6.5"/>
                        </PathFigure.Segments>
                    </PathFigure>
                    <PathFigure StartPoint="4,12">
                        <PathFigure.Segments>
                            <LineSegment Point="20,12"/>
                        </PathFigure.Segments>
                    </PathFigure>
                    <PathFigure StartPoint="4,17.5">
                        <PathFigure.Segments>
                            <LineSegment Point="20,17.5"/>
                        </PathFigure.Segments>
                    </PathFigure>
                    <PathFigure StartPoint="9,4.5">
                        <PathFigure.Segments>
                            <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="True"
                                        SweepDirection="Clockwise" Point="9,8.5"/>
                            <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="True"
                                        SweepDirection="Clockwise" Point="9,4.5"/>
                        </PathFigure.Segments>
                    </PathFigure>
                    <PathFigure StartPoint="15,10">
                        <PathFigure.Segments>
                            <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="True"
                                        SweepDirection="Clockwise" Point="15,14"/>
                            <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="True"
                                        SweepDirection="Clockwise" Point="15,10"/>
                        </PathFigure.Segments>
                    </PathFigure>
                    <PathFigure StartPoint="9,15.5">
                        <PathFigure.Segments>
                            <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="True"
                                        SweepDirection="Clockwise" Point="9,19.5"/>
                            <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="True"
                                        SweepDirection="Clockwise" Point="9,15.5"/>
                        </PathFigure.Segments>
                    </PathFigure>
                </PathGeometry.Figures>
            </PathGeometry>

            <PathGeometry x:Key="IconStatus">
                <PathGeometry.Figures>
                    <PathFigure StartPoint="12,4" IsClosed="True">
                        <PathFigure.Segments>
                            <ArcSegment Size="8,8" RotationAngle="0" IsLargeArc="True"
                                        SweepDirection="Clockwise" Point="12,20"/>
                            <ArcSegment Size="8,8" RotationAngle="0" IsLargeArc="True"
                                        SweepDirection="Clockwise" Point="12,4"/>
                        </PathFigure.Segments>
                    </PathFigure>
                    <PathFigure StartPoint="12,7.5">
                        <PathFigure.Segments>
                            <PolyLineSegment Points="12,12.3 16,14.5"/>
                        </PathFigure.Segments>
                    </PathFigure>
                </PathGeometry.Figures>
            </PathGeometry>
```

- [ ] **Step 2: Prove the geometry did not change**

```bash
node tools/check-icons.js --preview > /tmp/icons-after.txt
diff /tmp/icons-before.txt /tmp/icons-after.txt && echo "IDENTICAL RENDER"
xmllint --noout WhatsappApp/App.xaml && echo "WELL FORMED"
```

Expected: `IDENTICAL RENDER` and `WELL FORMED`. The guard regenerates the mini-language from the elements, so a byte-identical ASCII render proves every coordinate survived the rewrite. If `diff` shows anything, the element form is wrong — fix the XAML, never the reference file.

- [ ] **Step 3: Run the guard clean**

```bash
node tools/check-icons.js
```

Expected: exit `0`, `OK: 9 icon(s) defined, 9 reference(s) resolved.`

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/App.xaml
git commit -m "fix: write the icon geometries in the form WP8.1 compiles"
```

---

### Task 3: Implement the two App.xaml.cs TODOs

**Files:**
- Create: `WhatsappApp/Services/SessionService.cs`
- Modify: `WhatsappApp/App.xaml.cs` (the `OnLaunched` start-page block and `OnSuspending`)
- Modify: `WhatsappApp/Controls/SectionNav.xaml.cs` (`PageFor` becomes public)
- Modify: `WhatsappApp/WhatsappApp.csproj` (register the new source)

**Interfaces:**
- Consumes: `WhatsappApp.Controls.AppSection` (`Chats | Status | Calls`), `SettingsService.HasSavedSettings`.
- Produces: `SessionService.Section` (`AppSection` get/set, persisted in `ApplicationData.Current.LocalSettings["Session.Section"]`, defaults to `AppSection.Chats`); `SectionNav.PageFor(AppSection)` → `Type`.

- [ ] **Step 1: Add the session store**

Create `WhatsappApp/Services/SessionService.cs`:

```csharp
using System;
using Windows.Storage;
using WhatsappApp.Controls;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Stato della sessione che sopravvive alla terminazione dell'app: in quale
    /// sezione si trovava l'utente. Contatti e messaggi invece non si salvano:
    /// arrivano dall'adapter, che li rimanda ad ogni connessione.
    /// </summary>
    public static class SessionService
    {
        private const string KeySection = "Session.Section";

        // Snapshot in memoria, come SettingsService: la sezione viene letta
        // all'avvio e scritta alla sospensione, non ad ogni navigazione.
        private static bool _loaded;
        private static AppSection _section = AppSection.Chats;

        private static ApplicationDataContainer Settings
        {
            get { return ApplicationData.Current.LocalSettings; }
        }

        /// <summary>Sezione da mostrare all'avvio dopo una terminazione.</summary>
        public static AppSection Section
        {
            get
            {
                EnsureLoaded();
                return _section;
            }
            set
            {
                EnsureLoaded();
                _section = value;
                Settings.Values[KeySection] = (int)value;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            object stored;
            if (Settings.Values.TryGetValue(KeySection, out stored) && stored != null)
            {
                int value;
                if (int.TryParse(stored.ToString(), out value)
                    && value >= (int)AppSection.Chats
                    && value <= (int)AppSection.Calls)
                {
                    _section = (AppSection)value;
                }
            }
        }
    }
}
```

- [ ] **Step 2: Register it in the project**

In `WhatsappApp/WhatsappApp.csproj`, add to the `Services` group (next to `<Compile Include="Services\SettingsService.cs" />`):

```xml
    <Compile Include="Services\SessionService.cs" />
```

- [ ] **Step 3: Expose the section → page mapping**

In `WhatsappApp/Controls/SectionNav.xaml.cs`, change the `PageFor` signature and its doc comment:

```csharp
        /// <summary>Pagina di una sezione (la usa anche l'avvio dell'app).</summary>
        public static Type PageFor(AppSection section)
```

- [ ] **Step 4: Read the state back on a terminated launch**

In `WhatsappApp/App.xaml.cs`, add `using WhatsappApp.Controls;` next to `using WhatsappApp.Pages;`, then delete the empty template block

```csharp
                if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                {
                    // TODO: Caricare lo stato dall'applicazione sospesa in precedenza
                }
```

and replace the start-page decision

```csharp
                Type startPage = SettingsService.HasSavedSettings ? typeof(ChatsPage) : typeof(ConnectionPage);
```

with

```csharp
                // Dopo una terminazione (l'OS ha chiuso il processo mentre l'app era
                // sospesa) si riparte dalla sezione in cui l'utente si trovava,
                // invece che sempre dalle chat. Contatti e messaggi non si
                // ripristinano: l'adapter li rimanda alla connessione.
                Type startPage;
                if (!SettingsService.HasSavedSettings)
                {
                    startPage = typeof(ConnectionPage);
                }
                else if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                {
                    startPage = SectionNav.PageFor(SessionService.Section);
                }
                else
                {
                    startPage = typeof(ChatsPage);
                }
```

- [ ] **Step 5: Save the state on suspend**

Replace the body of `OnSuspending` in `WhatsappApp/App.xaml.cs`:

```csharp
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();

            // Se l'OS termina il processo mentre l'app e' sospesa, OnLaunched
            // riparte da qui.
            SessionService.Section = CurrentSection();

            // Nessuna attivita' di background da fermare: l'unica cosa viva e' il
            // socket verso l'adapter, e chiuderlo qui lascerebbe l'app segnata
            // come connessa ma muta alla ripresa, perche' non esiste un percorso
            // di riconnessione. Il processo viene congelato e il socket resta
            // aperto: non toccarlo.

            deferral.Complete();
        }

        /// <summary>Sezione della pagina in primo piano (la chat sta nelle chat).</summary>
        private static AppSection CurrentSection()
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null) return AppSection.Chats;
            if (frame.Content is StatusPage) return AppSection.Status;
            if (frame.Content is CallsPage) return AppSection.Calls;
            return AppSection.Chats;
        }
```

- [ ] **Step 6: Verify C# 5 and the guard suite**

```bash
node tools/check-csharp5.js
node tools/check-icons.js
node tools/check-resw.js --strict
for f in WhatsappApp/*.xaml WhatsappApp/Pages/*.xaml WhatsappApp/Controls/*.xaml; do
  xmllint --noout "$f" || echo "MALFORMED: $f"
done
```

Expected: `OK: 21 C# file(s) are C# 5 compatible.`, `OK: 9 icon(s) defined, 9 reference(s) resolved.`, `OK: 82 key(s) ...`, no `MALFORMED`. `is StatusPage` / `is CallsPage` are C# 5 (type test, not the C# 7 pattern), and `AppSection` is in scope through the new `using WhatsappApp.Controls;`.

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Services/SessionService.cs WhatsappApp/WhatsappApp.csproj WhatsappApp/App.xaml.cs WhatsappApp/Controls/SectionNav.xaml.cs
git commit -m "feat: restore the section after the app is terminated, drop the template TODOs"
```

---

### Task 4: Document the icon form and run the whole gate

**Files:**
- Modify: `.agents/skills/update-the-app/SKILL.md` ("Add an icon", step 1)
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (constraint 2 and the "Where a change belongs" table)
- Modify: `.agents/skills/test-the-app/SKILL.md` (guard table row)
- Modify: `README.md` (`### Icone, tile e splash screen`)

- [ ] **Step 1: Update the icon recipe**

In `.agents/skills/update-the-app/SKILL.md`, replace step 1 of "Add an icon" with:

```markdown
1. `App.xaml`: a `<PathGeometry x:Key="IconMyThing">` in **element form**, 24x24
   view box - `<PathGeometry.Figures><PathFigure StartPoint="x,y">` then
   `<PathFigure.Segments>` with `LineSegment`/`PolyLineSegment`/`ArcSegment`,
   `IsClosed="True"` for a `Z`. A `Figures="M..."` attribute is a **build
   error** on WP8.1: its `PathFigureCollection` type converter has no string
   form, so the mini-language only ever works inside the guard's preview.
```

- [ ] **Step 2: Update the constraint**

In `.agents/skills/maintain-the-app/SKILL.md`, constraint 2, after "Every geometry must be both defined and used." add:

```markdown
   Geometries are written in element form (`PathFigure` + segments), never with
   `Figures="M..."`: WP8.1's `PathFigureCollection` converter has no string form,
   so that attribute costs 18 build errors. Gate: `node tools/check-icons.js`
   (add `--preview` for an ASCII render).
```

and add a row to "Where a change belongs":

```markdown
| State kept across suspend/termination | `Services/SessionService.cs` |
```

- [ ] **Step 3: Update the test guide**

In `.agents/skills/test-the-app/SKILL.md`, the `check-icons.js` row of the guard table becomes:

```markdown
| `check-icons.js` | A blank icon button (`Segoe MDL2 Assets`), a `{StaticResource IconX}` that does not exist, a geometry nothing uses, and `Figures="M..."` - the string form of `PathGeometry.Figures`, which does not compile on WP8.1. |
```

- [ ] **Step 4: Update the README**

In `README.md`, `### Icone, tile e splash screen`, replace the sentence

```markdown
vettoriali definiti una sola volta in `WhatsappApp/App.xaml`
(`PathGeometry x:Key="Icon…"`) e consumati con `Data="{StaticResource Icon…}"`.
```

with

```markdown
vettoriali definiti una sola volta in `WhatsappApp/App.xaml`
(`PathGeometry x:Key="Icon…"`) e consumati con `Data="{StaticResource Icon…}"`.
Le geometrie sono scritte in forma di elementi (`PathFigure` + `LineSegment` /
`PolyLineSegment` / `ArcSegment`): su WP8.1 il convertitore di
`PathFigureCollection` non accetta la stringa, quindi
`Figures="M…"` **non compila** (`The TypeConverter for "PathFigureCollection"
does not support converting from a string.`).
```

- [ ] **Step 5: Run the whole gate**

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict
cd WhatsappBridge && npm test
```

Expected: three `OK:` lines, then `pass 29`, `fail 0`.

- [ ] **Step 6: Commit and push**

```bash
git add README.md .agents/skills
git commit -m "docs: record the icon geometry form WP8.1 compiles and the session state"
git push origin master
```

- [ ] **Step 7: Hand the build to the user**

On the Windows machine:

```bash
cd C:\Mac\Home\Documents\WhatsappForWP
msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86
```

Expected `0 Error(s)`. The nine `App.xaml` errors are addressed by Task 2; if anything else surfaces, it is a new error list and a new fix.

---

## Self-Review

**1. Spec coverage.** All 18 pasted errors come from the nine `PathGeometry.Figures` attributes at `App.xaml:21-29` (lines 21, 22, 23, 24, 25, 26, 27, 28, 29 → `IconBack`, `IconCalls`, `IconChats`, `IconClose`, `IconNewChat`, `IconPhoto`, `IconSend`, `IconSettings`, `IconStatus`). Task 2 rewrites exactly those nine, one `PathFigure` per figure and one element per segment, keeping every coordinate; Task 1 makes the guard catch a recurrence. The two `TODO` comments in the app sources (`App.xaml.cs:75` "Caricare lo stato…" and `App.xaml.cs:130` "Salvare lo stato…") are Task 3.

**2. Placeholder scan.** No `TBD`, no "similar to Task N", no "add error handling". Every XAML element, every C# line and every guard line is given literally, including the expected guard output after each step.

**3. Type consistency.** `defined` is a `Map<string, string>` (key → mini-language) in both the parser and the preview loop; `SessionService.Section` is `AppSection` (from `WhatsappApp.Controls`), the same type `SectionNav.PageFor` and `SectionNav.Current` take; `SectionNav.PageFor` is `public static Type`, matching `Frame.Navigate(Type, object)`; `CurrentSection()` returns `AppSection`, the type `SessionService.Section` assigns.
