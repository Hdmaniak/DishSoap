#!/usr/bin/env python3
"""
gen_import_manifest.py -- build the SHA-256 manifest of the ORIGINAL game files
that the PUBLIC build needs the user to supply.

Only unmodified files from the shipped package are listed.  Nothing derived by
this project (PNG/GOG/WAV/cue_map.json/spritefont JSON/our compiled fx) is ever
listed: the manifest is a proof-of-ownership gate, so it must only name files
that come straight out of the retail package.

Required set (what the public build actually consumes):
  * data/**            -- original .zdx/.dqx/.dgx scripts + character data
  * gfx/**/*.xnb       -- original XNB v2 textures + SpriteFonts (derived on-device)
  * gfx/maps/maps.zdx  -- original map-segment script (read with File.Open)
  * Resources/**       -- original .NET .resources localized strings
  * sfx/**/*.xwb/.xsb/.xgs -- original XACT audio banks (decoded on-device:
                        XMA/XMA2 -> WAV/OGG, cue_map.json)

Usage:
  python3 gen_import_manifest.py \
      --source "$DISHWASHER_ASSETS_DIR" \
      --out    src/Dishwasher/Assets/import-manifest.json
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from datetime import datetime, timezone


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def wanted(rel: str) -> bool:
    rel = rel.replace("\\", "/")
    if rel.startswith("data/"):
        return True
    if rel.startswith("Resources/"):
        return True
    if rel.startswith("sfx/"):
        return rel.lower().endswith((".xwb", ".xsb", ".xgs"))
    if rel.startswith("gfx/"):
        return rel.lower().endswith(".xnb") or rel == "gfx/maps/maps.zdx"
    return False


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument(
        "--source",
        default=os.environ.get(
            "DISHWASHER_ASSETS_DIR", os.path.expanduser("~/dishwasher-assets-clean")
        ),
    )
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    src = os.path.abspath(args.source)
    files = []
    for root, _dirs, names in os.walk(src):
        for name in names:
            full = os.path.join(root, name)
            rel = os.path.relpath(full, src).replace("\\", "/")
            if not wanted(rel):
                continue
            files.append({
                "path": rel,
                "size": os.path.getsize(full),
                "sha256": sha256_file(full),
            })
    files.sort(key=lambda r: r["path"])

    counts = {
        "data": sum(1 for f in files if f["path"].startswith("data/")),
        "gfx_xnb": sum(1 for f in files if f["path"].startswith("gfx/")
                       and f["path"].endswith(".xnb")),
        "gfx_zdx": sum(1 for f in files if f["path"].startswith("gfx/")
                       and f["path"].endswith(".zdx")),
        "resources": sum(1 for f in files if f["path"].startswith("Resources/")),
        "sfx": sum(1 for f in files if f["path"].startswith("sfx/")),
    }
    manifest = {
        "version": 1,
        "generated": datetime.now(timezone.utc).isoformat(),
        "algorithm": "sha256",
        "note": ("Original retail files only. Generated on-device derivations "
                 "(PNG textures, font atlases, audio) are NOT listed. A file is "
                 "accepted only if its size and SHA-256 match exactly."),
        "required_counts": counts,
        "required_total": len(files),
        "files": files,
    }
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w") as f:
        json.dump(manifest, f, indent=1, sort_keys=True)
    print("required total:", len(files))
    print("counts        :", counts)
    print("wrote         :", args.out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
