#!/usr/bin/env python3
"""
export_xnb_textures.py -- extract every Texture2D (and the atlas+metrics of every
SpriteFont) from the XNA 3.0 / XNB v2 Xbox-360 content tree into PNG, preserving
the logical content paths.

Usage:
    python3 export_xnb_textures.py \
        --assets "$DISHWASHER_ASSETS_DIR" \
        --out    src/Dishwasher/Content \
        --manifest notes/content-export-manifest.json

Decode policy (measured; see docs/notes/android/content-conversion.md and
docs/notes/android/comic-colour-fix.md):
  * format id 1  (SurfaceFormat.Color, raw 32bpp): the Xbox-360 is big-endian,
    so D3DFMT_A8R8G8B8 (0xAARRGGBB) is stored MSB-first as file bytes A,R,G,B.
    Decoded with raw_order="argb".  (An earlier B,G,R,A decode produced a
    strong blue cast on the comic panels, the XNA splash and the control art;
    the correct A,R,G,B order is proven by the reference comic frame, the
    orange "BUILD" in the XNA splash, and the cream control-guide art.)
  * format id 28 (Dxt1) and 32 (Dxt5): RGB565 colour endpoints are stored
    BIG-endian (word-swapped); the DXT5 alpha block and all index bytes are
    natural byte order.  Verified visually (natural scene vs magenta/psych).
  * mip maps: every texture in this title has exactly one mip.  We export level 0
    only; if a file ever has >1 level we still export level 0 and flag it.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from collections import Counter
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import xnb_v2 as X  # noqa: E402

from PIL import Image  # noqa: E402


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--assets", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--manifest", default=None)
    ap.add_argument("--limit", type=int, default=0, help="debug: only N textures")
    args = ap.parse_args()

    assets = os.path.abspath(args.assets)
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)

    xnb_files = []
    for root, _dirs, files in os.walk(assets):
        for fn in files:
            if fn.lower().endswith(".xnb"):
                xnb_files.append(os.path.join(root, fn))
    xnb_files.sort()

    reader_hist = Counter()
    fmt_hist = Counter()
    mip_hist = Counter()
    records = []
    errors = []
    exported = 0
    fonts = 0
    effects = 0
    total_pixels = 0

    for path in xnb_files:
        rel = os.path.relpath(path, assets)
        data = open(path, "rb").read()
        try:
            head = X.XnbFile(data, rel, read_primary=False)
        except X.XnbError as e:
            errors.append({"path": rel, "error": "header: %s" % e})
            continue
        short = X.XnbFile._short(head.primary_reader or "")
        reader_hist[short] += 1

        if short == "EffectReader":
            effects += 1
            records.append({"src": rel, "reader": "EffectReader", "skipped": True})
            continue

        try:
            x = X.parse(data, rel)
        except X.XnbError as e:
            errors.append({"path": rel, "error": str(e)})
            continue

        relout = os.path.splitext(rel)[0]  # drop .xnb
        base = os.path.join(out, relout)
        os.makedirs(os.path.dirname(base), exist_ok=True)

        if isinstance(x.primary, X.Texture2D) and short == "Texture2DReader":
            tex = x.primary
            mip_hist[len(tex.mips)] += 1
            fmt_hist[tex.format_name] += 1
            w, h, _raw = tex.mips[0]
            rgba = X.decode_texture(tex, 0, raw_order="argb")
            png_path = base + ".png"
            Image.frombytes("RGBA", (w, h), rgba).save(png_path, optimize=False)
            # verify by reopening
            with Image.open(png_path) as im:
                vw, vh = im.size
            ok = (vw, vh) == (w, h) and len(rgba) == w * h * 4
            if not ok:
                errors.append({"path": rel, "error": "verify failed %dx%d" % (vw, vh)})
            exported += 1
            total_pixels += w * h
            records.append({
                "src": rel, "reader": "Texture2DReader", "format": tex.format,
                "format_name": tex.format_name, "width": w, "height": h,
                "mips": len(tex.mips), "mip0_bytes": len(rgba),
                "out": os.path.relpath(png_path, out), "verified": ok,
                "sha256": sha256_file(png_path),
            })
            if args.limit and exported >= args.limit:
                break

        elif isinstance(x.primary, X.SpriteFont):
            fonts += 1
            sf = x.primary
            tex = sf.texture
            w, h, _raw = tex.mips[0]
            rgba = X.decode_texture(tex, 0, raw_order="argb")
            atlas_path = base + "_atlas.png"
            Image.frombytes("RGBA", (w, h), rgba).save(atlas_path)
            meta = sf.info()
            meta.update({
                "src": rel,
                "atlas": os.path.relpath(atlas_path, out),
                "atlas_width": w, "atlas_height": h,
                "glyph_rects": [list(r) for r in sf.glyphs],
                "cropping_rects": [list(r) for r in sf.cropping],
                "char_map": sf.char_map,
                "kerning": [list(k) for k in sf.kerning],
            })
            with open(base + ".spritefont.json", "w") as f:
                json.dump(meta, f, indent=1)
            total_pixels += w * h
            records.append({
                "src": rel, "reader": "SpriteFontReader",
                "glyphs": len(sf.glyphs), "atlas_w": w, "atlas_h": h,
                "out": os.path.relpath(atlas_path, out),
                "meta": os.path.relpath(base + ".spritefont.json", out),
            })

    summary = {
        "generated": datetime.now(timezone.utc).isoformat(),
        "assets_root": assets,
        "out_root": out,
        "xnb_total": len(xnb_files),
        "reader_hist": dict(reader_hist),
        "textures_exported": exported,
        "fonts": fonts,
        "effects_skipped": effects,
        "texture_format_hist": dict(fmt_hist),
        "mip_histogram": dict(mip_hist),
        "total_pixels_exported": total_pixels,
        "errors": errors,
        "records": records,
    }
    if args.manifest:
        os.makedirs(os.path.dirname(os.path.abspath(args.manifest)), exist_ok=True)
        with open(args.manifest, "w") as f:
            json.dump(summary, f, indent=1)

    print("xnb total          :", len(xnb_files))
    print("readers            :", dict(reader_hist))
    print("textures exported  :", exported)
    print("spritefonts        :", fonts)
    print("effects skipped    :", effects)
    print("texture formats    :", dict(fmt_hist))
    print("mip histogram      :", dict(mip_hist))
    print("total pixels       :", total_pixels)
    print("errors             :", len(errors))
    for e in errors:
        print("   !!", e)
    return 0 if (not errors and exported == 125) else 1


if __name__ == "__main__":
    raise SystemExit(main())
