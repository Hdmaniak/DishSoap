# Comic / cutscene rendering — code path, root cause, fix

> **UPDATE 2026-10-02 (Final Consolidation Lead):** the §4 primary fix is now
> **APPLIED AND VERIFIED ON DEVICE**. `DishwasherContentManager` is wired in
> `Game1.LoadContent` and all 124 `BlendState.AlphaBlend` sites are
> `NonPremultiplied`. Measured comic backdrop band L **94.6 → 45.2** (reference
> `f_0002` = 34.0). See `android/notes/final-consolidation.md` and
> `<dev-notes>/proof/final/comic-before-after-ref.png`.

**Author:** Comic/Cutscene Rendering Specialist (subagent)
**Date:** 2026-10-02
**Device:** `<device-serial>` (adb `$ADB`), app `com.recomp.dishwasher`
**Scope:** read-only `managed/decompiled/game`; edits confined to `<dev-workspace>/` and
`android/notes/`. No read-only tree, no `MatrixTransform`, no `fade` fix touched.

**Status:** analysis + capture complete. **Build/install DEFERRED** — another agent
was building/installing throughout (`dotnet` MSBuild nodes running; the installed APK
changed from `…OC1JFt…` → `…sv7NAx…` and the local APK was rebuilt at 12:00 and again
at 12:09). The fix is supplied as an exact patch to apply afterwards.

---

## 0. TL;DR

1. **The comic cutscene is the `Strip.Draw` path, not `comicblur`.** After you choose
   Play/difficulty the comic plays **inside gameplay** (`gameMode == 0`): the map script
   sets `map.comicSwitch`, `Game1.Update` does `strip.playing = true` and `Game1.Draw`
   calls `Strip.Draw` **overload A** (`Strip.cs:718`), followed by the SPEED UP / SKIP
   buttons. The `comicBlurEffect` overload (`Strip.cs:764`) is behind `case 2`
   (`Game1.cs:4297`) and **`gameMode` is never set to 2 anywhere** — it is dead code.
2. **The panels load and draw correctly.** `gfx/panels/panel1.png` and the whole
   `gfx/*` set are byte-identical to the decoded original XNB (`tools/export_xnb_textures.py`,
   diff bbox `None`, mean channel diff `0.0`). Panel geometry, rotation, borders and the
   yellow speech bubbles all render. This is **not** a missing-texture or atlas-region bug.
3. **The rectangles come from the strip's full-screen `spritesTex` quads** — the "paper"
   quad at `Strip.cs:749` and the black backdrop quads at `:754` / `:760`, all stretched
   over `screenSize`. At mid-strip they render **~2.5× too bright** (see §2/§3), so the
   cutscene reads as a pale, washed rectangle instead of the reference's near-black grunge.
4. **Root cause (systemic, affects menu + play too):** XNA-3.0
   `SpriteBlendMode.AlphaBlend` was mapped to MonoGame `BlendState.AlphaBlend`, which is
   **premultiplied** (`ColorSourceBlend = One`, `ColorDestinationBlend = InverseSourceAlpha`).
   XNA 3.0 `SpriteBlendMode.AlphaBlend` is **straight** (`SourceAlpha / InverseSourceAlpha`).
   MonoGame's stock `ContentManager` also **premultiplies** every `gfx/*.png` on load. The
   net effect: for any draw whose tint alpha `< 1`, the source RGB is **not** attenuated by
   the tint alpha, so low-alpha quads (paper, coloured cursors, red tutorial boxes) become
   hard-edged bright/coloured rectangles instead of subtle overlays.
5. **Fix:** load `gfx` PNGs **straight** (the already-written
   `tools/csharp/DishwasherContentManager.cs`) and map
   `BlendState.AlphaBlend → BlendState.NonPremultiplied` (124 sites). Exact patch in §4
   and `<dev-notes>/apply-comic-cutscene-fix.sh`. **Not applied/verified** because of the
   build contention (a faithful after-shot requires the rebuild).

---

## 1. Comic / cutscene code-path map (port `file:line`)

**a) Trigger (gameplay script)**
- `Game1.cs:1935` `if (gameMode == 0 && map.comicSwitch != "")` → `strip.path = map.comicSwitch`
  (`:1938`), `strip.Read()` (`:1939`), `strip.Reset()` (`:1941`), `strip.playing = true` (`:1942`).
- `Globals.bloodFrame` is driven by `Strip.Update(Character[])` (`Strip.cs:458`).

**b) Draw — `Game1.Draw`, gameplay branch (`case 0`)**
- `Game1.cs:4283` `spriteMan.DrawHud(... strip.playing ...)` (HUD, drawn *under* the comic).
- `Game1.cs:4286` → **`strip.Draw(sprite, bubbleTex, panelTex, text, comicTex, base.Content, panelTexLoaded, spriteTex)`** ← **the comic**
- `Game1.cs:4287` `Globals.drawButtons(... speedUpStr, skipStr)` ← green SPEED UP / red SKIP.
- (`Game1.cs:4297` is the dead `case 2` overload with `comicBlurEffect`.)

**c) `Strip.Draw` overload A — `Strip.cs:718–762` — the draw calls that make the rectangles**
| line | call | what it is |
|---|---|---|
| `Strip.cs:724` | `Content.Load<Texture2D>("gfx/panels/panel" + (panel+1))` | lazy panel load (works) |
| `Strip.cs:737` | `sprite.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend)` | **wrong blend** (premultiplied) |
| `Strip.cs:749` | `sprite.Draw(spritesTex, fullscreen, (649,13,54,38), (1, 0.92, 0.82, num*0.35))` | **full-screen "paper" quad → the big rectangle** |
| `Strip.cs:750–753` | `splotch[i].Draw(...)` (512 black grunge blobs) | grunge |
| `Strip.cs:754` | `sprite.Draw(spritesTex, fullscreen, (768,192,128,128), (0,0,0, num*0.95))` | full-screen black backdrop quad |
| `Strip.cs:755` | `sprite.Draw(panelTex[panel], …scale 1.5, color (0,0,0,num*0.25))` | panel drop-shadow |
| `Strip.cs:756` | `sprite.Draw(panelTex[panel], …scale 1.5, color (1,1,1,num))` | **the comic panel** |
| `Strip.cs:758` | `drawBubbles(...)` | speech bubbles |
| `Strip.cs:759–761` | 2nd `spritesTex (768,192,128,128)` fullscreen black quad | extra darkening |

**d) Bubbles — `Strip.drawBubbles` (`Strip.cs:823`) / `drawBubble` (`Strip.cs:1009`)**
- Bubble colours at `Strip.cs:1011-1018` (`0 white / 1 yellow / 2 pale-yellow / 3 black`);
  bubble quads at `Strip.cs:1054-1055`. Bubbles render correctly.

**e) Same "panel" assets used by the boot intro (`gameMode == 3`)**
- `Game1.cs:3636` `intro.Draw(text, jtextTex, sprite, spriteTex, panelTex)` →
  `Intro.cs:326` and `Intro.cs:336` draw `panelTex[num4]` with **`SpriteBlendMode.Additive`**
  (the SKA / JAMES E SILVA / DISHWASHER / DEAD SAMURAI title cards). Additive + premultiplied
  texture also double-applies alpha; the systemic fix covers it.

**f) `comicblur` effect** — loaded (`Game1.cs:565`) but only bound in the dead `case 2`
overload (`Strip.cs:788-797`). It is **not** on the shipping comic path; the re-authored
`comicblur.fx` is therefore not the cause here.

---

## 2. Our-port vs reference

| figure | contents |
|---|---|
| `<dev-notes>/proof/render/comic/ref-vs-port-comic-midstrip.png` | **reference f_0002 vs our mid-strip port frame, side-by-side** |
| `.../comic/mid/strip01..12.png` | mid-strip port captures (no input, so the strip is at full opacity) |
| `.../comic/mid/007.png` | the frame where the comic was detected |
| `.../comic/cur/021.png`, `.../cur/023.png` | comic on the 12:02-installed build |
| `.../comic/scan/s15.png`, `s17.png`, `s20.png` | earlier (11:03) build comic frames (fade-out) |
| `.../comic/ref-vs-port-comic.png` | reference vs port (fade-out frame) |
| `.../comic/modes/m0-menu.png` | in-game INVENTORY screen: red rectangle artifact (same class) |

**Measured (1080×750 game band, rows 336–1085):**

| frame | band mean RGB | background patch RGB |
|---|---|---|
| reference `f_0002` | **(36.4, 34.9, 23.0)** | ~(40, 40, 35) |
| port mid-strip `strip06` (`num == 1`) | **(97.3, 90.8, 84.5)** | ~(75–88, 70–82, 62–83) |
| port fade-out `s15` (`num < 1`) | (38.6, 36.3, 34.2) | ~(6–21) |

The port's comic background is **~2–2.5× brighter than the reference at full opacity** and
**collapses to dark when `num < 1`** — i.e. the excess brightness is proportional to the
alpha-tint being (wrongly) ignored.

### Enumerated visual differences (mid-strip, same scene)
1. **Full-screen paper/backdrop rectangle** is the dominant difference: port is a pale
   grey-green noise sheet; reference is near-black grunge. This is the rectangle behind the
   art.
2. **Panel mid-tones lifted:** the source panel art is the same bytes, but the bright paper
   behind the panel's semi-transparent pixels + the alpha error lifts the panel from the
   reference's dark blue/black to a pale blue.
3. **Blue cast:** the source `panel1.png` genuinely contains blue-lavender tones
   (RGB buckets `(192,192,224)`, `(160,160,224)`), so part of the cool cast is the art; the
   reference reads cooler-neutral because the backdrop is dark. Once the backdrop is fixed
   the cast drops substantially. (No channel-swap bug: the PNGs are byte-exact to the XNB.)
4. **Panel border** reads yellow (the source border is yellow, bucket `(224,224,0)`);
   in the reference it reads light grey against the black backdrop. Secondary.
5. **Not broken** (verified present and correct): panel geometry (tilted, rotation `0.2`
   from `data/strips/strip1.zdx`), yellow bubbles with black text, white spiky bubbles,
   green SPEED UP / red SKIP, the `HOW DID I END UP BACK HERE?` … `THEY KILLED ME ONCE`
   caption set (identical text to the reference).

---

## 3. Root cause, with evidence

**Mechanism**
- Decompiled `MonoGame.Framework.dll` (3.8.5.1, Android):
  `BlendState.AlphaBlend = new BlendState("BlendState.AlphaBlend", Blend.One, Blend.InverseSourceAlpha)`
  → **premultiplied**;
  `BlendState.NonPremultiplied = new BlendState(…, Blend.SourceAlpha, Blend.InverseSourceAlpha)`
  → **straight**.
- `ContentManager.LoadTexture2DFromImageFile` (the PNG fallback that the port relies on)
  calls `Texture2D.FromStream(gd, stream, DefaultColorProcessors.PremultiplyAlpha)` →
  every `gfx/*.png` arrives **premultiplied**.
- The shipped assembly `/tmp/.../assemblies/com.recomp.dishwasher.dll` uses
  `BlendState.AlphaBlend` in `Strip.Draw` (and 123 other sites); there is **no**
  `NonPremultiplied` anywhere in the project.
- XNA 3.0's `SpriteBlendMode.AlphaBlend` was `SourceAlpha / InverseSourceAlpha`
  (**straight**).

For a premultiplied texture `T` and a straight tint `C = (rgb, a)`:
- MonoGame `BlendState.AlphaBlend`: `out = T.rgb·C.rgb + dst·(1 − T.a·a)` — **`C.a` never
  scales the source**.
- Correct XNA 3.0 / `NonPremultiplied` on a straight texture:
  `out = T.rgb·C.rgb·(T.a·a) + dst·(1 − T.a·a)`.

So every draw with `a < 1` is too bright by `1/a` (up to ~3× for the comic's `0.35` paper
quad). `Strip.cs:749` is exactly such a quad — full-screen, RGB `(1,0.92,0.82)`, alpha
`0.35` — drawn from a small noise tile scaled to 1080×750: a large, hard-edged, over-bright
rectangle. The same signature occurs in the menu/inventory/HUD and gameplay:
`MainMenu.cs:3508` and `Game1.cs:3620` (full-screen `spritesTex` quads),
`Overlay.cs:178-179,253-258` (coloured boxes), `Game1.cs:3619-3623`, `HUD.cs:329-398`,
`Pickups.cs`, etc. This is the user's "rectangular shapes behind some objects, in the menu
and during play".

**Why the earlier notes missed it:** `android/notes/colour-grade-fix.md §1.2` states the
game uses "`BlendState.AlphaBlend` (straight alpha: src.rgb·src.a + dst·(1−src.a))". That
label is incorrect — `BlendState.AlphaBlend` is `One/InvSrcAlpha` (verified above).
`<dev-notes>/content-integration.md §3` correctly identifies that the port needs a
straight-alpha `ContentManager` **and** the `AlphaBlend → NonPremultiplied` mapping, but
that wiring was never landed (no `ContentManager` subclass exists in the project; the
blend map still says `AlphaBlend`).

**Decisive measurements**
- `strip06` (`num == 1`): band L 97 vs reference 36 → the paper is at full strength.
- `s15` (`num < 1`, fade-out): band L 39 vs reference 36 → when the tint alpha is small,
  the error vanishes. The artifact scales with the tint alpha, exactly as the premultiplied
  tint model predicts and a texture/atlas bug would not.
- Textures are byte-exact → conversion exonerated.
- `comicblur` is unreachable → effect re-author exonerated for this path.

---

## 4. Fix

### 4.1 Primary (correct, systemic) — straight-alpha textures + `NonPremultiplied`
This is the design already written up in `<dev-notes>/content-integration.md`.

1. **Add the loader.** Copy the ready file
   `tools/csharp/DishwasherContentManager.cs` → `Dishwasher/DishwasherContentManager.cs`
   (namespace `Dishwasher.Content`; loads `Texture2D` via straight `Texture2D.FromStream`,
   SpriteFont from atlas+json, everything else falls through to stock `ContentManager`).
2. **Wire it in `Game1.LoadContent()` (`Game1.cs:865`)**, before the first
   `base.Content.Load` (the `xblaTex` load at `:880`):
   ```csharp
   // XNA 3.0 SpriteBlendMode.AlphaBlend is straight alpha; load textures straight.
   Content = new Dishwasher.Content.DishwasherContentManager(Services, graphics.GraphicsDevice, ".");
   ```
   (`Game1`'s `Content` setter is `public` — verified in `Microsoft.Xna.Framework.Game`.
   Passing `"."` keeps the existing `TitleContainer` resolution of `assets/gfx/...` and
   `assets/fx/*.xnb`.)
3. **Straight-alpha blend map** (124 sites, mechanical):
   ```
   GameSource/**/*.cs:  BlendState.AlphaBlend  ->  BlendState.NonPremultiplied
   ```
   `BlendState.Additive` and `BlendState.Opaque` are unchanged. For `a == 1` tints this is
   **pixel-identical** to the current code (both reduce to `T·a` on the source), so there
   is no regression risk for opaque sprites; only the `a < 1` quads change (they get darker
   / correct). `MatrixTransform` and the `fade` fix are untouched.

A one-shot, non-applied script for the three steps:
`<dev-notes>/apply-comic-cutscene-fix.sh`.

### 4.2 Minimal comic-only fallback (if the systemic change is owned by the
"rectangle artifact" agent and must not be duplicated)
The stock loader leaves textures **premultiplied**, so the equivalent local fix is to
premultiply the tint on the strip's `a < 1` draws (`rgb ← rgb·a`):
- `Strip.cs:749` `(1, 0.92, 0.82, num*0.35)` → `(num*0.35, 0.92·num*0.35, 0.82·num*0.35, num*0.35)`
- `Strip.cs:756` `(1, 1, 1, num)` → `(num, num, num, num)`
- `Strip.cs:764-816` (dead overload, optional) and `drawBubble` colours at
  `Strip.cs:1011-1020` likewise.
This fixes the comic but **not** the menu/inventory/play rectangles; the primary fix is
preferred.

### 4.3 Expected result (arithmetic, to verify after rebuild)
`Strip.cs:749` paper, tint alpha `0.35`, texture alpha `0.869`, then the `:754` black quad
(alpha `0.455·0.95`):
- current: `0.869 → (×0.568) ≈ 126` (matches the observed ~80–100 band with splotches);
- fixed: `0.869·0.35 = 0.304 → (×0.568) ≈ 44` (matches the reference ~36–46).

So the comic backdrop should drop from band L ≈ 97 to ≈ 45, matching `f_0002`.

---

## 5. Build / contention log

- Start of task: no `dotnet`/`ninja`/`mgcb` running.
- Mid-session the installed APK changed `…OC1JFt…` → `…sv7NAx…`; `GameSource/projectDish/Game1.cs`
  and `Platform/RenderDiagnostics.cs` were modified at 12:02; the local APK was rebuilt at
  12:02 and again at **12:09:45**, with MSBuild node processes running.
- Per the task instruction ("if one is running, STOP after producing the fix plan"), **no
  rebuild/install was performed and no project source was edited** — only this note, the
  patch script, and the proof captures were written.
- The fix in §4 should be applied once the concurrent build finishes, then captured at the
  same scene (`strip06`, mid-strip, `num == 1`) for the required before/after pair. The
  before frame is `<dev-notes>/proof/render/comic/mid/strip06.png`.

---

## 6. Trees touched

- **Written:** `android/notes/comic-cutscene.md` (this file),
  `<dev-notes>/apply-comic-cutscene-fix.sh`,
  `<dev-notes>/proof/render/comic/**` (captures + `ref-vs-port-comic*.png`).
- **Not touched:** `managed/decompiled`, `assets-clean`, `port`, `rexglue-sdk*`,
  `android-app`; no `GameSource`/`Content`/`Assets` edit, no APK build, no device install.
