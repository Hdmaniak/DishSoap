// PngWriter.cs -- PORT (public build): dependency-free RGBA8888 -> PNG encoder.
//
// Android has no System.Drawing/ImageSharp by default, so the public build needs
// its own encoder to turn decoded straight-alpha RGBA8888 into the `gfx/*.png`
// files DishwasherContentManager already knows how to load.  Filter 0 (None),
// colour type 6 (RGBA), no interlace; zlib wrapper via DeflateStream + Adler32.
using System;
using System.IO;
using System.IO.Compression;

namespace Dishwasher.Import
{
    public static class PngWriter
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            uint[] table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] data, int offset, int count)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < count; i++)
                c = CrcTable[(c ^ data[offset + i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        private static uint Adler32(byte[] data, int offset, int count)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < count; i++)
            {
                a = (a + data[offset + i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static void WriteBE32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            WriteBE32(s, (uint)(data == null ? 0 : data.Length));
            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes, 0, 4);
            if (data != null && data.Length > 0) s.Write(data, 0, data.Length);

            // CRC covers type + data
            byte[] crcInput = new byte[4 + (data == null ? 0 : data.Length)];
            Buffer.BlockCopy(typeBytes, 0, crcInput, 0, 4);
            if (data != null && data.Length > 0) Buffer.BlockCopy(data, 0, crcInput, 4, data.Length);
            WriteBE32(s, Crc32(crcInput, 0, crcInput.Length));
        }

        /// <summary>Write an RGBA8888 image as a PNG. `rgba` is width*height*4 bytes.</summary>
        public static void Write(Stream output, int width, int height, byte[] rgba)
        {
            if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
                throw new ArgumentException("bad RGBA buffer for PNG");

            // Filtered scanlines: filter byte 0 + RGBA row.
            int stride = width * 4;
            byte[] filtered = new byte[height * (1 + stride)];
            for (int y = 0; y < height; y++)
            {
                int dst = y * (1 + stride);
                filtered[dst] = 0;
                Buffer.BlockCopy(rgba, y * stride, filtered, dst + 1, stride);
            }

            output.Write(Signature, 0, Signature.Length);

            byte[] ihdr = new byte[13];
            ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16);
            ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
            ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16);
            ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
            ihdr[8] = 8;   // bit depth
            ihdr[9] = 6;   // colour type RGBA
            ihdr[10] = 0;  // compression
            ihdr[11] = 0;  // filter
            ihdr[12] = 0;  // interlace
            WriteChunk(output, "IHDR", ihdr);

            byte[] deflated;
            using (var ms = new MemoryStream())
            {
                using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, true))
                    deflate.Write(filtered, 0, filtered.Length);
                deflated = ms.ToArray();
            }

            byte[] idat = new byte[2 + deflated.Length + 4];
            idat[0] = 0x78; idat[1] = 0x9C; // zlib header (deflate, 32K window)
            Buffer.BlockCopy(deflated, 0, idat, 2, deflated.Length);
            uint adler = Adler32(filtered, 0, filtered.Length);
            int p = 2 + deflated.Length;
            idat[p] = (byte)(adler >> 24); idat[p + 1] = (byte)(adler >> 16);
            idat[p + 2] = (byte)(adler >> 8); idat[p + 3] = (byte)adler;
            WriteChunk(output, "IDAT", idat);

            WriteChunk(output, "IEND", null);
        }

        /// <summary>Convenience: encode straight to a file path.</summary>
        public static void WriteFile(string path, int width, int height, byte[] rgba)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                Write(fs, width, height, rgba);
        }
    }
}
