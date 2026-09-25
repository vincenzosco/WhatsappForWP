---
name: update-the-app
description: Step-by-step recipes for extending the WhatsApp WP8.1 app - adding a page, a section, an icon, a localized string, a language, a GOWA endpoint, and bumping the version. Use when the user asks for a new feature, a new screen, a new string, a new language, or a version bump.
---

# Updating the app

Every recipe ends the same way: run the guards, then commit.

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict \
  && node tools/check-docs.js
```

## Add a page

1. `Pages/MyPage.xaml` - copy the shape of `Pages/StatusPage.xaml`: a `Grid`
   with `Auto`/`*`/`Auto` rows (title bar, content, navigation) and
   `<controls:SectionNav x:Name="Nav" Grid.Row="2"/>` if it is a section.
2. `Pages/MyPage.xaml.cs` - `public sealed partial class MyPage : Page`,
   `InitializeComponent()`, and pick a `NavigationCacheMode`:
   `Enabled` for the three section pages, default for everything else.
3. Register both files in `WhatsappApp/WhatsappApp.csproj`:
   `<Compile Include="Pages\MyPage.xaml.cs"><DependentUpon>MyPage.xaml</DependentUpon></Compile>`
   and `<Page Include="Pages\MyPage.xaml"><Generator>MSBuild:Compile</Generator><SubType>Designer</SubType></Page>`.
   **A page that is not listed here simply does not exist at build time.**
4. Navigate with `Frame.Navigate(typeof(MyPage))` or
   `Frame.Navigate(typeof(MyPage), parameter)`; read the payload in
   `OnNavigatedTo` with `e.Parameter as MyType`.

## Add a section to the navigation bar

1. `Controls/SectionNav.xaml`: add a column to the `Grid` and a `Button` with a
   `Path` bound to an `IconX` geometry.
2. `Controls/SectionNav.xaml.cs`: add the value to `enum AppSection`, a case in
   `PageFor`, the type in `IsSectionPage`, a `Click` handler, a tooltip in the
   constructor and a line in `UpdateColors`.
3. Create the page (recipe above) with `Nav.Current = AppSection.Xxx;` in
   `OnNavigatedTo`.
4. Add `Nav_Xxx` to both `.resw` files for the tooltip.
5. Why the back-stack line exists: `Go()` removes the page it leaves so Back
   exits the app from a section root instead of walking through visited
   sections. Keep that behaviour when you add a section.

## Add an icon

1. Inline the geometry **on the `Path` itself** - never a `PathGeometry`
   resource in `App.xaml` and never `Data="{StaticResource IconX}"`. That form
   compiles and then throws at runtime:
   `XamlParseException: Failed to assign to property
   'Windows.UI.Xaml.Shapes.Path.Data'.` (in WinRT a `Geometry` is not shareable
   through a `StaticResource` - microsoft-ui-xaml#1909 and #5780).

   ```xml
   <Path Stroke="White" StrokeThickness="2" Width="24" Height="24">
       <!-- IconMyThing -->
       <Path.Data>
           <PathGeometry>
               <PathGeometry.Figures>
                   <PathFigure StartPoint="x,y">
                       <PathFigure.Segments>
                           <PolyLineSegment Points="x,y x,y"/>
                       </PathFigure.Segments>
                   </PathFigure>
               </PathGeometry.Figures>
           </PathGeometry>
       </Path.Data>
   </Path>
   ```

   24x24 view box, `IsClosed="True"` for a `Z`, and **element form only**: a
   `Figures="M..."` attribute is a build error on WP8.1, because its
   `PathFigureCollection` type converter has no string form. The
   `<!-- IconMyThing -->` comment is required - it names the icon for the guard.
2. Reuse an existing `<!-- IconX -->` name only with byte-identical geometry:
   the guard fails when two copies of the same name differ, so a duplicated
   icon cannot drift apart.
3. `node tools/check-icons.js --preview` and read the ASCII render before
   trusting it - this caught a mis-placed splash mark and a broken glyph before.
   Choose `Stroke` (outline) or `Fill` to match the neighbours.

## Add or change a string

1. Add the key to **both** `Strings/en-US/Resources.resw` and
   `Strings/it-IT/Resources.resw`, same `name`, translated `<value>`.
2. XAML: `x:Uid="KeyName"` and keep a sensible literal as the fallback
   (that literal is what shows if the resource is missing).
   C#: `Loc.Get("KeyName", "fallback in the default language (English)")`.
3. `node tools/check-resw.js --strict`.
4. Naming: XAML entries end with the property (`.Text`, `.Content`,
   `.PlaceholderText`); code-only entries are plain identifiers; never both for
   the same base name (the guard detects the collision).
5. `{0}` placeholders are used with `string.Format(Loc.Get(...), value)`, which
   keeps word order free for translators.

## Add a language

1. `mkdir WhatsappApp/Strings/<bcp47>/` (for example `fr-FR`), copy
   `Strings/en-US/Resources.resw`, translate the values only.
2. `WhatsappApp.csproj`: add
   `<PRIResource Include="Strings\<bcp47>\Resources.resw" />`.
3. Add the tag to `LANGS` in `tools/check-resw.js` so the guard covers it.
4. `node tools/check-resw.js --strict` - it fails if a key is missing in any
   language.
5. No code change is needed: Windows picks the file matching the device language
   and falls back to `<DefaultLanguage>` (currently `en-US`). Changing the
   neutral fallback means changing `<DefaultLanguage>` and the C# fallbacks to
   match.

## Talk to a new GOWA endpoint

1. `WhatsappBridge/gowa-client.js`: add the call, read the `results` object, and
   cover it in `test/gowa-client.test.js`.
2. `WhatsappBridge/server.js`: expose it as a control command
   (`hello|status|login.qr|login.code|contacts|logout`) or as a reply frame.
3. App side: send it with
   `CommunicationService.Instance.SendControlAsync("cmd", payload)` and handle
   the reply in `Pages/ConnectionPage.xaml.cs::OnControlMessageReceived`
   (control frames carry `Command`/`State`/`PairCode`/`QrImageData`/`AccountJid`).
4. `cd WhatsappBridge && npm test`.
5. Remember the wire format: WP8's `DataContractJsonSerializer` needs
   `\/Date(epochMs)\/`, which `message-format.js` produces on purpose.

## Change the documentation

1. Every document that explains the project exists twice: `README.md` /
   `README.it.md` and `WhatsappBridge/README.md` / `WhatsappBridge/README.it.md`.
   Write the section in the English file, then the same section in the Italian
   one - same position, same heading level, same order; only the text is
   translated. Adding, moving or dropping a section in one of the two is what the
   guard catches.
2. Keep the language switcher at the top of each file pointing at its pair
   (`**English** | [Italiano](README.it.md)` and its mirror), and keep
   `## Disclosure` (open source, maintainers wanted, written by an AI agent, no
   responsibility for the account used to sign in) as the **last** section of the
   project READMEs.
3. No emoji. The warning sign (U+26A0) is allowed only for a real hazard.
4. A new explanatory document is born as a pair (`DOC.md` + `DOC.it.md`) and gets
   an entry in `PAIRS` in `tools/check-docs.js`; add `disclosure: true` only if it
   presents the project.
5. `docs/superpowers/plans/*.md` are records of work already done, not guides:
   they stay English-only and out of `PAIRS`.
6. `node tools/check-docs.js`.

## Bump the version

1. `WhatsappApp/Package.appxmanifest`: `<Identity ... Version="1.0.2.0" />` - a
   higher version is what makes redeploy an *upgrade* instead of a conflict.
2. `WhatsappApp/Properties/AssemblyInfo.cs`: `AssemblyVersion` /
   `AssemblyFileVersion` to match.
3. Commit both together.

## Change the tile, name or splash

Follow `release-the-app` instead: the images are generated, and WP8.1 caches the
old name and tile until the app is uninstalled.
