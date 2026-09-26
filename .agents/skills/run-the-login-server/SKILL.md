---
name: run-the-login-server
description: How to start GOWA plus the WP8 adapter on this machine and log the WhatsApp account in from the terminal, with the QR drawn as a scannable terminal code. Use when the app shows nothing because no WhatsApp session exists yet, when a login fails, or when GOWA/the adapter must be (re)started or stopped.
---

# Running the login server

## What it is

The WP8 app has no WhatsApp client of its own: it shows something only when the
adapter sits in front of a **GOWA** instance that is already logged in.

```
WhatsApp ⇄ GOWA (127.0.0.1:3000) ⇄ adapter (TCP 8585, webhook 8586,
                                          discovery UDP 8587) ⇄ app WP8
```

**The login is normally done on the phone, not here.** The app announces nothing
and asks for the code itself: it broadcasts on UDP 8587 to find the adapter, and
when the connection opens it requests `login.qr` and shows the result full screen,
keeping the screen on so the code can be scanned. Reach for the terminal QR only
when the phone is not available (an emulator, a device with no app build).

One command brings the whole stack up, and puts the login QR in the terminal when
that is what you asked for:

```bash
node tools/start-login.js --download     # --download only the first time
```

| Option | Effect |
| --- | --- |
| `--code 393401234567` | link with a phone pairing code instead of the QR |
| `--url http://host:3000` | do not start GOWA, use this one |
| `--gowa <path>` | use a GOWA binary somewhere else |
| `--no-bridge` | GOWA and the QR only |
| `--once` | draw one code and exit (quick check, no login loop) |
| `--no-qr` | draw nothing and just wait: the login is done from the phone, in the app (first choice) |
| `--open-qr` | open `.tools/gowa/login-qr.png` in Preview, where it reloads on its own |
| `--plain` / `--ansi` | force the drawing without / with colours |
| `--quiet-zone <n>` | white margin around the QR (default 4) |
| `--port`, `--bridge-port`, `--webhook-port` | 3000 / 8585 / 8586 |
| `--calls-port <n>` | port for the second service (default 8588) |
| `--no-calls` | do not start the second service |
| `--list-services` | print which services would start and which are skipped, then exit |
| discovery | UDP 8587, broadcast by the adapter (`DISCOVERY_PORT`), the app listens on it |
| `--ui` | additionally serve GOWA's web dashboard |
| `--stop` | stop the stack started by an earlier run |

## What the script does

1. **GOWA** — `.tools/gowa/whatsapp rest --port=3000 --host=127.0.0.1`, run with
   the current directory inside `.tools/gowa` (that is where GOWA keeps
   `storages/` and `statics/`). Stdout goes to `.tools/gowa/gowa.log`.
   `--download` fetches the pinned official release for **the platform the script
   is running on** (`tools/download.js`), checks the published SHA-256, and
   unpacks it in-process with `node:zlib` — no `unzip`, no `tar`, so it works on
   Windows too. All eight published archives are covered:

   | OS / CPU | archive |
   | --- | --- |
   | macOS arm64 / Intel | `whatsapp_9.5.0_darwin_{arm64,amd64}.zip` |
   | Linux x64 / arm64 / armv7 / 386 | `whatsapp_9.5.0_linux_{amd64,arm64,armv7,386}.zip` |
   | Windows x64 / 386 | `whatsapp_9.5.0_windows_{amd64,386}.zip` |

   A pair with no published archive fails with the pair named, pointing at
   `--gowa` / `--url`. A digest mismatch is a hard failure.
2. **Device** — `GET /devices`, and `POST /devices` if there is none: every login
   route requires a device id (`X-Device-Id`). The id is passed to the adapter as
   `GOWA_DEVICE_ID` so both use the same one.
3. **Adapter** — `node server.js` in `WhatsappBridge/` with `GOWA_URL`,
   `BRIDGE_PORT`, `WEBHOOK_PORT`, `WEBHOOK_PUBLIC_URL`. It registers its own
   webhook on GOWA at startup, and its output is echoed with an `[adattatore]`
   prefix. It also broadcasts the discovery beacon on UDP 8587, so the app can
   find it on the LAN without being told an address.
4. **Login** — QR by default, nothing at all with `--no-qr`, pairing code with
   `--code`. `Ctrl-C` (or a signal)
   stops GOWA and the adapter; the PID file is `.tools/gowa/login-stack.pid` and
   `--stop` reads it, killing first the script itself (which brings down its
   children), so it also works on a stack left running in another terminal.

## Services (`tools/services.js`)

The stack is a list, not two hardcoded children. `SERVICE_DEFS` declares each
service with `name`, `kind` (`node` = run from this repo, `binary` = a downloaded
executable) and `enabled(options)`. A `node` service whose script is missing is
**skipped with a reason**, not an error: that is how the second server
(`WhatsappCallServer/server.js`) can be added later without touching the
launcher — put the file there and it starts, gets its env (`CALLS_PORT`,
`GOWA_URL`) from `serviceEnv`, appears in the banner, and is killed by `Ctrl-C`
and `--stop` like the adapter. `--list-services` shows the resolved list, and
`--no-calls` switches the second service off.

The first run prints the LAN address and ports to give the app
(`<ip>:8585`), then the QR: on the phone **WhatsApp → Impostazioni → Dispositivi
collegati → Collega un dispositivo**.

## The QR contract (why the script watches a directory)

- `GET /app/login` **restarts** the login flow (it disconnects the client first)
  and returns `results: { device_id, qr_link, qr_duration }`, where `qr_link`
  points at a PNG such as `/statics/qrcode/scan-qr-<uuid>.png`. Never poll it in a
  loop: one call per QR window.
- The flow pushes a **new** PNG for every code, under
  `.tools/gowa/statics/qrcode/`: the first code lasts ~60 s, then one every ~20 s,
  for a total window of ~160 s. GOWA deletes each PNG after `qr_duration` s, but
  a stopped GOWA leaves the last one behind — so the script clears that directory
  at startup and only considers files newer than its own start time.
- When the window closes without a login, the script calls `/app/login` again. If
  login already succeeded, GOWA answers `ALREADY_LOGGED_IN`, which is treated as
  success.

## Drawing the QR (`tools/qr-term.js`)

The terminal must show **modules that are square and the right way round**, which
a raw PNG cannot do. `tools/qr-term.js` recovers the module matrix and draws it
with horizontal half blocks (`▀`, one module per column, two per row) and explicit
black/white colours — never the terminal's theme.

- GOWA's PNG is 512 px but the grid is not a whole multiple of it (e.g. 65 modules
  of 7 px = 455 px inside 512). The module count is therefore recovered, not
  assumed: dark-pixel bounding box → the leading dark run of the first row is the
  7-module finder pattern → candidate counts (21 + 4k) → the one whose grid
  **reproduces every pixel** wins. The script fails loudly above 2% error.
- Check it after any change: `node tools/qr-term.js --self-test` (synthetic QRs at
  65/25/41 modules), `--info <png>` for the diagnosis, `--preview <png>` for the
  drawing.
- Verified against macOS Vision: the drawn text re-parsed into an image decodes to
  the **identical** payload as GOWA's PNG, so the drawing is not merely pretty.
- The coloured drawing and the `--plain` one must show the **same** codes, and
  `--self-test` says so (`colori: il disegno a colori mostra gli stessi moduli`):
  it decodes the ANSI output back into modules and compares. In colour mode the
  glyph is always `▀` and the foreground is the upper module, the background the
  lower one - `▄` or `█` swap the two colours and invert every light-on-top cell,
  which is what made the QR look like noise.
- Do not hardcode the module count: it follows the payload length (65 modules was
  observed for the current WhatsApp QR, and it will change).
- **A code that does not fit is never drawn.** `makePrinter` redraws in place
  only when the window is a TTY, not `--plain`, and both `columns >= the drawing`
  and `rows >= the drawing` hold; otherwise it prints the sizes, the PNG path and
  the `--no-qr` hint, and stays silent (one line per new code) until the window is
  enlarged. A truncated QR cannot be scanned and only looks like a bug: never
  "draw anyway".
- Every code is also copied to `.tools/gowa/login-qr.png`, a stable path, so an
  external viewer reloads it as the code rotates. `--open-qr` opens it with `open`.

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| `GOWA not found in .tools/gowa/whatsapp` | run with `--download`, or pass `--gowa`, or start GOWA elsewhere and use `--url` |
| `ImageMagick could not read ...` | `brew install imagemagick` — `magick` must be in PATH |
| `GOWA is not responding` | the script prints the last lines of `.tools/gowa/gowa.log`; port 3000 taken is the usual reason |
| macOS asks to allow incoming connections | allow it: the adapter listens on 8585/8586 for the phone and for GOWA |
| Nothing but `state: disconnected` in the app | the WhatsApp account is not linked yet: run this script and complete the QR login once |
| Wrong account linked | `rm -rf .tools/gowa/storages` and log in again (this deletes the session) |
| "The code needs N rows and M columns" | the window is too small: enlarge it or press `Cmd -`, or open `.tools/gowa/login-qr.png`, or use `--no-qr` |
| Terminal shows garbage instead of the QR | a real TTY gets the coloured drawing: do not pipe the output through a pager, and keep the window above the printed size |

## Rules

- **`.tools/` is git ignored and must stay that way**: `storages/whatsapp.db`
  holds the live WhatsApp credentials of the linked account.
- **GOWA binds to `127.0.0.1` on purpose.** Its REST API sends messages as the
  linked account and has no authentication unless `--basic-auth` is given; only
  the adapter (port 8585, AES-256-CBC + HMAC-SHA256 with `BRIDGE_KEY`) may face the LAN.
- **Run the tool tests too**: `node --test "tools/test/**/*.test.js"` covers the
  downloader (platform mapping, ZIP reader, digest table) and the service list.
  It is the fifth gate, next to the four `tools/check-*.js` guards. A bare
  directory (`node --test tools/test`) does **not** work on this Node build.
- Linking an account is a real action on a real phone number: only run the login
  loop when that is the intent.
