# Content integration (textures + SpriteFont) for the MonoGame/Android port

**Status:** proposal + compiled prototype. The `src/Dishwasher/` project is being
actively edited by another agent (project files, `Content/fx/*.fx` changed during this
task), so **no project file was modified**. The loader below is provided as a ready-to-
drop file plus the exact wiring steps. A reference copy lives at
`tools/csharp/DishwasherContentManager.cs`; it **compiles clean** against
`MonoGame.Framework 3.8.5.1` (`Build succeeded. 0 Warning(s) 0 Error(s)`).

The exported content itself is already in place: `Content/gfx/**` (125 texture PNGs),
`Content/gfx/*_atlas.png` and `Content/gfx/*.spritefont.json` (3 fonts). See
`<dev-workspace>/android/notes/content-conversion.md` for the decode details.

---

## 1. What this buys you

The ported game calls `Content.Load<T>(...)` ~59 times
(`38 Texture2D`, `19 Effect`, `2 SpriteFont`, `2 CharDef`). With this subclass:

* **`Texture2D`** loads straight from `Content/<asset>.png` with
  `Texture2D.FromStream` → preserves the original **straight / non-premultiplied**
  alpha (XNA 3.0 `SpriteBlendMode.AlphaBlend` = `SourceAlpha/InvSourceAlpha`).
* **`SpriteFont`** is rebuilt from `Content/<asset>_atlas.png` +
  `Content/<asset>.spritefont.json` using MonoGame's **public** `SpriteFont`
  constructor → exact original glyphs, cropping, char map, line spacing, kerning.
* **Everything else** (`Effect`, `CharDef`, future types) falls through to stock
  `ContentManager`, which reads the `.xnb` that MGCB produces (shaders from the shader
  agent; CharDef is a separate custom-reader workstream).

## 2. Why not just use stock `ContentManager`?

MonoGame's stock manager *does* already fall back to `Content/<asset>.png` when the
`.xnb` is missing, **but** that path calls
`Texture2D.FromStream(gd, stream, DefaultColorProcessors.PremultiplyAlpha)`
(`ContentManager.LoadTexture2DFromImageFile`). Premultiplying straight-alpha art while
the game blends non-premultiplied produces dark halos. This subclass is therefore
deliberately small: it only changes *how* the PNG is decoded (no premultiply) and adds
the SpriteFont sidecar path. If you instead let MGCB build textures, disable
premultiplication (`PremultiplyAlpha = false`) and keep the same blend mapping.

## 3. Drop-in file

Create `DishwasherContentManager.cs` in the project (same namespace is fine):

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        if (_cache.TryGetValue(key, out object? cached) && cached is T hit)
            return hit;

        object result;
        if (typeof(T) == typeof(Texture2D))
            result = LoadPng(key);
        else if (typeof(T) == typeof(SpriteFont))
            result = LoadSpriteFont(key);
        else
            result = base.Load<T>(assetName);   // Effect, CharDef, ...

        _cache[key] = result;
        return (T)result;
    }

    private Stream OpenContent(string relativePath) =>
        TitleContainer.OpenStream(Path.Combine(RootDirectory, relativePath));

    private Texture2D LoadPng(string assetName)
    {
        using Stream s = OpenContent(assetName + ".png");
        Texture2D tex = Texture2D.FromStream(_graphics, s);   // straight alpha
        _owned.Add(tex);
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

        Texture2D atlas = LoadPng(assetName + "_atlas");

        var glyphs = new List<Rectangle>(data.GlyphRects.Length);
        foreach (var r in data.GlyphRects) glyphs.Add(new Rectangle(r[0], r[1], r[2], r[3]));
        var cropping = new List<Rectangle>(data.CroppingRects.Length);
        foreach (var r in data.CroppingRects) cropping.Add(new Rectangle(r[0], r[1], r[2], r[3]));
        var chars = new List<char>(data.CharMap.Length);
        foreach (string c in data.CharMap)
            chars.Add(c.Length == 1 ? c[0] : char.ConvertFromUtf32(char.ConvertToUtf32(c, 0))[0]);
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
        foreach (IDisposable d in _owned) d.Dispose();
        _owned.Clear();
        _cache.Clear();
        base.Unload();
    }

    private sealed class SpriteFontData
    {
        [JsonPropertyName("glyph_rects")]        public int[][]?   GlyphRects { get; set; }
        [JsonPropertyName("cropping_rects")]     public int[][]?   CroppingRects { get; set; }
        [JsonPropertyName("char_map")]           public string[]?  CharMap { get; set; }
        [JsonPropertyName("line_spacing")]       public int        LineSpacing { get; set; }
        [JsonPropertyName("spacing")]            public float      Spacing { get; set; }
        [JsonPropertyName("kerning")]            public float[][]? Kerning { get; set; }
        [JsonPropertyName("default_character")]  public string?    DefaultCharacter { get; set; }
    }
}
```

## 4. Wiring steps (for whoever owns `Dishwasher.csproj` / `Game1.cs`)

1. **Package the raw assets.** Add to the project:
   ```xml
   <ItemGroup>
     <AndroidAsset Include="Content\**\*.png" />
     <AndroidAsset Include="Content\**\*.json" />
   </ItemGroup>
   ```
   (Alternatively add `/copy:` entries to `Content.mgcb`.) This puts them at
   `assets/Content/...`, the same root `TitleContainer.OpenStream` resolves.

2. **Use the subclass as the game's content manager.** In `Game1`, replace the base
   `Content` usage:
   ```csharp
   // after graphics device exists (e.g. in Initialize/LoadContent):
   Content = new DishwasherContentManager(Services, GraphicsDevice, "Content");
   ```
   The ported game's `base.Content.Load<Texture2D>("gfx/text")` then hits
   `Content/gfx/text.png`; `Load<SpriteFont>("gfx/SpriteFont1_JPN")` hits the atlas +
   sidecar.

3. **Translate the blend modes** (XNA 3.0 → MonoGame) — required, independently of the
   loader, or straight-alpha art renders wrong:

   | XNA 3.0 | MonoGame |
   |---|---|
   | `SpriteBlendMode.AlphaBlend` | `BlendState.NonPremultiplied` |
   | `SpriteBlendMode.Additive` | `BlendState.Additive` |
   | `SpriteBlendMode.None` | `BlendState.Opaque` |

   XNA 3.0 source: `SpriteBatch.cs:626` sets `SourceBlend=SourceAlpha,
   DestinationBlend=InverseSourceAlpha` for `AlphaBlend` — i.e. non-premultiplied.

4. **Do not set `PremultiplyAlpha` on textures.** The PNGs are straight alpha and match
   the original 360 art.

## 5. Sidecar schemas

`Content/<font>.spritefont.json` (produced by `tools/export_xnb_textures.py`):

```json
{
  "glyph_rects":    [[x,y,w,h], ...],   // N entries
  "cropping_rects": [[x,y,w,h], ...],   // N
  "char_map":       [" ", "!", ...],    // N UTF-8 chars
  "line_spacing":   26,
  "spacing":        0.0,
  "kerning":        [[left,width,right], ...], // N Vector3
  "default_character": null,             // XNA 3.0 has no default char
  "atlas_width": 256, "atlas_height": 256, "atlas": "gfx/Arials_atlas.png"
}
```

Atlas PNGs: `Arials` 256×256, `SpriteFont1_JPN` 1024×2048, `SpriteFont1_CHT` 2048×1024.

## 6. Fall-through types (not owned by this task)

* **`Effect`** (`fx/*`): `base.Load<Effect>("fx/poster")` reads
  `Content/fx/poster.xnb` built by MGCB from the shader agent's HLSL. The 360 effect
  bytecode is unusable and was skipped by the exporter.
* **`CharDef`** (`data/chars/*`): no `.xnb` exist; the data is `data/chars/*.dqx`
  (proprietary). Needs a custom `ContentTypeReader`/pipeline (or the ported game's own
  reader) — separate workstream. It falls through to `base.Load<T>` for now.

## 7. Testing status

* C# prototype: **compiles** against `MonoGame.Framework 3.8.5.1` (net8.0 reference
  from the MGCB tool package): `Build succeeded, 0 errors`.
* Exported PNGs: 125/125 dimension-verified; fonts 3/3 decode to EOF.
* **Not yet run on a device** — no MonoGame `GraphicsDevice` was driven in this task.
  First on-device check should (a) load `gfx/sprites`, `gfx/dish`, `gfx/panels/panel1`
  and confirm alpha edges, and (b) load `gfx/SpriteFont1_JPN` and draw a mixed
  ASCII/kana string.
