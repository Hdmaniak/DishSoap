// GameContentSource.cs -- PORT (public build): pluggable read-only view over
// whatever the user supplied -- a folder of extracted files, a zip of extracted
// files, or the retail XBLA .zip (a single STFS/LIVE package).  The importer
// only needs "give me the bytes for this normalized relative path".
//
// Paths are always forward-slash, relative to the content root, matching the
// layout of the manifest (data/..., gfx/....xnb, Resources/...).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Dishwasher.Import
{
    public interface IGameContentSource : IDisposable
    {
        bool TryOpen(string path, out Stream stream, out long length);
    }

    /// <summary>A plain on-disk directory tree (also used by desktop tests).</summary>
    public sealed class DirectoryContentSource : IGameContentSource
    {
        private readonly string _root;
        public DirectoryContentSource(string root) { _root = root; }

        public bool TryOpen(string path, out Stream stream, out long length)
        {
            stream = null;
            length = 0;
            string full = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return false;
            var fi = new FileInfo(full);
            length = fi.Length;
            stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }

        public void Dispose() { }
    }

    /// <summary>Extracted-content zip: entries are the original tree (paths may
    /// use backslashes, which we normalize).</summary>
    public sealed class ZipContentSource : IGameContentSource
    {
        private readonly ZipArchive _zip;
        private readonly FileStream _file;
        private readonly Dictionary<string, ZipArchiveEntry> _map =
            new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        public ZipContentSource(string zipPath)
        {
            _file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _zip = new ZipArchive(_file, ZipArchiveMode.Read);
            foreach (var e in _zip.Entries)
            {
                if (e.Length == 0 && string.IsNullOrEmpty(e.Name)) continue; // dirs
                string name = e.FullName.Replace('\\', '/').TrimStart('/');
                if (name.Length == 0) continue;
                _map[name] = e;
            }
        }

        public bool TryOpen(string path, out Stream stream, out long length)
        {
            stream = null;
            length = 0;
            if (!_map.TryGetValue(path, out var e)) return false;
            length = e.Length;
            stream = e.Open();
            return true;
        }

        public void Dispose()
        {
            _zip?.Dispose();
            _file?.Dispose();
        }
    }

    /// <summary>Retail STFS/LIVE package (a file on disk, e.g. unpacked from the
    /// XBLA .zip).  Entries are read through the block-chain reader.</summary>
    public sealed class StfsContentSource : IGameContentSource
    {
        private readonly FileStream _file;
        private readonly StfsReader _reader;
        private readonly Dictionary<string, StfsEntry> _map =
            new Dictionary<string, StfsEntry>(StringComparer.OrdinalIgnoreCase);

        public StfsContentSource(string stfsPath)
        {
            _file = new FileStream(stfsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _reader = new StfsReader(_file);
            foreach (var e in _reader.Entries)
            {
                if (e.IsDirectory) continue;
                _map[e.Path] = e;
            }
        }

        public bool TryOpen(string path, out Stream stream, out long length)
        {
            stream = null;
            length = 0;
            if (!_map.TryGetValue(path, out var e)) return false;
            byte[] data = _reader.ReadEntry(e);
            length = data.Length;
            stream = new MemoryStream(data, false);
            return true;
        }

        public void Dispose()
        {
            _file?.Dispose();
        }
    }
}
