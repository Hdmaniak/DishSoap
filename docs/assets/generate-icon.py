#!/usr/bin/env python3
"""Generate ORIGINAL launcher-icon concepts for the port.

.. deprecated:: shipped icon
   SUPERSEDED.  The project now uses the user-supplied artwork instead of these
   procedural concepts.  The shipping generator is
   ``docs/assets/generate-icon-from-art.py``; this file is kept only for the
   historical banner/contact-sheet helpers and its "halftone-burst" concept is
   NO LONGER wired into the app.

Everything here is drawn procedurally with Pillow from simple geometry and the
project's own palette.  It is 100% original work: it contains NO game artwork,
NO characters, NO title lettering, NO sprites, NO panel art, and nothing traced
or derived from any reference frame.  The only typeface used is DejaVu Sans
(Bitstream Vera / public licence), and even that is only used for generic words
(never to reproduce a logo).

Theme: grungy black-and-white comic / ink, high contrast, distressed texture,
blood-red accents, comic speech-bubble yellow, halftone / ben-day dots.  Each
concept is a *bold shape* first so it still reads at 48 px (the launcher size).

Usage
-----
    python3 docs/assets/generate-icon.py                 # writes next to script
    python3 docs/assets/generate-icon.py --out DIR       # write elsewhere
    python3 docs/assets/generate-icon.py --only monogram # one concept only
    python3 docs/assets/generate-icon.py --banner monogram

Outputs (per concept)
---------------------
    icon-<name>-512.png         full square icon
    icon-<name>-fg-512.png      adaptive-icon foreground (content in safe zone)
    icon-<name>-bg-512.png      adaptive-icon background
Plus:
    icon-concepts.png           contact sheet (large grid + 48 px strip)
    banner-<name>-1024x500.png   README banner for the recommended concept
    banner-1024x500.png          same banner under a generic name
"""
import argparse
import math
import os
import random

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))

# --------------------------------------------------------------------------
# Palette (entirely our own choices, not sampled from the game).
# --------------------------------------------------------------------------
INK       = (13, 12, 14)
INK_SOFT  = (30, 27, 32)
PAPER     = (238, 233, 218)
PAPER_DIM = (208, 201, 186)
BLOOD     = (198, 24, 30)
BLOOD_DK  = (138, 13, 18)
YELLOW    = (255, 209, 42)
YELLOW_DK = (206, 146, 10)

FONT_BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
FONT_REG  = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"

SS = 4          # supersample factor
BASE = 512      # output icon size


# ==========================================================================
# small geometry / drawing helpers
# ==========================================================================
def rng(seed):
    return random.Random(seed)


def blob(cx, cy, radius, n, jitter, seed, squash=1.0, rot=0.0):
    """Return points of an irregular (ink-blob) polygon."""
    rr = random.Random(seed)
    pts = []
    for i in range(n):
        a = 2 * math.pi * i / n + rot
        rad = radius * (1.0 + rr.uniform(-jitter, jitter))
        pts.append((cx + math.cos(a) * rad,
                    cy + math.sin(a) * rad * squash))
    return pts


def wedge(cx, cy, length, width, angle_deg, tip_frac=1.0):
    """Tapered blade/action-slash polygon centred on (cx, cy)."""
    a = math.radians(angle_deg)
    ux, uy = math.cos(a), math.sin(a)
    nx, ny = -uy, ux
    hx, hy = ux * length / 2.0, uy * length / 2.0
    w = width / 2.0
    return [
        (cx + hx, cy + hy),                                   # sharp tip
        (cx - hx + nx * w, cy - hy + ny * w),                 # base corner
        (cx - hx - nx * w, cy - hy - ny * w),                 # base corner
    ]


def star(cx, cy, r_out, r_in, spikes, rot=0.0):
    pts = []
    for i in range(spikes * 2):
        a = math.pi * i / spikes + rot
        rad = r_out if i % 2 == 0 else r_in
        pts.append((cx + math.cos(a) * rad, cy + math.sin(a) * rad))
    return pts


def horizontal_band(x0, x1, yc, thick, seed, rough=0.05):
    """A rough-edged horizontal band as a polygon."""
    rr = random.Random(seed)
    n = 26
    top, bot = [], []
    for i in range(n + 1):
        t = i / n
        x = x0 + (x1 - x0) * t
        j = rr.uniform(-rough, rough) * thick
        top.append((x, yc - thick / 2 + j))
        bot.append((x, yc + thick / 2 + j * 0.8))
    return top + bot[::-1]


def alpha_composite(base, layer, mask=None):
    if mask is not None:
        layer = layer.copy()
        layer.putalpha(ImageChops.multiply(layer.getchannel("A"), mask))
    base.alpha_composite(layer)


def draw_halftone(size, color, cx, cy, rmax, spacing, r0, r1,
                  mask=None, jitter=0.0, seed=0):
    """Ben-day style dot field on a transparent layer."""
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    rr = random.Random(seed)
    col = color + (255,)
    yy = 0
    while yy < size:
        xx = 0
        while xx < size:
            jx = rr.uniform(-jitter, jitter) * spacing
            jy = rr.uniform(-jitter, jitter) * spacing
            px, py = xx + jx, yy + jy
            dist = math.hypot(px - cx, py - cy) / rmax
            if dist <= 1.0:
                rad = r0 + (r1 - r0) * dist
                d.ellipse([px - rad, py - rad, px + rad, py + rad], fill=col)
            xx += spacing
        yy += spacing
    alpha_composite_img = layer
    return alpha_composite_img, mask


def speckle(size, color, count, rmin, rmax, seed, alpha=255):
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    rr = random.Random(seed)
    col = color + (alpha,)
    for _ in range(count):
        x = rr.uniform(0, size)
        y = rr.uniform(0, size)
        rad = rr.uniform(rmin, rmax)
        d.ellipse([x - rad, y - rad, x + rad, y + rad], fill=col)
    return layer


def distressed_rect(size, box, radius, fill, seed, notches=34,
                    notch_color=INK, notch_depth=0.035):
    """Filled rounded rect with random bites taken out of its edge."""
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    d.rounded_rectangle(box, radius=radius, fill=fill + (255,))
    rr = random.Random(seed)
    x0, y0, x1, y1 = box
    w = x1 - x0
    depth = notch_depth * w
    per = max(1, notches // 4)
    for _ in range(per):                       # each edge
        # top
        bx = rr.uniform(x0, x1)
        d.polygon([(bx - depth, y0), (bx + depth, y0), (bx, y0 + depth)],
                  fill=notch_color + (255,))
        # bottom
        bx = rr.uniform(x0, x1)
        d.polygon([(bx - depth, y1), (bx + depth, y1), (bx, y1 - depth)],
                  fill=notch_color + (255,))
        # left
        by = rr.uniform(y0, y1)
        d.polygon([(x0, by - depth), (x0, by + depth), (x0 + depth, by)],
                  fill=notch_color + (255,))
        # right
        by = rr.uniform(y0, y1)
        d.polygon([(x1, by - depth), (x1, by + depth), (x1 - depth, by)],
                  fill=notch_color + (255,))
    return layer


def grain(img, seed, light=220, dark=900):
    """Subtle grindhouse speckle over the whole canvas (in place)."""
    S = img.size[0]
    img.alpha_composite(speckle(S, PAPER_DIM, light, S * 0.001,
                                S * 0.0035, seed, alpha=70))
    img.alpha_composite(speckle(S, (0, 0, 0), dark, S * 0.001,
                                S * 0.003, seed + 7, alpha=90))


def vignette(img, strength=120):
    S = img.size[0]
    v = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(v)
    d.ellipse([-S * 0.25, -S * 0.25, S * 1.25, S * 1.25], fill=255)
    v = v.filter(ImageFilter.GaussianBlur(S * 0.12))
    inv = ImageChops.invert(v)
    shade = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    shade.putalpha(inv.point(lambda p: int(p * strength / 255)))
    img.alpha_composite(shade)


# ==========================================================================
# MARK DRAWERS  (each draws the foreground on a transparent canvas)
# ==========================================================================
def mark_panel_slash(img, S):
    d = ImageDraw.Draw(img, "RGBA")
    # bone-white comic panel with a distressed black frame
    pad = 0.085 * S
    panel = distressed_rect(S, (pad, pad, S - pad, S - pad), int(S * 0.045),
                            PAPER, seed=11, notches=40, notch_color=INK,
                            notch_depth=0.035)
    img.alpha_composite(panel)

    # halftone dot field in the lower-left inside the panel
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (pad, pad, S - pad, S - pad), radius=int(S * 0.045), fill=255)
    dots, _ = draw_halftone(S, INK, S * 0.16, S * 0.90, S * 0.68,
                            int(S * 0.062), S * 0.003, S * 0.017,
                            jitter=0.16, seed=21)
    alpha_composite(img, dots, mask)

    # black contact shadow wedge then the red action slash on top
    d.polygon(wedge(S * 0.50, S * 0.50, S * 0.98, S * 0.20, -42),
              fill=INK + (255,))
    d.polygon(wedge(S * 0.47, S * 0.53, S * 0.92, S * 0.155, -42),
              fill=BLOOD + (255,))
    d.polygon(wedge(S * 0.50, S * 0.50, S * 0.55, S * 0.052, -42),
              fill=YELLOW + (255,))

    # thin ink speed lines
    for off in (-0.16, 0.20):
        d.polygon(wedge(S * (0.5 + off * 0.4), S * (0.5 + off),
                        S * 0.7, S * 0.018, -42), fill=INK + (220,))
    # comic corner accent
    d.ellipse([S * 0.72, S * 0.12, S * 0.72 + S * 0.11, S * 0.12 + S * 0.11],
              fill=YELLOW + (255,), outline=INK + (255,), width=int(S * 0.012))


def mark_ink_band(img, S):
    d = ImageDraw.Draw(img, "RGBA")
    # rough red band behind the ink
    d.polygon(horizontal_band(S * 0.05, S * 0.95, S * 0.50, S * 0.20,
                              seed=31, rough=0.06), fill=BLOOD + (255,))
    d.polygon(horizontal_band(S * 0.05, S * 0.95, S * 0.51, S * 0.045,
                              seed=32, rough=0.10), fill=YELLOW + (255,))
    # main ink blob + satellites
    d.polygon(blob(S * 0.50, S * 0.48, S * 0.305, 40, 0.13, 41),
              fill=INK + (255,))
    for i in range(7):
        rr = random.Random(50 + i)
        ang = rr.uniform(0, 2 * math.pi)
        dist = rr.uniform(S * 0.34, S * 0.48)
        bx, by = S * 0.5 + math.cos(ang) * dist, S * 0.48 + math.sin(ang) * dist
        rad = rr.uniform(S * 0.03, S * 0.075)
        d.ellipse([bx - rad, by - rad, bx + rad, by + rad], fill=INK + (255,))
    # ink flick specks
    img.alpha_composite(speckle(S, INK, 150, S * 0.002, S * 0.010, 61, 230))
    # bone shine on the blob
    d.polygon(wedge(S * 0.40, S * 0.36, S * 0.20, S * 0.022, -38),
              fill=PAPER + (225,))


def mark_monogram(img, S):
    d = ImageDraw.Draw(img, "RGBA")
    # red band behind the lower half so the letters stay legible
    d.polygon(horizontal_band(S * 0.06, S * 0.94, S * 0.72, S * 0.30,
                              seed=71, rough=0.05), fill=BLOOD + (255,))
    d.polygon(horizontal_band(S * 0.06, S * 0.94, S * 0.86, S * 0.10,
                              seed=72, rough=0.09), fill=BLOOD_DK + (255,))
    # yellow blade slash behind the wordmark (an accent, not a strikethrough)
    d.polygon(wedge(S * 0.40, S * 0.72, S * 1.05, S * 0.075, -42),
              fill=YELLOW + (255,))
    # stencil monogram on top
    font = ImageFont.truetype(FONT_BOLD, int(S * 0.60))
    d.text((S * 0.50, S * 0.44), "DW", font=font, anchor="mm",
           fill=PAPER + (255,))
    # stencil cuts
    for (cx, cy, w, h) in ((0.30, 0.26, 0.030, 0.12),
                           (0.30, 0.66, 0.030, 0.12),
                           (0.62, 0.38, 0.028, 0.10)):
        d.rounded_rectangle([S * (cx - w / 2), S * (cy - h / 2),
                             S * (cx + w / 2), S * (cy + h / 2)],
                            radius=S * 0.01, fill=INK + (255,))


def mark_silhouette(img, S):
    d = ImageDraw.Draw(img, "RGBA")
    # red disc + bone ring
    cx, cy, r = S * 0.50, S * 0.47, S * 0.335
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=BLOOD + (255,))
    dots, _ = draw_halftone(S, BLOOD_DK, cx, cy, r, int(S * 0.055),
                            S * 0.004, S * 0.020, jitter=0.15, seed=81)
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).ellipse([cx - r, cy - r, cx + r, cy + r], fill=255)
    alpha_composite(img, dots, m)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=PAPER + (255,),
              width=int(S * 0.016))
    # standing blade silhouette (our own abstract mark — no figure likeness)
    tip = (cx, S * 0.105)
    d.polygon([tip, (cx + S * 0.072, S * 0.600), (cx - S * 0.072, S * 0.600)],
              fill=INK + (255,))
    # bone edge highlight on the blade
    d.line([(cx - S * 0.030, S * 0.30), (cx - S * 0.058, S * 0.575)],
           fill=PAPER + (210,), width=int(S * 0.012))
    # chip out of the edge (a "damaged" blade)
    d.polygon([(cx + S * 0.030, S * 0.285), (cx + S * 0.085, S * 0.330),
               (cx + S * 0.022, S * 0.400)], fill=BLOOD + (255,))
    # guard
    d.rounded_rectangle([cx - S * 0.205, S * 0.600, cx + S * 0.205, S * 0.658],
                        radius=S * 0.024, fill=INK + (255,))
    # grip
    d.rounded_rectangle([cx - S * 0.055, S * 0.658, cx + S * 0.055, S * 0.800],
                        radius=S * 0.022, fill=INK + (255,))
    for k in range(3):
        y = S * (0.685 + k * 0.040)
        d.line([cx - S * 0.055, y, cx + S * 0.055, y],
               fill=PAPER + (235,), width=int(S * 0.009))
    d.ellipse([cx - S * 0.062, S * 0.800, cx + S * 0.062, S * 0.900],
              fill=INK + (255,))


def mark_halftone_burst(img, S):
    d = ImageDraw.Draw(img, "RGBA")
    # radial ben-day burst
    dots, _ = draw_halftone(S, BLOOD, S * 0.5, S * 0.46, S * 0.46,
                            int(S * 0.050), S * 0.003, S * 0.024,
                            jitter=0.12, seed=91)
    img.alpha_composite(dots)
    # bold downward blade/arrow (strong small-size shape)
    d.polygon([(S * 0.50, S * 0.925), (S * 0.245, S * 0.205),
               (S * 0.50, S * 0.345), (S * 0.755, S * 0.205)],
              fill=INK + (255,))
    d.polygon([(S * 0.50, S * 0.835), (S * 0.325, S * 0.285),
               (S * 0.50, S * 0.395), (S * 0.675, S * 0.285)],
              fill=BLOOD + (255,))
    d.polygon([(S * 0.50, S * 0.760), (S * 0.40, S * 0.345),
               (S * 0.50, S * 0.425), (S * 0.60, S * 0.345)],
              fill=PAPER + (255,))
    # yellow tick ring corners
    for (px, py) in ((0.10, 0.10), (0.90, 0.10), (0.10, 0.90), (0.90, 0.90)):
        d.ellipse([S * px - S * 0.028, S * py - S * 0.028,
                   S * px + S * 0.028, S * py + S * 0.028],
                  fill=YELLOW + (255,), outline=INK + (255,),
                  width=int(S * 0.008))


def mark_bubble_slash(img, S):
    d = ImageDraw.Draw(img, "RGBA")
    # comic speech bubble with a tail (speech-bubble yellow)
    box = (S * 0.14, S * 0.15, S * 0.86, S * 0.70)
    d.rounded_rectangle(box, radius=S * 0.19, fill=YELLOW + (255,),
                        outline=INK + (255,), width=int(S * 0.032))
    d.polygon([(S * 0.30, S * 0.66), (S * 0.24, S * 0.94),
               (S * 0.46, S * 0.70)], fill=YELLOW + (255,),
              outline=INK + (255,), width=int(S * 0.032))
    # red impact star inside
    d.polygon(star(S * 0.50, S * 0.42, S * 0.22, S * 0.095, 9, rot=0.15),
              fill=BLOOD + (255,), outline=INK + (255,), width=int(S * 0.018))
    d.polygon(star(S * 0.50, S * 0.42, S * 0.11, S * 0.05, 9, rot=0.5),
              fill=YELLOW + (255,))
    # motion ticks
    for (x0, y0, x1, y1) in ((0.05, 0.30, 0.16, 0.30),
                             (0.84, 0.30, 0.95, 0.30),
                             (0.05, 0.55, 0.16, 0.55)):
        d.line([S * x0, S * y0, S * x1, S * y1], fill=INK + (255,),
               width=int(S * 0.022))


# ==========================================================================
# BACKGROUNDS
# ==========================================================================
def bg_ink(img, S, seed=3):
    d = ImageDraw.Draw(img, "RGBA")
    d.rectangle([0, 0, S, S], fill=INK + (255,))
    grain(img, seed)
    vignette(img, 130)


def bg_paper(img, S, seed=5):
    d = ImageDraw.Draw(img, "RGBA")
    d.rectangle([0, 0, S, S], fill=PAPER + (255,))
    grain(img, seed)
    vignette(img, 70)


def bg_ink_red(img, S, seed=9):
    bg_ink(img, S, seed)
    d = ImageDraw.Draw(img, "RGBA")
    d.polygon(horizontal_band(S * 0.0, S * 1.0, S * 0.5, S * 0.7,
                              seed=17, rough=0.03), fill=BLOOD_DK + (255,))
    grain(img, seed + 3)


# ==========================================================================
# CONCEPTS
# ==========================================================================
CONCEPTS = [
    dict(name="panel-slash", title="Comic Panel Slash",
         desc="Bold black comic frame, bone interior, red action slash + halftone.",
         mark=mark_panel_slash, bg=bg_ink, bgcol=INK),
    dict(name="ink-band", title="Ink Band",
         desc="Abstract grunge ink blob over a rough blood-red band.",
         mark=mark_ink_band, bg=bg_paper, bgcol=PAPER),
    dict(name="monogram", title="Stencil DW",
         desc="Geometric stencil monogram with a blade slash and red band.",
         mark=mark_monogram, bg=bg_ink, bgcol=INK),
    dict(name="silhouette", title="Standing Blade",
         desc="Abstract standing-blade silhouette on a red disc (no figure likeness).",
         mark=mark_silhouette, bg=bg_ink, bgcol=INK),
    dict(name="halftone-burst", title="Halftone Burst",
         desc="Radial ben-day burst with a bold downward blade wedge.",
         mark=mark_halftone_burst, bg=bg_paper, bgcol=PAPER),
    dict(name="bubble-slash", title="Bubble Slash",
         desc="Comic speech-bubble yellow with a red impact star.",
         mark=mark_bubble_slash, bg=bg_ink, bgcol=INK),
]
BY_NAME = {c["name"]: c for c in CONCEPTS}

SAFE = 0.62      # adaptive-icon content scale (fits the ~66% safe circle)


def render_mark(size, concept):
    """Transparent canvas with just the foreground mark."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    concept["mark"](img, size)
    return img


def render_full(concept, size=BASE, ss=SS):
    S = size * ss
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    concept["bg"](img, S)
    img.alpha_composite(render_mark(S, concept))
    return img.resize((size, size), Image.LANCZOS)


def render_fg(concept, size=BASE, ss=SS):
    S = size * ss
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    mark = render_mark(S, concept)
    safe = int(S * SAFE)
    mark = mark.resize((safe, safe), Image.LANCZOS)
    img.alpha_composite(mark, ((S - safe) // 2, (S - safe) // 2))
    return img.resize((size, size), Image.LANCZOS)


def render_bg(concept, size=BASE, ss=SS):
    S = size * ss
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    concept["bg"](img, S)
    return img.resize((size, size), Image.LANCZOS)


# ==========================================================================
# WRITERS
# ==========================================================================
def write_icon_set(concept, outdir):
    full = render_full(concept)
    fg = render_fg(concept)
    bg = render_bg(concept)
    n = concept["name"]
    full.save(os.path.join(outdir, f"icon-{n}-512.png"))
    fg.save(os.path.join(outdir, f"icon-{n}-fg-512.png"))
    bg.save(os.path.join(outdir, f"icon-{n}-bg-512.png"))
    print(f"  wrote icon-{n}-512.png (+adaptive fg/bg)")


# ------------------------------- android resources -------------------------
# (bucket, legacy ic_launcher px, adaptive foreground/background px)
ANDROID_BUCKETS = (
    ("mdpi", 48, 108),
    ("hdpi", 72, 162),
    ("xhdpi", 96, 216),
    ("xxhdpi", 144, 324),
    ("xxxhdpi", 192, 432),
)

# A complete adaptive-icon definition.  Requires API 26 (mipmap-anydpi-v26);
# older devices fall back to the legacy PNGs in mipmap-<density>/ic_launcher.png.
ADAPTIVE_XML = """<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@mipmap/ic_launcher_background" />
    <foreground android:drawable="@mipmap/ic_launcher_foreground" />
</adaptive-icon>
"""


def write_android_set(concept, outdir):
    """Write a complete Android resource overlay for one concept.

    The tree mirrors src/Dishwasher/Resources/ so it can be copied in directly:
        drawable/icon.png                          legacy @drawable/icon
        mipmap-<bucket>/ic_launcher.png            legacy launcher icon
        mipmap-<bucket>/ic_launcher_round.png      legacy round launcher icon
        mipmap-<bucket>/ic_launcher_foreground.png adaptive foreground (-fg)
        mipmap-<bucket>/ic_launcher_background.png adaptive background (-bg)
        mipmap-anydpi-v26/ic_launcher.xml          adaptive icon
        mipmap-anydpi-v26/ic_launcher_round.xml    adaptive round icon
    """
    for bucket, legacy, adaptive in ANDROID_BUCKETS:
        d = os.path.join(outdir, "mipmap-" + bucket)
        os.makedirs(d, exist_ok=True)
        full = render_full(concept, size=legacy)
        full.save(os.path.join(d, "ic_launcher.png"))
        full.save(os.path.join(d, "ic_launcher_round.png"))
        render_fg(concept, size=adaptive).save(
            os.path.join(d, "ic_launcher_foreground.png"))
        render_bg(concept, size=adaptive).save(
            os.path.join(d, "ic_launcher_background.png"))
        print(f"  wrote mipmap-{bucket} (legacy {legacy}px, adaptive {adaptive}px)")
    d = os.path.join(outdir, "mipmap-anydpi-v26")
    os.makedirs(d, exist_ok=True)
    for name in ("ic_launcher", "ic_launcher_round"):
        with open(os.path.join(d, name + ".xml"), "w", encoding="utf-8") as f:
            f.write(ADAPTIVE_XML)
    d = os.path.join(outdir, "drawable")
    os.makedirs(d, exist_ok=True)
    render_full(concept, size=BASE).save(os.path.join(d, "icon.png"))
    print(f"  wrote drawable/icon.png ({BASE}px) + mipmap-anydpi-v26/ *.xml")


# ------------------------------- contact sheet -----------------------------
def _fit(im, box):
    """Contain-fit an RGBA image inside a square box, return (im, x, y)."""
    im = im.copy()
    im.thumbnail((box, box), Image.LANCZOS)
    return im


def build_contact_sheet(concepts, path):
    W = 1180
    margin = 40
    cols = 3
    cell = (W - margin * 2) // cols
    tile = cell - 40
    labelh = 74
    header = 150
    rowh = tile + labelh

    # preview rows: (size, bgcolor, label)
    prev_sizes = [(96, INK, "96 px"), (48, INK, "48 px  (dark)"),
                  (48, PAPER, "48 px  (light)"), (32, INK, "32 px")]
    prev_h = sum(s + 58 for s, _, _ in prev_sizes) + 80

    H = header + rowh * 2 + prev_h + margin
    sheet = Image.new("RGBA", (W, H), INK + (255,))
    d = ImageDraw.Draw(sheet, "RGBA")

    # header
    d.rectangle([0, 0, W, header - 30], fill=BLOOD + (255,))
    d.rectangle([0, header - 30, W, header - 22], fill=YELLOW + (255,))
    f_title = ImageFont.truetype(FONT_BOLD, 46)
    f_sub = ImageFont.truetype(FONT_REG, 22)
    f_lab = ImageFont.truetype(FONT_BOLD, 22)
    f_small = ImageFont.truetype(FONT_REG, 18)
    d.text((margin, 30), "LAUNCHER ICON CONCEPTS", font=f_title,
           fill=PAPER + (255,))
    d.text((margin, 88),
           "100% original procedural artwork  \u00b7  no game assets  \u00b7  grunge ink "
           "\u00b7 blood red \u00b7 comic yellow", font=f_sub, fill=PAPER + (230,))

    # main grid
    for i, c in enumerate(concepts):
        r, col = divmod(i, cols)
        x = margin + col * cell + 20
        y = header + r * rowh + 20
        tileimg = _fit(render_full(c), tile)
        sheet.alpha_composite(tileimg, (x, y))
        d.rounded_rectangle([x - 6, y - 6, x + tile + 6, y + tile + 6],
                            radius=16, outline=(70, 66, 74, 255), width=2)
        d.text((x, y + tile + 14), c["title"], font=f_lab,
               fill=PAPER + (255,))
        d.text((x, y + tile + 42), c["name"], font=f_small,
               fill=YELLOW + (230,))

    # preview section
    py = header + rowh * 2 + 24
    d.text((margin, py), "SMALL-SIZE LEGIBILITY", font=f_lab,
           fill=PAPER + (255,))
    py += 44
    for size, bgcol, slab in prev_sizes:
        d.text((margin, py + size / 2 - 10), slab, font=f_small,
               fill=(170, 164, 172, 255))
        strip_x = margin + 170
        bg = Image.new("RGBA", (W - strip_x - margin, size + 20),
                       bgcol + (255,))
        dd = ImageDraw.Draw(bg)
        dd.rectangle([0, 0, bg.width - 1, bg.height - 1],
                     outline=(90, 86, 94, 255), width=2)
        xx = 16
        gap = 34
        for c in concepts:
            ic = render_full(c, size=size, ss=max(2, SS))
            bg.alpha_composite(ic, (xx, 10))
            xx += size + gap
        sheet.alpha_composite(bg, (strip_x, py))
        py += size + 58

    sheet.convert("RGB").save(path)
    print("wrote", path, sheet.size)


# ------------------------------- banner ------------------------------------
def build_banner(concept, path, W=1024, H=500):
    w, h = W * 2, H * 2
    canvas = Image.new("RGBA", (w, h), INK + (255,))
    d = ImageDraw.Draw(canvas, "RGBA")
    # red grindhouse band + grunge
    d.polygon(horizontal_band(0, w, h * 0.5, h * 0.76, seed=101, rough=0.015),
              fill=BLOOD_DK + (255,))
    grain(canvas, 111, light=600, dark=1800)
    vignette(canvas, 150)

    # icon tile on the left
    ic = render_full(concept, size=int(h * 0.62), ss=SS)
    ix, iy = int(h * 0.11), (h - ic.size[1]) // 2
    d = ImageDraw.Draw(canvas, "RGBA")
    d.rounded_rectangle([ix - 14, iy - 14, ix + ic.size[0] + 14,
                         iy + ic.size[1] + 14], radius=40,
                        fill=INK + (255,), outline=YELLOW + (255,), width=5)
    canvas.alpha_composite(ic, (ix, iy))

    # text block
    tx = ix + ic.size[0] + int(h * 0.06)
    f_big = ImageFont.truetype(FONT_BOLD, int(h * 0.126))
    f_mid = ImageFont.truetype(FONT_BOLD, int(h * 0.058))
    f_sml = ImageFont.truetype(FONT_REG, int(h * 0.045))
    # slash accent behind the wordmark
    d.polygon(wedge(tx + int(h * 0.10), h * 0.375, int(h * 0.26),
                    int(h * 0.030), -8), fill=BLOOD + (255,))
    d.text((tx, h * 0.175), "DISHWASHER", font=f_big, fill=PAPER + (255,))
    d.text((tx, h * 0.365), "native Android port", font=f_mid,
           fill=YELLOW + (255,))
    d.text((tx, h * 0.460), "MonoGame / .NET 8  \u00b7  arm64-v8a",
           font=f_sml, fill=PAPER_DIM + (255,))
    label = "UNOFFICIAL FAN PORT"
    tw = d.textlength(label, font=f_sml)
    px, py = tx, h * 0.575
    d.rounded_rectangle([px, py, px + tw + h * 0.05, py + h * 0.085],
                        radius=h * 0.042, outline=YELLOW + (255,), width=4)
    d.text((px + h * 0.025, py + h * 0.014), label, font=f_sml,
           fill=YELLOW + (255,))

    canvas.resize((W, H), Image.LANCZOS).convert("RGB").save(path)
    print("wrote", path, (W, H))


# ==========================================================================
def main():
    ap = argparse.ArgumentParser(description="Generate original icon concepts.")
    ap.add_argument("--out", default=HERE, help="output directory")
    ap.add_argument("--only", help="only this concept name")
    ap.add_argument("--banner", default="halftone-burst",
                    help="concept name(s) for the README banner, comma-separated")
    ap.add_argument("--android", metavar="DIR",
                    help="also write a complete Android resource overlay for the "
                         "chosen concept (--only, else the first --banner name) to DIR")
    ap.add_argument("--no-sheet", action="store_true")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    chosen = BY_NAME.get(args.only) if args.only else None
    todo = [chosen] if chosen else CONCEPTS
    for c in todo:
        write_icon_set(c, args.out)

    if not args.only and not args.no_sheet:
        build_contact_sheet(CONCEPTS, os.path.join(args.out, "icon-concepts.png"))

    names = [n.strip() for n in args.banner.split(",") if n.strip()]
    for i, n in enumerate(names):
        bc = BY_NAME.get(n)
        if bc is None:
            print("  banner: unknown concept", n)
            continue
        build_banner(bc, os.path.join(
            args.out, f"banner-{bc['name']}-1024x500.png"))
        if i == 0:
            build_banner(bc, os.path.join(args.out, "banner-1024x500.png"))

    if args.android:
        android_concept = chosen or (BY_NAME.get(names[0]) if names else None)
        if android_concept is None:
            print("  android: nothing to write (no --only / --banner concept)")
        else:
            os.makedirs(args.android, exist_ok=True)
            print(f"  writing Android resources for '{android_concept['name']}' "
                  f"to {args.android}")
            write_android_set(android_concept, args.android)


if __name__ == "__main__":
    main()
