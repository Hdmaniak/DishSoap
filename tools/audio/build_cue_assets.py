#!/usr/bin/env python3
"""
build_cue_assets.py -- turn the raw XSB parse into the runtime cue map used by
the MonoGame audio shim, and create cue-named hardlinks.

Reads  : Content-sfx/cue_map_raw.json   (parse_xsb.py --json output)
Writes : Content-sfx/cue_map.json       (runtime; cue -> list of wave files)
         Content-sfx/cue_map.csv        (human table)
         Content-sfx/cues/<cue>_<n>.ogg (hardlinks into ogg/, convenience)

Canonical wave file preference per cue:
   wav/<bank>/<bank>_w<idx>.wav   if present (MS-ADPCM, zero-dependency)
   else ogg/<bank>/<bank>_w<idx>.ogg
"""
import json
import os
import csv
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", "Content-sfx"))
RAW = os.path.join(ROOT, "cue_map_raw.json")


def canonical_paths(bank, idx):
    """Return (relative_path, kind) preferring ADPCM wav, else ogg."""
    wav = "wav/%s/%s_w%03d.wav" % (bank, bank, idx)
    ogg = "ogg/%s/%s_w%03d.ogg" % (bank, bank, idx)
    if os.path.exists(os.path.join(ROOT, wav)):
        return wav, "wav"
    return ogg, "ogg"


# Banks whose cues are long, seamless, self-looping tracks. Every XMA entry in
# the XWB carries a (whole-stream) loop region, so the raw loop metadata cannot
# distinguish one-shot SFX from music; looseness is decided by bank instead.
LOOP_BANKS = {"music", "solo_crux", "solo_dish", "solo_smash"}


def load_loop_flags():
    """bank -> set(all indices) for banks that should loop at runtime."""
    path = os.path.join(ROOT, "xwb_meta.json")
    if not os.path.exists(path):
        return {}
    meta = json.load(open(path))
    return {b["bank_name"]: set(range(b["entry_count"]))
            for b in meta if b["bank_name"] in LOOP_BANKS}


def main():
    if not os.path.exists(RAW):
        print("missing", RAW, file=sys.stderr)
        return 1
    banks = json.load(open(RAW))
    loops = load_loop_flags()

    flat = {}
    collisions = []
    rows = []
    for b in banks:
        bank = b["bank_name"]
        for cue, info in b["cues"].items():
            refs = []
            seen = set()
            for (wb_idx, track) in info["waves"]:
                # wb_idx indexes b["wave_banks"]; all our banks have exactly one
                # wave bank, so resolve the name and use the track as the wave index.
                wb_name = b["wave_banks"][wb_idx] if wb_idx < len(b["wave_banks"]) else bank
                if (wb_name, track) in seen:
                    continue
                seen.add((wb_name, track))
                path, kind = canonical_paths(wb_name, track)
                refs.append({"bank": wb_name, "index": track,
                             "file": path, "kind": kind,
                             "loop": track in loops.get(wb_name, set())})
            if cue in flat:
                collisions.append(cue)
            flat[cue] = {
                "bank": bank,
                "wave_bank": b["wave_banks"][0],
                "type": info.get("type"),
                # XGS category ids: 0 Global, 1 Default, 2 Music, 3 solo_bg,
                # 4 solo_fg, 5 halper. Complex-cue sounds resolve to Default.
                "category_id": info.get("category_id") if info.get("category_id") is not None else 1,
                "files": refs,
            }
            for n, r in enumerate(refs):
                rows.append([bank, cue, info.get("type"), n,
                             r["bank"], r["index"], r["file"], r["kind"],
                             "loop" if r["loop"] else ""])

    out = {
        "source": "XACT XSB cue->wave extraction (parse_xsb.py)",
        "note": "files are relative to Content-sfx/; prefer .wav (MS-ADPCM) "
                "when present, else .ogg (Vorbis)",
        "cue_count": len(flat),
        "cues": flat,
    }
    with open(os.path.join(ROOT, "cue_map.json"), "w") as f:
        json.dump(out, f, indent=2)

    with open(os.path.join(ROOT, "cue_map.csv"), "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["bank", "cue", "type", "variation", "wave_bank",
                    "wave_index", "file", "kind", "loop"])
        w.writerows(rows)

    # Optional cue-named hardlinks (off by default: a hardlink tree duplicates
    # on copy and the JSON/CSV map is the authoritative cue->file lookup).
    made = 0
    if "--links" in sys.argv:
        cuesdir = os.path.join(ROOT, "cues")
        os.makedirs(cuesdir, exist_ok=True)
        for cue, info in flat.items():
            for n, r in enumerate(info["files"]):
                src = os.path.join(ROOT, r["file"])
                dst = os.path.join(cuesdir, "%s_%d.%s" % (cue, n, r["kind"]))
                if os.path.exists(dst):
                    os.remove(dst)
                try:
                    os.link(src, dst)
                except OSError:
                    import shutil
                    shutil.copy2(src, dst)
                made += 1

    print("cues: %d, rows: %d, hardlinks: %d, collisions: %s"
          % (len(flat), len(rows), made, collisions or "none"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
