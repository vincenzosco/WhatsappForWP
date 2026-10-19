---
name: release-the-app
description: How to produce the icons, tiles and splash screen, check the package manifest, bump the version and deploy the WhatsApp WP8.1 app to a device or emulator. Use when packaging, rebuilding brand assets, or deploying to a phone.
---

# Releasing the app

## Brand assets

The tiles, the launcher icon and the splash are the official WhatsApp WP8.1
artwork, kept in `tools/brand/official/` (`Icon1.png`, `Images/Tile*.png`,
`Images/logo.png`, `Images/Background.jpg`, the 480×800 splash):

```bash
node tools/make-brand-assets.js             # rewrites the files in WhatsappApp/Assets
node tools/make-brand-assets.js --preview   # + ASCII preview, check before committing
```

The script copies the artwork at the sizes the manifest asks for at 240% and
derives only the three that the package does not ship (the live-tile icon at 200
and 480 px, the 120 px `StoreLogo`, the 1152×1920 splash). It needs no external
tool: the PNG is decoded, resampled with a triangular kernel and encoded with
Node's `zlib`. To change the mark, replace the files in
`tools/brand/official/` with the new artwork and rerun the script.

## Manifest

`WhatsappApp/Package.appxmanifest`:

| Field | Value |
| --- | --- |
| `Identity / Version` | bump on every release (`1.0.1.0` → `1.0.2.0`) |
| `Properties / DisplayName` | `WhatsApp` |
| `m3:VisualElements / DisplayName` | `WhatsApp` |
| `m3:VisualElements / BackgroundColor` | `#45D82C` - il verde dell'icona ufficiale; le tile sono il glifo bianco su trasparente, quindi il verde traspare; con `ForegroundText="light"`, perche' testo scuro su quel verde non si legge. Solo 6 cifre esadecimali: `APPX1404` rifiuta `#FF45D82C` |
| `Resources` | `<Resource Language="x-generate"/>` - the languages come from `Strings\*\Resources.resw` |

The manifest is intentionally **not** localized: the only translatable value is
the description, `makepri` fails the build on a bad `ms-resource:` reference, and
the app name is the same in both languages. Adding a language for the UI does not
require touching the manifest - `x-generate` picks the `Strings\*\` folders up.

## Deploy

1. `msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86`,
   **from a local disk path** - building inside a Parallels/Mac shared folder
   fails pass 2 of the XAML compiler with `WMC9999` (see `test-the-app`).
2. Deploy to the emulator or device from Visual Studio, or install the signed
   package. The emulator needs an **x86/x64 host with Hyper-V**: the XDE images
   are x86, so on an ARM host (Parallels on Apple Silicon, Windows 11 ARM64)
   they cannot boot at all and only a USB-attached device is an option.
3. WP8.1 caches the tile and the name: after changing icons or the display name,
   **uninstall** the app on the device and deploy again, otherwise the old tile
   stays on the Start screen.

Developer licence / sideload, if the machine complains about a missing developer
licence or an unsigned package:

```powershell
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" /f /v AllowDevelopmentWithoutDevLicense /t REG_DWORD /d 1
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" /f /v AllowAllTrustedApps /t REG_DWORD /d 1
```

Plus Settings > Privacy & security > For developers > **Developer Mode = On**, then
restart Visual Studio. The XAML designer is not required for building; the error
comes from `Microsoft.Expression.DesignHost` trying to launch the app in an
AppContainer, and on Windows 10/11 there is no "developer licence" any more, only
Developer Mode.

## Checklist before calling it a release

- [ ] The four guards pass (`check-csharp5`, `check-icons`, `check-resw --strict`, `check-docs`).
- [ ] `node tools/test-protocol.js` reports its full suite green.
- [ ] `msbuild` reports `0 Error(s)`.
- [ ] Version bumped in `Package.appxmanifest` **and** `Properties/AssemblyInfo.cs`.
- [ ] README sections that describe the changed behaviour are updated **in both languages** (`README.md` + `README.it.md`), and `## Disclosure` is still the last section of the project READMEs.
- [ ] The on-device checklist in `test-the-app` has been walked through.
- [ ] `git status` clean and pushed.
