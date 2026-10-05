#!/usr/bin/env python3
"""
parse_xsb.py -- XACT Sound Bank (.xsb) cue -> wave mapping extractor.

Targets XNA Game Studio 3.0 / XACT v42-43 Xbox 360 banks: BIG-ENDIAN with a
byte-swapped magic "KBDS" (= "SDBK" swapped). The field layout is ported from
MonoGame.Framework/Audio/Xact/SoundBank.cs + XactSound.cs + XactClip.cs, with
every multi-byte read switched to big-endian.

Usage:
    python3 parse_xsb.py FILE.xsb [FILE.xsb ...]     # human summary
    python3 parse_xsb.py --json OUT.json FILE.xsb ...
"""
import struct
import sys
import json
import os


class Reader:
    def __init__(self, data):
        self.d = data

    def u8(self, o):
        return self.d[o]

    def u16(self, o):
        return struct.unpack_from(">H", self.d, o)[0]

    def i16(self, o):
        return struct.unpack_from(">h", self.d, o)[0]

    def u32(self, o):
        return struct.unpack_from(">I", self.d, o)[0]

    def f32(self, o):
        return struct.unpack_from(">f", self.d, o)[0]

    def cstr64(self, o):
        raw = self.d[o:o + 64]
        return raw.split(b"\0", 1)[0].decode("latin-1")


class Xsb:
    """Parsed XSB. `cues` is a dict name -> list of wave refs."""
    def __init__(self, data, path):
        self.path = path
        self.data = data
        self.name = os.path.basename(path)
        self.r = Reader(data)
        r = self.r
        if data[0:4] != b"KBDS":
            raise ValueError("not a swapped XACT SDBK sound bank (magic %r)" % data[0:4])

        self.tool_version = r.u16(0x04)
        self.format_version = r.u16(0x06)
        self.crc = r.u16(0x08)
        self.platform = r.u8(0x12)
        self.num_simple_cues = r.u16(0x13)
        self.num_complex_cues = r.u16(0x15)
        self.num_total_cues = r.u16(0x19)
        self.num_wave_banks = r.u8(0x1B)
        self.num_sounds = r.u16(0x1C)
        self.cue_name_table_len = r.u32(0x1E)
        self.simple_cues_offset = r.u32(0x22)
        self.complex_cues_offset = r.u32(0x26)
        self.cue_names_offset = r.u32(0x2A)
        self.variation_tables_offset = r.u32(0x32)
        self.wave_bank_name_table_offset = r.u32(0x3A)
        self.sounds_offset = r.u32(0x46)

        # wave bank name table: num_wave_banks * 64 bytes
        self.wave_banks = []
        o = self.wave_bank_name_table_offset
        for _ in range(self.num_wave_banks):
            self.wave_banks.append(r.cstr64(o))
            o += 64

        # cue name table (length-delimited, NUL separated)
        raw = data[self.cue_names_offset:self.cue_names_offset + self.cue_name_table_len]
        self.cue_names = [s for s in raw.decode("latin-1").split("\0") if s != ""]

        # bank friendly name (64 bytes right after the 74-byte header)
        self.bank_name = r.cstr64(0x4A)

        self.cues = {}
        self._parse_simple_cues()
        self._parse_complex_cues()

    # ---- sound definition ------------------------------------------------
    def _parse_sound(self, off):
        """Return list of (wave_bank_index, track_index) plus metadata."""
        r = self.r
        flags = r.u8(off)
        complex_sound = (flags & 0x01) != 0
        has_rpcs = (flags & 0x0E) != 0
        has_dsps = (flags & 0x10) != 0
        p = off + 1
        category_id = r.u16(p); p += 2
        volume_db = r.u8(p); p += 1
        pitch_raw = r.i16(p); p += 2
        p += 1  # priority
        p += 2  # filter
        refs = []
        num_clips = 0
        if not complex_sound:
            track = r.u16(p); p += 2
            wb = r.u8(p); p += 1
            refs.append((wb, track))
        else:
            num_clips = r.u8(p); p += 1
        if has_rpcs:
            rpc_start = p
            data_len = r.u16(p)
            p = rpc_start + data_len   # MonoGame seeks to current + dataLength
        if has_dsps:
            p += 7
        if complex_sound:
            for _ in range(num_clips):
                ev_refs, p = self._parse_clip_at(p)
                refs.extend(ev_refs)
        return refs, {
            "complex": complex_sound,
            "category_id": category_id,
            "volume_dB": volume_db,
            "pitch_semitones": pitch_raw / 1000.0,
        }

    def _parse_clip_at(self, off):
        """Parse a clip; returns (refs, next_offset)."""
        r = self.r
        p = off
        # X360 clip record is 5 bytes: volumeDb u8 + clipOffset u32.
        # (The PC/XACT3 variant MonoGame reads adds 2+2 filter bytes -> 9 bytes.)
        p += 1  # volume dB
        clip_offset = r.u32(p); p += 4
        next_off = p
        # seek to event block (absolute)
        q = clip_offset
        num_events = r.u8(q); q += 1
        refs = []
        for _ in range(num_events):
            event_info = r.u32(q); q += 4
            q += 2  # randomOffset
            event_id = event_info & 0x1F
            if event_id in (1, 4):
                q += 1  # unknown
                q += 1  # event flags
                track = r.u16(q); q += 2
                wb = r.u8(q); q += 1
                refs.append((wb, track))
                q += 1  # loopCount
                q += 2  # panAngle
                q += 2  # panArc
                if event_id == 4:
                    q += 2 + 2  # min/max pitch
                    q += 1 + 1  # min/max volume
                    q += 4 * 4  # min/max freq + min/max Q (f32 x4)
                    q += 1 + 1  # unknown + variation flags
            elif event_id in (3, 6):
                q += 1  # unknown
                q += 1  # event flags
                q += 1  # loopCount
                q += 2  # panAngle
                q += 2  # panArc
                if event_id == 6:
                    q += 2 + 2 + 1 + 1 + 4 * 4 + 1 + 1
                num_tracks = r.u16(q); q += 2
                q += 1  # moreFlags
                q += 5  # unknown
                for _ in range(num_tracks):
                    track = r.u16(q); q += 2
                    wb = r.u8(q); q += 1
                    refs.append((wb, track))
                    q += 1 + 1  # min/max weight
            elif event_id == 8:
                q += 2 + 1
                q += 4
                q += 9
            elif event_id == 0:
                raise NotImplementedError("stop event")
            else:
                raise NotImplementedError("event id %d" % event_id)
        return refs, next_off

    # ---- cue tables ------------------------------------------------------
    def _parse_simple_cues(self):
        r = self.r
        o = self.simple_cues_offset
        for i in range(self.num_simple_cues):
            _flags = r.u8(o)
            sound_off = r.u32(o + 1)
            o += 5
            refs, meta = self._parse_sound(sound_off)
            name = self.cue_names[i] if i < len(self.cue_names) else "?%d" % i
            self.cues[name] = {"type": "simple", "waves": refs, **meta}

    def _parse_complex_cues(self):
        r = self.r
        o = self.complex_cues_offset
        for i in range(self.num_complex_cues):
            flags = r.u8(o); o += 1
            refs = []
            if ((flags >> 2) & 1) != 0:
                sound_off = r.u32(o); o += 4
                o += 4
                refs, meta = self._parse_sound(sound_off)
            else:
                var_off = r.u32(o); o += 4
                o += 4  # transition table
                refs, meta = self._parse_variation_table(var_off)
            o += 1 + 2 + 2 + 1  # instanceLimit, fadeIn, fadeOut, instanceFlags
            name = self.cue_names[self.num_simple_cues + i] \
                if (self.num_simple_cues + i) < len(self.cue_names) else "?c%d" % i
            self.cues[name] = {"type": "complex", "waves": refs, **meta}

    def _parse_variation_table(self, off):
        # X360 standalone variation table:
        #   u16 marker (0x000b) | u16 numEntries | u32 0xffffffff
        #   numEntries * { u32 soundOffset, u8 weightMin, u8 weightMax }
        # (The PC/XACT3 layout MonoGame reads packs numEntries/variationFlags
        #  and an 8-byte header with inline Wave entries; the 360 build instead
        #  always references sounds by absolute offset.)
        r = self.r
        p = off
        marker = r.u16(p); p += 2
        num_entries = r.u16(p); p += 2
        trailer = r.u32(p); p += 4
        refs = []
        for _ in range(num_entries):
            sound_off = r.u32(p); p += 4
            p += 1 + 1  # weightMin, weightMax
            r2, _ = self._parse_sound(sound_off)
            refs.extend(r2)
        return refs, {"complex": True, "variation_marker": marker,
                      "variation_trailer": trailer}

    def to_dict(self):
        return {
            "file": self.name,
            "bank_name": self.bank_name,
            "tool_version": self.tool_version,
            "format_version": self.format_version,
            "platform": self.platform,
            "num_wave_banks": self.num_wave_banks,
            "wave_banks": self.wave_banks,
            "num_simple_cues": self.num_simple_cues,
            "num_complex_cues": self.num_complex_cues,
            "num_total_cues": self.num_total_cues,
            "num_sounds": self.num_sounds,
            "cue_names": self.cue_names,
            "cues": self.cues,
        }


def main():
    args = sys.argv[1:]
    as_json = None
    if args and args[0] == "--json":
        as_json = args[1]
        args = args[2:]
    if not args:
        print(__doc__)
        return 1
    results = []
    for path in args:
        with open(path, "rb") as f:
            data = f.read()
        x = Xsb(data, path)
        results.append(x.to_dict())
        if as_json is None:
            print("=" * 70)
            print("%s  bank='%s' tool=%d fmt=%d platform=%d" %
                  (os.path.basename(path), x.bank_name, x.tool_version,
                   x.format_version, x.platform))
            print("  wave banks : %s" % ", ".join(x.wave_banks))
            print("  cues       : simple=%d complex=%d total=%d" %
                  (x.num_simple_cues, x.num_complex_cues, x.num_total_cues))
            # cue->wave lines
            for name, info in x.cues.items():
                waves = "; ".join("%s[%d]" % (x.wave_banks[wb] if wb < len(x.wave_banks) else "?%d" % wb, t)
                                  for wb, t in info["waves"])
                print("    %-22s -> %s" % (name, waves or "(no wave refs)"))
    if as_json:
        with open(as_json, "w") as f:
            json.dump(results, f, indent=2)
        print("wrote %s (%d banks)" % (as_json, len(results)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
