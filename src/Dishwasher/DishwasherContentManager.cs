// DishwasherContentManager.cs
//
// Runtime ContentManager for the MonoGame/Android port of
// "The Dishwasher: Dead Samurai".
//
// WHY THIS EXISTS
//   XNA 3.0 `SpriteBlendMode.AlphaBlend` is STRAIGHT alpha
//   (SourceBlend = SourceAlpha, DestinationBlend = InverseSourceAlpha). The
//   MonoGame stock ContentManager falls back to `Texture2D.FromStream(...,
//   DefaultColorProcessors.PremultiplyAlpha)` for PNGs, which premultiplies the
//   art. Premultiplied textures combined with the straight blend map
//   (`BlendState.NonPremultiplied`, see GameSource) is the correct XNA 3.0
//   behaviour; loading them premultiplied is not. This subclass loads the
//   `gfx/*.png` textures STRAIGHT (no premultiply), so the blend map and the
//   texture agree. See android/notes/comic-cutscene.md and
//   android/notes/final-consolidation.md.
//
// BEHAVIOUR
//   * Texture2D  -> <RootDirectory>/<asset>.png via Texture2D.FromStream
//                   (straight alpha; only fully-transparent RGB is zeroed,
//                   which is standard XNA behaviour and cannot contribute).
//   * SpriteFont -> <asset>_atlas.png + <asset>.spritefont.json via MonoGame's
//                   public SpriteFont constructor (exact original 360 metrics;
//                   no TTF / MGCB rasterisation). Used only for CJK locales.
//   * Effect / CharDef / anything else -> stock ContentManager (.xnb from MGCB).
//
// PARALLEL DECODE (PORT perf, 2026-10-02)
//   Texture2D.FromStream is CPU-bound StbImageSharp decode followed by a GL
//   upload that MonoGame marshals to the render thread (`Threading.BlockOnUIThread`).
//   `Prefetch()` runs the decode on the .NET thread pool while the uploads stay
//   serialised on the render thread; the loader thread picks completed textures
//   up through `_prefetched`. If a texture has not finished prefetching when the
//   loader asks for it, `GetTexture` decodes it inline on the caller (the loader
//   thread), exactly like the original serial code.
//
//   IMPORTANT: GetTexture must NEVER block on a worker task. `LoadContent()`
//   runs on the render thread, and a render-thread block waiting on a worker
//   that itself waits for the next `Threading.Run()` is a deadlock (observed
//   and fixed 2026-10-02). Decoding inline on the render thread is safe because
//   `Threading.BlockOnUIThread` executes immediately when already on that thread.
//
//   Set `Globals.ParallelTextureDecode = false` to disable prefetch entirely.
//
// This file is wiring, not instrumentation: it is part of the shipped render
// path and must stay enabled.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;

namespace Dishwasher.Content;

public sealed class DishwasherContentManager : ContentManager
{
    private readonly GraphicsDevice _graphics;
    private readonly Dictionary<string, object> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _owned = new();
    private readonly object _gate = new();

    // Textures finished by Prefetch(), waiting to be consumed by GetTexture().
    private readonly ConcurrentDictionary<string, Texture2D> _prefetched =
        new(StringComparer.OrdinalIgnoreCase);
    // Names currently being decoded by a prefetch worker (prevents duplicates).
    private readonly ConcurrentDictionary<string, byte> _prefetching =
        new(StringComparer.OrdinalIgnoreCase);

    public DishwasherContentManager(IServiceProvider services, GraphicsDevice graphics,
                                    string rootDirectory = "Content")
        : base(services, rootDirectory)
    {
        _graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
    }

    public override T Load<T>(string assetName)
    {
        if (string.IsNullOrEmpty(assetName))
            throw new ArgumentNullException(nameof(assetName));

        string key = assetName.Replace('\\', '/');
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out object? cached) && cached is T hit)
                return hit;
        }

        object result;
        if (typeof(T) == typeof(Texture2D))
            result = GetTexture(key);
        else if (typeof(T) == typeof(SpriteFont))
            result = LoadSpriteFont(key);
        else
            result = base.Load<T>(assetName); // Effect, CharDef, future .xnb types

        lock (_gate)
        {
            _cache[key] = result;
        }
        return (T)result;
    }

    // PORT (perf, parallel decode): decode the named textures ahead of time on
    // the thread pool. A name is decoded at most once by the pool; the loader
    // consumes the result via GetTexture.
    public void Prefetch(IEnumerable<string> assetNames)
    {
        if (assetNames == null)
            return;
        foreach (string name in assetNames)
        {
            if (string.IsNullOrEmpty(name))
                continue;
            string key = name.Replace('\\', '/');
            lock (_gate)
            {
                if (_cache.ContainsKey(key))
                    continue;
            }
            if (_prefetched.ContainsKey(key))
                continue;
            if (!_prefetching.TryAdd(key, 0))
                continue;

            Task.Run(() =>
            {
                try
                {
                    // Re-check: the loader may have decoded it inline already.
                    lock (_gate)
                    {
                        if (_cache.ContainsKey(key))
                            return;
                    }
                    Texture2D tex = DecodeTexture(key);
                    _prefetched[key] = tex;
                }
                catch (Exception ex)
                {
                    // Prefetch is an optimisation; never let it break boot.
                    Dishwasher.Log.Exception("prefetch:" + key, ex);
                }
                finally
                {
                    _prefetching.TryRemove(key, out _);
                }
            });
        }
    }

    // RootDirectory is "." in this port, so avoid emitting a "./" prefix (some
    // Android AssetManager paths dislike it).  Otherwise combine normally.
    private Stream OpenContent(string relativePath)
    {
        string path = string.IsNullOrEmpty(RootDirectory) || RootDirectory == "."
            ? relativePath
            : Path.Combine(RootDirectory, relativePath);
        // PORT (public build): prefer the app-private content tree (staged
        // originals + on-device derivations); fall back to APK assets.
        return global::Dishwasher.PublicBuild.OpenContent(path);
    }

    private Texture2D GetTexture(string assetName)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(assetName, out object? cached) && cached is Texture2D hit)
                return hit;
        }

        // Prefetch completed? Consume it. Otherwise decode inline on the caller
        // thread (never block on a worker -- see the header comment).
        if (_prefetched.TryRemove(assetName, out Texture2D? pre))
            return pre;

        return DecodeTexture(assetName);
    }

    private Texture2D DecodeTexture(string assetName)
    {
        using Stream s = OpenContent(assetName + ".png");
        // 2-arg FromStream == DefaultColorProcessors.ZeroTransparentPixels:
        // straight alpha, NOT premultiplied.
        Texture2D tex = Texture2D.FromStream(_graphics, s,
            Microsoft.Xna.Framework.Graphics.DefaultColorProcessors.ZeroTransparentPixels);

        lock (_gate)
        {
            _cache[assetName] = tex;
            _owned.Add(tex);
        }
        return tex;
    }

    private SpriteFont LoadSpriteFont(string assetName)
    {
        SpriteFontData? data;
        using (Stream js = OpenContent(assetName + ".spritefont.json"))
            data = JsonSerializer.Deserialize<SpriteFontData>(js);

        if (data?.GlyphRects is null || data.CroppingRects is null ||
            data.CharMap is null || data.Kerning is null)
            throw new ContentLoadException(
                $"Malformed spritefont sidecar '{assetName}.spritefont.json'");

        Texture2D atlas = GetTexture(assetName + "_atlas");

        var glyphs = new List<Rectangle>(data.GlyphRects.Length);
        foreach (var r in data.GlyphRects) glyphs.Add(new Rectangle(r[0], r[1], r[2], r[3]));
        var cropping = new List<Rectangle>(data.CroppingRects.Length);
        foreach (var r in data.CroppingRects) cropping.Add(new Rectangle(r[0], r[1], r[2], r[3]));
        var chars = new List<char>(data.CharMap.Length);
        foreach (string c in data.CharMap)
            chars.Add(c[0]);
        var kerning = new List<Vector3>(data.Kerning.Length);
        foreach (var k in data.Kerning) kerning.Add(new Vector3(k[0], k[1], k[2]));

        char? defaultChar = null;
        if (!string.IsNullOrEmpty(data.DefaultCharacter))
            defaultChar = data.DefaultCharacter![0];

        return new SpriteFont(atlas, glyphs, cropping, chars,
                              data.LineSpacing, data.Spacing, kerning, defaultChar);
    }

    public override void Unload()
    {
        lock (_gate)
        {
            foreach (IDisposable d in _owned) d.Dispose();
            _owned.Clear();
            _cache.Clear();
        }
        _prefetched.Clear();
        base.Unload();
    }

    // ---- sidecar JSON model (matches tools/export_xnb_textures.py) ----------
    private sealed class SpriteFontData
    {
        [JsonPropertyName("glyph_rects")]    public int[][]? GlyphRects { get; set; }
        [JsonPropertyName("cropping_rects")] public int[][]? CroppingRects { get; set; }
        [JsonPropertyName("char_map")]       public string[]? CharMap { get; set; }
        [JsonPropertyName("line_spacing")]   public int LineSpacing { get; set; }
        [JsonPropertyName("spacing")]        public float Spacing { get; set; }
        [JsonPropertyName("kerning")]        public float[][]? Kerning { get; set; }
        [JsonPropertyName("default_character")] public string? DefaultCharacter { get; set; }
    }
}
