// XnbV2.cs -- PORT (public build): faithful C# port of tools/xnb_v2.py.
//
// Decodes XNA Game Studio 3.0 / XNB container version 2 files shipped inside the
// Xbox 360 retail package so the PUBLIC build can derive its PNG textures and
// SpriteFont atlases on-device from files the user already owns.  Nothing here
// reads or writes game content itself; it is a pure decoder.
//
// Facts baked in (all measured; see android/notes/content-conversion.md):
//   * header = "XNB" + platform(1) + Int16 version (LE) + Int32 size (LE).
//     XNB v2 has NO flags byte -- the 0x00 seen in write-ups is the high byte of
//     the version short.
//   * all payload integers are little-endian, even for platform 'x' (Xbox 360).
//   * reader table = 7-bit count, then (7-bit strlen + utf8 name + Int32 ver)*.
//     then 7-bit shared-resource count; then primary object as 7-bit typeId
//     (0 == null, else readerIndex+1).
//   * List<T> of a VALUE type carries NO per-element type id (XNA
//     ContentReader.ReadObjectInternal checks TargetIsValueType); List<char>
//     elements are variable-length UTF-8 (BinaryReader default encoding).
//   * Texture2D: Int32 format, UInt32 w, UInt32 h, UInt32 mipCount, then per mip
//     UInt32 size + bytes.
//   * SurfaceFormat 1 = Color (raw 32bpp).  The Xbox 360 is BIG-endian, so
//     D3DFMT_A8R8G8B8 (0xAARRGGBB) is stored MSB-first as on-disk A,R,G,B.
//     DecodeTexture maps file byte 1->R, 2->G, 3->B, 0->A.  (The earlier
//     B,G,R,A little-endian decode produced a strong blue cast on the comic
//     panels and logos; see android/notes/comic-colour-fix.md.)  SurfaceFormat
//     28 = DXT1, 32 = DXT5; their colour endpoints are byte-swapped
//     (big-endian) RGB565 and the DXT5 alpha block is word-swapped too.
using System;
using System.Collections.Generic;
using System.Text;

namespace Dishwasher.Import
{
    public sealed class XnbException : Exception
    {
        public XnbException(string message) : base(message) { }
    }

    public struct XnbMip
    {
        public int Width;
        public int Height;
        public byte[] Data;
    }

    public sealed class XnbTexture
    {
        public int Format;
        public int Width;
        public int Height;
        public List<XnbMip> Mips = new List<XnbMip>();
    }

    public sealed class XnbSpriteFont
    {
        public XnbTexture Texture;
        public List<int[]> Glyphs;
        public List<int[]> Cropping;
        public List<char> CharMap;
        public int LineSpacing;
        public float Spacing;
        public List<float[]> Kerning;
    }

    public sealed class XnbFile
    {
        private static readonly HashSet<string> ValueTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Boolean", "Byte", "SByte", "Char", "Int16", "UInt16", "Int32", "UInt32",
            "Int64", "UInt64", "Single", "Double", "Decimal", "TimeSpan",
            "Vector2", "Vector3", "Vector4", "Matrix", "Quaternion", "Color", "Plane",
            "Ray", "Point", "Rectangle", "BoundingBox", "BoundingSphere",
            "BoundingFrustum", "Curve",
        };

        private byte[] _data;
        private int _o;

        public string Path;
        public string Magic;
        public int Platform;
        public int Version;
        public int DeclaredSize;
        public List<string> ReaderNames = new List<string>();
        public List<int> ReaderVersions = new List<int>();
        public int SharedResourceCount;
        public int PrimaryTypeId;
        public string PrimaryReader;
        public object Primary;

        public bool AtEof { get { return _o == _data.Length; } }

        public XnbFile(byte[] data, string path)
        {
            _data = data;
            Path = path;
            Parse();
        }

        public static XnbFile Parse(byte[] data, string path) { return new XnbFile(data, path); }

        // ---- primitives ----------------------------------------------------
        private int Read7BitUInt()
        {
            int value = 0;
            int shift = 0;
            while (true)
            {
                if (_o >= _data.Length)
                    throw new XnbException("unexpected EOF while reading 7-bit int in " + Path);
                byte b = _data[_o++];
                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return value;
                shift += 7;
                if (shift > 35)
                    throw new XnbException("7-bit int too long in " + Path);
            }
        }

        private string ReadCString()
        {
            int length = Read7BitUInt();
            if (_o + length > _data.Length)
                throw new XnbException("unexpected EOF while reading string in " + Path);
            string s = Encoding.UTF8.GetString(_data, _o, length);
            _o += length;
            return s;
        }

        private static int ReadI32(byte[] d, ref int o)
        {
            int v = d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
            o += 4;
            return v;
        }

        private static uint ReadU32(byte[] d, ref int o)
        {
            uint v = (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
            o += 4;
            return v;
        }

        private static float ReadF32(byte[] d, ref int o)
        {
            float v = BitConverter.ToSingle(d, o);
            o += 4;
            return v;
        }

        // ---- header / reader table ----------------------------------------
        private void Parse()
        {
            if (_data.Length < 10)
                throw new XnbException("file too small: " + Path);
            Magic = Encoding.ASCII.GetString(_data, 0, 3);
            if (Magic != "XNB")
                throw new XnbException(Path + ": bad magic");
            Platform = _data[3];
            Version = BitConverter.ToInt16(_data, 4);
            DeclaredSize = BitConverter.ToInt32(_data, 6);
            _o = 10;

            int nreaders = Read7BitUInt();
            for (int i = 0; i < nreaders; i++)
            {
                ReaderNames.Add(ReadCString());
                ReaderVersions.Add(ReadI32(_data, ref _o));
            }
            SharedResourceCount = Read7BitUInt();
            PrimaryTypeId = Read7BitUInt();
            if (PrimaryTypeId != 0)
            {
                PrimaryReader = ReaderNames[PrimaryTypeId - 1];
                Primary = ReadObjectIndex(PrimaryTypeId - 1);
            }
        }

        public static string ShortName(string name) { return Short(name); }

        private static string Short(string name)
        {
            int idx = name.IndexOf("[[", StringComparison.Ordinal);
            if (idx >= 0) name = name.Substring(0, idx);
            int dot = name.LastIndexOf('.');
            return dot >= 0 ? name.Substring(dot + 1) : name;
        }

        private static string ElementTypeName(string listReaderName)
        {
            int idx = listReaderName.IndexOf("[[", StringComparison.Ordinal);
            if (idx < 0) return null;
            string inner = listReaderName.Substring(idx + 2);
            int comma = inner.IndexOf(',');
            return (comma >= 0 ? inner.Substring(0, comma) : inner).Trim();
        }

        private int? ElementReaderIndex(string listReaderName)
        {
            string full = ElementTypeName(listReaderName);
            if (string.IsNullOrEmpty(full)) return null;
            int dot = full.LastIndexOf('.');
            string simple = dot >= 0 ? full.Substring(dot + 1) : full;
            string candidate = simple + "Reader";
            for (int i = 0; i < ReaderNames.Count; i++)
            {
                if (Short(ReaderNames[i]) == candidate) return i;
            }
            return null;
        }

        private object ReadObject()
        {
            int tid = Read7BitUInt();
            if (tid == 0) return null;
            return ReadObjectIndex(tid - 1);
        }

        private object ReadObjectIndex(int idx)
        {
            string name = ReaderNames[idx];
            string shortName = Short(name);
            switch (shortName)
            {
                case "Texture2DReader": return ReadTexture2D();
                case "SpriteFontReader": return ReadSpriteFont();
                case "RectangleReader":
                {
                    int x = ReadI32(_data, ref _o), y = ReadI32(_data, ref _o);
                    int w = ReadI32(_data, ref _o), h = ReadI32(_data, ref _o);
                    return new[] { x, y, w, h };
                }
                case "CharReader":
                {
                    byte b0 = _data[_o];
                    int n = b0 < 0x80 ? 1 : b0 < 0xE0 ? 2 : b0 < 0xF0 ? 3 : 4;
                    if (_o + n > _data.Length) throw new XnbException("truncated char in " + Path);
                    char c = Encoding.UTF8.GetString(_data, _o, n)[0];
                    _o += n;
                    return c;
                }
                case "Vector3Reader":
                {
                    float x = ReadF32(_data, ref _o), y = ReadF32(_data, ref _o), z = ReadF32(_data, ref _o);
                    return new[] { x, y, z };
                }
                case "NullableReader`1":
                {
                    bool has = _data[_o++] != 0;
                    return has ? ReadObject() : null;
                }
                case "ListReader`1":
                {
                    int count = (int)ReadU32(_data, ref _o);
                    string full = ElementTypeName(name);
                    int dot = full == null ? -1 : full.LastIndexOf('.');
                    string simple = full == null ? "" : (dot >= 0 ? full.Substring(dot + 1) : full);
                    int? elemIdx = ElementReaderIndex(name);
                    var list = new List<object>(count);
                    if (ValueTypes.Contains(simple) && elemIdx.HasValue)
                    {
                        for (int i = 0; i < count; i++)
                            list.Add(ReadObjectIndex(elemIdx.Value));
                    }
                    else
                    {
                        for (int i = 0; i < count; i++)
                            list.Add(ReadObject());
                    }
                    return list;
                }
                default:
                    throw new XnbException("unsupported reader '" + name + "' in " + Path);
            }
        }

        private XnbTexture ReadTexture2D()
        {
            XnbTexture t = new XnbTexture();
            t.Format = ReadI32(_data, ref _o);
            t.Width = (int)ReadU32(_data, ref _o);
            t.Height = (int)ReadU32(_data, ref _o);
            int mips = (int)ReadU32(_data, ref _o);
            int cw = t.Width, ch = t.Height;
            for (int i = 0; i < mips; i++)
            {
                uint size = ReadU32(_data, ref _o);
                if (_o + size > _data.Length)
                    throw new XnbException("truncated mip in " + Path);
                byte[] raw = new byte[size];
                Buffer.BlockCopy(_data, _o, raw, 0, (int)size);
                _o += (int)size;
                t.Mips.Add(new XnbMip { Width = cw, Height = ch, Data = raw });
                cw = Math.Max(1, cw / 2);
                ch = Math.Max(1, ch / 2);
            }
            if (t.Mips.Count == 0)
                throw new XnbException("texture with 0 mips in " + Path);
            return t;
        }

        private XnbSpriteFont ReadSpriteFont()
        {
            XnbSpriteFont sf = new XnbSpriteFont();
            sf.Texture = (XnbTexture)ReadObject();
            var glyphs = (List<object>)ReadObject();
            var cropping = (List<object>)ReadObject();
            var chars = (List<object>)ReadObject();
            sf.LineSpacing = ReadI32(_data, ref _o);
            sf.Spacing = ReadF32(_data, ref _o);
            var kerning = (List<object>)ReadObject();

            sf.Glyphs = new List<int[]>(glyphs.Count);
            foreach (var g in glyphs) sf.Glyphs.Add((int[])g);
            sf.Cropping = new List<int[]>(cropping.Count);
            foreach (var c in cropping) sf.Cropping.Add((int[])c);
            sf.CharMap = new List<char>(chars.Count);
            foreach (var c in chars) sf.CharMap.Add((char)c);
            sf.Kerning = new List<float[]>(kerning.Count);
            foreach (var k in kerning) sf.Kerning.Add((float[])k);
            return sf;
        }

        // ---- texture decode -------------------------------------------------
        private static byte[] Rgb565(int v)
        {
            int r = (v >> 11) & 0x1F, g = (v >> 5) & 0x3F, b = v & 0x1F;
            return new byte[]
            {
                (byte)((r << 3) | (r >> 2)),
                (byte)((g << 2) | (g >> 4)),
                (byte)((b << 3) | (b >> 2)),
            };
        }

        // Port of _decode_color_block: returns 16 (r,g,b,a) tuples packed as
        // r0,g0,b0,a0, r1,g1,b1,a1, ...
        private static void DecodeColorBlock(byte[] block, int off, bool beEndpoints,
                                             bool bigEndianIndex, byte[] outCols)
        {
            int c0, c1;
            if (beEndpoints)
            {
                c0 = (block[off] << 8) | block[off + 1];
                c1 = (block[off + 2] << 8) | block[off + 3];
            }
            else
            {
                c0 = block[off] | (block[off + 1] << 8);
                c1 = block[off + 2] | (block[off + 3] << 8);
            }
            uint idx;
            if (bigEndianIndex)
                idx = ((uint)block[off + 4] << 24) | ((uint)block[off + 5] << 16) |
                      ((uint)block[off + 6] << 8) | block[off + 7];
            else
                idx = (uint)(block[off + 4] | (block[off + 5] << 8) |
                             (block[off + 6] << 16) | (block[off + 7] << 24));

            byte[] a = Rgb565(c0), b = Rgb565(c1);
            byte[] cols = new byte[16]; // r,g,b,a x4
            // 0
            cols[0] = a[0]; cols[1] = a[1]; cols[2] = a[2]; cols[3] = 255;
            // 1
            cols[4] = b[0]; cols[5] = b[1]; cols[6] = b[2]; cols[7] = 255;
            if (c0 > c1)
            {
                cols[8] = (byte)((2 * a[0] + b[0]) / 3); cols[9] = (byte)((2 * a[1] + b[1]) / 3);
                cols[10] = (byte)((2 * a[2] + b[2]) / 3); cols[11] = 255;
                cols[12] = (byte)((a[0] + 2 * b[0]) / 3); cols[13] = (byte)((a[1] + 2 * b[1]) / 3);
                cols[14] = (byte)((a[2] + 2 * b[2]) / 3); cols[15] = 255;
            }
            else
            {
                cols[8] = (byte)((a[0] + b[0]) / 2); cols[9] = (byte)((a[1] + b[1]) / 2);
                cols[10] = (byte)((a[2] + b[2]) / 2); cols[11] = 255;
                cols[12] = 0; cols[13] = 0; cols[14] = 0; cols[15] = 0;
            }
            for (int i = 0; i < 16; i++)
            {
                int sel = (int)((idx >> (2 * i)) & 0x3);
                outCols[i * 4 + 0] = cols[sel * 4 + 0];
                outCols[i * 4 + 1] = cols[sel * 4 + 1];
                outCols[i * 4 + 2] = cols[sel * 4 + 2];
                outCols[i * 4 + 3] = cols[sel * 4 + 3];
            }
        }

        private static byte[] DecodeDxt1(byte[] data, int width, int height, bool beEndpoints,
                                         bool bigEndianIndex)
        {
            if (data.Length < (width * height) / 2)
                throw new XnbException("DXT1 payload too small for " + width + "x" + height);
            byte[] outBytes = new byte[width * height * 4];
            byte[] cols = new byte[64];
            int pos = 0;
            for (int by = 0; by < (height + 3) / 4; by++)
            {
                for (int bx = 0; bx < (width + 3) / 4; bx++)
                {
                    DecodeColorBlock(data, pos, beEndpoints, bigEndianIndex, cols);
                    pos += 8;
                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= height) continue;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= width) continue;
                            int p = py * 4 + px;
                            int o = (y * width + x) * 4;
                            outBytes[o] = cols[p * 4];
                            outBytes[o + 1] = cols[p * 4 + 1];
                            outBytes[o + 2] = cols[p * 4 + 2];
                            outBytes[o + 3] = cols[p * 4 + 3];
                        }
                    }
                }
            }
            return outBytes;
        }

        private static byte[] DecodeDxt5(byte[] data, int width, int height, bool beEndpoints,
                                         bool bigEndianIndex, bool beAlpha)
        {
            if (data.Length < width * height)
                throw new XnbException("DXT5 payload too small for " + width + "x" + height);
            byte[] outBytes = new byte[width * height * 4];
            byte[] cols = new byte[64];
            int pos = 0;
            for (int by = 0; by < (height + 3) / 4; by++)
            {
                for (int bx = 0; bx < (width + 3) / 4; bx++)
                {
                    int a0, a1;
                    long abits;
                    if (beAlpha)
                    {
                        // word-swap the 8-byte alpha block
                        a0 = data[pos + 1]; a1 = data[pos + 0];
                        abits = (long)data[pos + 3] | ((long)data[pos + 2] << 8) |
                                ((long)data[pos + 5] << 16) | ((long)data[pos + 4] << 24) |
                                ((long)data[pos + 7] << 32) | ((long)data[pos + 6] << 40);
                    }
                    else
                    {
                        a0 = data[pos + 0]; a1 = data[pos + 1];
                        abits = (long)data[pos + 2] | ((long)data[pos + 3] << 8) |
                                ((long)data[pos + 4] << 16) | ((long)data[pos + 5] << 24) |
                                ((long)data[pos + 6] << 32) | ((long)data[pos + 7] << 40);
                    }
                    int[] acols = new int[8];
                    acols[0] = a0; acols[1] = a1;
                    if (a0 > a1)
                    {
                        for (int i = 1; i <= 6; i++) acols[i + 1] = (a0 * (7 - i) + a1 * i) / 7;
                    }
                    else
                    {
                        for (int i = 1; i <= 4; i++) acols[i + 1] = (a0 * (5 - i) + a1 * i) / 5;
                        acols[6] = 0; acols[7] = 255;
                    }
                    DecodeColorBlock(data, pos + 8, beEndpoints, bigEndianIndex, cols);
                    pos += 16;
                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= height) continue;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= width) continue;
                            int p = py * 4 + px;
                            int alpha = acols[(int)((abits >> (3 * p)) & 0x7)];
                            int o = (y * width + x) * 4;
                            outBytes[o] = cols[p * 4];
                            outBytes[o + 1] = cols[p * 4 + 1];
                            outBytes[o + 2] = cols[p * 4 + 2];
                            outBytes[o + 3] = (byte)alpha;
                        }
                    }
                }
            }
            return outBytes;
        }

        /// <summary>Return RGBA8888 for one mip level. Port of xnb_v2.decode_texture
        /// (raw_order "argb", DXT endpoints big-endian).</summary>
        public static byte[] DecodeTexture(XnbTexture tex, int level, bool dxt1Be, bool dxt5Be,
                                           bool bigEndianIndex, bool beAlpha)
        {
            XnbMip mip = tex.Mips[level];
            int w = mip.Width, h = mip.Height;
            if (tex.Format == 1)
            {
                // Xbox-360 big-endian D3DFMT_A8R8G8B8: on-disk bytes are A,R,G,B.
                byte[] raw = mip.Data;
                byte[] o = new byte[raw.Length];
                for (int i = 0; i < raw.Length; i += 4)
                {
                    o[i + 0] = raw[i + 1]; // R <- byte 1
                    o[i + 1] = raw[i + 2]; // G <- byte 2
                    o[i + 2] = raw[i + 3]; // B <- byte 3
                    o[i + 3] = raw[i + 0]; // A <- byte 0
                }
                return o;
            }
            if (tex.Format == 28) return DecodeDxt1(mip.Data, w, h, dxt1Be, bigEndianIndex);
            if (tex.Format == 32) return DecodeDxt5(mip.Data, w, h, dxt5Be, bigEndianIndex, beAlpha);
            throw new XnbException("unhandled surface format " + tex.Format);
        }
    }
}
