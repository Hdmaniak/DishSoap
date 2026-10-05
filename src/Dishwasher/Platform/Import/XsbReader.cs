// XsbReader.cs -- PORT (public build): XACT sound-bank (.xsb) cue -> wave
// mapping for Xbox 360, byte-swapped magic "KBDS" (= "SDBK"), big-endian.
//
// Faithful port of <dev-notes>/audio/tools/parse_xsb.py, itself ported from
// MonoGame's SoundBank/XactSound/XactClip with the two 360-specific layout
// deviations the audio agent reverse-engineered:
//   1. complex-sound clip records are 5 bytes (u8 volumeDb, u32 clipOffset),
//      not the 9-byte PC record that also carries two filter fields;
//   2. standalone variation tables are
//        u16 marker(0x000b), u16 count, u32 trailer, count*(u32 soundOffset,
//        u8 weightMin, u8 weightMax)
//      rather than the PC inline-Wave layout.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dishwasher.Import
{
    public struct XsbRef
    {
        public int WaveBank;   // index into XsbFile.WaveBanks
        public int Track;      // wave index inside the wave bank
    }

    public sealed class XsbCue
    {
        public string Name;
        public string Type;       // "simple" | "complex"
        public int CategoryId = 1;
        public List<XsbRef> Refs = new List<XsbRef>();
    }

    public sealed class XsbFile
    {
        public string BankName;
        public string[] WaveBanks = Array.Empty<string>();
        public int NumSimple;
        public int NumComplex;
        public List<XsbCue> Cues = new List<XsbCue>();

        private byte[] _d;

        public static XsbFile Parse(byte[] data)
        {
            if (data.Length < 0x4A)
                throw new InvalidDataException("xsb too small");
            if (!(data[0] == 'K' && data[1] == 'B' && data[2] == 'D' && data[3] == 'S'))
                throw new InvalidDataException("not a 360 XACT sound bank (magic)");

            var f = new XsbFile { _d = data };
            f.ParseInternal();
            return f;
        }

        private void ParseInternal()
        {
            var f = this;
            var data = _d;
            f.NumSimple = U16(0x13);
            f.NumComplex = U16(0x15);
            int numWaveBanks = data[0x1B];
            long cueNameTableLen = U32(0x1E);
            long simpleCuesOff = U32(0x22);
            long complexCuesOff = U32(0x26);
            long cueNamesOff = U32(0x2A);
            long waveBankNameOff = U32(0x3A);

            // wave bank name table: numWaveBanks * 64-byte NUL-terminated names
            var wb = new string[numWaveBanks];
            for (int i = 0; i < numWaveBanks; i++)
                wb[i] = CStr64(waveBankNameOff + (long)i * 64);
            f.WaveBanks = wb;
            f.BankName = CStr64(0x4A);

            // cue name table: length-delimited, NUL-separated.
            var cueNames = new List<string>();
            {
                long start = cueNamesOff, end = Math.Min(data.Length, cueNamesOff + cueNameTableLen);
                if (start < end)
                {
                    string raw = Encoding.ASCII.GetString(data, (int)start, (int)(end - start));
                    foreach (var s in raw.Split('\0'))
                        if (s.Length > 0) cueNames.Add(s);
                }
            }

            // simple cues: 5-byte record (u8 flags, u32 soundOffset)
            for (int i = 0; i < f.NumSimple; i++)
            {
                long o = simpleCuesOff + (long)i * 5;
                uint soundOff = U32(o + 1);
                var cue = new XsbCue
                {
                    Name = i < cueNames.Count ? cueNames[i] : "?" + i,
                    Type = "simple",
                };
                ParseSound((long)soundOff, cue);
                f.Cues.Add(cue);
            }

            // complex cues
            long co = complexCuesOff;
            for (int i = 0; i < f.NumComplex; i++)
            {
                byte flags = _d[(int)co]; co += 1;
                var cue = new XsbCue { Type = "complex" };
                if (((flags >> 2) & 1) != 0)
                {
                    uint soundOff = U32(co); co += 4;
                    co += 4;                     // second u32 (unused here)
                    ParseSound((long)soundOff, cue);
                }
                else
                {
                    uint varOff = U32(co); co += 4;
                    co += 4;                     // transition table (unused)
                    ParseVariationTable((long)varOff, cue);
                }
                co += 1 + 2 + 2 + 1;             // instanceLimit, fadeIn, fadeOut, instanceFlags
                int nameIdx = f.NumSimple + i;
                cue.Name = nameIdx < cueNames.Count ? cueNames[nameIdx] : "?c" + i;
                f.Cues.Add(cue);
            }
        }

        // sound definition at absolute offset
        private void ParseSound(long off, XsbCue cue)
        {
            byte flags = _d[(int)off];
            bool complexSound = (flags & 0x01) != 0;
            bool hasRpcs = (flags & 0x0E) != 0;
            bool hasDsps = (flags & 0x10) != 0;
            long p = off + 1;
            cue.CategoryId = U16(p); p += 2;
            p += 1;                 // volume dB
            p += 2;                 // pitch (i16)
            p += 1;                 // priority
            p += 2;                 // filter

            int numClips = 0;
            if (!complexSound)
            {
                int track = U16(p); p += 2;
                int wb = _d[(int)p]; p += 1;
                cue.Refs.Add(new XsbRef { WaveBank = wb, Track = track });
            }
            else
            {
                numClips = _d[(int)p]; p += 1;
            }

            if (hasRpcs)
            {
                long rpcStart = p;
                int dataLen = U16(p);
                p = rpcStart + dataLen;   // MonoGame seeks current + dataLength
            }
            if (hasDsps) p += 7;

            if (complexSound)
            {
                for (int i = 0; i < numClips; i++)
                    p = ParseClip(p, cue);
            }
        }

        // returns next clip offset; appends refs to cue
        private long ParseClip(long off, XsbCue cue)
        {
            long p = off;
            p += 1;                              // volume dB
            long clipOffset = U32(p); p += 4;
            long nextOff = p;

            long q = clipOffset;
            int numEvents = _d[(int)q]; q += 1;
            for (int e = 0; e < numEvents; e++)
            {
                uint eventInfo = U32(q); q += 4;
                q += 2;                          // randomOffset
                int eventId = (int)(eventInfo & 0x1F);
                if (eventId == 1 || eventId == 4)
                {
                    q += 1;                      // unknown
                    q += 1;                      // event flags
                    int track = U16(q); q += 2;
                    int wb = _d[(int)q]; q += 1;
                    cue.Refs.Add(new XsbRef { WaveBank = wb, Track = track });
                    q += 1;                      // loop count
                    q += 2;                      // pan angle
                    q += 2;                      // pan arc
                    if (eventId == 4)
                    {
                        q += 2 + 2;              // min/max pitch
                        q += 1 + 1;              // min/max volume
                        q += 4 * 4;              // min/max freq + min/max Q (f32 x4)
                        q += 1 + 1;              // unknown + variation flags
                    }
                }
                else if (eventId == 3 || eventId == 6)
                {
                    q += 1;                      // unknown
                    q += 1;                      // event flags
                    q += 1;                      // loop count
                    q += 2;                      // pan angle
                    q += 2;                      // pan arc
                    if (eventId == 6)
                        q += 2 + 2 + 1 + 1 + 4 * 4 + 1 + 1;
                    int numTracks = U16(q); q += 2;
                    q += 1;                      // more flags
                    q += 5;                      // unknown
                    for (int t = 0; t < numTracks; t++)
                    {
                        int track = U16(q); q += 2;
                        int wb = _d[(int)q]; q += 1;
                        cue.Refs.Add(new XsbRef { WaveBank = wb, Track = track });
                        q += 1 + 1;              // min/max weight
                    }
                }
                else if (eventId == 8)
                {
                    q += 2 + 1;
                    q += 4;
                    q += 9;
                }
                else if (eventId == 0)
                {
                    throw new InvalidDataException("xsb stop event not supported");
                }
                else
                {
                    throw new InvalidDataException("xsb event id " + eventId + " not supported");
                }
            }
            return nextOff;
        }

        private void ParseVariationTable(long off, XsbCue cue)
        {
            long p = off;
            p += 2;                              // marker (0x000b)
            int numEntries = U16(p); p += 2;
            p += 4;                              // trailer (0xffffffff)
            for (int i = 0; i < numEntries; i++)
            {
                uint soundOff = U32(p); p += 4;
                p += 1 + 1;                      // weightMin, weightMax
                ParseSound((long)soundOff, cue);
            }
        }

        private void Need(long o, int n)
        {
            if (o < 0 || o + n > _d.Length)
                throw new InvalidDataException("xsb read past end");
        }

        private ushort U16(long o) { Need(o, 2); return BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(_d, (int)o, 2)); }
        private uint U32(long o) { Need(o, 4); return BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(_d, (int)o, 4)); }

        private string CStr64(long o)
        {
            Need(o, 1);
            int end = (int)o;
            int limit = (int)Math.Min(_d.Length, o + 64);
            while (end < limit && _d[end] != 0) end++;
            return Encoding.ASCII.GetString(_d, (int)o, end - (int)o);
        }
    }
}
