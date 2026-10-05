#!/usr/bin/env python3
"""Generate the Dishwasher launcher icon set from the approved artwork.

This is the SHIPPING icon generator.  It SUPERSEDES
``docs/assets/generate-icon.py`` (the old procedural "halftone-burst" concept
sheet); that script is kept only for its historical banner/contact-sheet
helpers and is no longer wired into the app.

Source artwork
--------------
The durable source is the user-supplied ``icon.jpeg`` (a 1376x768 JPEG with the
shield on a near-black background).  The single approved, already-reviewed crop
of that file is::

    icon.jpeg.crop((410, 106, 966, 650))     # -> 556 x 544

This crop keeps the shield's rounded frame and trims the black margin.  It is
re-padded onto a 556 x 556 canvas whose background is the artwork's own colour
(RGB 23, 23, 23 = #171717), matching
``icon-work/cropped-square.png`` byte-for-byte.  If a pre-cropped image is
passed with ``--art`` the ``--crop`` box is ignored.

Design decision (approved): KEEP the shield frame.  No output is full-bleed:
bleeding the artwork to the 108 dp adaptive layer would let the launcher's mask
cut the shield's own border away.  Instead the shield is scaled so its largest
dimension is ``SAFE_FRAC`` of the canvas and centred, on a solid #171717
background so any part of the mask that does clip is invisible.  The same
fraction is used for the legacy square/round PNGs, the @drawable fallback and
the docs assets, so every output carries identical padding.  ``SAFE_FRAC`` is
also exposed as ``--safe-frac`` for quick tuning.

Outputs
-------
With no arguments, writes the docs assets next to this script::

    icon-512.png  icon-192.png  ic_launcher-512.png

``--android DIR`` (repeatable) additionally writes a complete Android resource
overlay (mirroring ``src/Dishwasher/Resources``) into DIR::

    drawable/icon.png (or Drawable/Icon.png if that already exists)
    mipmap-<bucket>/ic_launcher.png          legacy square (padded, SAFE_FRAC)
    mipmap-<bucket>/ic_launcher_round.png    legacy round (padded, fits circle)
    mipmap-<bucket>/ic_launcher_foreground.png  adaptive foreground (safe zone)
    mipmap-<bucket>/ic_launcher_background.png  adaptive background (solid #171717)
    mipmap-anydpi-v26/ic_launcher.xml
    mipmap-anydpi-v26/ic_launcher_round.xml

Typical use::

    python3 docs/assets/generate-icon-from-art.py \\
        --art /path/to/icon.jpeg \\
        --android src/Dishwasher/Resources \\
        --android /path/to/src/Dishwasher/Resources
"""
import argparse
import math
import os

from PIL import Image, ImageChops

HERE = os.path.dirname(os.path.abspath(__file__))

# --------------------------------------------------------------------------
# Approved crop + background colour
# --------------------------------------------------------------------------
CROP_BOX = (410, 106, 966, 650)     # from icon.jpeg -> 556 x 544 (shield kept)
BG = (23, 23, 23)                   # #171717, sampled from the artwork
SQUARE_PAD_TOP = 6                  # centre the 544-tall crop in a 556 square

# Icon geometry -------------------------------------------------------------
# Every raster output keeps the shield's largest dimension at SAFE_FRAC of the
# canvas, centred, on solid #171717 (transparent-only for the adaptive
# foreground).  Tune this ONE value to zoom the artwork in/out; it is also a
# CLI flag (--safe-frac) so a nudge does not need an edit.
#
# The adaptive layers are 108 dp; only the central ~66 dp is guaranteed
# visible.  SAFE_FRAC=0.54 puts the shield at ~58 dp, i.e. ~88 % of the safe
# circle, leaving generous padding so the launcher mask can never clip the
# frame/head/cleaver.  (Was 0.62 = ~102 % of the safe circle -- too tight.)
SAFE_FRAC = 0.54

# (bucket, legacy px, adaptive px)
BUCKETS = (
    ("mdpi", 48, 108),
    ("hdpi", 72, 162),
    ("xhdpi", 96, 216),
    ("xxhdpi", 144, 324),
    ("xxxhdpi", 192, 432),
)
DOC_SIZES = {"icon-512.png": 512, "icon-192.png": 192, "ic_launcher-512.png": 512}
DRAWABLE_SIZE = 512

# A complete adaptive-icon definition (API 26+).  Older devices fall back to the
# legacy PNGs in mipmap-<density>/ic_launcher.png.
ADAPTIVE_XML = """<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@mipmap/ic_launcher_background" />
    <foreground android:drawable="@mipmap/ic_launcher_foreground" />
</adaptive-icon>
"""

ART_CANDIDATES = (
    "icon.jpeg",
    os.path.join("..", "..", "icon.jpeg"),
    os.path.join(HERE, "icon.jpeg"),
)


# ==========================================================================
# source
# ==========================================================================
def find_art(explicit):
    if explicit:
        return explicit
    for cand in ART_CANDIDATES:
        if os.path.isfile(cand):
            return os.path.abspath(cand)
    return None


def build_square(art_path, crop_box):
    """Return the canonical 556 x 556 RGB artwork (shield framed, #171717 bg)."""
    art = Image.open(art_path).convert("RGB")
    if crop_box is not None:
        if art.size == (556, 544):
            crop = art
        else:
            crop = art.crop(crop_box)
    else:
        crop = art
    square = Image.new("RGB", (crop.width, crop.height + SQUARE_PAD_TOP * 2), BG)
    square.paste(crop, (0, SQUARE_PAD_TOP))
    return square


def shield_bbox(square, thresh=16):
    """Bounding box of the shield inside `square` (non-background pixels)."""
    diff = ImageChops.difference(square, Image.new("RGB", square.size, BG))
    return diff.convert("L").point(lambda v: 255 if v > thresh else 0).getbbox()


# ==========================================================================
# renderers
# ==========================================================================
class Icon:
    def __init__(self, square, safe_frac=SAFE_FRAC):
        self.square = square
        self.safe_frac = safe_frac
        self.bbox = shield_bbox(square)
        if self.bbox is None:
            raise SystemExit("artwork has no visible shield (empty bbox)")
        x0, y0, x1, y1 = self.bbox
        self.bw, self.bh = x1 - x0, y1 - y0
        self.diag = math.hypot(self.bw, self.bh)
        self.cx, self.cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0

    def _scaled(self, scale):
        nw = max(1, round(self.square.width * scale))
        nh = max(1, round(self.square.height * scale))
        return self.square.resize((nw, nh), Image.LANCZOS)

    def padded(self, size):
        """Shield at SAFE_FRAC of the canvas, centred on solid #171717.

        This is the single 'look' for every opaque output: the legacy square
        icon, the legacy round icon (at this fraction the shield's diagonal
        still fits the inscribed circle), the @drawable fallback and the docs
        assets, so they all carry identical padding.
        """
        scale = (size * self.safe_frac) / max(self.bw, self.bh)
        s = self._scaled(scale)
        out = Image.new("RGB", (size, size), BG)
        out.paste(s, (round(size / 2 - self.cx * scale),
                      round(size / 2 - self.cy * scale)))
        return out

    def round_legacy(self, size):
        """Legacy round: same padded layout, which keeps the whole frame
        inside the inscribed circle (diagonal ~0.76 of the canvas)."""
        return self.padded(size)

    def adaptive_foreground(self, size):
        """Adaptive foreground: shield at SAFE_FRAC of the 108 dp layer,
        centred on transparency."""
        target = size * self.safe_frac
        scale = target / max(self.bw, self.bh)
        s = self._scaled(scale).convert("RGBA")
        ox = round(size / 2 - self.cx * scale)
        oy = round(size / 2 - self.cy * scale)
        layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        layer.alpha_composite(s, (ox, oy))
        return layer

    def adaptive_background(self, size):
        return Image.new("RGB", (size, size), BG)


# ==========================================================================
# writers
# ==========================================================================
def write_docs(icon, outdir):
    os.makedirs(outdir, exist_ok=True)
    for name, size in DOC_SIZES.items():
        icon.padded(size).save(os.path.join(outdir, name))
        print("  wrote", os.path.join(outdir, name), f"({size}px)")


def _drawable_target(resdir):
    """Respect an existing Resources/Drawable capitalisation, else lowercase."""
    cap_dir = os.path.join(resdir, "Drawable")
    low_dir = os.path.join(resdir, "drawable")
    if os.path.isdir(cap_dir):
        return os.path.join(cap_dir, "Icon.png")
    return os.path.join(low_dir, "icon.png")


def write_android(icon, resdir):
    os.makedirs(resdir, exist_ok=True)
    for bucket, legacy, adaptive in BUCKETS:
        d = os.path.join(resdir, "mipmap-" + bucket)
        os.makedirs(d, exist_ok=True)
        icon.padded(legacy).save(os.path.join(d, "ic_launcher.png"))
        icon.round_legacy(legacy).save(os.path.join(d, "ic_launcher_round.png"))
        icon.adaptive_foreground(adaptive).save(
            os.path.join(d, "ic_launcher_foreground.png"))
        icon.adaptive_background(adaptive).save(
            os.path.join(d, "ic_launcher_background.png"))
        print(f"  wrote mipmap-{bucket} "
              f"(legacy {legacy}px, adaptive {adaptive}px)")
    d = os.path.join(resdir, "mipmap-anydpi-v26")
    os.makedirs(d, exist_ok=True)
    for name in ("ic_launcher", "ic_launcher_round"):
        with open(os.path.join(d, name + ".xml"), "w", encoding="utf-8") as f:
            f.write(ADAPTIVE_XML)
    target = _drawable_target(resdir)
    os.makedirs(os.path.dirname(target), exist_ok=True)
    icon.padded(DRAWABLE_SIZE).save(target)
    print("  wrote", target, f"({DRAWABLE_SIZE}px) + mipmap-anydpi-v26/*.xml")


# ==========================================================================
def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--art", help="source image (icon.jpeg, or a pre-cropped PNG)")
    ap.add_argument("--crop", metavar="L,T,R,B",
                    help="override the crop box into the source (default %s)"
                         % (CROP_BOX,))
    ap.add_argument("--out", default=HERE, help="docs-asset output dir")
    ap.add_argument("--android", metavar="RESDIR", action="append", default=[],
                    help="also write the Android resource overlay into RESDIR "
                         "(repeatable: repo Resources + port Resources)")
    ap.add_argument("--safe-frac", type=float, default=SAFE_FRAC,
                    help="shield's largest dimension as a fraction of the "
                         "canvas (default %.2f); tune to zoom the artwork"
                         % SAFE_FRAC)
    ap.add_argument("--no-docs", action="store_true",
                    help="skip the docs/assets PNGs")
    args = ap.parse_args()

    art = find_art(args.art)
    if not art:
        raise SystemExit("could not find icon.jpeg; pass --art PATH")
    crop_box = CROP_BOX
    if args.crop:
        crop_box = tuple(int(v) for v in args.crop.split(","))
        if len(crop_box) != 4:
            raise SystemExit("--crop needs L,T,R,B")

    square = build_square(art, crop_box)
    icon = Icon(square, safe_frac=args.safe_frac)
    print(f"source    : {art}")
    print(f"crop      : {crop_box} -> "
          f"{square.size[0]}x{square.size[1] - SQUARE_PAD_TOP * 2} "
          f"(padded to {square.size})")
    print(f"shield bbox: {icon.bbox}  w={icon.bw} h={icon.bh}")
    print(f"safe frac : {icon.safe_frac:.2f} of the canvas")

    if not args.no_docs:
        write_docs(icon, args.out)
    for resdir in args.android:
        print("android   :", resdir)
        write_android(icon, os.path.abspath(resdir))


if __name__ == "__main__":
    main()
