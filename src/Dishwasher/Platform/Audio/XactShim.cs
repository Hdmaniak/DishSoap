// XactShim.cs -- drop-in XACT-shaped audio layer for the MonoGame/Android port
// of "The Dishwasher: Dead Samurai".
//
// WHY A SHIM (and why this namespace):
//   MonoGame.Framework.Android 3.8.5.1 *does* ship Microsoft.Xna.Framework.Audio
//   { AudioEngine, WaveBank, SoundBank, Cue, AudioCategory, AudioStopOptions, ... }.
//   Those classes parse the XACT .xgs/.xwb/.xsb formats, but this game's banks are
//   the BIG-ENDIAN Xbox 360 variants and the audio is Xbox 360 XMA, neither of which
//   MonoGame's desktop-oriented parser can read.  We therefore re-implement the same
//   API surface over MonoGame SoundEffect/SoundEffectInstance, driven by a cue->file
//   map extracted offline from the .xsb/.xwb files.
//
//   Because the type NAMES already exist in Microsoft.Xna.Framework.Audio, this file
//   must NOT live in that namespace (it would be a duplicate-type/CS0433 error).
//   It lives in `Dishwasher.Audio`.  Integration: in the 10 game files that do
//   `using Microsoft.Xna.Framework.Audio;` replace that line with
//   `using Dishwasher.Audio;` (see notes/audio/README.md).
//
// ASSETS: the cue map is `sfx/cue_map.json` (relative to the app Content root),
//   e.g. "wav/waves/waves_w009.wav" or "ogg/music/music_w002.ogg" under sfx/.
//
// OGG: MonoGame's SoundEffect.FromStream on Android accepts WAV only.  This shim
//   therefore plays MS-ADPCM/16-bit PCM WAV directly, and can decode OGG through
//   NVorbis when the DISHWASHER_OGG symbol is defined (add
//   <PackageReference Include="NVorbis" Version="0.10.5" /> and
//   <DefineConstants>DISHWASHER_OGG</DefineConstants>).  Without it, OGG-only
//   cues (the 8 long music tracks) degrade to silence instead of crashing.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

// Alias the MonoGame types we build on, to keep the namespace unambiguous.
using MgSoundEffect = Microsoft.Xna.Framework.Audio.SoundEffect;
using MgSoundEffectInstance = Microsoft.Xna.Framework.Audio.SoundEffectInstance;
using MgSoundState = Microsoft.Xna.Framework.Audio.SoundState;
using MgAudioChannels = Microsoft.Xna.Framework.Audio.AudioChannels;
using MgAudioListener = Microsoft.Xna.Framework.Audio.AudioListener;
using MgAudioEmitter = Microsoft.Xna.Framework.Audio.AudioEmitter;
using Microsoft.Xna.Framework;

namespace Dishwasher.Audio
{
    /// <summary>XNA-compatible stop options (mirrors Microsoft.Xna.Framework.Audio).</summary>
    public enum AudioStopOptions
    {
        AsAuthored = 0,
        Immediate = 1,
    }

    /// <summary>A named mixer bus (Default / Music / solo_fg / ...).</summary>
    public sealed class AudioCategory
    {
        private readonly List<Cue> _cues = new List<Cue>();
        private float _volume = 1f;

        internal AudioCategory(string name) { Name = name; }

        public string Name { get; }

        public float Volume => _volume;

        /// <summary>Sets the category volume (0..1) and applies it to live cues.</summary>
        public void SetVolume(float volume)
        {
            _volume = volume < 0f ? 0f : (volume > 1f ? 1f : volume);
            for (int i = 0; i < _cues.Count; i++)
                _cues[i].OnCategoryVolumeChanged();
        }

        internal void Register(Cue cue)
        {
            if (!_cues.Contains(cue)) _cues.Add(cue);
        }

        internal void Unregister(Cue cue) { _cues.Remove(cue); }

        internal void Update()
        {
            for (int i = _cues.Count - 1; i >= 0; i--)
            {
                var c = _cues[i];
                if (c.ReapIfStopped()) _cues.RemoveAt(i);
            }
        }

        internal void Dispose() { _cues.Clear(); }
    }

    /// <summary>Owns categories and acts as the registry handed to SoundBank.</summary>
    public sealed class AudioEngine : IDisposable
    {
        // XGS category ids used by this game (order from sfx/sfxproj.xgs):
        internal static readonly string[] CategoryNames =
            { "Global", "Default", "Music", "solo_bg", "solo_fg", "halper" };

        private readonly Dictionary<string, AudioCategory> _categories =
            new Dictionary<string, AudioCategory>(StringComparer.OrdinalIgnoreCase);

        public AudioEngine(string settingsFile)
        {
            // settingsFile (sfx/sfxproj.xgs) is intentionally not parsed: the only
            // thing the game needs from it is the category list, which is fixed.
            for (int i = 0; i < CategoryNames.Length; i++)
                _categories[CategoryNames[i]] = new AudioCategory(CategoryNames[i]);
            global::Dishwasher.Log.Info("[audio] AudioEngine created: " + CategoryNames.Length
                + " categories; .xgs ignored ('" + settingsFile + "')");
        }

        public AudioCategory GetCategory(string name)
        {
            if (name == null) name = "Default";
            if (_categories.TryGetValue(name, out var c)) return c;
            c = new AudioCategory(name);
            _categories[name] = c;
            return c;
        }

        internal AudioCategory GetCategoryById(int id)
        {
            string name = (id >= 0 && id < CategoryNames.Length) ? CategoryNames[id] : "Default";
            return GetCategory(name);
        }

        /// <summary>Called every frame by the game; reaps finished one-shot cues.</summary>
        public void Update()
        {
            foreach (var c in _categories.Values) c.Update();
            CueDatabase.Update();
            // PORT (music-switch-freeze): finish off-thread OGG decodes on this
            // (render) thread, where the OpenAL context is current.
            SoundLoader.Pump();
        }

        public void Dispose()
        {
            foreach (var c in _categories.Values) c.Dispose();
            _categories.Clear();
        }

        // Exposed for parity with XNA; the shim resolves cue files itself.
        internal Dictionary<string, object> Wavebanks { get; } =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A wave bank token. The shim does not parse .xwb at runtime (the cue map is
    /// already extracted); it only records the bank so SoundBank can find the map.
    /// </summary>
    public sealed class WaveBank : IDisposable
    {
        internal string FileName { get; }
        internal string BankName { get; }
        internal bool Streaming { get; }

        public WaveBank(AudioEngine engine, string nonStreamingWaveBankFilename)
        {
            FileName = nonStreamingWaveBankFilename;
            BankName = Path.GetFileNameWithoutExtension(nonStreamingWaveBankFilename);
        }

        public WaveBank(AudioEngine engine, string streamingWaveBankFilename, int offset, short packetsize)
        {
            FileName = streamingWaveBankFilename;
            BankName = Path.GetFileNameWithoutExtension(streamingWaveBankFilename);
            Streaming = true;
        }

        public bool IsInUse { get; private set; }
        public bool IsPrepared { get; private set; }
        public bool IsDisposed { get; private set; }

        public void Dispose() { IsDisposed = true; }
    }

    /// <summary>Resolves cue names to playable Cue objects using the offline map.</summary>
    public sealed class SoundBank : IDisposable
    {
        private readonly AudioEngine _engine;
        private readonly string _baseDir;
        private readonly Dictionary<string, Cue> _cache =
            new Dictionary<string, Cue>(StringComparer.Ordinal);

        public SoundBank(AudioEngine audioEngine, string fileName)
        {
            _engine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
            if (string.IsNullOrEmpty(fileName)) throw new ArgumentNullException(nameof(fileName));
            _baseDir = DeriveBaseDir(fileName);
            CueDatabase.Load(_baseDir);
            // PORT (music-switch-freeze): when the music bank is created (during
            // the boot load, before the menu appears) warm the two tracks the
            // game reaches first so the first switch is instant. Bounded to two.
            if (fileName.IndexOf("musicsnd", StringComparison.OrdinalIgnoreCase) >= 0)
                SoundLoader.PreloadDefaultMusic(_baseDir);
            global::Dishwasher.Log.Info("[audio] SoundBank ready: '" + fileName + "' -> "
                + CueDatabase.Count + " cues registered");
        }

        internal static string DeriveBaseDir(string fileName)
        {
            int slash = fileName.LastIndexOf('/');
            return slash >= 0 ? fileName.Substring(0, slash + 1) : "";
        }

        /// <summary>
        /// Gets a cue by friendly name. Unlike XNA this never throws: an unknown
        /// cue yields a silent Cue so missing/renamed entries cannot crash the game.
        /// </summary>
        public Cue GetCue(string name)
        {
            if (string.IsNullOrEmpty(name)) return Cue.Silent("");
            if (_cache.TryGetValue(name, out var c))
            {
                // PORT FIX (music): a cache hit that has ALREADY been disposed must
                // not be handed back. The game's Music.Play()/Music.setMusic()
                // dispose a Cue and then immediately re-fetch it by name to
                // (re)start it — XNA's SoundBank.GetCue returns a fresh, playable
                // Cue for that. The old shim returned the same dead cached object,
                // so every music Play was a silent no-op (SFX were unaffected
                // because they never dispose).
                if (!c.IsDisposed) return c;
            }

            if (CueDatabase.TryGet(name, out var def))
            {
                var cat = _engine.GetCategoryById(def.CategoryId);
                c = new Cue(name, cat, def);
            }
            else
            {
                CueDatabase.WarnOnce(name);
                c = Cue.Silent(name);
            }
            _cache[name] = c;
            return c;
        }

        /// <summary>Plays a cue by friendly name (fire-and-forget).</summary>
        public void PlayCue(string name) => GetCue(name).Play();

        public bool IsInUse { get; private set; }
        public bool IsDisposed { get; private set; }
        public void Dispose() { IsDisposed = true; }
    }

    /// <summary>A playable cue: one or more wave variations, a category and state.</summary>
    public sealed class Cue : IDisposable
    {
        private readonly AudioCategory _category;
        private readonly CueDatabase.Def _def;   // null == silent placeholder
        private MgSoundEffectInstance _instance;
        private int _variation = -1;
        private float _cueVolume = 1f;
        private bool _loggedPlay;   // one-line proof-of-playback log per cue name
        private bool _loggedMissing; // one-shot "no audio" log per cue name
        private string _boundPath;   // PORT (music-switch-freeze): pinned SoundLoader path

        internal Cue(string name, AudioCategory category, CueDatabase.Def def)
        {
            Name = name;
            _category = category;
            _def = def;
        }

        internal static Cue Silent(string name) => new Cue(name, null, null);

        public string Name { get; }

        public bool IsPreparing => false;
        public bool IsStopped => _instance == null || _instance.State == MgSoundState.Stopped;
        public bool IsPaused => _instance != null && _instance.State == MgSoundState.Paused;
        public bool IsPlaying =>
            _instance != null && (_instance.State == MgSoundState.Playing ||
                                  _instance.State == MgSoundState.Paused);
        public bool IsCreated => _instance != null;
        public bool IsDisposed { get; private set; }

        public void Play()
        {
            if (IsDisposed || _def == null || _def.Files.Length == 0) return;

            // Pick the next variation (XACT "Ordered" playlist is close enough for
            // the game's usage; randomised playlists resolve to the same file set).
            _variation = (_variation + 1) % _def.Files.Length;
            var file = _def.Files[_variation];
            string path = _def.BaseDir + file.Path;

            var effect = SoundLoader.Get(_def.BaseDir, file.Path);
            if (effect == null)
            {
                // PORT (music-switch-freeze): a long OGG that is still being
                // decoded on the worker is not an error. The game retries Play()
                // every frame while the music state holds, so playback starts as
                // soon as the track is ready; only genuinely missing/undecodable
                // audio is reported -- and only once per cue, so a permanently
                // failed file cannot spam logcat on every frame.
                if (!SoundLoader.IsPending(path) && !_loggedMissing)
                {
                    _loggedMissing = true;
                    global::Dishwasher.Log.Error("[audio] cue '" + Name + "' -> no audio at "
                        + path);
                }
                return;   // not ready yet / unsupported / missing -> silent
            }
            _loggedMissing = false;

            if (_instance != null)
            {
                _instance.Stop();
                _instance.Dispose();
                _instance = null;
            }

            // PORT (music-switch-freeze): bind this cue's live instance to `path`
            // so the bounded LRU cannot dispose a track that is playing.
            SoundLoader.Unpin(_boundPath);
            _boundPath = path;
            SoundLoader.Pin(path);

            _instance = effect.CreateInstance();
            _instance.IsLooped = file.Loop;
            _instance.Volume = FinalVolume;
            _instance.Play();
            _category?.Register(this);

            // PORT: one-line, always-on proof that a cue resolved to real audio
            // (the shim's per-load diagnostics are DEBUG-only).
            if (!_loggedPlay)
            {
                _loggedPlay = true;
                global::Dishwasher.Log.Info("[audio] cue playing: '" + Name + "' -> "
                    + path + " (loop=" + file.Loop + ")");
            }
        }

        public void Stop(AudioStopOptions options)
        {
            if (_instance == null) return;
            _instance.Stop();
            _instance.Dispose();
            _instance = null;
            SoundLoader.Unpin(_boundPath);   // PORT (music-switch-freeze)
            _boundPath = null;
            _category?.Unregister(this);
        }

        public void Pause()
        {
            if (_instance != null && _instance.State == MgSoundState.Playing)
                _instance.Pause();
        }

        public void Resume()
        {
            if (_instance != null && _instance.State == MgSoundState.Paused)
                _instance.Resume();
        }

        /// <summary>Optional XACT-style 3D placement; unused by the game's call sites.</summary>
        public void Apply3D(MgAudioListener listener, MgAudioEmitter emitter)
        {
            if (_instance == null || listener == null || emitter == null) return;
            var delta = emitter.Position - listener.Position;
            float distance = delta.Length();
            _cueVolume = 1f / (1f + distance);
            float pan = 0f;
            if (distance > 0.0001f)
            {
                delta /= distance;
                var right = Vector3.Cross(listener.Up, listener.Forward);
                pan = MathHelper.Clamp(Vector3.Dot(delta, right), -1f, 1f);
            }
            _instance.Pan = pan;
            _instance.Volume = FinalVolume;
        }

        private float FinalVolume
        {
            get
            {
                float v = _cueVolume * (_category != null ? _category.Volume : 1f);
                if (v < 0f) v = 0f;
                if (v > 1f) v = 1f;
                return v;
            }
        }

        internal void OnCategoryVolumeChanged()
        {
            if (_instance != null) _instance.Volume = FinalVolume;
        }

        /// <summary>Called from AudioEngine.Update; true if the cue can be forgotten.</summary>
        internal bool ReapIfStopped()
        {
            if (_instance != null && _instance.State == MgSoundState.Stopped)
            {
                _instance.Dispose();
                _instance = null;
                SoundLoader.Unpin(_boundPath);   // PORT (music-switch-freeze)
                _boundPath = null;
                return true;
            }
            return _instance == null;
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            if (_instance != null)
            {
                _instance.Stop();
                _instance.Dispose();
                _instance = null;
            }
            SoundLoader.Unpin(_boundPath);   // PORT (music-switch-freeze)
            _boundPath = null;
            _category?.Unregister(this);
        }
    }

    // ---------------------------------------------------------------------
    //  Asset loading
    // ---------------------------------------------------------------------

    internal static class SoundLoader
    {
        private static readonly Dictionary<string, MgSoundEffect> _cache =
            new Dictionary<string, MgSoundEffect>(StringComparer.Ordinal);
        private static readonly HashSet<string> _oggLogged = new HashSet<string>(StringComparer.Ordinal);

        // Only cache decoded OGG below this many samples/channel (~a few MB PCM);
        // long music tracks are decoded on a worker and held in a bounded LRU.
        private const long OggCacheSampleLimit = 1_000_000;

        // ------------------------------------------------------------------
        // PORT (music-switch-freeze): asynchronous long-OGG decode.
        //
        // Decoding a music OGG (up to ~250 s -> ~48 MB PCM) used to run inside
        // SoundLoader.Get(), which Cue.Play() calls on the game/render thread,
        // freezing the frame for several seconds on every track switch. Now the
        // expensive part (read + NVorbis decode to PCM) runs on one background
        // worker thread; the OpenAL buffer is created later on the main thread
        // (the OpenAL context is only current there) from the decoded PCM, which
        // is cheap (a buffer upload, tens of ms). Get() always returns without
        // blocking: a not-yet-ready track returns null and the game's own retry
        // loop (Music.Play() every frame) simply tries again.
        //
        // The retry-every-frame pattern MUST NOT enqueue a decode per frame:
        // `_pending` dedupes paths that are queued/decoding/ready. There is
        // exactly one worker thread, created lazily; the main thread never waits
        // on it, so there is no deadlock. Decoded tracks live in a bounded LRU
        // (LongOggCacheLimit) so re-play and back-and-forth switching are instant
        // without unbounded memory. Set AsyncOggDecode=false to restore the old
        // synchronous behaviour (diagnostics only).
        // ------------------------------------------------------------------
        private const bool AsyncOggDecode = true;

        // Keep the most recent two long tracks resident (current + next/cross-
        // fade). Worst case ~2 x 48 MB of PCM (e.g. musicboss 252 s + music2
        // 247 s @ 48 kHz stereo 16-bit) inside the SoundEffect, plus OpenAL's
        // own copy of each. Small WAV/ADPCM effects are unaffected (always
        // cached in `_cache`).
        private const int LongOggCacheLimit = 2;

        private sealed class OggJob { public string Path; }
        private sealed class DecodedPcm { public byte[] Data; public int Rate; public int Channels; }

        // `_gate` guards the SoundEffect caches; `_workLock` guards the decode
        // queue/state. They are never held at the same time, so no lock ordering
        // issue exists between them.
        private static readonly object _gate = new object();
        private static readonly Dictionary<string, MgSoundEffect> _longCache =
            new Dictionary<string, MgSoundEffect>(StringComparer.Ordinal);
        private static readonly LinkedList<string> _lru = new LinkedList<string>();
        private static readonly Dictionary<string, LinkedListNode<string>> _lruNodes =
            new Dictionary<string, LinkedListNode<string>>(StringComparer.Ordinal);

        // PORT (music-switch-freeze): paths a live Cue instance is currently
        // playing. The bounded LRU must never Dispose() the SoundEffect (and delete
        // its OpenAL buffer) out from under a playing source -- that would cut the
        // track, and deleting an in-use AL buffer is driver-dependent/unsafe.
        // Reference-counted because two cues (soft + hard crossfade) can share a
        // path. Guarded by _gate like the caches.
        private static readonly Dictionary<string, int> _playing =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static readonly object _workLock = new object();
        private static readonly Queue<OggJob> _queue = new Queue<OggJob>();
        private static readonly HashSet<string> _pending = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _failed = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, DecodedPcm> _ready =
            new Dictionary<string, DecodedPcm>(StringComparer.Ordinal);
        private static Thread _worker;

        private static bool IsOgg(string relativeFile) =>
            relativeFile.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);

        internal static MgSoundEffect Get(string baseDir, string relativeFile)
        {
            if (string.IsNullOrEmpty(relativeFile)) return null;
            string path = baseDir + relativeFile;

            lock (_gate)
            {
                if (_cache.TryGetValue(path, out var cached)) return cached;
                if (_longCache.TryGetValue(path, out var longCached))
                {
                    TouchLongLocked(path);
                    return longCached;
                }
            }

            if (AsyncOggDecode && IsOgg(relativeFile))
            {
                // Never decode on the caller (the render thread): a full music
                // track takes seconds. Queue a single worker decode and let the
                // game retry; Pump() uploads the result on the main thread.
                EnqueueOgg(path);
                return null;
            }

            return LoadSynchronous(relativeFile, path);
        }

        /// <summary>True while a path is queued/decoding (or decoded and waiting
        /// to be uploaded). Lets Cue.Play() suppress "no audio" spam while a
        /// music track is still being prepared off-thread.</summary>
        internal static bool IsPending(string path)
        {
            lock (_workLock) { return _pending.Contains(path) && !_failed.Contains(path); }
        }

        // Legacy path: decode + build the SoundEffect on the calling thread.
        // Used for all WAV/ADPCM (small, fast) and, when AsyncOggDecode=false,
        // for OGG diagnostics.
        private static MgSoundEffect LoadSynchronous(string relativeFile, string path)
        {
            long started = Stopwatch.GetTimestamp();
            MgSoundEffect effect = null;
            try
            {
                using (var raw = global::Dishwasher.PublicBuild.OpenContent(path))
                {
                    // Android asset streams are not seekable; copy to memory.
                    var ms = new MemoryStream();
                    raw.CopyTo(ms);
                    ms.Position = 0;

                    if (IsOgg(relativeFile))
                    {
                        if (!TryLoadOgg(ms, out effect))
                            global::Dishwasher.Log.Error("[audio] OGG decode FAILED: " + path);
                        else if (_oggLogged.Add(path))
                            global::Dishwasher.Log.Info("[audio] OGG decoded: " + path + " ("
                                + effect.Duration.TotalSeconds.ToString("0.0") + "s, "
                                + effect.Duration + ")");
                    }
                    else
                    {
                        effect = MgSoundEffect.FromStream(ms);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("audio load failed '" + path + "': " + ex.Message);
                return null;
            }

            // Diagnostic: a synchronous load that blocks the game/render thread
            // for long is exactly the freeze this file now avoids for OGG. This
            // stays silent in normal operation.
            double blocked = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            if (blocked > 80.0)
                global::Dishwasher.Log.Info("[audio] MAIN-THREAD load blocked "
                    + blocked.ToString("0") + "ms: " + path);

            if (effect != null)
                Log("loaded '" + path + "' (" + effect.Duration.TotalSeconds.ToString("0.0") + "s)");

            if (effect != null && ShouldCache(relativeFile, effect))
            {
                lock (_gate) { _cache[path] = effect; }
            }
            return effect;
        }

        // WAV/ADPCM effects are small and reused, so always cache. Long OGG
        // music decodes to tens of MB and is held in the bounded LRU instead.
        private static bool ShouldCache(string relativeFile, MgSoundEffect effect)
        {
            if (!IsOgg(relativeFile))
                return true;
            return effect.Duration.TotalSeconds <= 20.0;
        }

        private static bool TryLoadOgg(Stream stream, out MgSoundEffect effect)
        {
            effect = null;
            var pcm = DecodeOggPcm(stream);
            if (pcm == null) return false;
            effect = new MgSoundEffect(pcm.Data, pcm.Rate, (MgAudioChannels)pcm.Channels);
            return true;
        }

        // Pure managed PCM decode (no OpenAL) -- safe on any thread.
        private static DecodedPcm DecodeOggPcm(Stream stream)
        {
#if DISHWASHER_OGG
            using (var reader = new NVorbis.VorbisReader(stream, false))
            {
                int channels = reader.Channels;
                int rate = reader.SampleRate;
                long total = reader.TotalSamples;
                if (total > OggCacheSampleLimit * 4)
                    Log("decoding long OGG (" + total + " samples/ch): " + rate + "Hz x" + channels);

                var pcm = new byte[total * channels * 2];
                var buf = new float[Math.Max(channels, 1) * 8192];
                long written = 0;
                int n;
                while (written < pcm.Length &&
                       (n = reader.ReadSamples(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < n && written + 1 < pcm.Length; i++)
                    {
                        int s = (int)(buf[i] * 32767f);
                        if (s > 32767) s = 32767;
                        if (s < -32768) s = -32768;
                        pcm[written++] = (byte)s;
                        pcm[written++] = (byte)(s >> 8);
                    }
                }
                if (written == 0) return null;
                return new DecodedPcm { Data = pcm, Rate = rate, Channels = channels };
            }
#else
            return null;
#endif
        }

        // ---- background decode worker ------------------------------------

        private static void EnsureWorker()
        {
            if (_worker != null && _worker.IsAlive) return;
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "DishwasherOggDecode",
            };
            _worker.Start();
        }

        private static void EnqueueOgg(string path)
        {
            lock (_workLock)
            {
                if (_failed.Contains(path) || _pending.Contains(path) || _ready.ContainsKey(path))
                    return;
                _pending.Add(path);
                _queue.Enqueue(new OggJob { Path = path });
                EnsureWorker();
                Monitor.Pulse(_workLock);
            }
        }

        private static void WorkerLoop()
        {
            while (true)
            {
                OggJob job;
                lock (_workLock)
                {
                    while (_queue.Count == 0) Monitor.Wait(_workLock);
                    job = _queue.Dequeue();
                }

                long started = Stopwatch.GetTimestamp();
                DecodedPcm pcm = null;
                try
                {
                    global::Dishwasher.Log.Info("[audio] OGG decode START (worker thread "
                        + Thread.CurrentThread.ManagedThreadId + "): " + job.Path);
                    using (var raw = global::Dishwasher.PublicBuild.OpenContent(job.Path))
                    {
                        var ms = new MemoryStream();
                        raw.CopyTo(ms);
                        ms.Position = 0;
                        pcm = DecodeOggPcm(ms);
                    }
                }
                catch (Exception ex)
                {
                    global::Dishwasher.Log.Error("[audio] OGG worker decode failed '"
                        + job.Path + "': " + ex.Message);
                }

                double elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                lock (_workLock)
                {
                    if (pcm == null)
                    {
                        _pending.Remove(job.Path);
                        _failed.Add(job.Path);
                    }
                    else
                    {
                        _ready[job.Path] = pcm;
                    }
                }

                if (pcm == null)
                    global::Dishwasher.Log.Error("[audio] OGG decode FAILED: " + job.Path);
                else
                    global::Dishwasher.Log.Info("[audio] OGG decode DONE (worker thread "
                        + Thread.CurrentThread.ManagedThreadId + "): " + job.Path + " in "
                        + elapsed.ToString("0") + "ms (" + (pcm.Data.Length / (1024 * 1024))
                        + "MB PCM)");
            }
        }

        /// <summary>Main-thread pump: turn one worker-decoded PCM into a
        /// SoundEffect (the OpenAL context is current only on this thread) and
        /// place it in the bounded LRU. Called every frame by AudioEngine.Update,
        /// so preloaded tracks are ready before they are first requested.</summary>
        internal static void Pump()
        {
            string path = null;
            DecodedPcm pcm = null;
            lock (_workLock)
            {
                foreach (var kv in _ready) { path = kv.Key; pcm = kv.Value; break; }
                if (path == null) return;
                _ready.Remove(path);
                _pending.Remove(path);
            }

            MgSoundEffect effect = null;
            try
            {
                long started = Stopwatch.GetTimestamp();
                effect = new MgSoundEffect(pcm.Data, pcm.Rate, (MgAudioChannels)pcm.Channels);
                double elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                global::Dishwasher.Log.Info("[audio] OGG upload (main thread "
                    + Thread.CurrentThread.ManagedThreadId + "): " + path + " in "
                    + elapsed.ToString("0") + "ms");
                if (_oggLogged.Add(path))
                    global::Dishwasher.Log.Info("[audio] OGG decoded: " + path + " ("
                        + effect.Duration.TotalSeconds.ToString("0.0") + "s, "
                        + effect.Duration + ")");
            }
            catch (Exception ex)
            {
                global::Dishwasher.Log.Error("[audio] OGG upload failed '" + path + "': " + ex.Message);
                lock (_workLock) { _pending.Remove(path); _failed.Add(path); }
                return;
            }

            InsertLong(path, effect);
        }

        // Insert into the bounded LRU, disposing the least-recently-used track
        // outside the lock. Tracks pinned by a live Cue (_playing) are skipped, so
        // eviction never targets a track that is playing; if every resident track
        // is in use the cache temporarily exceeds LongOggCacheLimit.
        private static void InsertLong(string path, MgSoundEffect effect)
        {
            var evicted = new List<MgSoundEffect>();
            lock (_gate)
            {
                if (_longCache.TryGetValue(path, out var old))
                {
                    _longCache.Remove(path);
                    if (_lruNodes.TryGetValue(path, out var oldNode))
                    {
                        _lru.Remove(oldNode);
                        _lruNodes.Remove(path);
                    }
                    evicted.Add(old);
                }
                _longCache[path] = effect;
                _lruNodes[path] = _lru.AddFirst(path);
                while (_lru.Count > LongOggCacheLimit)
                {
                    // Never evict a path a live Cue is playing, nor the effect we
                    // just inserted (it is the one that was just requested). If
                    // every resident track is in use, allow a temporary, bounded
                    // overflow; the next insert after they stop trims back down.
                    var last = _lru.Last;
                    while (last != null && (last.Value == path || _playing.ContainsKey(last.Value)))
                        last = last.Previous;
                    if (last == null)
                        break;
                    _lru.Remove(last);
                    _lruNodes.Remove(last.Value);
                    if (_longCache.TryGetValue(last.Value, out var gone))
                    {
                        _longCache.Remove(last.Value);
                        evicted.Add(gone);
                    }
                }
            }
            for (int i = 0; i < evicted.Count; i++)
            {
                try { evicted[i].Dispose(); }
                catch (Exception) { /* deleting an in-use OpenAL buffer is allowed */ }
            }
        }

        private static void TouchLongLocked(string path)
        {
            if (_lruNodes.TryGetValue(path, out var node) && node.List != null)
                _lru.Remove(node);
            _lruNodes[path] = _lru.AddFirst(path);
        }

        // ---- in-use pinning (music-switch-freeze) -------------------------
        // Cue.Play() pins the path it has bound to an instance; Stop/Dispose/Reap
        // unpin it. InsertLong consults _playing so a track that is currently
        // playing is never disposed. Called on the main thread only, but guarded
        // by _gate for consistency with the caches.
        internal static void Pin(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (_gate)
            {
                _playing.TryGetValue(path, out int n);
                _playing[path] = n + 1;
            }
        }

        internal static void Unpin(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (_gate)
            {
                if (!_playing.TryGetValue(path, out int n)) return;
                if (n <= 1) _playing.Remove(path);
                else _playing[path] = n - 1;
            }
        }

        // ---- preload policy ----------------------------------------------

        internal static void Preload(string baseDir, string relativeFile)
        {
            if (!AsyncOggDecode || string.IsNullOrEmpty(relativeFile) || !IsOgg(relativeFile))
                return;
            EnqueueOgg(baseDir + relativeFile);
        }

        // Warm the two tracks the game reaches first -- the menu theme (soft1)
        // and the first gameplay theme (music2) -- during the ~4 s boot carousel,
        // which is otherwise dead time. Bounded to two so it matches the LRU and
        // never decodes all eight tracks at once.
        internal static void PreloadDefaultMusic(string baseDir)
        {
            PreloadCue("soft1");
            PreloadCue("music2");
        }

        private static void PreloadCue(string cueName)
        {
            if (!AsyncOggDecode) return;
            if (CueDatabase.TryGet(cueName, out var def) && def.Files != null && def.Files.Length > 0)
                EnqueueOgg(def.BaseDir + def.Files[0].Path);
        }

        internal static void Clear()
        {
            var dispose = new List<MgSoundEffect>();
            lock (_gate)
            {
                dispose.AddRange(_cache.Values); _cache.Clear();
                dispose.AddRange(_longCache.Values); _longCache.Clear();
                _lru.Clear(); _lruNodes.Clear(); _playing.Clear();
            }
            lock (_workLock)
            {
                _queue.Clear(); _pending.Clear(); _ready.Clear(); _failed.Clear();
            }
            for (int i = 0; i < dispose.Count; i++)
            {
                try { dispose[i].Dispose(); }
                catch (Exception) { }
            }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private static void Log(string message) =>
            // Route shim diagnostics to the port logger (logcat on Android) so boot
            // iteration can see them; Debug.WriteLine alone is not captured.
            global::Dishwasher.Log.Info("[audio] " + message);
    }

    // ---------------------------------------------------------------------
    //  cue_map.json reader
    // ---------------------------------------------------------------------

    internal static class CueDatabase
    {
        internal struct FileRef
        {
            public string Path;
            public bool Loop;
        }

        internal sealed class Def
        {
            public string Bank;
            public string BaseDir;
            public int CategoryId;
            public FileRef[] Files = Array.Empty<FileRef>();
        }

        private static readonly Dictionary<string, Def> _cues =
            new Dictionary<string, Def>(StringComparer.Ordinal);
        private static bool _loaded;
        private static readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);

        internal static bool TryGet(string name, out Def def) => _cues.TryGetValue(name, out def);

        internal static int Count => _cues.Count;

        internal static void Load(string baseDir)
        {
            if (_loaded) return;
            _loaded = true;
            string mapPath = baseDir + "cue_map.json";
            try
            {
                using (var raw = global::Dishwasher.PublicBuild.OpenContent(mapPath))
                using (var doc = JsonDocument.Parse(raw))
                {
                    var cues = doc.RootElement.GetProperty("cues");
                    foreach (var prop in cues.EnumerateObject())
                    {
                        var value = prop.Value;
                        var def = new Def
                        {
                            Bank = value.GetProperty("bank").GetString(),
                            BaseDir = baseDir,
                            CategoryId = value.TryGetProperty("category_id", out var c) ? c.GetInt32() : 1,
                        };
                        var list = new List<FileRef>();
                        foreach (var f in value.GetProperty("files").EnumerateArray())
                        {
                            list.Add(new FileRef
                            {
                                Path = f.GetProperty("file").GetString(),
                                Loop = f.TryGetProperty("loop", out var l) && l.GetBoolean(),
                            });
                        }
                        def.Files = list.ToArray();
                        _cues[prop.Name] = def;
                    }
                }
                Log("cue map loaded: " + _cues.Count + " cues from " + mapPath);
            }
            catch (Exception ex)
            {
                Log("cue map load failed '" + mapPath + "': " + ex.Message);
            }
        }

        internal static void Update() { }

        internal static void WarnOnce(string name)
        {
            if (_warned.Add(name)) Log("missing cue (silent): " + name);
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private static void Log(string message) =>
            // Route shim diagnostics to the port logger (logcat on Android) so boot
            // iteration can see them; Debug.WriteLine alone is not captured.
            global::Dishwasher.Log.Info("[audio] " + message);
    }
}
