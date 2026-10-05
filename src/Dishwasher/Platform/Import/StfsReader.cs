// StfsReader.cs -- PORT (public build): read-only STFS/LIVE ("XContent")
// package extractor, ported from the validated reference
// tools/stfs_extract.py (itself derived from Xenia/ReXGlue's
// stfs_container_device.cpp).  It lets the public build accept the retail
// XBLA .zip directly: the zip contains one LIVE package, we unpack that to a
// cache file and enumerate its entries here.
//
// Format summary:
//   * XContentHeader (0x344) + XContentMetadata; header_size at 0x340.
//   * STFS volume descriptor at 0x379 (len 0x24): read_only_format bit, a
//     little-endian file_table_block_count, a uint24-LE file_table_block_number.
//   * data is 0x1000-byte blocks; every 170 data blocks are followed by
//     `blocks_per_hash_table` (1 read-only else 2) 0x1000 hash blocks.
//     The level-0 hash table entry for a block stores the next block of a file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dishwasher.Import
{
    public sealed class StfsEntry
    {
        public string Name;
        public string Path;
        public bool IsDirectory;
        public uint Length;
        public uint StartBlock;
        public int DirIndex;
    }

    public sealed class StfsReader : IDisposable
    {
        private const int BlockSize = 0x1000;
        private const uint EndOfChain = 0xFFFFFF;
        private static readonly int[] BlocksPerHashLevel = { 170, 170 * 170, 170 * 170 * 170 };

        private readonly Stream _fs;
        private readonly long _base;
        private readonly int _blocksPerHashTable;
        private readonly int[] _blockStep = new int[2];
        private readonly ushort _fileTableBlockCount;
        private readonly uint _fileTableBlockNumber;

        public uint TotalBlockCount { get; private set; }
        public List<StfsEntry> Entries { get; private set; } = new List<StfsEntry>();

        public StfsReader(Stream seekable)
        {
            if (!seekable.CanSeek) throw new ArgumentException("STFS reader needs a seekable stream");
            _fs = seekable;

            byte[] header = ReadAt(0, 0x344);
            string magic = Encoding.ASCII.GetString(header, 0, 4);
            if (magic != "LIVE" && magic != "CON " && magic != "PIRS")
                throw new InvalidDataException("not an STFS/LIVE package (magic '" + magic + "')");

            uint headerSize = ReadBE32(header, 0x340);
            _base = RoundUp(headerSize, BlockSize);

            byte[] vd = ReadAt(0x379, 0x24);
            if (vd[0] != 0x24)
                throw new InvalidDataException("unexpected STFS volume descriptor length " + vd[0]);
            bool readOnly = (vd[2] & 1) != 0;
            _fileTableBlockCount = (ushort)(vd[3] | (vd[4] << 8)); // little-endian on disk
            _fileTableBlockNumber = U24(vd, 5);
            TotalBlockCount = ReadBE32(ReadAt(0x395, 4), 0);

            _blocksPerHashTable = readOnly ? 1 : 2;
            _blockStep[0] = BlocksPerHashLevel[0] + _blocksPerHashTable;
            _blockStep[1] = BlocksPerHashLevel[1] + (BlocksPerHashLevel[0] + 1) * _blocksPerHashTable;

            ReadDirectory();
        }

        // ---- block math -----------------------------------------------------
        private long BlockToOffset(uint blockIndex)
        {
            long basev = BlocksPerHashLevel[0];
            long block = blockIndex;
            for (int i = 0; i < 3; i++)
            {
                block += ((blockIndex + basev) / basev) * _blocksPerHashTable;
                if (blockIndex < basev) break;
                basev *= BlocksPerHashLevel[0];
            }
            return _base + (block << 12);
        }

        private long HashBlockNumber(uint blockIndex, int level)
        {
            if (level == 0)
            {
                if (blockIndex < 170) return 0;
                long block = (blockIndex / 170) * _blockStep[0];
                block += ((blockIndex / 28900) + 1) * _blocksPerHashTable;
                if (blockIndex < 28900) return block;
                return block + _blocksPerHashTable;
            }
            if (level == 1)
            {
                if (blockIndex < 28900) return _blockStep[0];
                long block = (blockIndex / 28900) * _blockStep[1];
                return block + _blocksPerHashTable;
            }
            return _blockStep[1];
        }

        private uint NextBlock(uint blockIndex)
        {
            long hb = HashBlockNumber(blockIndex, 0);
            byte[] table = ReadAt(_base + (hb << 12), BlockSize);
            int rec = (int)(blockIndex % 170);
            uint info = ReadBE32(table, rec * 0x18 + 0x14);
            return info & 0xFFFFFF;
        }

        // ---- directory ------------------------------------------------------
        private void ReadDirectory()
        {
            var list = new List<StfsEntry>();
            uint tableBlock = _fileTableBlockNumber;
            for (int n = 0; n < _fileTableBlockCount && tableBlock != EndOfChain; n++)
            {
                byte[] block = ReadAt(BlockToOffset(tableBlock), BlockSize);
                for (int m = 0; m < 0x40; m++)
                {
                    int off = m * 0x40;
                    if (block[off] == 0) break;
                    byte flags = block[off + 40];
                    int nameLen = flags & 0x3F;
                    bool isDir = ((flags >> 7) & 1) != 0;
                    string name = Encoding.ASCII.GetString(block, off, Math.Min(nameLen, 40));
                    uint length = ReadBE32(block, off + 52);
                    uint startBlock = U24(block, off + 47);
                    int dirIndex = (block[off + 50] << 8) | block[off + 51];
                    list.Add(new StfsEntry
                    {
                        Name = name,
                        IsDirectory = isDir,
                        Length = length,
                        StartBlock = startBlock,
                        DirIndex = dirIndex,
                    });
                }
                tableBlock = NextBlock(tableBlock);
            }

            // Resolve the flat directory_index tree into full relative paths
            // (same order as the entries, exactly like the reference tool).
            for (int i = 0; i < list.Count; i++)
            {
                StfsEntry e = list[i];
                string prefix = "";
                int parent = e.DirIndex;
                if (parent != 0xFFFF && parent >= 0 && parent < i)
                    prefix = list[parent].Path + "/";
                e.Path = prefix + e.Name;
            }
            Entries = list;
        }

        public byte[] ReadEntry(StfsEntry e)
        {
            uint blockIndex = e.StartBlock;
            long remaining = e.Length;
            using (var ms = new MemoryStream((int)e.Length))
            {
                while (remaining > 0 && blockIndex != EndOfChain)
                {
                    int n = (int)Math.Min((long)BlockSize, remaining);
                    byte[] chunk = ReadAt(BlockToOffset(blockIndex), n);
                    ms.Write(chunk, 0, n);
                    remaining -= n;
                    blockIndex = NextBlock(blockIndex);
                }
                return ms.ToArray();
            }
        }

        // ---- raw helpers ----------------------------------------------------
        private byte[] ReadAt(long offset, int count)
        {
            _fs.Seek(offset, SeekOrigin.Begin);
            byte[] buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = _fs.Read(buf, read, count - read);
                if (n <= 0) throw new EndOfStreamException("STFS: unexpected EOF at " + offset);
                read += n;
            }
            return buf;
        }

        private static long RoundUp(long v, long m) { return (v + m - 1) / m * m; }

        private static uint U24(byte[] b, int off)
        {
            return (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16));
        }

        private static uint ReadBE32(byte[] b, int off)
        {
            return ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) |
                   ((uint)b[off + 2] << 8) | b[off + 3];
        }

        public void Dispose()
        {
            // The stream is owned by the caller.
        }
    }
}
