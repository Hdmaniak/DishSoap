# Final consolidation — straight-alpha (BUG 2) landed, instrumentation tamed

**Author:** Final Consolidation Lead (subagent)
**Date:** 2026-10-02
**Device:** `<device-serial>` (`adb $ADB`), app `com.recomp.dishwasher`
**Scope:** edits confined to `src/Dishwasher/` and this notes tree.
Read-only trees (`managed/decompiled`, `assets-clean`, `port`, `rexglue-sdk*`,
`android-app`) were **not** modified.

**Result in one line:** XNA 3.0 straight-alpha is now landed end-to-end — PNGs load
straight **and** all 124 `BlendState.AlphaBlend` sites are `BlendState.NonPremultiplied`
— the comic "paper" backdrop dropped from **band L 94.6 → 45.2** (reference **34.0**),
the full-screen red tutorial band is gone, BUG 1 / `fade` / `MatrixTransform` are
untouched, and the accumulated diagnostics are inert by default.

---

## 0. TL;DR

| item | result |
|---|---|
| BUG 1 (DXT5 alpha) | **intact** — `Assets/gfx/head1.png` md5 `e4374dc854a34d76cdee0292826cc5f9`, no dither rectangles in gameplay |
| BUG 2 (straight alpha) | **landed** — 1 new file, 1 wiring line, 124 blend sites, 0 `BlendState.AlphaBlend` left |
| comic backdrop band L | `94.6` (before) → **`45.2`** (after); reference `f_0002` = `34.0`; predicted ~`45` |
| red tutorial band (gameplay) | full-width hard-red (56 305 hard-red px) → **gone** (2 489 hard-red px = the HUD health bar only) |
| `fade` fix (`fade.fx`) | untouched (mtime 10:59, pre-session); runtime overlay now uses the straight blend = correct |
| `MatrixTransform` | untouched — all 21 `Content/fx/*.fx` mtimes predate this session |
| lense sampler-bind fix | preserved (`IsLenseFix == true` whenever the harness is disabled) |
| InputDiagnostics log spam | `250` lines / ~2 min → **0**; master switch off |
| build | `0 Error(s)`; installs and reaches gameplay; no FATAL/exception in logcat |

---

## 1. BUG 2 — the straight-alpha patch

Root cause (recap, full analysis in `comic-cutscene.md`): XNA 3.0
`SpriteBlendMode.AlphaBlend` is **straight** (`SourceBlend=SourceAlpha`,
`DestinationBlend=InverseSourceAlpha`). The port mapped it to MonoGame
`BlendState.AlphaBlend`, which is **premultiplied** (`One / InverseSourceAlpha`), and
fed it **premultiplied** PNGs. For any tint with `a < 1` the source RGB was not scaled
by the tint alpha, so low-alpha full-screen quads (comic paper, tutorial band, menu
"dish" wash, HUD) rendered as hard, over-bright rectangles.

### 1.1 New file — straight-alpha content manager

`src/Dishwasher/DishwasherContentManager.cs` (namespace `Dishwasher.Content`,
`#nullable enable`). It overrides `ContentManager.Load<T>`:

* **`Texture2D`** → `Texture2D.FromStream(gd, <root>/<asset>.png)`. The 2-arg overload
  uses `DefaultColorProcessors.ZeroTransparentPixels` — i.e. **straight alpha, no
  premultiply** (verified against the decompiled MonoGame 3.8.5.1 Android
  `ContentManager.LoadTexture2DFromImageFile`, which is the only path that passes
  `PremultiplyAlpha`). Only fully-transparent texels get RGB zeroed, which cannot
  contribute under any of the blend modes below.
* **`SpriteFont`** → `<asset>_atlas.png` + `<asset>.spritefont.json` via MonoGame's
  **public** `SpriteFont` ctor (CJK locales only; no TTF / MGCB).
* **everything else** (`Effect`, `CharDef`) → stock `base.Load<T>` (`.xnb` from MGCB).

`OpenContent` avoids a `./` prefix for `RootDirectory == "."` (this port's root) since
the Android `AssetManager` path is sensitive to it.

### 1.2 Wiring — `Game1.LoadContent()` (`GameSource/projectDish/Game1.cs:874`)

```csharp
TraceWriteLine("Loading Graphics content");
// XNA 3.0 SpriteBlendMode.AlphaBlend is straight alpha; load the PNGs
// straight (no premultiply) so they agree with the
// BlendState.NonPremultiplied map used throughout GameSource. ...
Content = new Dishwasher.Content.DishwasherContentManager(Services, graphics.GraphicsDevice, ".");
```

Placed before the first `base.Content.Load<Texture2D>` (`gfx/xbla`, `:889`). The
`ThreadLoader` captured in `Initialize` only loads `CharDef`, so it is unaffected. The
loader boot log confirms the new path: `[trace] Loaded XBLA texture`.

### 1.3 Blend map — 124 sites

Mechanical replacement `BlendState.AlphaBlend` → `BlendState.NonPremultiplied`
(case-sensitive, whole-token), 20 files:

| file | sites | file | sites |
|---|---:|---|---:|
| `MainMenu.cs` | 34 | `GameRender.cs` | 2 |
| `HUD.cs` | 27 | `ComboList.cs` | 2 |
| `Game1.cs` | 23 | `Achievements.cs` | 1 |
| `Text.cs` | 9 | `BloodStreak.cs` | 1 |
| `Globals.cs` | 8 | `Character.cs` | 1 |
| `SpriteManager.cs` | 5 | `Credits.cs` | 1 |
| `Strip.cs` | 4 | `GuitarSolo.cs` | 1 |
| `Map.cs` | 1 | `Intro.cs` | 1 |
| `Overlay.cs` | 1 | `Pickups.cs` | 1 |
| `Practice.cs` | 1 | **total** | **124** |

`grep -r BlendState.AlphaBlend GameSource/` = **0**;
`grep -r BlendState.NonPremultiplied GameSource/ | grep -v //` = **124**.

---

## 2. Interaction reasoning (why the loader and blend map must ship together)

SpriteBatch multiplies the tint `C` into the sampled texel `T`; the resulting source is
`src.rgb = T.rgb·C.rgb`, `src.a = T.a·C.a`, then the blend state is applied.

**Straight texture + `NonPremultiplied`** — correct XNA 3.0:
`out.rgb = T.rgb·C.rgb·(T.a·C.a) + dst·(1 − T.a·C.a)`

For a tint alpha `a = C.a`:

| loader | blend | `a = 1` source term | `a < 1` source term |
|---|---|---|---|
| premultiplied (old) | `AlphaBlend` (old) | `T.rgb·T.a` | `T.rgb·T.a` (alpha ignored → too bright by `1/a`) |
| straight | `AlphaBlend` | `T.rgb·T.a` | `T.rgb·T.a` (still ignores tint alpha) |
| premultiplied | `NonPremultiplied` | `T.rgb·T.a²` | `T.rgb·T.a²·a` (double alpha → too dark) |
| **straight** | **`NonPremultiplied`** | **`T.rgb·T.a`** | **`T.rgb·T.a·a` ← correct** |

So neither change alone is right: straight textures with the premultiplied blend are
too bright, premultiplied textures with the straight blend are too dark. Together they
are exactly the original pipeline for **every** alpha. For `a == 1` (opaque sprites) the
four expressions are identical, so opaque art is pixel-for-pixel unchanged.

### 2.1 Blend-mode audit — what else the game uses

`grep` over `GameSource/` finds exactly three MonoGame blend states:
`NonPremultiplied` (124), `Additive` (19), `Opaque` (22). No custom `BlendState` /
`Blend` / `BlendFunction` overrides exist.

**`Additive`** — decompiled `BlendState.Additive` is `SourceAlpha / One`
(`AlphaSourceBlend=SourceAlpha, AlphaDest=One`), which is the same mapping
`content-integration.md §3` prescribes for XNA `SpriteBlendMode.Additive`. Under
straight textures the source term becomes `T.rgb·C.rgb·(T.a·C.a)` — the correct XNA
result. **Before** the fix, premultiplied textures double-multiplied alpha
(`T.rgb·T.a²·C.a·C.rgb`), so additive glows/particles were too dark. The fix therefore
**corrects** additive draws; opaque-tinted (`C.a = 1`) additive is unchanged.

**`Opaque`** — `One / Zero`; `out.rgb = T.rgb·C.rgb`. Before, `T.rgb` was premultiplied
(`T.rgb·T.a`), so semi-transparent texels drawn opaque were darkened; after they carry
their true colour, which is what the straight XNA originals did. The 22 `Opaque` sites
are almost all **full-screen render-target blits** (`rTarg`, `lenseTarg`, `mapTarg`,
`bloomTarg`) and the menu/level RT present, where the RT alpha is 1 — so they are
unchanged. Visual regression check below confirms no `Opaque`-path breakage.

**Render targets.** RTs are written with the same blend map the original XNA 3.0 game
used (`SourceAlpha / InverseSourceAlpha`), so the RT contents (RGB *and* alpha) now
match the 360 pipeline; the final composite/grad pass is `Opaque` and reads RGB only,
so it is unaffected. This is why the bloom/fade/lense overlays still look right in the
gameplay captures.

**No effect, technique, pass name, `MatrixTransform`, or texture was changed.** The
DXT5 alpha fix (BUG 1) lives in the exporter/PNG data and is orthogonal to blending.

---

## 3. Measurements

Method: full device screenshots are `1080×2400`; the game band is rows **336–1085**
(749 px). The reference YouTube frames are `640×360` and fill the frame, so the whole
image is the band. `band L` = `0.299·R̄ + 0.587·Ḡ + 0.114·B̄`.

### 3.1 Comic cutscene (mid-strip, full opacity), same scene

| frame | band mean RGB | band L |
|---|---|---|
| reference `f_0002` (ground truth) | (36.46, 34.93, 23.21) | **34.0** |
| port **before** (`before-comic-3.png`) | (100.18, 93.34, 86.33) | **94.6** |
| port **after** (`after-comic-2.png`) | (47.79, 44.86, 39.75) | **45.2** |
| analytic prediction (`comic-cutscene.md §4.3`) | — | ~44 |

The before/after comparison, with the reference on top, is
`<dev-notes>/proof/final/comic-before-after-ref.png`. The "paper" quad
(`Strip.cs:749`, tint `(1,0.92,0.82, num·0.35)`) is no longer a pale sheet: the
backdrop is near-black grunge, matching the reference. The black backdrop quads
(`:754`, `:760`, RGB=0) were already alpha-independent and expected to be unchanged.

### 3.2 Gameplay red band + HUD (same room, tutorial state)

Counting "hard red" pixels (`R>140, R−G>70, R−B>60`) in the band:

| frame | hard-red pixels (2-px sample) | location | top-left rect mean RGB |
|---|---:|---|---|
| before (`before-gameplay-2.png`) | **56 305** | full width x 74–1078, y 336–894 | (124.9, 21.7, 21.7) |
| after (`after-gameplay-2.png`) | **2 489** | x 74–298, y 392–440 (health bar only) | (91.9, 14.7, 14.7) |

The full-screen bright-red tutorial band is **gone**; the only remaining hard-red area
is the top-left HUD health bar (also correctly ~26 % darker, i.e. no longer
alpha-ignored). Comparison: `<dev-notes>/proof/final/gameplay-before-after.png`.

### 3.3 Menu legibility

| screen (same menu level) | before | after |
|---|---|---|
| main menu (`after-menu-stable-2.png`) | blown-out pink/white wash (dish quad over-bright) | muted ink/paper grey, text white with black outline |
| PLAY STORY submenu | before `before-nav-3.png` band L **140.4** | after `after-menu-stable-3.png` band L **110.1** |

The before "dish" full-screen quad (`alpha 0.8`) had lost all highlight detail (a solid
white blob); the fix restores the underlying ink artwork. Text contrast is equal or
better. Comparison: `<dev-notes>/proof/final/menu-before-after.png`.

---

## 4. Regression status

* **BUG 1 (DXT5 alpha / rectangles): intact.** The runtime atlas is unchanged:
  `Assets/gfx/head1.png` md5 = `Content/gfx/head1.png` md5 =
  `e4374dc854a34d76cdee0292826cc5f9` (the value recorded in `rectangle-artifact.md`).
  Gameplay captures (`after-gameplay-2/3.png`) show clean silhouettes and room art —
  no checkerboard cell dither. The fix was in the exporter + PNG data, which this task
  did not touch.
* **`fade` fix: intact.** `Content/fx/fade.fx` (mtime `10:59:05`, before this session)
  and all other `.fx` are byte-untouched. The runtime fade overlay does use the new
  straight blend, which is the correct XNA behaviour (the shader itself is unchanged).
* **`MatrixTransform`: intact.** Present in the re-authored `.fx` sources; all 21
  `Content/fx/*.fx` mtimes predate the session and no `.fx`/effect/technique/pass was
  edited. `grep` for `MatrixTransform` shows only the pre-existing shader sources and
  their MGCB output.
* **Lense sampler-bind fix: intact.** `GameRender.drawLenseTarg`'s fixed bind order is
  the `RenderDiagnostics.IsLenseFix` path; with the harness disabled `Mode` reads `0`,
  so `IsLenseFix == true` and the shipping (fixed) order is always used. Gameplay
  renders correctly, not black.
* **Build/run:** `Build succeeded. 0 Error(s)`; installs and reaches gameplay; logcat
  has no `FATAL EXCEPTION`, `ContentLoadException`, or `DirectoryNotFound` for the app.

---

## 5. On-device screenshots

All under `<dev-notes>/proof/final/` (kept alongside this note reference):

| file | what |
|---|---|
| `comic-before-after-ref.png` | **reference / before / after** comic strip + band labels |
| `before-comic-1..4.png` | comic, BUG 2 present (band L ≈ 94) |
| `after-comic-1..5.png` | comic, fixed (band L ≈ 45) |
| `gameplay-before-after.png` | tutorial room, before/after |
| `before-gameplay-2.png`, `after-gameplay-2/3.png` | full-band gameplay frames |
| `menu-before-after.png` | PLAY STORY submenu before/after |
| `before-nav-3/5/7.png`, `after-menu-stable-2/3.png` | menu screens, before/after |

---

## 6. Instrumentation inventory (all inert by default)

Every hook is now gated by a single master switch. Nothing logs or changes rendering
unless the switch is flipped.

### 6.1 `Platform.InputDiagnostics.cs` — **OFF** (`Enabled = false`)

Guards at the top of `AttachView`, `LogAndroidDevices`, `Tick`, `LogKeyEvent`,
`LogMotionEvent`. `Tick`'s call site in `Game1.Update` is additionally guarded:
`if (InputDiagnostics.Enabled) InputDiagnostics.Tick(gameTime);`.

*Effect:* the 500 ms `GamePad.GetState`/`Keyboard.GetState` poll and its
`StringBuilder` allocation no longer run — logcat went `250 → 0` `[input] P0=` lines.
*Disable (already done):* leave `Enabled = false`. *Re-enable:* set `Enabled = true`.
*Remove entirely:* delete `Platform/InputDiagnostics.cs` and its 5 call sites in
`Activity1.cs` (`AttachView`, 2× `LogAndroidDevices`, `LogKeyEvent`, `LogMotionEvent`)
plus the guarded `Tick` call in `Game1.cs` — **keep** `AndroidInputBridge` (below).

### 6.2 `AndroidInputBridge.cs` — **NOT diagnostics; keep**

`InitializePads` / `RouteKey` / `RouteMotion` are the working gamepad input fix (how
`adb shell input keyevent` drives the game). They are not debug logging and must stay.

### 6.3 `Platform.RenderDiagnostics.cs` + `Game1`/`GameRender`/`Activity1` blocks — **OFF** (`Enabled = false`)

`Mode` is now a property that reads `0` whenever `Enabled == false`, so every
`Skip*`/`Bypass*` predicate is false, `IsLenseFix` is true, `Bump()` is a no-op, and the
volume keys are no longer swallowed. The mode-specific blocks in `Game1` (menu RT
display modes 40–43; final-composite experiments 1–9, 14–18) therefore never execute.
The harness remains fully usable by setting `Enabled = true`.

*Disable (already done):* leave `Enabled = false`. *Re-enable:* set `Enabled = true`,
then use VOLUME_UP/DOWN (only then are they intercepted in `Activity1.DispatchKeyEvent`).
*Remove entirely:* delete `Platform/RenderDiagnostics.cs`; replace each
`!RenderDiagnostics.SkipX` with the true branch; delete the TEMP blocks at
`Game1.cs:3857–3870` and `:4150–4249`, the `SkipNewBlood` check at `Game1.cs:5171`, the
volume-key block in `Activity1.cs`, and the `LastPath` writes in `GameRender.cs:83,93`.
**Preserve the lense sampler-bind fix** (`GameRender.cs:100` branch) by inlining the
`IsLenseFix == true` path.

Remaining cheap per-frame remnants even while disabled (documented, harmless):
`RenderDiagnostics.LastPath = "minimal"/"lense"` (two static string stores per frame)
and the `RenderDiagnostics.Mode` reads (a `bool`-guarded property returning constant 0).

### 6.4 Other

* `Platform/Log.cs`, `Game1.TraceWriteLine`: logging shim / boot-phase traces, not
  per-frame; **shipping**.
* `AndroidContentBootstrap.cs`: extracts APK assets and sets the working directory;
  **shipping**.
* `Platform/Shim_EffectCompat.cs` "RENDER DIAGNOSIS FIX" comment: this is a shipped
  effect-compat fix (`Effect.Begin/End` → `Apply()`), not a debug toggle.

---

## 7. Remaining visual issue (out of BUG 2 scope)

The comic **panel interiors still read blue-lavender**, while the YouTube reference
`f_0002` reads neutral grey/black. This is *not* the alpha bug: the panel PNG
(`Content/gfx/panels/panel1.png`) is genuinely light blue-lavender and is byte-exact to
its XNB, and the same cast is already noted in `comic-cutscene.md §2`. It looks like a
colour-grade/order difference on the comic path (the port draws the strip after the grad
composite; the 360 reference may have had it graded), plus the HUD health bar renders as
a plain bright red rectangle rather than the reference's rounded capsule. Both are
pre-existing and unrelated to straight alpha; they are the main remaining fidelity gaps
in the scenes captured here.

---

## 8. Build / deploy (verified this session)

```sh
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true        # Build succeeded, 0 Error(s)
adb -s <device-serial> install -r -d bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk
adb -s <device-serial> shell am force-stop com.recomp.dishwasher && adb -s <device-serial> logcat -c
adb -s <device-serial> shell am start -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1
# drive: adb shell input keyevent 96 (A)  -> menus -> PLAY STORY -> EASY ->
#        SELECT LEVEL (RIGHT to PLAY) -> A -> comic -> gameplay
```

`targetSdkVersion=35` preserved; only the API-34-vs-35 `XA1008` warning and the
pre-existing `SYSLIB0006` `Thread.Abort` warnings remain.

---

## 9. Files changed this task

* **new** `src/Dishwasher/DishwasherContentManager.cs`
* `GameSource/projectDish/Game1.cs` — content-manager wiring; 23 blend sites; guarded
  `InputDiagnostics.Tick` call.
* 19 other `GameSource/projectDish/*.cs` — 101 more blend sites (table §1.3).
* `Platform/InputDiagnostics.cs` — master switch + guards.
* `Platform/RenderDiagnostics.cs` — master switch; `Mode` property; gated `Bump`.
* `Activity1.cs` — volume-key harness gated on `RenderDiagnostics.Enabled`.
* **new** this note + `<dev-notes>/proof/final/**`.
