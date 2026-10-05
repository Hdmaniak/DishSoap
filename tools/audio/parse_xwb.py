#!/usr/bin/env python3
"""
parse_xwb.py -- XACT Wave Bank (.xwb) metadata extractor for XNA GS 3.0 /
XACT v42-43 Xbox 360 banks (BIG-ENDIAN, byte-swapped "DNBW" = "WBND").

Layout follows DirectXTK WaveBankReader / MonoGame.Framework WaveBank.cs:
  52-byte header: magic, toolVersion, headerVersion, 5 * (offset u32, len u32)
  BANKDATA @ segments[0].offset: flags, entryCount, bankName[64],
      entryMetaDataElementSize, entryNameElementSize, alignment, [compactFormat]
  ENTRY metadata @ segments[1].offset: entryMetaDataElementSize bytes each
  ENTRY NAMES  @ segments[3].offset (v42+): entryNameElementSize bytes each
  PLAY REGION  @ segments[4].offset

Usage: python3 parse_xwb.py [--names] FILE.xwb ...
"""
import struct
import sys
import os
import json

FLAG_ENTRY_NAMES = 0x00010000
FLAG_COMPACT = 0x00020000

CODECS = {0: "PCM", 1: "XMA", 2: "ADPCM", 3: "WMA"}


class Xwb:
    def __init__(self, data, path):
        self.path = path
        self.data = data
        self.name = os.path.basename(path)
        if data[0:4] != b"DNBW":
            raise ValueError("not a byte-swapped XACT WBND wave bank (magic %r)"
                             % data[0:4])

        def u32(o):
            return struct.unpack_from(">I", data, o)[0]

        self.tool_version = u32(0x04)
        self.header_version = u32(0x08)
        self.segments = []
        for i in range(5):
            off = u32(0x0C + i * 8)
            length = u32(0x10 + i * 8)
            self.segments.append((off, length))

        bd = self.segments[0][0]
        self.flags = u32(bd)
        self.entry_count = u32(bd + 4)
        self.bank_name = data[bd + 8:bd + 72].split(b"\0", 1)[0].decode("latin-1")
        self.entry_meta_size = u32(bd + 72)
        self.entry_name_size = u32(bd + 76)
        self.alignment = u32(bd + 80)
        self.compact_format = None
        if self.flags & FLAG_COMPACT:
            self.compact_format = u32(bd + 84)

        self.entries = []
        self.names = []
        self._parse_entries()
        self._parse_names()

    def _decode_format(self, fmt):
        codec = CODECS.get(fmt & 0x3, "?%d" % (fmt & 0x3))
        channels = (fmt >> 2) & 0x7
        rate = (fmt >> 5) & 0x3FFFF
        alignment = (fmt >> 23) & 0xFF
        return codec, channels, rate, alignment

    def _parse_entries(self):
        data = self.data
        meta_off, meta_len = self.segments[1]
        play_off = self.segments[4][0]
        if play_off == 0:
            play_off = meta_off + self.entry_count * self.entry_meta_size
        for i in range(self.entry_count):
            o = meta_off + i * self.entry_meta_size
            flags_dur = struct.unpack_from(">I", data, o)[0]
            fmt = struct.unpack_from(">I", data, o + 4)[0]
            codec, channels, rate, align = self._decode_format(fmt)
            entry = {
                "index": i,
                "flags": (flags_dur >> 28) & 0xF,
                "duration_samples": flags_dur & 0x0FFFFFFF,
                "format_raw": fmt,
                "codec": codec,
                "channels": channels,
                "sample_rate": rate,
                "block_align": align,
            }
            if self.entry_meta_size >= 12:
                entry["play_offset"] = struct.unpack_from(">I", data, o + 8)[0]
            if self.entry_meta_size >= 16:
                entry["play_length"] = struct.unpack_from(">I", data, o + 12)[0]
            if self.entry_meta_size >= 20:
                entry["loop_offset"] = struct.unpack_from(">I", data, o + 16)[0]
            if self.entry_meta_size >= 24:
                entry["loop_length"] = struct.unpack_from(">I", data, o + 20)[0]
            self.entries.append(entry)

    def _parse_names(self):
        data = self.data
        if self.header_version < 42:
            return
        name_off, name_len = self.segments[3]
        if name_off == 0 or name_len == 0 or self.entry_name_size == 0:
            return
        for i in range(self.entry_count):
            o = name_off + i * self.entry_name_size
            raw = data[o:o + self.entry_name_size]
            self.names.append(raw.split(b"\0", 1)[0].decode("latin-1"))

    def to_dict(self):
        from collections import Counter
        codecs = Counter(e["codec"] for e in self.entries)
        return {
            "file": self.name,
            "bank_name": self.bank_name,
            "tool_version": self.tool_version,
            "header_version": self.header_version,
            "flags": "0x%08x" % self.flags,
            "has_entry_names": bool(self.flags & FLAG_ENTRY_NAMES),
            "entry_count": self.entry_count,
            "entry_meta_size": self.entry_meta_size,
            "entry_name_size": self.entry_name_size,
            "alignment": self.alignment,
            "codec_histogram": dict(codecs),
            "entries": self.entries,
            "names": self.names,
        }


def main():
    args = sys.argv[1:]
    out_path = "xwb_meta.json"
    if len(args) >= 2 and args[0] == "--json":
        out_path = args[1]
        args = args[2:]
    if not args:
        print(__doc__)
        return 1
    allb = []
    for path in args:
        with open(path, "rb") as f:
            data = f.read()
        x = Xwb(data, path)
        allb.append(x.to_dict())
        print("%-26s bank=%-10s entries=%3d codecs=%s names=%s" %
              (os.path.basename(path), x.bank_name, x.entry_count,
               dict((k, v) for k, v in
                    __import__("collections").Counter(
                        e["codec"] for e in x.entries).items()),
               x.names[:3] if x.names else "-"))
    with open(out_path, "w") as f:
        json.dump(allb, f, indent=2)
    print("wrote %s" % out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
