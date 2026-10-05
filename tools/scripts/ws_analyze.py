#!/usr/bin/env python3
"""Widescreen verification helper.

Reports:
  * image size
  * bounding box of non-black content (threshold on max(r,g,b))
  * the fill fraction of the frame
  * per-corner presence probe (for HUD anchoring)
"""
import sys
from PIL import Image, ImageChops


def bbox_nonblack(im, thr=8):
    px = im.convert("RGB")
    r, g, b = px.split()
    mx = ImageChops.lighter(ImageChops.lighter(r, g), b)
    mask = mx.point(lambda v: 255 if v > thr else 0)
    bb = mask.getbbox()
    if bb is None:
        return None
    # getbbox: right/lower exclusive -> convert to inclusive
    return (bb[0], bb[1], bb[2] - 1, bb[3] - 1)


def mean_rect(im, x0, y0, x1, y1):
    px = im.convert("RGB")
    data = px.load()
    n = 0
    rs = gs = bs = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            r, g, b = data[x, y]
            rs += r; gs += g; bs += b; n += 1
    if n == 0:
        return (0, 0, 0)
    return (rs / n, gs / n, bs / n)


def main():
    path = sys.argv[1]
    im = Image.open(path)
    print(f"file: {path}")
    print(f"size: {im.size[0]}x{im.size[1]}")
    bb = bbox_nonblack(im)
    w, h = im.size
    print(f"bbox_nonblack(thr=8): {bb}")
    if bb:
        x0, y0, x1, y1 = bb
        print(f"  width={x1-x0+1} height={y1-y0+1}")
        print(f"  margins L={x0} T={y0} R={w-1-x1} B={h-1-y1}")
        print(f"  fill_fraction={(x1-x0+1)*(y1-y0+1)/(w*h):.4f}")
        print(f"  fills_frame={x0==0 and y0==0 and x1==w-1 and y1==h-1}")
    for name, (x0, y0) in {
        "TL": (0, 0), "TR": (w - 40, 0), "BL": (0, h - 40), "BR": (w - 40, h - 40)
    }.items():
        m = mean_rect(im, x0, y0, x0 + 40, y0 + 40)
        print(f"corner {name} mean RGB = {m[0]:.1f},{m[1]:.1f},{m[2]:.1f}")
    if len(sys.argv) > 2:
        vals = [int(v) for v in sys.argv[2:6]]
        print("rect mean:", mean_rect(im, *vals))


if __name__ == "__main__":
    main()
