---
name: release-the-app
description: How to produce the icons, tiles and splash screen, check the package manifest, bump the version and deploy the WhatsApp WP8.1 app to a device or emulator. Use when packaging, rebuilding brand assets, or deploying to a phone.
---

# Releasing the app

## Brand assets

The logo (white bubble with the handset cut out) is generated vectorially, so it
stays sharp at every size:

```bash
node tools/make-brand-assets.js             # rewrites the PNGs in WhatsappApp/Assets
node tools/make-brand-assets.js --preview   # + ASCII preview, check before committing
```

Requires ImageMagick 7 (`magick`). Sizes and colours are constants at the top of
that script; the tile background is the WhatsApp teal `#075E54` and the splash
gradient ends on the same colour, so it blends into the first page.

## Manifest

`WhatsappApp/Package.appxmanifest`:

| Field | Value |
| --- | --- |
| `Identity / Version` | bump on every release (`1.0.1.0` → `1.0.2.0`) |
| `Properties / DisplayName` | `WhatsApp` |
| `m3:VisualElements / DisplayName` | `WhatsApp` |
| `m3:VisualElements / BackgroundColor` | `#075E54` |
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

- [ ] The three guards pass (`check-csharp5`, `check-icons`, `check-resw --strict`).
- [ ] `cd WhatsappBridge && npm test` is 29/29.
- [ ] `msbuild` reports `0 Error(s)`.
- [ ] Version bumped in `Package.appxmanifest` **and** `Properties/AssemblyInfo.cs`.
- [ ] README sections that describe the changed behaviour are updated.
- [ ] The on-device checklist in `test-the-app` has been walked through.
- [ ] `git status` clean and pushed.
