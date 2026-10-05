// XwbReader.cs -- PORT (public build): minimal reader for Xbox 360 XACT wave
// banks (.xwb).  Big-endian, byte-swapped magic "DNBW" (= "WBND").
//
// Ported from <dev-notes>/audio/tools/parse_xwb.py + vgmstream's
// src/meta/xwb.c.  Only what the audio derivation needs: the bank name and,
// per entry, the codec / channels / sample-rate / sample-count and the byte
// range of the wave data in the ENTRYWAVEDATA segment.
//
//   v42+ entry element (24 bytes, big-endian):
//     u32 entryInfo  -> flags = &0xF, numSamples = (info>>4)&0x0FFFFFFF
//     u32 format     -> codec = &3, channels = (>>2)&7, rate = (>>5)&0x3FFFF,
//                       blockAlign = (>>23)&0xFF
//     u32 fileOffset (relative to the wave-data segment)
//     u32 fileLength
//     u32 loopStartSample, u32 loopEndSample
using System;
using System.Buffers.Binary;
using System.IO;

namespace Dishwasher.Import
{
    public sealed class XwbWave
    {
        public int Index;
        public int Codec;        // 0 PCM, 1 XMA, 2 ADPCM
        public int Channels;
        public int SampleRate;
        public long NumSamples;
        public int Offset;       // within the wave-data segment
        public int Length;
    }

    public sealed class XwbFile
    {
        public string BankName;
        public int ToolVersion;
        public int HeaderVersion;
        public XwbWave[] Waves = Array.Empty<XwbWave>();

        private byte[] _data;
        private int _waveDataOffset;

        public static XwbFile Parse(byte[] data)
        {
            if (data.Length < 0x34)
                throw new InvalidDataException("xwb too small");
            // magic is "WBND" byte-swapped -> "DNBW"
            if (!(data[0] == 'D' && data[1] == 'N' && data[2] == 'B' && data[3] == 'W'))
                throw new InvalidDataException("not a 360 XACT wave bank (magic)");

            var f = new XwbFile { _data = data };
            f.ToolVersion = (int)U32(data, 0x04);
            f.HeaderVersion = (int)U32(data, 0x08);

            // 5 segments: BANK_DATA, ENTRY_METADATA, SEEK_TABLES, ENTRY_NAMES, WAVE_DATA
            long[] segOff = new long[5], segLen = new long[5];
            for (int i = 0; i < 5; i++)
            {
                segOff[i] = U32(data, 0x0C + i * 8);
                segLen[i] = U32(data, 0x10 + i * 8);
            }

            long bd = segOff[0];
            long entryCount = U32(data, bd + 4);
            f.BankName = CStr(data, bd + 8, 64);
            long entryMetaSize = U32(data, bd + 72);
            // entry name size at +76, alignment at +80 (not needed here)
            long entryOff = segOff[1];
            long waveOff = segOff[4];
            f._waveDataOffset = (int)waveOff;

            if (entryMetaSize < 16)
                throw new InvalidDataException("unsupported xwb entry metadata size " + entryMetaSize);

            var waves = new XwbWave[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                long o = entryOff + (long)i * entryMetaSize;
                uint info = U32(data, o);
                uint fmt = U32(data, o + 4);
                long fileOffset = U32(data, o + 8);
                long fileLength = U32(data, o + 12);

                waves[i] = new XwbWave
                {
                    Index = i,
                    Codec = (int)(fmt & 0x3),
                    Channels = (int)((fmt >> 2) & 0x7),
                    SampleRate = (int)((fmt >> 5) & 0x3FFFF),
                    NumSamples = (info >> 4) & 0x0FFFFFFF,
                    Offset = (int)fileOffset,
                    Length = (int)fileLength,
                };
            }
            f.Waves = waves;
            // Sanity: the wave-data segment must cover every entry.
            for (int i = 0; i < waves.Length; i++)
            {
                var w = waves[i];
                if (w.Length <= 0) continue;
                if ((long)waveOff + w.Offset + w.Length > data.Length)
                    throw new InvalidDataException("xwb wave " + i + " runs past EOF");
            }
            return f;
        }

        /// <summary>Copies the raw bytes of wave <paramref name="index"/> into a new array.</summary>
        public byte[] GetWaveData(int index)
        {
            var w = Waves[index];
            var outBytes = new byte[w.Length];
            Buffer.BlockCopy(_data, _waveDataOffset + w.Offset, outBytes, 0, w.Length);
            return outBytes;
        }

        private static uint U32(byte[] d, long o) =>
            BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(d, (int)o, 4));

        private static string CStr(byte[] d, long o, int max)
        {
            int end = (int)o;
            int limit = (int)(o + max);
            while (end < limit && end < d.Length && d[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(d, (int)o, end - (int)o);
        }
    }
}
