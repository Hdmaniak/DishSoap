// Desktop test harness for the public-build importer (not shipped).
// Validates the C# XNB v2 / STFS / manifest / texture / audio derivation code
// against the genuine retail package and the Python reference outputs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dishwasher.Import;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Portable defaults: set DISHWASHER_REPO_ROOT / DISHWASHER_ASSETS_DIR, or
        // pass the paths as arguments.  Nothing here is machine-specific.
        string repoRoot = Environment.GetEnvironmentVariable("DISHWASHER_REPO_ROOT")
            ?? Directory.GetCurrentDirectory();
        string assetsDir = Environment.GetEnvironmentVariable("DISHWASHER_ASSETS_DIR")
            ?? Path.Combine(repoRoot, "assets-clean");
        string package = args.Length > 0 ? args[0]
            : Path.Combine(assetsDir, "extracted", "58410902", "000D0000",
                "BF12F19032010E0A01D658FDBD4D49505327249858");
        string manifestPath = args.Length > 1 ? args[1]
            : Path.Combine(repoRoot, "src", "Dishwasher", "Assets", "import-manifest.json");
        string dest = args.Length > 2 ? args[2]
            : Path.Combine(Path.GetTempPath(), "dw-importer-test");
        string referenceMap = args.Length > 3 ? args[3]
            : Path.Combine(repoRoot, "src", "Dishwasher", "Assets", "sfx", "cue_map.json");

        // Native bridge for the host: build libdishaudio.so for x86_64 from
        // tools/xma-decoder/dishaudio.c and copy it next to this exe
        // (only needed to run the full audio derivation in this harness).
        Console.WriteLine("native probe: " + NativeXma.Probe() + " v" + NativeXma.Version
                          + (NativeXma.ProbeError == null ? "" : " (" + NativeXma.ProbeError + ")"));

        ImportManifest manifest;
        using (var fs = File.OpenRead(manifestPath))
            manifest = ImportManifest.Load(fs);
        Console.WriteLine("manifest: " + manifest.Files.Count + " required originals");

        IGameContentSource source;
        if (args.Length >= 6 && args[4] == "--local")
            source = ContentSourceFactory.FromLocalFile(args[5],
                Path.Combine(Path.GetTempPath(), "dw-importer-test"));
        else if (args.Length >= 6 && args[4] == "--folder")
            source = new DirectoryContentSource(args[5]);
        else
            source = new StfsContentSource(package);

        AudioImportResult audio;
        using (source)
        {
            ImportReport report = ContentImporter.Validate(source, manifest, null);
            Console.WriteLine("validate: " + report.Summary()
                              + " core_present=" + report.CorePresent);
            if (report.NothingUsable)
            {
                int i = 0;
                foreach (var m in report.MissingFiles) { if (i++ < 10) Console.WriteLine("  missing   " + m); }
                i = 0;
                foreach (var m in report.DifferedFiles) { if (i++ < 10) Console.WriteLine("  mismatch  " + m); }
                return 1;
            }

            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Console.WriteLine("deriving into " + dest + " ...");
            int last = -1;
            audio = ContentImporter.StageAndDerive(source, manifest, dest, p =>
            {
                int pct = p.Total > 0 ? (int)((long)p.Done * 100 / p.Total) : 0;
                if (pct / 10 != last / 10) { last = pct; Console.WriteLine("  " + p.Phase + " " + pct + "% " + p.Done + "/" + p.Total); }
            });
        }

        // Count outputs.
        int png = 0, json = 0, ogg = 0, wav = 0, other = 0;
        foreach (var f in Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories))
        {
            string e = Path.GetExtension(f).ToLowerInvariant();
            if (e == ".png") png++;
            else if (e == ".json") json++;
            else if (e == ".ogg") ogg++;
            else if (e == ".wav") wav++;
            else other++;
        }
        Console.WriteLine("derived: png=" + png + " json=" + json + " ogg=" + ogg + " wav=" + wav + " other=" + other);
        Console.WriteLine("audio  : decoded=" + audio.WavesDecoded + " failed=" + audio.WavesFailed
                          + " cues=" + audio.CueCount + " native=" + audio.NativeAvailable
                          + (string.IsNullOrEmpty(audio.Notes) ? "" : " notes=[" + audio.Notes + "]"));

        if (File.Exists(referenceMap))
        {
            bool ok = CompareCueMaps(Path.Combine(dest, "sfx", "cue_map.json"), referenceMap);
            Console.WriteLine(ok ? "CUE MAP MATCHES REFERENCE" : "CUE MAP DIFFERS");
            if (!ok) return 2;
        }
        Console.WriteLine("OK");
        return 0;
    }

    private static readonly string[] OggBanks = { "music", "solo_crux", "solo_dish", "solo_smash" };

    private static bool CompareCueMaps(string generated, string reference)
    {
        using var ga = JsonDocument.Parse(File.ReadAllText(generated));
        using var ra = JsonDocument.Parse(File.ReadAllText(reference));
        var gc = ga.RootElement.GetProperty("cues");
        var rc = ra.RootElement.GetProperty("cues");

        var gnames = gc.EnumerateObject().Select(p => p.Name).ToHashSet();
        var rnames = rc.EnumerateObject().Select(p => p.Name).ToHashSet();
        var missing = rnames.Except(gnames).ToList();
        var extra = gnames.Except(rnames).ToList();
        Console.WriteLine("cue names: generated=" + gnames.Count + " reference=" + rnames.Count
                          + " missing=" + missing.Count + " extra=" + extra.Count);
        bool ok = missing.Count == 0 && extra.Count == 0;

        int refDiff = 0, fileDiff = 0, metaDiff = 0;
        foreach (var rn in rnames.Intersect(gnames))
        {
            var g = gc.GetProperty(rn);
            var r = rc.GetProperty(rn);
            if (g.GetProperty("type").GetString() != r.GetProperty("type").GetString()) metaDiff++;
            if (g.GetProperty("category_id").GetInt32() != r.GetProperty("category_id").GetInt32()) metaDiff++;

            var gset = new HashSet<string>();
            foreach (var f in g.GetProperty("files").EnumerateArray())
                gset.Add(f.GetProperty("bank").GetString() + "/" + f.GetProperty("index").GetInt32());
            var rset = new HashSet<string>();
            foreach (var f in r.GetProperty("files").EnumerateArray())
                rset.Add(f.GetProperty("bank").GetString() + "/" + f.GetProperty("index").GetInt32());
            if (!gset.SetEquals(rset))
            {
                fileDiff++;
                if (fileDiff <= 5)
                    Console.WriteLine("  " + rn + " files differ: gen={" + string.Join(",", gset)
                                      + "} ref={" + string.Join(",", rset) + "}");
            }
        }
        Console.WriteLine("cue refs: fileDiff=" + fileDiff + " metaDiff=" + metaDiff);
        if (fileDiff != 0 || metaDiff != 0) ok = false;
        return ok;
    }
}
