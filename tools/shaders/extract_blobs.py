#!/usr/bin/env python3
import os, sys, struct, hashlib
sys.path.insert(0, '/tmp/opencode/shader')
from effect_parse import parse, PAYLOAD_DIR

OUT = '/tmp/opencode/shader/blobs'
os.makedirs(OUT, exist_ok=True)
allblobs = {}
manifest = []
for fn in sorted(os.listdir(PAYLOAD_DIR)):
    if not fn.endswith('.bin'):
        continue
    nm = fn[:-4]
    inv = parse(open(os.path.join(PAYLOAD_DIR, fn), 'rb').read())
    idx = 0
    for s in inv['small']:
        if s['type'] in ('pixelshader', 'vertexshader'):
            pass
    for s in inv['large']:
        if s['type'] != 0:
            continue
        b = s['blob']
        h = hashlib.sha256(b).hexdigest()
        m = struct.unpack_from('>III', b, 0)
        # find shader profile string in blob
        prof = b''
        for cand in (b'ps_3_0', b'vs_3_0', b'xps_3_0', b'xvs_3_0'):
            if cand in b:
                prof = cand
                break
        name = f"{nm}_t{s['technique']}p{s['pass']}"
        open(os.path.join(OUT, name + '.bin'), 'wb').write(b)
        manifest.append((name, len(b), h[:16], m, prof.decode() if prof else '?', s['technique'], s['pass']))
        allblobs.setdefault(h, []).append(name)

print("name                          len   sha16             hdr0        hdr1   hdr2   profile")
for name, ln, h, m, prof, t, p in manifest:
    print(f"{name:28s} {ln:5d} {h}  {m[0]:08x} {m[1]:6d} {m[2]:6d}  {prof}")

print("\n=== distinct blobs (sha) ===")
for h, names in sorted(allblobs.items(), key=lambda kv: -len(kv[1])):
    print(f"{h[:16]}  x{len(names):2d}  {names}")
print(f"\ntotal blobs={len(manifest)} distinct={len(allblobs)}")
