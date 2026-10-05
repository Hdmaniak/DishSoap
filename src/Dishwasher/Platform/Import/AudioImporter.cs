// AudioImporter.cs -- PORT (public build): on-device audio derivation.
//
// After the SHA-256 gate has verified the owner's original sfx/*.xwb + *.xsb,
// this reads the 360 XACT wave/sound banks, decodes every wave (XMA via
// libdishaudio.so; the 22 already-PCM entries byte-swapped), and writes the
// exact layout the runtime XactShim consumes:
//
//   sfx/wav/<bank>/<bank>_wNNN.wav   PCM16  (short SFX: waves, vox, halper)
//   sfx/ogg/<bank>/<bank>_wNNN.ogg   Vorbis (long/looping: music, solo_*)
//   sfx/cue_map.json                 cue -> file map (same shape as internal)
//
// Per-wave failures are logged and skipped; one bad wave never aborts the
// whole import.  The original .xwb/.xsb/.xgs are never copied anywhere.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Dishwasher.Import
{
    public sealed class AudioImportResult
    {
        public int WavesDecoded;
        public int WavesFailed;
        public int OggFiles;
        public int WavFiles;
        public int CueCount;
        public bool NativeAvailable;
        public string Notes = "";
        /// <summary>Non-audio originals successfully staged/derived (tolerant import).</summary>
        public int Staged;
        /// <summary>Files present in the source but skipped because derive/copy failed (warned, non-fatal).</summary>
        public int StageFailed;
    }

    public static class AudioImporter
    {
        // The seven banks.  NB: four sound banks are not named after their wave
        // banks (waves.xwb <-> sounds.xsb, vox.xwb <-> voxsnd.xsb, and so on).
        private struct BankInfo { public string Xwb; public string Xsb; }
        private static readonly BankInfo[] BankList =
        {
            new BankInfo { Xwb = "waves",     Xsb = "sounds"    },
            new BankInfo { Xwb = "vox",       Xsb = "voxsnd"    },
            new BankInfo { Xwb = "halper",    Xsb = "halpsnds"  },
            new BankInfo { Xwb = "music",     Xsb = "musicsnd"  },
            new BankInfo { Xwb = "solo_crux", Xsb = "solo_crux" },
            new BankInfo { Xwb = "solo_dish", Xsb = "solo_dish" },
            new BankInfo { Xwb = "solo_smash",Xsb = "solo_smash"},
        };

        private static readonly HashSet<string> OggBanks =
            new HashSet<string>(StringComparer.Ordinal)
            { "music", "solo_crux", "solo_dish", "solo_smash" };

        public static AudioImportResult Derive(IGameContentSource src, string destRoot,
                                               int baseDone, int baseTotal,
                                               Action<ImportProgress> progress)
        {
            var result = new AudioImportResult();
            result.NativeAvailable = NativeXma.Probe();
            if (!result.NativeAvailable)
                result.Notes += "libdishaudio unavailable (" + NativeXma.ProbeError + "); ";

            // Read every bank we need once; ~35 MB total.
            var bankBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var banks = new List<XwbFile>();
            foreach (var bi in BankList)
            {
                string rel = "sfx/" + bi.Xwb + ".xwb";
                if (!src.TryOpen(rel, out Stream s, out _)) continue;
                using (s)
                {
                    byte[] data = ReadAll(s);
                    bankBytes[bi.Xwb] = data;
                    try { banks.Add(XwbFile.Parse(data)); }
                    catch (Exception ex) { result.Notes += bi.Xwb + ".xwb parse failed: " + ex.Message + "; "; }
                }
            }

            int totalWaves = 0;
            foreach (var x in banks) totalWaves += x.Waves.Length;
            int waveDone = 0;

            foreach (var xwb in banks)
            {
                byte[] raw = bankBytes[xwb.BankName];
                bool ogg = OggBanks.Contains(xwb.BankName);
                for (int i = 0; i < xwb.Waves.Length; i++)
                {
                    waveDone++;
                    var w = xwb.Waves[i];
                    string sub = ogg ? "ogg" : "wav";
                    string ext = ogg ? ".ogg" : ".wav";
                    string rel = "sfx/" + sub + "/" + xwb.BankName + "/"
                               + xwb.BankName + "_w" + i.ToString("D3") + ext;
                    if (progress != null)
                        progress(new ImportProgress
                        {
                            Done = baseDone + waveDone,
                            Total = baseTotal + totalWaves,
                            Path = rel,
                            Phase = "Audio",
                        });

                    try
                    {
                        string full = Path.Combine(destRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(full));
                        byte[] wave = xwb.GetWaveData(i);
                        int rc;
                        if (w.Codec == 0) // raw big-endian PCM16
                            rc = NativeXma.Pcm16BeToWav(wave, w.Channels, w.SampleRate, full);
                        else if (w.Codec == 1) // XMA / XMA2
                            rc = NativeXma.XmaToFile(wave, w.Channels, w.SampleRate, w.NumSamples,
                                                     full, ogg ? 1 : 0);
                        else
                            rc = -200;

                        if (rc == 0)
                        {
                            result.WavesDecoded++;
                            if (ogg) result.OggFiles++; else result.WavFiles++;
                        }
                        else
                        {
                            result.WavesFailed++;
                            result.Notes += xwb.BankName + "_w" + i.ToString("D3")
                                          + " decode rc=" + rc + "; ";
                        }
                    }
                    catch (Exception ex)
                    {
                        result.WavesFailed++;
                        result.Notes += xwb.BankName + "_w" + i.ToString("D3") + " " + ex.Message + "; ";
                    }
                }
            }

            // ---- cue map -----------------------------------------------------
            var cues = new Dictionary<string, object>(StringComparer.Ordinal);
            var collisions = new List<string>();
            foreach (var bi in BankList)
            {
                string rel = "sfx/" + bi.Xsb + ".xsb";
                if (!src.TryOpen(rel, out Stream s, out _)) continue;
                XsbFile xsb;
                using (s) { xsb = XsbFile.Parse(ReadAll(s)); }

                foreach (var cue in xsb.Cues)
                {
                    var files = new List<object>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    string waveBank = xsb.WaveBanks.Length > 0 ? xsb.WaveBanks[0] : bi.Xwb;
                    foreach (var r in cue.Refs)
                    {
                        string wb = (r.WaveBank >= 0 && r.WaveBank < xsb.WaveBanks.Length)
                            ? xsb.WaveBanks[r.WaveBank] : waveBank;
                        string key = wb + "/" + r.Track;
                        if (!seen.Add(key)) continue;
                        bool loop = OggBanks.Contains(wb);
                        string kind = loop ? "ogg" : "wav";
                        string file = loop
                            ? "ogg/" + wb + "/" + wb + "_w" + r.Track.ToString("D3") + ".ogg"
                            : "wav/" + wb + "/" + wb + "_w" + r.Track.ToString("D3") + ".wav";
                        files.Add(new Dictionary<string, object>
                        {
                            { "bank", wb },
                            { "index", r.Track },
                            { "file", file },
                            { "kind", kind },
                            { "loop", loop },
                        });
                    }

                    if (cues.ContainsKey(cue.Name)) collisions.Add(cue.Name);
                    cues[cue.Name] = new Dictionary<string, object>
                    {
                        { "bank", xsb.BankName },
                        { "wave_bank", waveBank },
                        { "type", cue.Type },
                        { "category_id", cue.CategoryId },
                        { "files", files },
                    };
                }
            }

            result.CueCount = cues.Count;
            if (collisions.Count > 0)
                result.Notes += "cue name collisions: " + string.Join(",", collisions) + "; ";

            string mapPath = Path.Combine(destRoot, "sfx", "cue_map.json");
            Directory.CreateDirectory(Path.GetDirectoryName(mapPath));
            using (var fs = new FileStream(mapPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("source", "XACT XWB/XSB on-device derivation (XwbReader+XsbReader+libdishaudio)");
                w.WriteString("note", "files are relative to sfx/; .wav = PCM16, .ogg = Vorbis");
                w.WriteNumber("cue_count", cues.Count);
                w.WritePropertyName("cues");
                w.WriteStartObject();
                foreach (var kv in cues)
                {
                    w.WritePropertyName(kv.Key);
                    WriteValue(w, kv.Value);
                }
                w.WriteEndObject();
                w.WriteEndObject();
                w.Flush();
            }

            return result;
        }

        private static void WriteValue(Utf8JsonWriter w, object value)
        {
            switch (value)
            {
                case null:
                    w.WriteNullValue(); break;
                case string s:
                    w.WriteStringValue(s); break;
                case int i:
                    w.WriteNumberValue(i); break;
                case bool b:
                    w.WriteBooleanValue(b); break;
                case Dictionary<string, object> map:
                    w.WriteStartObject();
                    foreach (var kv in map) { w.WritePropertyName(kv.Key); WriteValue(w, kv.Value); }
                    w.WriteEndObject();
                    break;
                case IEnumerable<object> list:
                    w.WriteStartArray();
                    foreach (var item in list) WriteValue(w, item);
                    w.WriteEndArray();
                    break;
                default:
                    w.WriteStringValue(value.ToString()); break;
            }
        }

        private static byte[] ReadAll(Stream s)
        {
            if (s is MemoryStream ms) return ms.ToArray();
            using var mem = new MemoryStream();
            s.CopyTo(mem, 1 << 20);
            return mem.ToArray();
        }
    }
}
