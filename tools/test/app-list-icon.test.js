// The app-list icon is drawn as a mark over the square the shell paints (the
// manifest's BackgroundColor). A green bubble on that green square is
// invisibility itself - which is what the phone shows: a flat green square.
// The client reference draws its own app-list and tile marks as a WHITE glyph
// on a transparent field (`Images/Tile44x44.png`, `Images/IconicTileSmall.png`
// in the 2.17.52 package: white pixels, no colour of its own), so the mark
// reads on any background the shell chooses. This contract pins that shape and
// pins that every image the manifests name exists under the exact name they
// name it with.
"use strict";

const test = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");
const zlib = require("node:zlib");

const ROOT = path.resolve(__dirname, "..", "..");
const ASSETS = path.join(ROOT, "WhatsappApp", "Assets");
const GEN = path.join(ROOT, "tools", "make-brand-assets.js");

// RGBA rows out of a PNG, the same way the brand test reads corners.
function pixels(file) {
  const b = fs.readFileSync(file);
  let pos = 8, w = 0, h = 0, ct = 0;
  const idat = [];
  while (pos < b.length) {
    const len = b.readUInt32BE(pos);
    const type = b.toString("ascii", pos + 4, pos + 8);
    if (type === "IHDR") { w = b.readUInt32BE(pos + 8); h = b.readUInt32BE(pos + 12); ct = b[pos + 17]; }
    if (type === "IDAT") idat.push(b.slice(pos + 8, pos + 8 + len));
    pos += 12 + len;
  }
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const bpp = ct === 6 ? 4 : 3;
  const stride = w * bpp;
  const out = Buffer.alloc(h * stride);
  let prev = Buffer.alloc(stride);
  for (let y = 0; y < h; y++) {
    const filter = raw[y * (stride + 1)];
    const cur = Buffer.from(raw.slice(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    for (let i = 0; i < stride; i++) {
      const left = i >= bpp ? cur[i - bpp] : 0;
      const up = prev[i];
      const upLeft = i >= bpp ? prev[i - bpp] : 0;
      if (filter === 1) cur[i] = (cur[i] + left) & 255;
      else if (filter === 2) cur[i] = (cur[i] + up) & 255;
      else if (filter === 3) cur[i] = (cur[i] + ((left + up) >> 1)) & 255;
      else if (filter === 4) {
        const p = left + up - upLeft;
        const pa = Math.abs(p - left), pb = Math.abs(p - up), pc = Math.abs(p - upLeft);
        const pick = pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
        cur[i] = (cur[i] + pick) & 255;
      }
    }
    cur.copy(out, y * stride);
    prev = cur;
  }
  return { w, h, bpp, out };
}

test("the app-list marks are white glyphs on a transparent field", () => {
  // The two names the app list reads: `IconPath`/`SmallImageURI` in
  // WMAppManifest.xml and `Square44x44Logo` in Package.appxmanifest.
  for (const name of ["WhatsAppLogo.png", "SmallLogo.scale-240.png", "SmallLogo.png"]) {
    const file = path.join(ASSETS, name);
    const { w, h, bpp, out } = pixels(file);

    let transparent = 0, white = 0, painted = 0;
    for (let i = 0; i < w * h; i++) {
      const px = out.slice(i * bpp, (i + 1) * bpp);
      const r = px[0], g = px[1], b = px[2], a = bpp === 4 ? px[3] : 255;
      if (a < 40) { transparent++; continue; }
      painted++;
      if (r > 190 && g > 190 && b > 190) white++;
    }

    assert.ok(transparent > (w * h) / 3, name + " must keep its field transparent (the shell's square shows through)");
    assert.ok(painted > 0, name + " must carry the mark");
    assert.strictEqual(white, painted, name + " must be a white glyph - the shell paints the square, "
      + "and a coloured mark disappears on it (" + (painted - white) + " painted pixels are not white)");
  }
});

test("the generator draws those marks as glyphs", () => {
  const src = fs.readFileSync(GEN, "utf8");
  assert.ok(/glyph\(/.test(src), "the generator has a glyph helper");
  const at = src.indexOf("write('WhatsAppLogo.png'");
  assert.ok(at >= 0, "WhatsAppLogo.png is generated");
  assert.ok(/glyph\(/.test(src.slice(at, at + 200)), "WhatsAppLogo.png is drawn as a glyph");
});

test("every image the manifests name exists under that exact name", () => {
  const manifests = [
    path.join(ROOT, "WhatsappApp", "Package.appxmanifest"),
    path.join(ROOT, "WhatsappApp", "Properties", "WMAppManifest.xml"),
  ];
  for (const manifest of manifests) {
    const text = fs.readFileSync(manifest, "utf8");
    for (const match of text.matchAll(/Assets\\([^"]+\.(?:png|jpg))/g)) {
      const name = match[1].replace(/\\/g, "/");
      const exact = path.join(ASSETS, name);
      // The platform does scale-qualifier resolution for some of these and not
      // for others; a file named exactly as the manifest names it is found in
      // both worlds, so it is the one thing this contract will accept alone.
      assert.ok(fs.existsSync(exact),
        path.basename(manifest) + " names Assets\\" + name + " and no such file exists");
    }
  }
});