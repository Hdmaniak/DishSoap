// PublicBuild.cs -- PORT (public build): the single switch + content gate.
//
// The SAME source tree builds two variants:
//   * internal (default): bundles all game assets and boots straight into the
//     game, exactly as before.
//   * public (-p:DishwasherPublic=true): bundles only our own work (the compiled
//     fx shaders + this import manifest).  On first run it must ask the owner to
//     supply their own copy of the game, check it against the SHA-256 manifest,
//     and derive the textures/fonts/audio on-device.
//
// Import validation is TOLERANT by default: a file that differs (a different
// game revision) or is missing is a warning, not a blocker.  The import is only
// refused when there is nothing usable to derive (see ContentImporter).  Set
// StrictContentValidation (or build with -p:DishwasherStrictContent=true) to
// restore the old ownership gate, where any missing/mismatched file blocks.
//
// IsPublic is a compile-time constant, so the internal build pays nothing and
// the import UI is not even reachable there.
using System;
using System.IO;
using Microsoft.Xna.Framework;

namespace Dishwasher
{
    public static class PublicBuild
    {
#if DISHWASHER_PUBLIC
        public const bool IsPublic = true;
#else
        public const bool IsPublic = false;
#endif

        /// <summary>
        /// Ownership gate switch.  Default false = tolerant: a mismatched or
        /// missing original file is a warning and the import continues with
        /// whatever is present (only "nothing usable" blocks).  Set true (or
        /// build with -p:DishwasherStrictContent=true) to require every
        /// manifest entry to match exactly, as the first v1.0 build did.
        /// </summary>
#if DISHWASHER_STRICT_CONTENT
        public static bool StrictContentValidation = true;
#else
        public static bool StrictContentValidation = false;
#endif

        /// <summary>App-private content root (files/content) set by the bootstrap.</summary>
        public static string ContentDir
        {
            get { return AndroidContentBootstrap.ContentRoot; }
        }

        public static string MarkerPath
        {
            get
            {
                if (string.IsNullOrEmpty(ContentDir)) return null;
                return Path.Combine(ContentDir, ".import-complete");
            }
        }

        /// <summary>True when the public build has a validated, derived content tree.</summary>
        public static bool ContentReady()
        {
            if (!IsPublic) return true;
            if (string.IsNullOrEmpty(ContentDir) || !Directory.Exists(ContentDir)) return false;
            string marker = MarkerPath;
            if (marker == null || !File.Exists(marker)) return false;
            // Cheap sanity check that the derivation really landed.  Audio is
            // deliberately NOT required: a missing audio bank is tolerated, so
            // a silent-but-playable import must still count as ready.
            string[] keys =
            {
                "data/levels.zdx",
                "gfx/text.png",
                "gfx/Arials_atlas.png",
                "gfx/Arials.spritefont.json",
                "Resources/dishX.Resources.Strings.resources",
            };
            foreach (string k in keys)
            {
                if (!File.Exists(Path.Combine(ContentDir, k.Replace('/', Path.DirectorySeparatorChar))))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Open a content-relative file, preferring the app-private content tree
        /// (staged originals + on-device derivations) and falling back to the APK
        /// assets (the internal build).  This is what lets the derived PNGs be
        /// seen by DishwasherContentManager, whose stock TitleContainer only ever
        /// reads APK assets.
        /// </summary>
        public static Stream OpenContent(string relativePath)
        {
            string root = ContentDir;
            if (!string.IsNullOrEmpty(root))
            {
                string p = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p))
                    return new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            return TitleContainer.OpenStream(relativePath);
        }

        public static Import.ImportManifest LoadManifest()
        {
            using Stream s = OpenContent("import-manifest.json");
            return Import.ImportManifest.Load(s);
        }

        /// <summary>Record that a full validate+derive pass completed, so later
        /// launches skip the import screen.</summary>
        public static void WriteCompletionMarker(int manifestVersion, int fileCount, string root)
        {
            string marker = MarkerPath;
            if (marker == null) return;
            using var w = new System.Text.Json.Utf8JsonWriter(
                new FileStream(marker, FileMode.Create, FileAccess.Write, FileShare.None));
            w.WriteStartObject();
            w.WriteNumber("manifest_version", manifestVersion);
            w.WriteNumber("files", fileCount);
            w.WriteString("root", root);
            w.WriteString("completed", DateTime.UtcNow.ToString("o"));
            w.WriteEndObject();
            w.Flush();
        }
    }
}
