// ContentImporter.cs -- PORT (public build): validate + stage + derive.
//
// Policy (v1.0 republish): TOLERANT by default.
//   1. Validate every manifest entry against the user's supplied source by size
//      and SHA-256.  A *mismatch* (different revision) or a *missing* file is a
//      WARNING, never a blocker: the file is used if present, skipped if not.
//   2. The import is only refused when there is *nothing usable* -- see
//      NothingUsable below.
//   3. Stage the original non-texture files verbatim (data/**, Resources/**,
//      gfx/maps/maps.zdx) into app-private files/content.
//   4. Derive gfx textures (XNB v2 -> straight-alpha PNG) and SpriteFont atlases
//      (+ .spritefont.json) so the unmodified game/content path finds them.
//
// Ownership gating can be re-enabled with PublicBuild.StrictContentValidation
// (build with -p:DishwasherStrictContent=true), which restores the old
// "any missing/mismatched file blocks the import" behaviour.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dishwasher.Import
{
    public sealed class ImportReport
    {
        public int Total;      // manifest entries checked
        public int Matched;    // present and byte-identical to the reference
        public int Differed;   // present but a different size/hash (different revision)
        public int Missing;    // absent from the supplied source

        public List<string> MissingFiles = new List<string>();
        public List<string> DifferedFiles = new List<string>();
        public List<string> Notes = new List<string>();

        /// <summary>Files present at all (matched + differed) -- i.e. usable content.</summary>
        public int Usable { get { return Matched + Differed; } }

        /// <summary>True only when every manifest entry matched exactly (strict mode).</summary>
        public bool AllOk { get { return Differed == 0 && Missing == 0; } }

        /// <summary>True when every file in the core boot set was found (matched or differed).</summary>
        public bool CorePresent;

        /// <summary>The "nothing usable" rule: refuse the import only when the
        /// selection contains no content we can derive, or when any of the core
        /// boot files (the ones the game needs to reach its menu) is absent.
        /// Individual missing/mismatched non-core files never block.</summary>
        public bool NothingUsable { get { return Usable == 0 || !CorePresent; } }

        public string Summary()
        {
            string s = Matched + " matched · " + Differed + " differed · " + Missing + " missing";
            if (Total > 0) s += " (of " + Total + ")";
            return s;
        }
    }

    public struct ImportProgress
    {
        public int Done;
        public int Total;
        public string Path;
        public string Phase;
    }

    public static class ContentImporter
    {
        /// <summary>Core boot set: the four original files the game needs to reach
        /// its menu (main script + primary texture + primary font + localized
        /// strings).  They must all be present for a selection to count as
        /// "usable"; they may differ from the reference (a different revision is
        /// still valid).  Audio and every other asset are optional.</summary>
        public static readonly string[] CoreAnchors =
        {
            "data/levels.zdx",
            "gfx/text.xnb",
            "gfx/Arials.xnb",
            "Resources/dishX.Resources.Strings.resources",
        };

        public static bool IsCore(string path)
        {
            for (int i = 0; i < CoreAnchors.Length; i++)
                if (string.Equals(CoreAnchors[i], path, StringComparison.Ordinal))
                    return true;
            return false;
        }

        public static ImportReport Validate(IGameContentSource src, ImportManifest manifest,
                                            Action<ImportProgress> progress)
        {
            var report = new ImportReport { Total = manifest.Files.Count };
            int done = 0;
            int coreFound = 0;
            foreach (var e in manifest.Files)
            {
                done++;
                if (progress != null)
                    progress(new ImportProgress { Done = done, Total = report.Total, Path = e.Path, Phase = "Checking" });

                Stream s = null;
                long len = -1;
                bool present = false;
                try { present = src.TryOpen(e.Path, out s, out len); }
                catch (Exception ex)
                {
                    report.Notes.Add("cannot open " + e.Path + ": " + ex.Message);
                    present = false;
                }

                if (!present)
                {
                    report.Missing++;
                    report.MissingFiles.Add(e.Path);
                    continue;
                }

                using (s)
                {
                    bool differs = false;
                    if (len >= 0 && len != e.Size) // -1 == source could not report a size
                    {
                        differs = true;
                        report.DifferedFiles.Add(e.Path + "  (size " + len + " != " + e.Size + ")");
                    }
                    else
                    {
                        string hash = Sha256Hex(s);
                        if (!string.Equals(hash, e.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            differs = true;
                            report.DifferedFiles.Add(e.Path + "  (sha256 mismatch)");
                        }
                    }
                    if (differs) report.Differed++;
                    else report.Matched++;
                }

                if (IsCore(e.Path)) coreFound++;
            }
            report.CorePresent = coreFound == CoreAnchors.Length;
            return report;
        }

        /// <summary>Stage and derive whatever is present.  Missing files are
        /// skipped and per-file failures are logged, never fatal: one bad asset
        /// must not abort the rest of the import.</summary>
        public static AudioImportResult StageAndDerive(IGameContentSource src, ImportManifest manifest,
                                          string destRoot, Action<ImportProgress> progress)
        {
            int done = 0;
            int staged = 0;
            int failed = 0;
            foreach (var e in manifest.Files)
            {
                done++;
                if (progress != null)
                    progress(new ImportProgress { Done = done, Total = manifest.Files.Count, Path = e.Path, Phase = "Importing" });

                // The original sfx banks are verified but never staged: their
                // playable audio is derived below and the originals stay only in
                // the user's own copy.
                if (e.Path.StartsWith("sfx/", StringComparison.OrdinalIgnoreCase))
                    continue;

                Stream s = null;
                long len = -1;
                bool present = false;
                try { present = src.TryOpen(e.Path, out s, out len); }
                catch (Exception ex) { global::Dishwasher.Log.Info("[import] cannot open " + e.Path + ": " + ex.Message); }
                if (!present)
                {
                    failed++;
                    global::Dishwasher.Log.Info("[import] skipping absent " + e.Path + " (not in the supplied copy)");
                    continue;
                }

                using (s)
                {
                    try
                    {
                        if (e.Path.StartsWith("gfx/", StringComparison.OrdinalIgnoreCase)
                            && e.Path.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
                        {
                            DeriveGfx(e.Path, s, destRoot);
                        }
                        else
                        {
                            CopyTo(Path.Combine(destRoot, e.Path.Replace('/', Path.DirectorySeparatorChar)), s);
                        }
                        staged++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        global::Dishwasher.Log.Error("[import] derive/copy failed for " + e.Path + ": " + ex.Message);
                    }
                }
            }

            // On-device audio derivation (XMA/XMA2 -> WAV/OGG + cue_map.json).
            // Must never abort the import: a missing decoder degrades to silence.
            try
            {
                var audio = AudioImporter.Derive(src, destRoot, 0, 0, progress);
                audio.Staged = staged;
                audio.StageFailed = failed;
                global::Dishwasher.Log.Info("[import] audio: " + audio.WavesDecoded + "/"
                    + (audio.WavesDecoded + audio.WavesFailed) + " waves derived ("
                    + audio.WavFiles + " wav, " + audio.OggFiles + " ogg), " + audio.CueCount
                    + " cues, native=" + audio.NativeAvailable
                    + (string.IsNullOrEmpty(audio.Notes) ? "" : " [" + audio.Notes + "]"));
                return audio;
            }
            catch (Exception ex)
            {
                global::Dishwasher.Log.Error("[import] audio derivation failed: " + ex.Message);
                return new AudioImportResult { Notes = ex.Message, Staged = staged, StageFailed = failed };
            }
        }

        private static void DeriveGfx(string relPath, Stream input, string destRoot)
        {
            byte[] data = ReadAll(input);
            XnbFile x = new XnbFile(data, relPath);
            string shortReader = XnbFile.ShortName(x.PrimaryReader ?? "");

            if (shortReader == "Texture2DReader" && x.Primary is XnbTexture tex)
            {
                byte[] rgba = XnbFile.DecodeTexture(tex, 0, true, true, false, true);
                string outRel = Path.ChangeExtension(relPath, ".png");
                PngWriter.WriteFile(Full(destRoot, outRel), tex.Width, tex.Height, rgba);
                return;
            }

            if (shortReader == "SpriteFontReader" && x.Primary is XnbSpriteFont sf)
            {
                string baseRel = relPath.Substring(0, relPath.Length - 4); // strip .xnb
                byte[] rgba = XnbFile.DecodeTexture(sf.Texture, 0, true, true, false, true);
                PngWriter.WriteFile(Full(destRoot, baseRel + "_atlas.png"),
                                    sf.Texture.Width, sf.Texture.Height, rgba);
                WriteSpriteFontJson(Full(destRoot, baseRel + ".spritefont.json"), sf);
                return;
            }

            throw new InvalidDataException("unsupported XNB reader '" + x.PrimaryReader + "' in " + relPath);
        }

        private static void WriteSpriteFontJson(string path, XnbSpriteFont sf)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var w = new Utf8JsonWriter(fs);

            w.WriteStartObject();

            w.WritePropertyName("glyph_rects");
            w.WriteStartArray();
            foreach (var r in sf.Glyphs) { w.WriteStartArray(); w.WriteNumberValue(r[0]); w.WriteNumberValue(r[1]); w.WriteNumberValue(r[2]); w.WriteNumberValue(r[3]); w.WriteEndArray(); }
            w.WriteEndArray();

            w.WritePropertyName("cropping_rects");
            w.WriteStartArray();
            foreach (var r in sf.Cropping) { w.WriteStartArray(); w.WriteNumberValue(r[0]); w.WriteNumberValue(r[1]); w.WriteNumberValue(r[2]); w.WriteNumberValue(r[3]); w.WriteEndArray(); }
            w.WriteEndArray();

            w.WritePropertyName("char_map");
            w.WriteStartArray();
            foreach (char c in sf.CharMap) w.WriteStringValue(c.ToString());
            w.WriteEndArray();

            w.WriteNumber("line_spacing", sf.LineSpacing);
            w.WriteNumber("spacing", sf.Spacing);

            w.WritePropertyName("kerning");
            w.WriteStartArray();
            foreach (var k in sf.Kerning) { w.WriteStartArray(); w.WriteNumberValue(k[0]); w.WriteNumberValue(k[1]); w.WriteNumberValue(k[2]); w.WriteEndArray(); }
            w.WriteEndArray();

            w.WriteNull("default_character");
            w.WriteEndObject();
            w.Flush();
        }

        private static string Full(string root, string rel)
        {
            return Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void CopyTo(string path, Stream input)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(fs, 1 << 20);
        }

        private static byte[] ReadAll(Stream s)
        {
            if (s is MemoryStream ms) return ms.ToArray();
            using var mem = new MemoryStream();
            s.CopyTo(mem, 1 << 20);
            return mem.ToArray();
        }

        public static string Sha256Hex(Stream s)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(s);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
