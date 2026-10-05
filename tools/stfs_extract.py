#!/usr/bin/env python3
"""
stfs_extract.py -- reference STFS/LIVE ("XContent") extractor used to validate
the on-device C# implementation of the same algorithm.

Ported from the Xenia/ReXGlue STFS reader
(rexglue-sdk/src/filesystem/devices/stfs_container_device.cpp), which is the
authoritative description of the format:
  * XContentHeader (0x344) + XContentMetadata (0x93D6); header.header_size at 0x340.
  * STFS volume descriptor at 0x379 (descriptor_length 0x24).
  * Data is stored in 0x1000-byte blocks.  Every 170 data blocks are followed by
    `blocks_per_hash_table` (1 for read-only, else 2) 0x1000 hash-table blocks.
    Level-1 tables cover 170 level-0 tables, etc.
  * The file/directory table is a chain of 0x1000 directory blocks; each holds
    0x40 0x40-byte entries.  File data is a block chain; the "next block" lives
    in the level-0 hash table entry for the current block.

Usage:
    python3 stfs_extract.py <package> --list
    python3 stfs_extract.py <package> --extract <outdir> [--match substr]
"""
from __future__ import annotations

import argparse
import os
import struct
import sys

K_BLOCK = 0x1000
K_END_OF_CHAIN = 0xFFFFFF
BLOCKS_PER_HASH_LEVEL = (170, 170 * 170, 170 * 170 * 170)


def round_up(v: int, m: int) -> int:
    return (v + m - 1) // m * m


def u24le(b: bytes, off: int) -> int:
    return b[off] | (b[off + 1] << 8) | (b[off + 2] << 16)


class Stfs:
    def __init__(self, fh):
        self.fh = fh
        fh.seek(0)
        self.header = fh.read(0x344)
        magic = self.header[0:4]
        if magic not in (b"LIVE", b"CON ", b"PIRS"):
            raise ValueError("not an STFS/LIVE package (magic %r)" % magic)
        self.header_size = struct.unpack_from(">I", self.header, 0x340)[0]
        # volume descriptor at 0x379 (inside XContentMetadata, past the 0x344 header)
        fh.seek(0x379)
        vd = fh.read(0x24)
        if vd[0] != 0x24:
            raise ValueError("unexpected volume descriptor length %d" % vd[0])
        self.read_only = (vd[2] & 1) != 0
        # NOTE: file_table_block_count is a plain uint16_t in the Xenia/ReXGlue
        # struct (no be<> wrapper), i.e. it is stored little-endian on disk.
        self.file_table_block_count = struct.unpack_from("<H", vd, 3)[0]
        self.file_table_block_number = u24le(vd, 5)
        self.total_block_count = struct.unpack_from(">I", vd, 0x1C)[0]
        self.blocks_per_hash_table = 1 if self.read_only else 2
        self.block_step = (
            BLOCKS_PER_HASH_LEVEL[0] + self.blocks_per_hash_table,
            BLOCKS_PER_HASH_LEVEL[1]
            + (BLOCKS_PER_HASH_LEVEL[0] + 1) * self.blocks_per_hash_table,
        )
        self.base = round_up(self.header_size, K_BLOCK)

    # --- block -> file offset -------------------------------------------
    def block_to_offset(self, block_index: int) -> int:
        base = BLOCKS_PER_HASH_LEVEL[0]
        block = block_index
        for _ in range(3):
            block += ((block_index + base) // base) * self.blocks_per_hash_table
            if block_index < base:
                break
            base *= BLOCKS_PER_HASH_LEVEL[0]
        return self.base + (block << 12)

    def hash_block_number(self, block_index: int, level: int) -> int:
        if level == 0:
            if block_index < 170:
                return 0
            block = (block_index // 170) * self.block_step[0]
            block += ((block_index // 28900) + 1) * self.blocks_per_hash_table
            if block_index < 28900:
                return block
            return block + self.blocks_per_hash_table
        if level == 1:
            if block_index < 28900:
                return self.block_step[0]
            block = (block_index // 28900) * self.block_step[1]
            return block + self.blocks_per_hash_table
        return self.block_step[1]

    def _read(self, off: int, size: int) -> bytes:
        self.fh.seek(off)
        return self.fh.read(size)

    def next_block(self, block_index: int) -> int:
        hb = self.hash_block_number(block_index, 0)
        hoff = self.base + (hb << 12)
        table = self._read(hoff, K_BLOCK)
        rec = block_index % 170
        info = struct.unpack_from(">I", table, rec * 0x18 + 0x14)[0]
        return info & 0xFFFFFF

    # --- directory table -------------------------------------------------
    def entries(self):
        out = []
        table_block = self.file_table_block_number
        for _ in range(self.file_table_block_count):
            block = self._read(self.block_to_offset(table_block), K_BLOCK)
            for m in range(0x40):
                e = block[m * 0x40:(m + 1) * 0x40]
                if e[0] == 0:
                    break
                flags = e[40]
                name_len = flags & 0x3F
                is_dir = (flags >> 7) & 1
                name = e[:name_len].decode("ascii", "replace")
                length = struct.unpack_from(">I", e, 52)[0]
                start_block = u24le(e, 47)
                dir_index = struct.unpack_from(">H", e, 50)[0]
                out.append({
                    "name": name, "dir": bool(is_dir), "length": length,
                    "start_block": start_block, "dir_index": dir_index,
                    "valid_blocks": u24le(e, 41),
                    "alloc_blocks": u24le(e, 44), "path": "",
                })
            if table_block == K_END_OF_CHAIN:
                break
            table_block = self.next_block(table_block)
        # Directories are entries too, in creation order; resolve the tree.
        for i, e in enumerate(out):
            parent = e["dir_index"]
            prefix = ""
            if parent != 0xFFFF and 0 <= parent < i:
                prefix = out[parent]["path"] + "/"
            e["path"] = prefix + e["name"]
        return out

    def read_entry(self, e) -> bytes:
        block_index = e["start_block"]
        remaining = e["length"]
        chunks = []
        while remaining and block_index != K_END_OF_CHAIN:
            n = min(K_BLOCK, remaining)
            chunks.append(self._read(self.block_to_offset(block_index), n))
            remaining -= n
            block_index = self.next_block(block_index)
        return b"".join(chunks)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("package")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--extract")
    ap.add_argument("--match")
    ap.add_argument("--verify-sha")
    args = ap.parse_args()

    size = os.path.getsize(args.package)
    print("package size:", size)
    with open(args.package, "rb") as fh:
        st = Stfs(fh)
        print("header_size: 0x%X  base: 0x%X  read_only: %s  bpt: %d"
              % (st.header_size, st.base, st.read_only, st.blocks_per_hash_table))
        print("file_table_block_count:", st.file_table_block_count,
              " table_block:", st.file_table_block_number,
              " total_blocks:", st.total_block_count)
        ents = st.entries()
        print("entries:", len(ents))
        if args.list:
            for e in ents[:60]:
                print("  %s %-8s %8d  start=%d" % (
                    "D" if e["dir"] else "F", e["name"], e["length"], e["start_block"]))
            if len(ents) > 60:
                print("  ... (%d more)" % (len(ents) - 60))
            return 0
        if args.extract:
            import hashlib
            total = 0
            for e in ents:
                if e["dir"]:
                    continue
                if args.match and args.match not in e["name"]:
                    continue
                data = st.read_entry(e)
                out = os.path.join(args.extract, e["path"].replace("\\", "/"))
                os.makedirs(os.path.dirname(out), exist_ok=True)
                with open(out, "wb") as f:
                    f.write(data)
                total += 1
                print("  wrote %-50s %8d sha=%s" % (
                    e["name"], len(data), hashlib.sha256(data).hexdigest()[:16]))
            print("extracted files:", total)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
