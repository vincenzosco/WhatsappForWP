# Local login server (GOWA + adapter + terminal QR)

> **Status:** executed and verified on this Mac on 2026-09-25.

**Goal.** Deploy the WhatsApp login stack on this machine, with a script that
starts it, prints the LAN address and draws the login QR (or the pairing code) in
the terminal, so the account can be linked from the phone without the WP8 app -
which cannot be launched here at all.

## Decisions and why

| Decision | Because |
| --- | --- |
| GOWA from the **official prebuilt release** (`whatsapp_9.5.0_darwin_arm64.zip`), unpacked into `.tools/gowa/`, SHA-256 checked against the published `checksums-macos.txt`. | No Go toolchain, no Docker and no GOWA on this machine; the user's fork has no releases (it would need `brew install go` plus a source build). The v9.5.0 REST surface matches what the adapter already calls: `/app/login` → `qr_link`, `/app/login-with-code`, `/app/status`, `/devices`, `PATCH /devices/:id/webhook`, `/send/*`, `/user/my/contacts`. |
| New `tools/start-login.js` starts GOWA **and** the adapter and owns the login loop; `tools/qr-term.js` owns the drawing. | The alternative - hand-building `npm start` + a separate GOWA command line - is exactly the setup that keeps failing for lack of a QR. Splitting the drawing out keeps it testable on its own. |
| The QR is **recovered module by module** and drawn with half blocks, not printed as an image. | A terminal cell is taller than it is wide: one module per column and two per row (with `▀` and explicit black/white) keeps the modules square on any theme. GOWA's PNG is 512 px but not a whole multiple of the grid (65 modules × 7 px = 455 px), so the count is recovered and then **proven** by rebuilding every pixel (0.00% error observed). |
| The script watches `.tools/gowa/statics/qrcode/` instead of calling `/app/login` in a loop. | `GET /app/login` disconnects the client and restarts the flow; the flow itself publishes a new PNG per code (first code 60 s, then every ~20 s, ~160 s window). Stale PNGs from a previous run are cleared at startup and files older than the start time are ignored. |
| GOWA is bound to `127.0.0.1`; only the adapter faces the LAN. | GOWA's REST API sends messages as the linked account and has no authentication by default. The adapter's own channel is the AES-256-GCM one the app already speaks (`BRIDGE_KEY`). |
| `.env` is now actually read by the adapter (`applyDotEnv` in `config.js`). | `cp .env.example .env` was documented but nothing loaded the file: settings were silently ignored. Exported variables still win, so the launcher and tests are unaffected. |

## Verification

- `sha256(whatsapp_9.5.0_darwin_arm64.zip)` = `0a5639e0…a444` - identical to the
  checksum published with the release.
- Live run (real WhatsApp login attempt, no phone linked):
  - GOWA on `127.0.0.1:3000`, adapter listening on `8585` + webhook `8586`;
  - the adapter logged `Webhook registrato su GOWA` and `Server TCP in ascolto`;
  - three QR codes were drawn in 110 s (`14:30:59`, `14:31:58`, `14:32:18`),
    matching the 60 s + 20 s cadence of GOWA's flow;
  - every drawing reported `65 moduli, ricostruzione 0.00%`.
- **Independent proof the drawing is scannable:** the drawn text was parsed back
  into a PNG and decoded with macOS Vision; it returned the *identical* payload as
  GOWA's own PNG.
- `node tools/qr-term.js --self-test` - synthetic 65/25/41-module QRs recovered
  exactly, quiet zone and ANSI framing checked.
- `node tools/start-login.js --no-bridge --once` draws exactly one code;
  `--code 393000000000` printed a pairing code (`4AQY-79C6`, dummy number, now
  expired); `--stop` removed the PID file and reported the processes gone.
- `cd WhatsappBridge && npm test` → `pass 31, fail 0` (two new tests for the
  `.env` loader). All three guards still exit 0.

## Not verified

- The phone side of the link: it needs the account owner. Everything up to the
  hand-off is verified, but nobody has scanned a code yet.
- The WP8 app against this stack: still blocked by the missing x86 emulator /
  USB WP8.1 device (see `2026-09-25-wp81-icon-inline-path-data.md`).
