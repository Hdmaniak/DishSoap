#!/usr/bin/env python3
"""
xnb_v2.py -- robust read-only parser for XNA Game Studio 2.0/3.0 XNB containers
(container version 2) with Xbox-360 ("platform 'x'") texture quirks.

Facts this module is built on (all measured; see android/notes/asset-formats.md):
  * header = "XNB" + platform(1) + version(1) + flags(1) + size(uint32 LE)
  * all integer fields in the payload are LITTLE-endian even on platform 'x'
  * type reader table: 7-bit count, then (7-bit strlen + utf8 name + int32 ver)*
  * then 7-bit shared-resource count (0 in every file we have)
  * then primary object: 7-bit typeId (0 == null, else readerIndex+1)
  * nested objects use the same 7-bit typeId scheme
  * Texture2D payload: int32 format, uint32 w, uint32 h, uint32 mipCount,
                       then per mip: uint32 dataSize + dataSize bytes
  * SurfaceFormat: 1 = Color (raw 32bpp), 28 = DXT1, 32 = DXT5
  * The XNA "Color" surface is Xbox-360 D3DFMT_A8R8G8B8.  The 360 is a
    BIG-endian platform, so the packed 32-bit word 0xAARRGGBB is stored MSB
    first: file bytes are A,R,G,B.  decode_texture(raw_order="argb") is the
    correct unpack; bgra/rgba are kept only for comparison.
  * Likewise the Xbox-360 DXT colour endpoints are BIG-endian RGB565 and the
    DXT5 alpha block is word-swapped BIG-endian (see decode_dxt1/decode_dxt5).

Pure stdlib; Pillow is only required by the export driver.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple

SURFACE_FORMATS: Dict[int, str] = {
    1: "Color",   # 32bpp A8R8G8B8 raw
    28: "Dxt1",
    32: "Dxt5",
}

PLATFORMS = {
    ord("w"): "Windows", ord("x"): "Xbox360", ord("m"): "WindowsPhone",
    ord("i"): "iOS", ord("a"): "Android", ord("d"): "DesktopGL",
}

# XNB v2 has NO flags byte: after the platform byte come Int16 version (LE) then
# Int32 file size.  The "flags" seen in many write-ups is the high byte of the
# version short (always 0x00 for version 2).  Compression flags only exist in v3+.
FLAG_COMPRESSED_LZX = 0x80
FLAG_COMPRESSED_LZ4 = 0x40

# XNA value types are serialized inside List<T> WITHOUT a per-element type id
# (ContentReader.ReadObjectInternal<T>(typeReader) checks TargetIsValueType).
VALUE_TYPES = {
    "Boolean", "Byte", "SByte", "Char", "Int16", "UInt16", "Int32", "UInt32",
    "Int64", "UInt64", "Single", "Double", "Decimal", "TimeSpan",
    "Vector2", "Vector3", "Vector4", "Matrix", "Quaternion", "Color", "Plane",
    "Ray", "Point", "Rectangle", "BoundingBox", "BoundingSphere",
    "BoundingFrustum", "Curve",
}


class XnbError(Exception):
    pass


def read_7bit_uint(data: bytes, offset: int) -> Tuple[int, int]:
    value = 0
    shift = 0
    while True:
        if offset >= len(data):
            raise XnbError("unexpected EOF while reading 7-bit int")
        b = data[offset]
        offset += 1
        value |= (b & 0x7F) << shift
        if (b & 0x80) == 0:
            return value, offset
        shift += 7
        if shift > 35:
            raise XnbError("7-bit int too long")


def read_cstring(data: bytes, offset: int) -> Tuple[str, int]:
    length, offset = read_7bit_uint(data, offset)
    raw = data[offset:offset + length]
    if len(raw) != length:
        raise XnbError("unexpected EOF while reading string")
    return raw.decode("utf-8", "replace"), offset + length


# ---------------------------------------------------------------------------
# DXT / BC decoders
# ---------------------------------------------------------------------------
def _rgb565_to_rgb(v: int) -> Tuple[int, int, int]:
    r = (v >> 11) & 0x1F
    g = (v >> 5) & 0x3F
    b = v & 0x1F
    return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2))


def _decode_color_block(block: bytes, off: int, be_endpoints: bool,
                        big_endian_index: bool) -> List[Tuple[int, int, int, int]]:
    if be_endpoints:
        c0 = (block[off] << 8) | block[off + 1]
        c1 = (block[off + 2] << 8) | block[off + 3]
    else:
        c0 = block[off] | (block[off + 1] << 8)
        c1 = block[off + 2] | (block[off + 3] << 8)
    idx = int.from_bytes(block[off + 4:off + 8],
                         "big" if big_endian_index else "little")
    r0, g0, b0 = _rgb565_to_rgb(c0)
    r1, g1, b1 = _rgb565_to_rgb(c1)
    if c0 > c1:
        cols = [
            (r0, g0, b0, 255),
            (r1, g1, b1, 255),
            ((2 * r0 + r1) // 3, (2 * g0 + g1) // 3, (2 * b0 + b1) // 3, 255),
            ((r0 + 2 * r1) // 3, (g0 + 2 * g1) // 3, (b0 + 2 * b1) // 3, 255),
        ]
    else:
        cols = [
            (r0, g0, b0, 255),
            (r1, g1, b1, 255),
            ((r0 + r1) // 2, (g0 + g1) // 2, (b0 + b1) // 2, 255),
            (0, 0, 0, 0),
        ]
    return [cols[(idx >> (2 * i)) & 0x3] for i in range(16)]


def decode_dxt1(data: bytes, width: int, height: int, *, be_endpoints: bool = True,
                big_endian_index: bool = False) -> bytes:
    """BC1/DXT1 -> RGBA8888. Returns width*height*4 bytes."""
    if len(data) < (width * height) // 2:
        raise XnbError("DXT1 payload too small for %dx%d" % (width, height))
    out = bytearray(width * height * 4)
    pos = 0
    for by in range((height + 3) // 4):
        for bx in range((width + 3) // 4):
            block = data[pos:pos + 8]
            pos += 8
            cols = _decode_color_block(block, 0, be_endpoints, big_endian_index)
            for py in range(4):
                y = by * 4 + py
                if y >= height:
                    continue
                for px in range(4):
                    x = bx * 4 + px
                    if x >= width:
                        continue
                    r, g, b, a = cols[py * 4 + px]
                    o = (y * width + x) * 4
                    out[o:o + 4] = bytes((r, g, b, a))
    return bytes(out)


def decode_dxt5(data: bytes, width: int, height: int, *, be_endpoints: bool = True,
                big_endian_index: bool = False, be_alpha: bool = True) -> bytes:
    """BC3/DXT5 -> RGBA8888.

    Measured 360 quirk: the whole DXT5 payload is stored as byte-swapped
    16-bit words.  The colour RGB565 endpoints are big-endian (be_endpoints)
    and the 8-byte alpha block is therefore ALSO word-swapped: its two
    endpoint bytes and its three 16-bit index words must be byte-reversed
    before the standard BC3 alpha decode (be_alpha).  big_endian_index toggles
    the 32-bit colour-index word; the clean decode uses little-endian index
    (False).
    """
    if len(data) < width * height:
        raise XnbError("DXT5 payload too small for %dx%d" % (width, height))
    out = bytearray(width * height * 4)
    pos = 0
    for by in range((height + 3) // 4):
        for bx in range((width + 3) // 4):
            block = data[pos:pos + 16]
            pos += 16
            if be_alpha:
                # swap bytes within each 16-bit word of the 8-byte alpha block:
                #   b0 b1 b2 b3 b4 b5 b6 b7  ->  b1 b0 b3 b2 b5 b4 b7 b6
                a0, a1 = block[1], block[0]
                abits = int.from_bytes(
                    bytes((block[3], block[2], block[5], block[4], block[7], block[6])),
                    "little",
                )
            else:
                a0, a1 = block[0], block[1]
                abits = int.from_bytes(block[2:8], "little")
            if a0 > a1:
                acols = [a0, a1] + [((a0 * (7 - i) + a1 * i) // 7) for i in range(1, 7)]
            else:
                acols = [a0, a1] + [((a0 * (5 - i) + a1 * i) // 5) for i in range(1, 5)] + [0, 255]
            ccols = _decode_color_block(block, 8, be_endpoints, big_endian_index)
            for py in range(4):
                y = by * 4 + py
                if y >= height:
                    continue
                for px in range(4):
                    x = bx * 4 + px
                    if x >= width:
                        continue
                    p = py * 4 + px
                    a = acols[(abits >> (3 * p)) & 0x7]
                    r, g, b, _ = ccols[p]
                    o = (y * width + x) * 4
                    out[o:o + 4] = bytes((r, g, b, a))
    return bytes(out)


# ---------------------------------------------------------------------------
# decoded objects
# ---------------------------------------------------------------------------
@dataclass
class Texture2D:
    format: int
    width: int
    height: int
    mips: List[Tuple[int, int, bytes]]  # level 0 first; (w,h,raw surface bytes)

    @property
    def format_name(self) -> str:
        return SURFACE_FORMATS.get(self.format, "Unknown(%d)" % self.format)

    def raw_size(self, level: int = 0) -> int:
        return len(self.mips[level][2])


@dataclass
class SpriteFont:
    texture: Texture2D
    glyphs: List[Tuple[int, int, int, int]]
    cropping: List[Tuple[int, int, int, int]]
    char_map: List[str]
    line_spacing: int
    spacing: float
    kerning: List[Tuple[float, float, float]]
    default_character: Optional[str]
    char_encoding: str = "utf-8"

    def info(self) -> Dict:
        return {
            "glyphs": len(self.glyphs),
            "cropping": len(self.cropping),
            "char_map": len(self.char_map),
            "line_spacing": self.line_spacing,
            "spacing": self.spacing,
            "kerning": len(self.kerning),
            "default_character": self.default_character,
            "char_encoding": self.char_encoding,
            "first_chars": "".join(self.char_map[:32]),
            "texture_w": self.texture.width,
            "texture_h": self.texture.height,
            "texture_format": self.texture.format_name,
            "texture_mips": len(self.texture.mips),
        }


# ---------------------------------------------------------------------------
# reader
# ---------------------------------------------------------------------------
class XnbFile:
    def __init__(self, data: bytes, path: str = "<mem>", read_primary: bool = True):
        self.data = data
        self.path = path
        self.magic = data[:3]
        self.platform = data[3] if len(data) > 3 else 0
        # XNB v2 header: Int16 version at offset 4 (LE), Int32 size at offset 6.
        self.version = struct.unpack_from("<h", data, 4)[0] if len(data) >= 6 else -1
        self.flags = 0  # v2 has no flags byte
        self.declared_size = struct.unpack_from("<I", data, 6)[0] if len(data) >= 10 else -1
        self.reader_names: List[str] = []
        self.reader_versions: List[int] = []
        self.shared_resource_count = 0
        self.primary_type_id = 0
        self.primary: object = None
        self.primary_reader: Optional[str] = None
        self.objects_read: List[str] = []
        self._o = 10
        self.table_end = 0
        self._read_primary = read_primary
        self._parse()

    # -- header / table ----------------------------------------------------
    def _parse(self) -> None:
        if self.magic != b"XNB":
            raise XnbError("%s: bad magic %r" % (self.path, self.magic))
        if self.flags & FLAG_COMPRESSED_LZX:
            raise XnbError("%s: LZX-compressed XNB (unsupported)" % self.path)
        if self.flags & FLAG_COMPRESSED_LZ4:
            raise XnbError("%s: LZ4-compressed XNB (unsupported)" % self.path)
        o = 10
        nreaders, o = read_7bit_uint(self.data, o)
        for _ in range(nreaders):
            name, o = read_cstring(self.data, o)
            ver = struct.unpack_from("<i", self.data, o)[0]
            o += 4
            self.reader_names.append(name)
            self.reader_versions.append(ver)
        self.shared_resource_count, o = read_7bit_uint(self.data, o)
        self.table_end = o
        self.primary_type_id, o = read_7bit_uint(self.data, o)
        self._o = o
        if self.primary_type_id != 0:
            self.primary_reader = self.reader_names[self.primary_type_id - 1]
            if self._read_primary:
                self.primary = self._read_object_index(self.primary_type_id - 1)

    @property
    def at_eof(self) -> bool:
        return self._o == len(self.data)

    # -- dispatch ----------------------------------------------------------
    @staticmethod
    def _short(name: str) -> str:
        return name.split("[[", 1)[0].rsplit(".", 1)[-1]

    def _element_type_name(self, list_reader_name: str) -> Optional[str]:
        """Extract 'Microsoft.Xna.Framework.Rectangle' from a ListReader`1[[...]] name."""
        if "[[" not in list_reader_name:
            return None
        inner = list_reader_name.split("[[", 1)[1]
        return inner.split(",", 1)[0].strip()

    def _element_reader_index(self, list_reader_name: str) -> Optional[int]:
        """Resolve the reader index of a List<T>'s element reader by name."""
        full = self._element_type_name(list_reader_name)
        if not full:
            return None
        simple = full.rsplit(".", 1)[-1]
        candidate = simple + "Reader"
        for i, n in enumerate(self.reader_names):
            if self._short(n) == candidate:
                return i
        return None

    def _read_object(self):
        """Read a 7-bit type id then the referenced object (reference-type path)."""
        tid, self._o = read_7bit_uint(self.data, self._o)
        if tid == 0:
            return None
        return self._read_object_index(tid - 1)

    def _read_object_index(self, idx: int):
        name = self.reader_names[idx]
        short = self._short(name)
        self.objects_read.append(short)
        if short == "Texture2DReader":
            return self._read_texture2d()
        if short == "SpriteFontReader":
            return self._read_spritefont()
        if short == "ListReader`1":
            (count,) = struct.unpack_from("<I", self.data, self._o)
            self._o += 4
            full = self._element_type_name(name)
            simple = full.rsplit(".", 1)[-1] if full else ""
            elem_idx = self._element_reader_index(name)
            is_value = simple in VALUE_TYPES
            if is_value and elem_idx is not None:
                # value-type elements: no per-element type id (XNA TargetIsValueType)
                return [self._read_object_index(elem_idx) for _ in range(count)]
            # reference-type elements carry a type id
            return [self._read_object() for _ in range(count)]
        if short == "RectangleReader":
            v = struct.unpack_from("<4i", self.data, self._o)
            self._o += 16
            return v
        if short == "CharReader":
            # CharReader calls ContentReader.ReadChar(); ContentReader is a
            # BinaryReader constructed with the DEFAULT UTF-8 encoding, so a char
            # is 1-4 bytes (variable) on the wire, NOT a fixed UTF-16 unit.
            b0 = self.data[self._o]
            if b0 < 0x80:
                n = 1
            elif b0 < 0xE0:
                n = 2
            elif b0 < 0xF0:
                n = 3
            else:
                n = 4
            raw = self.data[self._o:self._o + n]
            self._o += n
            return raw.decode("utf-8", "replace")
        if short == "Vector3Reader":
            v = struct.unpack_from("<3f", self.data, self._o)
            self._o += 12
            return v
        if short == "NullableReader`1":
            has = self.data[self._o]
            self._o += 1
            return self._read_object() if has else None
        raise XnbError("unsupported reader %r in %s" % (name, self.path))

    def _read_texture2d(self) -> Texture2D:
        fmt, w, h, mips = struct.unpack_from("<iIII", self.data, self._o)
        self._o += 16
        levels: List[Tuple[int, int, bytes]] = []
        cw, ch = w, h
        for _ in range(mips):
            (size,) = struct.unpack_from("<I", self.data, self._o)
            self._o += 4
            levels.append((cw, ch, self.data[self._o:self._o + size]))
            if len(levels[-1][2]) != size:
                raise XnbError("truncated mip in %s" % self.path)
            self._o += size
            cw = max(1, cw // 2)
            ch = max(1, ch // 2)
        if not levels:
            raise XnbError("texture with 0 mips in %s" % self.path)
        return Texture2D(fmt, levels[0][0], levels[0][1], levels)

    def _read_spritefont(self) -> SpriteFont:
        tex = self._read_object()  # embedded Texture2D
        glyphs = self._read_object()      # List<Rectangle>
        cropping = self._read_object()    # List<Rectangle>
        char_map = self._read_object()    # List<char>
        (line_spacing,) = struct.unpack_from("<i", self.data, self._o)
        self._o += 4
        (spacing,) = struct.unpack_from("<f", self.data, self._o)
        self._o += 4
        kerning = self._read_object()     # List<Vector3>
        # NOTE: XNA Game Studio 3.0 SpriteFontReader has NO defaultCharacter field
        # (it was added in XNA 4.0).  Payload must end exactly here.
        return SpriteFont(tex, glyphs, cropping, char_map, line_spacing, spacing,
                          kerning, None)


def parse(data: bytes, path: str = "<mem>") -> XnbFile:
    """Parse an XNB v2 buffer.  Char list elements are decoded UTF-8 (BinaryReader
    default encoding), so no width guessing is needed."""
    return XnbFile(data, path)


def decode_texture(tex: Texture2D, level: int = 0, *, dxt1_be: bool = True,
                   dxt5_be: bool = True, big_endian_index: bool = False,
                   raw_order: str = "argb") -> bytes:
    """Return RGBA8888 for one mip level.

    raw_order for SurfaceFormat.Color (Xbox-360 D3DFMT_A8R8G8B8):
      * 'argb' (default, CORRECT): big-endian 0xAARRGGBB -> file bytes A,R,G,B.
        The 360 is big-endian, so R is file byte 1, G byte 2, B byte 3 and the
        alpha is file byte 0.  Verified against the reference comic (neutral
        grey art + red "CAFE" sign), the XNA "DREAM BUILD PLAY" splash (orange
        "BUILD") and the control-guide art (cream, not blue).
      * 'bgra'/'rgba' preserved for A/B comparison only.
    """
    w, h, raw = tex.mips[level]
    if tex.format == 1:
        if raw_order == "argb":
            # file bytes are A,R,G,B (big-endian A8R8G8B8) -> RGBA
            out = bytearray(len(raw))
            out[0::4] = raw[1::4]  # R <- byte 1
            out[1::4] = raw[2::4]  # G <- byte 2
            out[2::4] = raw[3::4]  # B <- byte 3
            out[3::4] = raw[0::4]  # A <- byte 0
            return bytes(out)
        if raw_order == "bgra":
            out = bytearray(len(raw))
            out[0::4] = raw[2::4]
            out[1::4] = raw[1::4]
            out[2::4] = raw[0::4]
            out[3::4] = raw[3::4]
            return bytes(out)
        return bytes(raw)
    if tex.format == 28:
        return decode_dxt1(raw, w, h, be_endpoints=dxt1_be,
                           big_endian_index=big_endian_index)
    if tex.format == 32:
        return decode_dxt5(raw, w, h, be_endpoints=dxt5_be,
                           big_endian_index=big_endian_index)
    raise XnbError("unhandled surface format %d" % tex.format)


if __name__ == "__main__":
    import sys
    x = parse(open(sys.argv[1], "rb").read(), sys.argv[1])
    print("readers:", len(x.reader_names))
    for n, v in zip(x.reader_names, x.reader_versions):
        print("   %s  (v%d)" % (n, v))
    print("primary:", type(x.primary).__name__, "eof:", x.at_eof)
    if isinstance(x.primary, Texture2D):
        t = x.primary
        print("%dx%d fmt=%s mips=%d" % (t.width, t.height, t.format_name, len(t.mips)))
        for i, (mw, mh, raw) in enumerate(t.mips):
            print("   mip%d %dx%d %d bytes" % (i, mw, mh, len(raw)))
    elif isinstance(x.primary, SpriteFont):
        print(x.primary.info())
