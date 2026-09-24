---
name: update-the-app
description: Step-by-step recipes for extending the WhatsApp WP8.1 app - adding a page, a section, an icon, a localized string, a language, a GOWA endpoint, and bumping the version. Use when the user asks for a new feature, a new screen, a new string, a new language, or a version bump.
---

# Updating the app

Every recipe ends the same way: run the guards, then commit.

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict
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

1. `App.xaml`: `<PathGeometry x:Key="IconMyThing" Figures="M... Z"/>`, 24x24 view box.
2. `node tools/check-icons.js --preview` and read the ASCII render before
   trusting it - this caught a mis-placed splash mark and a broken glyph before.
3. Reference it with `Data="{StaticResource IconMyThing}"`, and choose
   `Stroke` (outline) or `Fill` to match the neighbours.
4. Delete any geometry whose last reference you removed - the guard fails on
   unused definitions.

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

## Bump the version

1. `WhatsappApp/Package.appxmanifest`: `<Identity ... Version="1.0.2.0" />` - a
   higher version is what makes redeploy an *upgrade* instead of a conflict.
2. `WhatsappApp/Properties/AssemblyInfo.cs`: `AssemblyVersion` /
   `AssemblyFileVersion` to match.
3. Commit both together.

## Change the tile, name or splash

Follow `release-the-app` instead: the images are generated, and WP8.1 caches the
old name and tile until the app is uninstalled.
