// ContentSourceFactory.cs -- PORT (public build): decide what the user handed us.
//
// Accepts three shapes:
//   * a folder tree (handled by the Android SAF source) of extracted files;
//   * a zip of the extracted content tree;
//   * the retail XBLA .zip, whose single entry is an STFS/LIVE package.
// `FromLocalFile` sniffs a local file (we copy content:// streams to app cache
// first) and returns the right IGameContentSource.
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Dishwasher.Import
{
    public static class ContentSourceFactory
    {
        public static IGameContentSource FromLocalFile(string path, string tempDir)
        {
            byte[] head = new byte[4];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int n = fs.Read(head, 0, 4);
                if (n < 4) throw new InvalidDataException("file too small: " + path);
                fs.Seek(0, SeekOrigin.Begin);
            }

            string magic = Encoding.ASCII.GetString(head, 0, 4);
            if (magic == "LIVE" || magic == "CON " || magic == "PIRS")
                return new StfsContentSource(path);

            if (head[0] == 'P' && head[1] == 'K')
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    ZipArchiveEntry only = null;
                    int fileCount = 0;
                    foreach (var e in zip.Entries)
                    {
                        if (e.Length == 0 && string.IsNullOrEmpty(e.Name)) continue;
                        fileCount++;
                        only = e;
                    }
                    if (fileCount == 1 && IsStfsEntry(only))
                    {
                        Directory.CreateDirectory(tempDir);
                        string stfs = Path.Combine(tempDir, "package.stfs");
                        using (var src = only.Open())
                        using (var dst = new FileStream(stfs, FileMode.Create, FileAccess.Write, FileShare.None))
                            src.CopyTo(dst, 1 << 20);
                        return new StfsContentSource(stfs);
                    }
                }
                return new ZipContentSource(path);
            }

            throw new InvalidDataException(
                "Unrecognized file. Supply the retail .zip (an XBLA package) or a .zip / folder of extracted files.");
        }

        private static bool IsStfsEntry(ZipArchiveEntry entry)
        {
            try
            {
                using var s = entry.Open();
                byte[] b = new byte[4];
                int n = 0;
                while (n < 4)
                {
                    int r = s.Read(b, n, 4 - n);
                    if (r <= 0) return false;
                    n += r;
                }
                string m = Encoding.ASCII.GetString(b);
                return m == "LIVE" || m == "CON " || m == "PIRS";
            }
            catch
            {
                return false;
            }
        }
    }
}
