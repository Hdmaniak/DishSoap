#!/usr/bin/env python3
"""Extract XNA 3.0 (Xbox 360) Effect payloads from XNB v2 files and inventory them."""
import os, sys, struct, re, json, collections

# Input: your cleaned extracted fx tree.  Override with DISHWASHER_FX_DIR, or
# DISHWASHER_ASSETS_DIR (whose fx/ subdir is used).  Nothing machine-specific.
FXDIR = os.environ.get(
    "DISHWASHER_FX_DIR",
    os.path.join(os.environ.get(
        "DISHWASHER_ASSETS_DIR", os.path.expanduser("~/dishwasher-assets-clean")), "fx"),
)
OUTDIR = os.environ.get(
    "DISHWASHER_SHADER_OUT",
    os.path.join(os.environ.get("XDG_CACHE_HOME", os.path.expanduser("~/.cache")),
                 "dishwasher", "shader-payloads"))

def read_xnb(path):
    d = open(path,'rb').read()
    assert d[:3] == b'XNB', d[:4]
    plat = d[3:4]
    ver = d[4]
    flags = d[5]
    size = struct.unpack_from('<I', d, 6)[0]
    p = 10
    # 7-bit encoded reader count
    def read7(p):
        val = 0; shift = 0
        while True:
            b = d[p]; p += 1
            val |= (b & 0x7f) << shift
            if not (b & 0x80): break
            shift += 7
        return val, p
    nread, p = read7(p)
    readers = []
    for _ in range(nread):
        s, p = read7(p)
        name = d[p:p+s].decode('ascii'); p += s
        v = struct.unpack_from('<i', d, p)[0]; p += 4
        readers.append((name, v))
    shared, p = read7(p)
    # object graph: type reader index (7bit), then payload
    idx, p = read7(p)
    # EffectReader payload: int32 count then bytes
    count = struct.unpack_from('<i', d, p)[0]; p += 4
    payload = d[p:p+count]
    return dict(platform=plat, ver=ver, flags=flags, size=size, readers=readers,
                shared=shared, objidx=idx, count=count, payload=payload,
                payload_off=p)

def strings_in(payload, minlen=3):
    out = []
    i = 0
    n = len(payload)
    while i < n:
        # find printable run
        j = i
        while j < n and 0x20 <= payload[j] < 0x7f:
            j += 1
        if j - i >= minlen:
            # null-terminated?
            nz = (j < n and payload[j] == 0)
            out.append((i, payload[i:j].decode('ascii'), j-i, nz))
        i = j + 1
    return out

def main():
    files = sorted(os.listdir(FXDIR))
    allinfo = {}
    for fn in files:
        if not fn.endswith('.xnb'): continue
        path = os.path.join(FXDIR, fn)
        info = read_xnb(path)
        name = fn[:-4]
        allinfo[name] = info
        strs = strings_in(info['payload'])
        print(f"===== {fn}  filesize={os.path.getsize(path)} payload_len={info['count']} payload_off={info['payload_off']} =====")
        print("  readers:", [r[0].split('.')[-1] for r in info['readers']])
        print("  header[0:32]:", info['payload'][:32].hex(' '))
        for off, s, ln, nz in strs:
            print(f"    +0x{off:04x} len={ln:3d} {'' if nz else 'NONULL'}  {s!r}")
    # save payloads
    os.makedirs(OUTDIR, exist_ok=True)
    for name, info in allinfo.items():
        open(f'{OUTDIR}/{name}.bin','wb').write(info['payload'])
    print("\n\nSummary: effect -> payload size, #strings, first 8 bytes")
    for name, info in sorted(allinfo.items(), key=lambda kv: kv[1]['count']):
        print(f"  {name:12s} {info['count']:5d}  hdr={info['payload'][:8].hex(' ')}  nstr={len(strings_in(info['payload']))}")

if __name__ == '__main__':
    main()
